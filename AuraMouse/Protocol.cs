using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace AuraMouse;

/// A screen edge. On the host it says where a computer sits; in Enter/Leave it says which edge the cursor crosses.
internal enum Edge { None = -1, Left = 0, Right = 1, Top = 2, Bottom = 3 }

internal static class Edges
{
    public static Edge Opposite(this Edge e) => e switch
    {
        Edge.Left => Edge.Right,
        Edge.Right => Edge.Left,
        Edge.Top => Edge.Bottom,
        Edge.Bottom => Edge.Top,
        _ => Edge.None,
    };

    public static bool IsVertical(this Edge e) => e is Edge.Left or Edge.Right;
}

/// What the sessions report to the window; the window turns it into text in the chosen language.
internal enum SessionStatus { Waiting, Connected, Disconnected, WrongCodeAttempt, CodeLocked, DiscoveryFailed }

internal enum MsgType : byte
{
    // Handshake, sent in the clear: Hello -> KeyShare -> (encrypted Auth) -> Welcome/Rejected.
    // Pairing with the code adds a PakeShare each way, after Hello and after KeyShare.
    Hello = 1,     // client -> host, payload = client public key, C = pair id if using a saved key instead of the code
    Welcome = 2,   // host -> client, sent in the clear and then again encrypted
    Rejected = 3,  // host -> client, wrong pairing code
    KeyShare = 4,  // host -> client, payload = host public key
    PakeShare = 6, // either way when pairing with the code, payload = see Pake
    Auth = 5,      // client -> host, first encrypted message (payload = computer name, A = 1 for a phone); decrypts only with the right code
    // Everything below is sent encrypted.
    Enter = 10,    // host -> client, A = Edge the cursor enters from, B = position along that edge (0..10000)
    Move = 11,     // host -> client, A = dx, B = dy
    Button = 12,   // host -> client, A = MOUSEEVENTF_* flags
    Wheel = 13,    // host -> client, A = delta, B = 1 if horizontal
    Key = 14,      // host -> client, A = vk, B = scan code, C = KEYEVENTF_* flags
    Clipboard = 15, // either way, sent as soon as something is copied; A = ClipKind, payload = UTF-8 text / PNG / JSON DragInfo
    Leave = 20,    // client -> host, B = position along the edge it left by (0..10000)
    Release = 21,  // host -> client, stop showing the cursor here (control went elsewhere)

    // A client driving with its own mouse and keyboard. While it does, it sends Move/Button/Wheel/Key to the host.
    YourEdge = 22,    // host -> client, A = the client's Edge around the host (so it knows which of its edges leads there)
    ClientEnter = 24, // client -> host, its cursor crossed into the host; B = position along the edge
    Return = 25,      // host -> client, the cursor came back to the client that is driving; B = position
    StopDriving = 26, // host -> client, someone else's mouse took over; stop capturing
    TakeOver = 27,    // client -> host, this client's own mouse or keyboard was touched; it is local again
    DragStart = 28,   // either way, files are being dragged over to you; payload = JSON DragInfo
    DropAt = 29,      // host -> client, the files you dragged over were dropped; payload = id, send them now
    TextInput = 33,   // like Key, payload = UTF-8 text, for a phone's on-screen keyboard (any language)
    WakeInfo = 31,    // either way when connecting, payload = JSON WakeInfo: how to turn this computer on later
    Lock = 30,        // either way, A = 1 while the mouse must stay on the screen it's on (the user's shortcut)
    FileKey = 40,  // host -> client, A = the client's id, payload = secret for its file connections
    FileReady = 41, // host -> client, payload = id of files the host wants to send; the client connects to fetch them
    PairKey = 42,   // host -> client after pairing with the code, A = pair id, payload = key + host name; replaces the code from now on

    // On a file connection (port 47802, always opened by the client), after its own handshake:
    FileStart = 50,   // client -> host, A = 0 client uploads / 1 client downloads, payload = transfer id
    Manifest = 51,    // sender -> receiver, payload = JSON list of files
    ManifestAck = 52, // receiver -> sender, payload = JSON: accepted? and where to resume each file
    FileChunk = 53,   // sender -> receiver, A = file index, payload = file bytes
    FileEnd = 54,     // sender -> receiver, A = file index
    Done = 55,        // sender -> receiver, then receiver -> sender once everything is on disk
    Cancel = 56,      // either way, the user cancelled

    // Remote desktop, on its own port with nothing shared with the mouse connection. In the clear:
    RemoteProbe = 60,    // viewer -> sharer, who are you? (when looking around the network)
    RemoteInfo = 61,     // sharer -> viewer, payload = this computer's name
    // Then Hello (payload = hash of the viewer's key), KeyShare (sharer's key), KeyShare (viewer's key), encrypted Auth,
    // plain Welcome or Rejected (a saved key that's no longer valid), and everything below is encrypted.
    RemoteWaiting = 63,  // sharer -> viewer, the person is being asked
    RemoteAccepted = 64, // sharer -> viewer, B = number of monitors, payload = its name
    RemoteDenied = 65,   // sharer -> viewer, A = RemoteRefusal
    RemoteTrust = 66,    // sharer -> viewer, the person allowed this device in without asking from now on; A = id, payload = key
    RemoteFrame = 67,    // sharer -> viewer, A = frame id, B and C = picture size, payload = see FrameEncoder
    RemoteAck = 68,      // viewer -> sharer, A = frame id, it's on screen
    RemotePointer = 69,  // viewer -> sharer, A and B = position on the picture, 0..65535. Also uses Button, Wheel, Key, TextInput
    RemoteMonitor = 70,  // viewer -> sharer, A = which monitor to show
    RemoteViewSize = 71, // viewer -> sharer, A = the longest picture side worth sending
}

/// Every message starts with a fixed 13 byte header: type + three ints, then an optional payload.
/// In the clear (handshake), only Hello and KeyShare have a payload and A is its length;
/// inside encrypted frames the payload is whatever follows the header.
internal readonly record struct Msg(MsgType Type, int A = 0, int B = 0, int C = 0)
{
    public const int Size = 13;
    public const int MaxPayload = 16 << 20;     // big enough for the file list of a large folder
    public const int MaxClipboard = 1_000_000;
    public const int MaxHandshakePayload = 1024;

    public byte[]? Payload { get; init; }

    public string PayloadText => Payload == null ? "" : Encoding.UTF8.GetString(Payload);

    public static bool HasPayload(MsgType type) => type is MsgType.Hello or MsgType.KeyShare or MsgType.RemoteInfo or MsgType.PakeShare;

    public static Msg WithPayload(MsgType type, byte[] payload) => new(type, payload.Length) { Payload = payload };

    public byte[] ToBytes()
    {
        var buf = new byte[Size + (Payload?.Length ?? 0)];
        WriteTo(buf);
        Payload?.CopyTo(buf, Size);
        return buf;
    }

    public void WriteTo(Span<byte> buf)
    {
        buf[0] = (byte)Type;
        BinaryPrimitives.WriteInt32LittleEndian(buf[1..], A);
        BinaryPrimitives.WriteInt32LittleEndian(buf[5..], B);
        BinaryPrimitives.WriteInt32LittleEndian(buf[9..], C);
    }

    public static Msg ReadFrom(ReadOnlySpan<byte> buf) => new(
        (MsgType)buf[0],
        BinaryPrimitives.ReadInt32LittleEndian(buf[1..]),
        BinaryPrimitives.ReadInt32LittleEndian(buf[5..]),
        BinaryPrimitives.ReadInt32LittleEndian(buf[9..]));
}

internal static class Net
{
    public const int TcpPort = 47800;
    public const int DiscoveryPort = 47801;
    public const int FilePort = 47802;
    public const int RemotePort = 47810;
    public const string Beacon = "EASYMOUSE12|"; // bumped with the protocol so old versions don't list each other

    /// Low latency, and a computer that vanished (shut down, asleep) is noticed within seconds
    /// instead of the cursor staying stuck on it.
    public static void Tune(System.Net.Sockets.TcpClient tcp)
    {
        tcp.NoDelay = true;
        var socket = tcp.Client;
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 3);
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 1);
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount, 3);
        // Keepalive only runs while nothing is being sent. With the mouse moving over a dead connection
        // Windows keeps retrying for 20 seconds or so, and the mouse stays stuck on the other side all that time.
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, (System.Net.Sockets.SocketOptionName)TcpMaxRetransmitSeconds, DeadAfterSeconds);
    }

    /// How long someone who hasn't proved anything yet may hold a connection open.
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// Names from the network are only ever for showing: no invisible characters that could disguise them.
    public static string CleanName(string? name, string fallback)
    {
        var clean = new string((name ?? "").Where(c => char.GetUnicodeCategory(c) is not (
            System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or
            System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator or
            System.Globalization.UnicodeCategory.PrivateUse)).ToArray()).Trim();
        if (clean.Length == 0) return fallback;
        return clean.Length > 40 ? clean[..40] : clean;
    }

    const int TcpMaxRetransmitSeconds = 5; // TCP_MAXRT, which .NET has no name for
    const int DeadAfterSeconds = 5;

    /// Plain (unencrypted) send, only used for the handshake.
    public static async Task SendAsync(Stream s, Msg m, CancellationToken ct) => await s.WriteAsync(m.ToBytes(), ct);

    /// Plain (unencrypted) receive, only used for the handshake.
    public static async Task<Msg> ReceiveAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[Msg.Size];
        await s.ReadExactlyAsync(buf, ct);
        var m = Msg.ReadFrom(buf);
        if (!Msg.HasPayload(m.Type)) return m;

        if (m.A < 0 || m.A > Msg.MaxHandshakePayload) throw new InvalidDataException("Handshake message too large");
        var payload = new byte[m.A];
        await s.ReadExactlyAsync(payload, ct);
        return m with { Payload = payload };
    }

    /// Converts a coordinate to 0..10000 along a span, so screens of different sizes line up.
    public static int ToRatio(int value, int start, int length) =>
        Math.Clamp((int)((long)(value - start) * 10000 / Math.Max(1, length)), 0, 10000);

    public static int FromRatio(int ratio, int start, int length) =>
        start + (int)((long)length * ratio / 10000);

    /// Position along an edge: y for left/right edges, x for top/bottom.
    public static int RatioAlong(Edge edge, int x, int y, Rectangle screen) => edge.IsVertical()
        ? ToRatio(y, screen.Top, screen.Height)
        : ToRatio(x, screen.Left, screen.Width);

    /// The point just inside the given edge at a ratio along it.
    public static Point PointOnEdge(Edge edge, int ratio, Rectangle screen, int inset)
    {
        int x = FromRatio(ratio, screen.Left, screen.Width), y = FromRatio(ratio, screen.Top, screen.Height);
        return edge switch
        {
            Edge.Left => new Point(screen.Left + inset, y),
            Edge.Right => new Point(screen.Right - 1 - inset, y),
            Edge.Top => new Point(x, screen.Top + inset),
            Edge.Bottom => new Point(x, screen.Bottom - 1 - inset),
            _ => new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2),
        };
    }
}

/// Who may start talking to this computer. Only computers on its own network, and only a few half-finished
/// connections at a time, so nobody can use up its memory by opening connections and saying nothing.
internal sealed class ConnectionGate
{
    const int MaxPerAddress = 8, MaxTotal = 64;

    readonly Dictionary<IPAddress, int> _open = [];
    int _total;

    /// Null if it isn't welcome. Dispose it once the connection has proved itself, or when it ends.
    public IDisposable? Enter(System.Net.Sockets.TcpClient tcp)
    {
        if ((tcp.Client.RemoteEndPoint as IPEndPoint)?.Address is not { } ip || !LanScan.IsNearby(ip)) return null;
        lock (_open)
        {
            _open.TryGetValue(ip, out int count);
            if (count >= MaxPerAddress || _total >= MaxTotal) return null;
            _open[ip] = count + 1;
            _total++;
        }
        return new Ticket(this, ip);
    }

    void Leave(IPAddress ip)
    {
        lock (_open)
        {
            _total--;
            if (--CollectionsMarshal.GetValueRefOrNullRef(_open, ip) == 0) _open.Remove(ip);
        }
    }

    sealed class Ticket(ConnectionGate gate, IPAddress ip) : IDisposable
    {
        int _left;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) gate.Leave(ip);
        }
    }
}
