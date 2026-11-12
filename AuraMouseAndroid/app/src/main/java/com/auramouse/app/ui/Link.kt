package com.auramouse.app.ui

import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import com.auramouse.app.core.Clip
import com.auramouse.app.core.HostLink
import com.auramouse.app.core.LinkEvents
import com.auramouse.app.core.Network
import com.auramouse.app.core.SavedHost
import com.auramouse.app.core.Transfer
import com.auramouse.app.core.Transfers
import org.json.JSONObject
import java.net.InetAddress
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.concurrent.thread

/** The connection, which outlives the screen. Everything the screen shows comes from here. */
@SuppressLint("StaticFieldLeak") // only ever holds the application context
object Link : LinkEvents {
    enum class State { UNPAIRED, SEARCHING, WRONG_CODE, NOT_FOUND, REFUSED, CONNECTED }

    class RemoteFiles(val id: String, val title: String?, val count: Int)

    private const val MAX_CLIPBOARD = 1_000_000
    private const val MAX_NAME = 40
    private val MAC = Regex("^[0-9A-Fa-f]{12}$")
    private const val PAIR_ROUNDS = 6
    private const val RETRY_MS = 3000L
    private const val SCAN_EVERY_MS = 20_000L
    private const val UI_INTERVAL_MS = 200L

    private lateinit var app: Context
    private lateinit var store: SavedStore
    private val main = Handler(Looper.getMainLooper())
    private var generation = 0 // a newer attempt makes the older loops stop
    private var uiQueued = false
    private var lastClip: String? = null

    @Volatile var state = State.UNPAIRED; private set
    @Volatile var link: HostLink? = null; private set
    @Volatile var remoteFiles: RemoteFiles? = null; private set
    val transfers = CopyOnWriteArrayList<Transfer>()
    var onChange: (() -> Unit)? = null

    val saved: SavedHost? get() = store.host
    val wakeTargets: List<WakeTarget> get() = store.wakeTargets
    private val deviceName: String get() = "${Build.MANUFACTURER} ${Build.MODEL}".trim().take(40)

    fun init(context: Context) {
        if (::app.isInitialized) return
        app = context.applicationContext
        store = SavedStore(app)
    }

    /** Goes back to the saved computer, if there is one. */
    fun resume() {
        if (state == State.CONNECTED || state == State.SEARCHING) return
        val host = store.host ?: return
        startSaved(host)
    }

    fun forgetAttempt() {
        generation++
        link?.close()
        link = null
        state = State.UNPAIRED
        LinkService.stop(app)
        notifyUi()
    }

    fun pair(code: Int) {
        val mine = ++generation
        state = State.SEARCHING
        notifyUi()
        LinkService.start(app)
        thread(isDaemon = true) {
            var wrong = false
            for (round in 0 until PAIR_ROUNDS) {
                for (ip in Network.findHosts()) {
                    if (mine != generation) return@thread
                    val attempt = HostLink(deviceName, this)
                    try {
                        if (attempt.connectWithCode(ip, code, ip.hostAddress ?: "")) {
                            connected(attempt, mine)
                            return@thread
                        }
                        wrong = true
                    } catch (_: Exception) {
                    }
                }
                if (wrong) break
                Thread.sleep(RETRY_MS)
            }
            if (mine != generation) return@thread
            state = if (wrong) State.WRONG_CODE else State.NOT_FOUND
            LinkService.stop(app)
            notifyUi()
        }
    }

    private fun startSaved(host: SavedHost) {
        val mine = ++generation
        state = State.SEARCHING
        notifyUi()
        LinkService.start(app)
        thread(isDaemon = true) {
            var lastScan = 0L
            while (mine == generation) {
                val addresses = LinkedHashSet<InetAddress>()
                runCatching { host.ip?.let { addresses.add(InetAddress.getByName(it)) } }
                if (System.currentTimeMillis() - lastScan > SCAN_EVERY_MS) {
                    lastScan = System.currentTimeMillis()
                    addresses.addAll(runCatching { Network.findHosts() }.getOrDefault(emptyList()))
                }
                var refused = false
                for (ip in addresses) {
                    if (mine != generation) return@thread
                    val attempt = HostLink(deviceName, this)
                    try {
                        if (attempt.connectSaved(ip, host)) {
                            store.host = SavedHost(host.id, host.name, host.key, ip.hostAddress)
                            connected(attempt, mine)
                            return@thread
                        }
                        refused = true
                    } catch (_: Exception) {
                    }
                }
                if (refused && state != State.REFUSED) {
                    state = State.REFUSED
                    notifyUi()
                }
                Thread.sleep(RETRY_MS)
            }
        }
    }

    private fun connected(attempt: HostLink, mine: Int) {
        if (mine != generation) {
            attempt.close()
            return
        }
        link = attempt
        state = State.CONNECTED
        notifyUi()
    }

    /** The phone's screen went away: the computer's own mouse is free again. */
    fun release() { link?.release() }

    /** Stops for now. The computer stays saved. */
    fun disconnect() {
        generation++
        link?.close()
        link = null
        state = if (store.host != null) State.NOT_FOUND else State.UNPAIRED
        LinkService.stop(app)
        notifyUi()
    }

    // ───── from the computer ─────

    override fun onPaired(host: SavedHost) {
        store.host = host
        notifyUi()
    }

    override fun onDisconnected() {
        link = null
        val host = store.host
        if (host != null && state == State.CONNECTED) startSaved(host) // it comes back by itself
        else {
            state = State.UNPAIRED
            LinkService.stop(app)
            notifyUi()
        }
    }

    override fun onDrivingChanged(driving: Boolean) = notifyUi()

    override fun onClipboard(kind: Int, payload: ByteArray) {
        val clipboard = app.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        when (kind) {
            Clip.TEXT -> {
                val text = String(payload)
                lastClip = text
                remoteFiles = null
                clipboard.setPrimaryClip(ClipData.newPlainText("Aura Mouse", text))
            }
            Clip.IMAGE -> {
                if (payload.size < 8 || payload[0] != 0x89.toByte() || payload[1] != 0x50.toByte()) return // PNG is all that's ever sent
                ClipProvider.file(app).writeBytes(payload)
                remoteFiles = null
                clipboard.setPrimaryClip(ClipData.newUri(app.contentResolver, "Aura Mouse", ClipProvider.URI))
            }
            Clip.FILES -> runCatching {
                val json = JSONObject(String(payload))
                val id = json.getString("Id")
                if (Transfers.isValidId(id)) {
                    remoteFiles = RemoteFiles(id, if (json.isNull("Title")) null else json.getString("Title"), json.getInt("Count"))
                }
            }
        }
        notifyUi()
    }

    override fun onFilesReady(id: String) = download(id)

    override fun onWakeInfo(name: String, macs: List<String>) {
        val clean = name.trim()
        val valid = macs.filter { MAC.matches(it) }
        if (clean.isEmpty() || clean.length > MAX_NAME || valid.isEmpty()) return
        store.saveWake(WakeTarget(clean, valid))
        notifyUi()
    }

    // ───── what the screen asks for ─────

    /** Android only lets an app read the clipboard while it's on screen, so this runs when it is. */
    fun sendClipboard(): Boolean {
        val active = link ?: return false
        val clipboard = app.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val text = clipboard.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(app)?.toString()
        if (text.isNullOrEmpty() || text.toByteArray().size > MAX_CLIPBOARD) return false
        lastClip = text
        active.sendClipboardText(text)
        return true
    }

    /** The app came to the front: if something new was copied, the computer gets it too. */
    private const val SENSITIVE_CLIP = "android.content.extra.IS_SENSITIVE"

    fun sendClipboardIfNew() {
        val active = link ?: return
        val clipboard = app.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        // Password managers mark what they copy. It only goes if the person presses Send themselves.
        if (clipboard.primaryClipDescription?.extras?.getBoolean(SENSITIVE_CLIP) == true) return
        val text = runCatching { clipboard.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString() }.getOrNull()
        if (text.isNullOrEmpty() || text == lastClip || text.toByteArray().size > MAX_CLIPBOARD) return
        lastClip = text
        active.sendClipboardText(text)
    }

    fun download(id: String) {
        val active = link ?: return
        if (!Transfers.isValidId(id) || transfers.any { it.id == id && !it.outgoing && it.active }) return
        transfers.removeAll { it.id == id && !it.outgoing }
        val item = Transfer(id, false)
        transfers.add(0, item)
        val downloads = DownloadStore(app)
        thread(isDaemon = true) { Transfers.run(active, item, 1, ::notifyUi) { Transfers.receive(it, downloads, item, ::notifyUi) } }
        notifyUi()
    }

    fun sendFiles(uris: List<Uri>) {
        val active = link ?: return
        thread(isDaemon = true) {
            val set = PhoneFiles.setFrom(app, uris, deviceName) ?: return@thread
            if (transfers.any { it.id == set.id && it.outgoing && it.active }) return@thread
            transfers.removeAll { it.id == set.id && it.outgoing }
            val item = Transfer(set.id, true).apply {
                title = set.title
                fileCount = set.files.size
                total = set.files.sumOf { it.size }
            }
            transfers.add(0, item)
            notifyUi()
            Transfers.run(active, item, 0, ::notifyUi) { Transfers.send(it, set, item, ::notifyUi) }
        }
    }

    fun wake(target: WakeTarget, done: (Int) -> Unit) {
        thread(isDaemon = true) {
            val sent = Network.wake(target.macs)
            main.post { done(sent) }
        }
    }

    fun removeWake(target: WakeTarget) {
        store.removeWake(target.name)
        notifyUi()
    }

    /** True while connected to this exact computer, so it can be turned off from here. */
    fun isConnectedTo(name: String): Boolean =
        state == State.CONNECTED && saved?.name.equals(name, ignoreCase = true)

    /** Turn off the computer we're connected to. */
    fun shutdown() { link?.shutdown() }

    // ───── from the Quick Settings tiles, with no app window open ─────

    /** The saved computer, matched to its wake details. */
    private fun savedWakeTarget(): WakeTarget? {
        val host = store.host
        val targets = store.wakeTargets
        return targets.firstOrNull { host != null && it.name.equals(host.name, ignoreCase = true) } ?: targets.firstOrNull()
    }

    /** Wake the saved computer (Wake on LAN). Needs no connection. done(true) if the signal went out. */
    fun wakeSaved(done: (Boolean) -> Unit) {
        val target = savedWakeTarget()
        if (target == null) { done(false); return }
        thread(isDaemon = true) {
            val sent = runCatching { Network.wake(target.macs) }.getOrDefault(0)
            main.post { done(sent > 0) }
        }
    }

    /** Turn off the saved computer. Uses the live link if there is one, else connects just long enough. */
    fun shutdownSaved(done: (Boolean) -> Unit) {
        val existing = link
        if (existing != null && state == State.CONNECTED) { existing.shutdown(); done(true); return }
        val host = store.host
        if (host == null) { done(false); return }
        thread(isDaemon = true) {
            val ok = sendShutdownOnce(host)
            main.post { done(ok) }
        }
    }

    private val noEvents = object : LinkEvents {
        override fun onPaired(host: SavedHost) {}
        override fun onDisconnected() {}
        override fun onDrivingChanged(driving: Boolean) {}
        override fun onClipboard(kind: Int, payload: ByteArray) {}
        override fun onFilesReady(id: String) {}
        override fun onWakeInfo(name: String, macs: List<String>) {}
    }

    private fun sendShutdownOnce(host: SavedHost): Boolean {
        val addresses = LinkedHashSet<InetAddress>()
        runCatching { host.ip?.let { addresses.add(InetAddress.getByName(it)) } }
        addresses.addAll(runCatching { Network.findHosts() }.getOrDefault(emptyList()))
        for (ip in addresses) {
            val attempt = HostLink(deviceName, noEvents)
            try {
                if (attempt.connectSaved(ip, host)) {
                    store.host = SavedHost(host.id, host.name, host.key, ip.hostAddress)
                    attempt.shutdown()
                    Thread.sleep(900) // let the send loop flush it before we hang up
                    attempt.close()
                    return true
                }
            } catch (_: Exception) {
            }
        }
        return false
    }

    fun hasSavedComputer(): Boolean = store.host != null

    /** Progress comes in thousands of small steps; the screen only needs a few a second. */
    private fun notifyUi() {
        main.post {
            if (uiQueued) return@post
            uiQueued = true
            main.postDelayed({
                uiQueued = false
                onChange?.invoke()
                LinkService.refresh(app)
            }, UI_INTERVAL_MS)
        }
    }
}
