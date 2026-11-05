using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace AuraMouse.Ui;

/// Base for every custom control: transparent over the window backdrop, double buffered,
/// and drawn in design pixels (W x H) with the Graphics already scaled for the current DPI.
internal abstract class CanvasControl : Control
{
    protected bool Hover, Pressed;

    protected CanvasControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected float W => Width / Theme.Scale;
    protected float H => Height / Theme.Scale;

    /// Mirrors a rectangle horizontally when the UI is in Arabic, so layouts read right-to-left.
    protected RectangleF M(RectangleF r) => L.Rtl ? new RectangleF(W - r.Right, r.Y, r.Width, r.Height) : r;

    protected PointF ToDesign(Point p) => new(p.X / Theme.Scale, p.Y / Theme.Scale);

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Prepare(e.Graphics);
        e.Graphics.ScaleTransform(Theme.Scale, Theme.Scale);
        Draw(e.Graphics);
    }

    protected abstract void Draw(Graphics g);

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hover = Pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

/// A CanvasControl that redraws ~60 times a second while Animating is true.
internal abstract class AnimatedControl : CanvasControl
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    readonly Stopwatch _clock = Stopwatch.StartNew();

    protected AnimatedControl() => _timer.Tick += (_, _) => { if (Animating) Invalidate(); };

    protected float Time => (float)_clock.Elapsed.TotalSeconds;
    protected virtual bool Animating => true;

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _timer.Start(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// A clickable control that also works from the keyboard (Tab, then Enter or Space).
internal abstract class PressableControl : CanvasControl
{
    protected PressableControl()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
    }

    protected void DrawFocusRing(Graphics g, RectangleF r, float radius)
    {
        if (!Focused || !ShowFocusCues) return;
        using var path = Theme.Rounded(RectangleF.Inflate(r, -2, -2), radius - 2);
        using var pen = new Pen(Theme.WithAlpha(Color.White, 150), 1.5f) { DashStyle = DashStyle.Dot };
        g.DrawPath(pen, path);
    }
}

internal sealed class TextBlock : CanvasControl
{
    public float FontSize { get; init; } = 11;
    public bool Bold { get; init; }
    public Color Color { get; set; } = Theme.Text;
    public StringAlignment Align { get; init; } = StringAlignment.Center;
    public string Glyph { get; init; } = "";

    protected override void Draw(Graphics g)
    {
        var font = Theme.Font(FontSize, Bold ? FontStyle.Bold : FontStyle.Regular);
        if (Glyph.Length == 0)
        {
            Theme.DrawText(g, Text, font, Color, new RectangleF(0, 0, W, H), Align);
            return;
        }

        // Icon + text, centred together as one group.
        float textWidth = g.MeasureString(Text, font).Width + 2;
        float glyphWidth = FontSize * 1.9f;
        float start = (W - glyphWidth - textWidth) / 2;
        Theme.DrawGlyph(g, Glyph, FontSize, Color, M(new RectangleF(start, 0, glyphWidth, H)));
        Theme.DrawText(g, Text, font, Color, M(new RectangleF(start + glyphWidth, 0, textWidth, H)), StringAlignment.Near, wrap: false);
    }
}

internal sealed class BrandMark : CanvasControl
{
    protected override void Draw(Graphics g)
    {
        Theme.DrawLogo(g, M(new RectangleF(0, 2, 36, 36)));

        Theme.DrawText(g, "Aura Mouse", Theme.Font(14, FontStyle.Bold), Theme.Text, M(new RectangleF(46, 0, W - 46, H)),
            StringAlignment.Near, wrap: false);
    }
}

internal enum ButtonKind { Primary, Ghost, Danger }

internal sealed class PillButton : PressableControl
{
    public string Glyph { get; init; } = "";
    public ButtonKind Kind { get; init; } = ButtonKind.Ghost;

    protected override void Draw(Graphics g)
    {
        var r = new RectangleF(0.75f, 0.75f, W - 1.5f, H - 1.5f);
        using var path = Theme.Rounded(r, r.Height / 2);
        int dim = Enabled ? 255 : 90;
        Color fore;

        switch (Kind)
        {
            case ButtonKind.Primary:
                using (var brush = Theme.AccentBrush(r, dim)) g.FillPath(brush, path);
                if (Hover && Enabled)
                    using (var shine = new SolidBrush(Theme.WithAlpha(Color.White, Pressed ? 10 : 35))) g.FillPath(shine, path);
                fore = Theme.WithAlpha(Color.White, dim);
                break;

            case ButtonKind.Danger:
                using (var brush = new SolidBrush(Theme.WithAlpha(Theme.Bad, Hover ? 55 : 28))) g.FillPath(brush, path);
                using (var pen = new Pen(Theme.WithAlpha(Theme.Bad, 130), 1.2f)) g.DrawPath(pen, path);
                fore = Theme.Bad;
                break;

            default:
                using (var brush = new SolidBrush(Hover && Enabled ? Theme.SurfaceHover : Theme.Surface)) g.FillPath(brush, path);
                using (var pen = new Pen(Theme.Border, 1.2f)) g.DrawPath(pen, path);
                fore = Theme.WithAlpha(Theme.Text, dim);
                break;
        }

        Theme.GlassShine(g, r, r.Height / 2);

        var font = Theme.Font(11, FontStyle.Bold);
        float textWidth = g.MeasureString(Text, font).Width + 2;
        float glyphWidth = Glyph.Length > 0 ? 24 : 0;
        float start = (W - textWidth - glyphWidth) / 2;
        float nudge = Pressed ? 1 : 0;
        if (Glyph.Length > 0)
            Theme.DrawGlyph(g, Glyph, 10.5f, fore, M(new RectangleF(start, nudge, glyphWidth - 4, H)));
        Theme.DrawText(g, Text, font, fore, M(new RectangleF(start + glyphWidth, nudge, textWidth, H)), StringAlignment.Near, wrap: false);

        DrawFocusRing(g, r, r.Height / 2);
    }
}

/// A page in the sidebar: icon and name, with the current page filled in.
internal sealed class SidebarItem : PressableControl
{
    public string Glyph { get; init; } = "";
    public bool Selected { get; init; }

    protected override void Draw(Graphics g)
    {
        var r = new RectangleF(1, 1, W - 2, H - 2);
        if (Selected || Hover)
        {
            using var path = Theme.Rounded(r, 12);
            using var fill = new SolidBrush(Selected ? Theme.SurfaceHover : Theme.Surface);
            g.FillPath(fill, path);
            if (Selected)
            {
                using var pen = Theme.AccentPen(r, 1.4f);
                g.DrawPath(pen, path);
            }
            Theme.GlassShine(g, r, 12);
        }
        var fore = Selected ? Theme.Text : Theme.Muted;
        Theme.DrawGlyph(g, Glyph, 11.5f, Selected ? Theme.Accent : fore, M(new RectangleF(10, 0, 32, H)));
        Theme.DrawText(g, Text, Theme.Font(11, FontStyle.Bold), fore, M(new RectangleF(46, 0, W - 54, H)), StringAlignment.Near, wrap: false);
        DrawFocusRing(g, r, 12);
    }
}

/// Big card on the first screen: gradient icon, title, subtitle and a chevron.
internal sealed class ChoiceCard : PressableControl
{
    public string Glyph { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";

    protected override void Draw(Graphics g)
    {
        float lift = Hover ? -2 : 0;
        var r = new RectangleF(1, 3 + lift, W - 2, H - 6);

        if (Hover)
        {
            // soft glow under the card
            using var glowPath = Theme.Rounded(RectangleF.Inflate(r, 2, 2), 20);
            using var glow = Theme.AccentPen(r, 4, 45);
            g.DrawPath(glow, glowPath);
        }

        using (var path = Theme.Rounded(r, 18))
        {
            using (var fill = new SolidBrush(Hover ? Theme.SurfaceHover : Theme.Surface)) g.FillPath(fill, path);
            using var border = Hover ? Theme.AccentPen(r, 1.5f) : new Pen(Theme.Border, 1.2f);
            g.DrawPath(border, path);
        }
        Theme.GlassShine(g, r, 18);

        var icon = M(new RectangleF(20, r.Y + (r.Height - 54) / 2, 54, 54));
        using (var iconPath = Theme.Rounded(icon, 16))
        using (var iconBrush = Theme.AccentBrush(icon, Hover ? 255 : 215))
            g.FillPath(iconBrush, iconPath);
        Theme.GlassShine(g, icon, 16);
        Theme.DrawGlyph(g, Glyph, 18, Color.White, icon);

        float textX = 92, textW = W - textX - 52;
        Theme.DrawText(g, Title, Theme.Font(13.5f, FontStyle.Bold), Theme.Text,
            M(new RectangleF(textX, r.Y + r.Height / 2 - 27, textW, 28)), StringAlignment.Near, StringAlignment.Far, wrap: false);
        Theme.DrawText(g, Subtitle, Theme.Font(10), Theme.Muted,
            M(new RectangleF(textX, r.Y + r.Height / 2 + 2, textW, 24)), StringAlignment.Near, StringAlignment.Near, wrap: false);

        var chevron = M(new RectangleF(W - 46 + (Hover ? 4 : 0), r.Y, 24, r.Height));
        Theme.DrawGlyph(g, L.Rtl ? Glyphs.ChevronLeft : Glyphs.ChevronRight, 11, Hover ? Theme.Accent : Theme.Muted, chevron);

        DrawFocusRing(g, r, 18);
    }
}

/// Digit tiles. Shared by the read-only code display and the code input.
internal static class CodeTiles
{
    public const int Digits = 6;

    public static void Draw(Graphics g, float w, float h, string code, int active, bool caret)
    {
        const float gap = 12;
        float tile = (w - gap * (Digits - 1)) / Digits;
        for (int i = 0; i < Digits; i++)
        {
            var r = new RectangleF(i * (tile + gap) + 1, 1, tile - 2, h - 2);
            using var path = Theme.Rounded(r, 14);
            using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, path);

            bool filled = i < code.Length;
            using var pen = i == active ? Theme.AccentPen(r, 2.2f)
                : filled ? Theme.AccentPen(r, 1.5f, 150)
                : new Pen(Theme.Border, 1.5f);
            g.DrawPath(pen, path);
            Theme.GlassShine(g, r, 14);

            if (filled)
            {
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var brush = new SolidBrush(Theme.Text);
                g.DrawString(code[i].ToString(), Theme.Font(25, FontStyle.Bold), brush, r, format);
            }
            else if (i == active && caret)
            {
                using var caretPen = new Pen(Theme.Accent, 2);
                float cx = r.X + r.Width / 2;
                g.DrawLine(caretPen, cx, r.Y + r.Height * 0.3f, cx, r.Y + r.Height * 0.7f);
            }
        }
    }
}

internal sealed class CodeDisplay : AnimatedControl
{
    string _code = "";
    float _changedAt = -10;

    public string Code
    {
        get => _code;
        set { if (_code != value && _code.Length > 0) _changedAt = Time; _code = value; Invalidate(); }
    }

    protected override bool Animating => Time - _changedAt < 0.6f;

    protected override void Draw(Graphics g)
    {
        // A new code "pops" so the user notices it changed.
        float t = Math.Clamp((Time - _changedAt) / 0.6f, 0, 1);
        float scale = 1 + 0.12f * MathF.Sin(t * MathF.PI);
        g.TranslateTransform(W / 2, H / 2);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-W / 2, -H / 2);
        CodeTiles.Draw(g, W, H, _code, -1, false);
    }
}

/// Type the code straight into the tiles. Accepts Arabic-Indic digits and paste.
internal sealed class CodeInput : AnimatedControl
{
    string _code = "";

    public CodeInput()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.IBeam;
    }

    public event EventHandler? CodeChanged;

    public string Code { get => _code; set { _code = value; Invalidate(); } }

    protected override bool Animating => Focused;

    void Change(string code)
    {
        if (code == _code) return;
        Code = code;
        CodeChanged?.Invoke(this, EventArgs.Empty);
    }

    static string Digits(string text) =>
        new(text.Where(char.IsDigit).Select(c => (char)('0' + (int)char.GetNumericValue(c))).ToArray());

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        e.Handled = true;
        if (e.KeyChar == '\b') { if (_code.Length > 0) Change(_code[..^1]); return; }
        var digit = Digits(e.KeyChar.ToString());
        if (digit.Length == 1 && _code.Length < CodeTiles.Digits) Change(_code + digit);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V))
        {
            string pasted;
            try { pasted = Clipboard.GetText(); } catch { pasted = ""; }
            var all = _code + Digits(pasted);
            Change(all[..Math.Min(all.Length, CodeTiles.Digits)]);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Draw(Graphics g)
    {
        int active = Focused && _code.Length < CodeTiles.Digits ? _code.Length : -1;
        CodeTiles.Draw(g, W, H, _code, active, Time % 1 < 0.55f);
    }
}

/// Coloured status bar with a dot that pulses while something is in progress.
internal sealed class StatusPill : AnimatedControl
{
    string _message = "";
    Tone _tone;

    public void Set(string message, Tone tone)
    {
        _message = message;
        _tone = tone;
        Invalidate();
    }

    protected override bool Animating => _tone == Tone.Busy;

    protected override void Draw(Graphics g)
    {
        var color = Theme.ToneColor(_tone);
        var r = new RectangleF(0.75f, 0.75f, W - 1.5f, H - 1.5f);
        using (var path = Theme.Rounded(r, 16))
        {
            using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, path);
            using (var tint = new SolidBrush(Theme.WithAlpha(color, 22))) g.FillPath(tint, path);
            using var pen = new Pen(Theme.WithAlpha(color, 90), 1.2f);
            g.DrawPath(pen, path);
        }
        Theme.GlassShine(g, r, 16);

        float cx = L.Rtl ? W - 24 : 24, cy = H / 2;
        if (_tone == Tone.Busy)
        {
            float phase = Time * 1.1f % 1;
            float halo = 5 + phase * 10;
            using var haloBrush = new SolidBrush(Theme.WithAlpha(color, (int)((1 - phase) * 140)));
            g.FillEllipse(haloBrush, cx - halo, cy - halo, halo * 2, halo * 2);
        }
        using (var dot = new SolidBrush(color)) g.FillEllipse(dot, cx - 5, cy - 5, 10, 10);

        Theme.DrawText(g, _message, Theme.Font(10.5f), Theme.Text, M(new RectangleF(44, 4, W - 60, H - 8)), StringAlignment.Near);
    }
}

/// One theme to choose: a small picture of it, drawn in that theme's own colours, and its name.
internal sealed class ThemeSwatch : PressableControl
{
    public Theme.Palette Palette { get; init; } = Theme.Current;
    public bool Selected { get; init; }

    protected override void Draw(Graphics g)
    {
        var shown = Theme.Current;
        var picture = new RectangleF(2, 2, W - 4, 60);
        var previous = g.Clip;
        using (var frame = Theme.Rounded(picture, 10))
        {
            g.SetClip(frame, CombineMode.Intersect);
            using (var fill = new SolidBrush(Palette.Background)) g.FillPath(fill, frame);
            foreach (var glow in Palette.Glows)
            {
                using var ellipse = new GraphicsPath();
                ellipse.AddEllipse(picture.X + glow.X * picture.Width, picture.Y + glow.Y * picture.Height, glow.W * picture.Width, glow.H * picture.Height);
                using var brush = new PathGradientBrush(ellipse) { CenterColor = Theme.WithAlpha(glow.Color, glow.Alpha), SurroundColors = [Color.FromArgb(0, glow.Color)] };
                g.FillPath(brush, ellipse);
            }
            g.Clip = previous;

            var pill = new RectangleF(picture.X + 12, picture.Y + 16, picture.Width - 24, 28);
            using var pillPath = Theme.Rounded(pill, 14);
            using (var surface = new SolidBrush(Palette.Surface)) g.FillPath(surface, pillPath);
            using (var edge = new Pen(Palette.Border, 1.1f)) g.DrawPath(edge, pillPath);
            Theme.Set(Palette.Id); // only while drawing the picture: the shine belongs to that theme
            Theme.GlassShine(g, pill, 14);
            Theme.Set(shown.Id);
            using var dot = new SolidBrush(Palette.Accent);
            g.FillEllipse(dot, L.Rtl ? pill.Right - 20 : pill.X + 9, pill.Y + 9, 10, 10);
            using var line = new SolidBrush(Theme.WithAlpha(Palette.Text, 170));
            g.FillRectangle(line, L.Rtl ? pill.X + 12 : pill.X + 26, pill.Y + 12, pill.Width - 38, 4);

            using var ring = Selected ? Theme.AccentPen(picture, 2.4f) : new Pen(Theme.Border, 1.1f);
            g.DrawPath(ring, frame);
        }

        Theme.DrawText(g, Text, Theme.Font(9.5f, Selected ? FontStyle.Bold : FontStyle.Regular), Selected ? Theme.Text : Theme.Muted, new RectangleF(0, 64, W, H - 64));
        DrawFocusRing(g, new RectangleF(0, 0, W, H), 10);
    }
}
