using System.Runtime.InteropServices;
using static AuraMouse.Native;

namespace AuraMouse;

/// This computer's own mouse and keyboard. Normally it only watches (LocalMouse/LocalKey); while
/// capturing it swallows all input and forwards it as messages, with the real cursor parked in the
/// middle of the screen so movement in every direction can be measured.
internal sealed class InputCapture : IDisposable
{
    const int CornerSize = 40; // edges near a corner don't switch, so close buttons and the Start button stay usable

    readonly HookProc _mouseProc, _keyProc; // kept as fields so the GC doesn't collect them
    readonly ChordDetector _lockChord = new(), _escapeChord = new();
    IntPtr _mouseHook, _keyHook;
    Thread? _thread;
    uint _threadId;
    volatile Action<Msg>? _sink; // set while capturing
    POINT _park;

    public InputCapture()
    {
        _mouseProc = OnMouse;
        _keyProc = OnKey;
    }

    /// A real (not injected) mouse event while not capturing. Return true to swallow it.
    public Func<int, POINT, bool>? LocalMouse { get; set; }

    /// A real key press (its virtual key) while not capturing; it still goes through.
    public Action<int>? LocalKey { get; set; }

    /// The user's way-back shortcut while capturing. Outside capturing those keys belong to other programs.
    public Action? Escape { get; set; }

    /// The user's lock shortcut was pressed, capturing or not.
    public Action? LockShortcut { get; set; }

    public bool Capturing => _sink != null;

    /// Whether this computer's own left button is held down (tracked while not capturing).
    public bool LeftButtonDown { get; private set; }

    /// The hooks get a thread of their own. Every mouse movement in Windows waits for them, so on the
    /// window's thread any busy moment (drawing, a slow clipboard) made the whole mouse stutter, and if
    /// it went on too long Windows silently removed the hooks, so the cursor stopped crossing over.
    public void Install()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
            ready.Set();

            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // hooks are called from inside this loop

            UnhookWindowsHookEx(_mouseHook);
            UnhookWindowsHookEx(_keyHook);
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "Aura Mouse input",
        };
        _thread.Start();
        ready.Wait();
    }

    public void Begin(Action<Msg> sink)
    {
        var primary = Screen.PrimaryScreen!.Bounds;
        _park = new POINT { X = primary.Left + primary.Width / 2, Y = primary.Top + primary.Height / 2 };
        _sink = sink;
        CursorHider.Hide(); // only the cursor on the computer being controlled should show
        SetCursorPos(_park.X, _park.Y);
    }

    public void End()
    {
        if (_sink == null) return;
        _sink = null;
        CursorHider.Show();
    }

    /// Which screen edge the cursor is pressed against, ignoring the corners.
    public static Edge EdgeAt(POINT pt)
    {
        var vs = SystemInformation.VirtualScreen;
        bool nearTopOrBottom = pt.Y < vs.Top + CornerSize || pt.Y >= vs.Bottom - CornerSize;
        bool nearLeftOrRight = pt.X < vs.Left + CornerSize || pt.X >= vs.Right - CornerSize;
        if (pt.X <= vs.Left && !nearTopOrBottom) return Edge.Left;
        if (pt.X >= vs.Right - 1 && !nearTopOrBottom) return Edge.Right;
        if (pt.Y <= vs.Top && !nearLeftOrRight) return Edge.Top;
        if (pt.Y >= vs.Bottom - 1 && !nearLeftOrRight) return Edge.Bottom;
        return Edge.None;
    }

    IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLMHF_INJECTED) != 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        int msg = (int)wParam;
        var sink = _sink;
        if (sink == null)
        {
            if (msg == WM_LBUTTONDOWN) LeftButtonDown = true;
            else if (msg == WM_LBUTTONUP) LeftButtonDown = false;
            return LocalMouse?.Invoke(msg, info.pt) == true ? 1 : CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
        if (msg == WM_LBUTTONUP) LeftButtonDown = false; // released while over the other computer

        // Blocked moves leave the cursor at _park, so each event's point minus _park is the movement.
        switch (msg)
        {
            case WM_MOUSEMOVE:
                int dx = info.pt.X - _park.X, dy = info.pt.Y - _park.Y;
                if (dx != 0 || dy != 0) sink(new Msg(MsgType.Move, dx, dy));
                break;
            case WM_LBUTTONDOWN: sink(new Msg(MsgType.Button, MOUSEEVENTF_LEFTDOWN)); break;
            case WM_LBUTTONUP: sink(new Msg(MsgType.Button, MOUSEEVENTF_LEFTUP)); break;
            case WM_RBUTTONDOWN: sink(new Msg(MsgType.Button, MOUSEEVENTF_RIGHTDOWN)); break;
            case WM_RBUTTONUP: sink(new Msg(MsgType.Button, MOUSEEVENTF_RIGHTUP)); break;
            case WM_MBUTTONDOWN: sink(new Msg(MsgType.Button, MOUSEEVENTF_MIDDLEDOWN)); break;
            case WM_MBUTTONUP: sink(new Msg(MsgType.Button, MOUSEEVENTF_MIDDLEUP)); break;
            case WM_MOUSEWHEEL: sink(new Msg(MsgType.Wheel, (short)(info.mouseData >> 16), 0)); break;
            case WM_MOUSEHWHEEL: sink(new Msg(MsgType.Wheel, (short)(info.mouseData >> 16), 1)); break;
        }
        return 1;
    }

    IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((k.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_keyHook, nCode, wParam, lParam);

        bool up = (k.flags & LLKHF_UP) != 0;
        var sink = _sink;
        bool capturing = sink != null;
        // Both always see every key, so each knows what's held whichever one fires.
        bool locked = _lockChord.Key((int)k.vkCode, up, capturing, Shortcuts.Lock, () => LockShortcut?.Invoke());
        bool escaped = _escapeChord.Key((int)k.vkCode, up, capturing, capturing ? Shortcuts.Escape : [], () => Escape?.Invoke());
        if (locked || escaped) return 1;

        if (sink == null)
        {
            if (!up) LocalKey?.Invoke((int)k.vkCode);
            return CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        int flags = (up ? KEYEVENTF_KEYUP : 0) | ((k.flags & LLKHF_EXTENDED) != 0 ? KEYEVENTF_EXTENDEDKEY : 0);
        sink(new Msg(MsgType.Key, (int)k.vkCode, (int)k.scanCode, flags));
        return 1;
    }

    public void Dispose()
    {
        End();
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); // the thread unhooks and exits
        _thread.Join(TimeSpan.FromSeconds(1));
        _thread = null;
    }
}

/// While this computer's mouse is controlling another one, its own cursor would sit frozen in the
/// middle of the screen. Swapping every system cursor for a blank one hides it; reloading the
/// user's cursor scheme brings them back.
internal static class CursorHider
{
    static readonly uint[] SystemCursors =
    [
        32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672,
    ];
    static int _hidden;

    public static void Hide()
    {
        if (Interlocked.Exchange(ref _hidden, 1) == 1) return;
        var and = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray(); // AND 1 + XOR 0 = transparent
        var xor = new byte[32 * 32 / 8];
        foreach (var id in SystemCursors)
            SetSystemCursor(CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor), id); // takes ownership of the handle
    }

    public static void Show()
    {
        if (Interlocked.Exchange(ref _hidden, 0) == 1) Restore();
    }

    /// Reloads the user's own cursors. Also called at start and exit, in case a previous run ended while hidden.
    public static void Restore() => SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
}

/// The actual cursor, apart so the tracking below can be tested against a pretend game.
internal interface ICursorDevice
{
    POINT Get();
    void Set(int x, int y);
    void MoveBy(int dx, int dy);
    bool OnMonitor(int x, int y);
}

internal sealed class RealCursor : ICursorDevice
{
    const uint MONITOR_DEFAULTTONULL = 0;

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    public POINT Get()
    {
        GetCursorPos(out var p);
        return p;
    }

    public void Set(int x, int y) => SetCursorPos(x, y);
    public void MoveBy(int dx, int dy) => SendMove(dx, dy);
    public bool OnMonitor(int x, int y) => MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
}

/// Plays input that arrives from another computer on this one, tracking where it put the cursor.
///
/// Games take the cursor away: they keep it inside their window, put it back in the middle every frame,
/// or (running as administrator) don't let it be moved at all. It then never reaches a screen edge, so
/// the mouse could never get back. While that's going on the crossing is worked out from where the
/// cursor would have been, and movement goes in as real mouse movement, which is what such games read.
internal sealed class InputPlayer(ICursorDevice? cursor = null)
{
    static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift, Ctrl, Alt, LWin, RWin
    const int OwnMoveTolerance = 4;
    const int GameHoldMs = 1000; // how long after the last sign of a game this keeps going

    readonly ICursorDevice _cursor = cursor ?? new RealCursor();
    POINT _pos;
    long _gameUntil;
    int _freeX, _freeY; // where the cursor would be if the game hadn't got hold of it

    public POINT Position => _pos;

    bool GameHasCursor => Environment.TickCount64 < _gameUntil;

    void GameNoticed(int x, int y)
    {
        if (!GameHasCursor) (_freeX, _freeY) = (x, y);
        _gameUntil = Environment.TickCount64 + GameHoldMs;
    }

    public void PlaceAt(Edge edge, int ratio, int inset)
    {
        var p = Net.PointOnEdge(edge, ratio, SystemInformation.VirtualScreen, inset);
        _gameUntil = 0;
        MoveTo(p.X, p.Y);
    }

    /// Carries on from wherever the cursor is now (a phone has no edge to come in from).
    public void SyncToCursor()
    {
        _pos = _cursor.Get();
        _gameUntil = 0;
    }

    /// Moves the cursor by (dx, dy). If that would cross an edge that isExit accepts, the cursor stays
    /// put and the edge is returned with the position along it; otherwise returns Edge.None.
    public Edge Move(int dx, int dy, Func<Edge, bool> isExit, out int ratio)
    {
        var vs = SystemInformation.VirtualScreen;

        // It isn't where we left it, and our own mouse being touched is handled elsewhere: something else moved it.
        var now = _cursor.Get();
        if (Math.Abs(now.X - _pos.X) > OwnMoveTolerance || Math.Abs(now.Y - _pos.Y) > OwnMoveTolerance) GameNoticed(_pos.X, _pos.Y);

        bool game = GameHasCursor;
        int nx = (game ? _freeX : _pos.X) + dx, ny = (game ? _freeY : _pos.Y) + dy;
        var crossed = nx < vs.Left ? Edge.Left
            : nx >= vs.Right ? Edge.Right
            : ny < vs.Top ? Edge.Top
            : ny >= vs.Bottom ? Edge.Bottom
            : Edge.None;

        if (crossed != Edge.None && isExit(crossed))
        {
            ratio = Net.RatioAlong(crossed, nx, ny, vs);
            _gameUntil = 0;
            return crossed;
        }

        ratio = 0;
        if (game)
        {
            _freeX = Math.Clamp(nx, vs.Left, vs.Right - 1);
            _freeY = Math.Clamp(ny, vs.Top, vs.Bottom - 1);
            int expectedX = now.X + dx, expectedY = now.Y + dy;
            _cursor.MoveBy(dx, dy);
            _pos = _cursor.Get();
            // Still held in place with room to move: the game's still at it.
            if (_pos.X == now.X && _pos.Y == now.Y && (dx != 0 || dy != 0) && _cursor.OnMonitor(expectedX, expectedY)) GameNoticed(_freeX, _freeY);
            return Edge.None;
        }

        MoveTo(nx, ny);
        // Asked for a spot on a real monitor and didn't land there, so it isn't just a gap between screens.
        if ((Math.Abs(_pos.X - nx) > OwnMoveTolerance || Math.Abs(_pos.Y - ny) > OwnMoveTolerance) && _cursor.OnMonitor(nx, ny)) GameNoticed(nx, ny);
        return Edge.None;
    }

    /// True if a mouse event at pt is just us having moved the cursor, not someone touching this mouse.
    public bool IsOwnMove(POINT pt) =>
        Math.Abs(pt.X - _pos.X) <= OwnMoveTolerance && Math.Abs(pt.Y - _pos.Y) <= OwnMoveTolerance;

    void MoveTo(int x, int y)
    {
        _cursor.Set(x, y);
        _pos = _cursor.Get(); // Windows clamps to the real monitors, so read back where we landed
    }

    public static void Play(Msg m)
    {
        switch (m.Type)
        {
            case MsgType.Button: SendMouse(m.A, 0); break;
            case MsgType.Wheel: SendMouse(m.B == 1 ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, m.A); break;
            case MsgType.Key: SendKey(m.A, m.B, m.C); break;
            case MsgType.TextInput: SendText(m.PayloadText); break;
        }
    }

    /// Avoids Ctrl/Alt/Shift staying "stuck" down when control moves to another computer.
    public static void ReleaseModifiers()
    {
        foreach (var vk in Modifiers)
            if (GetAsyncKeyState(vk) < 0) SendKey(vk, 0, KEYEVENTF_KEYUP);
    }
}
