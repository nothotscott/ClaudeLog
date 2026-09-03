using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClaudeLog.Core;

namespace ClaudeLog.Views;

/// <summary>
/// The settings anyone actually changes, as controls instead of JSON.
///
/// It edits a <see cref="Settings.Clone"/> and hands it back on Save, so Cancel has nothing to
/// undo and the live instance is only ever written in one go. What it deliberately does *not*
/// cover is the per-project dictionaries and the terminal command-line templates: they are maps
/// and format strings, they change once, and a grid editor for them would be most of this dialog
/// for the least-used part of the file. The dropdown next to the button still opens settings.json
/// itself, which is where those belong.
/// </summary>
public partial class SettingsDialog : Window
{
    private Settings _settings = new();

    public SettingsDialog()
    {
        InitializeComponent();

        this.FindControl<ComboBox>("NewFileMode")!.ItemsSource = Enum.GetValues<ParseMode>();
        this.FindControl<ComboBox>("DefaultShell")!.ItemsSource = Enum.GetValues<TerminalShell>();

        this.FindControl<Button>("Ok")!.Click += (_, _) =>
        {
            // The delay is the one field that can be typed into an unusable state — the binding
            // refuses a non-number and silently leaves the old value, which would look like the
            // dialog ignored the edit.
            var typed = (this.FindControl<TextBox>("SubmitDelay")!.Text ?? string.Empty).Trim();
            if (!int.TryParse(typed, out var delay) || delay is < 0 or > 10_000)
            {
                Fail("The delay before Enter has to be a number of milliseconds, 0–10000.");
                return;
            }

            _settings.SubmitDelayMs = delay;

            if (_settings.LogRoot.Trim().Length == 0)
            {
                Fail("The log root can't be empty — it's the folder this app reads.");
                return;
            }

            _settings.LogRoot = _settings.LogRoot.Trim();
            _settings.DefaultSessionDir = _settings.DefaultSessionDir.Trim();
            Close(_settings);
        };

        this.FindControl<Button>("Cancel")!.Click += (_, _) => Close(null);
    }

    private void Fail(string message)
    {
        var error = this.FindControl<TextBlock>("Error")!;
        error.Text = message;
        error.IsVisible = true;
    }

    /// <summary>Edits a copy of the settings; returns it on Save, or null when cancelled.</summary>
    public static Task<Settings?> Show(Window owner, Settings settings)
    {
        var dialog = new SettingsDialog { _settings = settings, DataContext = settings };
        return dialog.ShowDialog<Settings?>(owner);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
