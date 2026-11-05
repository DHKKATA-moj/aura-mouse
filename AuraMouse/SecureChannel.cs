using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AuraMouse;

/// AES-GCM encrypted message stream. Keys come from an ECDH exchange mixed with a shared secret
/// (from pairing with the code, see Pake, or a per-computer key), so someone listening on the
/// network can't read anything, and a wrong secret simply fails to decrypt.
/// Frame: [int32 length][ciphertext][16 byte tag]; the nonce is a per-direction counter.
/// Buffers are reused between frames, so one channel must have one sender and one receiver at a time.
internal sealed class SecureChannel : IDisposable
{
    const int TagSize = 16;
    public const int MaxFrame = Msg.Size + Msg.MaxPayload;
    public const int HandshakeFrame = 4096;

    readonly Stream _stream;
    readonly AesGcm _sendKey, _recvKey;
    readonly byte[] _lengthBuffer = new byte[4];
    byte[] _sendBuffer = new byte[4096], _recvBuffer = new byte[4096], _plain = new byte[4096];
    int _lastPayloadLength;
    ulong _sent, _received;

    /// Lowered until the other side has proved itself, so a stranger can't make us set aside big buffers.
    public int FrameLimit { get; set; } = MaxFrame;

    SecureChannel(Stream stream, byte[] sendKey, byte[] recvKey)
    {
        _stream = stream;
        _sendKey = new AesGcm(sendKey, TagSize);
        _recvKey = new AesGcm(recvKey, TagSize);
    }

    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static SecureChannel Create(Stream stream, ECDiffieHellman mine, byte[] clientPublic, byte[] hostPublic, byte[] secret, bool isHost)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(isHost ? clientPublic : hostPublic, out _);
        var shared = mine.DeriveRawSecretAgreement(peer.PublicKey);

        byte[] ikm = [.. shared, .. secret];
        // These labels keep the program's first name on purpose: changing them would break every saved pairing.
        var salt = SHA256.HashData([.. clientPublic, .. hostPublic]);
        var clientToHost = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, "EasyMouse c2h"u8.ToArray());
        var hostToClient = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, "EasyMouse h2c"u8.ToArray());

        return isHost
            ? new SecureChannel(stream, hostToClient, clientToHost)
            : new SecureChannel(stream, clientToHost, hostToClient);
    }

    public Task SendAsync(Msg m, CancellationToken ct) => SendAsync(m.Type, m.A, m.B, m.C, m.Payload, ct);

    /// Sends a message whose payload is a slice of a caller's buffer, with no extra copies or allocations.
    public async Task SendAsync(MsgType type, int a, int b, int c, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        int length = Seal(type, a, b, c, payload.Span);
        await _stream.WriteAsync(_sendBuffer.AsMemory(0, length), ct);
    }

    public void Send(Msg m)
    {
        int length = Seal(m.Type, m.A, m.B, m.C, m.Payload);
        _stream.Write(_sendBuffer, 0, length);
    }

    /// Throws AuthenticationTagMismatchException if the data was tampered with or the keys differ.
    public async Task<Msg> ReceiveAsync(CancellationToken ct)
    {
        var m = await ReceiveFrameAsync(ct);
        return _lastPayloadLength > 0 ? m with { Payload = LastPayload.ToArray() } : m;
    }

    /// Like ReceiveAsync, but leaves the payload in an internal buffer (LastPayload) that the next receive reuses.
    public async Task<Msg> ReceiveFrameAsync(CancellationToken ct)
    {
        await _stream.ReadExactlyAsync(_lengthBuffer, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(_lengthBuffer);
        if (length < Msg.Size || length > FrameLimit) throw new InvalidDataException("Bad frame length");

        if (_recvBuffer.Length < length + TagSize) _recvBuffer = new byte[length + TagSize];
        await _stream.ReadExactlyAsync(_recvBuffer.AsMemory(0, length + TagSize), ct);
        return Open(length);
    }

    public ReadOnlyMemory<byte> LastPayload => _plain.AsMemory(Msg.Size, _lastPayloadLength);

    int Seal(MsgType type, int a, int b, int c, ReadOnlySpan<byte> payload)
    {
        int plainLength = Msg.Size + payload.Length;
        int total = 4 + plainLength + TagSize;
        if (_sendBuffer.Length < total) _sendBuffer = new byte[total];

        var frame = _sendBuffer.AsSpan(0, total);
        BinaryPrimitives.WriteInt32LittleEndian(frame, plainLength);
        var body = frame.Slice(4, plainLength);
        new Msg(type, a, b, c).WriteTo(body);
        payload.CopyTo(body[Msg.Size..]);

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _sent++);
        _sendKey.Encrypt(nonce, body, body, frame.Slice(4 + plainLength, TagSize), frame[..4]); // in place
        return total;
    }

    Msg Open(int length)
    {
        if (_plain.Length < length) _plain = new byte[length];
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _received++);
        _recvKey.Decrypt(nonce, _recvBuffer.AsSpan(0, length), _recvBuffer.AsSpan(length, TagSize), _plain.AsSpan(0, length), _lengthBuffer);
        _lastPayloadLength = length - Msg.Size;
        return Msg.ReadFrom(_plain);
    }

    public void Dispose()
    {
        _sendKey.Dispose();
        _recvKey.Dispose();
    }
}
