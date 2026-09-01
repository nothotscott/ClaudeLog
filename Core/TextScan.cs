namespace ClaudeLog.Core;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public bool Contains(int offset) => offset >= Start && offset < End;
}

/// <summary>
/// The lexical facts both the highlighter and the spell checker need: where the code is, and what
/// counts as a word. Kept in one place so a squiggle can never appear under something the
/// highlighter is painting as code.
/// </summary>
public static class TextScan
{
    /// <summary>
    /// Fenced blocks, inline `code`, URLs and file paths — everything spell checking must leave
    /// alone. Scott's prompts are full of pasted SIP traces, JSON and Windows paths; without this
    /// the editor would be a wall of red.
    /// </summary>
    public static List<TextSpan> CodeAndPathSpans(string text)
    {
        var spans = new List<TextSpan>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            // Fenced block: ``` or ~~~ at the start of a line, closed by a matching run or EOF.
            if ((c == '`' || c == '~') && AtLineStart(text, i))
            {
                var run = RunLength(text, i, c);
                if (run >= 3)
                {
                    var end = FindFenceEnd(text, i + run, c, run);
                    spans.Add(new TextSpan(i, end - i));
                    i = end;
                    continue;
                }
            }

            // Inline code span: a backtick run closed by an equal run on the same line.
            if (c == '`')
            {
                var run = RunLength(text, i, '`');
                var close = FindInlineClose(text, i + run, run);
                if (close > 0)
                {
                    spans.Add(new TextSpan(i, close + run - i));
                    i = close + run;
                    continue;
                }
            }

            // URLs and paths: run to the next whitespace.
            if (IsUrlOrPathStart(text, i))
            {
                var end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                spans.Add(new TextSpan(i, end - i));
                i = end;
                continue;
            }

            i++;
        }

        return spans;
    }

    private static bool AtLineStart(string text, int i)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            if (text[j] == '\n') return true;
            if (!char.IsWhiteSpace(text[j])) return false;
        }
        return true;
    }

    private static int RunLength(string text, int start, char c)
    {
        var n = 0;
        while (start + n < text.Length && text[start + n] == c) n++;
        return n;
    }

    private static int FindFenceEnd(string text, int from, char fence, int fenceLength)
    {
        var i = from;
        while (i < text.Length)
        {
            if (text[i] == '\n' && AtLineStart(text, i + 1))
            {
                var j = i + 1;
                while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
                if (j < text.Length && text[j] == fence && RunLength(text, j, fence) >= fenceLength)
                {
                    var close = j + RunLength(text, j, fence);
                    while (close < text.Length && text[close] != '\n') close++;
                    return close;
                }
            }
            i++;
        }
        return text.Length;
    }

    private static int FindInlineClose(string text, int from, int run)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '\n') return -1;
            if (text[i] == '`' && RunLength(text, i, '`') == run) return i;
        }
        return -1;
    }

    private static bool IsUrlOrPathStart(string text, int i)
    {
        if (i > 0 && !char.IsWhiteSpace(text[i - 1])) return false;

        return text.AsSpan(i).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || text.AsSpan(i).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || text.AsSpan(i).StartsWith("www.", StringComparison.OrdinalIgnoreCase)
               || IsWindowsPath(text, i);
    }

    private static bool IsWindowsPath(string text, int i) =>
        i + 2 < text.Length && char.IsAsciiLetter(text[i]) && text[i + 1] == ':' &&
        (text[i + 2] == '\\' || text[i + 2] == '/');

    /// <summary>
    /// Words that aren't prose and shouldn't be spell checked even outside code: identifiers
    /// (`AIMediaSession`, `resetsAt`), anything with digits or underscores, and acronyms.
    /// </summary>
    public static bool LooksLikeCode(string word)
    {
        if (word.Length < 2) return true;

        var upper = 0;
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (char.IsAsciiDigit(c) || c == '_') return true;
            if (char.IsUpper(c))
            {
                upper++;
                if (i > 0) return true; // internal capital: camelCase or PascalCase
            }
        }

        return upper == word.Length && word.Length >= 3; // SIP, RTP, DID
    }

    /// <summary>The word containing or immediately before <paramref name="offset"/>.</summary>
    public static TextSpan WordAt(string text, int offset)
    {
        if (text.Length == 0) return new TextSpan(0, 0);

        var start = Math.Clamp(offset, 0, text.Length);
        while (start > 0 && IsWordChar(text[start - 1])) start--;

        var end = Math.Clamp(offset, 0, text.Length);
        while (end < text.Length && IsWordChar(text[end])) end++;

        return new TextSpan(start, end - start);
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\'';

    /// <summary>Word tokens for the completion dictionary.</summary>
    public static IEnumerable<string> Words(string text, int minLength)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var word = i < text.Length && IsWordChar(text[i]);
            if (word)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0)
            {
                var length = i - start;
                if (length >= minLength && char.IsLetter(text[start]))
                {
                    yield return text.Substring(start, length);
                }
                start = -1;
            }
        }
    }
}
