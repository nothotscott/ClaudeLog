using System.Security.Cryptography;
using System.Text;

namespace ClaudeLog.Core;

public enum ParseMode
{
    /// <summary>Blank line(s) separate prompts. What every hand-written log file uses.</summary>
    Legacy,

    /// <summary>A `---` line separates prompts; blank lines are just formatting.</summary>
    Modern,
}

/// <summary>One prompt inside a session file, located by line range in the source text.</summary>
public sealed class Prompt
{
    public required int Index { get; init; }

    /// <summary>First line of the prompt body, 0-based, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>One past the last line of the prompt body.</summary>
    public required int EndLine { get; init; }

    /// <summary>Body text, LF-normalized, with surrounding blank lines trimmed.</summary>
    public required string Text { get; init; }

    private string? _hash;

    /// <summary>Stable identity across edits elsewhere in the file, moves and re-syncs.</summary>
    public string Hash => _hash ??= PromptParser.HashOf(Text);

    public string Preview
    {
        get
        {
            var line = Text.AsSpan();
            var nl = line.IndexOf('\n');
            var first = (nl < 0 ? line : line[..nl]).Trim().ToString();
            if (first.Length == 0) first = "(empty)";
            return first.Length <= 90 ? first : first[..89] + "…";
        }
    }

    public int LineCount => EndLine - StartLine;
}

/// <summary>
/// Splits session files into prompts. Both modes are fence-aware: nothing inside a ``` or ~~~
/// block is ever treated as a boundary, because pasted logs and code are full of blank lines
/// and horizontal rules.
/// </summary>
public static class PromptParser
{
    public static IReadOnlyList<Prompt> Parse(string text, ParseMode mode)
    {
        var lines = SplitLines(text);
        var fenced = MapFences(lines);
        return mode == ParseMode.Modern
            ? ParseModern(lines, fenced)
            : ParseLegacy(lines, fenced);
    }

    /// <summary>
    /// Boundary: a line of three or more dashes, outside a code fence.
    /// </summary>
    private static List<Prompt> ParseModern(string[] lines, bool[] fenced)
    {
        var prompts = new List<Prompt>();
        var start = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (fenced[i] || !IsRule(lines[i])) continue;
            Emit(prompts, lines, start, i);
            start = i + 1;
        }
        Emit(prompts, lines, start, lines.Length);
        return prompts;
    }

    /// <summary>
    /// Boundary: a blank line outside a code fence — except where the text says otherwise.
    ///
    /// A blank line is genuinely ambiguous in these files: it separates two prompts in
    /// `DevMem\dev_mem_continued.md`, and separates paragraphs of a single 68-line briefing
    /// document in `CallTree\call_tree.md`. Counting blank lines alone can't tell them apart —
    /// call_tree.md contains exactly one double blank in 540 lines, so treating doubles as the
    /// boundary collapses its 28 prompts into 2.
    ///
    /// What does tell them apart is the text on either side of the gap. A block that opens with
    /// a markdown structural marker (heading, list, quote, table, fence, bold lead-in) continues
    /// the document above it rather than starting a new prompt, and a line ending in a colon is
    /// introducing whatever follows — "I ran the script and got the following result:" belongs to
    /// its output. Everything else is a boundary, and a double blank always is.
    /// </summary>
    private static List<Prompt> ParseLegacy(string[] lines, bool[] fenced)
    {
        var prompts = new List<Prompt>();
        var start = 0;
        var i = 0;

        while (i < lines.Length)
        {
            if (fenced[i] || lines[i].Trim().Length != 0)
            {
                i++;
                continue;
            }

            var runStart = i;
            while (i < lines.Length && !fenced[i] && lines[i].Trim().Length == 0) i++;
            if (i >= lines.Length) break;
            if (runStart <= start) continue;

            // A prompt that has opened a markdown heading is a document being pasted in one piece
            // (call_tree.md's project brief), and its own paragraph breaks aren't prompt breaks.
            // Only an explicit double blank ends it.
            var doubleBlank = i - runStart >= 2;
            var inDocument = !doubleBlank && HasHeading(lines, fenced, start, runStart);

            if (doubleBlank || (!inDocument && IsBoundary(PreviousNonBlank(lines, runStart - 1), lines[i])))
            {
                Emit(prompts, lines, start, runStart);
                start = i;
            }
        }

        Emit(prompts, lines, start, lines.Length);
        return prompts;
    }

    private static bool IsBoundary(string previous, string next) =>
        !ContinuesBlock(next) && !IntroducesBlock(previous);

    /// <summary>Markdown structure that belongs to the block above it, not to a new prompt.</summary>
    public static bool ContinuesBlock(string line)
    {
        var s = line.AsSpan().TrimStart();
        if (s.Length == 0) return false;

        if (s[0] is '#' or '>' or '|') return true;
        if (s.StartsWith("```") || s.StartsWith("~~~")) return true;
        if (s.StartsWith("**")) return true;
        if (s.Length >= 2 && s[0] is '-' or '*' or '+' && char.IsWhiteSpace(s[1])) return true;
        if (IsRule(line)) return true;

        var digits = 0;
        while (digits < s.Length && char.IsAsciiDigit(s[digits])) digits++;
        return digits > 0 && digits + 1 < s.Length && s[digits] is '.' or ')' && char.IsWhiteSpace(s[digits + 1]);
    }

    /// <summary>A line ending in a colon is introducing the block after it.</summary>
    public static bool IntroducesBlock(string line)
    {
        var s = line.AsSpan().Trim();
        return s.Length > 0 && (s[^1] == ':' || s[0] == '#');
    }

    /// <summary>True when the lines gathered so far contain an ATX heading outside a fence.</summary>
    private static bool HasHeading(string[] lines, bool[] fenced, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (fenced[i]) continue;
            var s = lines[i].AsSpan().TrimStart();
            var hashes = 0;
            while (hashes < s.Length && s[hashes] == '#') hashes++;
            if (hashes is > 0 and <= 6 && hashes < s.Length && char.IsWhiteSpace(s[hashes])) return true;
        }
        return false;
    }

    private static string PreviousNonBlank(string[] lines, int from)
    {
        for (var i = from; i >= 0; i--)
        {
            if (lines[i].Trim().Length > 0) return lines[i];
        }
        return string.Empty;
    }

    private static void Emit(List<Prompt> prompts, string[] lines, int start, int end)
    {
        // Trim blank lines off both ends so the body is exactly what gets copied.
        while (start < end && lines[start].Trim().Length == 0) start++;
        while (end > start && lines[end - 1].Trim().Length == 0) end--;
        if (end <= start) return;

        prompts.Add(new Prompt
        {
            Index = prompts.Count,
            StartLine = start,
            EndLine = end,
            Text = string.Join("\n", lines[start..end]),
        });
    }

    /// <summary>Per-line "is inside a fenced code block" map. The fence lines themselves count as inside.</summary>
    public static bool[] MapFences(string[] lines)
    {
        var inside = new bool[lines.Length];
        char fenceChar = '\0';
        var fenceLen = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var span = lines[i].AsSpan().TrimStart();
            var c = span.Length > 0 ? span[0] : '\0';
            var run = 0;
            if (c == '`' || c == '~')
            {
                while (run < span.Length && span[run] == c) run++;
            }

            if (fenceLen == 0)
            {
                if (run >= 3)
                {
                    fenceChar = c;
                    fenceLen = run;
                    inside[i] = true;
                }
            }
            else
            {
                inside[i] = true;
                // A closing fence is the same character, at least as long, and carries no info string.
                if (c == fenceChar && run >= fenceLen && span[run..].Trim().Length == 0)
                {
                    fenceLen = 0;
                    fenceChar = '\0';
                }
            }
        }

        return inside;
    }

    /// <summary>
    /// True when the file already uses `---` separators. Files the app didn't write default to
    /// Legacy, but Scott has started writing `---` by hand — when they're there, honor them.
    /// </summary>
    public static bool LooksModern(string text)
    {
        var lines = SplitLines(text);
        var fenced = MapFences(lines);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!fenced[i] && IsRule(lines[i])) return true;
        }
        return false;
    }

    public static bool IsRule(string line)
    {
        var s = line.AsSpan().Trim();
        if (s.Length < 3) return false;
        foreach (var c in s)
        {
            if (c != '-') return false;
        }
        return true;
    }

    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Identity of a prompt: SHA-256 of the body with trailing whitespace and line endings
    /// normalized away, truncated. Whitespace-only edits must not orphan the prompt's state.
    /// </summary>
    public static string HashOf(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in SplitLines(text))
        {
            sb.Append(line.TrimEnd()).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString().Trim()));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
