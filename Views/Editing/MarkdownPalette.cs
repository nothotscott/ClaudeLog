using Avalonia.Media;
using Avalonia.Media.Immutable;
using ClaudeLog.Core;

namespace ClaudeLog.Views.Editing;

/// <summary>
/// One palette for both renderers of <see cref="MarkdownScanner"/>'s spans. The brushes are
/// immutable and shared: the editor asks for them on every visible line of every repaint, so
/// allocating a brush per lookup was pure churn.
/// </summary>
public static class MarkdownPalette
{
    private static readonly IImmutableBrush CodeDark = Rgb(0xCE, 0x91, 0x78);
    private static readonly IImmutableBrush CodeLight = Rgb(0xA3, 0x31, 0x1B);
    private static readonly IImmutableBrush MarkerDark = Rgb(0x56, 0x9C, 0xD6);
    private static readonly IImmutableBrush MarkerLight = Rgb(0x1B, 0x5E, 0xA6);
    private static readonly IImmutableBrush HeadingDark = Rgb(0x4E, 0xC9, 0xB0);
    private static readonly IImmutableBrush HeadingLight = Rgb(0x0A, 0x7A, 0x66);
    private static readonly IImmutableBrush QuoteDark = Rgb(0x8A, 0x9A, 0x8A);
    private static readonly IImmutableBrush QuoteLight = Rgb(0x60, 0x70, 0x60);

    /// <summary>Null means "leave the foreground alone" — prose keeps the control's own color.</summary>
    public static IBrush? For(MarkdownRole role, bool dark) => role switch
    {
        MarkdownRole.Code => dark ? CodeDark : CodeLight,
        MarkdownRole.Marker => dark ? MarkerDark : MarkerLight,
        MarkdownRole.Heading => dark ? HeadingDark : HeadingLight,
        MarkdownRole.Quote => dark ? QuoteDark : QuoteLight,
        _ => null,
    };

    private static IImmutableBrush Rgb(byte r, byte g, byte b) =>
        new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));
}
