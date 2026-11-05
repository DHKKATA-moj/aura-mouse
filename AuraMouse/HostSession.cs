using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AuraMouse.Transfers;
using AuraMouse.Ui;
using static AuraMouse.Native;

namespace AuraMouse;

/// Active: the cursor is on this computer right now. Driving: this computer's own mouse is in control.
internal readonly record struct PeerInfo(int Id, string Name, Edge Edge, bool Active, bool Driving, bool IsPhone);

/// The computer the others connect to. Each connected computer sits on one of its screen edges.
/// Control works both ways: this computer's mouse can go to any of them, and any of them can come
/// over here with its own mouse and keyboard and carry on through to the others. Whichever mouse
/// or keyboard is touched last is in control.
internal sealed class HostSession : IDisposable
{
    const int VK_ESCAPE = 0x1B;

    sealed class Peer(int id, string name, SecureChannel secure)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public SecureChannel Secure { get; } = secure;
        public Channel<Msg> Outbox { get; } = Channel.CreateUnbounded<Msg>(new() { SingleReader = true });
        public volatile Edge Edge = Edge.None;
        public CancellationTokenSource? Connection;
        public bool IsPhone { get; init; } // no screen to cross over to: it only drives us as a touchpad
        public byte[] FileKey { get; } = RandomNumberGenerator.GetBytes(32);
    }

    /// Files offered to a client, waiting for it to connect and fetch them (again, after a break).
    sealed record PendingSend(int PeerId, FileSet Set, TransferItem Item);

    /// Files a client is dragging over our screen, not dropped yet.
    sealed record Carry(Peer From, DragInfo Info);

    readonly CancellationTokenSource _cts = new();
    readonly TcpListener _listener = new(IPAddress.Any, Net.TcpPort);
    readonly TcpListener _fileListener = new(IPAddress.Any, Net.FilePort);
    readonly ConcurrentDictionary<string, PendingSend> _outgoing = new();
    readonly ConcurrentDictionary<string, (int PeerId, FileSet Set)> _dragOffers = new(); // dragged to a client, not dropped yet
    readonly ConcurrentDictionary<string, string> _dropFolders = new(); // where files dragged to us were dropped
    DragOutProbe? _probe;
    SharedClipboard? _clipboard;
    volatile Peer? _clipboardFiles; // the computer whose copied files our clipboard holds
    volatile Carry? _carry;
    readonly InputCapture _capture = new();
    readonly InputPlayer _player = new();
    readonly object _lock = new();
    readonly Dictionary<string, Edge> _layout = LayoutStore.Load(); // guarded by _lock
    volatile Peer[] _peers = []; // replaced, never mutated, so the hook can read it without locking

    // Who is in control (guarded by _lock when changed together):
    volatile Peer? _active; // our own mouse is controlling this computer
    volatile Peer? _driver; // this computer's mouse is controlling us...
    volatile Peer? _relay;  // ...and the cursor has moved on from our screen to this computer

    volatile bool _locked; // the user's shortcut: the mouse stays on the screen it's on, on every computer

    SynchronizationContext? _ui;
    int _nextId;

    // A guess at the code is one attempt on this computer, whoever makes it. After a handful it stops accepting
    // the code until the person asks for a new one, so guessing can't get anywhere however long it goes on.
    const int MaxCodeAttempts = 5;
    readonly object _codeLock = new();
    readonly ConnectionGate _gate = new();
    int _codeAttempts;

    public int PairingCode { get; private set; } = NewCode();
    public event Action<SessionStatus>? StatusChanged;
    public event Action<int>? PairingCodeChanged;
    /// Raised (from any thread) when computers connect, leave, move edge, or the cursor/control moves.
    public event Action? PeersChanged;
    /// The mouse was locked to its screen or let go, from any computer's shortcut. Any thread.
    public event Action<bool>? LockChanged;

    public bool Locked => _locked;

    static int NewCode() => RandomNumberGenerator.GetInt32(100_000, 1_000_000);

    /// Too many wrong tries: the code doesn't work until RenewCode.
    public bool CodeLocked { get { lock (_codeLock) return _codeAttempts >= MaxCodeAttempts; } }

    /// A fresh code, after it was locked or used.
    public void RenewCode()
    {
        int code;
        lock (_codeLock)
        {
            _codeAttempts = 0;
            PairingCode = code = NewCode();
        }
        PairingCodeChanged?.Invoke(code);
    }

    /// Counted before the guess is looked at, so many guesses at the same moment can't slip past the limit.
    int? TakeCodeAttempt()
    {
        lock (_codeLock)
        {
            if (_codeAttempts >= MaxCodeAttempts) return null;
            _codeAttempts++;
            return PairingCode;
        }
    }

    public IReadOnlyList<PeerInfo> Peers
    {
        get
        {
            Peer? active = _active, relay = _relay, driver = _driver;
            return _peers.Select(p => new PeerInfo(p.Id, p.Name, p.Edge, p == active || p == relay, p == driver, p.IsPhone)).ToList();
        }
    }

    /// Call on the UI thread: clipboard work is posted back to it.
    public void Start()
    {
        _ui = SynchronizationContext.Current;
        _probe = new DragOutProbe(_ui);
        _clipboard = new SharedClipboard();
        _clipboard.Copied += m =>
        {
            _clipboardFiles = null;
            foreach (var p in _peers) p.Outbox.Writer.TryWrite(m);
        };
        _clipboard.PasteFiles += PasteFiles;
        _listener.Start();
        _fileListener.Start();
        _capture.LocalMouse = OnLocalMouse;
        _capture.LocalKey = vk =>
        {
            if (_driver != null) TakeBackFromDriver();
            _clipboard.KeyDown(vk);
        };
        _capture.Escape = () => { if (_active is { } a) { Post(a, MsgType.Release); ComeBack(a, 5000); } };
        _capture.LockShortcut = ToggleLock;
        _capture.Install();
        // Not on the window's thread: started there, every await would come back to it, and any busy
        // moment (a slow clipboard, a redraw) would hold up all the mouse movement passing through here.
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _ = Task.Run(() => FileAcceptLoop(_cts.Token));
        _ = Task.Run(() => BeaconLoop(_cts.Token));
        StatusChanged?.Invoke(SessionStatus.Waiting);
    }

    // ───────────────────────── layout ─────────────────────────

    /// Puts a computer on an edge; whoever was there takes its old place.
    public void MovePeer(int id, Edge edge)
    {
        if (edge == Edge.None) return;
        Peer? peer, occupant;
        lock (_lock)
        {
            peer = _peers.FirstOrDefault(p => p.Id == id);
            if (peer == null || peer.Edge == edge) return;
            occupant = _peers.FirstOrDefault(p => p.Edge == edge);
            if (occupant != null) { occupant.Edge = peer.Edge; Remember(occupant); }
            peer.Edge = edge;
            Remember(peer);
        }
        SendEdge(peer);
        if (occupant != null) SendEdge(occupant);
        SaveLayout();
        PeersChanged?.Invoke();
    }

    void Remember(Peer peer)
    {
        if (peer.Edge == Edge.None) _layout.Remove(peer.Name);
        else _layout[peer.Name] = peer.Edge;
    }

    /// Each client needs its edge so it knows which of its own edges leads here.
    static void SendEdge(Peer peer) => Post(peer, MsgType.YourEdge, (int)peer.Edge);

    /// Writes the layout on a worker thread, since this can be called from inside the mouse hook.
    void SaveLayout()
    {
        Dictionary<string, Edge> snapshot;
        lock (_lock) snapshot = new(_layout, StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() => LayoutStore.Save(snapshot));
    }

    Peer? PeerAtEdge(Edge edge) => _peers.FirstOrDefault(p => p.Edge == edge);

    /// The computer on this edge. If there is none, the first computer that has no place yet is
    /// assumed to be on the side the user is heading for, and learns that edge.
    Peer? PeerFor(Edge edge)
    {
        var peers = _peers;
        foreach (var p in peers)
            if (p.Edge == edge) return p;

        // Top and bottom are only learned once left and right are taken: people fling the mouse
        // to the top for tabs and to the bottom for the taskbar all the time.
        if (!edge.IsVertical() && peers.Count(p => p.Edge.IsVertical()) < 2) return null;

        Peer? learner;
        lock (_lock)
        {
            learner = _peers.FirstOrDefault(p => p.Edge == Edge.None && !p.IsPhone);
            if (learner == null || _peers.Any(p => p.Edge == edge)) return null;
            learner.Edge = edge;
            Remember(learner);
        }
        SendEdge(learner);
        SaveLayout();
        return learner;
    }

    // ───────────────────────── connections ─────────────────────────

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Serve(tcp, ct);
        }
    }

    async Task Serve(TcpClient tcp, CancellationToken ct)
    {
        using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var ticket = _gate.Enter(tcp);
        if (ticket == null)
        {
            tcp.Dispose();
            return;
        }
        SecureChannel? channel = null;
        Peer? peer = null;
        bool guessing = false;
        try
        {
            conn.CancelAfter(Net.HandshakeTimeout);
            Net.Tune(tcp);
            var stream = tcp.GetStream();
            var hello = await Net.ReceiveAsync(stream, conn.Token);
            if (hello.Type != MsgType.Hello || hello.Payload == null) return;

            using var ecdh = SecureChannel.NewKey();
            var hostPublic = ecdh.ExportSubjectPublicKeyInfo();
            bool usingKey = hello.C != 0;
            byte[] secret;
            if (usingKey)
            {
                // A computer paired before uses its saved key; one we don't know gets a key nobody can match.
                secret = PairStore.ClientById(hello.C)?.Key ?? RandomNumberGenerator.GetBytes(PairStore.KeySize);
                await Net.SendAsync(stream, Msg.WithPayload(MsgType.KeyShare, hostPublic), conn.Token);
            }
            else
            {
                var share = await Net.ReceiveAsync(stream, conn.Token);
                if (share.Type != MsgType.PakeShare || share.Payload == null) return;
                if (TakeCodeAttempt() is not { } code)
                {
                    await Net.SendAsync(stream, new Msg(MsgType.Rejected), conn.Token);
                    return;
                }
                guessing = true;
                var pake = new Pake(code, isClient: false);
                await Net.SendAsync(stream, Msg.WithPayload(MsgType.KeyShare, hostPublic), conn.Token);
                await Net.SendAsync(stream, Msg.WithPayload(MsgType.PakeShare, pake.Share), conn.Token);
                secret = pake.Finish(share.Payload, hello.Payload, hostPublic);
            }
            channel = SecureChannel.Create(stream, ecdh, hello.Payload, hostPublic, secret, isHost: true);
            channel.FrameLimit = SecureChannel.HandshakeFrame;

            // With the wrong code the client derived different keys, so its Auth won't decrypt.
            Msg auth;
            try { auth = await channel.ReceiveAsync(conn.Token); }
            catch (CryptographicException) { auth = default; }
            if (auth.Type != MsgType.Auth)
            {
                await Net.SendAsync(stream, new Msg(MsgType.Rejected), conn.Token);
                return;
            }

            await Net.SendAsync(stream, new Msg(MsgType.Welcome), conn.Token);
            await channel.SendAsync(new Msg(MsgType.Welcome), conn.Token);
            conn.CancelAfter(Timeout.InfiniteTimeSpan);
            channel.FrameLimit = SecureChannel.MaxFrame;
            ticket.Dispose();
            if (guessing)
            {
                guessing = false;
                RenewCode(); // a code only ever works once
            }

            peer = new Peer(Interlocked.Increment(ref _nextId), Net.CleanName(auth.PayloadText, "PC"), channel) { Connection = conn, IsPhone = auth.A == 1 };
            AddPeer(peer);
            peer.Outbox.Writer.TryWrite(new Msg(MsgType.FileKey, peer.Id) { Payload = peer.FileKey });
            if (usingKey) PairStore.BecomeHost();
            else
            {
                // Paired with the code: from now on this computer connects with a key of its own.
                var pair = PairStore.AddClient(peer.Name);
                byte[] payload = [.. pair.Key, .. Encoding.UTF8.GetBytes(Environment.MachineName)];
                peer.Outbox.Writer.TryWrite(new Msg(MsgType.PairKey, pair.Id) { Payload = payload });
            }
            SendEdge(peer);
            if (_locked) Post(peer, MsgType.Lock, 1);
            if (peer.IsPhone) SendWakeList(peer);
            else peer.Outbox.Writer.TryWrite(Wake.InfoMessage());
            StatusChanged?.Invoke(SessionStatus.Connected);
            PeersChanged?.Invoke();
            _ = SendLoop(peer, conn.Token);

            while (true)
            {
                var m = await channel.ReceiveAsync(conn.Token);
                switch (m.Type)
                {
                    case MsgType.Leave: OnLeave(peer, m.B); break;
                    case MsgType.ClientEnter: OnClientEnter(peer, m.B); break;
                    case MsgType.TakeOver: OnTakeOver(peer); break;
                    case MsgType.DragStart: OnDragStart(peer, m); break;
                    case MsgType.Move or MsgType.Button or MsgType.Wheel or MsgType.Key or MsgType.TextInput: OnDriverInput(peer, m); break;
                    case MsgType.Clipboard: OnClipboard(peer, m); break;
                    case MsgType.Lock: SetLock(m.A == 1); break;
                    case MsgType.WakeInfo:
                        Wake.Accept(m.Payload, peer.Name, (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address);
                        foreach (var p in _peers) if (p.IsPhone) SendWakeList(p);
                        break;
                }
            }
        }
        catch { }
        finally
        {
            conn.Cancel();
            tcp.Dispose();
            channel?.Dispose();
            if (guessing) WrongCode();
            if (peer != null)
            {
                lock (_lock) _peers = _peers.Where(p => p != peer).ToArray();
                OnTakeOver(peer); // whatever it was doing with the cursor, that's over
                foreach (var (id, pending) in _outgoing)
                    if (pending.PeerId == peer.Id) _outgoing.TryRemove(id, out _);
                TransferCenter.FailWaiting(peer.Id);
                if (!ct.IsCancellationRequested)
                {
                    StatusChanged?.Invoke(SessionStatus.Disconnected);
                    PeersChanged?.Invoke();
                }
            }
        }
    }

    static void SendWakeList(Peer phone)
    {
        foreach (var m in Wake.PhoneMessages()) phone.Outbox.Writer.TryWrite(m);
    }

    void AddPeer(Peer peer)
    {
        Peer[] replaced;
        lock (_lock)
        {
            // The same computer back again: its old connection is dead, even if we hadn't noticed yet.
            replaced = _peers.Where(p => p.Name.Equals(peer.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            _peers = _peers.Except(replaced).ToArray();

            // Back to the edge it had last time, if that edge is still free.
            if (!peer.IsPhone && _layout.TryGetValue(peer.Name, out var edge) && _peers.All(p => p.Edge != edge))
                peer.Edge = edge;
            _peers = [.. _peers, peer];
        }
        foreach (var old in replaced) old.Connection?.Cancel(); // can run its cleanup right here, so not under the lock
    }

    static async Task SendLoop(Peer peer, CancellationToken ct)
    {
        try
        {
            await foreach (var m in peer.Outbox.Reader.ReadAllAsync(ct))
                await peer.Secure.SendAsync(m, ct);
        }
        catch { }
    }

    static void Post(Peer peer, MsgType type, int a = 0, int b = 0, int c = 0) =>
        peer.Outbox.Writer.TryWrite(new Msg(type, a, b, c));

    void WrongCode() => StatusChanged?.Invoke(CodeLocked ? SessionStatus.CodeLocked : SessionStatus.WrongCodeAttempt);

    /// Announces this computer on the LAN so the others can find it without typing an IP.
    /// Sent on every network: a plain 255.255.255.255 broadcast leaves through one adapter only,
    /// often a virtual one (Hyper-V, WSL, VPN) that the other computer isn't on.
    async Task BeaconLoop(CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(Net.Beacon + Environment.MachineName);
        while (!ct.IsCancellationRequested)
        {
            foreach (var network in LanScan.LocalNetworks())
            {
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(network.Address, 0)) { EnableBroadcast = true };
                    await udp.SendAsync(payload, new IPEndPoint(LanScan.Broadcast(network.Address, network.Mask), Net.DiscoveryPort), ct);
                }
                catch (SocketException) { }
                catch (OperationCanceledException) { return; }
            }
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ───────────────────────── clipboard ─────────────────────────

    /// A client copied something: it's ours too, and every other client's. Copied files can only be
    /// fetched from the computer they're on, so those stay between that client and us.
    void OnClipboard(Peer from, Msg m) => _ui?.Post(_ =>
    {
        _clipboard?.Apply(m);
        _clipboardFiles = m.A == (int)ClipKind.Files ? from : null;
        if (m.A == (int)ClipKind.Files) return;
        foreach (var p in _peers)
            if (p != from) p.Outbox.Writer.TryWrite(m);
    }, null);

    /// Files a client copied were pasted here: they're fetched into that folder, as if dropped there.
    void PasteFiles(DragInfo info, string folder)
    {
        if (_clipboardFiles is not { } from || !_peers.Contains(from)) return;
        _dropFolders[info.Id] = folder;
        from.Outbox.Writer.TryWrite(new Msg(MsgType.DropAt) { Payload = Encoding.UTF8.GetBytes(info.Id) });
    }

    /// Cuts off a computer that was just removed from the saved list.
    public void Disconnect(string name)
    {
        foreach (var p in _peers.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            p.Connection?.Cancel();
    }

    public void ToggleLock() => SetLock(!_locked);

    /// Every computer follows, because the cursor can be on any of them when it's pressed.
    void SetLock(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        foreach (var p in _peers) Post(p, MsgType.Lock, locked ? 1 : 0);
        LockChanged?.Invoke(locked);
    }

    // ───────────────────────── our mouse controls another computer ─────────────────────────

    bool OnLocalMouse(int msg, POINT pt)
    {
        _probe?.Track(msg, pt);
        if (_driver != null)
        {
            // Our own mouse was touched while another computer's mouse was in control: ours wins.
            if (msg != WM_MOUSEMOVE || !_player.IsOwnMove(pt)) TakeBackFromDriver();
            return false;
        }

        if (msg != WM_MOUSEMOVE || _peers.Length == 0 || _locked) return false;
        var edge = InputCapture.EdgeAt(pt);
        if (edge == Edge.None || PeerFor(edge) is not { } target) return false;

        if (_capture.LeftButtonDown)
        {
            // Button held at the edge: cross only if it's files being dragged, so dragging a window
            // against the edge to snap it still works.
            _probe?.Probe(pt, paths => DragFilesTo(target, edge, pt, paths));
            return false;
        }

        GoRemote(target, edge, pt);
        return true;
    }

    /// Files dragged against the edge: the cursor goes over carrying them, and they're sent once dropped there.
    async void DragFilesTo(Peer peer, Edge edge, POINT pt, string[] paths)
    {
        if (_active != null || _driver != null || !_peers.Contains(peer)) return;
        GoRemote(peer, edge, pt);
        try
        {
            var set = await Task.Run(() => FileSet.FromPaths(paths));
            if (set.Files.Count == 0) return;
            _dragOffers[set.Id] = (peer.Id, set);
            var info = new DragInfo(set.Id, set.Title, set.Files.Count, set.TotalBytes);
            peer.Outbox.Writer.TryWrite(new Msg(MsgType.DragStart) { Payload = TransferJson.Bytes(info) });
        }
        catch (Exception) { } // unreadable files: it simply becomes a normal crossing
    }

    void GoRemote(Peer peer, Edge edge, POINT pt)
    {
        Post(peer, MsgType.Enter, (int)edge.Opposite(), Net.RatioAlong(edge, pt.X, pt.Y, SystemInformation.VirtualScreen));
        _active = peer;
        _capture.Begin(m => peer.Outbox.Writer.TryWrite(m));
        PeersChanged?.Invoke();
    }

    void ComeBack(Peer peer, int ratio)
    {
        _capture.End();
        _active = null;
        var at = Net.PointOnEdge(peer.Edge, ratio, SystemInformation.VirtualScreen, inset: 2);
        SetCursorPos(at.X, at.Y);
        PeersChanged?.Invoke();
    }

    // ───────────────────────── another computer's mouse controls us ─────────────────────────

    /// A client's cursor crossed its edge into our screen: from now on its mouse and keyboard drive us.
    void OnClientEnter(Peer peer, int ratio)
    {
        if (peer.Edge == Edge.None && !peer.IsPhone) { Post(peer, MsgType.StopDriving); return; }
        CancelCarry();

        Peer? oldActive, oldDriver, oldRelay;
        lock (_lock)
        {
            (oldActive, oldDriver, oldRelay) = (_active, _driver, _relay);
            (_active, _driver, _relay) = (null, peer, null);
        }
        if (oldActive != null) { _capture.End(); Post(oldActive, MsgType.Release); }
        if (oldDriver != null && oldDriver != peer) Post(oldDriver, MsgType.StopDriving);
        if (oldRelay != null) { Post(oldRelay, MsgType.Release); CursorHider.Show(); }

        if (peer.IsPhone) _player.SyncToCursor();
        else _player.PlaceAt(peer.Edge, ratio, inset: 2);
        PeersChanged?.Invoke();
    }

    void OnDriverInput(Peer peer, Msg m)
    {
        if (_driver != peer) return;

        // Carrying files the driver dragged over: letting go of the button drops them here.
        if (_carry is { } carry && _relay == null)
        {
            if (m.Type == MsgType.Button && m.A == MOUSEEVENTF_LEFTUP) { DropCarried(carry); return; }
            if (m.Type == MsgType.Key && m.A == VK_ESCAPE && (m.C & KEYEVENTF_KEYUP) == 0) CancelCarry();
        }

        // The cursor is on another client: pass the input straight through.
        if (_relay is { } relay)
        {
            relay.Outbox.Writer.TryWrite(m);
            return;
        }

        if (m.Type != MsgType.Move)
        {
            InputPlayer.Play(m);
            if (m.Type == MsgType.Key && (m.C & KEYEVENTF_KEYUP) == 0) _clipboard?.KeyDown(m.A);
            return;
        }

        var exit = _player.Move(m.A, m.B, edge => !_locked && PeerAtEdge(edge) != null, out int ratio);
        if (exit != Edge.None && PeerAtEdge(exit) is { } target) CrossFromDriven(peer, target, exit, ratio);
    }

    /// The driven cursor left our screen: either back home to the driver, or on to another client.
    void CrossFromDriven(Peer driver, Peer target, Edge exit, int ratio)
    {
        CancelCarry(); // carried files can only be dropped on this computer
        InputPlayer.ReleaseModifiers(); // keys the driver was holding down here
        if (target == driver)
        {
            lock (_lock) if (_driver == driver) _driver = null;
            Post(driver, MsgType.Return, 0, ratio);
        }
        else
        {
            Post(target, MsgType.Enter, (int)exit.Opposite(), ratio);
            _relay = target;
            CursorHider.Hide(); // our cursor waits at the edge; only the one on the target should show
        }
        PeersChanged?.Invoke();
    }

    void OnLeave(Peer peer, int ratio)
    {
        if (_active == peer) ComeBack(peer, ratio);
        else if (_relay == peer)
        {
            // Back on our screen, still driven by the same client.
            _relay = null;
            CursorHider.Show();
            _player.PlaceAt(peer.Edge, ratio, inset: 2);
            PeersChanged?.Invoke();
        }
    }

    void OnDragStart(Peer peer, Msg m)
    {
        if (_driver != peer || _relay != null) return;
        var info = TransferJson.Read<DragInfo>(m.Payload);
        if (!FileReceiver.IsValidId(info.Id)) return;
        _carry = new Carry(peer, info);
        _ui?.Post(_ => DragUi.ShowGhost(info), null);
    }

    void DropCarried(Carry carry)
    {
        _carry = null;
        _ui?.Post(_ =>
        {
            DragUi.HideGhost();
            _dropFolders[carry.Info.Id] = DropFolder.At(Cursor.Position);
            carry.From.Outbox.Writer.TryWrite(new Msg(MsgType.DropAt) { Payload = Encoding.UTF8.GetBytes(carry.Info.Id) });
        }, null);
    }

    void CancelCarry()
    {
        if (_carry == null) return;
        _carry = null;
        _ui?.Post(_ => DragUi.HideGhost(), null);
    }

    /// A client's own mouse or keyboard was touched (or it disconnected): it is local again.
    void OnTakeOver(Peer peer)
    {
        if (_carry?.From == peer) CancelCarry();
        if (_driver == peer)
        {
            Peer? relay;
            lock (_lock) { relay = _relay; _driver = _relay = null; }
            if (relay != null) { Post(relay, MsgType.Release); CursorHider.Show(); }
            else InputPlayer.ReleaseModifiers();
        }
        else if (_active == peer) ComeBack(peer, 5000);
        else if (_relay == peer)
        {
            _relay = null;
            CursorHider.Show();
            _player.PlaceAt(peer.Edge, 5000, inset: 2);
        }
        PeersChanged?.Invoke();
    }

    void TakeBackFromDriver()
    {
        CancelCarry();
        Peer? driver, relay;
        lock (_lock)
        {
            (driver, relay) = (_driver, _relay);
            _driver = _relay = null;
        }
        if (driver != null) Post(driver, MsgType.StopDriving);
        if (relay != null) { Post(relay, MsgType.Release); CursorHider.Show(); }
        InputPlayer.ReleaseModifiers();
        PeersChanged?.Invoke();
    }

    // ───────────────────────── files ─────────────────────────

    /// Offers files to a computer. It connects back to fetch them, and again after any break, resuming.
    public async Task SendFilesAsync(int peerId, IReadOnlyList<string> paths)
    {
        var peer = _peers.FirstOrDefault(p => p.Id == peerId);
        if (peer == null) return;

        var set = await Task.Run(() => FileSet.FromPaths(paths));
        if (set.Files.Count == 0) return;

        var item = TransferCenter.GetOrAdd(set.Id, outgoing: true, peer.Name, peer.Id);
        if (!item.IsActive) return; // just cancelled
        item.Title ??= set.Title;
        item.FileCount = set.Files.Count;
        item.TotalBytes = set.TotalBytes;
        _outgoing[set.Id] = new PendingSend(peer.Id, set, item);
        peer.Outbox.Writer.TryWrite(new Msg(MsgType.FileReady) { Payload = Encoding.UTF8.GetBytes(set.Id) });
    }

    async Task FileAcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _fileListener.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = ServeFile(tcp, ct);
        }
    }

    async Task ServeFile(TcpClient tcp, CancellationToken ct)
    {
        FileLink? link = null;
        using var ticket = _gate.Enter(tcp);
        if (ticket == null)
        {
            tcp.Dispose();
            return;
        }
        try
        {
            var accepted = await FileLink.AcceptAsync(tcp, id => _peers.FirstOrDefault(p => p.Id == id)?.FileKey, ct);
            if (accepted is not var (fileLink, peerId)) { tcp.Dispose(); return; }
            link = fileLink;
            ticket.Dispose();
            var peer = _peers.FirstOrDefault(p => p.Id == peerId);
            if (peer == null) return;

            var start = await link.Channel.ReceiveAsync(ct);
            var id = start.PayloadText;
            if (start.Type != MsgType.FileStart || !FileReceiver.IsValidId(id)) return;

            if (start.A == 0)
            {
                // The client is sending to us: into the folder they were dropped on, if they were dragged over.
                var root = _dropFolders.TryGetValue(id, out var dropped) ? dropped : FileReceiver.DefaultFolder;
                var item = TransferCenter.GetOrAdd(id, outgoing: false, peer.Name, peer.Id);
                var channel = link.Channel;
                if (await TransferAttempt.RunAsync(item, channel, c => FileReceiver.RunAsync(channel, item, root, c), root, ct))
                    _dropFolders.TryRemove(id, out _);
            }
            else
            {
                // The client is fetching files we offered, dragged over and it has now dropped, or copied
                // here and it has now pasted.
                FileSet? offered = null;
                if (_dragOffers.TryRemove(id, out var offer) && offer.PeerId == peer.Id) offered = offer.Set;
                else if (_clipboard?.LocalFiles is { } copied && copied.Id == id) offered = copied;
                if (offered != null && (!_outgoing.TryGetValue(id, out var existing) || !existing.Item.IsActive))
                {
                    var item = TransferCenter.GetOrAdd(id, outgoing: true, peer.Name, peer.Id);
                    item.Title ??= offered.Title;
                    item.FileCount = offered.Files.Count;
                    item.TotalBytes = offered.TotalBytes;
                    _outgoing[id] = new PendingSend(peer.Id, offered, item);
                }
                if (!_outgoing.TryGetValue(id, out var pending) || pending.PeerId != peer.Id || !pending.Item.IsActive)
                {
                    await link.Channel.SendAsync(new Msg(MsgType.Cancel), ct);
                    return;
                }
                var channel = link.Channel;
                if (await TransferAttempt.RunAsync(pending.Item, channel, c => FileSender.RunAsync(channel, pending.Set, pending.Item, c), null, ct))
                    _outgoing.TryRemove(id, out _);
            }
        }
        catch { }
        finally
        {
            if (link != null) link.Dispose();
            else tcp.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _capture.Dispose();
        _clipboard?.Dispose();
        CursorHider.Show();
        CancelCarry();
        _active = _driver = _relay = null;
        _peers = [];
        _listener.Stop();
        _fileListener.Stop();
        // Not cancelled: the parts stay on disk, so sending the same files later resumes.
        foreach (var item in TransferCenter.Items.Where(t => t.IsActive)) item.Fail("err_disconnected");
    }
}
