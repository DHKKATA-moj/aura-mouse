package com.auramouse.app.core

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.util.concurrent.Callable
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Finding the computer and waking it. Nothing here touches the mobile data connection. */
object Network {
    private const val PROBE_MS = 700
    private const val THREADS = 64
    private val NOT_LOCAL = listOf("rmnet", "ccmni", "pdp", "wwan", "tun", "lo", "dummy")
    private val WAKE_PORTS = intArrayOf(9, 7)

    private class Local(val address: InetAddress, val broadcast: InetAddress?)

    private fun localNetworks(): List<Local> {
        val result = ArrayList<Local>()
        for (nic in NetworkInterface.getNetworkInterfaces() ?: return result) {
            if (!nic.isUp || nic.isLoopback || NOT_LOCAL.any { nic.name.startsWith(it) }) continue
            for (entry in nic.interfaceAddresses) {
                val ip = entry.address as? Inet4Address ?: continue
                if (ip.isSiteLocalAddress) result.add(Local(ip, entry.broadcast))
            }
        }
        return result
    }

    /** Computers on our network (the /24 around our address) with Aura Mouse's port open. */
    fun findHosts(): List<InetAddress> {
        val locals = localNetworks()
        val own = locals.map { it.address }.toSet()
        val candidates = LinkedHashSet<InetAddress>()
        for (local in locals) {
            val b = local.address.address
            for (last in 1..254) candidates.add(InetAddress.getByAddress(byteArrayOf(b[0], b[1], b[2], last.toByte())))
        }
        candidates.removeAll(own)

        val pool = Executors.newFixedThreadPool(THREADS)
        try {
            return pool.invokeAll(candidates.map { ip -> Callable { if (isOpen(ip)) ip else null } }, 15, TimeUnit.SECONDS)
                .mapNotNull { runCatching { it.get() }.getOrNull() }
        } finally {
            pool.shutdownNow()
        }
    }

    /** Computers on our network accepting remote desktop requests, with their names. */
    fun findRemote(): List<Pair<String, InetAddress>> {
        val locals = localNetworks()
        val own = locals.map { it.address }.toSet()
        val candidates = LinkedHashSet<InetAddress>()
        for (local in locals) {
            val b = local.address.address
            for (last in 1..254) candidates.add(InetAddress.getByAddress(byteArrayOf(b[0], b[1], b[2], last.toByte())))
        }
        candidates.removeAll(own)

        val pool = Executors.newFixedThreadPool(THREADS)
        try {
            return pool.invokeAll(candidates.map { ip -> Callable { RemoteLink.askName(ip, PROBE_MS)?.let { it to ip } } }, 15, TimeUnit.SECONDS)
                .mapNotNull { runCatching { it.get() }.getOrNull() }
                .sortedBy { it.first.lowercase() }
        } finally {
            pool.shutdownNow()
        }
    }

    private fun isOpen(ip: InetAddress): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(ip, Ports.MAIN), PROBE_MS) }
        true
    } catch (_: Exception) {
        false
    }

    /** 6 bytes of FF, then the address 16 times. */
    fun magicPacket(mac: String): ByteArray {
        val address = ByteArray(6) { mac.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        return ByteArray(6) { 0xFF.toByte() } + (1..16).flatMap { address.asIterable() }.toByteArray()
    }

    /** Returns how many networks the signal went out on. */
    fun wake(macs: List<String>): Int {
        val packets = macs.filter { Regex("^[0-9A-Fa-f]{12}$").matches(it) }.map(::magicPacket)
        var sent = 0
        for (local in localNetworks()) {
            try {
                DatagramSocket(InetSocketAddress(local.address, 0)).use { socket ->
                    socket.broadcast = true
                    val targets = listOfNotNull(local.broadcast, InetAddress.getByName("255.255.255.255"))
                    repeat(3) {
                        for (packet in packets) for (target in targets) for (port in WAKE_PORTS)
                            socket.send(DatagramPacket(packet, packet.size, target, port))
                        Thread.sleep(100)
                    }
                }
                sent++
            } catch (_: Exception) {
            }
        }
        return sent
    }
}
