package com.auramouse.app.core

import java.io.IOException
import java.math.BigInteger
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Turns the short pairing code into a strong shared secret (SPAKE2 in the 2048-bit group of RFC 3526).
 * Nothing sent lets anyone test guesses at the code offline. Always the client side here.
 * The numbers must match Pake.cs in the Windows program exactly.
 */
class Pake(code: Int) {
    private val w = expand("EasyMouse pake w$code".toByteArray(Charsets.UTF_8)).mod(Q)
    private val secret = BigInteger(1, ByteArray(40).also { SecureRandom().nextBytes(it) }).add(BigInteger.ONE)

    /** What to send to the computer. */
    val share: ByteArray = encode(G.modPow(secret, P).multiply(CLIENT_MASK.modPow(w, P)).mod(P))

    /** The secret both sides end up with only if they used the same code. Throws on a share that isn't valid. */
    fun finish(hostShare: ByteArray, clientPublic: ByteArray, hostPublic: ByteArray): ByteArray {
        if (hostShare.size != SHARE_SIZE) throw IOException("Bad share size")
        val theirs = BigInteger(1, hostShare)
        if (theirs <= BigInteger.ONE || theirs >= P.subtract(BigInteger.ONE) || theirs.modPow(Q, P) != BigInteger.ONE) throw IOException("Bad share")

        // Their mask to the power (Q - w) is the inverse of it to the power w, because the mask has order Q.
        val unmasked = theirs.multiply(HOST_MASK.modPow(Q.subtract(w), P)).mod(P)
        val key = unmasked.modPow(secret, P)
        if (key <= BigInteger.ONE) throw IOException("Bad share")

        val digest = MessageDigest.getInstance("SHA-256")
        digest.update("EasyMouse pake key".toByteArray(Charsets.UTF_8))
        digest.update(share)
        digest.update(hostShare)
        digest.update(encode(key))
        digest.update(encode(w))
        digest.update(clientPublic)
        return digest.digest(hostPublic)
    }

    companion object {
        const val SHARE_SIZE = 256

        private val P = BigInteger(
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
                "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
                "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
                "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
                "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
                "15728E5A8AACAA68FFFFFFFFFFFFFFFF", 16,
        )
        private val Q = P.subtract(BigInteger.ONE).shiftRight(1)
        private val G = BigInteger.valueOf(4)
        private val TWO = BigInteger.valueOf(2)
        private val CLIENT_MASK = expand("EasyMouse pake M".toByteArray(Charsets.UTF_8)).modPow(TWO, P)
        private val HOST_MASK = expand("EasyMouse pake N".toByteArray(Charsets.UTF_8)).modPow(TWO, P)

        private fun encode(value: BigInteger): ByteArray {
            val bytes = value.toByteArray().dropWhile { it == 0.toByte() }.toByteArray()
            return ByteArray(SHARE_SIZE - bytes.size) + bytes
        }

        private fun expand(label: ByteArray): BigInteger {
            val output = ByteArray(320)
            for (block in 0 until 10) {
                val digest = MessageDigest.getInstance("SHA-256")
                digest.update(block.toByte())
                digest.digest(label).copyInto(output, block * 32)
            }
            return BigInteger(1, output).mod(P)
        }
    }
}

/** Names from the network are only ever for showing: no invisible characters that could disguise them. */
object Names {
    private const val MAX = 40

    fun clean(name: String, fallback: String): String {
        val kept = name.filter {
            when (Character.getType(it).toByte()) {
                Character.CONTROL, Character.FORMAT, Character.LINE_SEPARATOR, Character.PARAGRAPH_SEPARATOR, Character.PRIVATE_USE -> false
                else -> true
            }
        }.trim()
        return if (kept.isEmpty()) fallback else kept.take(MAX)
    }
}
