using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AuraMouse;

/// Programs run as administrator (many games) are walled off from ordinary ones: while one is in
/// front, Windows stops telling Aura Mouse about the mouse and keyboard and won't let it move the
/// cursor or type. Running Aura Mouse as administrator too removes the wall.
internal static class Elevation
{
    public const string RestartArgument = "--restarted-from", ForGameArgument = "--for-game";

    public static bool IsAdmin { get; } = CheckAdmin();

    /// An administrator's program came to the front (once for each of its windows), with its name.
    /// Raised from a timer thread.
    public static event Action<string>? AdminInFront;

    internal static void RaiseAdminInFront(string name) => AdminInFront?.Invoke(name);

    static bool CheckAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    /// The window in front belongs to a program we can't reach, with its name for messages.
    /// Ours or an ordinary program's: false.
    public static bool FrontIsWalledOff(out IntPtr window, out string name)
    {
        name = "";
        window = GetForegroundWindow();
        if (IsAdmin || window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return true; // a protected or system program: just as unreachable
        try
        {
            name = NameOf(process);
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

    static string NameOf(IntPtr process)
    {
        var buffer = new char[1024];
        int size = buffer.Length;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? Path.GetFileNameWithoutExtension(new string(buffer, 0, size)) : "";
    }

    static string ChoiceFile => Path.Combine(App.DataFolder, "admin.txt");

    /// The user chose to run as administrator once, so it does every time from then on.
    public static bool Wanted
    {
        get => File.Exists(ChoiceFile);
        set
        {
            try
            {
                if (value) { Directory.CreateDirectory(App.DataFolder); File.WriteAllText(ChoiceFile, "yes"); }
                else File.Delete(ChoiceFile);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// Starts a copy as administrator (Windows asks first) that takes over once this one has closed.
    /// False if the user said no, which also stops it asking at every start.
    public static bool StartAsAdmin(string passOn = ForGameArgument)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{RestartArgument} {Environment.ProcessId} {passOn}".TrimEnd())
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Wanted = true;
            return true;
        }
        catch (Win32Exception)
        {
            Wanted = false;
            return false;
        }
    }

    /// In the copy started above: waits for the old one to close, so it can take over.
    public static bool WaitForOldCopy(string[] args)
    {
        int at = Array.IndexOf(args, RestartArgument);
        if (at < 0) return false;
        if (at + 1 < args.Length && int.TryParse(args[at + 1], out int pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.WaitForExit(15000);
            }
            catch (ArgumentException) { } // already gone
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        return true;
    }

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    const int TokenElevation = 20;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int size, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, [Out] char[] name, ref int size);
}
