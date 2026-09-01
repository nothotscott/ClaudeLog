namespace ClaudeLog.Core;

/// <summary>
/// Where things live. App data goes to %LOCALAPPDATA% and never into the log tree: that tree is
/// Syncthing-synced, and a state file written from two machines is a sync conflict waiting to
/// happen. State is machine-local by design.
/// </summary>
public static class Paths
{
    /// <summary>
    /// %LOCALAPPDATA%\ClaudeLog, unless CLAUDELOG_HOME says otherwise. The override exists so a
    /// second instance can be run against throwaway settings and state without disturbing the one
    /// you have open — the app is usually already running while it's being worked on.
    /// </summary>
    public static string AppDataDir { get; } =
        Environment.GetEnvironmentVariable("CLAUDELOG_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeLog");

    public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");

    public static string StateFile => Path.Combine(AppDataDir, "state.json");

    public static string DefaultLogRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClaudeLog");

    public static string DefaultClaudeProjectsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static void EnsureAppDataDir() => Directory.CreateDirectory(AppDataDir);

    /// <summary>Forward-slashed path relative to the log root — the key used in state.json.</summary>
    public static string RelativeKey(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
