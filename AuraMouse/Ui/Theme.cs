using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AuraMouse.Ui;

internal enum Tone { Neutral, Busy, Good, Bad }

internal enum ThemeId { Dark, Light, Glass, GlassDark }

/// Colours, fonts and drawing helpers. Controls draw in "design pixels" (96 DPI) and
/// CanvasControl scales the Graphics by Theme.Scale, so everything stays sharp on high-DPI screens.
internal static class Theme
{
    public static float Scale { get; set; } = 1;

    /// A soft patch of colour on the window's background. Position and size are fractions of the window.
    internal readonly record struct Glow(Color Color, int Alpha, float X, float Y, float W, float H);

    internal sealed record Palette(
        ThemeId Id, Color Background, Color Surface, Color SurfaceHover, Color Border, Color Text, Color Muted,
        Color Accent, Color Accent2, Color Good, Color Warn, Color Bad, Color Sidebar, bool DarkTitleBar, bool IsGlass, Glow[] Glows);

    static Color C(int r, int g, int b, int a = 255) => Color.FromArgb(a, r, g, b);

    static readonly Palette[] Palettes =
    [
        new(ThemeId.Dark, C(10, 14, 24), C(20, 26, 41), C(28, 36, 56), C(40, 50, 74), C(235, 240, 250), C(132, 145, 174),
            C(34, 211, 238), C(37, 99, 235), C(52, 211, 153), C(251, 191, 36), C(248, 113, 113), C(20, 26, 41, 170), true, false,
            [new(C(34, 211, 238), 48, -0.45f, -0.35f, 1.1f, 0.8f), new(C(37, 99, 235), 42, 0.35f, 0.55f, 1.0f, 0.8f)]),
        new(ThemeId.Light, C(241, 245, 250), C(255, 255, 255), C(232, 240, 250), C(203, 213, 225), C(15, 23, 42), C(82, 96, 120),
            C(8, 145, 178), C(37, 99, 235), C(5, 150, 105), C(180, 83, 9), C(220, 38, 38), C(255, 255, 255, 190), false, false,
            [new(C(34, 211, 238), 40, -0.45f, -0.35f, 1.1f, 0.8f), new(C(37, 99, 235), 26, 0.35f, 0.55f, 1.0f, 0.8f)]),
        new(ThemeId.Glass, C(205, 222, 245), C(255, 255, 255, 105), C(255, 255, 255, 150), C(255, 255, 255, 170), C(12, 20, 40), C(50, 62, 88),
            C(8, 120, 170), C(58, 80, 225), C(4, 120, 87), C(170, 75, 5), C(200, 30, 45), C(255, 255, 255, 70), false, true,
            [new(C(56, 189, 248), 245, -0.35f, -0.35f, 1.15f, 1.0f), new(C(192, 132, 252), 235, 0.35f, -0.3f, 1.0f, 0.95f),
             new(C(251, 146, 180), 225, 0.45f, 0.4f, 0.95f, 0.95f), new(C(45, 212, 191), 225, -0.3f, 0.45f, 1.05f, 0.95f), new(C(253, 224, 171), 170, 0.1f, 0.15f, 0.7f, 0.6f)]),
        new(ThemeId.GlassDark, C(14, 16, 40), C(255, 255, 255, 30), C(255, 255, 255, 52), C(255, 255, 255, 62), C(245, 248, 255), C(190, 200, 225),
            C(103, 232, 249), C(99, 102, 241), C(74, 222, 128), C(253, 186, 116), C(252, 129, 129), C(255, 255, 255, 18), true, true,
            [new(C(37, 99, 235), 235, -0.4f, -0.4f, 1.2f, 1.05f), new(C(147, 51, 234), 225, 0.35f, -0.3f, 1.05f, 0.95f),
             new(C(219, 39, 119), 170, 0.5f, 0.45f, 0.95f, 0.9f), new(C(13, 148, 136), 205, -0.35f, 0.45f, 1.05f, 0.95f)]),
    ];

    static string SettingsFile => Path.Combine(App.DataFolder, "theme.txt");

    public static Palette Current { get; private set; } = Palettes[0];

    public static Color Background => Current.Background;
    public static Color Surface => Current.Surface;
    public static Color SurfaceHover => Current.SurfaceHover;
    public static Color Border => Current.Border;
    public static Color Text => Current.Text;
    public static Color Muted => Current.Muted;
    public static Color Accent => Current.Accent;
    public static Color Accent2 => Current.Accent2;
    public static Color Good => Current.Good;
    public static Color Warn => Current.Warn;
    public static Color Bad => Current.Bad;

    /// Surface without see-through, for real windows and boxes, which can't be translucent.
    public static Color SolidSurface
    {
        get
        {
            var s = Surface;
            int Mix(int top, int under) => (top * s.A + under * (255 - s.A)) / 255;
            return Color.FromArgb(Mix(s.R, Background.R), Mix(s.G, Background.G), Mix(s.B, Background.B));
        }
    }

    public static Palette PaletteOf(ThemeId id) => Palettes.First(p => p.Id == id);

    public static void Set(ThemeId id) => Current = PaletteOf(id);

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFile) && Enum.TryParse<ThemeId>(File.ReadAllText(SettingsFile).Trim(), out var id) && Enum.IsDefined(id)) Set(id);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            File.WriteAllText(SettingsFile, Current.Id.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// The light catching a pane of glass: a sheen over its top half and a bright rim. Nothing in other themes.
    public static void GlassShine(Graphics g, RectangleF r, float radius)
    {
        if (!Current.IsGlass || r.Width < 2 || r.Height < 2) return;
        bool dark = Current.DarkTitleBar;

        var top = new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, Math.Max(1, r.Height * 0.48f));
        using (var sheenPath = Rounded(top, Math.Min(radius, top.Height / 2)))
        using (var sheen = new LinearGradientBrush(new RectangleF(top.X, top.Y - 1, top.Width, top.Height + 2), Color.FromArgb(dark ? 62 : 130, Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical))
            g.FillPath(sheen, sheenPath);

        using var rimPath = Rounded(r, radius);
        using var rimBrush = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(dark ? 190 : 240, Color.White), Color.FromArgb(dark ? 38 : 110, Color.White), LinearGradientMode.Vertical);
        using var rim = new Pen(rimBrush, 1.15f);
        g.DrawPath(rim, rimPath);
    }

    static readonly string IconFamily =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    static readonly Dictionary<(string, float, FontStyle), Font> Fonts = [];
    static readonly Dictionary<float, Font> IconFonts = [];
    static readonly Dictionary<(string, float, float), Font> RealFonts = [];

    /// Font sized in points, for drawing inside a CanvasControl (design pixels).
    public static Font Font(float points, FontStyle style = FontStyle.Regular)
    {
        var family = L.Current.Font; // some languages need a font of their own
        if (!Fonts.TryGetValue((family, points, style), out var f))
            Fonts[(family, points, style)] = f = new Font(family, points * 96f / 72f, style, GraphicsUnit.Pixel);
        return f;
    }

    public static Font Icon(float points)
    {
        if (!IconFonts.TryGetValue(points, out var f))
            IconFonts[points] = f = new Font(IconFamily, points * 96f / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        return f;
    }

    /// Font for real child controls (like TextBox) that are not drawn through the scaled Graphics.
    public static Font RealFont(float points)
    {
        var family = L.Current.Font;
        if (!RealFonts.TryGetValue((family, points, Scale), out var f))
            RealFonts[(family, points, Scale)] = f = new Font(family, points * 96f / 72f * Scale, FontStyle.Regular, GraphicsUnit.Pixel);
        return f;
    }

    public static Color ToneColor(Tone tone) => tone switch
    {
        Tone.Good => Good,
        Tone.Bad => Bad,
        Tone.Busy => Warn,
        _ => Accent,
    };

    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c);

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static LinearGradientBrush AccentBrush(RectangleF r, int alpha = 255)
    {
        if (r.Width < 1) r.Width = 1;
        if (r.Height < 1) r.Height = 1;
        return new LinearGradientBrush(r, WithAlpha(Accent, alpha), WithAlpha(Accent2, alpha), LinearGradientMode.ForwardDiagonal);
    }

    public static Pen AccentPen(RectangleF r, float width, int alpha = 255)
    {
        using var brush = AccentBrush(r, alpha);
        return new Pen(brush, width);
    }

    public static void DrawText(Graphics g, string text, Font font, Color color, RectangleF r,
        StringAlignment align = StringAlignment.Center, StringAlignment line = StringAlignment.Center, bool wrap = true,
        bool? rightToLeft = null)
    {
        using var format = new StringFormat { Alignment = align, LineAlignment = line, Trimming = StringTrimming.EllipsisCharacter };
        if (!wrap) format.FormatFlags |= StringFormatFlags.NoWrap;
        if (rightToLeft ?? L.Rtl) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, r, format);
    }

    public static void DrawGlyph(Graphics g, string glyph, float points, Color color, RectangleF r)
    {
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var brush = new SolidBrush(color);
        g.DrawString(glyph, Icon(points), brush, r, format);
    }

    public static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            Prepare(g);
            DrawLogo(g, new RectangleF(0, 0, 64, 64));
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// A mouse with a wave of colour rising through it. The same drawing as branding/logo.svg.
    public static void DrawLogo(Graphics g, RectangleF r)
    {
        var state = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 100, r.Height / 100);

        var all = new RectangleF(0, 0, 100, 100);
        using (var square = Rounded(all, 24))
        using (var sea = new LinearGradientBrush(all, Color.FromArgb(34, 211, 238), Color.FromArgb(37, 99, 235), LinearGradientMode.ForwardDiagonal))
            g.FillPath(sea, square);

        using var body = Rounded(new RectangleF(29, 12, 42, 76), 21);
        g.FillPath(Brushes.White, body);
        var outside = g.Clip;
        g.SetClip(body, CombineMode.Intersect);
        Wave(g, 56, Color.FromArgb(56, 189, 248));
        Wave(g, 68, Color.FromArgb(37, 99, 235));
        g.Clip = outside;

        using var wheel = Rounded(new RectangleF(47, 24, 6, 14), 3);
        using var wheelBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
        g.FillPath(wheelBrush, wheel);
        g.Restore(state);
    }

    static void Wave(Graphics g, float y, Color color)
    {
        using var wave = new GraphicsPath();
        wave.AddBezier(29, y, 36, y - 8, 43, y - 8, 50, y);
        wave.AddBezier(50, y, 57, y + 8, 64, y + 8, 71, y);
        wave.AddLine(71, y, 71, 90);
        wave.AddLine(71, 90, 29, 90);
        wave.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, wave);
    }

    public static float Ease(float t) => t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// Segoe Fluent Icons / MDL2 Assets code points.
internal static class Glyphs
{
    public const string Power = "\uE7E8";
    public const string Delete = "\uE74D";
    public const string Settings = "\uE713";
    public const string Computers = "\uE977";
    public const string Mouse = "";
    public const string Monitor = "";
    public const string Keyboard = "";
    public const string Globe = "";
    public const string Lock = "";
    public const string Stop = "";
    public const string Back = "";
    public const string Forward = "";
    public const string ChevronRight = "";
    public const string ChevronLeft = "";
    public const string Check = "";
    public const string Swap = "";
    public const string Link = "";
    public const string Wifi = "";
    public const string Upload = "";
    public const string Download = "";
}
