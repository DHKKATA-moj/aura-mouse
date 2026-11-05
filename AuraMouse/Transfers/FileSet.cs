using System.Security.Cryptography;
using System.Text;

namespace AuraMouse.Transfers;

internal sealed record SourceFile(string FullPath, string RelativePath, long Size);

/// The files the user picked to send. Folders are sent with everything inside, keeping their structure.
internal sealed class FileSet
{
    public const int MaxFiles = 100_000;

    public required string Id { get; init; }
    public required IReadOnlyList<SourceFile> Files { get; init; }
    public string? Title { get; init; }
    public long TotalBytes => Files.Sum(f => f.Size);

    public static FileSet FromPaths(IEnumerable<string> paths)
    {
        var files = new List<SourceFile>();
        var topLevel = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            if (File.Exists(path))
            {
                var name = Path.GetFileName(path);
                topLevel.Add(name);
                TryAdd(files, path, name);
            }
            else if (Directory.Exists(path))
            {
                var name = new DirectoryInfo(path).Name.TrimEnd('\\', ':');
                topLevel.Add(name);
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    TryAdd(files, file, name + "/" + Path.GetRelativePath(path, file).Replace('\\', '/'));
                    if (files.Count > MaxFiles) throw new TransferRefusedException("err_too_many");
                }
            }
        }

        // The same files (path, size, date) always get the same id, so sending them again resumes.
        var key = new StringBuilder(Environment.MachineName);
        foreach (var f in files)
            key.Append('|').Append(f.FullPath).Append('|').Append(f.Size).Append('|').Append(SafeWriteTime(f.FullPath));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..24].ToLowerInvariant();

        return new FileSet { Id = id, Files = files, Title = topLevel.Count == 1 ? topLevel[0] : null };
    }

    static void TryAdd(List<SourceFile> files, string fullPath, string relativePath)
    {
        try { files.Add(new SourceFile(fullPath, relativePath, new FileInfo(fullPath).Length)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static long SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return 0; }
    }
}
