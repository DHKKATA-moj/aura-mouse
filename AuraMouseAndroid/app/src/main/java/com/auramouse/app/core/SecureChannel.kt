package com.auramouse.app.core

import java.io.DataInputStream
import java.io.IOException
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * The encrypted stream, matching SecureChannel.cs: ECDH P-256 mixed with a shared secret through HKDF,
 * then AES-GCM with a counter for each direction. Frame: [int32 length][ciphertext][16 byte tag].
 * One thread may send while another receives.
 */
class SecureChannel private constructor(
    private val input: DataInputStream,
    private val output: OutputStream,
    sendKeyBytes: ByteArray,
    recvKeyBytes: ByteArray,
) {
    private val sendKey = SecretKeySpec(sendKeyBytes, "AES")
    private val recvKey = SecretKeySpec(recvKeyBytes, "AES")
    private val sendCipher = Cipher.getInstance("AES/GCM/NoPadding")
    private val recvCipher = Cipher.getInstance("AES/GCM/NoPadding")
    private var sent = 0L
    private var received = 0L

    fun send(m: Msg) = send(m.type, m.a, m.b, m.c, m.payload ?: EMPTY, 0, m.payload?.size ?: 0)

    @Synchronized
    fun send(type: Int, a: Int, b: Int, c: Int, payload: ByteArray, offset: Int, length: Int) {
        val plainLength = Plain.HEADER + length
        val plain = ByteArray(plainLength)
        ByteBuffer.wrap(plain).order(ByteOrder.LITTLE_ENDIAN).put(type.toByte()).putInt(a).putInt(b).putInt(c).put(payload, offset, length)

        val header = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(plainLength).array()
        sendCipher.init(Cipher.ENCRYPT_MODE, sendKey, GCMParameterSpec(TAG_BITS, nonce(sent++)))
        sendCipher.updateAAD(header)
        val sealed = sendCipher.doFinal(plain)

        val frame = ByteArray(4 + sealed.size)
        System.arraycopy(header, 0, frame, 0, 4)
        System.arraycopy(sealed, 0, frame, 4, sealed.size)
        output.write(frame)
        output.flush()
    }

    fun receive(): Msg {
        val header = ByteArray(4)
        input.readFully(header)
        val length = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN).int
        if (length < Plain.HEADER || length > MAX_FRAME) throw IOException("Bad frame length")

        val sealed = ByteArray(length + TAG_BITS / 8)
        input.readFully(sealed)
        recvCipher.init(Cipher.DECRYPT_MODE, recvKey, GCMParameterSpec(TAG_BITS, nonce(received++)))
        recvCipher.updateAAD(header)
        val plain = recvCipher.doFinal(sealed)

        val b = ByteBuffer.wrap(plain).order(ByteOrder.LITTLE_ENDIAN)
        val type = b.get().toInt() and 0xFF
        val a = b.int
        val second = b.int
        val c = b.int
        val body = if (plain.size > Plain.HEADER) plain.copyOfRange(Plain.HEADER, plain.size) else null
        return Msg(type, a, second, c, body)
    }

    private fun nonce(counter: Long): ByteArray {
        val nonce = ByteArray(12)
        ByteBuffer.wrap(nonce, 4, 8).order(ByteOrder.LITTLE_ENDIAN).putLong(counter)
        return nonce
    }

    companion object {
        private const val TAG_BITS = 128
        private const val MAX_FRAME = Plain.HEADER + (16 shl 20)
        private val EMPTY = ByteArray(0)

        fun newKey(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

        /** Always the client side here: the phone connects to the computer. */
        fun create(input: DataInputStream, output: OutputStream, mine: KeyPair, hostPublic: ByteArray, secret: ByteArray): SecureChannel {
            val clientPublic = mine.public.encoded
            val peer = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(hostPublic))
            val shared = KeyAgreement.getInstance("ECDH").run {
                init(mine.private)
                doPhase(peer, true)
                generateSecret()
            }
            val salt = MessageDigest.getInstance("SHA-256").digest(clientPublic + hostPublic)
            val prk = hmac(salt, shared + secret)
            return SecureChannel(input, output, expand(prk, "EasyMouse c2h"), expand(prk, "EasyMouse h2c"))
        }

        /** HKDF-Expand for a single 32 byte block. */
        private fun expand(prk: ByteArray, info: String) = hmac(prk, info.toByteArray(Charsets.UTF_8) + byteArrayOf(1))

        private fun hmac(key: ByteArray, data: ByteArray): ByteArray =
            Mac.getInstance("HmacSHA256").run {
                init(SecretKeySpec(key, "HmacSHA256"))
                doFinal(data)
            }
    }
}
