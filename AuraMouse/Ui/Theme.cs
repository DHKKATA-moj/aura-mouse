using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AuraMouse.Ui;

internal enum Tone { Neutral, Busy, Good, Bad }

/// Colours, fonts and drawing helpers. Controls draw in "design pixels" (96 DPI) and
/// CanvasControl scales the Graphics by Theme.Scale, so everything stays sharp on high-DPI screens.
internal static class Theme
{
    public static float Scale { get; set; } = 1;

    public static readonly Color Background = Color.FromArgb(10, 14, 24);
    public static readonly Color Surface = Color.FromArgb(20, 26, 41);
    public static readonly Color SurfaceHover = Color.FromArgb(28, 36, 56);
    public static readonly Color Border = Color.FromArgb(40, 50, 74);
    public static readonly Color Text = Color.FromArgb(235, 240, 250);
    public static readonly Color Muted = Color.FromArgb(132, 145, 174);
    public static readonly Color Accent = Color.FromArgb(34, 211, 238);
    public static readonly Color Accent2 = Color.FromArgb(37, 99, 235);
    public static readonly Color Good = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Bad = Color.FromArgb(248, 113, 113);

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
