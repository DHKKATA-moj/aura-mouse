namespace AuraMouse;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool startedWithWindows = args.Contains(AutoStart.Argument);
        App.MoveOldData();

        // Run as administrator, so the mouse works over games that are: switched on or off in settings,
        // and once on, an ordinary start hands over to the task that starts it that way without asking.
        if (args.Contains(Elevation.SetupArgument) || args.Contains(Elevation.UndoArgument))
        {
            if (!Elevation.FinishSwitch(args)) return;
        }
        else if (args.Contains(Elevation.FromTaskArgument)) startedWithWindows = Elevation.TakeHiddenFlag();
        else if (Elevation.HandOverToTask(startedWithWindows)) return;

        // It lives in the tray, so opening it again just brings up the one already running.
        Mutex instance;
        EventWaitHandle showRequest;
        bool first;
        try
        {
            instance = new Mutex(true, @"Local\Aura Mouse", out first);
            showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Aura Mouse.Show");
        }
        catch (UnauthorizedAccessException) { return; } // a copy run as administrator is open, and this one can't reach it
        using var _instance = instance;
        using var _showRequest = showRequest;
        if (!first)
        {
            if (!startedWithWindows) showRequest.Set();
            return;
        }

        // A game loading uses every core; the mouse passing through here shouldn't have to wait behind it.
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; }
        catch (System.ComponentModel.Win32Exception) { }

        // The cursor is hidden while controlling another computer; make sure it always comes back.
        CursorHider.Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CursorHider.Show();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => CursorHider.Show();

        AutoStart.Refresh();
        ApplicationConfiguration.Initialize();
        var form = new MainForm(startHidden: startedWithWindows, resumeLast: startedWithWindows || AutoStart.Enabled);
        form.StartRemote();
        new Thread(() =>
        {
            while (showRequest.WaitOne()) form.BringBack();
        }) { IsBackground = true }.Start();
        Application.Run(form);
        CursorHider.Show();
    }
}
