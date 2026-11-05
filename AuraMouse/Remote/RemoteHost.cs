using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using static AuraMouse.Native;

namespace AuraMouse.Remote;

internal enum RemoteRefusal { Declined = 0, Busy = 1 }

/// Someone on the network wants in. Code is what their screen shows too, so the person can tell it's really them.
internal sealed record RemoteRequest(string Name, bool IsPhone, string Code, IPAddress Ip);

internal sealed record RemoteAnswer(bool Allow, bool Trust);

/// Where the viewer's mouse and keyboard end up.
internal interface IRemoteInput
{
    void MoveTo(int x, int y);
    void Play(Msg m);
    void ReleaseAll();
}

/// Plays it on this computer, remembering what's held down so nothing stays stuck when the session ends.
internal sealed class WindowsRemoteInput : IRemoteInput
{
    const int ButtonFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP | MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP | MOUSEEVENTF_MIDDLEDOWN | MOUSEEVENTF_MIDDLEUP;

    readonly HashSet<(int Vk, int Scan, int Flags)> _keys = [];
    readonly HashSet<int> _buttons = [];

    public void MoveTo(int x, int y) => SetCursorPos(x, y);

    public void Play(Msg m)
    {
        switch (m.Type)
        {
            case MsgType.Button:
                int flags = m.A & ButtonFlags;
                if (flags == 0) return;
                if (flags is MOUSEEVENTF_LEFTDOWN or MOUSEEVENTF_RIGHTDOWN or MOUSEEVENTF_MIDDLEDOWN) _buttons.Add(flags);
                else _buttons.Remove(flags >> 1);
                SendMouse(flags, 0);
                break;
            case MsgType.Key:
                int keyFlags = m.C & (KEYEVENTF_KEYUP | KEYEVENTF_EXTENDEDKEY);
                var key = (m.A & 0xFF, m.B & 0xFFFF, keyFlags & KEYEVENTF_EXTENDEDKEY);
                if ((keyFlags & KEYEVENTF_KEYUP) != 0) _keys.Remove(key);
                else _keys.Add(key);
                SendKey(key.Item1, key.Item2, keyFlags);
                break;
            case MsgType.Wheel or MsgType.TextInput:
                InputPlayer.Play(m);
                break;
        }
    }

    public void ReleaseAll()
    {
        foreach (var flags in _buttons) SendMouse(flags << 1, 0);
        foreach (var (vk, scan, flags) in _keys) SendKey(vk, scan, flags | KEYEVENTF_KEYUP);
        _buttons.Clear();
        _keys.Clear();
    }
}

/// The handshake details both sides of remote desktop agree on.
internal static class RemoteHandshake
{
    public static readonly byte[] NoKey = "EasyMouse remote"u8.ToArray();

    /// Six digits from both public keys. A machine in the middle would make the two screens show different digits.
    /// The viewer commits to its key before seeing ours, so it can't search for a key that gives matching digits.
    public static string Code(byte[] viewerPublic, byte[] sharerPublic)
    {
        var hash = SHA256.HashData([.. "EasyMouse code"u8.ToArray(), .. viewerPublic, .. sharerPublic]);
        var n = BitConverter.ToUInt32(hash, 0) % 1_000_000;
        return n.ToString("000000"); // no space: in Arabic text the two halves would swap places
    }
}

/// The side being viewed. It's separate from the mouse sharing: its own port, file and threads, running
/// whether or not those are connected. One viewer at a time, and nobody gets in unless the person here
/// says yes or said yes earlier for that device.
internal sealed class RemoteHost : IDisposable
{
    const int MaxViewSide = 3840, MinViewSide = 480, DefaultViewSide = 2560, MaxInFlight = 2;
    const int FrameMs = 25, IdleFrameMs = 66, IdleAfterFrames = 40;
    const int SlowAckMs = 120, FastAckMs = 50, LowestQuality = 35, HighestQuality = 75, SharpQuality = 90;
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5), AskTimeout = TimeSpan.FromSeconds(30), DeclinedPause = TimeSpan.FromSeconds(15), AskGap = TimeSpan.FromSeconds(3);

    readonly CancellationTokenSource _cts = new();
    readonly TcpListener _listener;
    readonly Func<RemoteRequest, CancellationToken, Task<RemoteAnswer>> _ask;
    readonly IRemoteInput _input;
    readonly Dictionary<IPAddress, DateTime> _declined = [];
    readonly ConnectionGate _gate = new();
    DateTime _askAgainAt; // guarded by _declined. So nobody can keep questions flashing up, from any number of addresses.
    int _busy;
    volatile CancellationTokenSource? _session;

    /// The name of who's viewing now, or null when they've gone. Any thread.
    public event Action<string?>? ViewerChanged;

    public RemoteHost(Func<RemoteRequest, CancellationToken, Task<RemoteAnswer>> ask, IRemoteInput? input = null)
    {
        _ask = ask;
        _input = input ?? new WindowsRemoteInput();
        _listener = new TcpListener(IPAddress.Any, Net.RemotePort);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// The person here ended it.
    public void EndSession() => _session?.Cancel();

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(tcp));
        }
    }

    async Task Serve(TcpClient tcp)
    {
        using var _ = tcp;
        using var conn = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        using var ticket = _gate.Enter(tcp);
        if (ticket == null) return;
        bool claimed = false;
        SecureChannel? channel = null;
        try
        {
            conn.CancelAfter(HandshakeTimeout);
            Net.Tune(tcp);
            var stream = tcp.GetStream();
            var first = await Net.ReceiveAsync(stream, conn.Token);
            if (first.Type == MsgType.RemoteProbe)
            {
                await Net.SendAsync(stream, Msg.WithPayload(MsgType.RemoteInfo, Encoding.UTF8.GetBytes(Environment.MachineName)), conn.Token);
                return;
            }
            if (first.Type != MsgType.Hello || first.Payload?.Length != SHA256.HashSizeInBytes) return;

            using var ecdh = SecureChannel.NewKey();
            var sharerPublic = ecdh.ExportSubjectPublicKeyInfo();
            await Net.SendAsync(stream, Msg.WithPayload(MsgType.KeyShare, sharerPublic), conn.Token);
            var share = await Net.ReceiveAsync(stream, conn.Token);
            if (share.Type != MsgType.KeyShare || share.Payload == null) return;
            var viewerPublic = share.Payload;
            if (!SHA256.HashData(viewerPublic).AsSpan().SequenceEqual(first.Payload)) return;

            bool usingKey = first.C != 0;
            var secret = !usingKey ? RemoteHandshake.NoKey
                : RemoteStore.TrustedById(first.C)?.Key ?? RandomNumberGenerator.GetBytes(RemoteStore.KeySize);
            channel = SecureChannel.Create(stream, ecdh, viewerPublic, sharerPublic, secret, isHost: true);
            channel.FrameLimit = SecureChannel.HandshakeFrame;

            Msg auth;
            try { auth = await channel.ReceiveAsync(conn.Token); }
            catch (CryptographicException) { auth = default; }
            if (auth.Type != MsgType.Auth)
            {
                await Net.SendAsync(stream, new Msg(MsgType.Rejected), conn.Token);
                return;
            }
            await Net.SendAsync(stream, new Msg(MsgType.Welcome), conn.Token);
            ticket.Dispose();

            var name = Net.CleanName(auth.PayloadText, "?");
            var ip = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            claimed = Interlocked.Exchange(ref _busy, 1) == 0;
            if (!claimed)
            {
                await channel.SendAsync(new Msg(MsgType.RemoteDenied, (int)RemoteRefusal.Busy), conn.Token);
                return;
            }

            Task<Msg>? read = null;
            if (!usingKey)
            {
                if (RecentlyDeclined(ip))
                {
                    await channel.SendAsync(new Msg(MsgType.RemoteDenied, (int)RemoteRefusal.Declined), conn.Token);
                    return;
                }
                await channel.SendAsync(new Msg(MsgType.RemoteWaiting), conn.Token);
                conn.CancelAfter(Timeout.InfiniteTimeSpan);

                // Nothing is expected from the viewer while the person decides, so anything arriving,
                // normally the connection closing, means it gave up.
                read = channel.ReceiveAsync(conn.Token);
                var answer = await AskPerson(new RemoteRequest(name, auth.A == 1, RemoteHandshake.Code(viewerPublic, sharerPublic), ip), read, conn.Token);
                lock (_declined) _askAgainAt = DateTime.UtcNow + AskGap;
                if (answer == null) return;
                if (!answer.Allow)
                {
                    lock (_declined)
                    {
                        foreach (var old in _declined.Where(d => d.Value <= DateTime.UtcNow).Select(d => d.Key).ToList()) _declined.Remove(old);
                        _declined[ip] = DateTime.UtcNow + DeclinedPause;
                    }
                    await channel.SendAsync(new Msg(MsgType.RemoteDenied, (int)RemoteRefusal.Declined), conn.Token);
                    return;
                }
                if (answer.Trust)
                {
                    var trusted = RemoteStore.Trust(name);
                    await channel.SendAsync(new Msg(MsgType.RemoteTrust, trusted.Id) { Payload = trusted.Key }, conn.Token);
                }
            }
            conn.CancelAfter(Timeout.InfiniteTimeSpan);

            await channel.SendAsync(new Msg(MsgType.RemoteAccepted, 0, Screen.AllScreens.Length) { Payload = Encoding.UTF8.GetBytes(Environment.MachineName) }, conn.Token);
            await RunSession(tcp, channel, name, conn, read);
        }
        catch { }
        finally
        {
            conn.Cancel();
            channel?.Dispose();
            if (claimed) Interlocked.Exchange(ref _busy, 0);
        }
    }

    bool RecentlyDeclined(IPAddress ip)
    {
        lock (_declined) return _askAgainAt > DateTime.UtcNow || (_declined.TryGetValue(ip, out var until) && until > DateTime.UtcNow);
    }

    /// Null if the viewer gave up or nobody answered in time, which counts as no.
    async Task<RemoteAnswer?> AskPerson(RemoteRequest request, Task<Msg> viewerLeft, CancellationToken ct)
    {
        using var asking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        asking.CancelAfter(AskTimeout);
        var answer = _ask(request, asking.Token);
        try
        {
            if (await Task.WhenAny(answer, viewerLeft) == viewerLeft) return null;
            return await answer;
        }
        catch (OperationCanceledException) { return null; }
        finally
        {
            asking.Cancel(); // takes the question off the screen if it's still there
            _ = answer.ContinueWith(t => t.Exception, TaskScheduler.Default);
        }
    }

    // ───────────────────────── while being viewed ─────────────────────────

    /// What the viewer asked for and how far it has got, shared with the thread sending the picture.
    sealed class ViewState
    {
        public volatile int Monitor, ViewSide = DefaultViewSide, LastAckMs;
        public Rectangle Bounds; // guarded by Lock
        public int Sent, Acked;  // guarded by Lock
        public readonly object Lock = new();
        public readonly SemaphoreSlim Room = new(MaxInFlight, MaxInFlight);
        public readonly long[] SentAt = new long[MaxInFlight * 4];
        public readonly Stopwatch Clock = Stopwatch.StartNew();
    }

    async Task RunSession(TcpClient tcp, SecureChannel channel, string name, CancellationTokenSource conn, Task<Msg>? read)
    {
        var state = new ViewState();
        var sender = new Thread(() => StreamLoop(channel, state, conn.Token)) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Aura Mouse remote screen" };
        _session = conn;
        sender.Start();
        ViewerChanged?.Invoke(name);
        try
        {
            while (true)
            {
                Handle(await (read ?? channel.ReceiveAsync(conn.Token)), state);
                read = null;
            }
        }
        finally
        {
            conn.Cancel();
            tcp.Dispose(); // lets go of a send that's stuck on a dead connection
            sender.Join(TimeSpan.FromSeconds(2));
            _input.ReleaseAll();
            _session = null;
            ViewerChanged?.Invoke(null);
        }
    }

    void Handle(Msg m, ViewState state)
    {
        switch (m.Type)
        {
            case MsgType.RemoteAck:
                lock (state.Lock)
                {
                    if (m.A <= state.Acked || m.A > state.Sent) return; // a viewer can't buy itself more room
                    int released = m.A - state.Acked;
                    state.Acked = m.A;
                    state.LastAckMs = (int)(state.Clock.ElapsedMilliseconds - state.SentAt[m.A % state.SentAt.Length]);
                    state.Room.Release(released);
                }
                break;
            case MsgType.RemotePointer:
                Rectangle b;
                lock (state.Lock) b = state.Bounds;
                if (b.IsEmpty) return;
                _input.MoveTo(b.X + (int)((long)Math.Clamp(m.A, 0, 65535) * (b.Width - 1) / 65535), b.Y + (int)((long)Math.Clamp(m.B, 0, 65535) * (b.Height - 1) / 65535));
                break;
            case MsgType.Button or MsgType.Wheel or MsgType.Key or MsgType.TextInput:
                _input.Play(m);
                break;
            case MsgType.RemoteMonitor:
                state.Monitor = Math.Max(0, m.A);
                break;
            case MsgType.RemoteViewSize:
                state.ViewSide = Math.Clamp(m.A, MinViewSide, MaxViewSide);
                break;
        }
    }

    /// Never more than a couple of pictures on their way: on a slow network it skips pictures instead of
    /// falling behind, so what's shown is always close to now. It also lowers quality when replies come back slowly.
    unsafe void StreamLoop(SecureChannel channel, ViewState state, CancellationToken ct)
    {
        ScreenGrabber? grabber = null;
        FrameEncoder? encoder = null;
        try
        {
            int quality = HighestQuality, idleFrames = 0, width = 0, height = 0;
            var last = Rectangle.Empty;
            while (!ct.IsCancellationRequested)
            {
                long started = state.Clock.ElapsedMilliseconds;
                state.Room.Wait(ct);

                var screens = Screen.AllScreens;
                var bounds = screens[Math.Min(state.Monitor, screens.Length - 1)].Bounds;
                float scale = Math.Min(1f, (float)state.ViewSide / Math.Max(bounds.Width, bounds.Height));
                int w = (int)(bounds.Width * scale) & ~1, h = (int)(bounds.Height * scale) & ~1;
                if (bounds != last || w != width || h != height)
                {
                    grabber?.Dispose();
                    grabber = new ScreenGrabber(w, h);
                    encoder = new FrameEncoder(w, h);
                    (last, width, height) = (bounds, w, h);
                    lock (state.Lock) state.Bounds = bounds;
                }

                grabber!.Grab(bounds);
                var update = encoder!.Encode(grabber.Bits, quality, quality > LowestQuality ? SharpQuality : 0);
                if (update == null)
                {
                    idleFrames++;
                    state.Room.Release();
                }
                else
                {
                    idleFrames = 0;
                    int id;
                    lock (state.Lock)
                    {
                        id = ++state.Sent;
                        state.SentAt[id % state.SentAt.Length] = state.Clock.ElapsedMilliseconds;
                    }
                    channel.Send(new Msg(MsgType.RemoteFrame, id, w, h) { Payload = update });

                    int ack = state.LastAckMs;
                    if (ack > SlowAckMs) quality = Math.Max(LowestQuality, quality - 5);
                    else if (ack < FastAckMs && id % 15 == 0) quality = Math.Min(HighestQuality, quality + 5);
                }

                int wait = (idleFrames > IdleAfterFrames ? IdleFrameMs : FrameMs) - (int)(state.Clock.ElapsedMilliseconds - started);
                if (wait > 0 && ct.WaitHandle.WaitOne(wait)) break;
            }
        }
        catch { } // the connection went; the reading side tidies up
        finally
        {
            grabber?.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
