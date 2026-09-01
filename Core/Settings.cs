using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeLog.Core;

/// <summary>
/// User-editable configuration, %LOCALAPPDATA%\ClaudeLog\settings.json. Kept separate from
/// state.json so it stays readable and hand-editable; nothing here is written on a hot path.
/// </summary>
public sealed class Settings
{
    public string LogRoot { get; set; } = Paths.DefaultLogRoot;

    public string ClaudeProjectsDir { get; set; } = Paths.DefaultClaudeProjectsDir;

    /// <summary>Claude Code's OAuth credentials file, for <see cref="UsageWatcher"/>. Overridable
    /// the same way <see cref="ClaudeProjectsDir"/> is, so a throwaway instance can point at a
    /// fake one instead of the real login.</summary>
    public string ClaudeCredentialsFile { get; set; } = Paths.DefaultClaudeCredentialsFile;

    /// <summary>Mode for files the app creates. Existing files default to Legacy — see StateStore.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ParseMode NewFileMode { get; set; } = ParseMode.Modern;

    /// <summary>Copying a prompt is the act of sending it, so copying marks it sent.</summary>
    public bool MarkSentOnCopy { get; set; } = true;

    /// <summary>Flash the taskbar button when the limit resets, for when the window is behind the terminal.</summary>
    public bool FlashOnReset { get; set; } = true;

    /// <summary>Stage the next queued prompt on the clipboard the moment the limit resets.</summary>
    public bool StageClipboardOnReset { get; set; } = true;

    /// <summary>
    /// Show the manual reset-time entry under the countdown. Off by default: detection from Claude
    /// Code's transcripts is reliable, so the override is the fallback for when it isn't, not
    /// something to look at every day. The entry appears anyway while an override is actually set,
    /// so a hidden one can always be cleared.
    /// </summary>
    public bool ShowManualReset { get; set; }

    /// <summary>
    /// Log folder name → source folder, for "Open project source". Optional, and empty by default:
    /// the mapping isn't mechanical, and a downloaded binary shouldn't arrive pre-filled with
    /// paths from the machine it was written on.
    /// </summary>
    public Dictionary<string, string> ProjectSources { get; set; } = [];

    // ------------------------------------------------------------ terminal

    /// <summary>
    /// The directory Claude Code is launched in when a project doesn't name its own. Empty means
    /// "use the project's source folder", which is the sensible thing for a fresh install to do;
    /// setting it to a root that has a CLAUDE.md covering every project — `D:\Source` here — means
    /// one Claude Code session can work across projects and every project inherits it at once.
    /// </summary>
    public string DefaultSessionDir { get; set; } = "";

    /// <summary>
    /// Log folder name → the directory Claude Code runs in for that project's sessions. Overrides
    /// <see cref="DefaultSessionDir"/>, for the project that wants its own working directory.
    /// </summary>
    public Dictionary<string, string> ProjectSessionDirs { get; set; } = [];

    /// <summary>Claude Code itself. Resolved through PATH unless this is a full path.</summary>
    public string ClaudeExe { get; set; } = "claude.exe";

    /// <summary>
    /// The terminal Claude Code runs in. Windows Terminal by default, but nothing in the app
    /// depends on it: prompts are delivered through the Win32 console, which every terminal that
    /// hosts a real console provides.
    /// </summary>
    public string TerminalExe { get; set; } = "wt.exe";

    /// <summary>
    /// Arguments for a new session, formatted with {0} window name, {1} tab title, {2} working
    /// directory, {3} the PowerShell script that reports its PID and then runs Claude Code. The
    /// last three arrive already quoted.
    /// </summary>
    public string TerminalArgs { get; set; } =
        "-w {0} new-tab --title {1} -d {2} powershell.exe -NoProfile -ExecutionPolicy Bypass -File {3}";

    /// <summary>Arguments that bring an existing session's window to the front. {0} is its window name.</summary>
    public string TerminalShowArgs { get; set; } = "-w {0} focus-tab -t 0";

    /// <summary>Open a terminal automatically the first time a session's prompt is sent.</summary>
    public bool AutoStartTerminal { get; set; } = true;

    /// <summary>Sending a prompt is unambiguously sending it, so it marks the prompt sent.</summary>
    public bool MarkSentOnSend { get; set; } = true;

    /// <summary>
    /// Milliseconds between the pasted prompt and the Enter that submits it. A carriage return in
    /// the same write as the paste's closing marker can be swallowed into the paste and end up as
    /// a newline in the prompt rather than submitting it.
    /// </summary>
    public int SubmitDelayMs { get; set; } = 250;

    /// <summary>
    /// Send the head of the queue as soon as the limit resets, instead of only staging it on the
    /// clipboard. Off by default: the reset usually arrives while Scott is away from the machine,
    /// and a prompt that sends itself into a session nobody is watching is how work gets done that
    /// nobody asked for.
    /// </summary>
    public bool AutoSendOnReset { get; set; }

    /// <summary>
    /// The directory a project's Claude Code session runs in: the project's own setting, else the
    /// global default, else the source folder the project's prompts are about.
    /// </summary>
    public string SessionDirFor(string project)
    {
        if (ProjectSessionDirs.TryGetValue(project, out var own) && own.Length > 0) return own;
        if (DefaultSessionDir.Length > 0) return DefaultSessionDir;
        return ProjectSources.TryGetValue(project, out var source) ? source : "";
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Options);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings load failed: {ex.Message}");
        }

        var settings = new Settings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            Paths.EnsureAppDataDir();
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"settings save failed: {ex.Message}");
        }
    }
}
