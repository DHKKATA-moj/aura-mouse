using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AuraMouse.Remote;

/// Copies a monitor into memory, cursor included, scaled down if it's bigger than the viewer needs.
internal sealed unsafe class ScreenGrabber : IDisposable
{
    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, HALFTONE = 4, DI_NORMAL = 3, CURSOR_SHOWING = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public Native.POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);

    readonly IntPtr _screen, _memory, _bitmap, _old;

    public int Width { get; }
    public int Height { get; }
    public byte* Bits { get; }

    public ScreenGrabber(int width, int height)
    {
        Width = width;
        Height = height;
        _screen = GetDC(IntPtr.Zero);
        _memory = CreateCompatibleDC(_screen);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        _bitmap = CreateDIBSection(_memory, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Can't allocate the screen copy");
        Bits = (byte*)bits;
        _old = SelectObject(_memory, _bitmap);
        SetStretchBltMode(_memory, HALFTONE);
    }

    public void Grab(Rectangle source)
    {
        bool scaled = source.Width != Width || source.Height != Height;
        if (scaled) StretchBlt(_memory, 0, 0, Width, Height, _screen, source.X, source.Y, source.Width, source.Height, SRCCOPY | CAPTUREBLT);
        else BitBlt(_memory, 0, 0, Width, Height, _screen, source.X, source.Y, SRCCOPY | CAPTUREBLT);
        DrawCursor(source);
    }

    void DrawCursor(Rectangle source)
    {
        var cursor = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref cursor) || cursor.flags != CURSOR_SHOWING || !source.Contains(cursor.pt.X, cursor.pt.Y)) return;
        if (!GetIconInfo(cursor.hCursor, out var icon)) return;
        if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
        if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        int x = (cursor.pt.X - source.X - icon.xHotspot) * Width / source.Width;
        int y = (cursor.pt.Y - source.Y - icon.yHotspot) * Height / source.Height;
        DrawIconEx(_memory, x, y, cursor.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    public void Dispose()
    {
        SelectObject(_memory, _old);
        DeleteObject(_bitmap);
        DeleteDC(_memory);
        ReleaseDC(IntPtr.Zero, _screen);
    }
}

/// Turns screen copies into updates: only the parts that changed, as JPEG pieces.
/// Update: [int16 count] then per piece [int16 x][int16 y][int32 length][JPEG bytes].
/// Parts sent at speed are sent again sharper as soon as they stop changing, even with
/// a video playing elsewhere on the screen, so text ends up crisp.
internal sealed unsafe class FrameEncoder
{
    public const int PieceHeader = 8;
    const int Tile = 64, BandHeight = 256, SharpenAfterFrames = 4;

    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    readonly int _width, _height, _columns, _rows;
    readonly byte[] _previous;
    readonly bool[] _changed, _blurry, _sharpen;
    readonly byte[] _still; // frames since each blurry tile last changed
    bool _first = true;

    public FrameEncoder(int width, int height)
    {
        _width = width;
        _height = height;
        _columns = (width + Tile - 1) / Tile;
        _rows = (height + Tile - 1) / Tile;
        _previous = new byte[width * height * 4];
        _changed = new bool[_columns * _rows];
        _blurry = new bool[_columns * _rows];
        _sharpen = new bool[_columns * _rows];
        _still = new byte[_columns * _rows];
    }

    /// Null when there's nothing to send. A sharpQuality of 0 leaves sharpening for later.
    public byte[]? Encode(byte* bits, int quality, int sharpQuality)
    {
        FindChanges(bits);
        bool any = false, anySharp = false;
        for (int i = 0; i < _changed.Length; i++)
        {
            any |= _changed[i];
            _sharpen[i] = false;
            if (_changed[i])
            {
                _blurry[i] = true;
                _still[i] = 0;
            }
            else if (_blurry[i] && sharpQuality > 0 && ++_still[i] >= SharpenAfterFrames)
            {
                _blurry[i] = false;
                _sharpen[i] = true;
                anySharp = true;
            }
        }
        if (!any && !anySharp) return null;

        var parts = new List<(Rectangle Area, int Quality)>();
        if (any) parts.AddRange(Rectangles(_changed).Select(r => (r, quality)));
        if (anySharp) parts.AddRange(Rectangles(_sharpen).Select(r => (r, sharpQuality)));
        return Build(bits, parts);
    }

    void FindChanges(byte* bits)
    {
        int stride = _width * 4;
        fixed (byte* previous = _previous)
        {
            for (int row = 0; row < _rows; row++)
            {
                int top = row * Tile, bottom = Math.Min(top + Tile, _height);
                for (int column = 0; column < _columns; column++)
                {
                    int left = column * Tile * 4, bytes = Math.Min(Tile, _width - column * Tile) * 4;
                    bool changed = _first;
                    for (int y = top; y < bottom && !changed; y++)
                        changed = !new ReadOnlySpan<byte>(bits + y * stride + left, bytes).SequenceEqual(new ReadOnlySpan<byte>(previous + y * stride + left, bytes));
                    _changed[row * _columns + column] = changed;
                    if (!changed) continue;
                    for (int y = top; y < bottom; y++)
                        Buffer.MemoryCopy(bits + y * stride + left, previous + y * stride + left, bytes, bytes);
                }
            }
        }
        _first = false;
    }

    /// Joins neighbouring tiles, because every JPEG costs about 600 bytes of headers.
    /// Tall areas are cut into bands, which are encoded side by side on the processor's cores.
    List<Rectangle> Rectangles(bool[] tiles)
    {
        var used = new bool[tiles.Length];
        var parts = new List<Rectangle>();
        for (int row = 0; row < _rows; row++)
        {
            for (int column = 0; column < _columns; column++)
            {
                if (!tiles[row * _columns + column] || used[row * _columns + column]) continue;
                int end = column;
                while (end + 1 < _columns && tiles[row * _columns + end + 1] && !used[row * _columns + end + 1]) end++;
                int bottom = row;
                while (bottom + 1 < _rows && RowFilled(tiles, used, bottom + 1, column, end)) bottom++;
                for (int r = row; r <= bottom; r++)
                    for (int c = column; c <= end; c++) used[r * _columns + c] = true;

                int x = column * Tile, y = row * Tile;
                int w = Math.Min((end + 1) * Tile, _width) - x, h = Math.Min((bottom + 1) * Tile, _height) - y;
                for (int top = y; top < y + h; top += BandHeight)
                    parts.Add(new Rectangle(x, top, w, Math.Min(BandHeight, y + h - top)));
                column = end;
            }
        }
        return parts;
    }

    bool RowFilled(bool[] tiles, bool[] used, int row, int from, int to)
    {
        for (int c = from; c <= to; c++)
            if (!tiles[row * _columns + c] || used[row * _columns + c]) return false;
        return true;
    }

    byte[] Build(byte* bits, List<(Rectangle Area, int Quality)> parts)
    {
        var encoded = new byte[parts.Count][];
        var start = (IntPtr)bits;
        Parallel.For(0, parts.Count, i =>
        {
            var (part, quality) = parts[i];
            var first = start + part.Y * _width * 4 + part.X * 4;
            using var view = new Bitmap(part.Width, part.Height, _width * 4, PixelFormat.Format32bppRgb, first);
            using var stream = new MemoryStream(part.Width * part.Height / 8 + 1024);
            using var options = new EncoderParameters(1);
            options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            view.Save(stream, Jpeg, options);
            encoded[i] = stream.ToArray();
        });

        var update = new byte[2 + encoded.Sum(e => e.Length + PieceHeader)];
        BinaryPrimitives.WriteInt16LittleEndian(update, (short)parts.Count);
        int at = 2;
        for (int i = 0; i < parts.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(update.AsSpan(at), (short)parts[i].Area.X);
            BinaryPrimitives.WriteInt16LittleEndian(update.AsSpan(at + 2), (short)parts[i].Area.Y);
            BinaryPrimitives.WriteInt32LittleEndian(update.AsSpan(at + 4), encoded[i].Length);
            encoded[i].CopyTo(update, at + PieceHeader);
            at += PieceHeader + encoded[i].Length;
        }
        return update;
    }
}
