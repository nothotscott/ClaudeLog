using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;
using ClaudeLog.Core;
using ClaudeLog.ViewModels;
using ClaudeLog.Views.Editing;

namespace ClaudeLog.Views;

public partial class MainWindow : Window
{
    private WindowNotificationManager? _notifications;
    private EditorController? _editorController;

    public MainWindow()
    {
        InitializeComponent();

        // Clipboard, toasts and the taskbar flash all need a live window, so the view hands them
        // to the view model rather than the view model reaching for a TopLevel.
        Opened += (_, _) =>
        {
            _notifications = new WindowNotificationManager(this)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3,
            };

            if (DataContext is not MainWindowViewModel vm) return;

            vm.CopyToClipboard = async text =>
            {
                if (Clipboard is null) return;
                await Clipboard.SetTextAsync(text);
            };

            vm.Notify = (title, message) =>
                _notifications?.Show(new Notification(title, message, NotificationType.Information,
                    TimeSpan.FromSeconds(30)));

            vm.FlashWindow = () => Shell.FlashTaskbar(TryGetPlatformHandle()?.Handle ?? 0);

            vm.AskForText = (title, label, initial, select, validate) =>
                TextPrompt.Show(this, title, label, initial, select, validate);

            var editor = this.FindControl<TextEditor>("Editor")!;
            var promptList = this.FindControl<ListBox>("PromptList")!;

            _editorController = new EditorController(editor, vm)
            {
                FocusPromptList = () => promptList.Focus(),
            };

            // Ctrl+E jumps into the editor from anywhere; Escape in the editor comes back. Browsing
            // prompts and writing them stay on the keyboard.
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.E && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    _editorController?.Focus();
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);
        };

        Closed += (_, _) => _editorController?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
