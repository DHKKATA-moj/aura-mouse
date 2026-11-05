using static AuraMouse.Native;

namespace AuraMouse;

/// The key combinations the user chose. Each computer keeps its own.
internal static class Shortcuts
{
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;
    public const int MinKeys = 2;

    static readonly string FilePath = Path.Combine(App.DataFolder, "shortcuts.txt");

    static readonly int[] DefaultLock = [VK_CONTROL, VK_MENU, (int)Keys.L];
    static readonly int[] DefaultEscape = [VK_CONTROL, VK_SHIFT, (int)Keys.F12];
    static volatile int[] _lock = Load(0, DefaultLock);
    static volatile int[] _escape = Load(1, DefaultEscape);

    /// While the user is picking a new combination, pressing the old one shouldn't do anything.
    public static volatile bool Recording;

    /// Locks and unlocks the mouse crossing over to the other screens.
    public static int[] Lock => _lock;

    /// Brings the mouse back to this computer while it's controlling another one.
    public static int[] Escape => _escape;

    public static void SetLock(int[] keys)
    {
        _lock = keys;
        Save();
    }

    public static void SetEscape(int[] keys)
    {
        _escape = keys;
        Save();
    }

    /// One inside the other would fire both, so the two have to stay apart.
    public static bool Clash(int[] a, int[] b) => a.All(b.Contains) || b.All(a.Contains);

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, [string.Join(",", _lock), string.Join(",", _escape)]);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// Line 0 is the lock, line 1 the way back. Files from before the way back could change have only line 0.
    static int[] Load(int line, int[] fallback)
    {
        try
        {
            if (File.Exists(FilePath)) return Parse(File.ReadAllLines(FilePath), line, fallback);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fallback;
    }

    internal static int[] Parse(string[] lines, int line, int[] fallback)
    {
        if (line >= lines.Length) return fallback;
        var keys = lines[line].Split(',').Select(k => int.TryParse(k, out int vk) ? vk : 0).Where(vk => vk > 0).Distinct().ToArray();
        return keys.Length >= MinKeys ? keys : fallback;
    }

    /// Left and right Ctrl/Alt/Shift/Win count as the same key.
    public static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => VK_SHIFT,
        0xA2 or 0xA3 => VK_CONTROL,
        0xA4 or 0xA5 => VK_MENU,
        0x5C => VK_LWIN,
        _ => vk,
    };

    /// "Ctrl + Alt + L": modifiers first, whatever order they were pressed in.
    public static string Describe(IEnumerable<int> keys) =>
        string.Join(" + ", keys.OrderBy(Rank).Select(Name));

    static int Rank(int vk) => vk switch { VK_CONTROL => 0, VK_MENU => 1, VK_SHIFT => 2, VK_LWIN => 3, _ => 4 };

    static string Name(int vk) => vk switch
    {
        VK_CONTROL => "Ctrl",
        VK_MENU => "Alt",
        VK_SHIFT => "Shift",
        VK_LWIN => "Win",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(), // the digit keys
        _ => ((Keys)vk).ToString(),
    };
}

/// Spots a key combination from the stream of real key presses. Works out what's held itself, because
/// keys swallowed while controlling another computer never show up in Windows' own key state.
internal sealed class ChordDetector
{
    readonly HashSet<int> _down = [];
    int _swallowed; // the key that completed the combination: its repeats and release stay hidden too

    /// Returns true if this key event belongs to a combination and must be hidden from everything else.
    public bool Key(int vkCode, bool up, bool capturing, int[] chord, Action pressed)
    {
        int vk = Shortcuts.Normalize(vkCode);
        if (up)
        {
            _down.Remove(vk);
            if (vk != _swallowed) return false;
            _swallowed = 0;
            return true;
        }

        // A release can go unseen (the lock screen, a UAC prompt), which would leave a key "held" forever.
        if (!capturing) _down.RemoveWhere(k => k != vk && GetAsyncKeyState(k) >= 0);

        bool isNew = _down.Add(vk);
        if (vk == _swallowed) return true;
        // Recording only counts here at home: while capturing, keys can't reach the box being recorded,
        // and the way back has to keep working.
        if (!isNew || (Shortcuts.Recording && !capturing) || !chord.Contains(vk) || !chord.All(_down.Contains)) return false;

        _swallowed = vk;
        pressed();
        return true;
    }
}
