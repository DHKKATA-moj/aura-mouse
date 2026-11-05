package com.auramouse.app.core

import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import kotlin.concurrent.thread

class RefusedException(val reason: String) : IOException(reason)
class CanceledByPeerException : IOException()

enum class TransferState { WAITING, MOVING, RETRYING, DONE, FAILED, CANCELED }

/** One send or receive, as shown on the Files page. Updated from its own thread. */
class Transfer(val id: String, val outgoing: Boolean) {
    @Volatile var title: String? = null
    @Volatile var fileCount = 0
    @Volatile var total = 0L
    @Volatile var done = 0L
    @Volatile var state = TransferState.WAITING
    @Volatile var error: String? = null
    @Volatile var cancelRequested = false
    val active get() = state == TransferState.WAITING || state == TransferState.MOVING || state == TransferState.RETRYING
}

/** A file to send: where its name goes, its size, and a way to read it from a given position. */
class OutgoingFile(val path: String, val size: Long, val open: (offset: Long) -> InputStream)

class OutgoingSet(val id: String, val title: String?, val files: List<OutgoingFile>) {
    companion object {
        /** The same files always get the same id, so sending them again resumes. */
        fun idFor(device: String, keys: List<String>): String {
            val digest = MessageDigest.getInstance("SHA-256").digest((device + "|" + keys.joinToString("|")).toByteArray())
            return digest.joinToString("") { "%02x".format(it) }.take(24)
        }
    }
}

/** Where received files go. Parts are kept until a file is complete, so a break resumes from their end. */
interface IncomingStore {
    fun partLength(id: String, index: Int): Long
    fun isComplete(id: String, index: Int): Boolean
    fun append(id: String, index: Int): OutputStream
    /** Moves the finished part to its final place. */
    fun complete(id: String, index: Int, safePath: List<String>)
    fun freeSpace(): Long
    /** Everything is done or cancelled: drop what's left. */
    fun clear(id: String)
}

/** A connection of its own for one transfer, so a big file never makes the cursor lag. */
class FileLink private constructor(private val socket: Socket, val channel: SecureChannel) : AutoCloseable {
    override fun close() { runCatching { socket.close() } }

    companion object {
        private const val BUFFER = 1 shl 20

        fun connect(link: HostLink): FileLink {
            val ip = link.hostIp ?: throw IOException("Not connected")
            val key = link.fileKey ?: throw IOException("No file key yet")
            val s = Socket()
            try {
                s.tcpNoDelay = true
                s.sendBufferSize = BUFFER
                s.receiveBufferSize = BUFFER
                s.connect(InetSocketAddress(ip, Ports.FILES), 8000)
                s.soTimeout = 10000
                val input = DataInputStream(s.getInputStream().buffered(BUFFER))
                val output = BufferedOutputStream(s.getOutputStream())
                val mine = SecureChannel.newKey()
                Plain.send(output, T.HELLO, mine.public.encoded, link.peerId)
                val share = Plain.receive(input)
                if (share.type != T.KEY_SHARE || share.payload == null) throw IOException("Unexpected handshake reply")
                val channel = SecureChannel.create(input, output, mine, share.payload, key)
                channel.send(Msg(T.AUTH))
                if (channel.receive().type != T.WELCOME) throw IOException("Refused")
                s.soTimeout = 60000
                return FileLink(s, channel)
            } catch (e: Exception) {
                runCatching { s.close() }
                throw if (e is IOException) e else IOException(e)
            }
        }
    }
}

object Transfers {
    const val CHUNK = 512 * 1024
    private const val MARGIN = 64L shl 20

    fun isValidId(id: String) = Regex("^[0-9a-f]{8,64}$").matches(id)

    /** Keeps going across broken connections, picking up from where the other side got to. */
    fun run(link: HostLink, item: Transfer, direction: Int, onChange: () -> Unit, work: (SecureChannel) -> Unit) {
        var delay = 1000L
        while (link.connected && !item.cancelRequested) {
            try {
                FileLink.connect(link).use { files ->
                    files.channel.send(Msg(T.FILE_START, direction, payload = item.id.toByteArray()))
                    item.state = TransferState.MOVING
                    onChange()
                    work(files.channel)
                }
                item.state = if (item.cancelRequested) TransferState.CANCELED else TransferState.DONE
                onChange()
                return
            } catch (e: RefusedException) {
                item.error = e.reason
                item.state = TransferState.FAILED
                onChange()
                return
            } catch (_: CanceledByPeerException) {
                item.state = TransferState.CANCELED
                onChange()
                return
            } catch (_: Exception) {
                if (item.cancelRequested) break
                item.state = TransferState.RETRYING
                onChange()
                Thread.sleep(delay)
                delay = minOf(delay * 2, 8000)
            }
        }
        item.state = if (item.cancelRequested) TransferState.CANCELED else TransferState.FAILED
        if (!item.cancelRequested) item.error = "err_disconnected"
        onChange()
    }

    fun send(channel: SecureChannel, set: OutgoingSet, item: Transfer, onChange: () -> Unit) {
        item.title = set.title
        item.fileCount = set.files.size
        item.total = set.files.sumOf { it.size }

        val manifest = JSONObject().put("Id", set.id).put("Title", set.title ?: JSONObject.NULL)
            .put("Files", JSONArray(set.files.map { JSONObject().put("Path", it.path).put("Size", it.size) }))
        channel.send(Msg(T.MANIFEST, payload = manifest.toString().toByteArray()))

        val reply = channel.receive()
        if (reply.type == T.CANCEL) throw CanceledByPeerException()
        if (reply.type != T.MANIFEST_ACK) throw IOException("Expected ManifestAck")
        val ack = JSONObject(reply.text)
        if (!ack.getBoolean("Accepted")) throw RefusedException(ack.optString("Reason", "err_generic"))
        val offsets = ack.getJSONArray("Offsets")
        if (offsets.length() != set.files.size) throw IOException("Bad offsets")

        item.done = set.files.indices.sumOf { i -> offsets.getLong(i).let { if (it < 0) set.files[i].size else it.coerceIn(0, set.files[i].size) } }
        onChange()

        // The receiver only speaks again to say Done or Cancel.
        var answer: Msg? = null
        var broken: Exception? = null
        val listener = thread(isDaemon = true) {
            try { answer = channel.receive() } catch (e: Exception) { broken = e }
        }

        val buffer = ByteArray(CHUNK)
        for ((i, file) in set.files.withIndex()) {
            val offset = offsets.getLong(i)
            if (offset < 0) continue
            val start = offset.coerceIn(0, file.size)
            if (start < file.size) file.open(start).use { stream ->
                var remaining = file.size - start
                while (remaining > 0) {
                    if (item.cancelRequested) {
                        channel.send(Msg(T.CANCEL))
                        return
                    }
                    answer?.let { throw if (it.type == T.CANCEL) CanceledByPeerException() else IOException("Unexpected ${it.type}") }
                    broken?.let { throw IOException(it) }
                    val n = stream.read(buffer, 0, minOf(CHUNK.toLong(), remaining).toInt())
                    if (n <= 0) throw RefusedException("err_source_changed")
                    channel.send(T.FILE_CHUNK, i, 0, 0, buffer, 0, n)
                    remaining -= n
                    item.done += n
                    onChange()
                }
            }
            channel.send(Msg(T.FILE_END, i))
        }

        channel.send(Msg(T.DONE))
        listener.join()
        broken?.let { throw IOException(it) }
        val final = answer
        if (final?.type == T.CANCEL) throw CanceledByPeerException()
        if (final?.type != T.DONE) throw IOException("Expected Done")
    }

    fun receive(channel: SecureChannel, store: IncomingStore, item: Transfer, onChange: () -> Unit) {
        val first = channel.receive()
        if (first.type == T.CANCEL) throw CanceledByPeerException()
        if (first.type != T.MANIFEST) throw IOException("Expected Manifest")

        val manifest = JSONObject(first.text)
        val files = manifest.getJSONArray("Files")
        val count = files.length()
        if (manifest.getString("Id") != item.id || count == 0 || count > 100_000) throw IOException("Bad manifest")
        val sizes = LongArray(count) { files.getJSONObject(it).getLong("Size") }
        val paths = List(count) { SafePath.segments(files.getJSONObject(it).getString("Path")) }
        if (sizes.any { it < 0 }) throw IOException("Bad file size")
        sizes.fold(0L) { total, size -> Math.addExact(total, size) } // a total too big to add up isn't a real one

        item.title = if (manifest.isNull("Title")) null else manifest.getString("Title")
        item.fileCount = count
        item.total = sizes.sum()

        val offsets = JSONArray()
        var have = 0L
        var need = 0L
        for (i in 0 until count) {
            if (store.isComplete(item.id, i)) {
                offsets.put(-1L)
                have += sizes[i]
                continue
            }
            val length = store.partLength(item.id, i).coerceAtMost(sizes[i])
            offsets.put(length)
            have += length
            need += sizes[i] - length
        }
        if (store.freeSpace() < need + MARGIN) {
            val no = JSONObject().put("Accepted", false).put("Reason", "err_space_remote").put("Offsets", JSONObject.NULL)
            channel.send(Msg(T.MANIFEST_ACK, payload = no.toString().toByteArray()))
            throw RefusedException("err_space_local")
        }
        val yes = JSONObject().put("Accepted", true).put("Reason", JSONObject.NULL).put("Offsets", offsets)
        channel.send(Msg(T.MANIFEST_ACK, payload = yes.toString().toByteArray()))
        item.done = have
        onChange()

        var output: OutputStream? = null
        var outputIndex = -1
        val written = LongArray(count) { store.partLength(item.id, it) }
        try {
            while (true) {
                if (item.cancelRequested) {
                    channel.send(Msg(T.CANCEL))
                    store.clear(item.id)
                    return
                }
                val m = channel.receive()
                when (m.type) {
                    T.FILE_CHUNK -> {
                        val i = m.a
                        if (i !in 0 until count || store.isComplete(item.id, i)) throw IOException("Bad file index")
                        val data = m.payload ?: continue
                        if (written[i] + data.size > sizes[i]) throw IOException("More data than the file size")
                        if (i != outputIndex) {
                            output?.close()
                            output = store.append(item.id, i)
                            outputIndex = i
                        }
                        output!!.write(data)
                        written[i] += data.size.toLong()
                        item.done += data.size
                        onChange()
                    }
                    T.FILE_END -> {
                        val i = m.a
                        if (i !in 0 until count || store.isComplete(item.id, i)) throw IOException("Bad file index")
                        if (outputIndex == i) {
                            output?.close()
                            output = null
                            outputIndex = -1
                        }
                        if (store.partLength(item.id, i) != sizes[i]) throw IOException("Size mismatch")
                        store.complete(item.id, i, paths[i])
                    }
                    T.DONE -> {
                        if ((0 until count).any { !store.isComplete(item.id, it) }) throw IOException("Done before all files arrived")
                        channel.send(Msg(T.DONE))
                        store.clear(item.id)
                        return
                    }
                    T.CANCEL -> {
                        store.clear(item.id)
                        throw CanceledByPeerException()
                    }
                    else -> throw IOException("Unexpected ${m.type}")
                }
            }
        } finally {
            runCatching { output?.close() }
        }
    }
}

/** Names from the network: no "..", no separators, nothing the phone's storage would refuse. */
object SafePath {
    private val BAD = Regex("[\\\\/:*?\"<>|\\p{Cntrl}]")

    fun segments(path: String): List<String> {
        val parts = path.split('/', '\\').filter { it.isNotEmpty() && it != "." && it != ".." }.map(::clean)
        return if (parts.isEmpty()) listOf("file") else parts
    }

    private fun clean(name: String): String {
        val clean = name.replace(BAD, "_").trim().trimEnd('.')
        val safe = if (clean.isEmpty()) "_" else clean
        return if (safe.length > 150) safe.take(150) else safe
    }
}
