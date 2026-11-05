using System.Diagnostics;
using System.Text.Json;

namespace AuraMouse.Transfers;

internal enum TransferState { Waiting, Transferring, Retrying, Done, Failed, Canceled }

/// One send or receive, as shown in the transfers window. Updated from network threads, read by the UI.
internal sealed class TransferItem(string id, bool outgoing, string peerName, int peerId)
{
    long _doneBytes, _sampleBytes, _sampleTicks;
    volatile TransferState _state = TransferState.Waiting;

    public string Id { get; } = id;
    public bool Outgoing { get; } = outgoing;
    public string PeerName { get; } = peerName;
    public int PeerId { get; } = peerId;
    public string? Title { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorKey { get; private set; }
    public string? Folder { get; set; }
    public DateTime FinishedAt { get; private set; }
    public double BytesPerSecond { get; private set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public TransferState State => _state;
    public long DoneBytes => Interlocked.Read(ref _doneBytes);
    public bool IsActive => _state is TransferState.Waiting or TransferState.Transferring or TransferState.Retrying;

    public void SetDone(long bytes) => Interlocked.Exchange(ref _doneBytes, bytes);
    public void AddDone(long bytes) => Interlocked.Add(ref _doneBytes, bytes);

    public void SetState(TransferState state)
    {
        if (_state == state) return;
        _state = state;
        if (!IsActive) FinishedAt = DateTime.UtcNow;
        TransferCenter.Notify();
    }

    public void Fail(string errorKey)
    {
        ErrorKey = errorKey;
        SetState(TransferState.Failed);
    }

    /// Running transfers stop at the next chunk; ones that are only waiting stop right away.
    public void RequestCancel()
    {
        Cancellation.Cancel();
        if (_state is TransferState.Waiting or TransferState.Retrying) SetState(TransferState.Canceled);
    }

    /// Called by the UI a few times a second; smoothed so the number doesn't jump around.
    public void SampleSpeed()
    {
        long now = Stopwatch.GetTimestamp(), bytes = DoneBytes;
        if (_state != TransferState.Transferring) BytesPerSecond = 0;
        else if (_sampleTicks != 0)
        {
            double seconds = (now - _sampleTicks) / (double)Stopwatch.Frequency;
            double instant = Math.Max(0, bytes - _sampleBytes) / Math.Max(seconds, 0.001);
            BytesPerSecond = BytesPerSecond == 0 ? instant : BytesPerSecond * 0.75 + instant * 0.25;
        }
        _sampleTicks = now;
        _sampleBytes = bytes;
    }
}

/// All transfers of this run, newest first.
internal static class TransferCenter
{
    static readonly object Lock = new();
    static readonly List<TransferItem> All = [];

    /// Raised from any thread when a transfer is added, removed or changes state.
    public static event Action? Changed;

    public static IReadOnlyList<TransferItem> Items
    {
        get { lock (Lock) return All.ToList(); }
    }

    /// The running transfer with this id, or a new one. A transfer cancelled moments ago is returned
    /// too, so the other side's automatic retries are refused instead of quietly starting it again.
    public static TransferItem GetOrAdd(string id, bool outgoing, string peerName, int peerId)
    {
        lock (Lock)
        {
            var existing = All.FirstOrDefault(t => t.Id == id && t.Outgoing == outgoing &&
                (t.IsActive || t.State == TransferState.Canceled && DateTime.UtcNow - t.FinishedAt < TimeSpan.FromMinutes(1)));
            if (existing != null) return existing;

            All.RemoveAll(t => t.Id == id && t.Outgoing == outgoing); // an old finished row for the same files
            var item = new TransferItem(id, outgoing, peerName, peerId);
            All.Insert(0, item);
        }
        Notify();
        return Items.First(t => t.Id == id && t.Outgoing == outgoing);
    }

    public static void Remove(TransferItem item)
    {
        lock (Lock) All.Remove(item);
        Notify();
    }

    /// When a computer disconnects, transfers with it that aren't moving can't resume on their own.
    public static void FailWaiting(int peerId)
    {
        foreach (var item in Items.Where(t => t.PeerId == peerId && t.State is TransferState.Waiting or TransferState.Retrying))
            item.Fail("err_disconnected");
    }

    public static void Notify() => Changed?.Invoke();
}

/// Files being carried across the screen edge by a drag; they're transferred once dropped.
/// Files copied or dragged on one computer. Files lists them (up to PasteData.MaxFiles) so the clipboard
/// on the others can show them as files; older versions leave it out, and then only Ctrl+V pastes them.
internal sealed record DragInfo(string Id, string? Title, int Count, long Bytes, List<ManifestFile>? Files = null);

internal sealed record ManifestFile(string Path, long Size);
internal sealed record Manifest(string Id, string? Title, List<ManifestFile> Files);

/// Offsets[i] is where to resume file i, or -1 if the receiver already has all of it.
internal sealed record ManifestAck(bool Accepted, string? Reason, List<long>? Offsets);

/// The other side said no (no space, cancelled...). Not worth retrying.
internal sealed class TransferRefusedException(string reasonKey) : Exception(reasonKey)
{
    public string ReasonKey { get; } = reasonKey;
}

internal sealed class TransferCanceledByPeerException : Exception;

internal static class TransferJson
{
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    public static T Read<T>(byte[]? data) =>
        data == null ? throw new InvalidDataException("Missing payload") : JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Bad JSON");
}
