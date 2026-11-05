using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AuraMouse;

/// Turns the short pairing code into a strong shared secret (SPAKE2 in the 2048-bit group of RFC 3526).
/// Nothing sent lets anyone test guesses at the code offline, not even a fake computer the user connects to:
/// each guess takes a real attempt against the other side, and HostSession allows only a few.
/// The numbers must match Pake.kt on the phone exactly.
internal sealed class Pake
{
    public const int ShareSize = 256;

    static readonly BigInteger P = BigInteger.Parse(
        "0FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AACAA68FFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
    static readonly BigInteger Q = (P - 1) / 2;
    static readonly BigInteger G = 4; // a square, so it's in the group of order Q

    // Hashed into the group, so nobody knows how they relate to G. That's what makes the code unguessable offline.
    static readonly BigInteger ClientMask = BigInteger.ModPow(Expand("EasyMouse pake M"u8), 2, P);
    static readonly BigInteger HostMask = BigInteger.ModPow(Expand("EasyMouse pake N"u8), 2, P);

    readonly bool _isClient;
    readonly BigInteger _w, _secret;

    /// What to send to the other side.
    public byte[] Share { get; }

    public Pake(int code, bool isClient)
    {
        _isClient = isClient;
        _w = Expand(Encoding.UTF8.GetBytes("EasyMouse pake w" + code)) % Q;
        _secret = new BigInteger(RandomNumberGenerator.GetBytes(40), isUnsigned: true, isBigEndian: true) + 1;
        Share = Encode(BigInteger.ModPow(G, _secret, P) * BigInteger.ModPow(isClient ? ClientMask : HostMask, _w, P) % P);
    }

    /// The secret both sides end up with only if they used the same code. Throws on a share that isn't valid.
    public byte[] Finish(byte[] theirShare, byte[] clientPublic, byte[] hostPublic)
    {
        if (theirShare.Length != ShareSize) throw new InvalidDataException("Bad share size");
        var theirs = new BigInteger(theirShare, isUnsigned: true, isBigEndian: true);
        if (theirs <= 1 || theirs >= P - 1 || BigInteger.ModPow(theirs, Q, P) != 1) throw new InvalidDataException("Bad share");

        // Their mask to the power (Q - w) is the inverse of it to the power w, because the mask has order Q.
        var unmasked = theirs * BigInteger.ModPow(_isClient ? HostMask : ClientMask, Q - _w, P) % P;
        var key = BigInteger.ModPow(unmasked, _secret, P);
        if (key <= 1) throw new InvalidDataException("Bad share");

        var clientShare = _isClient ? Share : theirShare;
        var hostShare = _isClient ? theirShare : Share;
        return SHA256.HashData([.. "EasyMouse pake key"u8, .. clientShare, .. hostShare, .. Encode(key), .. Encode(_w), .. clientPublic, .. hostPublic]);
    }

    static byte[] Encode(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var fixedSize = new byte[ShareSize];
        bytes.CopyTo(fixedSize, ShareSize - bytes.Length);
        return fixedSize;
    }

    /// A number as long as the group's, plus some, so it's evenly spread once reduced.
    static BigInteger Expand(ReadOnlySpan<byte> label)
    {
        var output = new byte[320];
        for (int block = 0; block < 10; block++)
            SHA256.HashData([(byte)block, .. label]).CopyTo(output, block * 32);
        return new BigInteger(output, isUnsigned: true, isBigEndian: true) % P;
    }
}
