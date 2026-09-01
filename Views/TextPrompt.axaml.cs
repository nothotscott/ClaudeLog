using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ClaudeLog.Views;

/// <summary>
/// A one-line text prompt — Avalonia has no equivalent of an input box, and naming a file needs one.
///
/// Validation runs before the dialog closes and reports inline, because the alternative is closing
/// on a name that can't be used and explaining why in the status bar afterwards.
/// </summary>
public partial class TextPrompt : Window
{
    private Func<string, string?>? _validate;

    public TextPrompt()
    {
        InitializeComponent();

        var input = this.FindControl<TextBox>("Input")!;
        var error = this.FindControl<TextBlock>("Error")!;

        this.FindControl<Button>("Ok")!.Click += (_, _) =>
        {
            var text = (input.Text ?? string.Empty).Trim();
            var problem = _validate?.Invoke(text);

            if (problem is not null)
            {
                error.Text = problem;
                error.IsVisible = true;
                input.Focus();
                return;
            }

            Close(text);
        };

        this.FindControl<Button>("Cancel")!.Click += (_, _) => Close(null);
    }

    /// <summary>Returns the entered text, or null if it was cancelled.</summary>
    public static Task<string?> Show(Window owner, string title, string label, string initial,
        int selectLength, Func<string, string?>? validate = null)
    {
        var dialog = new TextPrompt { Title = title, _validate = validate };

        dialog.FindControl<TextBlock>("Label")!.Text = label;
        var input = dialog.FindControl<TextBox>("Input")!;
        input.Text = initial;

        // Select the part worth retyping — the stem of a filename, the way Explorer's rename does,
        // so the extension survives a name typed straight over the top.
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectionStart = 0;
            input.SelectionEnd = selectLength >= 0 ? selectLength : initial.Length;
        };

        return dialog.ShowDialog<string?>(owner);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
