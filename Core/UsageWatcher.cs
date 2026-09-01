using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeLog.Core;

public sealed record UsageSnapshot(
    double SessionPercent,
    DateTimeOffset SessionResetsAt,
    double? WeeklyPercent,
    DateTimeOffset? WeeklyResetsAt,
    DateTime FetchedAt);

/// <summary>
/// Polls the same usage endpoint Claude Code's own status line and the Desktop app read, for a
/// live percentage of the five-hour session limit (and the weekly limit alongside it) instead of
/// the all-or-nothing view <see cref="QuotaWatcher"/> gets from a rejected request.
///
/// GET https://api.anthropic.com/api/oauth/usage, bearer-authenticated with the OAuth access token
/// Claude Code already keeps in %USERPROFILE%\.claude\.credentials.json. This is an undocumented
/// endpoint — found by reading strings out of the installed claude.exe, not from any published API
/// — so every failure path degrades to "no usage detected" exactly like QuotaWatcher, and the
/// countdown detected there still works if this never does.
///
/// This class only ever reads .credentials.json. It never writes to it and never refreshes the
/// token itself — that stays Claude Code's job. If the stored access token has expired, usage
/// silently stops updating until something else (an ordinary `claude` session) refreshes it; that
/// tends to happen often enough in practice that it's not worth taking on token-refresh ourselves,
/// which would mean writing to a credentials file this app has otherwise never touched.
/// </summary>
public sealed class UsageWatcher : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromSeconds(60);

    private readonly string _credentialsFile;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly System.Timers.Timer _poll = new(5 * 60_000);
    private int _fetching;

    public UsageWatcher(string credentialsFile)
    {
        _credentialsFile = credentialsFile;
        _poll.Elapsed += (_, _) => FetchInBackground();
    }

    public UsageSnapshot? Latest { get; private set; }

    /// <summary>Raised on a background thread whenever the fetched percentages change.</summary>
    public event Action<UsageSnapshot?>? Updated;

    public void Start()
    {
        FetchInBackground();
        _poll.Start();
    }

    /// <summary>Worth calling right after a rejection is detected — that's exactly when the number
    /// is most likely to have moved, and the poll timer would otherwise leave it stale for minutes.</summary>
    public void Refresh() => FetchInBackground();

    private void FetchInBackground()
    {
        if (Interlocked.Exchange(ref _fetching, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                var found = await FetchNowAsync();
                if (Changed(found, Latest))
                {
                    Latest = found;
                    Updated?.Invoke(found);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"usage fetch failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _fetching, 0);
            }
        });
    }

    /// <summary>Ignores FetchedAt — otherwise every successful poll would look "changed" and fire
    /// an update even when the percentages haven't moved.</summary>
    private static bool Changed(UsageSnapshot? a, UsageSnapshot? b) =>
        a?.SessionPercent != b?.SessionPercent ||
        a?.SessionResetsAt != b?.SessionResetsAt ||
        a?.WeeklyPercent != b?.WeeklyPercent ||
        a?.WeeklyResetsAt != b?.WeeklyResetsAt;

    /// <summary>One live call, independent of the poll timer — what `ClaudeLog --usage` uses.</summary>
    public async Task<UsageSnapshot?> FetchNowAsync()
    {
        var token = ReadAccessToken();
        if (token is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"usage: {(int)response.StatusCode} from {UsageUrl}");
            return null;
        }

        return Parse(await response.Content.ReadAsStringAsync());
    }

    private string? ReadAccessToken()
    {
        try
        {
            if (!System.IO.File.Exists(_credentialsFile)) return null;

            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(_credentialsFile));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            if (!oauth.TryGetProperty("accessToken", out var tokenEl)) return null;

            var token = tokenEl.GetString();
            if (string.IsNullOrEmpty(token)) return null;

            if (oauth.TryGetProperty("expiresAt", out var expEl) && expEl.TryGetInt64(out var expiresAtMs))
            {
                var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs);
                if (expiresAt <= DateTimeOffset.UtcNow + TokenExpiryBuffer) return null;
            }

            return token;
        }
        catch (Exception ex)
        {
            Log.Warn($"usage: cannot read credentials: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The response shape, confirmed against a live call: { "five_hour": { "utilization": 50.0,
    /// "resets_at": "2026-09-01T22:49:59Z", ... } | null, "seven_day": {...} | null, ... }.
    /// utilization is already a 0-100 percentage, not a fraction. five_hour is the session limit;
    /// seven_day is the weekly limit. Internal, and static, so SelfTest can check it without a
    /// network call.
    /// </summary>
    internal static UsageSnapshot? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!TryWindow(root, "five_hour", out var sessionPercent, out var sessionResets) || sessionResets is null)
        {
            return null;
        }

        TryWindow(root, "seven_day", out var weeklyPercentValue, out var weeklyResets);
        double? weeklyPercent = weeklyResets is null ? null : weeklyPercentValue;

        return new UsageSnapshot(sessionPercent, sessionResets.Value, weeklyPercent, weeklyResets, DateTime.Now);
    }

    private static bool TryWindow(JsonElement root, string name, out double percent, out DateTimeOffset? resetsAt)
    {
        percent = 0;
        resetsAt = null;

        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return false;
        if (!window.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return false;

        percent = u.GetDouble();
        if (window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(r.GetString(), out var parsed))
        {
            resetsAt = parsed;
        }

        return true;
    }

    public void Dispose()
    {
        _poll.Dispose();
        _http.Dispose();
    }
}
