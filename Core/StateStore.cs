using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeLog.Core;

public enum PromptStatus
{
    /// <summary>Written, not sent. The default for anything the app has never seen copied.</summary>
    Draft,

    /// <summary>Waiting for the session limit to reset.</summary>
    Queued,

    /// <summary>Copied to the clipboard for pasting into Claude Code.</summary>
    Sent,
}

public sealed class PromptState
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PromptStatus Status { get; set; } = PromptStatus.Draft;

    public DateTimeOffset? SentAt { get; set; }
}

public sealed class FileState
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ParseMode Mode { get; set; } = ParseMode.Legacy;

    public Dictionary<string, PromptState> Prompts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The Claude Code conversation this file's prompts are sent to. ClaudeLog picks the GUID and
    /// passes it to `claude --session-id`, so it is known before the session exists and survives
    /// the terminal being closed and reopened — `claude --resume` takes it straight back.
    /// </summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>
    /// The directory that session runs in. Stored per file rather than only per project because
    /// it is half of the transcript's path on disk, and a project's default can be changed later
    /// without stranding the sessions that were started under the old one.
    /// </summary>
    public string? SessionDir { get; set; }

    /// <summary>
    /// The terminal process the session was last seen in. A claim, not a fact: it is checked
    /// against the PID file and the live process list before anything is sent to it.
    /// </summary>
    public int? TerminalPid { get; set; }

    public DateTimeOffset? SessionStartedAt { get; set; }
}

public sealed class QueueEntry
{
    public required string File { get; set; }
    public required string Hash { get; set; }
}

public sealed class AppState
{
    public int Version { get; set; } = 1;

    /// <summary>Keyed by log-root-relative path with forward slashes.</summary>
    public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<QueueEntry> Queue { get; set; } = [];

    /// <summary>Manual reset override, used when transcript detection comes up empty.</summary>
    public DateTimeOffset? ManualResetAt { get; set; }

    /// <summary>The session open when the app last closed, reopened on launch.</summary>
    public string? LastSession { get; set; }
}

/// <summary>
/// Per-prompt state, keyed by file path plus prompt content hash. Content hashing rather than
/// indexing is what lets prompts be inserted, reordered or edited in Notepad++ without losing
/// what was already sent.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;

    public AppState State { get; private set; } = new();

    public static StateStore Load()
    {
        var store = new StateStore();
        try
        {
            if (File.Exists(Paths.StateFile))
            {
                store.State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(Paths.StateFile), Options)
                              ?? new AppState();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"state load failed, starting fresh: {ex.Message}");
            store.State = new AppState();
        }
        return store;
    }

    public FileState ForFile(string relativePath)
    {
        lock (_gate)
        {
            if (!State.Files.TryGetValue(relativePath, out var file))
            {
                State.Files[relativePath] = file = new FileState();
            }
            return file;
        }
    }

    public PromptState Prompt(string relativePath, string hash)
    {
        var file = ForFile(relativePath);
        lock (_gate)
        {
            if (!file.Prompts.TryGetValue(hash, out var state))
            {
                file.Prompts[hash] = state = new PromptState();
            }
            return state;
        }
    }

    /// <summary>The stored parse mode, or null when this file has never been opened by the app.</summary>
    public ParseMode? PeekFileMode(string relativePath)
    {
        lock (_gate)
        {
            return State.Files.TryGetValue(relativePath, out var file) ? file.Mode : null;
        }
    }

    public PromptState? PeekPrompt(string relativePath, string hash)
    {
        lock (_gate)
        {
            return State.Files.TryGetValue(relativePath, out var file) &&
                   file.Prompts.TryGetValue(hash, out var state)
                ? state
                : null;
        }
    }

    /// <summary>
    /// Carries a file's state across a rename. Everything here is keyed by relative path — the
    /// per-prompt statuses, the queue entries and the last-open session — so without this a rename
    /// silently loses which prompts had been sent and orphans anything queued from the file.
    /// </summary>
    public void RenameFile(string oldKey, string newKey)
    {
        if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            if (State.Files.Remove(oldKey, out var file)) State.Files[newKey] = file;

            foreach (var entry in State.Queue.Where(q =>
                         string.Equals(q.File, oldKey, StringComparison.OrdinalIgnoreCase)))
            {
                entry.File = newKey;
            }

            if (string.Equals(State.LastSession, oldKey, StringComparison.OrdinalIgnoreCase))
            {
                State.LastSession = newKey;
            }
        }
        MarkDirty();
    }

    /// <summary>Carries state across an edit that changed a prompt's hash.</summary>
    public void Rekey(string relativePath, string oldHash, string newHash)
    {
        if (oldHash == newHash) return;
        lock (_gate)
        {
            if (!State.Files.TryGetValue(relativePath, out var file)) return;
            if (!file.Prompts.Remove(oldHash, out var state)) return;
            file.Prompts[newHash] = state;

            foreach (var entry in State.Queue.Where(q =>
                         string.Equals(q.File, relativePath, StringComparison.OrdinalIgnoreCase) &&
                         q.Hash == oldHash))
            {
                entry.Hash = newHash;
            }
        }
        MarkDirty();
    }

    /// <summary>
    /// Drops state for prompts that no longer exist in the file, so state.json can't grow forever —
    /// but only Draft entries, which carry no information anyway.
    ///
    /// Sent and Queued entries are kept even when their hash isn't in the current parse: switching a
    /// file between Legacy and Modern re-splits it and changes every hash, and pruning on that would
    /// silently erase which prompts had already been sent. Stale entries are a few bytes each.
    /// </summary>
    public void Prune(string relativePath, IEnumerable<string> liveHashes)
    {
        var live = liveHashes.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            if (!State.Files.TryGetValue(relativePath, out var file)) return;

            var dead = file.Prompts
                .Where(p => !live.Contains(p.Key) && p.Value.Status == PromptStatus.Draft)
                .Select(p => p.Key)
                .ToList();

            foreach (var hash in dead) file.Prompts.Remove(hash);
            if (dead.Count > 0) _dirty = true;
        }
    }

    public void MarkDirty()
    {
        lock (_gate) _dirty = true;
    }

    /// <summary>Called on a timer; writes at most every few seconds and only when something changed.</summary>
    public void SaveIfDirty()
    {
        lock (_gate)
        {
            if (!_dirty || DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(2)) return;
        }
        Save();
    }

    public void Save()
    {
        try
        {
            Paths.EnsureAppDataDir();
            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(State, Options);
                _dirty = false;
                _lastSave = DateTime.UtcNow;
            }

            var tmp = Paths.StateFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(Paths.StateFile)) File.Replace(tmp, Paths.StateFile, null);
            else File.Move(tmp, Paths.StateFile);
        }
        catch (Exception ex)
        {
            Log.Warn($"state save failed: {ex.Message}");
        }
    }
}
