package com.auramouse.app.core

import java.io.DataInputStream
import java.io.IOException
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Message types, the same numbers as Protocol.cs in the Windows program. */
object T {
    const val HELLO = 1; const val WELCOME = 2; const val REJECTED = 3; const val KEY_SHARE = 4; const val AUTH = 5; const val PAKE_SHARE = 6
    const val MOVE = 11; const val BUTTON = 12; const val WHEEL = 13; const val KEY = 14; const val CLIPBOARD = 15
    const val CLIENT_ENTER = 24; const val RETURN = 25; const val STOP_DRIVING = 26; const val TAKE_OVER = 27
    const val DROP_AT = 29; const val WAKE_INFO = 31; const val TEXT_INPUT = 33
    const val FILE_KEY = 40; const val FILE_READY = 41; const val PAIR_KEY = 42
    const val FILE_START = 50; const val MANIFEST = 51; const val MANIFEST_ACK = 52; const val FILE_CHUNK = 53
    const val FILE_END = 54; const val DONE = 55; const val CANCEL = 56
    const val REMOTE_PROBE = 60; const val REMOTE_INFO = 61; const val REMOTE_WAITING = 63; const val REMOTE_ACCEPTED = 64
    const val REMOTE_DENIED = 65; const val REMOTE_TRUST = 66; const val REMOTE_FRAME = 67; const val REMOTE_ACK = 68
    const val REMOTE_POINTER = 69; const val REMOTE_MONITOR = 70; const val REMOTE_VIEW_SIZE = 71
}

object Clip { const val TEXT = 0; const val IMAGE = 1; const val FILES = 2 }

object Ports { const val MAIN = 47800; const val FILES = 47802; const val REMOTE = 47810 }

/** Windows input flags the computer plays back. */
object Win {
    const val LEFT_DOWN = 0x2; const val LEFT_UP = 0x4; const val RIGHT_DOWN = 0x8; const val RIGHT_UP = 0x10
    const val KEY_UP = 0x2
    const val VK_BACK = 0x08; const val VK_TAB = 0x09; const val VK_RETURN = 0x0D; const val VK_SHIFT = 0x10; const val VK_CONTROL = 0x11
    const val VK_MENU = 0x12; const val VK_ESCAPE = 0x1B; const val VK_LEFT = 0x25; const val VK_UP = 0x26; const val VK_RIGHT = 0x27
    const val VK_DOWN = 0x28; const val VK_DELETE = 0x2E; const val VK_LWIN = 0x5B
}

class Msg(val type: Int, val a: Int = 0, val b: Int = 0, val c: Int = 0, val payload: ByteArray? = null) {
    val text: String get() = payload?.toString(Charsets.UTF_8) ?: ""
}

/** The unencrypted start of a connection: a 13 byte header, and for Hello and KeyShare a payload of A bytes. */
object Plain {
    const val HEADER = 13
    private const val MAX_PAYLOAD = 1024

    fun send(out: OutputStream, type: Int, payload: ByteArray, c: Int) {
        val buffer = ByteBuffer.allocate(HEADER + payload.size).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put(type.toByte()).putInt(payload.size).putInt(0).putInt(c).put(payload)
        out.write(buffer.array())
        out.flush()
    }

    fun receive(input: DataInputStream): Msg {
        val header = ByteArray(HEADER)
        input.readFully(header)
        val b = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN)
        val type = b.get().toInt() and 0xFF
        val a = b.int
        val m = Msg(type, a, b.int, b.int)
        if (type != T.HELLO && type != T.KEY_SHARE && type != T.REMOTE_INFO && type != T.PAKE_SHARE) return m
        if (a < 0 || a > MAX_PAYLOAD) throw IOException("Handshake message too large")
        val payload = ByteArray(a)
        input.readFully(payload)
        return Msg(type, a, m.b, m.c, payload)
    }
}
