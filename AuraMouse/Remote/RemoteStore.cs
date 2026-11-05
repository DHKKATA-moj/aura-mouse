using System.Security.Cryptography;
using System.Text.Json;

namespace AuraMouse.Remote;

/// A device its owner let in without asking.
internal sealed record RemoteTrusted(int Id, string Name, byte[] Key);

/// A computer viewed before. Id and Key are there if its owner let us in without asking.
internal sealed record RemoteComputer(string Name, string Ip, int Id, byte[]? Key);

/// Remote desktop's own file, kept apart from the mouse sharing's saved computers. Keys are encrypted for this Windows user.
internal static class RemoteStore
{
    public const int KeySize = 32;

    sealed class Data
    {
        public bool Enabled { get; set; } // off until the person turns it on: nobody can ask until then
        public List<RemoteTrusted> Trusted { get; set; } = [];
        public List<RemoteComputer> Computers { get; set; } = [];
    }

    static readonly object Lock = new();
    static Data? _data;

    public static string FilePath { get; set; } = Path.Combine(App.DataFolder, "remote.json");

    static Data Current => _data ??= Load();

    public static bool Enabled
    {
        get { lock (Lock) return Current.Enabled; }
        set { lock (Lock) { Current.Enabled = value; Save(); } }
    }

    public static List<RemoteTrusted> Trusted { get { lock (Lock) return Current.Trusted.ToList(); } }
    public static List<RemoteComputer> Computers { get { lock (Lock) return Current.Computers.ToList(); } }

    public static RemoteTrusted? TrustedById(int id) { lock (Lock) return Current.Trusted.FirstOrDefault(t => t.Id == id); }

    /// The same device asking again replaces its old permission.
    public static RemoteTrusted Trust(string name)
    {
        lock (Lock)
        {
            int id;
            do id = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            while (Current.Trusted.Any(t => t.Id == id));
            var trusted = new RemoteTrusted(id, name, RandomNumberGenerator.GetBytes(KeySize));
            Current.Trusted.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            Current.Trusted.Add(trusted);
            Save();
            return trusted;
        }
    }

    public static void Untrust(int id)
    {
        lock (Lock)
        {
            Current.Trusted.RemoveAll(t => t.Id == id);
            Save();
        }
    }

    public static void SaveComputer(RemoteComputer computer)
    {
        lock (Lock)
        {
            Current.Computers.RemoveAll(c => string.Equals(c.Name, computer.Name, StringComparison.OrdinalIgnoreCase));
            Current.Computers.Insert(0, computer);
            Save();
        }
    }

    public static void ForgetComputer(string name)
    {
        lock (Lock)
        {
            Current.Computers.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    /// The owner took the permission back: from now on it asks again.
    public static void ForgetKey(string name)
    {
        lock (Lock)
        {
            var at = Current.Computers.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return;
            Current.Computers[at] = Current.Computers[at] with { Id = 0, Key = null };
            Save();
        }
    }

    static Data Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Data();
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllBytes(FilePath)) ?? new Data();
            data.Trusted = data.Trusted.Select(t => t with { Key = Open(t.Key) }).ToList();
            data.Computers = data.Computers.Select(c => c with { Key = c.Key == null ? null : Open(c.Key) }).ToList();
            return data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return new Data(); // unreadable, or from another Windows user: everything asks again
        }
    }

    static void Save()
    {
        try
        {
            var onDisk = new Data
            {
                Enabled = _data!.Enabled,
                Trusted = _data.Trusted.Select(t => t with { Key = Seal(t.Key) }).ToList(),
                Computers = _data.Computers.Select(c => c with { Key = c.Key == null ? null : Seal(c.Key) }).ToList(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(onDisk));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (CryptographicException) { } // Windows can't encrypt here: it just asks again next time
    }

    static byte[] Seal(byte[] key) => ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
    static byte[] Open(byte[] key) => ProtectedData.Unprotect(key, null, DataProtectionScope.CurrentUser);
}
