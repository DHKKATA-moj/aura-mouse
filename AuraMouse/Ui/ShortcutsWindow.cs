namespace AuraMouse.Ui;

/// Shows a key combination. Clicked, it records: every key pressed until all are let go.
internal sealed class ShortcutBox : PressableControl
{
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, VK_ESCAPE = 0x1B;

    readonly HashSet<int> _held = [], _pressed = [];
    bool _recording;

    public int[] Keys { get; set; } = [];
    /// The other combination this one must not overlap.
    public Func<int[]>? Other { get; init; }
    public event Action<int[]>? Saved;
    public event Action? TooFew, Clashes;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (_recording) return;
        Focus();
        _recording = true;
        Shortcuts.Recording = true;
        _held.Clear();
        _pressed.Clear();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Stop();
    }

    // The page can be rebuilt while recording (another computer locking the mouse, say).
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_recording) Stop();
        base.OnHandleDestroyed(e);
    }

    void Stop()
    {
        if (_recording) Shortcuts.Recording = false;
        _recording = false;
        Invalidate();
    }

    /// Handled here, ahead of everything else, so Tab, Alt or F10 record like any other key.
    public override bool PreProcessMessage(ref Message m)
    {
        if (!_recording) return base.PreProcessMessage(ref m);

        if (m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            int vk = Shortcuts.Normalize((int)m.WParam);
            _held.Add(vk);
            _pressed.Add(vk);
            Invalidate();
            return true;
        }
        if (m.Msg is WM_KEYUP or WM_SYSKEYUP)
        {
            _held.Remove(Shortcuts.Normalize((int)m.WParam));
            if (_held.Count == 0) Finish();
            return true;
        }
        return base.PreProcessMessage(ref m);
    }

    void Finish()
    {
        if (_pressed.Count == 1 && _pressed.Contains(VK_ESCAPE))
        {
            Stop();
            return;
        }
        if (_pressed.Count < Shortcuts.MinKeys)
        {
            _pressed.Clear();
            TooFew?.Invoke();
            Invalidate();
            return; // still recording: try again
        }
        if (Other != null && Shortcuts.Clash(_pressed.ToArray(), Other()))
        {
            _pressed.Clear();
            Clashes?.Invoke();
            Invalidate();
            return; // still recording: try again
        }
        Keys = _pressed.ToArray();
        Saved?.Invoke(Keys);
        Stop();
    }

    protected override void Draw(Graphics g)
    {
        var r = new RectangleF(1, 1, W - 2, H - 2);
        using var path = Theme.Rounded(r, 12);
        using (var fill = new SolidBrush(Hover ? Theme.SurfaceHover : Theme.Surface)) g.FillPath(fill, path);
        using (var pen = _recording ? Theme.AccentPen(r, 1.8f) : new Pen(Theme.Border, 1.2f)) g.DrawPath(pen, path);

        var text = !_recording ? Shortcuts.Describe(Keys)
            : _pressed.Count > 0 ? Shortcuts.Describe(_pressed)
            : L.T("shortcut_press");
        Theme.DrawText(g, text, Theme.Font(_recording && _pressed.Count == 0 ? 9.5f : 10.5f, FontStyle.Bold),
            Theme.Text, r, StringAlignment.Center);
    }
}
