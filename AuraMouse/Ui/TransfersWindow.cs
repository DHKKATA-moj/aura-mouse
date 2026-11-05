using System.Diagnostics;
using AuraMouse.Transfers;

namespace AuraMouse.Ui;

/// Small window in the bottom corner listing transfers with progress, speed and time left.
/// It appears on its own when a transfer starts and never steals focus.
internal sealed class TransfersWindow : Form
{
    const float DesignWidth = 420, MaxRows = 5;

    readonly TransferList _list = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    public TransfersWindow()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Background;
        Controls.Add(_list);
        _timer.Tick += (_, _) => RefreshList();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = Theme.Current.DarkTitleBar ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    public void ShowNearCorner(Form owner)
    {
        RefreshList();
        if (_list.Count == 0) return;
        if (!Visible)
        {
            Show(owner);
            var area = Screen.FromControl(owner).WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        }
        _timer.Start();
    }

    void RefreshList()
    {
        Text = L.T("transfers_title");
        var items = TransferCenter.Items;
        foreach (var item in items) item.SampleSpeed();
        _list.SetItems(items);

        int rows = (int)Math.Min(Math.Max(items.Count, 1), MaxRows);
        var size = new Size((int)(DesignWidth * Theme.Scale), (int)((rows * (TransferList.RowHeight + TransferList.RowGap) + 12) * Theme.Scale));
        if (ClientSize != size)
        {
            int bottom = Bottom;
            ClientSize = size;
            if (Visible) Top = bottom - Height; // grow upwards, staying in the corner
        }
        _list.Bounds = new Rectangle(Point.Empty, ClientSize);

        if (items.Count == 0)
        {
            _timer.Stop();
            Hide();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // just hide; it comes back with the next transfer
            _timer.Stop();
            Hide();
        }
        base.OnFormClosing(e);
    }
}

internal sealed class TransferList : CanvasControl
{
    public const float RowHeight = 92, RowGap = 8;

    IReadOnlyList<TransferItem> _items = [];
    readonly List<(RectangleF Rect, Action Action)> _buttons = [];
    int _hoverButton = -1;

    public int Count => _items.Count;

    public void SetItems(IReadOnlyList<TransferItem> items)
    {
        _items = items;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToDesign(e.Location);
        int hit = _buttons.FindIndex(b => b.Rect.Contains(p));
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        if (hit != _hoverButton) { _hoverButton = hit; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var p = ToDesign(e.Location);
        var hit = _buttons.FirstOrDefault(b => b.Rect.Contains(p));
        hit.Action?.Invoke();
    }

    protected override void Draw(Graphics g)
    {
        _buttons.Clear();
        for (int i = 0; i < _items.Count && i < 5; i++)
            DrawRow(g, _items[i], new RectangleF(8, 8 + i * (RowHeight + RowGap), W - 16, RowHeight));
    }

    void DrawRow(Graphics g, TransferItem item, RectangleF r)
    {
        using (var path = Theme.Rounded(r, 14))
        {
            using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, path);
            using var pen = new Pen(Theme.Border, 1.2f);
            g.DrawPath(pen, path);
        }

        var tone = item.State switch
        {
            TransferState.Done => Theme.Good,
            TransferState.Failed => Theme.Bad,
            TransferState.Canceled => Theme.Muted,
            TransferState.Retrying or TransferState.Waiting => Theme.Warn,
            _ => Theme.Accent,
        };

        // Arrow icon: up for sending, down for receiving.
        var icon = M(new RectangleF(r.X + 12, r.Y + 12, 36, 36));
        using (var iconPath = Theme.Rounded(icon, 10))
        using (var iconBrush = Theme.AccentBrush(icon, item.IsActive ? 255 : 90))
            g.FillPath(iconBrush, iconPath);
        Theme.DrawGlyph(g, item.Outgoing ? "" : "", 12, Color.White, icon);

        float textX = r.X + 58, textW = r.Width - 58 - 76;
        var title = item.Title ?? string.Format(L.T("n_files"), item.FileCount);
        Theme.DrawText(g, title, Theme.Font(10.5f, FontStyle.Bold), Theme.Text, M(new RectangleF(textX, r.Y + 10, textW, 22)),
            StringAlignment.Near, wrap: false);
        var files = item.FileCount == 1 ? L.T("one_file") : string.Format(L.T("n_files"), item.FileCount);
        var who = string.Format(L.T(item.Outgoing ? "to_peer" : "from_peer"), item.PeerName);
        Theme.DrawText(g, $"{who} · {files}", Theme.Font(9), Theme.Muted, M(new RectangleF(textX, r.Y + 31, textW, 18)),
            StringAlignment.Near, wrap: false);

        // Progress bar.
        var bar = new RectangleF(r.X + 12, r.Y + 58, r.Width - 24, 6);
        using (var track = Theme.Rounded(bar, 3))
        using (var trackBrush = new SolidBrush(Theme.Border))
            g.FillPath(trackBrush, track);
        double fraction = item.TotalBytes > 0 ? Math.Clamp(item.DoneBytes / (double)item.TotalBytes, 0, 1) : item.State == TransferState.Done ? 1 : 0;
        if (fraction > 0)
        {
            var filled = M(new RectangleF(bar.X, bar.Y, Math.Max(6, (float)(bar.Width * fraction)), bar.Height));
            using var fillPath = Theme.Rounded(filled, 3);
            using Brush fillBrush = item.State == TransferState.Transferring ? Theme.AccentBrush(filled) : new SolidBrush(tone);
            g.FillPath(fillBrush, fillPath);
        }

        Theme.DrawText(g, Describe(item, fraction), Theme.Font(8.5f), item.State == TransferState.Failed ? Theme.Bad : Theme.Muted,
            M(new RectangleF(r.X + 12, r.Y + 68, r.Width - 24, 18)), StringAlignment.Near, wrap: false);

        // Buttons on the far side: cancel while running; open folder / dismiss when finished.
        float bx = r.Right - 40;
        if (item.IsActive)
            AddButton(g, M(new RectangleF(bx, r.Y + 14, 30, 30)), "", L.T("cancel"), item.RequestCancel);
        else
        {
            AddButton(g, M(new RectangleF(bx, r.Y + 14, 30, 30)), "", L.T("dismiss"), () => TransferCenter.Remove(item));
            if (!item.Outgoing && item.State == TransferState.Done && item.Folder != null)
            {
                var folder = item.Folder;
                AddButton(g, M(new RectangleF(bx - 36, r.Y + 14, 30, 30)), "", L.T("open_folder"),
                    () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }));
            }
        }
    }

    void AddButton(Graphics g, RectangleF rect, string glyph, string label, Action action)
    {
        bool hover = _buttons.Count == _hoverButton;
        _buttons.Add((rect, action));
        if (hover)
            using (var brush = new SolidBrush(Theme.SurfaceHover)) g.FillEllipse(brush, rect);
        Theme.DrawGlyph(g, glyph, 9.5f, hover ? Theme.Text : Theme.Muted, rect);
    }

    static string Describe(TransferItem item, double fraction) => item.State switch
    {
        TransferState.Waiting => L.T("state_waiting"),
        TransferState.Retrying => $"{(int)(fraction * 100)}% · {L.T("state_retrying")}",
        TransferState.Done => $"{L.T("state_done")} · {Format.Bytes(item.TotalBytes)}",
        TransferState.Canceled => L.T("state_canceled"),
        TransferState.Failed => L.T(item.ErrorKey ?? "err_generic"),
        _ => Progress(item, fraction),
    };

    static string Progress(TransferItem item, double fraction)
    {
        var text = $"{(int)(fraction * 100)}% · {string.Format(L.T("of"), Format.Bytes(item.DoneBytes), Format.Bytes(item.TotalBytes))}";
        if (item.BytesPerSecond > 1024)
        {
            text += $" · {Format.Bytes((long)item.BytesPerSecond)}‎/s";
            var secondsLeft = (item.TotalBytes - item.DoneBytes) / item.BytesPerSecond;
            text += " · " + string.Format(L.T("time_left"), Format.Duration(secondsLeft));
        }
        return text;
    }
}

internal static class Format
{
    // Left-to-right marks keep "4 MB" and "1:05" in order inside Arabic sentences.
    static string Ltr(string s) => L.Rtl ? $"‎{s}‎" : s;

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return Ltr(unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}");
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Min(seconds, 359999)));
        return Ltr(t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}");
    }
}
