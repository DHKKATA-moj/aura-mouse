using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace AuraMouse;

/// Programs run as administrator (many games) are walled off from ordinary ones: while one is in
/// front, Windows stops telling Aura Mouse about the mouse and keyboard and won't let it move the
/// cursor or type. Running Aura Mouse as administrator too removes the wall.
///
/// So that this doesn't mean a Windows question at every start, turning it on (Windows asks once)
/// registers a scheduled task that may run Aura Mouse as administrator. From then on an ordinary
/// start just runs that task and steps aside.
internal static class Elevation
{
    /// Given to the copy started as administrator when it's turned on or off, with the pid of the copy it replaces.
    public const string SetupArgument = "--admin-on", UndoArgument = "--admin-off";
    /// Given by the task, so the copy it starts never hands over to the task again.
    public const string FromTaskArgument = "--from-task";

    const string TaskName = "Aura Mouse (administrator)";

    public static bool IsAdmin { get; } = CheckAdmin();

    static bool CheckAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    /// The window in front belongs to a program we can't reach. Ours or an ordinary program's: false.
    public static bool FrontIsWalledOff(out IntPtr window)
    {
        window = GetForegroundWindow();
        if (IsAdmin || window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return true; // a protected or system program: just as unreachable
        try
        {
            // An administrator's process won't even show its token to an ordinary one.
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return true;
            try
            {
                return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    // ---- Always as administrator ----

    static string HiddenFlag => Path.Combine(App.DataFolder, "start-hidden.txt");

    /// Whether the "always as administrator" task is set up (by its name, wherever it points).
    public static bool Always => Run("schtasks", $"/Query /TN \"{TaskName}\"").Code == 0;

    /// At start, before anything else: an ordinary copy hands over to the task if it's set up. True if it did.
    public static bool HandOverToTask(bool startHidden)
    {
        if (IsAdmin) return false;
        var target = RegisteredPath();
        // Not set up, or set up but pointing at a copy that's since been deleted: just run as usual.
        if (target == null || (!target.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && !File.Exists(target)))
            return false;
        try
        {
            if (startHidden) { Directory.CreateDirectory(App.DataFolder); File.WriteAllText(HiddenFlag, "yes"); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (Run("schtasks", $"/Run /TN \"{TaskName}\"").Code == 0) return true;
        TakeHiddenFlag();
        return false;
    }

    /// The exe the task runs, or null if there's no task. Read from its XML.
    static string? RegisteredPath()
    {
        var (code, xml) = Run("schtasks", $"/Query /TN \"{TaskName}\" /XML");
        if (code != 0) return null;
        var start = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
        var end = xml.IndexOf("</Command>", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0) return null;
        start += "<Command>".Length;
        return System.Net.WebUtility.HtmlDecode(xml[start..end]).Trim();
    }

    /// In the copy the task started: whether the one that handed over was starting with Windows.
    public static bool TakeHiddenFlag()
    {
        try
        {
            if (!File.Exists(HiddenFlag)) return false;
            File.Delete(HiddenFlag);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// Turns it on or off from the running copy. Windows asks first unless this copy is already
    /// administrator. True if the change happened or is under way in a new copy (then this one must close).
    public static bool Switch(bool on, out bool closeThisCopy)
    {
        closeThisCopy = false;
        if (!on && IsAdmin) return RemoveTask();
        try
        {
            // Turning on always goes through a new copy as administrator, which then carries on in place of this one.
            using var _ = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
                $"{(on ? SetupArgument : UndoArgument)} {Environment.ProcessId}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            closeThisCopy = on;
            return true;
        }
        catch (Win32Exception) { return false; } // the user said no
    }

    /// In the copy started by Switch. Returns whether this copy should go on to run as Aura Mouse.
    public static bool FinishSwitch(string[] args)
    {
        bool on = args.Contains(SetupArgument);
        if (!on)
        {
            RemoveTask();
            return false;
        }
        WaitForOldCopy(args);
        if (IsAdmin) CreateTask();
        return true;
    }

    static void WaitForOldCopy(string[] args)
    {
        int at = Array.IndexOf(args, SetupArgument);
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out int pid)) return;
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(15000);
        }
        catch (ArgumentException) { } // already gone
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    static bool CreateTask()
    {
        string? user;
        using (var identity = WindowsIdentity.GetCurrent()) user = identity.User?.Value;
        if (user == null) return false;

        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Aura Mouse as administrator, so the mouse keeps working over games run as administrator. Turn it off in Aura Mouse's settings.</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(Environment.ProcessPath!)}</Command>
                  <Arguments>{FromTaskArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"aura-mouse-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return Run("schtasks", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F").Code == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static bool RemoveTask() => Run("schtasks", $"/Delete /TN \"{TaskName}\" /F").Code == 0;

    /// Also removes it when Aura Mouse is removed from this computer (Windows may ask).
    public static void RemoveIfThere()
    {
        if (!Always) return;
        Switch(false, out _);
    }

    static (int Code, string Output) Run(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (InvalidOperationException) { } return (-1, ""); }
            return (p.ExitCode, output.Result);
        }
        catch (Win32Exception) { return (-1, ""); }
    }

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    const int TokenElevation = 20;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int size, out int returned);
}
