package com.auramouse.app.core

import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/** A computer paired once with the code. Key stands in for the code from then on. */
class SavedHost(val id: Int, val name: String, val key: ByteArray, val ip: String?)

/** What the computer tells us. Called on the link's own thread. */
interface LinkEvents {
    fun onPaired(host: SavedHost)
    fun onDisconnected()
    fun onDrivingChanged(driving: Boolean)
    fun onClipboard(kind: Int, payload: ByteArray)
    fun onFilesReady(id: String)
    fun onWakeInfo(name: String, macs: List<String>)
}

/** One connection to the computer. The phone only ever drives it, like a touchpad and keyboard. */
class HostLink(private val deviceName: String, private val events: LinkEvents) : KeySink {
    private val queue = LinkedBlockingQueue<Msg>()
    private var socket: Socket? = null
    private var pendingX = 0
    private var pendingY = 0
    private val moveLock = Any()

    @Volatile var connected = false; private set
    @Volatile var driving = false; private set
    @Volatile var fileKey: ByteArray? = null; private set
    @Volatile var peerId = 0; private set
    @Volatile var hostIp: InetAddress? = null; private set
    @Volatile var hostName = ""; private set

    /** Pairs with the code shown on the computer. False if the code was wrong; throws if it can't be reached. */
    fun connectWithCode(ip: InetAddress, code: Int, name: String) = connect(ip, Pake(code), null, 0, name)

    /** Connects with the saved key. False if the computer no longer accepts it. */
    fun connectSaved(ip: InetAddress, host: SavedHost) = connect(ip, null, host.key, host.id, host.name)

    private fun connect(ip: InetAddress, pake: Pake?, key: ByteArray?, pairId: Int, name: String): Boolean {
        val s = Socket()
        try {
            s.tcpNoDelay = true
            s.keepAlive = true
            s.connect(InetSocketAddress(ip, Ports.MAIN), 5000)
            s.soTimeout = 5000
            val input = DataInputStream(s.getInputStream())
            val output = BufferedOutputStream(s.getOutputStream())

            val mine = SecureChannel.newKey()
            Plain.send(output, T.HELLO, mine.public.encoded, pairId)
            if (pake != null) Plain.send(output, T.PAKE_SHARE, pake.share, 0)
            val share = Plain.receive(input)
            if (share.type == T.REJECTED) { // it isn't accepting the code right now
                s.close()
                return false
            }
            if (share.type != T.KEY_SHARE || share.payload == null) throw IOException("Unexpected handshake reply")

            val secret = if (pake != null) {
                val theirs = Plain.receive(input)
                if (theirs.type != T.PAKE_SHARE || theirs.payload == null) throw IOException("Unexpected handshake reply")
                pake.finish(theirs.payload, mine.public.encoded, share.payload)
            } else key!!

            val channel = SecureChannel.create(input, output, mine, share.payload, secret)
            channel.send(Msg(T.AUTH, 1, payload = deviceName.toByteArray(Charsets.UTF_8))) // A = 1: a phone
            val reply = Plain.receive(input)
            if (reply.type != T.WELCOME) {
                s.close()
                return false
            }
            // The plain Welcome could be forged; the encrypted one proves the computer has the same keys.
            if (channel.receive().type != T.WELCOME) throw IOException("Host failed to confirm")

            s.soTimeout = 0
            socket = s
            hostIp = ip
            hostName = name
            connected = true
            thread(isDaemon = true, name = "link send") { sendLoop(channel) }
            thread(isDaemon = true, name = "link receive") { receiveLoop(channel) }
            return true
        } catch (e: Exception) {
            runCatching { s.close() }
            throw if (e is IOException) e else IOException(e)
        }
    }

    // ───── what the phone sends ─────

    /** Added up and sent together, so a fast finger never builds a backlog. */
    fun move(dx: Int, dy: Int) {
        if (dx == 0 && dy == 0) return
        ensureDriving()
        synchronized(moveLock) {
            pendingX += dx
            pendingY += dy
        }
    }

    fun button(flags: Int) { ensureDriving(); queue.add(Msg(T.BUTTON, flags)) }
    fun wheel(delta: Int, horizontal: Boolean) { ensureDriving(); queue.add(Msg(T.WHEEL, delta, if (horizontal) 1 else 0)) }
    override fun text(text: String) { ensureDriving(); queue.add(Msg(T.TEXT_INPUT, payload = text.toByteArray(Charsets.UTF_8))) }

    override fun tapKey(vk: Int) = chord(vk)

    /** Ctrl+C and the like: down in order, up in reverse. */
    fun chord(vararg vks: Int) {
        ensureDriving()
        vks.forEach { queue.add(Msg(T.KEY, it)) }
        vks.reversed().forEach { queue.add(Msg(T.KEY, it, 0, Win.KEY_UP)) }
    }

    fun sendClipboardText(text: String) { queue.add(Msg(T.CLIPBOARD, Clip.TEXT, payload = text.toByteArray(Charsets.UTF_8))) }

    /** The computer's own mouse is free to take over again. */
    fun release() {
        if (!driving) return
        driving = false
        queue.add(Msg(T.TAKE_OVER))
        events.onDrivingChanged(false)
    }

    fun close() {
        connected = false
        runCatching { socket?.close() }
    }

    private fun ensureDriving() {
        if (driving) return
        driving = true
        queue.add(Msg(T.CLIENT_ENTER, 0, 5000))
        events.onDrivingChanged(true)
    }

    private fun sendLoop(channel: SecureChannel) {
        try {
            while (connected) {
                val m = queue.poll(MOVE_INTERVAL_MS, TimeUnit.MILLISECONDS)
                // Moves go before a click that came after them, but after the message that starts driving.
                if (m?.type == T.CLIENT_ENTER) {
                    channel.send(m)
                    flushMoves(channel)
                } else {
                    flushMoves(channel)
                    if (m != null) channel.send(m)
                }
            }
        } catch (_: Exception) {
            runCatching { socket?.close() } // the receive loop notices, and reports it
        }
    }

    private fun flushMoves(channel: SecureChannel) {
        val x: Int
        val y: Int
        synchronized(moveLock) {
            x = pendingX
            y = pendingY
            pendingX = 0
            pendingY = 0
        }
        if (x != 0 || y != 0) channel.send(Msg(T.MOVE, x, y))
    }

    private fun receiveLoop(channel: SecureChannel) {
        try {
            while (true) handle(channel.receive())
        } catch (_: Exception) {
        }
        val wasConnected = connected
        close()
        driving = false
        if (wasConnected) events.onDisconnected()
    }

    private fun handle(m: Msg) {
        when (m.type) {
            T.FILE_KEY -> {
                peerId = m.a
                fileKey = m.payload
            }
            T.PAIR_KEY -> {
                val p = m.payload ?: return
                if (p.size <= KEY_SIZE) return
                val name = Names.clean(String(p, KEY_SIZE, p.size - KEY_SIZE, Charsets.UTF_8), hostIp?.hostAddress ?: "PC")
                hostName = name
                events.onPaired(SavedHost(m.a, name, p.copyOf(KEY_SIZE), hostIp?.hostAddress))
            }
            T.STOP_DRIVING, T.RETURN -> if (driving) {
                driving = false
                events.onDrivingChanged(false)
            }
            T.CLIPBOARD -> m.payload?.let { events.onClipboard(m.a, it) }
            T.FILE_READY -> events.onFilesReady(m.text)
            T.WAKE_INFO -> runCatching {
                val json = org.json.JSONObject(m.text)
                val list = json.getJSONArray("Macs")
                events.onWakeInfo(Names.clean(json.getString("Name"), "PC"), List(list.length()) { list.getString(it) })
            }
        }
    }

    companion object {
        const val KEY_SIZE = 32
        private const val MOVE_INTERVAL_MS = 8L
    }
}
