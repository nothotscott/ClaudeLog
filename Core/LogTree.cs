namespace ClaudeLog.Core;

public sealed class LogSession
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Key { get; init; }
    public DateTime Modified { get; init; }
}

/// <summary>A subfolder of a project — Examples, Plans and the like. Attachments, not sessions.</summary>
public sealed class LogFolder
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public int ItemCount { get; init; }
}

public sealed class LogProject
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public List<LogSession> Sessions { get; } = [];
    public List<LogFolder> Folders { get; } = [];
}

/// <summary>
/// Reads the log tree: projects are folders, sessions are the .md/.txt files directly inside them,
/// and any deeper folder is an attachment folder that the app shows but never parses.
/// </summary>
public static class LogTree
{
    private static readonly string[] SessionExtensions = [".md", ".txt"];

    public static bool IsSessionFile(string path) =>
        SessionExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static List<LogProject> Scan(string root)
    {
        var projects = new List<LogProject>();
        if (!Directory.Exists(root)) return projects;

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;

            var project = new LogProject { Name = name, Path = dir };
            try
            {
                // Newest first. A session file gets worked on for days after it's created, so the
                // one touched last is almost always the one being continued — alphabetical order
                // buried it somewhere among thirty siblings. Name breaks ties, so the order doesn't
                // shuffle when Syncthing lands a batch of files with equal timestamps.
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles()
                             .Where(f => IsSessionFile(f.Name))
                             .OrderByDescending(f => f.LastWriteTime)
                             .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    project.Sessions.Add(new LogSession
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        Key = Paths.RelativeKey(root, file.FullName),
                        Modified = file.LastWriteTime,
                    });
                }

                foreach (var sub in Directory.EnumerateDirectories(dir)
                             .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    project.Folders.Add(new LogFolder
                    {
                        Name = System.IO.Path.GetFileName(sub),
                        Path = sub,
                        ItemCount = CountItems(sub),
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"scan failed for {dir}: {ex.Message}");
            }

            projects.Add(project);
        }

        return projects;
    }

    private static int CountItems(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir).Count();
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>Debounced file-system watcher. The tree changes under the app: Syncthing writes it, so does Notepad++.</summary>
public sealed class TreeWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounce = new(750) { AutoReset = false };

    public event Action? Changed;

    public TreeWatcher(string root)
    {
        _debounce.Elapsed += (_, _) => Changed?.Invoke();

        if (!Directory.Exists(root)) return;
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
        }
        catch (Exception ex)
        {
            Log.Warn($"tree watcher failed: {ex.Message}");
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        // Ignore our own atomic-save temp files.
        if (e.Name?.Contains(".claudelog.tmp", StringComparison.OrdinalIgnoreCase) == true) return;
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
