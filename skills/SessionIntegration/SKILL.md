# Session Integration — the session-limit countdown and the live usage bars

Applies to: `Core/QuotaWatcher.cs`, `Core/UsageWatcher.cs`, the `ClaudeProjectsDir` /
`ClaudeCredentialsFile` overrides in `Core/Paths.cs` and `Core/Settings.cs`, the `_quota`/`_usage`
wiring and `UpdateReset`/`UpdateUsage` in `ViewModels/MainWindowViewModel.cs`, and the SESSION LIMIT
panel in `Views/MainWindow.axaml`. Follow this when the countdown, the manual override, or the
usage bars stop matching what Claude Code itself is showing, or when adding a third data source to
that panel.

Two independent watchers feed the SESSION LIMIT panel, and they read from genuinely different
places for genuinely different reasons — that split is the thing to understand before touching
either one.

| | QuotaWatcher | UsageWatcher |
|---|---|---|
| Answers | "when does the block lift" | "how close am I, right now" |
| Reads | Claude Code's own transcripts (`%USERPROFILE%\.claude\projects\**\*.jsonl`) | Anthropic's usage API over HTTPS |
| Only has data | after a rejection has actually happened | continuously, whether or not anything's been rejected |
| Documented? | undocumented internal field, but shipped in a transcript on disk | undocumented endpoint, found by reading strings out of `claude.exe` |
| Writes to `.claude`? | never | never — also never refreshes the OAuth token |

Both are best-effort in the same way as everything else that reads outside this app's own files:
every failure path degrades to "no data detected" rather than showing a stale or wrong number, and
neither one throws out of `Start()` or its poll tick.

---

## QuotaWatcher — the reset time, from a rejected request

A rejected request leaves this in the transcript:

```json
"quotaLimits": { "status": "rejected", "resetsAt": 1788138000,
                 "rateLimitType": "five_hour", "overageStatus": "rejected" }
```

`resetsAt` is **unix seconds**. `QuotaWatcher` scans the whole `projects` tree, not one project
slug — the slug is derived from the directory Claude Code was launched in (`D--Source` for
`D:\Source`) and there are several, so it reads the 40 most recently written transcripts (last
2 MB of each, `FileShare.ReadWrite | Delete` since Claude Code is appending live) and keeps the
newest future `resetsAt` among rejected records.

If the format changes, `SelfTest.QuotaReadsARejectedRecord` is what fails — it builds a synthetic
transcript in the observed shape. Re-derive the shape from a real transcript if that ever happens;
the record only shows up once a rejection has actually occurred, so it can't be watched live.

**Detection is reliable enough that the manual override is hidden by default**
(`Settings.ShowManualReset`). It reappears on its own whenever an override is actually set, so a
hidden entry can't become unclearable.

**The expiry trap** (fixed once already — don't reintroduce it): the countdown crossing zero is
what fires the reset, so `MainWindowViewModel.EffectiveReset` must keep returning the manual
override for the moment *after* it passes. Dropping an expired override from that property makes
the reset silently never fire — the countdown just reads "No limit pending" forever and the queue
sits there. `OnResetReached` clears the override; the constructor clears one left stale by a
previous run.

---

## UsageWatcher — the live percentage, from Anthropic's own usage endpoint

`QuotaWatcher` only ever sees the all-or-nothing moment. `UsageWatcher` fills in the percentage
leading up to it — the same number Claude Code's own status line and the Desktop app show — by
polling the endpoint they call:

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>
```

`accessToken` comes from `%USERPROFILE%\.claude\.credentials.json` (`claudeAiOauth.accessToken`),
the same file Claude Code itself writes and refreshes. A real response looks like this (trimmed to
the fields `UsageWatcher.Parse` reads):

```json
{
  "five_hour": { "utilization": 50.0, "resets_at": "2026-09-01T22:49:59.570946+00:00" },
  "seven_day": { "utilization": 5.0,  "resets_at": "2026-09-08T12:59:59.570969+00:00" }
}
```

Two things are easy to get backwards if this is ever touched without rereading it first:

- **`utilization` is already a 0–100 percentage, not a fraction.** `50.0` means half used.
- **`resets_at` is an ISO-8601 string**, not the unix-seconds `resetsAt` QuotaWatcher reads. Don't
  share parsing between the two.

`five_hour` is the session limit (what the countdown panel calls "SESSION LIMIT"); `seven_day` is
the weekly limit, shown as the smaller secondary bar. Other keys on the real response
(`seven_day_opus`, `seven_day_sonnet`, `extra_usage`, `spend`, `limits`, …) exist for plans this
account doesn't have priced features on, and aren't parsed — `TryWindow` only ever looks at the
named window it's asked for and returns `false` on anything not shaped like `{ utilization,
resets_at }`, so an extra field elsewhere in the payload is silently ignored rather than breaking
the parse.

**Read-only, deliberately, and this is the one to actually respect:** `UsageWatcher` reads the
access token and **never writes to `.credentials.json` and never refreshes it**. Implementing our
own refresh would mean minting a new token via `grant_type=refresh_token` and persisting it into a
file Claude Code itself owns and rotates — a different order of risk than tailing a log file, and
not worth it for a progress bar. If the stored token expires (`expiresAt` in the same file, unix
*milliseconds*) with nothing else refreshing it, the percentage just goes stale — `ReadAccessToken`
returns `null` (with a 60-second buffer so a request doesn't start against a token that expires
mid-flight), `Fetch` returns `null`, and the UI falls back to "No limit pending" the same as if
`UsageWatcher` had never run. In practice this resolves itself the next time `claude` is used
anywhere on the machine, since that's an ordinary side effect of any real Claude Code session.

**This is an undocumented endpoint.** It doesn't appear in any published API reference — it was
found by grepping strings out of the installed `claude.exe` (a Bun-compiled binary; the bundled JS
source is plain text inside it and greppable with a binary-safe `grep -a`):

```bash
grep -a -o '"/api/[a-zA-Z0-9/_{}-]*"' claude.exe | sort -u    # → "/api/oauth/usage" among others
grep -a -o '.\{60\}utilization.\{60\}' claude.exe             # → the response's field names and the
                                                                #   eF={five_hour:"session limit",...}
                                                                #   label map Claude Code itself uses
```

The actual field shapes above were then confirmed against a **live call** (`Invoke-WebRequest` with
the real access token from `.credentials.json`) rather than trusted from the decompiled minified JS
alone — the minified code mixes camelCase and snake_case across different sub-objects, and the wire
format only became certain once a real response was in hand. If this endpoint 404s or its shape
changes, that's the sequence to repeat: grep the current `claude.exe` for the same strings first (it
may have moved), then make one live call with a valid token to see the real shape before writing any
parsing code against it.

Every failure path (missing/expired token, network error, non-2xx, reshaped JSON) logs a `Log.Warn`
and returns `null` rather than throwing — `UsageWatcher.FetchInBackground` is the only place
exceptions are caught, so `Fetch`/`Parse`/`ReadAccessToken` are free to just propagate up to it.
`UsageWatcher.Parse` is `internal static` specifically so `SelfTest` can pin the JSON shape without
a network call: `UsageParsesASessionAndWeeklyResponse` and
`UsageIgnoresNullWindowsAndMalformedResponses`.

---

## How MainWindowViewModel combines them

`_quota` and `_usage` are both constructed and started in the constructor (usage first — see the
comment there about why order matters: `_quota.Updated` calls `_usage.Refresh()`, so `_usage` has to
exist before `_quota.Start()` can possibly fire its event). Both dispose in `Dispose()`.

- **`EffectiveReset`** (session-limit-blocking state) is unchanged by any of this — it's still
  manual override, falling back to `_quota.Latest?.ResetsAt`, with the expiry trap above.
- **`UpdateReset()`** now also calls `UpdateUsage()` on every tick (once a second, same as
  everything else in that method) — cheap, since it's just copying whatever `_usage.Latest` already
  holds, not fetching anything. The percentage itself only actually changes on `UsageWatcher`'s own
  5-minute poll, or right after a rejection (`_usage.Refresh()`).
- **The headline switches meaning** depending on whether a block is currently active
  (`EffectiveReset is not null`): blocked shows the `Xh Ym Zs` countdown (how long until sendable
  again matters more than the percentage at that point); not blocked shows `{percent}% used`
  instead of the old bare "No limit pending", when `UsageWatcher` has data — falls back to "No limit
  pending" if it doesn't (expired token, offline, first launch before the initial poll lands).
- **The progress bars** (`SessionUsagePercent`, `HasSessionUsage`, `WeeklyUsagePercent`,
  `HasWeeklyUsage`, `WeeklyUsageDetail`) are independent of the headline and stay visible in both
  states — a session that's currently blocked still has a weekly percentage worth showing.

---

## Verifying a change here

```powershell
ClaudeLog --quota      # QuotaWatcher.Scan() against the real transcripts on this machine
ClaudeLog --usage      # one live UsageWatcher.FetchNowAsync() call, prints session % and weekly %
ClaudeLog --selftest   # QuotaReadsARejectedRecord, QuotaIgnoresPastAndMalformedRecords,
                        # UsageParsesASessionAndWeeklyResponse, UsageIgnoresNullWindowsAndMalformedResponses
```

`--selftest` never makes a network call — the usage tests are pure `UsageWatcher.Parse` calls
against a captured JSON string. `--usage` is the one that actually hits Anthropic, so it's the
command to run after touching anything in the fetch path, and it's also the fastest way to notice
if the endpoint has been reshaped upstream.

A second throwaway instance can point at a fake `.credentials.json` instead of the real login —
`CLAUDELOG_HOME` moves settings/state, and `Settings.ClaudeCredentialsFile` (like
`ClaudeProjectsDir`) can be pointed anywhere, including a scratch file with a deliberately expired
`expiresAt` to exercise the degrade path without waiting for the real token to expire.
