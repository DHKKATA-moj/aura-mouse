namespace AuraMouse;

internal static class App
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraMouse");

    /// Everything saved here: paired computers, trusted devices, settings. Received files are elsewhere and stay.
    public static bool DeleteData() => DeleteFolder(DataFolder);

    internal static bool DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// Saved computers and settings from when the program had an older name, newest first. The keys in them
    /// keep working, because they're encrypted for this Windows user rather than for a folder.
    public static void MoveOldData()
    {
        if (Directory.Exists(DataFolder)) return;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var name in new[] { "MojMouse", "EasyMouse" })
        {
            var old = Path.Combine(appData, name);
            if (!Directory.Exists(old)) continue;
            try { Directory.Move(old, DataFolder); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return;
        }
    }
}
