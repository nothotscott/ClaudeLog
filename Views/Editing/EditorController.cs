using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using ClaudeLog.Core;
using ClaudeLog.ViewModels;

namespace ClaudeLog.Views.Editing;

/// <summary>
/// Everything that makes the prompt editor a writing tool rather than a text box: markdown
/// highlighting, completion from the vocabulary of the logs, and spell checking with squiggles.
///
/// It also owns the two-way text sync with the view model. The view model stays the single source
/// of truth for the prompt text; this class only mirrors it into the editor and back, with a guard
/// so an echo can't be mistaken for the user typing (which would mark the prompt dirty forever).
/// </summary>
public sealed class EditorController : IDisposable
{
    /// <summary>Long enough that a fast typist never sees a squiggle chase the caret.</summary>
    private static readonly TimeSpan SpellDelay = TimeSpan.FromMilliseconds(400);

    private const int MinPrefix = 3;

    /// <summary>Windows will offer a dozen; a right-click menu is not the place for the tail of that list.</summary>
    private const int MaxSuggestions = 6;

    private readonly TextEditor _editor;
    private readonly MainWindowViewModel _vm;
    private readonly MarkdownColorizer _colorizer = new();
    private readonly SquiggleRenderer _squiggles = new();
    private readonly SpellChecker _checker = new();
    private readonly SpellCheckPass _spelling;
    private readonly DispatcherTimer _spellTimer;

    private CompletionWindow? _completion;
    private bool _syncing;
    private IReadOnlyList<SpellingError> _errors = [];

    /// <summary>Where Escape sends focus: back to the prompt list, so browsing stays keyboard-only.</summary>
    public Action? FocusPromptList { get; set; }

    public EditorController(TextEditor editor, MainWindowViewModel vm)
    {
        _editor = editor;
        _vm = vm;
        _spelling = new SpellCheckPass(_checker, vm.Words);

        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_squiggles);

        _spellTimer = new DispatcherTimer(SpellDelay, DispatcherPriority.Background, (_, _) => RunSpellCheck());

        _editor.TextChanged += OnTextChanged;
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        _editor.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        _vm.PropertyChanged += OnViewModelChanged;

        _editor.ContextMenu = BuildContextMenu();

        ApplyTheme();
        _editor.ActualThemeVariantChanged += (_, _) => ApplyTheme();

        PushToEditor(_vm.EditorText);
        if (!_checker.Available) Log.Warn("spell check unavailable; squiggles disabled");
    }

    private void ApplyTheme()
    {
        _colorizer.Dark = _editor.ActualThemeVariant != ThemeVariant.Light;
        _editor.TextArea.TextView.Redraw();
    }

    // ---------------------------------------------------------- text sync

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.EditorText) && _vm.EditorText != _editor.Text)
        {
            PushToEditor(_vm.EditorText);
        }

        // Starting a new prompt means you're about to type — don't make the user click first.
        if (e.PropertyName == nameof(MainWindowViewModel.IsNewPrompt) && _vm.IsNewPrompt)
        {
            Focus();
        }
    }

    public void Focus()
    {
        _editor.Focus();
        _editor.CaretOffset = _editor.Document.TextLength;
    }

    /// <summary>
    /// Replaces the whole document, keeping the caret where it was. The offset has to be read
    /// *before* the assignment: replacing the text moves the caret itself, so reading it
    /// afterwards and clamping that is a no-op that leaves the caret at the end.
    /// </summary>
    private void PushToEditor(string text)
    {
        var caret = _editor.CaretOffset;

        _syncing = true;
        _editor.Document.Text = text;
        _editor.CaretOffset = Math.Min(_editor.Document.TextLength, caret);
        _syncing = false;

        Recolorize();
        RunSpellCheck();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        Recolorize();

        _spellTimer.Stop();
        _spellTimer.Start();

        if (_syncing) return;
        _vm.EditorText = _editor.Text;
    }

    private void Recolorize()
    {
        _colorizer.SetFencedLines(MarkdownColorizer.MapFencedLines(_editor.Text));
        _editor.TextArea.TextView.Redraw();
    }

    // -------------------------------------------------------- spell check

    private void RunSpellCheck()
    {
        _spellTimer.Stop();
        if (!_spelling.Available) return;

        try
        {
            _errors = _spelling.Run(_editor.Text);
            _squiggles.SetErrors(_errors);
            _editor.TextArea.TextView.Redraw();
        }
        catch (Exception ex)
        {
            Log.Warn($"spell check pass failed: {ex.Message}");
        }
    }

    private SpellingError? ErrorAt(int offset) =>
        _errors.FirstOrDefault(e => offset >= e.Start && offset <= e.Start + e.Length) is { Length: > 0 } hit
            ? hit
            : null;

    // ------------------------------------------------------- context menu

    /// <summary>
    /// The right-click menu every word processor has: the spellings for the word under the
    /// pointer, then the editing commands.
    ///
    /// One menu is built once and refills itself on <c>Opening</c>, rather than a fresh menu per
    /// click — a ContextMenu subscribes to its control's ContextRequested when it is *assigned*,
    /// so a menu created while handling that event has already missed it and never opens.
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) => menu.ItemsSource = ContextItems();
        return menu;
    }

    /// <summary>
    /// Right-clicking doesn't move the caret on its own, so the word the menu is about has to
    /// come from where the pointer is — otherwise the suggestions belong to wherever the caret
    /// happened to be left. A click inside the selection is left alone: that one is on its way to
    /// Cut or Copy, and moving the caret would clear what it is about to act on.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_editor);
        if (!point.Properties.IsRightButtonPressed) return;

        if (_editor.GetPositionFromPoint(point.Position) is not { } hit) return;
        var offset = _editor.Document.GetOffset(hit.Location);

        var selectionStart = _editor.SelectionStart;
        var selectionEnd = selectionStart + _editor.SelectionLength;
        if (_editor.SelectionLength > 0 && offset >= selectionStart && offset <= selectionEnd) return;

        _editor.CaretOffset = offset;
    }

    private List<Control> ContextItems()
    {
        var items = new List<Control>();

        if (ErrorAt(_editor.CaretOffset) is { } error && error.Start + error.Length <= _editor.Document.TextLength)
        {
            var word = _editor.Text.Substring(error.Start, error.Length);
            var suggestions = _spelling.Suggest(word).Take(MaxSuggestions).ToList();

            foreach (var suggestion in suggestions)
            {
                var replacement = suggestion;
                items.Add(Item(replacement, () => Correct(error.Start, error.Length, replacement),
                    FontWeight.SemiBold));
            }

            if (suggestions.Count == 0)
            {
                items.Add(new MenuItem { Header = "No spelling suggestions", IsEnabled = false });
            }

            items.Add(Item($"Add “{word}” to dictionary", () =>
            {
                _spelling.AddToDictionary(word);
                RunSpellCheck();
                _vm.Status = $"Added \"{word}\" to the Windows dictionary";
            }));

            items.Add(new Separator());
        }

        var hasSelection = _editor.SelectionLength > 0;
        items.Add(Item("Cut", () => _editor.Cut(), enabled: hasSelection));
        items.Add(Item("Copy", () => _editor.Copy(), enabled: hasSelection));
        items.Add(Item("Paste", () => _editor.Paste()));
        items.Add(new Separator());
        items.Add(Item("Select all", () => _editor.SelectAll(), enabled: _editor.Document.TextLength > 0));

        return items;
    }

    private static MenuItem Item(string header, Action action, FontWeight weight = FontWeight.Normal,
        bool enabled = true)
    {
        var item = new MenuItem { Header = header, FontWeight = weight, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Applies a suggestion. It goes through the document rather than through the view model so
    /// the edit lands in the undo stack and marks the prompt dirty like any other typing would.
    /// </summary>
    private void Correct(int start, int length, string replacement)
    {
        if (start + length > _editor.Document.TextLength) return;

        _editor.Document.Replace(start, length, replacement);
        _editor.CaretOffset = start + replacement.Length;
        RunSpellCheck();
    }

    // --------------------------------------------------------- completion

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is null || e.Text.Length != 1) return;
        if (!TextScan.IsWordChar(e.Text[0]))
        {
            _completion?.Close();
            return;
        }

        if (_completion is not null) return;

        var word = CurrentWord();
        if (word.Length < MinPrefix) return;

        ShowCompletion(word, spelling: false);
    }

    private string CurrentWord()
    {
        var caret = _editor.CaretOffset;
        var span = TextScan.WordAt(_editor.Text, caret);
        return span.Length == 0 ? string.Empty : _editor.Text.Substring(span.Start, caret - span.Start);
    }

    /// <summary>
    /// The popup: spelling fixes when the caret sits in a misspelled word, otherwise completions
    /// from the log vocabulary. Keyboard only — Up/Down/Enter/Tab/Esc are AvaloniaEdit's own.
    /// </summary>
    private void ShowCompletion(string prefix, bool spelling)
    {
        var caret = _editor.CaretOffset;
        var span = TextScan.WordAt(_editor.Text, caret);
        var items = new List<CompletionItem>();

        if (spelling && span.Length > 0)
        {
            var word = _editor.Text.Substring(span.Start, span.Length);
            var priority = 100d;
            foreach (var suggestion in _spelling.Suggest(word))
            {
                items.Add(new CompletionItem(suggestion, "spelling", priority--));
            }

            if (items.Count > 0)
            {
                items.Add(new CompletionItem($"Add \"{word}\" to dictionary", "dictionary", -1,
                    (_, _) =>
                    {
                        _spelling.AddToDictionary(word);
                        RunSpellCheck();
                        _vm.Status = $"Added \"{word}\" to the Windows dictionary";
                    }));
            }
        }

        if (items.Count == 0)
        {
            var documentWords = TextScan.Words(_editor.Text, 4).ToHashSet(StringComparer.Ordinal);
            var priority = 100d;
            foreach (var match in _vm.Words.Matching(prefix, documentWords, 12))
            {
                items.Add(new CompletionItem(match, documentWords.Contains(match) ? "this prompt" : "your logs",
                    priority--));
            }
        }

        if (items.Count == 0) return;

        _completion = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = spelling ? span.Start : caret - prefix.Length,
            EndOffset = spelling ? span.Start + span.Length : caret,
        };

        foreach (var item in items) _completion.CompletionList.CompletionData.Add(item);

        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    // ---------------------------------------------------------- shortcuts

    /// <summary>
    /// Tunnelled so the app's shortcuts win while the caret is in the editor — AvaloniaEdit would
    /// otherwise swallow some of them, and these are the keys the whole workflow runs on.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Escape leaves the editor for the prompt list — unless the popup is open, where it belongs
        // to the popup.
        if (e.Key == Key.Escape && !ctrl)
        {
            if (_completion is not null) return;
            FocusPromptList?.Invoke();
            e.Handled = true;
            return;
        }

        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.Space:
                _completion?.Close();
                ShowCompletion(CurrentWord(), spelling: ErrorAt(_editor.CaretOffset) is not null);
                e.Handled = true;
                break;

            case Key.S:
                _vm.SaveEditorCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                _vm.CopyPromptCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.N:
                _vm.NewPromptCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Q:
                _vm.QueuePromptCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    public void Dispose()
    {
        _spellTimer.Stop();
        _editor.TextChanged -= OnTextChanged;
        _editor.TextArea.TextEntered -= OnTextEntered;
        _editor.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _vm.PropertyChanged -= OnViewModelChanged;
        _checker.Dispose();
    }
}
