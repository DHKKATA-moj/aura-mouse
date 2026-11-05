using System.Drawing.Drawing2D;

namespace AuraMouse.Ui;

/// Two monitors with a cursor gliding between them. When Interactive, clicking swaps
/// which side the other computer is on (the monitors hop over each other).
internal sealed class ScreensArt : AnimatedControl
{
    const float MonitorW = 150, MonitorH = 94, Gap = 92, TopY = 12;
    const float SwapSeconds = 0.5f, LoopSeconds = 3.4f;

    bool _otherOnRight = true;
    float _swappedAt = -10;

    public bool Connected { get; set; }
    public bool Interactive { get; init; }
    public string ThisLabel { get; init; } = "";
    public string OtherLabel { get; init; } = "";
    public event EventHandler? SideToggled;

    public bool OtherOnRight
    {
        get => _otherOnRight;
        set { _otherOnRight = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (Interactive) Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Interactive) return;
        _otherOnRight = !_otherOnRight;
        _swappedAt = Time;
        SideToggled?.Invoke(this, EventArgs.Empty);
    }

    protected override void Draw(Graphics g)
    {
        float leftX = W / 2 - Gap / 2 - MonitorW, rightX = W / 2 + Gap / 2;
        float thisTarget = _otherOnRight ? leftX : rightX, otherTarget = _otherOnRight ? rightX : leftX;

        float p = Theme.Ease(Math.Clamp((Time - _swappedAt) / SwapSeconds, 0, 1));
        float hop = MathF.Sin(p * MathF.PI) * 18;
        var thisRect = new RectangleF(Theme.Lerp(otherTarget, thisTarget, p), TopY - hop, MonitorW, MonitorH);
        var otherRect = new RectangleF(Theme.Lerp(thisTarget, otherTarget, p), TopY + hop * 0.6f, MonitorW, MonitorH);
        bool settled = p >= 1;

        if (settled) DrawLink(g, thisRect, otherRect);
        DrawMonitor(g, otherRect, isThis: false);
        DrawMonitor(g, thisRect, isThis: true);

        Theme.DrawText(g, ThisLabel, Theme.Font(10.5f, FontStyle.Bold), Theme.Text,
            new RectangleF(thisRect.X - 10, TopY + MonitorH + 42, MonitorW + 20, 22), wrap: false);
        Theme.DrawText(g, OtherLabel, Theme.Font(10.5f), Theme.Muted,
            new RectangleF(otherRect.X - 10, TopY + MonitorH + 42, MonitorW + 20, 22), wrap: false);

        if (Interactive && Hover && settled)
        {
            var badge = new RectangleF(W / 2 - 17, TopY + MonitorH / 2 - 52, 34, 34);
            using (var brush = Theme.AccentBrush(badge)) g.FillEllipse(brush, badge);
            Theme.DrawGlyph(g, Glyphs.Swap, 11, Color.White, badge);
        }

        if (settled) DrawCursor(g, CursorPosition(thisRect, otherRect));
    }

    PointF CursorPosition(RectangleF thisRect, RectangleF otherRect)
    {
        var from = new PointF(thisRect.X + thisRect.Width / 2, thisRect.Y + thisRect.Height / 2 - 4);
        var to = new PointF(otherRect.X + otherRect.Width / 2, otherRect.Y + otherRect.Height / 2 - 4);

        if (!Connected)
        {
            // Not connected yet: the cursor nudges towards the other screen, waiting.
            float dir = to.X > from.X ? 1 : -1;
            float nudge = (MathF.Sin(Time * 2.6f) + 1) / 2 * 22 * dir;
            return new PointF(from.X + nudge, from.Y);
        }

        // Rest, glide over, rest, glide back.
        float u = Time % LoopSeconds / LoopSeconds;
        float t = u switch
        {
            < 0.15f => 0,
            < 0.5f => Theme.Ease((u - 0.15f) / 0.35f),
            < 0.65f => 1,
            _ => 1 - Theme.Ease((u - 0.65f) / 0.35f),
        };
        float arc = MathF.Sin(t * MathF.PI) * -16;
        return new PointF(Theme.Lerp(from.X, to.X, t), Theme.Lerp(from.Y, to.Y, t) + arc);
    }

    void DrawLink(Graphics g, RectangleF a, RectangleF b)
    {
        var (left, right) = a.X < b.X ? (a, b) : (b, a);
        float x1 = left.Right + 8, x2 = right.Left - 8, y = TopY + MonitorH / 2;

        if (Connected)
        {
            var span = new RectangleF(x1, y - 2, x2 - x1, 4);
            using var brush = new LinearGradientBrush(span, Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal);
            using var pen = new Pen(brush, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, x1, y, x2, y);

            // Little packets flowing along the cable.
            for (int k = 0; k < 3; k++)
            {
                float t = (Time * 0.8f + k / 3f) % 1;
                float x = Theme.Lerp(x1, x2, t);
                using var dot = new SolidBrush(Theme.WithAlpha(Color.White, (int)(MathF.Sin(t * MathF.PI) * 230)));
                g.FillEllipse(dot, x - 3, y - 3, 6, 6);
            }
        }
        else
        {
            using var pen = new Pen(Theme.WithAlpha(Theme.Muted, 160), 2)
            {
                DashPattern = [2.5f, 2.5f],
                DashOffset = -Time * 6,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawLine(pen, x1, y, x2, y);
        }
    }

    static void DrawMonitor(Graphics g, RectangleF r, bool isThis)
    {
        // stand
        using (var standBrush = new SolidBrush(Theme.Border))
        {
            g.FillRectangle(standBrush, r.X + r.Width / 2 - 9, r.Bottom - 2, 18, 14);
            using var foot = Theme.Rounded(new RectangleF(r.X + r.Width / 2 - 32, r.Bottom + 10, 64, 7), 3.5f);
            g.FillPath(standBrush, foot);
        }

        using (var bezel = Theme.Rounded(r, 12))
        {
            using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, bezel);
            using var pen = isThis ? Theme.AccentPen(r, 1.8f) : new Pen(Theme.Border, 1.5f);
            g.DrawPath(pen, bezel);
        }

        var screen = RectangleF.Inflate(r, -7, -7);
        using (var screenPath = Theme.Rounded(screen, 7))
        using (var screenBrush = Theme.AccentBrush(screen, isThis ? 75 : 38))
            g.FillPath(screenBrush, screenPath);

        // Tiny keyboard + mouse under "this" screen hints which computer owns them.
        if (isThis)
        {
            using var kb = Theme.Rounded(new RectangleF(r.X + 8, r.Bottom + 24, 54, 9), 3);
            using var kbBrush = new SolidBrush(Theme.Border);
            g.FillPath(kbBrush, kb);
            g.FillEllipse(kbBrush, r.Right - 26, r.Bottom + 21, 11, 15);
        }
    }

    static void DrawCursor(Graphics g, PointF at)
    {
        PointF[] arrow =
        [
            new(0, 0), new(0, 17), new(4.5f, 13), new(7.5f, 19.5f),
            new(10, 18.5f), new(7, 12), new(12.5f, 12),
        ];
        using var path = new GraphicsPath();
        path.AddPolygon(arrow.Select(pt => new PointF(at.X + pt.X, at.Y + pt.Y)).ToArray());

        using (var shadow = new SolidBrush(Theme.WithAlpha(Color.Black, 90)))
        {
            g.TranslateTransform(1.5f, 2);
            g.FillPath(shadow, path);
            g.TranslateTransform(-1.5f, -2);
        }
        g.FillPath(Brushes.White, path);
        using var outline = new Pen(Theme.Background, 1.1f) { LineJoin = LineJoin.Round };
        g.DrawPath(outline, path);
    }
}
