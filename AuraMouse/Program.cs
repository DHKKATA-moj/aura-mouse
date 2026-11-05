namespace AuraMouse;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool startedWithWindows = args.Contains(AutoStart.Argument);
        App.MoveOldData();

        // It lives in the tray, so opening it again just brings up the one already running.
        using var instance = new Mutex(true, @"Local\Aura Mouse", out bool first);
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Aura Mouse.Show");
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
