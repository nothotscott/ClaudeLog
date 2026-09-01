using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using ClaudeLog.Core;

namespace ClaudeLog.Views.Editing;

/// <summary>
/// Markdown highlighting for the prompt editor. Deliberately narrow — the things Scott's prompts
/// actually contain and that he asked to see at a glance: code (fenced and inline), bullets,
/// numbered steps, plus headings, bold and quotes since they cost nothing extra.
///
/// The fenced-block map is computed once per document version by the controller rather than
/// re-scanned per line: a line can only be told it's inside a fence by looking at everything above
/// it, and this runs for every visible line on every repaint.
/// </summary>
public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    private HashSet<int> _fencedLines = [];

    public bool Dark { get; set; } = true;

    public void SetFencedLines(HashSet<int> lines) => _fencedLines = lines;

    private IBrush Code => Brush(Color.FromRgb(0xCE, 0x91, 0x78), Color.FromRgb(0xA3, 0x31, 0x1B));
    private IBrush Marker => Brush(Color.FromRgb(0x56, 0x9C, 0xD6), Color.FromRgb(0x1B, 0x5E, 0xA6));
    private IBrush Heading => Brush(Color.FromRgb(0x4E, 0xC9, 0xB0), Color.FromRgb(0x0A, 0x7A, 0x66));
    private IBrush Quote => Brush(Color.FromRgb(0x8A, 0x9A, 0x8A), Color.FromRgb(0x60, 0x70, 0x60));

    private IBrush Brush(Color dark, Color light) => new SolidColorBrush(Dark ? dark : light);

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;

        var text = CurrentContext.Document.GetText(line);
        var start = line.Offset;

        if (_fencedLines.Contains(line.LineNumber))
        {
            Paint(start, start + line.Length, Code);
            return;
        }

        var trimmed = text.AsSpan().TrimStart();
        var indent = text.Length - trimmed.Length;

        if (trimmed.StartsWith("#"))
        {
            var hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes <= 6 && hashes < trimmed.Length && char.IsWhiteSpace(trimmed[hashes]))
            {
                Paint(start, start + line.Length, Heading, FontWeight.Bold);
                return;
            }
        }

        if (trimmed.StartsWith(">"))
        {
            Paint(start, start + line.Length, Quote);
            return;
        }

        PaintListMarker(text, trimmed, start, indent);
        PaintInlineCode(text, start);
        PaintBold(text, start);
    }

    /// <summary>`- item` and `1. item`: the marker is colored, the text is left alone.</summary>
    private void PaintListMarker(string text, ReadOnlySpan<char> trimmed, int start, int indent)
    {
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && char.IsWhiteSpace(trimmed[1]))
        {
            Paint(start + indent, start + indent + 1, Marker, FontWeight.Bold);
            return;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' &&
            char.IsWhiteSpace(trimmed[digits + 1]))
        {
            Paint(start + indent, start + indent + digits + 1, Marker, FontWeight.Bold);
        }

        _ = text;
    }

    private void PaintInlineCode(string text, int start)
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

            Paint(start + i, start + close + run, Code);
            i = close + run;
        }
    }

    private void PaintBold(string text, int start)
    {
        var i = 0;
        while (i < text.Length - 3)
        {
            if (text[i] == '*' && text[i + 1] == '*')
            {
                var close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close < 0) return;
                Paint(start + i, start + close + 2, null, FontWeight.Bold);
                i = close + 2;
                continue;
            }
            i++;
        }
    }

    private void Paint(int from, int to, IBrush? brush, FontWeight? weight = null)
    {
        if (to <= from) return;

        ChangeLinePart(from, to, element =>
        {
            if (brush is not null) element.TextRunProperties.SetForegroundBrush(brush);
            if (weight is not null)
            {
                element.TextRunProperties.SetTypeface(new Typeface(
                    element.TextRunProperties.Typeface.FontFamily, FontStyle.Normal, weight.Value));
            }
        });
    }

    /// <summary>
    /// Which lines sit inside a fenced block. Shares <see cref="PromptParser.MapFences"/> with the
    /// parser so the editor colors exactly what the parser treats as code.
    /// </summary>
    public static HashSet<int> MapFencedLines(string text)
    {
        var lines = PromptParser.SplitLines(text);
        var fenced = PromptParser.MapFences(lines);
        var result = new HashSet<int>();

        for (var i = 0; i < fenced.Length; i++)
        {
            if (fenced[i]) result.Add(i + 1); // AvaloniaEdit line numbers are 1-based
        }

        return result;
    }
}
