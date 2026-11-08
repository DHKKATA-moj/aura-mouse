namespace AuraMouse;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool startedWithWindows = args.Contains(AutoStart.Argument);
        bool restarted = Elevation.WaitForOldCopy(args); // as administrator
        bool forGame = restarted && args.Contains(Elevation.ForGameArgument);
        App.MoveOldData();

        // It lives in the tray, so opening it again just brings up the one already running.
        using var instance = new Mutex(true, @"Local\Aura Mouse", out bool first);
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Aura Mouse.Show");
        if (!first)
        {
            if (!startedWithWindows) showRequest.Set();
            return;
        }

        // Chosen once for a game: from then on every start, including with Windows, goes straight to administrator.
        if (!restarted && !Elevation.IsAdmin && Elevation.Wanted && Elevation.StartAsAdmin(startedWithWindows ? AutoStart.Argument : "")) return;

        // A game loading uses every core; the mouse passing through here shouldn't have to wait behind it.
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; }
        catch (System.ComponentModel.Win32Exception) { }

        // The cursor is hidden while controlling another computer; make sure it always comes back.
        CursorHider.Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CursorHider.Show();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => CursorHider.Show();

        AutoStart.Refresh();
        ApplicationConfiguration.Initialize();
        var form = new MainForm(startHidden: startedWithWindows || forGame, resumeLast: startedWithWindows || forGame || AutoStart.Enabled);
        if (forGame)
        {
            // It starts in the tray, so the window's own events never come; say so once things are running.
            var said = new System.Windows.Forms.Timer { Interval = 800 };
            said.Tick += (_, _) => { said.Dispose(); Ui.DragUi.ShowNotice(L.T("admin_now")); };
            said.Start();
        }
        form.StartRemote();
        new Thread(() =>
        {
            while (showRequest.WaitOne()) form.BringBack();
        }) { IsBackground = true }.Start();
        Application.Run(form);
        CursorHider.Show();
    }
}
