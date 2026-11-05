package com.auramouse.app.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.net.InetAddress
import java.util.concurrent.atomic.AtomicInteger

/** The phone's remote desktop code against the real Windows side, with its input going to a recorder. */
class RemoteInteropTest {
    private val dir = File(System.getenv("EMREMOTE_DIR") ?: "")
    private val local: InetAddress = InetAddress.getLoopbackAddress()

    private fun ask(command: String): String {
        val reply = File(dir, "reply.txt")
        reply.delete()
        val temp = File(dir, "cmd.tmp")
        temp.writeText(command)
        temp.renameTo(File(dir, "cmd.txt"))
        repeat(200) {
            if (reply.exists()) {
                Thread.sleep(30)
                return reply.readText()
            }
            Thread.sleep(50)
        }
        throw AssertionError("no reply to $command")
    }

    private class Events : RemoteEvents {
        // The tests run on a desktop Java, but compile against Android, which has no ImageIO.
        private fun decode(stream: java.io.InputStream): Any? =
            Class.forName("javax.imageio.ImageIO").getMethod("read", java.io.InputStream::class.java).invoke(null, stream)

        @Volatile var code: String? = null
        @Volatile var computer: RemoteSaved? = null
        @Volatile var refused: String? = null
        @Volatile var ended: RemoteEnd? = null
        @Volatile var size = 0 to 0
        val pieces = AtomicInteger()
        val decoded = AtomicInteger()
        val broken = AtomicInteger()
        override fun onWaiting(code: String) { this.code = code }
        override fun onAccepted(computer: RemoteSaved, monitors: Int) { this.computer = computer }
        override fun onKeyRefused(name: String) { refused = name }
        override fun onEnded(reason: RemoteEnd) { ended = reason }
        override fun onFrame(width: Int, height: Int, update: ByteArray) {
            size = width to height
            FramePieces.forEach(update) { _, _, offset, length ->
                pieces.incrementAndGet()
                if (decode(ByteArrayInputStream(update, offset, length)) != null) decoded.incrementAndGet() else broken.incrementAndGet()
            }
        }
    }

    private fun waitFor(what: String, check: () -> Boolean) {
        repeat(200) {
            if (check()) return
            Thread.sleep(50)
        }
        throw AssertionError("timed out: $what")
    }

    private fun view(saved: RemoteSaved? = null): Pair<RemoteLink, Events> {
        val events = Events()
        val link = RemoteLink(local, "TEST-PHONE", saved, events)
        link.start()
        return link to events
    }

    @Test
    fun everything() {
        assumeTrue("needs the Windows test program", System.getenv("EMREMOTE_DIR") != null)
        assertNotNull("answers the search", RemoteLink.askName(local, 2000))

        // Declined: no picture, and the same code on both screens.
        ask("decline")
        val (a, ea) = view()
        waitFor("declined") { ea.ended != null }
        assertEquals(RemoteEnd.DECLINED, ea.ended)
        assertEquals("same code on both screens", ask("code").trim(), ea.code)
        assertEquals(0, ea.pieces.get())
        a.close()
        Thread.sleep(15500) // a no holds for a while

        // Allowed: pictures that really decode, and input in the right order.
        ask("allow")
        val (b, eb) = view()
        waitFor("accepted") { eb.computer != null }
        assertNull("no permission given", eb.computer!!.key)
        waitFor("picture") { eb.decoded.get() > 0 }
        assertEquals("pieces that did not decode", 0, eb.broken.get())
        assertTrue("real size ${eb.size}", eb.size.first > 0)
        b.viewSize(960)
        waitFor("smaller picture") { eb.size.first in 1..960 && eb.size.second in 1..960 }
        b.pointer(32768, 32768)
        b.button(Win.LEFT_DOWN)
        b.button(Win.LEFT_UP)
        b.text("سلام")
        b.chord(Win.VK_CONTROL, 0x43)
        Thread.sleep(400)
        val input = ask("input")
        assertTrue(input, input.contains("move "))
        assertTrue("position goes before the click: $input", input.indexOf("move ") < input.indexOf("Button 2"))
        assertTrue(input, input.contains("Button 4"))
        assertTrue(input, input.contains("سلام"))
        assertTrue(input, input.contains("Key 17"))
        b.close()
        Thread.sleep(500)

        // Enter without asking once its owner allows it.
        ask("trust")
        val (c, ec) = view()
        waitFor("trusted") { ec.computer != null }
        val saved = ec.computer!!
        assertEquals(RemoteLink.KEY_SIZE, saved.key?.size)
        c.close()
        Thread.sleep(500)
        val asked = ask("asked").trim().toInt()
        val (d, ed) = view(saved)
        waitFor("back in") { ed.computer != null && ed.pieces.get() > 0 }
        assertEquals("didn't ask", asked, ask("asked").trim().toInt())
        assertNull(ed.code)
        d.close()
        Thread.sleep(500)

        // Its owner takes that back: it has to ask again, and is told the key is dead.
        ask("untrust")
        ask("allow")
        val (e, ee) = view(saved)
        waitFor("asks again") { ee.computer != null }
        assertEquals(saved.name, ee.refused)
        assertEquals(asked + 1, ask("asked").trim().toInt())
        assertNull(ee.computer!!.key)
        e.close()
    }
}
