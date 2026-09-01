namespace ClaudeLog.Core;

public enum MarkdownRole
{
    /// <summary>Ordinary prose — inherits whatever foreground the control already has.</summary>
    None,
    Code,
    Marker,
    Heading,
    Quote,
}

/// <summary>A styled run within one line, offsets relative to the start of that line.</summary>
public readonly record struct MarkdownSpan(int Start, int Length, MarkdownRole Role, bool Bold);

/// <summary>
/// The markdown highlighting rules, as spans over a single line. Deliberately narrow — the things
/// Scott's prompts actually contain and that he asked to see at a glance: code (fenced and inline),
/// bullets, numbered steps, plus headings, bold and quotes since they cost nothing extra.
///
/// Kept here, apart from any renderer, because two of them consume it: the editor paints spans
/// through AvaloniaEdit, and the prompt list builds inlines for a TextBlock. One rule set is what
/// stops the same prompt from looking like two different documents in the two panes.
///
/// Spans may overlap, and later ones win — the same precedence a colorizer has always had, since
/// painting a run just overwrites the properties it was handed.
/// </summary>
public static class MarkdownScanner
{
    public static void ScanLine(string text, bool fenced, List<MarkdownSpan> into)
    {
        if (text.Length == 0) return;

        if (fenced)
        {
            into.Add(new MarkdownSpan(0, text.Length, MarkdownRole.Code, false));
            return;
        }

        var trimmed = text.AsSpan().TrimStart();
        var indent = text.Length - trimmed.Length;

        if (IsHeading(trimmed))
        {
            into.Add(new MarkdownSpan(0, text.Length, MarkdownRole.Heading, true));
            return;
        }

        if (trimmed.StartsWith(">"))
        {
            into.Add(new MarkdownSpan(0, text.Length, MarkdownRole.Quote, false));
            return;
        }

        ScanListMarker(trimmed, indent, into);
        ScanInlineCode(text, into);
        ScanBold(text, into);
    }

    private static bool IsHeading(ReadOnlySpan<char> trimmed)
    {
        if (!trimmed.StartsWith("#")) return false;

        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
        return hashes <= 6 && hashes < trimmed.Length && char.IsWhiteSpace(trimmed[hashes]);
    }

    /// <summary>`- item` and `1. item`: the marker is colored, the text is left alone.</summary>
    private static void ScanListMarker(ReadOnlySpan<char> trimmed, int indent, List<MarkdownSpan> into)
    {
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && char.IsWhiteSpace(trimmed[1]))
        {
            into.Add(new MarkdownSpan(indent, 1, MarkdownRole.Marker, true));
            return;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' &&
            char.IsWhiteSpace(trimmed[digits + 1]))
        {
            into.Add(new MarkdownSpan(indent, digits + 1, MarkdownRole.Marker, true));
        }
    }

    private static void ScanInlineCode(string text, List<MarkdownSpan> into)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }

            var run = 1;
            while (i + run < text.Length && text[i + run] == '`') run++;

            var close = -1;
            for (var j = i + run; j < text.Length; j++)
            {
                if (text[j] != '`') continue;
                var closeRun = 1;
                while (j + closeRun < text.Length && text[j + closeRun] == '`') closeRun++;
                if (closeRun == run)
                {
                    close = j;
                    break;
                }
                j += closeRun - 1;
            }

            if (close < 0) return;

            into.Add(new MarkdownSpan(i, close + run - i, MarkdownRole.Code, false));
            i = close + run;
        }
    }

    private static void ScanBold(string text, List<MarkdownSpan> into)
    {
        var i = 0;
        while (i < text.Length - 3)
        {
            if (text[i] == '*' && text[i + 1] == '*')
            {
                var close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close < 0) return;
                into.Add(new MarkdownSpan(i, close + 2 - i, MarkdownRole.None, true));
                i = close + 2;
                continue;
            }
            i++;
        }
    }
}
