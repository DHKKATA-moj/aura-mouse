package com.auramouse.app.core

import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/** Where typed keys go: the mouse connection or a remote desktop. */
interface KeySink {
    fun tapKey(vk: Int)
    fun text(text: String)
}

/** A computer viewed before. Id and key are there if its owner let this phone in without asking. */
class RemoteSaved(val name: String, val ip: String, val id: Int, val key: ByteArray?)

enum class RemoteEnd { DECLINED, BUSY, UNREACHABLE, LOST }

/** What happens on a remote desktop connection. Called on its own thread. */
interface RemoteEvents {
    /** The person there is being asked; both screens show this code. */
    fun onWaiting(code: String)
    fun onAccepted(computer: RemoteSaved, monitors: Int)
    /** Its key no longer works there, which also means it now has to ask. */
    fun onKeyRefused(name: String)
    /** Returning from here tells the computer the picture is on screen, so draw before returning. */
    fun onFrame(width: Int, height: Int, update: ByteArray)
    fun onEnded(reason: RemoteEnd)
}

/** An update is [int16 count] then per piece [int16 x][int16 y][int32 length][JPEG bytes]. */
object FramePieces {
    inline fun forEach(update: ByteArray, piece: (x: Int, y: Int, offset: Int, length: Int) -> Unit) {
        val b = ByteBuffer.wrap(update).order(ByteOrder.LITTLE_ENDIAN)
        val count = b.short.toInt()
        repeat(count) {
            val x = b.short.toInt()
            val y = b.short.toInt()
            val length = b.int
            if (length <= 0 || length > b.remaining()) throw IOException("Bad picture piece")
            // JPEG only: the decoder understands far more formats than this ever needs to accept.
            val at = b.position()
            if (length < 3 || update[at] != 0xFF.toByte() || update[at + 1] != 0xD8.toByte() || update[at + 2] != 0xFF.toByte()) throw IOException("Not a JPEG")
            piece(x, y, b.position(), length)
            b.position(b.position() + length)
        }
    }
}

/** Remote desktop's own connection, separate from the mouse one: its own port and threads. */
class RemoteLink(
    private val ip: InetAddress,
    private val deviceName: String,
    private val saved: RemoteSaved?,
    private val events: RemoteEvents,
) : KeySink {
    private val queue = LinkedBlockingQueue<Msg>()
    private val pointerLock = Any()
    private var pointerX = -1
    private var pointerY = -1
    @Volatile private var socket: Socket? = null
    @Volatile private var closed = false

    fun start() { thread(isDaemon = true, name = "remote receive") { run() } }

    /** Only the newest position matters, so a fast finger never builds a backlog. 0..65535 on the picture. */
    fun pointer(x: Int, y: Int) = synchronized(pointerLock) {
        pointerX = x
        pointerY = y
    }

    fun button(flags: Int) { queue.add(Msg(T.BUTTON, flags)) }
    fun wheel(delta: Int) { queue.add(Msg(T.WHEEL, delta)) }
    fun monitor(index: Int) { queue.add(Msg(T.REMOTE_MONITOR, index)) }
    fun viewSize(longestSide: Int) { queue.add(Msg(T.REMOTE_VIEW_SIZE, longestSide)) }
    override fun text(text: String) { queue.add(Msg(T.TEXT_INPUT, payload = text.toByteArray(Charsets.UTF_8))) }
    override fun tapKey(vk: Int) = chord(vk)

    fun chord(vararg vks: Int) {
        vks.forEach { queue.add(Msg(T.KEY, it)) }
        vks.reversed().forEach { queue.add(Msg(T.KEY, it, 0, Win.KEY_UP)) }
    }

    fun close() {
        closed = true
        runCatching { socket?.close() }
    }

    private fun run() {
        var end = RemoteEnd.LOST
        try {
            end = session()
        } catch (_: Exception) {
        }
        runCatching { socket?.close() }
        if (!closed) events.onEnded(end)
    }

    private fun session(): RemoteEnd {
        var id = saved?.id ?: 0
        var key = saved?.key
        while (true) {
            val s = Socket()
            socket = s
            s.tcpNoDelay = true
            s.keepAlive = true
            try {
                s.connect(InetSocketAddress(ip, Ports.REMOTE), CONNECT_MS)
            } catch (_: IOException) {
                return RemoteEnd.UNREACHABLE
            }
            s.soTimeout = CONNECT_MS
            val input = DataInputStream(s.getInputStream().buffered(1 shl 16))
            val output = BufferedOutputStream(s.getOutputStream())

            val mine = SecureChannel.newKey()
            val viewerPublic = mine.public.encoded
            Plain.send(output, T.HELLO, MessageDigest.getInstance("SHA-256").digest(viewerPublic), id)
            val share = Plain.receive(input)
            if (share.type != T.KEY_SHARE || share.payload == null) throw IOException("Unexpected handshake reply")
            val sharerPublic = share.payload
            Plain.send(output, T.KEY_SHARE, viewerPublic, 0)

            val channel = SecureChannel.create(input, output, mine, sharerPublic, key ?: NO_KEY)
            channel.send(Msg(T.AUTH, 1, payload = deviceName.toByteArray(Charsets.UTF_8)))
            val reply = Plain.receive(input)
            if (reply.type != T.WELCOME) {
                if (key == null) throw IOException("Refused without a key")
                // Its owner took the permission back: ask again like anyone else.
                s.close()
                saved?.let { events.onKeyRefused(it.name) }
                id = 0
                key = null
                continue
            }
            s.soTimeout = 0
            thread(isDaemon = true, name = "remote send") { sendLoop(channel) }
            return receiveLoop(channel, viewerPublic, sharerPublic, id, key)
        }
    }

    private fun receiveLoop(channel: SecureChannel, viewerPublic: ByteArray, sharerPublic: ByteArray, startId: Int, startKey: ByteArray?): RemoteEnd {
        var id = startId
        var key = startKey
        while (true) {
            val m = channel.receive()
            when (m.type) {
                T.REMOTE_WAITING -> events.onWaiting(code(viewerPublic, sharerPublic))
                T.REMOTE_TRUST -> if (m.payload?.size == KEY_SIZE) {
                    id = m.a
                    key = m.payload
                }
                T.REMOTE_ACCEPTED -> events.onAccepted(RemoteSaved(Names.clean(m.text, ip.hostAddress ?: ""), ip.hostAddress ?: "", id, key), m.b)
                T.REMOTE_DENIED -> return if (m.a == BUSY) RemoteEnd.BUSY else RemoteEnd.DECLINED
                T.REMOTE_FRAME -> m.payload?.let {
                    if (m.b !in 1..MAX_SIDE || m.c !in 1..MAX_SIDE) throw IOException("Bad picture size")
                    events.onFrame(m.b, m.c, it)
                    queue.add(Msg(T.REMOTE_ACK, m.a))
                }
            }
        }
    }

    private fun sendLoop(channel: SecureChannel) {
        try {
            while (!closed) {
                val m = queue.poll(POINTER_INTERVAL_MS, TimeUnit.MILLISECONDS)
                flushPointer(channel) // a click lands where the finger was, so the position goes first
                if (m != null) channel.send(m)
            }
        } catch (_: Exception) {
            runCatching { socket?.close() }
        }
    }

    private fun flushPointer(channel: SecureChannel) {
        val x: Int
        val y: Int
        synchronized(pointerLock) {
            x = pointerX
            y = pointerY
            pointerX = -1
            pointerY = -1
        }
        if (x >= 0) channel.send(Msg(T.REMOTE_POINTER, x, y))
    }

    companion object {
        const val KEY_SIZE = 32
        private const val BUSY = 1
        private const val MAX_SIDE = 8192
        private const val CONNECT_MS = 5000
        private const val POINTER_INTERVAL_MS = 8L
        private val NO_KEY = "EasyMouse remote".toByteArray(Charsets.UTF_8)

        /** Six digits from both keys: a machine in the middle would make the two screens show different numbers. */
        fun code(viewerPublic: ByteArray, sharerPublic: ByteArray): String {
            val hash = MessageDigest.getInstance("SHA-256").digest("EasyMouse code".toByteArray(Charsets.UTF_8) + viewerPublic + sharerPublic)
            val n = (ByteBuffer.wrap(hash).order(ByteOrder.LITTLE_ENDIAN).int.toLong() and 0xFFFFFFFFL) % 1_000_000
            return "%06d".format(n)
        }

        /** The computer's name if it's accepting remote requests, otherwise null. */
        fun askName(ip: InetAddress, timeoutMs: Int): String? = try {
            Socket().use { s ->
                s.connect(InetSocketAddress(ip, Ports.REMOTE), timeoutMs)
                s.soTimeout = timeoutMs
                val output = BufferedOutputStream(s.getOutputStream())
                Plain.send(output, T.REMOTE_PROBE, ByteArray(0), 0)
                val info = Plain.receive(DataInputStream(s.getInputStream()))
                if (info.type == T.REMOTE_INFO) Names.clean(info.text, ip.hostAddress ?: "") else null
            }
        } catch (_: Exception) {
            null
        }
    }
}
