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
