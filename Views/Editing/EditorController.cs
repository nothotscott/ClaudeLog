using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        _vm.PropertyChanged += OnViewModelChanged;

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

    private void PushToEditor(string text)
    {
        _syncing = true;
        _editor.Document.Text = text;
        _editor.CaretOffset = Math.Min(_editor.Document.TextLength, _editor.CaretOffset);
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
        _vm.PropertyChanged -= OnViewModelChanged;
        _checker.Dispose();
    }
}
