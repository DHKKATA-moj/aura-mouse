using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AuraMouse;

/// A computer paired once with the code. Key is what stands in for the code from then on.
internal sealed record SavedComputer(int Id, string Name, byte[] Key, string? Ip);

/// Remembers the computers paired with this one, so they reconnect without the code.
/// The keys on disk are encrypted for this Windows user. Safe to use from any thread.
internal static class PairStore
{
    public const int KeySize = 32;

    sealed class Data
    {
        public string? Role { get; set; } // "host" or "client": what this computer goes back to at start
        public SavedComputer? Host { get; set; } // the computer we connect to
        public List<SavedComputer> Clients { get; set; } = []; // the computers that connect to us
        public List<WakeTarget> Wake { get; set; } = []; // the computers we can turn on
        public bool AutoStartDecided { get; set; } // it was turned on by default once, or the user chose
    }

    static readonly object Lock = new();
    static Data? _data;

    public static string FilePath { get; set; } = Path.Combine(App.DataFolder, "pairs.json");

    public static string? Role { get { lock (Lock) return Current.Role; } }
    public static SavedComputer? Host { get { lock (Lock) return Current.Host; } }
    public static List<SavedComputer> Clients { get { lock (Lock) return Current.Clients.ToList(); } }
    public static bool HasPairs { get { lock (Lock) return Current.Host != null || Current.Clients.Count > 0; } }

    public static List<WakeTarget> WakeTargets { get { lock (Lock) return Current.Wake.ToList(); } }

    public static void SaveWake(WakeTarget target)
    {
        lock (Lock)
        {
            Current.Wake.RemoveAll(w => string.Equals(w.Name, target.Name, StringComparison.OrdinalIgnoreCase));
            Current.Wake.Add(target);
            Save();
        }
    }

    public static void RemoveWake(string name)
    {
        lock (Lock)
        {
            Current.Wake.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    public static SavedComputer? ClientById(int id)
    {
        lock (Lock) return Current.Clients.FirstOrDefault(c => c.Id == id);
    }

    /// A computer paired with the code: gets a new key, replacing the one it had.
    public static SavedComputer AddClient(string name)
    {
        var client = new SavedComputer(RandomNumberGenerator.GetInt32(1, int.MaxValue), name, RandomNumberGenerator.GetBytes(KeySize), null);
        lock (Lock)
        {
            Current.Clients.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            Current.Clients.Add(client);
            Current.Role = "host";
            Save();
        }
        return client;
    }

    /// That computer's key stops working, so it needs a new code to connect again.
    public static void RemoveClient(int id)
    {
        lock (Lock)
        {
            Current.Clients.RemoveAll(c => c.Id == id);
            Save();
        }
    }

    public static void RemoveHost()
    {
        lock (Lock)
        {
            Current.Host = null;
            Save();
        }
    }

    public static void BecomeHost()
    {
        lock (Lock)
        {
            if (Current.Role == "host") return;
            Current.Role = "host";
            Save();
        }
    }

    /// Starting with Windows is on by default once computers are paired, unless the user turned it off.
    /// Returns true if this turned it on.
    public static bool EnableAutoStartOnce()
    {
        lock (Lock)
        {
            if (Current.AutoStartDecided) return false;
            Current.AutoStartDecided = true;
            Save();
        }
        AutoStart.Enabled = true;
        return true;
    }

    public static void SetAutoStart(bool on)
    {
        lock (Lock)
        {
            Current.AutoStartDecided = true;
            Save();
        }
        AutoStart.Enabled = on;
    }

    public static void SetHost(SavedComputer host)
    {
        lock (Lock)
        {
            Current.Host = host;
            Current.Role = "client";
            Save();
        }
    }

    public static void Forget()
    {
        lock (Lock)
        {
            _data = new Data();
            Save();
        }
        AutoStart.Enabled = false;
    }

    static Data Current => _data ??= Load();

    static Data Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Data();
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllBytes(FilePath)) ?? new Data();
            if (data.Host != null) data.Host = Unprotected(data.Host);
            data.Clients = data.Clients.Select(Unprotected).ToList();
            return data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return new Data(); // unreadable, or from another Windows user: pair again with the code
        }
    }

    static void Save()
    {
        try
        {
            var onDisk = new Data
            {
                Role = _data!.Role,
                AutoStartDecided = _data.AutoStartDecided,
                Wake = _data.Wake,
                Host = _data.Host == null ? null : Protected(_data.Host),
                Clients = _data.Clients.Select(Protected).ToList(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(onDisk));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static SavedComputer Protected(SavedComputer c) =>
        c with { Key = ProtectedData.Protect(c.Key, null, DataProtectionScope.CurrentUser) };

    static SavedComputer Unprotected(SavedComputer c) =>
        c with { Key = ProtectedData.Unprotect(c.Key, null, DataProtectionScope.CurrentUser) };
}

/// Starting Aura Mouse when the user signs in to Windows.
internal static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Aura Mouse";
    static readonly string[] OldValueNames = ["Moj Mouse", "EasyMouse"];
    public const string Argument = "--auto";

    static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// The exe may have been moved since it was turned on, or still be set up under one of the program's old names.
    public static void Refresh()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            foreach (var old in OldValueNames)
            {
                if (key.GetValue(old) == null) continue;
                key.DeleteValue(old, throwOnMissingValue: false);
                key.SetValue(ValueName, Command);
            }
        }
        if (Enabled) Enabled = true;
    }
}

/// Other computers can only reach the one they connect to if Windows Firewall lets them in, and
/// its own question about that is easy to miss or answer wrong (a "no" stays as a block rule).
internal static class Firewall
{
    static string Program => Environment.ProcessPath!;

    // Tied to the exe's location: a copy somewhere else is a different program to the firewall.
    static string RuleName => "Aura Mouse LAN " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Program.ToLowerInvariant())))[..8];

    /// Blocks while it runs, so call it off the UI thread.
    public static bool IsOpen()
    {
        using var netsh = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        netsh.StandardOutput.ReadToEnd();
        netsh.WaitForExit();
        return netsh.ExitCode == 0;
    }

    /// Replaces this exe's firewall rules (including a block from an earlier "no") with one allowing it,
    /// from its own network only.
    /// Windows asks the user first. Blocks while it runs, so call it off the UI thread.
    public static bool Open()
    {
        if (Program.Contains('%')) return false; // cmd would expand it, in a command that runs as administrator
        var command = $"/c \"netsh advfirewall firewall delete rule name=all dir=in program=\"{Program}\" & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow enable=yes profile=any remoteip=localsubnet program=\"{Program}\"\"";
        try
        {
            using var cmd = Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            cmd.WaitForExit();
            return IsOpen();
        }
        catch (Win32Exception) { return false; } // the user said no
    }
}
