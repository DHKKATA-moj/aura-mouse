namespace AuraMouse;

/// Remembers which screen edge each computer (by name) sits on, so it is placed right away next time.
internal static class LayoutStore
{
    static readonly string FilePath = Path.Combine(App.DataFolder, "layout.txt");

    static readonly object FileLock = new();

    public static Dictionary<string, Edge> Load()
    {
        var layout = new Dictionary<string, Edge>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return layout;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && Enum.TryParse<Edge>(parts[1], out var edge) && edge != Edge.None)
                    layout[parts[0]] = edge;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return layout;
    }

    public static void Save(IReadOnlyDictionary<string, Edge> layout)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllLines(FilePath, layout.Select(kv => $"{kv.Key}\t{kv.Value}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
