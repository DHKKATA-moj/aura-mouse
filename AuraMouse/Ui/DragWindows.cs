using System.Runtime.InteropServices;
using System.Text;
using AuraMouse.Transfers;

namespace AuraMouse.Ui;

/// Dragging files from one computer's screen onto another's. Everything here runs on the UI thread.
internal static class DragUi
{
    static CatcherForm? _catcher;
    static GhostForm? _ghost;

    /// Puts a tiny, nearly invisible drop target under the cursor at the screen edge. If the user is
    /// dragging files, Windows hands it the file list; anything else (a window being snapped, text)
    /// never arrives, and the result is null.
    public static void Catch(Point at, Action<string[]?> done)
    {
        _catcher ??= new CatcherForm();
        _catcher.Start(at, done);
    }

    /// The little "3 files" label that follows the cursor while files are carried over.
    public static void ShowGhost(DragInfo info)
    {
        _ghost ??= new GhostForm();
        _ghost.Carry(info.Title ?? string.Format(L.T("n_files"), info.Count));
    }

    public static void HideGhost() => _ghost?.Stop();

    static NoticeForm? _notice;

    /// A short message at the top of the screen that never takes focus away from a game.
    public static void ShowNotice(string text)
    {
        _notice ??= new NoticeForm();
        _notice.Flash(text);
    }

    sealed class NoticeForm : Form
    {
        readonly TextBlock _label = new() { Dock = DockStyle.Fill, Glyph = Glyphs.Lock, FontSize = 11, Bold = true };
        readonly System.Windows.Forms.Timer _hide = new() { Interval = 1500 };

        public NoticeForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.SolidSurface;
            Controls.Add(_label);
            _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void Flash(string text)
        {
            _label.Text = text;
            var area = Screen.FromPoint(Cursor.Position).Bounds;
            // Wide enough for the text and its icon, whatever the language.
            float textWidth = TextRenderer.MeasureText(text, Theme.Font(11, FontStyle.Bold)).Width;
            int w = (int)(Math.Max(340, textWidth + 11 * 1.9f + 48) * Theme.Scale), h = (int)(48 * Theme.Scale);
            w = Math.Min(w, area.Width - 20);
            Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (int)(40 * Theme.Scale), w, h);
            if (!Visible) Show();
            _hide.Stop();
            _hide.Start();
        }
    }

    static AdminPromptForm? _adminPrompt;

    /// Says an administrator's program is in the way, with a button that fixes it. Like the notice,
    /// it never takes focus from the game, but it can be clicked.
    public static void ShowAdminPrompt(string text, Action restart)
    {
        if (_adminPrompt == null || _adminPrompt.IsDisposed) _adminPrompt = new AdminPromptForm();
        _adminPrompt.Open(text, restart);
    }

    sealed class AdminPromptForm : Form
    {
        readonly TextBlock _label = new() { FontSize = 10.5f, Bold = true }; // no icon: a label with one doesn't wrap
        readonly PillButton _restart = new() { Glyph = Glyphs.Power, Kind = ButtonKind.Primary };
        readonly PillButton _close = new() { Kind = ButtonKind.Ghost };
        readonly System.Windows.Forms.Timer _hide = new() { Interval = 15000 };
        Action? _onRestart;

        public AdminPromptForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.SolidSurface;
            Controls.AddRange([_label, _restart, _close]);
            _restart.Click += (_, _) => { Hide(); _onRestart?.Invoke(); };
            _close.Click += (_, _) => Hide();
            _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
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

        public void Open(string text, Action restart)
        {
            _onRestart = restart;
            _label.Text = text;
            _restart.Text = L.T("admin_restart"); // here, in case the language changed since last time
            _close.Text = L.T("not_now");
            BackColor = Theme.SolidSurface;
            int S(float v) => (int)(v * Theme.Scale);
            var area = Screen.PrimaryScreen!.WorkingArea;
            int w = S(460), h = S(136);
            Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + S(40), w, h);
            _label.Bounds = new Rectangle(S(16), S(10), w - S(32), S(64));
            _restart.Bounds = new Rectangle(w - S(16 + 230), S(80), S(230), S(42));
            _close.Bounds = new Rectangle(w - S(16 + 230 + 10 + 120), S(80), S(120), S(42));
            if (L.Rtl)
            {
                _restart.Left = S(16);
                _close.Left = S(16 + 230 + 10);
            }
            if (!Visible) Show();
            _hide.Stop();
            _hide.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var edge = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        }
    }

    const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    sealed class CatcherForm : Form
    {
        const int Side = 24;
        readonly System.Windows.Forms.Timer _timeout = new() { Interval = 700 };
        Action<string[]?>? _done;

        public CatcherForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 0.02; // invisible, but still a target (fully transparent windows aren't)
            BackColor = Color.Black;
            AllowDrop = true;
            _timeout.Tick += (_, _) => Finish(null);
            DragEnter += (_, e) =>
            {
                e.Effect = DragDropEffects.None;
                Finish(e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths : null);
            };
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

        public void Start(Point at, Action<string[]?> done)
        {
            _done?.Invoke(null);
            _done = done;

            var screen = Screen.FromPoint(at).Bounds;
            int x = Math.Clamp(at.X - Side / 2, screen.Left, screen.Right - Side);
            int y = Math.Clamp(at.Y - Side / 2, screen.Top, screen.Bottom - Side);
            Bounds = new Rectangle(x, y, Side, Side);
            if (!Visible) Show();

            // Nudge the cursor inward: the drag loop only looks at what's under the cursor when the
            // mouse moves, so move it (a real-looking move, which the drag loop is sure to notice).
            int nx = Math.Clamp(at.X, x + 3, x + Side - 4), ny = Math.Clamp(at.Y, y + 3, y + Side - 4);
            Native.SetCursorPos(nx, ny);
            Native.SendMove(Math.Sign(x + Side / 2 - nx), Math.Sign(y + Side / 2 - ny));
            _timeout.Stop();
            _timeout.Start();
        }

        void Finish(string[]? paths)
        {
            _timeout.Stop();
            Hide();
            var done = _done;
            _done = null;
            // The files go to the other computer; Escape ends the drag here without dropping anything.
            if (paths != null)
            {
                Native.SendKey(0x1B, 0, 0);
                Native.SendKey(0x1B, 0, Native.KEYEVENTF_KEYUP);
            }
            done?.Invoke(paths);
        }
    }

    sealed class GhostForm : Form
    {
        readonly GhostLabel _label = new() { Dock = DockStyle.Fill };
        readonly System.Windows.Forms.Timer _follow = new() { Interval = 15 };

        public GhostForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 0.95;
            BackColor = Theme.Background;
            Controls.Add(_label);
            _follow.Tick += (_, _) => FollowCursor();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT; // clicks pass through
                return cp;
            }
        }

        public void Carry(string text)
        {
            _label.Text = text;
            ClientSize = new Size((int)(220 * Theme.Scale), (int)(46 * Theme.Scale));
            FollowCursor();
            if (!Visible) Show();
            _follow.Start();
        }

        public void Stop()
        {
            _follow.Stop();
            Hide();
        }

        // Below-right of the cursor, so the cursor's tip (where the drop lands) stays uncovered.
        void FollowCursor() => Location = Cursor.Position + new Size((int)(18 * Theme.Scale), (int)(22 * Theme.Scale));
    }

    sealed class GhostLabel : CanvasControl
    {
        protected override void Draw(Graphics g)
        {
            var r = new RectangleF(1, 1, W - 2, H - 2);
            using (var path = Theme.Rounded(r, 12))
            {
                using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, path);
                using var pen = Theme.AccentPen(r, 1.6f);
                g.DrawPath(pen, path);
            }
            var icon = M(new RectangleF(8, 7, 32, 32));
            using (var iconPath = Theme.Rounded(icon, 9))
            using (var brush = Theme.AccentBrush(icon))
                g.FillPath(brush, iconPath);
            Theme.DrawGlyph(g, "", 11, Color.White, icon); // document
            Theme.DrawText(g, Text, Theme.Font(10, FontStyle.Bold), Theme.Text, M(new RectangleF(48, 0, W - 56, H)),
                StringAlignment.Near, wrap: false);
        }
    }
}

/// Watches for files being dragged against a screen edge: one attempt each time the cursor reaches
/// the edge, so holding a window against the edge (to snap it) isn't disturbed over and over.
internal sealed class DragOutProbe(SynchronizationContext? ui)
{
    volatile bool _busy, _triedThisPress;

    const int AwayFromEdge = 60;

    /// Call for every press of the left button and every move while it is held.
    public void Track(int msg, Native.POINT pt)
    {
        if (msg == Native.WM_LBUTTONDOWN) _triedThisPress = false;
        else if (_triedThisPress && msg == Native.WM_MOUSEMOVE)
        {
            // Well away from every edge again: the next time it reaches one, try again.
            var vs = SystemInformation.VirtualScreen;
            if (pt.X - vs.Left > AwayFromEdge && vs.Right - pt.X > AwayFromEdge &&
                pt.Y - vs.Top > AwayFromEdge && vs.Bottom - pt.Y > AwayFromEdge)
                _triedThisPress = false;
        }
    }

    public void Probe(Native.POINT pt, Action<string[]> onFiles)
    {
        if (ui == null || _busy || _triedThisPress) return;
        _busy = _triedThisPress = true;
        ui.Post(_ => DragUi.Catch(new Point(pt.X, pt.Y), paths =>
        {
            _busy = false;
            if (paths != null) onFiles(paths);
        }), null);
    }
}

/// Where the user let go of carried files: the desktop, an open Explorer folder, or
/// Downloads\Aura Mouse when it wasn't a folder (an app, the taskbar...).
internal static class DropFolder
{
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public Rectangle rcCaret;
    }

    const uint GA_ROOT = 2;

    public static string At(Point screen) =>
        FolderOf(GetAncestor(WindowFromPoint(screen), GA_ROOT)) ?? FileReceiver.DefaultFolder;

    /// The folder Ctrl+V would paste files into: the desktop or the Explorer window in front.
    /// Null when that's something else, or when typing in a box (renaming a file, the address bar).
    public static string? ForPaste()
    {
        var window = Native.GetForegroundWindow();
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) && ClassOf(info.hwndFocus) is "Edit" or "RichEdit20W")
            return null;
        return FolderOf(GetAncestor(window, GA_ROOT));
    }

    static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    static string? FolderOf(IntPtr window) => ClassOf(window) switch
    {
        "Progman" or "WorkerW" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "CabinetWClass" => ExplorerFolder(window),
        _ => null,
    };

    /// Asks the shell which folder an Explorer window is showing.
    static string? ExplorerFolder(IntPtr window)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic open in shell.Windows())
            {
                try
                {
                    if (new IntPtr((long)open.HWND) != window) continue;
                    string path = open.Document.Folder.Self.Path;
                    if (Directory.Exists(path)) return path;
                }
                catch { } // not a file folder (Control Panel, This PC...)
            }
        }
        catch { }
        return null;
    }
}
