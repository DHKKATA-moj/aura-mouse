using System.Runtime.InteropServices;
using AuraMouse.Remote;
using static AuraMouse.Native;

namespace AuraMouse.Ui;

/// Someone asks to come in. It appears without taking the keyboard, and Allow can't be pressed for the
/// first moment, so a key or click meant for something else can't let anyone in by accident.
internal sealed class RemoteApprovalForm : Form
{
    const float DesignW = 440, DesignH = 300, Pad = 24;
    const int ArmMs = 1500, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    readonly TaskCompletionSource<RemoteAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CheckBox _trust;
    readonly System.Windows.Forms.Timer _arm = new() { Interval = ArmMs };

    public Task<RemoteAnswer> Answer => _answer.Task;

    public RemoteApprovalForm(RemoteRequest request)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        var area = Screen.PrimaryScreen!.WorkingArea;
        int w = S(DesignW), h = S(DesignH);
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);

        Add(new TextBlock { Text = string.Format(L.T(request.IsPhone ? "remote_ask_phone" : "remote_ask"), request.Name), FontSize = 12, Bold = true }, Pad, 18, DesignW - 2 * Pad, 60);
        Add(new TextBlock { Text = string.Format(L.T("remote_ask_sub"), request.Ip), FontSize = 10, Color = Theme.Muted }, Pad, 80, DesignW - 2 * Pad, 40);
        Add(new TextBlock { Text = L.T("remote_code_label"), FontSize = 9.5f, Color = Theme.Muted }, Pad, 122, DesignW - 2 * Pad, 22);
        Add(new TextBlock { Text = request.Code, FontSize = 17, Bold = true, Color = Theme.Accent }, Pad, 144, DesignW - 2 * Pad, 34);

        _trust = new CheckBox { Text = L.T("remote_trust_box"), ForeColor = Theme.Text, BackColor = Theme.Surface, Font = Theme.RealFont(10), RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No };
        Add(_trust, Pad, 184, DesignW - 2 * Pad, 34);

        var decline = Add(new PillButton { Text = L.T("remote_decline"), Glyph = Glyphs.Stop, Kind = ButtonKind.Danger }, Pad, 234, 186, 46);
        var allow = Add(new PillButton { Text = L.T("remote_allow"), Glyph = Glyphs.Check, Kind = ButtonKind.Primary, Enabled = false }, DesignW - Pad - 186, 234, 186, 46);
        decline.Click += (_, _) => Finish(false);
        allow.Click += (_, _) => Finish(true);
        _arm.Tick += (_, _) =>
        {
            _arm.Stop();
            allow.Enabled = true;
            allow.Invalidate();
        };
        _arm.Start();
    }

    /// Nobody answered, or the other side gave up.
    public void Withdraw()
    {
        _answer.TrySetCanceled();
        Close();
    }

    void Finish(bool allow)
    {
        _answer.TrySetResult(new RemoteAnswer(allow, allow && _trust.Checked));
        Close();
    }

    T Add<T>(T control, float x, float y, float w, float h) where T : Control
    {
        control.Bounds = new Rectangle(S(x), S(y), S(w), S(h));
        Controls.Add(control);
        return control;
    }

    static int S(float v) => (int)MathF.Round(v * Theme.Scale);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _arm.Dispose();
        base.Dispose(disposing);
    }
}

/// Always on screen while someone is viewing, including when they came in without asking.
internal sealed class RemoteBanner : Form
{
    const float DesignW = 460, DesignH = 54;
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    public RemoteBanner(string viewer, Action end)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(120, 28, 28);
        var area = Screen.PrimaryScreen!.WorkingArea;
        int w = (int)(DesignW * Theme.Scale), h = (int)(DesignH * Theme.Scale);
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top, w, h);

        int buttonW = (int)(120 * Theme.Scale), pad = (int)(8 * Theme.Scale);
        Controls.Add(new TextBlock
        {
            Text = string.Format(L.T("remote_banner"), viewer),
            FontSize = 10.5f,
            Bold = true,
            Glyph = Glyphs.Monitor,
            Bounds = L.Rtl ? new Rectangle(buttonW + 2 * pad, 0, w - buttonW - 3 * pad, h) : new Rectangle(pad, 0, w - buttonW - 3 * pad, h),
        });
        var stop = new PillButton
        {
            Text = L.T("remote_end"),
            Glyph = Glyphs.Stop,
            Kind = ButtonKind.Danger,
            Bounds = new Rectangle(L.Rtl ? pad : w - buttonW - pad, pad, buttonW, h - 2 * pad),
        };
        stop.Click += (_, _) => end();
        Controls.Add(stop);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }
}

/// While the viewer window is in front, every key goes to the other computer, Alt+Tab and the Windows key
/// included. On a thread of its own, like the mouse sharing's hooks, because every key press in Windows waits for it.
internal sealed class RemoteKeyGrabber : IDisposable
{
    readonly HookProc _proc;
    readonly Func<bool> _active;
    readonly Action<Msg> _send;
    readonly HashSet<(int Vk, int Scan, int Flags)> _down = [];
    readonly Thread _thread;
    uint _threadId;
    IntPtr _hook;

    public RemoteKeyGrabber(Func<bool> active, Action<Msg> send)
    {
        _proc = OnKey;
        _active = active;
        _send = send;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "Aura Mouse remote keys",
        };
        _thread.Start();
        ready.Wait();
    }

    IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        bool up = (k.flags & LLKHF_UP) != 0;
        var key = ((int)k.vkCode, (int)k.scanCode, (k.flags & LLKHF_EXTENDED) != 0 ? KEYEVENTF_EXTENDEDKEY : 0);

        lock (_down)
        {
            // A key held when the window lost focus still has to come up on this side.
            if ((k.flags & LLKHF_INJECTED) != 0 || (!_active() && !_down.Contains(key))) return CallNextHookEx(_hook, nCode, wParam, lParam);
            if (up) _down.Remove(key);
            else _down.Add(key);
        }
        _send(new Msg(MsgType.Key, key.Item1, key.Item2, key.Item3 | (up ? KEYEVENTF_KEYUP : 0)));
        return 1;
    }

    /// The window went to the background: nothing should stay held down over there.
    public void ReleaseAll()
    {
        lock (_down)
        {
            foreach (var (vk, scan, flags) in _down) _send(new Msg(MsgType.Key, vk, scan, flags | KEYEVENTF_KEYUP));
            _down.Clear();
        }
    }

    public void Dispose()
    {
        ReleaseAll();
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}

/// Draws the other screen, fitted to the window, and turns the mouse into positions on that picture.
internal sealed class RemoteView : Control
{
    const int Full = 65535;

    public RemoteCanvas? Canvas { get; set; }
    public Action<int, int>? PointerMoved { get; set; }
    public Action<int>? Button { get; set; }
    public Action<int>? Wheel { get; set; }

    public RemoteView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.Selectable, true);
        BackColor = Color.Black;
        Cursor = Cursors.Cross;
    }

    Rectangle Fitted(Size picture)
    {
        if (picture.IsEmpty) return ClientRectangle;
        float scale = Math.Min((float)ClientSize.Width / picture.Width, (float)ClientSize.Height / picture.Height);
        int w = (int)(picture.Width * scale), h = (int)(picture.Height * scale);
        return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var size = Canvas?.Size ?? Size.Empty;
        var target = Fitted(size);
        using (var outside = new Region(ClientRectangle))
        {
            if (!size.IsEmpty) outside.Exclude(target);
            using var black = new SolidBrush(Color.Black);
            e.Graphics.FillRegion(black, outside);
        }
        Canvas?.Draw(e.Graphics, target);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    void SendPointer(Point p)
    {
        var target = Fitted(Canvas?.Size ?? Size.Empty);
        if (target.Width <= 1 || target.Height <= 1) return;
        PointerMoved?.Invoke(Math.Clamp((p.X - target.X) * Full / (target.Width - 1), 0, Full), Math.Clamp((p.Y - target.Y) * Full / (target.Height - 1), 0, Full));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SendPointer(e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        SendPointer(e.Location);
        if (e.Button == MouseButtons.Left) Button?.Invoke(MOUSEEVENTF_LEFTDOWN);
        else if (e.Button == MouseButtons.Right) Button?.Invoke(MOUSEEVENTF_RIGHTDOWN);
        else if (e.Button == MouseButtons.Middle) Button?.Invoke(MOUSEEVENTF_MIDDLEDOWN);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        SendPointer(e.Location);
        if (e.Button == MouseButtons.Left) Button?.Invoke(MOUSEEVENTF_LEFTUP);
        else if (e.Button == MouseButtons.Right) Button?.Invoke(MOUSEEVENTF_RIGHTUP);
        else if (e.Button == MouseButtons.Middle) Button?.Invoke(MOUSEEVENTF_MIDDLEUP);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Wheel?.Invoke(e.Delta);
    }
}

/// The window showing another computer.
internal sealed class RemoteViewerForm : Form
{
    const int VK_LWIN = 0x5B;
    const float BarHeight = 46;

    readonly System.Net.IPAddress _ip;
    readonly RemoteComputer? _saved;
    readonly RemoteView _view = new() { Dock = DockStyle.Fill };
    readonly Panel _bar = new() { Dock = DockStyle.Top };
    readonly TextBlock _status = new() { Dock = DockStyle.Fill, FontSize = 13, Bold = true, BackColor = Color.Black };
    readonly TextBlock _title = new() { FontSize = 10.5f, Bold = true };
    readonly PillButton _monitor = new() { Text = L.T("remote_monitor"), Glyph = Glyphs.Monitor };
    RemoteLink? _link;
    RemoteKeyGrabber? _keys;
    int _monitors = 1, _monitorIndex;
    int _repaintQueued;
    FormWindowState _windowedState;
    bool _fullScreen;

    public RemoteViewerForm(System.Net.IPAddress ip, string name, RemoteComputer? saved)
    {
        _ip = ip;
        _saved = saved;
        Text = name + " - Aura Mouse";
        Icon = Theme.CreateAppIcon();
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen!.WorkingArea;
        ClientSize = new Size(area.Width * 3 / 4, area.Height * 3 / 4);
        KeyPreview = false;

        _bar.Height = S(BarHeight);
        _bar.BackColor = Theme.Surface;
        var end = AddToBar(new PillButton { Text = L.T("remote_end"), Glyph = Glyphs.Stop, Kind = ButtonKind.Danger }, 8, 118);
        end.Click += (_, _) => Close();
        var full = AddToBar(new PillButton { Text = L.T("remote_full_screen"), Glyph = Glyphs.Monitor }, 134, 128);
        full.Click += (_, _) => ToggleFullScreen();
        var win = AddToBar(new PillButton { Text = "Win", Glyph = Glyphs.Keyboard }, 270, 86);
        win.Click += (_, _) =>
        {
            _link?.Send(new Msg(MsgType.Key, VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY));
            _link?.Send(new Msg(MsgType.Key, VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP));
            _view.Focus();
        };
        AddToBar(_monitor, 364, 118);
        _monitor.Visible = false;
        _monitor.Click += (_, _) =>
        {
            _monitorIndex = (_monitorIndex + 1) % _monitors;
            _link?.Send(new Msg(MsgType.RemoteMonitor, _monitorIndex));
            _view.Focus();
        };
        AddToBar(_title, 490, 300);
        _title.Text = name;

        Controls.Add(_view);
        Controls.Add(_status);
        Controls.Add(_bar);
        _view.Visible = false;
        _status.BringToFront();

        _view.PointerMoved = (x, y) => _link?.Pointer(x, y);
        _view.Button = flags => _link?.Send(new Msg(MsgType.Button, flags));
        _view.Wheel = delta => _link?.Send(new Msg(MsgType.Wheel, delta));
        Connect();
    }

    PillButton AddToBar(PillButton button, float x, float w)
    {
        button.Bounds = new Rectangle(S(x), S(5), S(w), S(BarHeight - 10));
        _bar.Controls.Add(button);
        return button;
    }

    void AddToBar(TextBlock text, float x, float w)
    {
        text.Bounds = new Rectangle(S(x), 0, S(w), S(BarHeight));
        _bar.Controls.Add(text);
    }

    static int S(float v) => (int)MathF.Round(v * Theme.Scale);

    void Connect()
    {
        SetStatus(L.T("remote_connecting"));
        var link = new RemoteLink(_ip, Environment.MachineName, _saved);
        _link = link;
        _view.Canvas = link.Canvas;
        link.Waiting += code => RunOnUi(link, () => SetStatus(string.Format(L.T("remote_waiting"), code)));
        link.Accepted += (name, monitors) => RunOnUi(link, () =>
        {
            Text = name + " - Aura Mouse";
            _title.Text = name;
            _title.Invalidate();
            _monitors = monitors;
            _monitor.Visible = monitors > 1;
            _status.Visible = false;
            _view.Visible = true;
            _view.BringToFront();
            _view.Focus();
            _keys ??= new RemoteKeyGrabber(() => IsForeground, m => _link?.Send(m));
        });
        link.PictureChanged += () =>
        {
            // Pictures can arrive faster than the window draws; one repaint covers them all.
            if (Interlocked.Exchange(ref _repaintQueued, 1) == 0) RunOnUi(link, () =>
            {
                Interlocked.Exchange(ref _repaintQueued, 0);
                _view.Invalidate();
            });
        };
        link.Ended += reason => RunOnUi(link, () => OnEnded(reason));
        link.Start();
    }

    volatile bool _foreground;
    bool IsForeground => _foreground;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _foreground = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _foreground = false;
        _keys?.ReleaseAll();
    }

    void OnEnded(RemoteEnd reason)
    {
        _keys?.Dispose();
        _keys = null;
        _view.Visible = false;
        _status.Visible = true;
        _status.BringToFront();
        SetStatus(L.T(reason switch
        {
            RemoteEnd.Declined => "remote_declined",
            RemoteEnd.Busy => "remote_busy",
            RemoteEnd.Unreachable => "remote_unreachable",
            _ => "remote_lost",
        }));
    }

    void SetStatus(string text)
    {
        _status.Text = text;
        _status.Invalidate();
    }

    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _windowedState = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _windowedState;
        }
        _view.Focus();
    }

    void RunOnUi(RemoteLink from, Action action)
    {
        if (IsDisposed || !IsHandleCreated || _link != from) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { } // closing
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _keys?.Dispose();
        _keys = null;
        _link?.Dispose();
        _link = null;
        base.OnFormClosed(e);
    }
}
