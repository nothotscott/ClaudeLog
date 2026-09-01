using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using ClaudeLog.Core;

namespace ClaudeLog.Views.Editing;

/// <summary>
/// Draws the red wavy underline under misspelled words. AvaloniaEdit has no built-in marker layer,
/// so this is a background renderer that walks the visible lines only — the document can be long
/// and this runs on every repaint.
/// </summary>
public sealed class SquiggleRenderer : IBackgroundRenderer
{
    private const double WaveLength = 4;
    private const double WaveHeight = 2.5;

    private IReadOnlyList<SpellingError> _errors = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetErrors(IReadOnlyList<SpellingError> errors) => _errors = errors;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_errors.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A)), 1.1);
        var start = textView.VisualLines[0].FirstDocumentLine.Offset;
        var end = textView.VisualLines[^1].LastDocumentLine.EndOffset;

        foreach (var error in _errors)
        {
            if (error.Start + error.Length < start || error.Start > end) continue;

            var segment = new TextSegment { StartOffset = error.Start, Length = error.Length };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                drawingContext.DrawGeometry(null, pen, Wave(rect));
            }
        }
    }

    private static StreamGeometry Wave(Rect rect)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();

        var y = rect.Bottom - WaveHeight / 2;
        context.BeginFigure(new Point(rect.Left, y), false);

        var up = false;
        for (var x = rect.Left + WaveLength; x < rect.Right; x += WaveLength)
        {
            context.LineTo(new Point(x, y + (up ? -WaveHeight : WaveHeight) / 2));
            up = !up;
        }

        context.EndFigure(false);
        return geometry;
    }
}
