using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeLog.Core;

public sealed record QuotaSnapshot(DateTimeOffset ResetsAt, string RateLimitType, string Source, DateTime DetectedAt);

/// <summary>
/// Reads the session-limit reset time out of Claude Code's own transcripts. A rejected request
/// leaves a record like
///
///   "quotaLimits":{"status":"rejected","resetsAt":1788138000,"rateLimitType":"five_hour",...}
///
/// in %USERPROFILE%\.claude\projects\&lt;cwd-slug&gt;\&lt;session&gt;.jsonl, where resetsAt is unix seconds.
///
/// This is an undocumented internal format that can change without warning, so every failure path
/// here degrades to "no reset detected" and leaves the user's manual override in charge. Nothing
/// in this class writes to anything under .claude.
/// </summary>
public sealed partial class QuotaWatcher : IDisposable
{
    private const int TailBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 40;
    private static readonly TimeSpan Freshness = TimeSpan.FromDays(14);

    private readonly string _projectsDir;
    private readonly System.Timers.Timer _poll = new(60_000);
    private readonly System.Timers.Timer _debounce = new(3_000) { AutoReset = false };
    private FileSystemWatcher? _watcher;
    private int _scanning;

    public QuotaWatcher(string projectsDir)
    {
        _projectsDir = projectsDir;
        _poll.Elapsed += (_, _) => ScanInBackground();
        _debounce.Elapsed += (_, _) => ScanInBackground();
    }

    public QuotaSnapshot? Latest { get; private set; }

    /// <summary>Raised on a background thread whenever the detected reset time changes.</summary>
    public event Action<QuotaSnapshot?>? Updated;

    public void Start()
    {
        ScanInBackground();
        _poll.Start();

        if (!Directory.Exists(_projectsDir))
        {
            Log.Warn($"quota: {_projectsDir} not found; manual override only");
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_projectsDir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Restart(_debounce);
            _watcher.Created += (_, _) => Restart(_debounce);
        }
        catch (Exception ex)
        {
            Log.Warn($"quota watcher failed, falling back to polling: {ex.Message}");
        }
    }

    private static void Restart(System.Timers.Timer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private void ScanInBackground()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var found = Scan();
                if (found?.ResetsAt != Latest?.ResetsAt)
                {
                    Latest = found;
                    Updated?.Invoke(found);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"quota scan failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
            }
        });
    }

    /// <summary>
    /// Scans every recently-written transcript, not one project slug: the slug comes from the
    /// directory Claude Code was launched in, and there are several.
    /// </summary>
    public QuotaSnapshot? Scan()
    {
        if (!Directory.Exists(_projectsDir)) return null;

        var cutoff = DateTime.Now - Freshness;
        var files = new DirectoryInfo(_projectsDir)
            .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTime >= cutoff)
            .OrderByDescending(f => f.LastWriteTime)
            .Take(MaxFiles);

        QuotaSnapshot? best = null;
        foreach (var file in files)
        {
            foreach (var record in RecordsIn(file.FullName))
            {
                if (record.ResetsAt <= DateTimeOffset.Now) continue;
                if (best is null || record.ResetsAt > best.ResetsAt) best = record;
            }
        }

        return best;
    }

    private static IEnumerable<QuotaSnapshot> RecordsIn(string path)
    {
        string tail;
        try
        {
            tail = ReadTail(path, TailBytes);
        }
        catch (Exception ex)
        {
            Log.Warn($"quota: cannot read {Path.GetFileName(path)}: {ex.Message}");
            yield break;
        }

        foreach (Match match in QuotaLimitsRegex().Matches(tail))
        {
            QuotaSnapshot? snapshot = null;
            try
            {
                using var doc = JsonDocument.Parse(match.Groups[1].Value);
                var root = doc.RootElement;

                if (!root.TryGetProperty("resetsAt", out var resets) ||
                    !resets.TryGetInt64(out var seconds) || seconds <= 0)
                {
                    continue;
                }

                var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                if (!status.Contains("reject", StringComparison.OrdinalIgnoreCase)) continue;

                var type = root.TryGetProperty("rateLimitType", out var t) ? t.GetString() ?? "unknown" : "unknown";
                snapshot = new QuotaSnapshot(
                    DateTimeOffset.FromUnixTimeSeconds(seconds), type, Path.GetFileName(path), DateTime.Now);
            }
            catch (Exception ex)
            {
                // Truncated or reshaped record. Not fatal: the manual override still works.
                Log.Warn($"quota: unparsable record in {Path.GetFileName(path)}: {ex.Message}");
            }

            if (snapshot is not null) yield return snapshot;
        }
    }

    /// <summary>
    /// Reads the last <paramref name="maxBytes"/> of a file that Claude Code is actively appending
    /// to — hence FileShare.ReadWrite | Delete, and a UTF-8 decode that tolerates a chopped
    /// leading character.
    /// </summary>
    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var length = stream.Length;
        var take = (int)Math.Min(length, maxBytes);
        stream.Seek(length - take, SeekOrigin.Begin);

        var buffer = new byte[take];
        var read = stream.ReadAtLeast(buffer, take, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>The quotaLimits object holds only scalars and one string array, so brace matching stays trivial.</summary>
    [GeneratedRegex("\"quotaLimits\"\\s*:\\s*(\\{[^{}]*\\})", RegexOptions.Compiled)]
    private static partial Regex QuotaLimitsRegex();

    public void Dispose()
    {
        _watcher?.Dispose();
        _poll.Dispose();
        _debounce.Dispose();
    }
}
