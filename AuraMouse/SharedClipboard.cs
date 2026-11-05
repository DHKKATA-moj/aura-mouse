using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using AuraMouse.Transfers;
using AuraMouse.Ui;
using static AuraMouse.Native;

namespace AuraMouse;

/// What a Clipboard message carries (its A).
internal enum ClipKind { Text = 0, Image = 1, Files = 2 }

/// Keeps every computer's clipboard the same. Whatever is copied (text, a picture, files) goes to
/// the others the moment it's copied, whichever mouse or keyboard is in use. Copied files aren't sent
/// right away: the others get their list, offered as files Explorer can paste (PasteData), so pasting
/// any way at all fetches them. Lives on the UI (STA) thread.
internal sealed class SharedClipboard : NativeWindow, IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D, VK_CONTROL = 0x11, VK_V = 0x56;
    const int MaxImage = Msg.MaxPayload - 1024;
    static readonly string[] PrivateMarks = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];
    static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// Offered as real files (Explorer fetches them) or, for older senders and huge sets, as their names
    /// with Ctrl+V watched instead.
    sealed record RemoteFiles(DragInfo Info, uint Sequence, bool AsFiles);

    [DllImport("ole32.dll")] static extern int OleSetClipboard(System.Runtime.InteropServices.ComTypes.IDataObject? data);
    [DllImport("ole32.dll")] static extern int OleIsCurrentClipboard(System.Runtime.InteropServices.ComTypes.IDataObject data);

    /// Where pasted files arrive before Explorer copies them to wherever they were pasted.
    static readonly string Staging = Path.Combine(Path.GetTempPath(), "Aura Mouse paste");
    PasteData? _offered;

    readonly SynchronizationContext _ui = SynchronizationContext.Current!;
    readonly System.Windows.Forms.Timer _settle = new() { Interval = 120 };
    uint _ownSequence; // the clipboard as we last filled it ourselves (from another computer)
    volatile RemoteFiles? _remoteFiles;
    volatile FileSet? _localFiles;

    public SharedClipboard()
    {
        try { if (Directory.Exists(Staging)) Directory.Delete(Staging, recursive: true); } // left over from last time
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE: never shown
        AddClipboardFormatListener(Handle);
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = ReadAsync();
        };
    }

    /// Something was copied on this computer; send this message to the others. (UI thread)
    public event Action<Msg>? Copied;

    /// Files copied on another computer were pasted here, into this folder. (UI thread)
    public event Action<DragInfo, string>? PasteFiles;

    /// The files last copied on this computer, ready for the others to fetch when they paste.
    public FileSet? LocalFiles => _localFiles;

    protected override void WndProc(ref Message m)
    {
        // Apps often put several formats on the clipboard one after another; wait until they're done.
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            _settle.Stop();
            _settle.Start();
        }
        base.WndProc(ref m);
    }

    async Task ReadAsync()
    {
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == _ownSequence) return; // what we just put there ourselves

        try
        {
            if (PrivateMarks.Any(Clipboard.ContainsData)) return; // a password manager asking not to be shared
            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
                var set = await Task.Run(() => FileSet.FromPaths(paths));
                if (set.Files.Count == 0 || GetClipboardSequenceNumber() != sequence) return;
                _localFiles = set;
                var list = set.Files.Count <= PasteData.MaxFiles ? set.Files.Select(f => new ManifestFile(f.RelativePath, f.Size)).ToList() : null;
                var info = new DragInfo(set.Id, set.Title, set.Files.Count, set.TotalBytes, list);
                Copied?.Invoke(new Msg(MsgType.Clipboard, (int)ClipKind.Files) { Payload = TransferJson.Bytes(info) });
            }
            else if (Clipboard.ContainsImage())
            {
                using var image = Clipboard.GetImage();
                if (image == null) return;
                var png = await Task.Run(() =>
                {
                    using var stream = new MemoryStream();
                    image.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                });
                if (png.Length <= MaxImage && GetClipboardSequenceNumber() == sequence)
                    Copied?.Invoke(new Msg(MsgType.Clipboard, (int)ClipKind.Image) { Payload = png });
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (text.Length > 0 && Encoding.UTF8.GetByteCount(text) <= Msg.MaxClipboard)
                    Copied?.Invoke(new Msg(MsgType.Clipboard, (int)ClipKind.Text) { Payload = Encoding.UTF8.GetBytes(text) });
            }
        }
        catch (Exception) { } // another app has the clipboard open, or the files can't be read
    }

    /// Puts what another computer copied on our clipboard. (UI thread)
    public void Apply(Msg m)
    {
        try
        {
            switch ((ClipKind)m.A)
            {
                case ClipKind.Text:
                    var text = m.PayloadText;
                    if (text.Length == 0) return;
                    Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 100);
                    _remoteFiles = null;
                    break;

                case ClipKind.Image:
                    if (m.Payload == null || !m.Payload.AsSpan().StartsWith(PngStart)) return; // PNG is all we ever send
                    using (var stream = new MemoryStream(m.Payload))
                    using (var bitmap = new Bitmap(stream))
                        Clipboard.SetDataObject(bitmap, copy: true, retryTimes: 5, retryDelay: 100);
                    _remoteFiles = null;
                    break;

                case ClipKind.Files:
                    var info = TransferJson.Read<DragInfo>(m.Payload);
                    if (!FileReceiver.IsValidId(info.Id)) return;
                    var staging = Path.Combine(Staging, info.Id);
                    bool started = false;
                    var data = PasteData.For(info, () =>
                    {
                        // The first file Explorer reads starts the transfer; the rest wait for it.
                        if (!started)
                        {
                            started = true;
                            Directory.CreateDirectory(staging);
                            PasteFiles?.Invoke(info, staging);
                        }
                        return staging;
                    });
                    // OLE has to be running on this thread first, or the offer quietly fails.
                    if (data != null && Application.OleRequired() == ApartmentState.STA && OleSetClipboard(data) == 0)
                    {
                        _offered = data;
                        _remoteFiles = new RemoteFiles(info, GetClipboardSequenceNumber(), AsFiles: true);
                        break;
                    }
                    // Their names, so pasting into a text box still gives something sensible.
                    Clipboard.SetDataObject(info.Title ?? string.Format(L.T("n_files"), info.Count), copy: true, retryTimes: 5, retryDelay: 100);
                    _remoteFiles = new RemoteFiles(info, GetClipboardSequenceNumber(), AsFiles: false);
                    break;

                default:
                    return;
            }
            _ownSequence = GetClipboardSequenceNumber();
        }
        catch (Exception) { } // clipboard busy, or a picture that won't decode
    }

    /// A key went down on this computer (its own keyboard, or one controlling it). Any thread.
    public void KeyDown(int vk)
    {
        // Explorer fetches files offered as files itself, so this is only for the other kind.
        if (vk != VK_V || _remoteFiles is not { AsFiles: false } || GetAsyncKeyState(VK_CONTROL) >= 0) return;
        _ui.Post(_ =>
        {
            // Only while the clipboard still holds those files, and only into a folder.
            if (_remoteFiles is not { } files || files.Sequence != GetClipboardSequenceNumber()) return;
            if (DropFolder.ForPaste() is { } folder) PasteFiles?.Invoke(files.Info, folder);
        }, null);
    }

    public void Dispose()
    {
        // Files offered but never fetched would be a promise nobody can keep once we're gone.
        if (_offered != null && OleIsCurrentClipboard(_offered) == 0) OleSetClipboard(null);
        _settle.Dispose();
        if (Handle == IntPtr.Zero) return;
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
