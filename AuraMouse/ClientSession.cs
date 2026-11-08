using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AuraMouse.Transfers;
using AuraMouse.Ui;
using static AuraMouse.Native;

namespace AuraMouse;

/// Runs on a computer that connects to the host. Control works both ways: the host's mouse can come
/// over here, and this computer's own mouse can cross the edge facing the host and drive it (and the
/// other computers beyond it). Whichever mouse or keyboard is touched last is in control.
internal sealed class ClientSession : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly SharedClipboard _clipboard = new();
    readonly SynchronizationContext? _ui = SynchronizationContext.Current; // created on the UI thread
    readonly InputCapture _capture = new();
    readonly InputPlayer _player = new();
    readonly HashSet<string> _runningTransfers = [];
    readonly ConcurrentDictionary<string, FileSet> _dragSets = new(); // dragged over to the host, sent once dropped
    readonly DragOutProbe _probe;
    volatile DragInfo? _carry; // files the host is dragging over our screen, not dropped yet
    Channel<Msg> _outbox = Channel.CreateUnbounded<Msg>(new() { SingleReader = true });
    TcpClient? _tcp;
    bool _hooked;

    volatile bool _active;  // another computer's mouse is controlling us
    volatile bool _driving; // our own mouse is controlling the host side
    volatile bool _locked;  // the user's shortcut: the mouse stays on the screen it's on, on every computer
    volatile Edge _myEdge = Edge.None; // where the host put us; our opposite edge leads to it
    Edge _enteredFrom, _exitEdge;

    // The host, and what it gave us for opening file connections.
    IPAddress? _hostIp;
    string _hostName = "";
    volatile bool _connected;
    volatile byte[]? _fileKey;
    int _peerId;

    public event Action<SessionStatus>? StatusChanged;
    public event Action<string, IPAddress>? HostFound;
    /// The host gave us a key for connecting without the code from now on.
    public event Action<SavedComputer>? Paired;
    /// The mouse was locked to its screen or let go, from any computer's shortcut. Any thread.
    public event Action<bool>? LockChanged;

    public bool Locked => _locked;

    public ClientSession()
    {
        _probe = new DragOutProbe(_ui);
        _clipboard.Copied += m => { if (_connected) Post(m); };
        _clipboard.PasteFiles += (info, folder) => StartDownload(info.Id, folder);
        _capture.LocalMouse = OnLocalMouse;
        _capture.LocalKey = vk =>
        {
            if (_active) TakeOverLocally();
            _clipboard.KeyDown(vk);
        };
        _capture.LockShortcut = ToggleLock;
        _capture.Escape = () =>
        {
            StopDrivingLocally(5000);
            Post(new Msg(MsgType.TakeOver));
        };
        _capture.FrontWalledOff = () =>
        {
            if (_driving) _capture.Escape?.Invoke();
            if (_active) GiveBack();
        };
    }

    public bool CanTransfer => _connected && _fileKey != null;

    public void StartDiscovery()
    {
        _ = DiscoveryLoop(_cts.Token);
        _ = ScanLoop(_cts.Token);
    }

    /// Looks for the host directly as well, in case its announcements never reach us.
    async Task ScanLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_connected)
        {
            try
            {
                foreach (var ip in await LanScan.FindHostsAsync(ct))
                    HostFound?.Invoke(await LanScan.NameOfAsync(ip), ip);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { }
        }
    }

    const int MaxAnnouncementsPerSecond = 32;

    async Task DiscoveryLoop(CancellationToken ct)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Net.DiscoveryPort));
        }
        catch (SocketException)
        {
            StatusChanged?.Invoke(SessionStatus.DiscoveryFailed);
            return;
        }

        long windowStart = 0;
        int inWindow = 0;
        using (udp)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; }

                // Real computers announce once a second. Anyone on the network could send thousands, and each
                // would reach the window, so past a sensible number they're dropped.
                long now = Environment.TickCount64;
                if (now - windowStart >= 1000) (windowStart, inWindow) = (now, 0);
                if (++inWindow > MaxAnnouncementsPerSecond) continue;

                var text = Encoding.UTF8.GetString(r.Buffer);
                if (text.StartsWith(Net.Beacon) && LanScan.IsNearby(r.RemoteEndPoint.Address))
                    HostFound?.Invoke(text[Net.Beacon.Length..], r.RemoteEndPoint.Address);
            }
        }
    }

    /// Returns false if the pairing code was wrong; throws if the host can't be reached.
    public Task<bool> ConnectAsync(IPAddress ip, int code, string hostName) =>
        ConnectAsync(ip, code, key: null, pairId: 0, hostName);

    /// Connects with the key saved when the two computers were paired; false if the host doesn't accept it.
    public Task<bool> ConnectAsync(IPAddress ip, SavedComputer host) => ConnectAsync(ip, 0, host.Key, host.Id, host.Name);

    /// With no key, the code is proved through Pake, so even a fake computer learns nothing from the attempt.
    async Task<bool> ConnectAsync(IPAddress ip, int code, byte[]? key, int pairId, string hostName)
    {
        _hostIp = ip;
        _hostName = hostName;
        _tcp?.Dispose();
        _tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        await _tcp.ConnectAsync(ip, Net.TcpPort, timeout.Token);
        Net.Tune(_tcp);
        var stream = _tcp.GetStream();

        using var ecdh = SecureChannel.NewKey();
        var clientPublic = ecdh.ExportSubjectPublicKeyInfo();
        await Net.SendAsync(stream, Msg.WithPayload(MsgType.Hello, clientPublic) with { C = pairId }, timeout.Token);
        var pake = key == null ? new Pake(code, isClient: true) : null;
        if (pake != null) await Net.SendAsync(stream, Msg.WithPayload(MsgType.PakeShare, pake.Share), timeout.Token);
        var share = await Net.ReceiveAsync(stream, timeout.Token);
        if (share.Type == MsgType.Rejected) // it isn't accepting the code right now
        {
            _tcp.Dispose();
            _tcp = null;
            return false;
        }
        if (share.Type != MsgType.KeyShare || share.Payload == null) throw new InvalidDataException("Unexpected handshake reply");

        byte[] secret;
        if (pake != null)
        {
            var theirs = await Net.ReceiveAsync(stream, timeout.Token);
            if (theirs.Type != MsgType.PakeShare || theirs.Payload == null) throw new InvalidDataException("Unexpected handshake reply");
            secret = pake.Finish(theirs.Payload, clientPublic, share.Payload);
        }
        else secret = key!;

        var channel = SecureChannel.Create(stream, ecdh, clientPublic, share.Payload, secret, isHost: false);
        // Our name lets the host remember which edge this computer sits on.
        await channel.SendAsync(Msg.WithPayload(MsgType.Auth, Encoding.UTF8.GetBytes(Environment.MachineName)), timeout.Token);
        var reply = await Net.ReceiveAsync(stream, timeout.Token);
        if (reply.Type != MsgType.Welcome)
        {
            channel.Dispose();
            _tcp.Dispose();
            _tcp = null;
            return false;
        }

        // The plain Welcome could be forged; the encrypted one proves the host has the same keys.
        var confirm = await channel.ReceiveAsync(timeout.Token);
        if (confirm.Type != MsgType.Welcome) throw new InvalidDataException("Host failed to confirm");

        _connected = true;
        _outbox = Channel.CreateUnbounded<Msg>(new() { SingleReader = true });
        var outbox = _outbox;
        _ = Task.Run(() => SendLoop(channel, outbox, _cts.Token)); // off the window's thread, or a busy window holds up the mouse
        _ = Task.Run(() => ReceiveLoop(channel, _cts.Token));
        Post(Wake.InfoMessage());

        // Watch our own mouse and keyboard, so they can drive the host too.
        if (!_hooked && !_cts.IsCancellationRequested)
        {
            _hooked = true;
            _capture.Install();
        }
        return true;
    }

    /// Everything we send goes through one queue, so the hook and the network thread never write at once.
    void Post(Msg m) => _outbox.Writer.TryWrite(m);

    static async Task SendLoop(SecureChannel channel, Channel<Msg> outbox, CancellationToken ct)
    {
        try
        {
            await foreach (var m in outbox.Reader.ReadAllAsync(ct))
                await channel.SendAsync(m, ct);
        }
        catch { }
    }

    async Task ReceiveLoop(SecureChannel channel, CancellationToken ct)
    {
        try
        {
            while (true)
                Handle(await channel.ReceiveAsync(ct));
        }
        catch { }

        _connected = false;
        _outbox.Writer.TryComplete();
        channel.Dispose();
        if (_driving) StopDrivingLocally(5000);
        if (_active) InputPlayer.ReleaseModifiers();
        _active = false;
        if (!ct.IsCancellationRequested) StatusChanged?.Invoke(SessionStatus.Disconnected);
    }

    void Handle(Msg m)
    {
        switch (m.Type)
        {
            // ── the host's mouse (or one passing through it) controls us ──
            case MsgType.Enter:
                if (_driving) StopDrivingLocally(5000);
                _enteredFrom = (Edge)m.A;
                _player.PlaceAt(_enteredFrom, m.B, inset: 1);
                _active = true;
                break;

            case MsgType.Move when _active:
                var exit = _player.Move(m.A, m.B, edge => !_locked && edge == _enteredFrom, out int ratio);
                if (exit != Edge.None) GiveBack(ratio);
                break;

            case MsgType.TextInput when _active:
                InputPlayer.Play(m);
                break;

            case MsgType.Button or MsgType.Wheel when _active:
                // Carrying files dragged over from the host: letting go of the button drops them here.
                if (m.Type == MsgType.Button && m.A == MOUSEEVENTF_LEFTUP && _carry is { } carry) { DropHere(carry); break; }
                InputPlayer.Play(m);
                break;

            case MsgType.Key:
                if (m.A == 0x1B && (m.C & KEYEVENTF_KEYUP) == 0) CancelCarry(); // Escape
                InputPlayer.Play(m); // key-ups still go through after leaving, so nothing stays stuck
                if (_active && (m.C & KEYEVENTF_KEYUP) == 0) _clipboard.KeyDown(m.A);
                break;

            case MsgType.Release:
                CancelCarry();
                InputPlayer.ReleaseModifiers();
                _active = false;
                break;

            // ── files dragged across the screen edge ──
            case MsgType.DragStart:
                var info = TransferJson.Read<DragInfo>(m.Payload);
                if (!_active || !FileReceiver.IsValidId(info.Id)) break;
                _carry = info;
                _ui?.Post(_ => DragUi.ShowGhost(info), null);
                break;

            case MsgType.DropAt:
                // Files dragged over and dropped, or copied here and pasted there.
                if (_dragSets.TryRemove(m.PayloadText, out var dropped)) SendSet(dropped);
                else if (_clipboard.LocalFiles is { } copied && copied.Id == m.PayloadText) SendSet(copied);
                break;

            // ── our own mouse controls the host side ──
            case MsgType.YourEdge:
                _myEdge = (Edge)m.A;
                if (_myEdge == Edge.None && _driving) StopDrivingLocally(5000);
                break;

            case MsgType.Return:
                if (_driving) StopDrivingLocally(m.B);
                break;

            case MsgType.StopDriving:
                if (_driving) StopDrivingLocally(5000);
                break;

            case MsgType.Lock:
                SetLock(m.A == 1);
                break;

            case MsgType.WakeInfo:
                Wake.Accept(m.Payload, null, _hostIp);
                break;

            // ── clipboard and files ──
            case MsgType.Clipboard:
                _ui?.Post(_ => _clipboard.Apply(m), null);
                break;

            case MsgType.FileKey:
                _peerId = m.A;
                _fileKey = m.Payload;
                break;

            case MsgType.PairKey:
                if (m.Payload is { Length: > PairStore.KeySize } pair && _hostIp != null)
                {
                    var name = Net.CleanName(Encoding.UTF8.GetString(pair, PairStore.KeySize, pair.Length - PairStore.KeySize), _hostIp.ToString());
                    Paired?.Invoke(new SavedComputer(m.A, name, pair[..PairStore.KeySize], _hostIp.ToString()));
                }
                break;

            case MsgType.FileReady:
                StartDownload(m.PayloadText, FileReceiver.DefaultFolder);
                break;
        }
    }

    public void ToggleLock()
    {
        Post(new Msg(MsgType.Lock, _locked ? 0 : 1)); // the host passes it on to the others
        SetLock(!_locked);
    }

    void SetLock(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        LockChanged?.Invoke(locked);
    }

    bool OnLocalMouse(int msg, POINT pt)
    {
        _probe.Track(msg, pt);
        if (!_connected) return false;

        if (_active)
        {
            // Someone touched this computer's own mouse while another one was in control: ours wins.
            if (msg != WM_MOUSEMOVE || !_player.IsOwnMove(pt)) TakeOverLocally();
            return false;
        }

        if (msg != WM_MOUSEMOVE || _myEdge == Edge.None || _locked) return false;
        var exit = _myEdge.Opposite(); // e.g. we sit on the host's right, so our left edge leads to it
        if (InputCapture.EdgeAt(pt) != exit) return false;

        if (_capture.LeftButtonDown)
        {
            // Button held at the edge: cross only if it's files being dragged, so window snapping still works.
            _probe.Probe(pt, paths => DragFilesToHost(exit, pt, paths));
            return false;
        }

        StartDriving(exit, pt);
        return true;
    }

    /// Files dragged against the edge: the cursor goes over carrying them; they're sent once dropped there.
    async void DragFilesToHost(Edge exit, POINT pt, string[] paths)
    {
        if (_driving || _active || !_connected) return;
        StartDriving(exit, pt);
        try
        {
            var set = await Task.Run(() => FileSet.FromPaths(paths));
            if (set.Files.Count == 0) return;
            _dragSets[set.Id] = set;
            var info = new DragInfo(set.Id, set.Title, set.Files.Count, set.TotalBytes);
            Post(new Msg(MsgType.DragStart) { Payload = TransferJson.Bytes(info) });
        }
        catch (Exception) { } // unreadable files: it simply becomes a normal crossing
    }

    void DropHere(DragInfo carry)
    {
        _carry = null;
        _ui?.Post(_ =>
        {
            DragUi.HideGhost();
            StartDownload(carry.Id, DropFolder.At(Cursor.Position));
        }, null);
    }

    /// The other computer's mouse goes back to it, at this point along the edge it came in by.
    void GiveBack(int ratio = 5000)
    {
        _active = false;
        CancelCarry(); // went back without dropping
        Post(new Msg(MsgType.Leave, 0, ratio));
        InputPlayer.ReleaseModifiers();
    }

    void CancelCarry()
    {
        if (_carry == null) return;
        _carry = null;
        _ui?.Post(_ => DragUi.HideGhost(), null);
    }

    void StartDriving(Edge exit, POINT pt)
    {
        _exitEdge = exit;
        Post(new Msg(MsgType.ClientEnter, 0, Net.RatioAlong(exit, pt.X, pt.Y, SystemInformation.VirtualScreen)));
        _driving = true;
        _capture.Begin(Post);
    }

    void StopDrivingLocally(int ratio)
    {
        _capture.End();
        if (!_driving) return;
        _driving = false;
        _player.PlaceAt(_exitEdge, ratio, inset: 2); // just inside the edge, so it doesn't cross straight back
    }

    void TakeOverLocally()
    {
        CancelCarry();
        _active = false;
        InputPlayer.ReleaseModifiers();
        Post(new Msg(MsgType.TakeOver));
    }

    // ───────────────────────── files ─────────────────────────

    public async Task SendFilesAsync(IReadOnlyList<string> paths)
    {
        var set = await Task.Run(() => FileSet.FromPaths(paths));
        if (set.Files.Count > 0) SendSet(set);
    }

    void SendSet(FileSet set)
    {
        var item = TransferCenter.GetOrAdd(set.Id, outgoing: true, _hostName, 0);
        if (!item.IsActive) return;
        item.Title ??= set.Title;
        item.FileCount = set.Files.Count;
        item.TotalBytes = set.TotalBytes;
        RunTransfer(item, direction: 0, channel => FileSender.RunAsync(channel, set, item, _cts.Token), receiveRoot: null);
    }

    void StartDownload(string id, string root)
    {
        if (!FileReceiver.IsValidId(id)) return;
        var item = TransferCenter.GetOrAdd(id, outgoing: false, _hostName, 0);
        if (!item.IsActive) return;
        RunTransfer(item, direction: 1, channel => FileReceiver.RunAsync(channel, item, root, _cts.Token), root);
    }

    /// We always open file connections. If one breaks, connect again and carry on from where it stopped,
    /// for as long as we're still connected to the host.
    async void RunTransfer(TransferItem item, int direction, Func<SecureChannel, Task> work, string? receiveRoot)
    {
        lock (_runningTransfers)
            if (!_runningTransfers.Add(item.Id + direction)) return; // already on it

        try
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // files don't belong on the window's thread

            var delay = TimeSpan.FromSeconds(1);
            while (!_cts.IsCancellationRequested)
            {
                if (item.Cancellation.IsCancellationRequested) { item.SetState(TransferState.Canceled); return; }
                if (!CanTransfer || _hostIp == null) { item.Fail("err_disconnected"); return; }

                try
                {
                    using var link = await FileLink.ConnectAsync(_hostIp, _peerId, _fileKey!, _cts.Token);
                    await link.Channel.SendAsync(new Msg(MsgType.FileStart, direction) { Payload = Encoding.UTF8.GetBytes(item.Id) }, _cts.Token);
                    if (await TransferAttempt.RunAsync(item, link.Channel, _ => work(link.Channel), receiveRoot, _cts.Token)) return;
                    delay = TimeSpan.FromSeconds(1); // it was moving; try again quickly
                }
                catch (Exception) when (!_cts.IsCancellationRequested)
                {
                    if (item.IsActive) item.SetState(TransferState.Retrying);
                }

                try { await Task.Delay(delay, item.Cancellation.Token); } catch (OperationCanceledException) { }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 8));
            }
        }
        finally
        {
            lock (_runningTransfers) _runningTransfers.Remove(item.Id + direction);
        }
    }

    public void Dispose()
    {
        _connected = false;
        _cts.Cancel();
        _capture.Dispose();
        _clipboard.Dispose();
        _tcp?.Dispose();
        // Not cancelled: the parts stay on disk, so sending the same files later resumes.
        foreach (var item in TransferCenter.Items.Where(t => t.IsActive)) item.Fail("err_disconnected");
    }
}
