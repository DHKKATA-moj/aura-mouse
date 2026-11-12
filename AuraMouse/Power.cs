using System.Diagnostics;

namespace AuraMouse;

/// Turning this computer off from a trusted phone. Windows shows its own warning and counts down,
/// so anyone sitting at it has time to stop it, and Aura Mouse adds a Cancel button of its own.
internal static class Power
{
    public const int ShutdownDelaySeconds = 20;

    /// Runs the "shutdown" command. Swapped out in tests so they never really turn the computer off.
    public static Func<string, bool> Runner { get; set; } = RealRun;

    /// Starts a shutdown after a delay. Returns true if Windows accepted the command.
    public static bool Shutdown(int seconds = ShutdownDelaySeconds)
    {
        var warning = L.T("shutdown_warning").Replace('"', '\'');
        return Runner($"/s /t {seconds} /c \"{warning}\"");
    }

    /// Calls off a shutdown that hasn't happened yet.
    public static void CancelShutdown() => Runner("/a");

    static bool RealRun(string arguments)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("shutdown", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
