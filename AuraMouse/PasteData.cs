using System.Runtime.InteropServices;
using System.Text;
using AuraMouse.Transfers;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace AuraMouse;

/// Files copied on another computer, offered to this one's clipboard as "virtual files", the way Remote Desktop
/// does it. Explorer sees files, so its Paste menu and button work as well as Ctrl+V, and nothing is fetched until
/// something actually reads a file's contents: Explorer only does that when pasting. Then the transfer starts
/// into a staging folder, and each file is handed to Explorer once it has arrived.
[ComVisible(true)]
internal sealed class PasteData : ComTypes.IDataObject
{
    const int S_OK = 0, DATA_S_SAMEFORMATETC = 0x00040130;
    const int DV_E_FORMATETC = unchecked((int)0x80040064), DV_E_LINDEX = unchecked((int)0x80040068), OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    const int CF_UNICODETEXT = 13, FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    const uint FD_ATTRIBUTES = 0x4, FD_FILESIZE = 0x40, FD_PROGRESSUI = 0x4000;
    const int DescriptorSize = 592;

    /// How many files the clipboard can describe this way. Bigger sets still paste with Ctrl+V.
    public const int MaxFiles = 2000;

    internal static readonly short Descriptor = (short)RegisterClipboardFormat("FileGroupDescriptorW");
    internal static readonly short Contents = (short)RegisterClipboardFormat("FileContents");
    static readonly short DropEffect = (short)RegisterClipboardFormat("Preferred DropEffect");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);

    /// One line of the list Explorer sees: a folder, or a file and which file of the transfer it is.
    internal sealed record Entry(string Name, bool IsFolder, long Size, int FileIndex);

    readonly DragInfo _info;
    readonly Func<string> _start; // starts the transfer (once) and says where the files arrive
    readonly string _text;

    public IReadOnlyList<Entry> Entries { get; }

    /// What Explorer asks for, for tests.
    internal static Action<string>? Trace { get; set; }

    PasteData(DragInfo info, IReadOnlyList<Entry> entries, Func<string> start)
    {
        _info = info;
        Entries = entries;
        _start = start;
        _text = info.Title ?? string.Format(L.T("n_files"), info.Count);
    }

    /// Null if this set can't be described (no list sent, too many files, names too long): Ctrl+V still works then.
    public static PasteData? For(DragInfo info, Func<string> start)
    {
        if (info.Files is not { Count: > 0 and <= MaxFiles } files || files.Count != info.Count) return null;
        var entries = new List<Entry>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            if (files[i].Size < 0 || string.IsNullOrEmpty(files[i].Path)) return null;
            // The same cleaning the receiver uses, so the names match the files that arrive.
            var segments = SafePath.Segments(files[i].Path);
            var name = string.Join('\\', segments);
            if (name.Length >= 260) return null;
            for (int depth = 1; depth < segments.Length; depth++)
            {
                var folder = string.Join('\\', segments[..depth]);
                if (folders.Add(folder)) entries.Add(new Entry(folder, true, 0, -1));
            }
            entries.Add(new Entry(name, false, files[i].Size, i));
        }
        return new PasteData(info, entries, start);
    }

    // ───────────── what Explorer reads ─────────────

    public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        medium = default;
        Trace?.Invoke($"GetData {format.cfFormat} tymed {format.tymed} index {format.lindex}");
        if (format.cfFormat == Descriptor && Has(format, ComTypes.TYMED.TYMED_HGLOBAL))
            medium = HGlobal(DescriptorBytes());
        else if (format.cfFormat == CF_UNICODETEXT && Has(format, ComTypes.TYMED.TYMED_HGLOBAL))
            medium = HGlobal(Encoding.Unicode.GetBytes(_text + "\0"));
        else if (format.cfFormat == DropEffect && Has(format, ComTypes.TYMED.TYMED_HGLOBAL))
            medium = HGlobal(BitConverter.GetBytes(1)); // copy, not move
        else if (format.cfFormat == Contents && Has(format, ComTypes.TYMED.TYMED_ISTREAM))
        {
            if (format.lindex < 0 || format.lindex >= Entries.Count || Entries[format.lindex].IsFolder) throw new COMException(null, DV_E_LINDEX);
            var entry = Entries[format.lindex];
            // Only now, when something wants a file's bytes, does anything get fetched.
            var folder = _start();
            var stream = new ArrivingFile(Path.Combine(folder, entry.Name), entry.Size, _info.Id);
            medium = new ComTypes.STGMEDIUM
            {
                tymed = ComTypes.TYMED.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(stream, typeof(ComTypes.IStream)),
            };
        }
        else throw new COMException(null, DV_E_FORMATETC);
    }

    public int QueryGetData(ref ComTypes.FORMATETC format)
    {
        Trace?.Invoke($"QueryGetData {format.cfFormat} tymed {format.tymed} aspect {format.dwAspect} index {format.lindex}");
        bool known = format.cfFormat == Contents ? Has(format, ComTypes.TYMED.TYMED_ISTREAM)
            : (format.cfFormat == Descriptor || format.cfFormat == CF_UNICODETEXT || format.cfFormat == DropEffect) && Has(format, ComTypes.TYMED.TYMED_HGLOBAL);
        return known ? S_OK : DV_E_FORMATETC;
    }

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction)
    {
        Trace?.Invoke("EnumFormatEtc " + direction);
        if (direction != ComTypes.DATADIR.DATADIR_GET) throw new NotImplementedException();
        return new FormatList([
            Format(Descriptor, ComTypes.TYMED.TYMED_HGLOBAL, -1),
            Format(Contents, ComTypes.TYMED.TYMED_ISTREAM, -1),
            Format(DropEffect, ComTypes.TYMED.TYMED_HGLOBAL, -1),
            Format(CF_UNICODETEXT, ComTypes.TYMED.TYMED_HGLOBAL, -1),
        ]);
    }

    public int GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DATA_S_SAMEFORMATETC;
    }

    /// Explorer reports back how a paste went ("Performed DropEffect" and the like). Nothing to keep.
    public void SetData(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium, bool release)
    {
        Trace?.Invoke($"SetData {format.cfFormat}");
        if (release) ReleaseStgMedium(ref medium);
    }

    public void GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium) => throw new NotImplementedException();
    public int DAdvise(ref ComTypes.FORMATETC format, ComTypes.ADVF advf, ComTypes.IAdviseSink sink, out int connection) { connection = 0; return OLE_E_ADVISENOTSUPPORTED; }
    public void DUnadvise(int connection) => throw new COMException(null, OLE_E_ADVISENOTSUPPORTED);
    public int EnumDAdvise(out ComTypes.IEnumSTATDATA? advise) { advise = null; return OLE_E_ADVISENOTSUPPORTED; }

    static bool Has(ComTypes.FORMATETC format, ComTypes.TYMED tymed) => (format.tymed & tymed) != 0;

    static ComTypes.FORMATETC Format(short format, ComTypes.TYMED tymed, int index) => new()
    {
        cfFormat = format,
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        tymed = tymed,
    };

    /// FILEGROUPDESCRIPTORW: a count, then a 592-byte FILEDESCRIPTORW for each entry.
    byte[] DescriptorBytes()
    {
        var bytes = new byte[4 + DescriptorSize * Entries.Count];
        BitConverter.GetBytes(Entries.Count).CopyTo(bytes, 0);
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            int at = 4 + i * DescriptorSize;
            BitConverter.GetBytes(FD_ATTRIBUTES | FD_FILESIZE | FD_PROGRESSUI).CopyTo(bytes, at);
            BitConverter.GetBytes(e.IsFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL).CopyTo(bytes, at + 36);
            BitConverter.GetBytes((uint)(e.Size >> 32)).CopyTo(bytes, at + 64);
            BitConverter.GetBytes((uint)(e.Size & 0xFFFFFFFF)).CopyTo(bytes, at + 68);
            Encoding.Unicode.GetBytes(e.Name).CopyTo(bytes, at + 72);
        }
        return bytes;
    }

    static ComTypes.STGMEDIUM HGlobal(byte[] bytes)
    {
        const uint GMEM_MOVEABLE = 0x0002;
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        var at = GlobalLock(memory);
        Marshal.Copy(bytes, 0, at, bytes.Length);
        GlobalUnlock(memory);
        return new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = memory };
    }

    [ComVisible(true)]
    sealed class FormatList(ComTypes.FORMATETC[] formats) : ComTypes.IEnumFORMATETC
    {
        int _next;

        public int Next(int count, ComTypes.FORMATETC[] into, int[]? fetched)
        {
            int n = 0;
            while (n < count && _next < formats.Length) into[n++] = formats[_next++];
            if (fetched is { Length: > 0 }) fetched[0] = n;
            return n == count ? 0 : 1;
        }

        public int Skip(int count)
        {
            _next = Math.Min(formats.Length, _next + count);
            return _next < formats.Length ? 0 : 1;
        }

        public int Reset() { _next = 0; return 0; }
        public void Clone(out ComTypes.IEnumFORMATETC clone) => clone = new FormatList(formats) { _next = _next };
    }
}

/// One pasted file, read by Explorer as it arrives. It appears in the staging folder only once complete
/// (the receiver moves it there at the end), so reading waits until then, or until the transfer gives up.
[ComVisible(true)]
internal sealed class ArrivingFile(string path, long size, string transferId) : ComTypes.IStream
{
    const int STGTY_STREAM = 2, STATFLAG_NONAME = 1;
    const int E_FAIL = unchecked((int)0x80004005);
    static readonly TimeSpan NoStartLimit = TimeSpan.FromSeconds(30);

    FileStream? _file;

    /// Pastes are read on the window's thread, so while waiting the window keeps being serviced.
    FileStream File()
    {
        if (_file != null) return _file;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!System.IO.File.Exists(path))
        {
            var item = TransferCenter.Items.FirstOrDefault(t => t.Id == transferId && !t.Outgoing);
            if (item is { IsActive: false, State: not TransferState.Done } || item == null && waited.Elapsed > NoStartLimit)
                throw new COMException(null, E_FAIL); // Explorer shows that the copy failed
            Application.DoEvents();
            Thread.Sleep(30);
        }
        return _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public void Read(byte[] buffer, int count, IntPtr read)
    {
        int n = File().Read(buffer, 0, count);
        if (read != IntPtr.Zero) Marshal.WriteInt32(read, n);
        if (n == 0) _file?.Dispose();
    }

    public void Seek(long offset, int origin, IntPtr position)
    {
        long at = File().Seek(offset, (SeekOrigin)origin);
        if (position != IntPtr.Zero) Marshal.WriteInt64(position, at);
    }

    public void Stat(out ComTypes.STATSTG stat, int flags) => stat = new ComTypes.STATSTG
    {
        type = STGTY_STREAM,
        cbSize = size,
        pwcsName = (flags & STATFLAG_NONAME) != 0 ? null! : Path.GetFileName(path),
    };

    public void Clone(out ComTypes.IStream clone) => throw new NotImplementedException();
    public void Commit(int flags) { }
    public void CopyTo(ComTypes.IStream target, long count, IntPtr read, IntPtr written) => throw new NotImplementedException();
    public void LockRegion(long offset, long count, int type) => throw new NotImplementedException();
    public void Revert() { }
    public void SetSize(long newSize) => throw new NotImplementedException();
    public void UnlockRegion(long offset, long count, int type) => throw new NotImplementedException();
    public void Write(byte[] buffer, int count, IntPtr written) => throw new NotImplementedException();
}
