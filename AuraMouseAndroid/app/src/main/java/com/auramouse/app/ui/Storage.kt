package com.auramouse.app.ui

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.webkit.MimeTypeMap
import com.auramouse.app.core.IncomingStore
import com.auramouse.app.core.OutgoingFile
import com.auramouse.app.core.OutgoingSet
import com.auramouse.app.core.SavedHost
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.OutputStream
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class WakeTarget(val name: String, val macs: List<String>)

/** The computer we're paired with. Its key is encrypted with a key that never leaves the phone's keystore. */
class SavedStore(context: Context) {
    private val prefs = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    var host: SavedHost?
        get() {
            val id = prefs.getInt("id", 0)
            val name = prefs.getString("name", null) ?: return null
            val sealed = prefs.getString("key", null) ?: return null
            val key = runCatching { unseal(sealed) }.getOrNull() ?: return null
            return SavedHost(id, name, key, prefs.getString("ip", null))
        }
        set(value) {
            if (value == null) {
                prefs.edit().remove("id").remove("name").remove("key").remove("ip").apply()
                return
            }
            prefs.edit().putInt("id", value.id).putString("name", value.name).putString("key", seal(value.key)).putString("ip", value.ip).apply()
        }

    /** Every computer that can be turned on from here, in the order they were first seen. */
    val wakeTargets: List<WakeTarget>
        @Synchronized get() {
            val saved = prefs.getString("wake_list", null) ?: return oldWakeTarget()
            val list = JSONArray(saved)
            return List(list.length()) { i ->
                val item = list.getJSONObject(i)
                WakeTarget(item.getString("name"), strings(item.getJSONArray("macs")))
            }
        }

    /** The same computer again just gets its details updated, and keeps its place. */
    @Synchronized
    fun saveWake(target: WakeTarget) {
        val list = wakeTargets.toMutableList()
        val at = list.indexOfFirst { it.name.equals(target.name, ignoreCase = true) }
        if (at >= 0) list[at] = target else list.add(target)
        writeWake(list)
    }

    @Synchronized
    fun removeWake(name: String) = writeWake(wakeTargets.filterNot { it.name.equals(name, ignoreCase = true) })

    private fun writeWake(list: List<WakeTarget>) {
        val json = JSONArray()
        for (t in list) json.put(JSONObject().put("name", t.name).put("macs", JSONArray(t.macs)))
        prefs.edit().putString("wake_list", json.toString()).apply()
    }

    /** Earlier versions only kept one computer. */
    private fun oldWakeTarget(): List<WakeTarget> {
        val name = prefs.getString("wake_name", null) ?: return emptyList()
        val macs = prefs.getString("wake_macs", null)?.let { strings(JSONArray(it)) } ?: return emptyList()
        return listOf(WakeTarget(name, macs))
    }

    private fun strings(array: JSONArray) = List(array.length()) { array.getString(it) }

    private fun secret(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build())
            generateKey()
        }
    }

    fun seal(plain: ByteArray): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, secret()) }
        return Base64.encodeToString(cipher.iv + cipher.doFinal(plain), Base64.NO_WRAP)
    }

    fun unseal(sealed: String): ByteArray {
        val all = Base64.decode(sealed, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, secret(), GCMParameterSpec(128, all, 0, IV_SIZE))
        return cipher.doFinal(all, IV_SIZE, all.size - IV_SIZE)
    }

    private companion object {
        const val ALIAS = "auramouse-pairing"
        const val IV_SIZE = 12
    }
}

/** Parts stay in the app's own folder; finished files go to Downloads/Aura Mouse where the user can see them. */
class DownloadStore(private val context: Context) : IncomingStore {
    private val dir: File = File(context.getExternalFilesDir(null) ?: context.filesDir, "partial").apply { mkdirs() }

    private fun part(id: String, index: Int) = File(dir, "$id-$index.part")
    private fun marker(id: String, index: Int) = File(dir, "$id-$index.done")

    override fun partLength(id: String, index: Int) = part(id, index).length()
    override fun isComplete(id: String, index: Int) = marker(id, index).exists()
    override fun append(id: String, index: Int): OutputStream = FileOutputStream(part(id, index), true)
    override fun freeSpace() = dir.usableSpace

    override fun complete(id: String, index: Int, safePath: List<String>) {
        val name = safePath.last()
        val folders = (listOf("Download", "Aura Mouse") + safePath.dropLast(1)).joinToString("/")
        val extension = name.substringAfterLast('.', "").lowercase()
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, name)
            put(MediaStore.Downloads.MIME_TYPE, MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension) ?: "application/octet-stream")
            put(MediaStore.Downloads.RELATIVE_PATH, folders)
            put(MediaStore.Downloads.IS_PENDING, 1)
        }
        val resolver = context.contentResolver
        val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw IOException("Can't save to Downloads")
        val source = part(id, index)
        if (source.length() > 0L || source.exists()) {
            resolver.openOutputStream(uri)?.use { out -> source.inputStream().use { it.copyTo(out, 1 shl 16) } }
        }
        resolver.update(uri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
        marker(id, index).createNewFile()
        source.delete()
    }

    override fun clear(id: String) {
        dir.listFiles()?.filter { it.name.startsWith(id) }?.forEach { it.delete() }
    }
}

/** Files picked or shared on the phone, ready to send. */
object PhoneFiles {
    fun setFrom(context: Context, uris: List<Uri>, device: String): OutgoingSet? {
        val resolver = context.contentResolver
        val files = ArrayList<OutgoingFile>()
        val keys = ArrayList<String>()
        for (uri in uris) {
            var name = uri.lastPathSegment?.substringAfterLast('/') ?: "file"
            var size = -1L
            runCatching {
                resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                    if (c.moveToFirst()) {
                        c.getString(0)?.let { name = it }
                        if (!c.isNull(1)) size = c.getLong(1)
                    }
                }
            }
            if (size < 0) size = runCatching { resolver.openFileDescriptor(uri, "r")?.use { it.statSize } }.getOrNull() ?: -1
            if (size < 0) continue
            files.add(OutgoingFile(name, size) { offset ->
                val stream = resolver.openInputStream(uri) ?: throw IOException("Can't read $name")
                var left = offset
                while (left > 0) {
                    val skipped = stream.skip(left)
                    if (skipped <= 0) throw IOException("Can't resume $name")
                    left -= skipped
                }
                stream
            })
            keys.add("$uri|$size")
        }
        if (files.isEmpty()) return null
        return OutgoingSet(OutgoingSet.idFor(device, keys), files.singleOrNull()?.path, files)
    }
}

/** Lets other apps paste a picture copied on the computer. */
class ClipProvider : ContentProvider() {
    companion object {
        val URI: Uri = Uri.parse("content://com.auramouse.app.clip/clip.png")
        fun file(context: Context) = File(context.cacheDir, "clip.png")
    }

    override fun onCreate() = true
    override fun getType(uri: Uri) = "image/png"
    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor =
        ParcelFileDescriptor.open(file(context!!), ParcelFileDescriptor.MODE_READ_ONLY)

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, args: Array<out String>?, sort: String?): Cursor {
        val cursor = android.database.MatrixCursor(arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE))
        cursor.addRow(arrayOf<Any>("clip.png", file(context!!).length()))
        return cursor
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, args: Array<out String>?) = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, args: Array<out String>?) = 0
}
