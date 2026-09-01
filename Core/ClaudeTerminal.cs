using System.Diagnostics;

namespace ClaudeLog.Core;

/// <summary>A terminal ClaudeLog started, and the Claude Code conversation running inside it.</summary>
public sealed record TerminalSession(string SessionId, string Dir, int Pid)
{
    /// <summary>The window name given to Windows Terminal; short enough to read in a command line.</summary>
    public string WindowName => WindowNameFor(SessionId);

    public static string WindowNameFor(string sessionId) => "cl-" + sessionId[..8];
}

/// <summary>
/// Starts Claude Code in a terminal and keeps hold of enough to talk to it again: the session
/// GUID, the directory it runs in, and the PID whose console <see cref="ConsoleInput"/> writes to.
///
/// **ClaudeLog chooses the session GUID rather than discovering it.** `claude --session-id &lt;uuid&gt;`
/// takes the id as an argument, so the id is known before the process exists and can be written
/// into state.json alongside the log file it belongs to. Discovering it afterwards — watching for
/// a new transcript to appear under .claude\projects — would be a guess whenever two sessions
/// start close together.
///
/// The terminal is Windows Terminal by default but nothing here depends on it. The mechanism is
/// the Win32 console, so any host that gives its tab a real console works, conhost included; the
/// command line is a setting for exactly that reason.
/// </summary>
public static class ClaudeTerminal
{
    /// <summary>How long to wait for the launched shell to report its PID before giving up.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Claude Code's directory name for a working directory: every character that isn't a letter
    /// or a digit becomes a hyphen, so `D:\Source` is `D--Source`. That is how the transcript for
    /// a known session id can be found on disk, which is what turns "sent" from a hopeful label
    /// into something the app has actually seen arrive.
    /// </summary>
    public static string SlugFor(string dir)
    {
        var trimmed = dir.TrimEnd('\\', '/');
        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsAsciiLetterOrDigit(source[i]) ? source[i] : '-';
            }
        });
    }

    /// <summary>Where Claude Code writes the transcript for a session started in a directory.</summary>
    public static string TranscriptPath(string projectsDir, string dir, string sessionId) =>
        Path.Combine(projectsDir, SlugFor(dir), sessionId + ".jsonl");

    /// <summary>A new session id. Just a GUID — `--session-id` requires a well-formed UUID.</summary>
    public static string NewSessionId() => Guid.NewGuid().ToString();

    /// <summary>True while the process behind a stored PID is still the terminal we started.</summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            // GetProcessById throws for a PID that no longer exists; that is the answer, not an error.
            return false;
        }
    }

    /// <summary>
    /// Opens a terminal running Claude Code for <paramref name="sessionId"/> in
    /// <paramref name="dir"/>, and returns it once the shell inside has reported its PID.
    /// Throws with a sentence worth showing in the status bar when it can't.
    /// </summary>
    public static async Task<TerminalSession> StartAsync(Settings settings, string sessionId, string dir,
        string title, CancellationToken token = default)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"{dir} does not exist");

        Directory.CreateDirectory(Paths.TabsDir);
        var pidFile = Path.Combine(Paths.TabsDir, sessionId + ".pid");
        var script = Path.Combine(Paths.TabsDir, sessionId + ".ps1");
        Delete(pidFile);

        var resuming = File.Exists(TranscriptPath(settings.ClaudeProjectsDir, dir, sessionId));
        File.WriteAllText(script, LaunchScript(settings.ClaudeExe, sessionId, pidFile, resuming));

        var args = string.Format(settings.TerminalArgs,
            TerminalSession.WindowNameFor(sessionId), Quote(title), Quote(dir), Quote(script));

        Start(settings.TerminalExe, args);

        var pid = await WaitForPidAsync(pidFile, token);
        if (pid is null)
        {
            // The command line is the only thing worth having here: a terminal that opens and
            // closes, or never opens, is almost always a TerminalArgs that the host didn't accept.
            Log.Warn($"terminal did not report a pid: {settings.TerminalExe} {args}");
            throw new TimeoutException(
                $"The terminal didn't start within {StartTimeout.TotalSeconds:0}s — " +
                $"check TerminalExe and TerminalArgs in settings.json (logged the command line)");
        }

        return new TerminalSession(sessionId, dir, pid.Value);
    }

    /// <summary>
    /// The script the terminal tab runs. It exists as a file rather than as a command line because
    /// Windows Terminal treats `;` as its own argument separator, so any inline PowerShell that
    /// needs a statement separator has to be escaped through two layers of quoting to survive.
    ///
    /// Reporting `$PID` is what makes the tab addressable: the shell and Claude Code share one
    /// console, so writing to the shell's console is writing to Claude Code's input.
    /// </summary>
    private static string LaunchScript(string claudeExe, string sessionId, string pidFile, bool resuming)
    {
        var flag = resuming ? "--resume" : "--session-id";

        // Two dollars: PowerShell is full of braces and $, and this way only {{...}} interpolates.
        return $$"""
            # Written by ClaudeLog. Recreated on every launch; editing it has no lasting effect.
            $PID | Set-Content -Path '{{pidFile}}' -Encoding ascii
            & '{{claudeExe}}' {{flag}} '{{sessionId}}'
            $code = $LASTEXITCODE
            Remove-Item -Path '{{pidFile}}' -ErrorAction SilentlyContinue
            if ($code -ne 0) {
                Write-Host ''
                Write-Host "Claude Code exited with $code. Press any key to close." -ForegroundColor Yellow
                [void][Console]::ReadKey($true)
            }

            """.ReplaceLineEndings("\r\n");
    }

    /// <summary>
    /// The launched `wt.exe` hands the tab to the already-running Windows Terminal process and
    /// exits, so its own PID is worthless — the tab reports the PID that matters by writing this
    /// file. Polling for it is also how a launch failure is detected: no file, no terminal.
    /// </summary>
    private static async Task<int?> WaitForPidAsync(string pidFile, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150, token);
            try
            {
                if (!File.Exists(pidFile)) continue;
                var text = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(text, out var pid) && IsAlive(pid)) return pid;
            }
            catch (IOException)
            {
                // Caught it mid-write. The next pass reads a whole number.
            }
        }
        return null;
    }

    /// <summary>Brings an already-running session's terminal to the front.</summary>
    public static void Show(Settings settings, TerminalSession session)
    {
        Start(settings.TerminalExe, string.Format(settings.TerminalShowArgs, session.WindowName));
    }

    /// <summary>
    /// Reconnects to a terminal that outlived a restart of the app. The PID in state.json is only
    /// a claim; the PID file is what the tab is still holding, so it wins.
    /// </summary>
    public static int? Reattach(string sessionId, int? rememberedPid)
    {
        try
        {
            var pidFile = Path.Combine(Paths.TabsDir, sessionId + ".pid");
            if (File.Exists(pidFile) &&
                int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) &&
                IsAlive(pid))
            {
                return pid;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"terminal reattach {sessionId}: {ex.Message}");
        }

        return rememberedPid is not null && IsAlive(rememberedPid.Value) ? rememberedPid : null;
    }

    /// <summary>Forgets a session's launch artefacts. The conversation itself is untouched.</summary>
    public static void Forget(string sessionId)
    {
        Delete(Path.Combine(Paths.TabsDir, sessionId + ".pid"));
        Delete(Path.Combine(Paths.TabsDir, sessionId + ".ps1"));
    }

    /// <summary>Windows Terminal splits its own arguments on spaces, so paths have to be quoted.</summary>
    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';

    /// <summary>
    /// Claude Code marks its own environment, and a session that inherits those markers is treated
    /// as a nested one: it writes no transcript at all. That would be invisible here — the terminal
    /// opens, prompts arrive, and only the confirmation and the quota countdown quietly stop
    /// working — so the markers are cleared for the terminal ClaudeLog starts.
    ///
    /// It matters because ClaudeLog gets launched from a Claude Code session all the time: that is
    /// what "run the app" does while the app is being worked on.
    /// </summary>
    private static readonly string[] InheritedClaudeMarkers =
        ["CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_ENTRYPOINT",
         "CLAUDE_CODE_BRIDGE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
         "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID"];

    private static void Start(string exe, string args)
    {
        // UseShellExecute has to be off to touch the child's environment, which is the whole point.
        var info = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var marker in InheritedClaudeMarkers) info.Environment.Remove(marker);
        Process.Start(info)?.Dispose();
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"terminal: could not delete {path}: {ex.Message}");
        }
    }
}
