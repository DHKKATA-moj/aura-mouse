using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AuraMouse.Transfers;

/// A dedicated encrypted connection for one transfer, separate from the mouse/keyboard connection
/// so a big file never makes the cursor lag. Always opened by the client, authenticated with the
/// per-computer key the host handed out over the main connection.
internal sealed class FileLink(TcpClient tcp, SecureChannel channel) : IDisposable
{
    const int SocketBuffer = 4 << 20;

    public SecureChannel Channel { get; } = channel;

    static void Tune(TcpClient tcp)
    {
        tcp.NoDelay = true;
        tcp.SendBufferSize = SocketBuffer;
        tcp.ReceiveBufferSize = SocketBuffer;
    }

    public static async Task<FileLink> ConnectAsync(IPAddress host, int peerId, byte[] key, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            Tune(tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            await tcp.ConnectAsync(host, Net.FilePort, timeout.Token);
            var stream = tcp.GetStream();
            using var ecdh = SecureChannel.NewKey();
            var clientPublic = ecdh.ExportSubjectPublicKeyInfo();
            await Net.SendAsync(stream, Msg.WithPayload(MsgType.Hello, clientPublic) with { C = peerId }, timeout.Token);

            var share = await Net.ReceiveAsync(stream, timeout.Token);
            if (share.Type != MsgType.KeyShare || share.Payload == null) throw new InvalidDataException("Unexpected handshake reply");
            var channel = SecureChannel.Create(stream, ecdh, clientPublic, share.Payload, key, isHost: false);

            await channel.SendAsync(new Msg(MsgType.Auth), timeout.Token);
            var welcome = await channel.ReceiveAsync(timeout.Token);
            if (welcome.Type != MsgType.Welcome) throw new InvalidDataException("Host refused the file connection");
            return new FileLink(tcp, channel);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// Host side. Returns null if the other end doesn't hold a valid key.
    public static async Task<(FileLink Link, int PeerId)?> AcceptAsync(TcpClient tcp, Func<int, byte[]?> keyFor, CancellationToken ct)
    {
        Tune(tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var stream = tcp.GetStream();
        var hello = await Net.ReceiveAsync(stream, timeout.Token);
        if (hello.Type != MsgType.Hello || hello.Payload == null || keyFor(hello.C) is not { } key) return null;

        using var ecdh = SecureChannel.NewKey();
        var hostPublic = ecdh.ExportSubjectPublicKeyInfo();
        await Net.SendAsync(stream, Msg.WithPayload(MsgType.KeyShare, hostPublic), timeout.Token);
        var channel = SecureChannel.Create(stream, ecdh, hello.Payload, hostPublic, key, isHost: true);
        channel.FrameLimit = SecureChannel.HandshakeFrame;

        Msg auth;
        try { auth = await channel.ReceiveAsync(timeout.Token); }
        catch (CryptographicException) { auth = default; }
        if (auth.Type != MsgType.Auth)
        {
            channel.Dispose();
            return null;
        }

        await channel.SendAsync(new Msg(MsgType.Welcome), timeout.Token);
        channel.FrameLimit = SecureChannel.MaxFrame;
        return (new FileLink(tcp, channel), hello.C);
    }

    public void Dispose()
    {
        Channel.Dispose();
        tcp.Dispose();
    }
}

internal static class TransferAttempt
{
    /// Runs one connection's worth of a transfer. Returns true when the transfer is over for good
    /// (done, refused, cancelled), false when the connection broke and it should be resumed.
    public static async Task<bool> RunAsync(TransferItem item, SecureChannel channel, Func<CancellationToken, Task> work,
        string? receiveRoot, CancellationToken ct)
    {
        try
        {
            await work(ct);
            return true;
        }
        catch (TransferRefusedException e)
        {
            item.Fail(e.ReasonKey);
            return true;
        }
        catch (TransferCanceledByPeerException)
        {
            if (receiveRoot != null) FileReceiver.Discard(item.Id);
            item.SetState(TransferState.Canceled);
            return true;
        }
        catch (Exception) when (item.Cancellation.IsCancellationRequested)
        {
            // Tell the other side, so it stops too and doesn't keep the half-received files.
            try { await channel.SendAsync(new Msg(MsgType.Cancel), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
            if (receiveRoot != null) FileReceiver.Discard(item.Id);
            item.SetState(TransferState.Canceled);
            return true;
        }
        catch (Exception)
        {
            if (item.IsActive) item.SetState(TransferState.Retrying);
            return false;
        }
    }
}
