package com.auramouse.app.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.net.InetAddress

/** The phone's pairing against the real Windows HostSession. EMPHONE_CODE_FILE names where that test host writes its code. */
class PairingInteropTest {
    private class Events : LinkEvents {
        @Volatile var paired: SavedHost? = null
        override fun onPaired(host: SavedHost) { paired = host }
        override fun onDisconnected() {}
        override fun onDrivingChanged(driving: Boolean) {}
        override fun onClipboard(kind: Int, payload: ByteArray) {}
        override fun onFilesReady(id: String) {}
        override fun onWakeInfo(name: String, macs: List<String>) {}
    }

    private val local = InetAddress.getLoopbackAddress()
    private val codeFile = System.getenv("EMPHONE_CODE_FILE")?.let(::File)

    private fun code() = codeFile!!.readText().trim().toInt()

    @Test
    fun pairsThroughPake() {
        assumeTrue("needs the host test program", codeFile != null)

        val first = code()
        assertEquals("the code has six digits", 6, first.toString().length)
        assertFalse("a wrong code is refused", HostLink("TEST-PHONE", Events()).connectWithCode(local, if (first == 999999) 100000 else first + 1, "PC"))

        val events = Events()
        val link = HostLink("TEST-PHONE", events)
        assertTrue("the right code works", link.connectWithCode(local, first, "PC"))
        repeat(100) { if (events.paired == null) Thread.sleep(50) }
        val saved = events.paired
        assertNotNull("the computer gave a key", saved)
        assertEquals(32, saved!!.key.size)
        link.close()
        Thread.sleep(300)

        assertTrue("the code works only once", code() != first)
        assertFalse("the used code is refused", HostLink("TEST-PHONE", Events()).connectWithCode(local, first, "PC"))

        val again = HostLink("TEST-PHONE", Events())
        assertTrue("the saved key connects without a code", again.connectSaved(local, saved))
        again.close()
    }
}
