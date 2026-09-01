namespace ClaudeLog.Core;

/// <summary>
/// A copy of a session file taken just before the app rewrites it destructively — deleting a prompt
/// or converting a whole file to `---`. These logs are a year of work in a folder with no version
/// control, and both operations are one click with no undo.
///
/// Snapshots live in %LOCALAPPDATA%, never in the log tree: the tree is synced, and backup files
/// there would replicate to every machine. Ordinary edits aren't snapshotted — that's just typing.
/// </summary>
public static class Backups
{
    private const int KeepPerFile = 10;

    public static string Directory => Path.Combine(Paths.AppDataDir, "backups");

    public static void Snapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            System.IO.Directory.CreateDirectory(Directory);
            var stem = Path.GetFileNameWithoutExtension(path);
            var target = Path.Combine(Directory, $"{stem}.{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
            File.Copy(path, target, overwrite: true);

            Prune(stem);
        }
        catch (Exception ex)
        {
            Log.Warn($"backup of {path} failed: {ex.Message}");
        }
    }

    private static void Prune(string stem)
    {
        var old = new DirectoryInfo(Directory)
            .EnumerateFiles($"{stem}.*")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(KeepPerFile);

        foreach (var file in old)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex)
            {
                Log.Warn($"backup prune failed: {ex.Message}");
            }
        }
    }
}
