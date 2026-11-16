using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace AuraMouse;

/// Games that need administrator won't let an ordinary Aura Mouse move the mouse over them, and
/// Windows' "run as administrator?" box can't be clicked from another computer — it's on a locked-off
/// screen on purpose. So instead of answering that box from afar, we remove it: each added game gets a
/// scheduled task that starts it as administrator with no box at all, and a shortcut on the desktop that
/// runs the task. Starting the shortcut (from here or across computers) opens the game elevated, silently.
internal static class Games
{
    public record Game(string Path, string Name, bool Ready);

    static string ListFile => System.IO.Path.Combine(App.DataFolder, "games.txt");

    public static IReadOnlyList<Game> All()
    {
        var games = new List<Game>();
        try
        {
            if (!File.Exists(ListFile)) return games;
            foreach (var line in File.ReadAllLines(ListFile))
            {
                var path = line.Trim();
                if (path.Length == 0 || games.Any(g => g.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                games.Add(new Game(path, System.IO.Path.GetFileNameWithoutExtension(path), TaskExists(TaskName(path))));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return games;
    }

    /// Registers the game so it opens as administrator with no box, and puts a shortcut on the desktop.
    /// Windows asks for administrator once here if Aura Mouse isn't already running as one.
    /// False only if that one question was answered no.
    public static bool Add(string path)
    {
        if (!CreateTask(path)) return false;
        Save(All().Select(g => g.Path).Append(path));
        MakeShortcut(path);
        return true;
    }

    public static void Remove(string path)
    {
        Save(All().Where(g => !g.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Select(g => g.Path));
        RemoveTask(TaskName(path));
        try { File.Delete(ShortcutPath(path)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// Starts the game as administrator, with no box. Running a task needs no administrator itself,
    /// so this works whoever is asking and from across computers.
    public static bool Play(string path)
    {
        var task = TaskName(path);
        if (TaskExists(task) && Run("schtasks", $"/Run /TN \"{task}\"") == 0) return true;
        // No task (it was added on another copy, or making it was refused): just open it the ordinary way.
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static void RemoveAll()
    {
        foreach (var game in All()) Remove(game.Path);
        try { File.Delete(ListFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Save(IEnumerable<string> paths)
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            File.WriteAllLines(ListFile, paths.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string TaskName(string path) =>
        "Aura Mouse game " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..12];

    static bool CreateTask(string path)
    {
        string? user;
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent()) user = identity.User?.Value;
        if (user == null) return false;

        string folder = System.IO.Path.GetDirectoryName(path) ?? "";
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts {SecurityElement.Escape(System.IO.Path.GetFileName(path))} as administrator for Aura Mouse, with no prompt. Remove it in Aura Mouse.</Description>
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
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(path)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(folder)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aura-game-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            string args = $"/Create /TN \"{TaskName(path)}\" /XML \"{file}\" /F";
            // Creating a highest-privilege task needs administrator; if we aren't one, Windows asks this once.
            return Elevation.IsAdmin ? Run("schtasks", args) == 0 : RunElevated("schtasks", args);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static void RemoveTask(string task)
    {
        string args = $"/Delete /TN \"{task}\" /F";
        if (Elevation.IsAdmin) Run("schtasks", args);
        else RunElevated("schtasks", args);
    }

    static bool TaskExists(string task) => Run("schtasks", $"/Query /TN \"{task}\"") == 0;

    // ---- desktop shortcut, so the game can be started without opening Aura Mouse ----

    static string ShortcutPath(string path) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            SafeName(System.IO.Path.GetFileNameWithoutExtension(path)) + " (Aura).lnk");

    static string SafeName(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        return name.Trim();
    }

    /// The shortcut runs the task, so starting it opens the game elevated with no box.
    /// A nicety, not essential — any failure is swallowed.
    static void MakeShortcut(string path)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            shell = Activator.CreateInstance(type);
            if (shell == null) return;
            dynamic s = shell;
            dynamic link = s.CreateShortcut(ShortcutPath(path));
            link.TargetPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
            link.Arguments = $"/Run /TN \"{TaskName(path)}\"";
            link.WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "";
            link.IconLocation = path + ",0";
            link.Description = "Starts this game as administrator for Aura Mouse, with no prompt.";
            link.WindowStyle = 7; // start minimised, so the task-runner window doesn't flash
            link.Save();
        }
        catch (Exception e) when (e is COMException or MissingMemberException or TargetInvocationException or IOException or UnauthorizedAccessException) { }
        finally { if (shell != null) try { Marshal.ReleaseComObject(shell); } catch (ArgumentException) { } }
    }

    static int Run(string file, string arguments)
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
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (InvalidOperationException) { } return -1; }
            return p.ExitCode;
        }
        catch (Win32Exception) { return -1; }
    }

    /// Runs it with administrator, letting Windows ask first. True when it finished and succeeded.
    static bool RunElevated(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit(30000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; } // the user said no
    }

}
