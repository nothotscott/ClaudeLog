using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using ClaudeLog.Core;

namespace ClaudeLog.Views.Editing;

/// <summary>
/// Paints <see cref="MarkdownScanner"/>'s spans into the prompt editor.
///
/// The fenced-block map is computed once per document version by the controller rather than
/// re-scanned per line: a line can only be told it's inside a fence by looking at everything above
/// it, and this runs for every visible line on every repaint.
/// </summary>
public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    private readonly List<MarkdownSpan> _spans = [];
    private HashSet<int> _fencedLines = [];

    public bool Dark { get; set; } = true;

    public void SetFencedLines(HashSet<int> lines) => _fencedLines = lines;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;

        _spans.Clear();
        MarkdownScanner.ScanLine(CurrentContext.Document.GetText(line), _fencedLines.Contains(line.LineNumber), _spans);

        foreach (var span in _spans)
        {
            Paint(line.Offset + span.Start, line.Offset + span.Start + span.Length,
                MarkdownPalette.For(span.Role, Dark), span.Bold ? FontWeight.Bold : null);
        }
    }

    private void Paint(int from, int to, IBrush? brush, FontWeight? weight)
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

    /// <summary>Which lines sit inside a fenced block, as AvaloniaEdit's 1-based line numbers.</summary>
    public static HashSet<int> MapFencedLines(string text)
    {
        var fenced = PromptParser.MapFences(PromptParser.SplitLines(text));
        var result = new HashSet<int>();

        for (var i = 0; i < fenced.Length; i++)
        {
            if (fenced[i]) result.Add(i + 1);
        }

        return result;
    }
}
