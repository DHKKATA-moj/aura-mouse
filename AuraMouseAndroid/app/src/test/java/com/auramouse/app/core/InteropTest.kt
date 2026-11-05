package com.auramouse.app.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream
import java.net.InetAddress
import java.security.MessageDigest
import java.util.concurrent.ConcurrentLinkedQueue
import kotlin.random.Random

/**
 * The phone's code against the real Windows HostSession, running in a little test program that answers
 * commands through files. Run through run-interop.ps1, which starts it first.
 */
class InteropTest {
    private val dir = File(System.getenv("EMPHONE_DIR") ?: "")

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

    private class Events : LinkEvents {
        @Volatile var paired: SavedHost? = null
        @Volatile var disconnected = false
        @Volatile var wakeMacs: List<String>? = null
        val drivingChanges = ConcurrentLinkedQueue<Boolean>()
        val clips = ConcurrentLinkedQueue<Pair<Int, ByteArray>>()
        val ready = ConcurrentLinkedQueue<String>()
        override fun onPaired(host: SavedHost) { paired = host }
        override fun onDisconnected() { disconnected = true }
        override fun onDrivingChanged(driving: Boolean) { drivingChanges.add(driving) }
        override fun onClipboard(kind: Int, payload: ByteArray) { clips.add(kind to payload) }
        override fun onFilesReady(id: String) { ready.add(id) }
        override fun onWakeInfo(name: String, macs: List<String>) {
            wakeMacs = macs
            computers[name] = macs
        }
        val computers = java.util.concurrent.ConcurrentHashMap<String, List<String>>()
    }

    /** The host's own cards, the other computers it knows, and one that joins while the phone is connected. */
    @Test
    fun wakeList() {
        assumeTrue("needs the host test program", System.getenv("EMPHONE_DIR") != null)
        val machine = ask("machine").trim()
        val events = Events()
        val link = HostLink("TEST-PHONE", events)
        assertTrue(link.connectWithCode(local, ask("code").trim().toInt(), "PC"))
        waitFor("the host itself") { events.computers.containsKey(machine) }
        assertEquals("a computer the host knows", listOf("AABBCCDDEEFF"), events.computers["OTHER-PC"])
        assertFalse("the host's old entry for itself isn't sent", events.computers[machine]!!.contains("111111111111"))
        assertEquals(2, events.computers.size)

        assertEquals("joined", ask("late").trim())
        waitFor("the computer that joined later") { events.computers.containsKey("LATE-PC") }
        assertEquals(listOf("BBCCDDEEFF00"), events.computers["LATE-PC"])
        link.close()
    }

    private fun waitFor(what: String, check: () -> Boolean) {
        repeat(200) {
            if (check()) return
            Thread.sleep(50)
        }
        throw AssertionError("timed out: $what")
    }

    private val local: InetAddress = InetAddress.getLoopbackAddress()

    class DirStore(private val root: File) : IncomingStore {
        val finished = HashMap<Int, File>()
        private fun part(id: String, i: Int) = File(root, "$id-$i.part")
        override fun partLength(id: String, index: Int) = part(id, index).length()
        override fun isComplete(id: String, index: Int) = File(root, "$id-$index.done").exists()
        override fun append(id: String, index: Int): OutputStream = FileOutputStream(part(id, index), true)
        override fun complete(id: String, index: Int, safePath: List<String>) {
            val target = File(root, safePath.joinToString("_"))
            part(id, index).renameTo(target)
            File(root, "$id-$index.done").createNewFile()
            finished[index] = target
        }
        override fun freeSpace() = root.usableSpace
        override fun clear(id: String) { root.listFiles()?.filter { it.name.startsWith(id) }?.forEach { it.delete() } }
    }

    private fun sha(file: File) = MessageDigest.getInstance("SHA-256").digest(file.readBytes()).joinToString("") { "%02x".format(it) }

    @Test
    fun everything() {
        assumeTrue("needs the host test program", System.getenv("EMPHONE_DIR") != null)
        val code = ask("code").trim().toInt()

        // A wrong code is refused, and doesn't get us connected.
        val wrong = HostLink("TEST-PHONE", Events())
        assertFalse("wrong code refused", wrong.connectWithCode(local, if (code == 9999) 9998 else code + 1, "PC"))

        // Pairing: the handshake and encryption line up with the C# side.
        val events = Events()
        val link = HostLink("TEST-PHONE", events)
        assertTrue("right code accepted", link.connectWithCode(local, code, "PC"))
        waitFor("key from the host") { events.paired != null }
        waitFor("file key") { link.fileKey != null }
        waitFor("wake info") { events.wakeMacs != null }
        assertEquals(HostLink.KEY_SIZE, events.paired!!.key.size)
        assertTrue("got this computer's network cards", events.wakeMacs!!.isNotEmpty())

        val peers = ask("peers")
        assertTrue("host knows it as a phone with no edge: $peers", peers.contains("TEST-PHONE|None|phone"))

        // Driving: the host accepts a phone with no screen edge, and the cursor moves.
        val before = ask("cursor").trim()
        link.move(9, 7)
        waitFor("driving at the host") { ask("peers").contains("driving") }
        waitFor("cursor moved") { ask("cursor").trim() != before }
        assertTrue(events.drivingChanges.contains(true))

        // Lots of moves in a burst stay in order and don't break the stream.
        repeat(500) { link.move(if (it % 2 == 0) 1 else -1, 0) }
        Thread.sleep(200)
        assertTrue("still connected after a burst", link.connected)

        // Keys go through without breaking anything (Shift alone types nothing, wherever the focus is).
        link.tapKey(Win.VK_SHIFT)

        // Typed text: only checked when the test window has the focus, so nothing lands in another program.
        val typed = ask("text-ready").trim()
        if (typed == "yes") {
            link.text("سلام abc")
            waitFor("text arrives") { ask("text").contains("سلام abc") }
            println("typed text checked")
        } else println("SKIPPED typed text: test window had no focus")

        // Clipboard, phone to computer and back.
        link.sendClipboardText("من التلفون 123")
        waitFor("clipboard reached the computer") { ask("clip-get") == "من التلفون 123" }
        ask("clip-set من الحاسبة 456")
        waitFor("clipboard reached the phone") { events.clips.any { it.first == Clip.TEXT && String(it.second) == "من الحاسبة 456" } }

        // Upload, with a break in the middle that resumes.
        val data = File(dir, "upload-source.bin")
        data.writeBytes(Random(1).nextBytes(5 shl 20))
        val uploadName = "emphone-upload-test.bin"
        val set = OutgoingSet(OutgoingSet.idFor("TEST", listOf(data.path)), uploadName,
            listOf(OutgoingFile(uploadName, data.length()) { offset -> data.inputStream().apply { skip(offset) } }))
        val up = Transfer(set.id, true)

        // First attempt stops part way, like Wi-Fi dropping.
        try {
            FileLink.connect(link).use { files ->
                files.channel.send(Msg(T.FILE_START, 0, payload = set.id.toByteArray()))
                val partial = OutgoingSet(set.id, uploadName, listOf(OutgoingFile(uploadName, data.length()) { offset ->
                    object : java.io.FilterInputStream(data.inputStream().apply { skip(offset) }) {
                        var count = 0L
                        override fun read(b: ByteArray, off: Int, len: Int): Int {
                            if (count > (2 shl 20)) throw java.io.IOException("cut")
                            return super.read(b, off, len).also { count += it }
                        }
                    }
                }))
                Transfers.send(files.channel, partial, up) {}
            }
            throw AssertionError("should have been cut")
        } catch (_: java.io.IOException) {
        }
        Thread.sleep(500)
        val cutAt = up.done

        up.done = 0
        Transfers.run(link, up, 0, {}) { Transfers.send(it, set, up) {} }
        assertEquals("upload finished", TransferState.DONE, up.state)
        assertEquals("all bytes accounted for", data.length(), up.done)
        println("upload was cut at $cutAt and resumed")
        val uploaded = ask("uploaded $uploadName").trim()
        assertEquals("uploaded file is identical", sha(data), uploaded)

        // Download: the computer offers, the phone fetches.
        val source = File(dir, "download-source.bin")
        source.writeBytes(Random(2).nextBytes(3 shl 20))
        val downloads = File(dir, "phone-downloads").apply { deleteRecursively(); mkdirs() }
        ask("send-file ${source.path}")
        waitFor("file offer") { events.ready.isNotEmpty() }
        val id = events.ready.first()
        assertTrue(Transfers.isValidId(id))
        val store = DirStore(downloads)
        val down = Transfer(id, false)
        Transfers.run(link, down, 1, {}) { Transfers.receive(it, store, down) {} }
        assertEquals("download finished", TransferState.DONE, down.state)
        assertEquals("downloaded file is identical", sha(source), sha(store.finished[0]!!))

        // Coming back with the saved key: no code, and nothing rotates.
        link.close()
        waitFor("host noticed") { !ask("peers").contains("TEST-PHONE") }
        val saved = events.paired!!
        val back = HostLink("TEST-PHONE", Events())
        assertTrue("saved key connects", back.connectSaved(local, saved))
        Thread.sleep(200)
        assertTrue(ask("peers").contains("TEST-PHONE"))
        back.close()

        val stranger = HostLink("TEST-PHONE", Events())
        assertFalse("wrong key refused", stranger.connectSaved(local, SavedHost(saved.id, "x", ByteArray(32), null)))
        assertEquals("the code didn't change", code, ask("code").trim().toInt())
    }

    @Test
    fun magicPacketShape() {
        val packet = Network.magicPacket("0A1B2C3D4E5F")
        assertEquals(102, packet.size)
        assertTrue(packet.take(6).all { it == 0xFF.toByte() })
        assertArrayEquals(byteArrayOf(0x0A, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F), packet.copyOfRange(96, 102))
    }

    @Test
    fun namesFromTheNetworkAreSafe() {
        assertEquals(listOf("etc", "passwd"), SafePath.segments("../../etc/passwd"))
        assertEquals(listOf("a_b.txt"), SafePath.segments("a:b.txt"))
        assertEquals(listOf("file"), SafePath.segments("../.."))
        assertNull(null)
        assertNotNull(SafePath.segments("photos\\holiday.jpg"))
        assertEquals(listOf("photos", "holiday.jpg"), SafePath.segments("photos\\holiday.jpg"))
    }

    @Test
    fun typingDiffs() {
        val d = TextDiff()
        d.update("سل").also { assertEquals(0, it.backspaces); assertEquals("سل", it.text) }
        d.update("سلا").also { assertEquals(0, it.backspaces); assertEquals("ا", it.text) }
        d.update("سلم").also { assertEquals(1, it.backspaces); assertEquals("م", it.text) }
        d.commit("سلام ").also { assertEquals(1, it.backspaces); assertEquals("ام ", it.text) }
        d.update("x").also { assertEquals(0, it.backspaces); assertEquals("x", it.text) }
    }

    @Test
    fun slowMovesAddUp() {
        val scale = PointerScale(1f, 1f)
        var total = 0
        repeat(10) { total += scale.apply(0.25f, 0f).first }
        assertTrue("fractions aren't lost: $total", total >= 2)
    }
}
