using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using ClaudeLog.Core;

namespace ClaudeLog.Views.Editing;

/// <summary>
/// A read-only block of markdown, highlighted by the same rules as the editor.
///
/// This is what makes the prompt list read like the session file rather than like a list of
/// summaries: each prompt is shown whole, wrapped, and colored exactly the way it is colored one
/// pane down. It is a TextBlock rather than a second TextEditor on purpose — fifty editors in a
/// list would each bring a caret, an undo stack and a text area, and none of that belongs in a
/// view you only read.
/// </summary>
public sealed class MarkdownBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Source));

    private readonly List<MarkdownSpan> _spans = [];

    public MarkdownBlock()
    {
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    /// <summary>The markdown to show. Named Source because TextBlock.Text means something else here.</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty) Rebuild();
    }

    private void Rebuild()
    {
        var source = Source;
        if (string.IsNullOrEmpty(source))
        {
            Inlines = null;
            return;
        }

        var dark = ActualThemeVariant != ThemeVariant.Light;
        var lines = PromptParser.SplitLines(source);
        var fenced = PromptParser.MapFences(lines);
        var inlines = new InlineCollection();

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());
            AppendLine(inlines, lines[i], fenced[i], dark);
        }

        Inlines = inlines;
    }

    private void AppendLine(InlineCollection into, string line, bool fenced, bool dark)
    {
        if (line.Length == 0) return;

        _spans.Clear();
        MarkdownScanner.ScanLine(line, fenced, _spans);

        if (_spans.Count == 0)
        {
            into.Add(new Run(line));
            return;
        }

        // Flatten to one attribute per character before emitting runs. The spans can overlap —
        // bold inside a code span, a marker inside a list line — and resolving them per character
        // reproduces the editor's "later span wins" precedence exactly, without special cases.
        var roles = new MarkdownRole[line.Length];
        var bold = new bool[line.Length];

        foreach (var span in _spans)
        {
            var end = Math.Min(span.Start + span.Length, line.Length);
            for (var i = Math.Max(span.Start, 0); i < end; i++)
            {
                if (span.Role != MarkdownRole.None) roles[i] = span.Role;
                if (span.Bold) bold[i] = true;
            }
        }

        var start = 0;
        for (var i = 1; i <= line.Length; i++)
        {
            if (i < line.Length && roles[i] == roles[start] && bold[i] == bold[start]) continue;

            // Only ever *set* these. Assigning null or Normal writes a local value that stops the
            // run inheriting the control's, and a run with a null brush paints nothing at all.
            var run = new Run(line[start..i]);
            if (MarkdownPalette.For(roles[start], dark) is { } brush) run.Foreground = brush;
            if (bold[start]) run.FontWeight = FontWeight.Bold;

            into.Add(run);
            start = i;
        }
    }
}
