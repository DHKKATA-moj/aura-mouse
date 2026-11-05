using System.Drawing.Drawing2D;

namespace AuraMouse.Ui;

/// "This PC" in the middle with a slot on each edge. Connected computers sit in their slot;
/// ones with no place yet wait as chips underneath. Drag a computer onto any slot to move it.
/// The map is physical, so it is never mirrored for Arabic.
internal sealed class LayoutMap : AnimatedControl
{
    const float MonW = 126, MonH = 62, GapX = 18, GapY = 16, TopY = 6, ChipH = 28;
    const float DragThreshold = 4;

    IReadOnlyList<PeerInfo> _peers = [];
    int _pressedId = -1, _dragId = -1;
    PointF _pressAt, _mouse;

    public string ThisLabel { get; init; } = "";
    public string WhereLabel { get; init; } = "";
    public event Action<int, Edge>? PeerDropped;

    public void SetPeers(IReadOnlyList<PeerInfo> peers)
    {
        _peers = peers.Where(p => !p.IsPhone).ToList(); // phones have no place around the screen
        Invalidate();
    }

    RectangleF Center => new(W / 2 - MonW / 2, TopY + MonH + GapY, MonW, MonH);

    RectangleF Slot(Edge edge)
    {
        var c = Center;
        return edge switch
        {
            Edge.Left => new RectangleF(c.X - GapX - MonW, c.Y, MonW, MonH),
            Edge.Right => new RectangleF(c.Right + GapX, c.Y, MonW, MonH),
            Edge.Top => new RectangleF(c.X, TopY, MonW, MonH),
            _ => new RectangleF(c.X, c.Bottom + GapY, MonW, MonH),
        };
    }

    static readonly Edge[] AllEdges = [Edge.Left, Edge.Right, Edge.Top, Edge.Bottom];

    List<(PeerInfo Peer, RectangleF Rect)> Chips()
    {
        var waiting = _peers.Where(p => p.Edge == Edge.None).ToList();
        var result = new List<(PeerInfo, RectangleF)>();
        if (waiting.Count == 0) return result;

        var font = Theme.Font(9.5f, FontStyle.Bold);
        var widths = waiting.Select(p => Math.Min(200, TextRenderer.MeasureText(p.Name + " · " + WhereLabel, font).Width + 40f)).ToList();
        float total = widths.Sum() + 10 * (waiting.Count - 1);
        float x = Math.Max(0, (W - total) / 2), y = Slot(Edge.Bottom).Bottom + 12;
        for (int i = 0; i < waiting.Count; i++)
        {
            result.Add((waiting[i], new RectangleF(x, y, widths[i], ChipH)));
            x += widths[i] + 10;
        }
        return result;
    }

    int PeerAt(PointF p)
    {
        foreach (var peer in _peers)
            if (peer.Edge != Edge.None && Slot(peer.Edge).Contains(p)) return peer.Id;
        foreach (var (peer, rect) in Chips())
            if (rect.Contains(p)) return peer.Id;
        return -1;
    }

    /// The computer under a screen point (for files dropped onto the map), or -1.
    public int PeerAtScreen(Point screen) => PeerAt(ToDesign(PointToClient(screen)));

    Edge SlotAt(PointF p) => AllEdges.FirstOrDefault(e => RectangleF.Inflate(Slot(e), 8, 8).Contains(p), Edge.None);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressAt = ToDesign(e.Location);
        _pressedId = PeerAt(_pressAt);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = ToDesign(e.Location);
        if (_pressedId >= 0 && _dragId < 0 &&
            Math.Abs(_mouse.X - _pressAt.X) + Math.Abs(_mouse.Y - _pressAt.Y) > DragThreshold)
            _dragId = _pressedId;

        Cursor = _dragId >= 0 ? Cursors.SizeAll : PeerAt(_mouse) >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragId >= 0)
        {
            var edge = SlotAt(ToDesign(e.Location));
            if (edge != Edge.None) PeerDropped?.Invoke(_dragId, edge);
        }
        _pressedId = _dragId = -1;
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        var center = Center;
        var dragged = _peers.FirstOrDefault(p => p.Id == _dragId);
        var dropTarget = _dragId >= 0 ? SlotAt(_mouse) : Edge.None;

        foreach (var edge in AllEdges)
        {
            var peer = _peers.FirstOrDefault(p => p.Edge == edge && p.Id != _dragId);
            var slot = Slot(edge);
            if (peer.Name != null) DrawLink(g, center, slot, edge, peer.Active);

            if (edge == dropTarget) DrawSlot(g, slot, highlight: true);
            else if (peer.Name != null)
            {
                DrawMonitor(g, slot, peer.Name, peer.Active, isThis: false);
                if (peer.Driving) DrawDriverBadge(g, slot);
            }
            else DrawSlot(g, slot, highlight: false);
        }

        DrawMonitor(g, center, ThisLabel, active: _peers.All(p => !p.Active), isThis: true);

        foreach (var (peer, rect) in Chips())
            if (peer.Id != _dragId) DrawChip(g, rect, peer.Name);

        if (dragged.Name != null)
        {
            var ghost = new RectangleF(_mouse.X - MonW / 2, _mouse.Y - MonH / 2, MonW, MonH);
            var state = g.Save();
            g.TranslateTransform(2, 3);
            using (var shadowPath = Theme.Rounded(ghost, 10))
            using (var shadow = new SolidBrush(Theme.WithAlpha(Color.Black, 90)))
                g.FillPath(shadow, shadowPath);
            g.Restore(state);
            DrawMonitor(g, ghost, dragged.Name, active: true, isThis: false);
        }
    }

    void DrawLink(Graphics g, RectangleF center, RectangleF slot, Edge edge, bool active)
    {
        var (from, to) = edge switch
        {
            Edge.Left => (new PointF(center.Left - 4, center.Y + MonH / 2), new PointF(slot.Right + 4, slot.Y + MonH / 2)),
            Edge.Right => (new PointF(center.Right + 4, center.Y + MonH / 2), new PointF(slot.Left - 4, slot.Y + MonH / 2)),
            Edge.Top => (new PointF(center.X + MonW / 2, center.Top - 4), new PointF(slot.X + MonW / 2, slot.Bottom + 4)),
            _ => (new PointF(center.X + MonW / 2, center.Bottom + 4), new PointF(slot.X + MonW / 2, slot.Top - 4)),
        };

        using var pen = active
            ? Theme.AccentPen(RectangleF.FromLTRB(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X) + 1, Math.Max(from.Y, to.Y) + 1), 3)
            : new Pen(Theme.WithAlpha(Theme.Accent, 110), 2);
        pen.StartCap = pen.EndCap = LineCap.Round;
        g.DrawLine(pen, from, to);

        if (!active) return;
        // Packets flowing towards the computer that has the mouse.
        for (int k = 0; k < 2; k++)
        {
            float t = (Time * 1.4f + k / 2f) % 1;
            var p = new PointF(Theme.Lerp(from.X, to.X, t), Theme.Lerp(from.Y, to.Y, t));
            using var dot = new SolidBrush(Theme.WithAlpha(Color.White, (int)(MathF.Sin(t * MathF.PI) * 230)));
            g.FillEllipse(dot, p.X - 3, p.Y - 3, 6, 6);
        }
    }

    void DrawSlot(Graphics g, RectangleF r, bool highlight)
    {
        using var path = Theme.Rounded(r, 10);
        if (highlight)
        {
            using var fill = Theme.AccentBrush(r, 45);
            g.FillPath(fill, path);
        }
        using var pen = highlight
            ? Theme.AccentPen(r, 2)
            : new Pen(Theme.WithAlpha(Theme.Muted, 70), 1.4f) { DashPattern = [3, 3] };
        g.DrawPath(pen, path);
        Theme.DrawGlyph(g, "", 11, highlight ? Theme.Text : Theme.WithAlpha(Theme.Muted, 90), r); // plus
    }

    void DrawMonitor(Graphics g, RectangleF r, string name, bool active, bool isThis)
    {
        using (var bezel = Theme.Rounded(r, 10))
        {
            using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, bezel);
            using var pen = active ? Theme.AccentPen(r, 2) : new Pen(Theme.Border, 1.4f);
            g.DrawPath(pen, bezel);
        }

        var screen = RectangleF.Inflate(r, -5, -5);
        using (var screenPath = Theme.Rounded(screen, 6))
        using (var screenBrush = Theme.AccentBrush(screen, isThis ? 85 : active ? 70 : 32))
            g.FillPath(screenBrush, screenPath);

        if (isThis) Theme.DrawGlyph(g, Glyphs.Mouse, 10, Theme.WithAlpha(Color.White, 170), new RectangleF(screen.X, screen.Y + 4, screen.Width, 20));
        // Computer names are drawn left-to-right even in Arabic, so a long name is cut at its end.
        Theme.DrawText(g, name, Theme.Font(8.5f, FontStyle.Bold), Theme.Text,
            new RectangleF(screen.X + 3, isThis ? screen.Y + 22 : screen.Y, screen.Width - 6, isThis ? screen.Height - 24 : screen.Height),
            wrap: false, rightToLeft: isThis ? null : false);

        if (active && !isThis)
            using (var dot = new SolidBrush(Theme.Good)) g.FillEllipse(dot, r.Right - 13, r.Y + 7, 6, 6);
    }

    /// A small mouse badge on the computer whose own mouse and keyboard are in control.
    void DrawDriverBadge(Graphics g, RectangleF monitor)
    {
        float pulse = (MathF.Sin(Time * 3) + 1) / 2;
        var badge = new RectangleF(monitor.X - 8, monitor.Y - 8, 24, 24);
        using (var glow = new SolidBrush(Theme.WithAlpha(Theme.Good, 50 + (int)(pulse * 60))))
            g.FillEllipse(glow, RectangleF.Inflate(badge, 3, 3));
        using (var fill = new SolidBrush(Theme.Good)) g.FillEllipse(fill, badge);
        Theme.DrawGlyph(g, Glyphs.Mouse, 8, Theme.Background, badge);
    }

    void DrawChip(Graphics g, RectangleF r, string name)
    {
        float pulse = (MathF.Sin(Time * 4) + 1) / 2;
        using var path = Theme.Rounded(r, r.Height / 2);
        using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, path);
        using (var tint = new SolidBrush(Theme.WithAlpha(Theme.Warn, 18))) g.FillPath(tint, path);
        using (var pen = new Pen(Theme.WithAlpha(Theme.Warn, 90 + (int)(pulse * 110)), 1.4f)) g.DrawPath(pen, path);

        Theme.DrawGlyph(g, Glyphs.Monitor, 9, Theme.Warn, new RectangleF(r.X + 8, r.Y, 20, r.Height));
        Theme.DrawText(g, name + " · " + WhereLabel, Theme.Font(9.5f, FontStyle.Bold), Theme.Text,
            new RectangleF(r.X + 30, r.Y, r.Width - 38, r.Height), StringAlignment.Near, wrap: false);
    }
}
