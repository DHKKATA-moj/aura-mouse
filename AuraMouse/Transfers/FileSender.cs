using System.Buffers;

namespace AuraMouse.Transfers;

/// Sends a FileSet over an established channel, starting each file where the receiver says it got to.
internal static class FileSender
{
    public const int ChunkSize = 512 * 1024;

    public static async Task RunAsync(SecureChannel channel, FileSet set, TransferItem item, CancellationToken ct)
    {
        item.Title ??= set.Title;
        item.FileCount = set.Files.Count;
        item.TotalBytes = set.TotalBytes;
        item.SetState(TransferState.Transferring);

        var manifest = new Manifest(set.Id, set.Title, set.Files.Select(f => new ManifestFile(f.RelativePath, f.Size)).ToList());
        var manifestBytes = TransferJson.Bytes(manifest);
        if (manifestBytes.Length > Msg.MaxPayload) throw new TransferRefusedException("err_too_many");
        await channel.SendAsync(new Msg(MsgType.Manifest) { Payload = manifestBytes }, ct);

        var reply = await channel.ReceiveAsync(ct);
        if (reply.Type == MsgType.Cancel) throw new TransferCanceledByPeerException();
        if (reply.Type != MsgType.ManifestAck) throw new InvalidDataException("Expected ManifestAck");
        var ack = TransferJson.Read<ManifestAck>(reply.Payload);
        if (!ack.Accepted) throw new TransferRefusedException(ack.Reason ?? "err_generic");
        if (ack.Offsets == null || ack.Offsets.Count != set.Files.Count) throw new InvalidDataException("Bad offsets");

        item.SetDone(set.Files.Select((f, i) => ack.Offsets[i] < 0 ? f.Size : Math.Clamp(ack.Offsets[i], 0, f.Size)).Sum());

        // The receiver only speaks again to say Done or Cancel; listen the whole time so a Cancel stops us at once.
        var incoming = channel.ReceiveAsync(ct);
        var buffers = new[] { ArrayPool<byte>.Shared.Rent(ChunkSize), ArrayPool<byte>.Shared.Rent(ChunkSize) };
        try
        {
            for (int i = 0; i < set.Files.Count; i++)
            {
                if (ack.Offsets[i] < 0) continue; // the receiver already has this one
                await SendFileAsync(channel, set.Files[i], i, Math.Clamp(ack.Offsets[i], 0, set.Files[i].Size), buffers, item, incoming, ct);
            }

            await channel.SendAsync(new Msg(MsgType.Done), ct);
            var done = await incoming;
            if (done.Type == MsgType.Cancel) throw new TransferCanceledByPeerException();
            if (done.Type != MsgType.Done) throw new InvalidDataException("Expected Done");
            item.SetState(TransferState.Done);
        }
        finally
        {
            foreach (var b in buffers) ArrayPool<byte>.Shared.Return(b);
        }
    }

    static async Task SendFileAsync(SecureChannel channel, SourceFile file, int index, long offset,
        byte[][] buffers, TransferItem item, Task<Msg> incoming, CancellationToken ct)
    {
        if (offset < file.Size)
        {
            await using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != file.Size) throw new TransferRefusedException("err_source_changed");
            stream.Position = offset;

            long remaining = file.Size - offset;
            int current = 0;
            var read = ReadChunk(stream, buffers[current], remaining, ct);
            while (true)
            {
                int n = await read;
                if (n == 0) break;
                remaining -= n;

                // Read the next chunk from disk while this one goes over the network.
                int next = current ^ 1;
                read = ReadChunk(stream, buffers[next], remaining, ct);

                // Cancel only between chunks, so the stream stays clean for the Cancel message.
                item.Cancellation.Token.ThrowIfCancellationRequested();
                if (incoming.IsCompleted)
                {
                    var early = await incoming; // throws if the connection broke
                    throw early.Type == MsgType.Cancel ? new TransferCanceledByPeerException() : new InvalidDataException($"Unexpected {early.Type}");
                }
                await channel.SendAsync(MsgType.FileChunk, index, 0, 0, buffers[current].AsMemory(0, n), ct);
                item.AddDone(n);
                current = next;
            }
            if (remaining != 0) throw new TransferRefusedException("err_source_changed");
        }

        await channel.SendAsync(new Msg(MsgType.FileEnd, index), ct);
    }

    static Task<int> ReadChunk(FileStream stream, byte[] buffer, long remaining, CancellationToken ct) =>
        remaining <= 0
            ? Task.FromResult(0)
            : stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, remaining)), ct).AsTask();
}
