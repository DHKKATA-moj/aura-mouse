namespace AuraMouse;

internal static class App
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraMouse");

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
