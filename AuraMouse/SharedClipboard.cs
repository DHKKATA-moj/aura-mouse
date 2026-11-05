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
/// right away: the others get their names, and pressing Ctrl+V on the desktop or in an Explorer
/// window fetches them into that folder, like dragged files. Lives on the UI (STA) thread.
internal sealed class SharedClipboard : NativeWindow, IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D, VK_CONTROL = 0x11, VK_V = 0x56;
    const int MaxImage = Msg.MaxPayload - 1024;
    static readonly string[] PrivateMarks = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];
    static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    sealed record RemoteFiles(DragInfo Info, uint Sequence);

    readonly SynchronizationContext _ui = SynchronizationContext.Current!;
    readonly System.Windows.Forms.Timer _settle = new() { Interval = 120 };
    uint _ownSequence; // the clipboard as we last filled it ourselves (from another computer)
    volatile RemoteFiles? _remoteFiles;
    volatile FileSet? _localFiles;

    public SharedClipboard()
    {
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
                var info = new DragInfo(set.Id, set.Title, set.Files.Count, set.TotalBytes);
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
                    // Their names, so pasting into a text box still gives something sensible.
                    Clipboard.SetDataObject(info.Title ?? string.Format(L.T("n_files"), info.Count), copy: true, retryTimes: 5, retryDelay: 100);
                    _remoteFiles = new RemoteFiles(info, GetClipboardSequenceNumber());
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
        if (vk != VK_V || _remoteFiles == null || GetAsyncKeyState(VK_CONTROL) >= 0) return;
        _ui.Post(_ =>
        {
            // Only while the clipboard still holds those files, and only into a folder.
            if (_remoteFiles is not { } files || files.Sequence != GetClipboardSequenceNumber()) return;
            if (DropFolder.ForPaste() is { } folder) PasteFiles?.Invoke(files.Info, folder);
        }, null);
    }

    public void Dispose()
    {
        _settle.Dispose();
        if (Handle == IntPtr.Zero) return;
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
