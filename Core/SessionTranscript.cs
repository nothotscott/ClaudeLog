using System.Text;
using System.Text.Json;

namespace ClaudeLog.Core;

/// <summary>
/// Reads back Claude Code's own transcript for one session, to confirm that a sent prompt actually
/// arrived.
///
/// Writing to a console always succeeds if the console exists, so a successful write proves the
/// terminal is alive and nothing more. It does not prove Claude Code took the text as a prompt: at
/// a permission prompt or a menu the same keystrokes answer the question on screen instead. The
/// transcript is the only place on disk that says a prompt was accepted, so that is what "sent"
/// gets checked against.
///
/// Like <see cref="QuotaWatcher"/> this reads an undocumented internal format, and degrades the
/// same way — an unreadable or absent transcript means "couldn't confirm", never an error. One
/// known case of that is real: a session launched from inside another Claude Code session inherits
/// CLAUDE_CODE_CHILD_SESSION and writes no transcript at all.
/// </summary>
public static class SessionTranscript
{
    private const int TailBytes = 256 * 1024;

    /// <summary>
    /// When the session last recorded a prompt from the user, or null if it has recorded none.
    ///
    /// Only the timestamp is compared, never the text. Claude Code rewrites what it stores — long
    /// pastes, command expansions and attachments all arrive in the transcript in a shape that no
    /// longer matches the characters that were typed — so matching on content would report a
    /// delivered prompt as missing.
    /// </summary>
    public static DateTimeOffset? LastPromptAt(string transcriptPath)
    {
        string tail;
        try
        {
            if (!File.Exists(transcriptPath)) return null;
            tail = ReadTail(transcriptPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"transcript: cannot read {Path.GetFileName(transcriptPath)}: {ex.Message}");
            return null;
        }

        // Walk backwards and stop at the first user entry: the newest is nearest the end. Line 0
        // is skipped because a tail read starts mid-line.
        var lines = tail.Split('\n');
        for (var i = lines.Length - 1; i >= 1; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var type) || type.GetString() != "user") continue;
                if (!root.TryGetProperty("timestamp", out var stamp)) continue;
                if (DateTimeOffset.TryParse(stamp.GetString(), out var when)) return when;
            }
            catch (JsonException)
            {
                // A truncated or reshaped line. Keep looking further back.
            }
        }

        return null;
    }

    /// <summary>
    /// Waits for the session to record a prompt newer than <paramref name="after"/>. False means
    /// it didn't within the timeout — which is a reason to go and look at the terminal, not proof
    /// that nothing was sent.
    /// </summary>
    public static async Task<bool> WaitForPromptAsync(string transcriptPath, DateTimeOffset after,
        TimeSpan timeout, CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, token);
            if (LastPromptAt(transcriptPath) is { } when && when > after) return true;
        }
        return false;
    }

    /// <summary>
    /// Reads the end of a file Claude Code is appending to, hence the permissive sharing — the
    /// same reason <see cref="QuotaWatcher"/> needs it.
    /// </summary>
    private static string ReadTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var take = (int)Math.Min(stream.Length, TailBytes);
        stream.Seek(stream.Length - take, SeekOrigin.Begin);

        var buffer = new byte[take];
        var read = stream.ReadAtLeast(buffer, take, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
