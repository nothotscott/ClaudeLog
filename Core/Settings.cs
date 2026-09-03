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
    /// Arguments for a new PowerShell session, formatted with {0} window name, {1} tab title,
    /// {2} working directory, {3} the script that reports its PID and then runs Claude Code. The
    /// last three arrive already quoted. Used when the session's <see cref="TerminalShell"/> is
    /// <see cref="Core.TerminalShell.PowerShell"/>.
    /// </summary>
    public string TerminalArgs { get; set; } =
        "-w {0} new-tab --title {1} -d {2} powershell.exe -NoProfile -ExecutionPolicy Bypass -File {3}";

    /// <summary>
    /// Same four placeholders as <see cref="TerminalArgs"/>, for a session whose
    /// <see cref="TerminalShell"/> is <see cref="Core.TerminalShell.GitBash"/>. Bash has no `-File`
    /// equivalent — the script path is just the last argument to bash itself — and `--login` picks
    /// up whatever put `claude` on PATH (nvm, a global npm prefix) the way a real interactive Git
    /// Bash session would.
    ///
    /// The full path to Git's bash.exe is baked in rather than left to PATH resolution — unlike
    /// `claude.exe`, plain `bash.exe` is ambiguous on a machine with WSL installed:
    /// `C:\Windows\System32\bash.exe` is the WSL launcher, sits earlier on PATH than Git's own, and
    /// silently wins. Confirmed on a real machine with both installed, not a hypothetical.
    /// </summary>
    public string TerminalArgsGitBash { get; set; } =
        "-w {0} new-tab --title {1} -d {2} \"C:\\Program Files\\Git\\bin\\bash.exe\" --login {3}";

    /// <summary>Arguments that bring an existing session's window to the front. {0} is its window name.</summary>
    public string TerminalShowArgs { get; set; } = "-w {0} focus-tab -t 0";

    /// <summary>
    /// The shell a project's Claude Code session runs in when it doesn't name its own — see
    /// <see cref="ProjectShells"/>. PowerShell by default; Git Bash is the alternative
    /// <see cref="WinTerminal"/> knows how to launch.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TerminalShell DefaultShell { get; set; } = TerminalShell.PowerShell;

    /// <summary>Log folder name → shell override for that project's sessions. Overrides <see cref="DefaultShell"/>.</summary>
    public Dictionary<string, TerminalShell> ProjectShells { get; set; } = [];

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

    /// <summary>The shell a project's Claude Code session runs in: its own override, else the global default.</summary>
    public TerminalShell ShellFor(string project) =>
        ProjectShells.TryGetValue(project, out var shell) ? shell : DefaultShell;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Options);
                if (loaded is not null) return Normalize(loaded);
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

    /// <summary>
    /// Guards against a settings.json that has <c>"ClaudeProjectsDir": null</c> or `""` — a hand
    /// -trimmed "clean" config, or one saved by a version that predates the field. The C# type is
    /// non-nullable, but that's a compile-time annotation only; System.Text.Json will still happily
    /// write `null` straight into the property, and every terminal launch reads it through
    /// <see cref="WinTerminal.TranscriptPath"/>, so a null here throws before Claude Code ever
    /// starts. Restoring the computed default instead means a session can always be started.
    /// </summary>
    internal static Settings Normalize(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClaudeProjectsDir))
        {
            settings.ClaudeProjectsDir = Paths.DefaultClaudeProjectsDir;
        }

        return settings;
    }

    /// <summary>
    /// A detached copy, for the settings dialog to edit. Cancel then has to undo nothing, and OK
    /// is a single <see cref="CopyFrom"/> — which matters because a half-applied settings change
    /// is a working directory that doesn't match the shell it's launched in.
    /// </summary>
    public Settings Clone() =>
        Normalize(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Options), Options)
                  ?? new Settings());

    /// <summary>
    /// Takes on another instance's values in place, so everything already holding this one — the
    /// view model, the watchers — sees the change without being rebuilt.
    ///
    /// Reflected over rather than written out property by property on purpose: a setting added
    /// later is carried automatically, where a hand-written list silently drops it and the dialog
    /// appears to forget one field.
    /// </summary>
    public void CopyFrom(Settings other)
    {
        foreach (var property in typeof(Settings).GetProperties())
        {
            if (property is { CanRead: true, CanWrite: true }) property.SetValue(this, property.GetValue(other));
        }
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
