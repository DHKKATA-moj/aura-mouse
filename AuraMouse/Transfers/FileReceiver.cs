using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AuraMouse.Transfers;

/// Receives files into a folder. Each file is written to "Downloads\Aura Mouse\.partial\<id>-<n>.part" and only
/// moved to its real name once complete, so a broken transfer leaves the part behind and resumes from its end.
/// Parts always live there, even when the files go elsewhere (dropped on the desktop, say), so no hidden
/// folders are left around the user's own folders.
internal static partial class FileReceiver
{
    const long SpaceMargin = 64L << 20;

    public static string DefaultFolder => Path.Combine(Native.DownloadsFolder(), "Aura Mouse");

    static string PartialFolder => Path.Combine(DefaultFolder, ".partial");

    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public static async Task RunAsync(SecureChannel channel, TransferItem item, string root, CancellationToken ct)
    {
        // Reads stop when the user cancels; the caller then tells the sender.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, item.Cancellation.Token);
        var readCt = linked.Token;

        var first = await channel.ReceiveAsync(readCt);
        if (first.Type == MsgType.Cancel) throw new TransferCanceledByPeerException();
        if (first.Type != MsgType.Manifest) throw new InvalidDataException("Expected Manifest");
        var manifest = TransferJson.Read<Manifest>(first.Payload);
        Validate(manifest, item.Id);

        if (item.State == TransferState.Canceled)
        {
            await Refuse(channel, "err_canceled_remote", ct);
            return;
        }

        Directory.CreateDirectory(root);
        var partialDir = Directory.CreateDirectory(PartialFolder);
        partialDir.Attributes |= FileAttributes.Hidden;
        var state = ReceiveState.Load(partialDir.FullName, manifest);

        item.Title = manifest.Title;
        item.FileCount = manifest.Files.Count;
        item.TotalBytes = manifest.Files.Sum(f => f.Size);
        item.Folder = root;

        var offsets = new List<long>(manifest.Files.Count);
        long have = 0, need = 0;
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            long size = manifest.Files[i].Size;
            if (state.Completed[i]) { offsets.Add(-1); have += size; continue; }

            var part = PartPath(partialDir.FullName, manifest.Id, i);
            long length = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (length > size) { File.Delete(part); length = 0; }
            offsets.Add(length);
            have += length;
            need += size - length;
        }

        // The parts are written in Downloads; if the files end up on another drive they need room there too.
        var drives = new[] { partialDir.FullName, root }.Select(p => Path.GetPathRoot(Path.GetFullPath(p))!).Distinct(StringComparer.OrdinalIgnoreCase);
        if (drives.Any(d => new DriveInfo(d).AvailableFreeSpace < need + SpaceMargin))
        {
            await Refuse(channel, "err_space_remote", ct);
            throw new TransferRefusedException("err_space_local");
        }

        await channel.SendAsync(new Msg(MsgType.ManifestAck) { Payload = TransferJson.Bytes(new ManifestAck(true, null, offsets)) }, ct);
        item.SetDone(have);
        item.SetState(TransferState.Transferring);

        FileStream? output = null;
        int outputIndex = -1;
        long outputLength = 0;
        var buffers = new[] { ArrayPool<byte>.Shared.Rent(FileSender.ChunkSize), ArrayPool<byte>.Shared.Rent(FileSender.ChunkSize) };
        int current = 0;
        Task pendingWrite = Task.CompletedTask;
        try
        {
            while (true)
            {
                var m = await channel.ReceiveFrameAsync(readCt);
                switch (m.Type)
                {
                    case MsgType.FileChunk:
                    {
                        int i = CheckIndex(m.A, manifest, state);
                        if (i != outputIndex)
                        {
                            await pendingWrite;
                            if (output != null) await output.DisposeAsync();
                            output = new FileStream(PartPath(partialDir.FullName, manifest.Id, i), FileMode.Append, FileAccess.Write,
                                FileShare.Read, bufferSize: 0, FileOptions.Asynchronous);
                            outputIndex = i;
                            outputLength = output.Length;
                        }

                        var data = channel.LastPayload;
                        if (data.Length > FileSender.ChunkSize || outputLength + data.Length > manifest.Files[i].Size)
                            throw new InvalidDataException("More data than the file size");

                        // Write this chunk while the next one is being received; only one write runs at a time.
                        await pendingWrite;
                        data.CopyTo(buffers[current]);
                        pendingWrite = output!.WriteAsync(buffers[current].AsMemory(0, data.Length), ct).AsTask();
                        current ^= 1;
                        outputLength += data.Length;
                        item.AddDone(data.Length);
                        break;
                    }

                    case MsgType.FileEnd:
                    {
                        int i = CheckIndex(m.A, manifest, state);
                        await pendingWrite;
                        if (output != null && outputIndex == i)
                        {
                            await output.DisposeAsync();
                            output = null;
                            outputIndex = -1;
                        }

                        var part = PartPath(partialDir.FullName, manifest.Id, i);
                        if (!File.Exists(part)) File.Create(part).Dispose(); // empty file
                        if (new FileInfo(part).Length != manifest.Files[i].Size) throw new InvalidDataException("Size mismatch");

                        var final = state.FinalPath(root, manifest.Files[i].Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                        File.Move(part, final);
                        MarkFromNetwork(final);
                        state.Completed[i] = true;
                        state.Save();
                        break;
                    }

                    case MsgType.Done:
                        if (!state.Completed.All(c => c)) throw new InvalidDataException("Done before all files arrived");
                        await channel.SendAsync(new Msg(MsgType.Done), ct);
                        state.Delete();
                        item.SetState(TransferState.Done);
                        return;

                    case MsgType.Cancel:
                        throw new TransferCanceledByPeerException();

                    default:
                        throw new InvalidDataException($"Unexpected {m.Type}");
                }
            }
        }
        finally
        {
            try { await pendingWrite; } catch { }
            if (output != null) await output.DisposeAsync();
            foreach (var b in buffers) ArrayPool<byte>.Shared.Return(b);
        }
    }

    /// The same mark a browser puts on downloads, so Windows warns before running a program that came over the network.
    public static void MarkFromNetwork(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { } // a drive without this feature
    }

    /// Deletes the parts of a cancelled transfer.
    public static void Discard(string id)
    {
        if (!IsValidId(id) || !Directory.Exists(PartialFolder)) return;
        foreach (var file in Directory.EnumerateFiles(PartialFolder, id + "*"))
            try { File.Delete(file); } catch (IOException) { }
    }

    static async Task Refuse(SecureChannel channel, string reason, CancellationToken ct) =>
        await channel.SendAsync(new Msg(MsgType.ManifestAck) { Payload = TransferJson.Bytes(new ManifestAck(false, reason, null)) }, ct);

    static string PartPath(string partialDir, string id, int index) => Path.Combine(partialDir, $"{id}-{index}.part");

    static int CheckIndex(int index, Manifest manifest, ReceiveState state)
    {
        if (index < 0 || index >= manifest.Files.Count || state.Completed[index]) throw new InvalidDataException("Bad file index");
        return index;
    }

    /// Everything in the manifest comes from the network, so check it before using it for file names.
    static void Validate(Manifest manifest, string expectedId)
    {
        if (manifest.Id != expectedId || !IsValidId(manifest.Id)) throw new InvalidDataException("Bad transfer id");
        if (manifest.Files == null || manifest.Files.Count is 0 or > FileSet.MaxFiles) throw new InvalidDataException("Bad file count");
        if (manifest.Files.Any(f => f.Size < 0 || string.IsNullOrEmpty(f.Path) || f.Path.Length > 4096))
            throw new InvalidDataException("Bad file entry");
        if (manifest.Files.Sum(f => (decimal)f.Size) > long.MaxValue / 2) throw new InvalidDataException("Bad total size");
    }
}

/// What a receive has finished so far, saved next to the parts so it survives restarts.
internal sealed class ReceiveState
{
    string _file = "";

    public string Id { get; set; } = "";
    public long[] Sizes { get; set; } = [];
    public bool[] Completed { get; set; } = [];
    /// Top-level names as received -> names used on disk (e.g. "Photos" -> "Photos (1)" if one existed).
    public Dictionary<string, string> TopNames { get; set; } = [];

    public static ReceiveState Load(string partialDir, Manifest manifest)
    {
        var file = Path.Combine(partialDir, manifest.Id + ".json");
        var sizes = manifest.Files.Select(f => f.Size).ToArray();
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<ReceiveState>(File.ReadAllBytes(file)) is { } saved &&
                saved.Id == manifest.Id && saved.Sizes.SequenceEqual(sizes) && saved.Completed.Length == sizes.Length)
            {
                saved._file = file;
                return saved;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }

        return new ReceiveState { Id = manifest.Id, Sizes = sizes, Completed = new bool[sizes.Length], _file = file };
    }

    public void Save() => File.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(this));

    public void Delete()
    {
        try { File.Delete(_file); } catch (IOException) { }
    }

    public string FinalPath(string root, string relativePath)
    {
        root = Path.GetFullPath(root);
        var segments = SafePath.Segments(relativePath);
        if (!TopNames.TryGetValue(segments[0], out var top))
        {
            top = SafePath.FreeName(root, segments[0], isFile: segments.Length == 1);
            TopNames[segments[0]] = top;
        }
        segments[0] = top;

        var full = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path escapes the download folder");

        // Two entries with the same name in one transfer: keep both.
        if (File.Exists(full) || Directory.Exists(full))
            full = Path.Combine(Path.GetDirectoryName(full)!, SafePath.FreeName(Path.GetDirectoryName(full)!, Path.GetFileName(full), isFile: true));
        return full;
    }
}

internal static class SafePath
{
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// Splits a received relative path into safe names: no "..", no drive letters, no invalid characters.
    public static string[] Segments(string path)
    {
        var parts = path.Split('/', '\\').Where(p => p.Length > 0 && p != "." && p != "..").Select(Clean).ToArray();
        return parts.Length == 0 ? ["file"] : parts;
    }

    static string Clean(string name)
    {
        var clean = new string(name.Select(c => Array.IndexOf(Invalid, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "_";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(clean))) clean = "_" + clean;
        return clean.Length > 150 ? clean[..150] : clean;
    }

    /// "name.ext", or "name (1).ext", "name (2).ext"... whichever doesn't exist yet in dir.
    public static string FreeName(string dir, string name, bool isFile)
    {
        for (int n = 0; ; n++)
        {
            var candidate = n == 0 ? name
                : isFile ? $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}"
                : $"{name} ({n})";
            var full = Path.Combine(dir, candidate);
            if (!File.Exists(full) && !Directory.Exists(full)) return candidate;
        }
    }
}
