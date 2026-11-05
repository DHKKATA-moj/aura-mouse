using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace AuraMouse.Remote;

internal enum RemoteEnd { Declined, Busy, Unreachable, Lost }

/// The picture of the other screen, updated from the network thread and drawn by the window.
internal sealed class RemoteCanvas : IDisposable
{
    const int MaxSide = 8192;

    readonly object _lock = new();
    Bitmap? _picture;

    public Size Size { get { lock (_lock) return _picture?.Size ?? Size.Empty; } }

    public void Apply(int width, int height, ReadOnlySpan<byte> update)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide) throw new InvalidDataException("Bad picture size");
        lock (_lock)
        {
            if (_picture == null || _picture.Width != width || _picture.Height != height)
            {
                _picture?.Dispose();
                _picture = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            }
            using var g = Graphics.FromImage(_picture);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;

            int count = BinaryPrimitives.ReadInt16LittleEndian(update);
            int at = 2;
            for (int i = 0; i < count; i++)
            {
                int x = BinaryPrimitives.ReadInt16LittleEndian(update[at..]);
                int y = BinaryPrimitives.ReadInt16LittleEndian(update[(at + 2)..]);
                int length = BinaryPrimitives.ReadInt32LittleEndian(update[(at + 4)..]);
                at += FrameEncoder.PieceHeader;
                if (length <= 0 || length > update.Length - at) throw new InvalidDataException("Bad picture piece");
                // JPEG only: the decoder behind Bitmap understands far more formats than this ever needs to accept.
                if (length < 3 || update[at] != 0xFF || update[at + 1] != 0xD8 || update[at + 2] != 0xFF) throw new InvalidDataException("Not a JPEG");
                using var stream = new MemoryStream(update.Slice(at, length).ToArray());
                using var piece = new Bitmap(stream);
                g.DrawImageUnscaled(piece, x, y);
                at += length;
            }
        }
    }

    public void Draw(Graphics g, Rectangle target)
    {
        lock (_lock)
        {
            if (_picture == null) return;
            bool exact = target.Size == _picture.Size;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = exact ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_picture, target);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _picture?.Dispose();
            _picture = null;
        }
    }
}

/// The viewing side's connection. Runs entirely on its own threads, never the window's.
internal sealed class RemoteLink : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly Channel<Msg> _outbox = Channel.CreateUnbounded<Msg>(new() { SingleReader = true });
    readonly IPAddress _ip;
    readonly string _myName;
    readonly bool _isPhone;
    readonly RemoteComputer? _saved;
    TcpClient? _tcp;

    public RemoteCanvas Canvas { get; } = new();

    /// The person there is being asked; both screens show this code.
    public event Action<string>? Waiting;
    /// In. The name is the computer's, and how many monitors it has.
    public event Action<string, int>? Accepted;
    public event Action? PictureChanged;
    public event Action<RemoteEnd>? Ended;

    public RemoteLink(IPAddress ip, string myName, RemoteComputer? saved, bool isPhone = false)
    {
        _ip = ip;
        _myName = myName;
        _saved = saved;
        _isPhone = isPhone;
    }

    public void Start() => _ = Task.Run(Run);

    public void Pointer(int x, int y) => _outbox.Writer.TryWrite(new Msg(MsgType.RemotePointer, x, y));
    public void Send(Msg m) => _outbox.Writer.TryWrite(m);

    async Task Run()
    {
        var end = RemoteEnd.Lost;
        bool saveKeyless = false;
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            connecting.CancelAfter(TimeSpan.FromSeconds(5));
            int id = _saved?.Id ?? 0;
            var key = _saved?.Key;
            SecureChannel? channel = null;
            byte[] clientPublic = [], hostPublic = [];
            while (channel == null)
            {
                _tcp?.Dispose();
                _tcp = new TcpClient();
                try { await _tcp.ConnectAsync(_ip, Net.RemotePort, connecting.Token); }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    end = RemoteEnd.Unreachable;
                    return;
                }
                Net.Tune(_tcp);
                var stream = _tcp.GetStream();

                using var ecdh = SecureChannel.NewKey();
                clientPublic = ecdh.ExportSubjectPublicKeyInfo();
                await Net.SendAsync(stream, Msg.WithPayload(MsgType.Hello, SHA256.HashData(clientPublic)) with { C = id }, connecting.Token);
                var share = await Net.ReceiveAsync(stream, connecting.Token);
                if (share.Type != MsgType.KeyShare || share.Payload == null) throw new InvalidDataException();
                hostPublic = share.Payload;
                await Net.SendAsync(stream, Msg.WithPayload(MsgType.KeyShare, clientPublic), connecting.Token);

                var attempt = SecureChannel.Create(stream, ecdh, clientPublic, hostPublic, key ?? RemoteHandshake.NoKey, isHost: false);
                await attempt.SendAsync(Msg.WithPayload(MsgType.Auth, Encoding.UTF8.GetBytes(_myName)) with { A = _isPhone ? 1 : 0 }, connecting.Token);
                var reply = await Net.ReceiveAsync(stream, connecting.Token);
                if (reply.Type == MsgType.Welcome) channel = attempt;
                else if (key != null)
                {
                    // Its owner took the permission back: ask again like anyone else.
                    attempt.Dispose();
                    (id, key) = (0, null);
                    saveKeyless = true;
                }
                else throw new InvalidDataException();
            }

            connecting.CancelAfter(Timeout.InfiniteTimeSpan);
            _ = Task.Run(() => SendLoop(channel));
            using (channel)
            {
                int newId = 0;
                byte[]? newKey = null;
                while (true)
                {
                    var m = await channel.ReceiveFrameAsync(_cts.Token);
                    switch (m.Type)
                    {
                        case MsgType.RemoteWaiting:
                            Waiting?.Invoke(RemoteHandshake.Code(clientPublic, hostPublic));
                            break;
                        case MsgType.RemoteTrust:
                            if (channel.LastPayload.Length == RemoteStore.KeySize) (newId, newKey) = (m.A, channel.LastPayload.ToArray());
                            break;
                        case MsgType.RemoteAccepted:
                            var name = Net.CleanName(Encoding.UTF8.GetString(channel.LastPayload.Span), _ip.ToString());
                            if (saveKeyless && _saved != null) RemoteStore.ForgetKey(_saved.Name);
                            RemoteStore.SaveComputer(new RemoteComputer(name, _ip.ToString(), newId != 0 ? newId : id, newKey ?? key));
                            Accepted?.Invoke(name, m.B);
                            break;
                        case MsgType.RemoteDenied:
                            end = m.A == (int)RemoteRefusal.Busy ? RemoteEnd.Busy : RemoteEnd.Declined;
                            return;
                        case MsgType.RemoteFrame:
                            Canvas.Apply(m.B, m.C, channel.LastPayload.Span);
                            _outbox.Writer.TryWrite(new Msg(MsgType.RemoteAck, m.A));
                            PictureChanged?.Invoke();
                            break;
                    }
                }
            }
        }
        catch { }
        finally
        {
            _outbox.Writer.TryComplete();
            _tcp?.Dispose();
            if (!_cts.IsCancellationRequested) Ended?.Invoke(end);
        }
    }

    async Task SendLoop(SecureChannel channel)
    {
        try
        {
            await foreach (var m in _outbox.Reader.ReadAllAsync(_cts.Token))
                await channel.SendAsync(m, _cts.Token);
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _tcp?.Dispose();
    }
}

/// Who's on the network and willing to be asked.
internal static class RemoteDiscovery
{
    const int MaxConcurrent = 128;
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(900);

    public static async Task<List<(string Name, IPAddress Ip)>> FindAsync(CancellationToken ct)
    {
        var candidates = new HashSet<IPAddress>();
        var networks = LanScan.LocalNetworks().Take(4).ToList();
        foreach (var n in networks)
        {
            var b = n.Address.GetAddressBytes();
            for (int last = 1; last < 255; last++) candidates.Add(new IPAddress([b[0], b[1], b[2], (byte)last]));
        }
        candidates.RemoveWhere(LanScan.IsThisComputer); // it can be reachable through another of its adapters

        var found = new ConcurrentBag<(string, IPAddress)>();
        using var gate = new SemaphoreSlim(MaxConcurrent);
        await Task.WhenAll(candidates.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await AskName(ip, ct) is { } name) found.Add((name, ip));
            }
            finally { gate.Release(); }
        }));
        return found.OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task<string?> AskName(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await tcp.ConnectAsync(ip, Net.RemotePort, timeout.Token);
            var stream = tcp.GetStream();
            await Net.SendAsync(stream, new Msg(MsgType.RemoteProbe), timeout.Token);
            var info = await Net.ReceiveAsync(stream, timeout.Token);
            return info.Type == MsgType.RemoteInfo ? Net.CleanName(info.PayloadText, ip.ToString()) : null;
        }
        catch { return null; }
    }
}
