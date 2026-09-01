using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClaudeLog.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeLog.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly Settings _settings;
    private readonly StateStore _store;
    private readonly QuotaWatcher _quota;
    private readonly DispatcherTimer _timer;
    private TreeWatcher? _treeWatcher;

    private SessionDocument? _doc;
    private DateTime _docStamp;
    private bool _suppressEditorSync;
    private TimeSpan? _lastRemaining;

    /// <summary>Set by the window: clipboard, toast and taskbar flash all need a TopLevel.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public Action<string, string>? Notify { get; set; }

    public Action? FlashWindow { get; set; }

    public MainWindowViewModel()
    {
        _settings = Settings.Load();
        _store = StateStore.Load();

        _quota = new QuotaWatcher(_settings.ClaudeProjectsDir);
        _quota.Updated += _ => Dispatcher.UIThread.Post(UpdateReset);
        _quota.Start();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTick);
        _timer.Start();

        // An override left over from a previous run has already come and gone; don't show a
        // countdown that reads "Limit reset" forever.
        if (_store.State.ManualResetAt <= DateTimeOffset.Now)
        {
            _store.State.ManualResetAt = null;
            _store.MarkDirty();
        }

        RefreshTree();
        RebuildQueue();
        UpdateReset();
        RestoreLastSession();
        BuildWordIndex();
    }

    /// <summary>
    /// Every word Scott has written across every session, used for completion and for silencing the
    /// spell checker on his own vocabulary. Reading ~40 files is quick but not instant, so it's
    /// built off the UI thread and swapped in whole — the index is only ever mutated on the UI
    /// thread afterwards, so no reader ever sees a half-built one.
    /// </summary>
    private void BuildWordIndex()
    {
        var root = _settings.LogRoot;
        Task.Run(() =>
        {
            var index = WordIndex.BuildFrom(root);
            Dispatcher.UIThread.Post(() => Words = index);
        });
    }

    /// <summary>Reopens whatever was open last time — this is a tool that gets left running all day.</summary>
    private void RestoreLastSession()
    {
        var last = _store.State.LastSession;
        if (last is null) return;

        SelectedNode = Tree.SelectMany(p => p.Children)
            .FirstOrDefault(c => c.Kind == NodeKind.Session &&
                                 string.Equals(c.Key, last, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- tree

    public ObservableCollection<TreeNodeViewModel> Tree { get; } = [];

    /// <summary>The project folder and its attachment folders, as Explorer shortcuts for the open session.</summary>
    public ObservableCollection<TreeNodeViewModel> Shortcuts { get; } = [];

    [ObservableProperty] private TreeNodeViewModel? _selectedNode;

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is { Kind: NodeKind.Session, Key: not null }) LoadSession(value);
    }

    public string LogRoot => _settings.LogRoot;

    [RelayCommand]
    private void RefreshTree()
    {
        var selectedPath = SelectedNode?.Path;
        Tree.Clear();

        foreach (var project in LogTree.Scan(_settings.LogRoot))
        {
            var node = new TreeNodeViewModel
            {
                Kind = NodeKind.Project,
                Name = project.Name,
                Path = project.Path,
                Detail = $"{project.Sessions.Count}",
            };

            foreach (var session in project.Sessions)
            {
                node.Children.Add(new TreeNodeViewModel
                {
                    Kind = NodeKind.Session,
                    Name = session.Name,
                    Path = session.Path,
                    Key = session.Key,
                    Detail = session.Modified.ToString("MMM d"),
                });
            }

            foreach (var folder in project.Folders)
            {
                node.Children.Add(new TreeNodeViewModel
                {
                    Kind = NodeKind.Folder,
                    Name = folder.Name,
                    Path = folder.Path,
                    Detail = $"{folder.ItemCount}",
                });
            }

            Tree.Add(node);
        }

        if (selectedPath is not null)
        {
            SelectedNode = Tree.SelectMany(p => p.Children).FirstOrDefault(c => c.Path == selectedPath) ??
                           Tree.FirstOrDefault(p => p.Path == selectedPath);
        }

        _treeWatcher ??= CreateWatcher();
    }

    private TreeWatcher CreateWatcher()
    {
        var watcher = new TreeWatcher(_settings.LogRoot);
        watcher.Changed += () => Dispatcher.UIThread.Post(OnTreeChanged);
        return watcher;
    }

    /// <summary>
    /// The tree changes underneath us — Syncthing, or Notepad++ still open on a file. Reload
    /// silently when there's nothing unsaved; say so when there is, rather than clobbering either side.
    /// </summary>
    private void OnTreeChanged()
    {
        if (_doc is not null && File.Exists(_doc.Path))
        {
            var stamp = File.GetLastWriteTimeUtc(_doc.Path);
            if (stamp != _docStamp)
            {
                if (IsDirty)
                {
                    Status = $"{Path.GetFileName(_doc.Path)} changed on disk — Save overwrites it, Refresh discards your edit";
                    return;
                }

                var index = SelectedPrompt?.Index ?? 0;
                LoadSession(SelectedNode!);
                SelectPromptAt(index);
                Status = "Reloaded after an external change";
            }
        }

        RefreshTree();
    }

    // ------------------------------------------------------------- session

    public ObservableCollection<PromptViewModel> Prompts { get; } = [];

    [ObservableProperty] private string _sessionTitle = "No session open";
    [ObservableProperty] private string _sessionSubtitle = "Pick a session on the left, or create one.";
    [ObservableProperty] private string? _currentKey;
    [ObservableProperty] private bool _hasSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel))]
    private ParseMode _mode = ParseMode.Legacy;

    public string ModeLabel => Mode == ParseMode.Modern ? "--- separators" : "blank-line (legacy)";

    private void LoadSession(TreeNodeViewModel node)
    {
        SaveEditorIfDirty();

        try
        {
            var key = node.Key!;
            var text = File.ReadAllText(node.Path);
            var mode = _store.PeekFileMode(key) ??
                       (PromptParser.LooksModern(text) ? ParseMode.Modern : ParseMode.Legacy);

            _doc = SessionDocument.Load(node.Path, mode);
            _docStamp = File.GetLastWriteTimeUtc(node.Path);
            _store.ForFile(key).Mode = mode;
            _store.State.LastSession = key;
            _store.MarkDirty();

            CurrentKey = key;
            Mode = mode;
            HasSession = true;
            SessionTitle = node.Name;
            SessionSubtitle = key;

            ReloadPrompts();
            BuildShortcuts(node);
            SelectPromptAt(Prompts.Count - 1);
        }
        catch (Exception ex)
        {
            Status = $"Could not open {node.Name}: {ex.Message}";
            Log.Warn($"load {node.Path}: {ex}");
        }
    }

    private void BuildShortcuts(TreeNodeViewModel sessionNode)
    {
        Shortcuts.Clear();
        var project = Tree.FirstOrDefault(p => p.Children.Contains(sessionNode));
        if (project is null) return;

        Shortcuts.Add(project);
        foreach (var folder in project.Children.Where(c => c.Kind == NodeKind.Folder))
        {
            Shortcuts.Add(folder);
        }
    }

    private void ReloadPrompts()
    {
        if (_doc is null || CurrentKey is null) return;

        Prompts.Clear();
        foreach (var prompt in _doc.Prompts)
        {
            Prompts.Add(new PromptViewModel
            {
                Index = prompt.Index,
                Hash = prompt.Hash,
                Text = prompt.Text,
                Preview = prompt.Preview,
                LineCount = prompt.LineCount,
                Status = _store.PeekPrompt(CurrentKey, prompt.Hash)?.Status ?? PromptStatus.Draft,
            });
        }

        _store.Prune(CurrentKey, _doc.Prompts.Select(p => p.Hash));
        UpdateSubtitle();
        RebuildQueue();
    }

    private void UpdateSubtitle()
    {
        if (CurrentKey is null) return;
        var sent = Prompts.Count(p => p.Status == PromptStatus.Sent);
        SessionSubtitle = $"{CurrentKey}  ·  {Prompts.Count} prompts, {sent} sent  ·  {ModeLabel}";
    }

    private void SelectPromptAt(int index)
    {
        if (Prompts.Count == 0)
        {
            SelectedPrompt = null;
            return;
        }
        SelectedPrompt = Prompts[Math.Clamp(index, 0, Prompts.Count - 1)];
    }

    // -------------------------------------------------------------- editor

    [ObservableProperty] private PromptViewModel? _selectedPrompt;
    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isNewPrompt;
    [ObservableProperty] private string _status = "Ready";

    /// <summary>Vocabulary from the whole log tree. Replaced wholesale once the background build finishes.</summary>
    [ObservableProperty] private WordIndex _words = new();

    public string EditorHeader => IsNewPrompt
        ? "New prompt"
        : SelectedPrompt is null ? "Editor" : $"Prompt {SelectedPrompt.Number}";

    partial void OnSelectedPromptChanged(PromptViewModel? oldValue, PromptViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;

        if (IsDirty && !ReferenceEquals(oldValue, newValue)) SaveEditorIfDirty(oldValue);

        _suppressEditorSync = true;
        EditorText = newValue?.Text ?? string.Empty;
        IsNewPrompt = false;
        IsDirty = false;
        _suppressEditorSync = false;
        OnPropertyChanged(nameof(EditorHeader));
    }

    partial void OnEditorTextChanged(string value)
    {
        if (!_suppressEditorSync) IsDirty = true;
    }

    partial void OnIsNewPromptChanged(bool value) => OnPropertyChanged(nameof(EditorHeader));

    [RelayCommand]
    private void NewPrompt()
    {
        SaveEditorIfDirty();
        SelectedPrompt = null;
        _suppressEditorSync = true;
        EditorText = string.Empty;
        _suppressEditorSync = false;
        IsNewPrompt = true;
        IsDirty = false;
        Status = "New prompt — Ctrl+S to append it to the file";
    }

    [RelayCommand]
    private void SaveEditor() => SaveEditorIfDirty(force: true);

    private void SaveEditorIfDirty(PromptViewModel? target = null, bool force = false)
    {
        if (_doc is null || CurrentKey is null) return;
        if (!IsDirty && !force) return;
        if (!IsNewPrompt && string.IsNullOrWhiteSpace(EditorText)) return;

        target ??= SelectedPrompt;

        try
        {
            int index;
            if (IsNewPrompt || target is null)
            {
                if (string.IsNullOrWhiteSpace(EditorText)) return;
                index = _doc.AppendPrompt(EditorText);
            }
            else
            {
                index = target.Index;
                var oldHash = target.Hash;
                _doc.ReplacePrompt(index, EditorText);
                if (index < _doc.Prompts.Count) _store.Rekey(CurrentKey, oldHash, _doc.Prompts[index].Hash);
            }

            _doc.Save();
            _docStamp = File.GetLastWriteTimeUtc(_doc.Path);
            IsDirty = false;
            IsNewPrompt = false;

            ReloadPrompts();
            SelectPromptAt(index);
            Status = $"Saved {Path.GetFileName(_doc.Path)}";
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
            Log.Warn($"save {_doc.Path}: {ex}");
        }
    }

    // --------------------------------------------------------------- copy

    /// <summary>
    /// Copying is the act of sending: it marks the prompt sent, drops it from the queue and
    /// timestamps it. The app never types into the terminal — the paste stays Scott's.
    /// </summary>
    [RelayCommand]
    private async Task CopyPrompt(PromptViewModel? prompt)
    {
        prompt ??= SelectedPrompt;
        if (prompt is null || CurrentKey is null) return;

        await CopyText(prompt.Text);

        if (_settings.MarkSentOnCopy)
        {
            MarkSent(CurrentKey, prompt.Hash);
            prompt.Status = PromptStatus.Sent;
            RebuildQueue();
        }

        Status = $"Copied prompt {prompt.Number}" + (_settings.MarkSentOnCopy ? " · marked sent" : "");
    }

    private async Task CopyText(string text)
    {
        if (CopyToClipboard is null)
        {
            Status = "Clipboard unavailable";
            return;
        }
        await CopyToClipboard(text);
    }

    private void MarkSent(string key, string hash)
    {
        var state = _store.Prompt(key, hash);
        state.Status = PromptStatus.Sent;
        state.SentAt = DateTimeOffset.Now;
        _store.State.Queue.RemoveAll(q =>
            string.Equals(q.File, key, StringComparison.OrdinalIgnoreCase) && q.Hash == hash);
        _store.MarkDirty();
    }

    [RelayCommand]
    private void ToggleSent(PromptViewModel? prompt)
    {
        prompt ??= SelectedPrompt;
        if (prompt is null || CurrentKey is null) return;

        var state = _store.Prompt(CurrentKey, prompt.Hash);
        if (state.Status == PromptStatus.Sent)
        {
            state.Status = PromptStatus.Draft;
            state.SentAt = null;
            prompt.Status = PromptStatus.Draft;
        }
        else
        {
            MarkSent(CurrentKey, prompt.Hash);
            prompt.Status = PromptStatus.Sent;
        }

        _store.MarkDirty();
        RebuildQueue();
    }

    // --------------------------------------------------------------- queue

    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];

    [ObservableProperty] private bool _hasQueue;

    [RelayCommand]
    private void QueuePrompt(PromptViewModel? prompt)
    {
        prompt ??= SelectedPrompt;
        if (prompt is null || CurrentKey is null) return;

        if (_store.State.Queue.Any(q =>
                string.Equals(q.File, CurrentKey, StringComparison.OrdinalIgnoreCase) && q.Hash == prompt.Hash))
        {
            Status = "Already queued";
            return;
        }

        _store.State.Queue.Add(new QueueEntry { File = CurrentKey, Hash = prompt.Hash });
        _store.Prompt(CurrentKey, prompt.Hash).Status = PromptStatus.Queued;
        _store.MarkDirty();
        prompt.Status = PromptStatus.Queued;

        RebuildQueue();
        Status = $"Queued prompt {prompt.Number}";
    }

    [RelayCommand]
    private void Unqueue(QueueItemViewModel? item)
    {
        if (item is null) return;

        _store.State.Queue.RemoveAll(q =>
            string.Equals(q.File, item.FileKey, StringComparison.OrdinalIgnoreCase) && q.Hash == item.Hash);

        var state = _store.PeekPrompt(item.FileKey, item.Hash);
        if (state is { Status: PromptStatus.Queued }) state.Status = PromptStatus.Draft;
        _store.MarkDirty();

        if (string.Equals(item.FileKey, CurrentKey, StringComparison.OrdinalIgnoreCase))
        {
            var prompt = Prompts.FirstOrDefault(p => p.Hash == item.Hash);
            if (prompt is not null) prompt.Status = PromptStatus.Draft;
        }

        RebuildQueue();
    }

    [RelayCommand]
    private void MoveQueueItem((QueueItemViewModel item, int delta) arg)
    {
        var (item, delta) = arg;
        var queue = _store.State.Queue;
        var index = queue.FindIndex(q =>
            string.Equals(q.File, item.FileKey, StringComparison.OrdinalIgnoreCase) && q.Hash == item.Hash);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= queue.Count) return;

        (queue[index], queue[target]) = (queue[target], queue[index]);
        _store.MarkDirty();
        RebuildQueue();
    }

    [RelayCommand]
    private void QueueUp(QueueItemViewModel? item)
    {
        if (item is not null) MoveQueueItemCommand.Execute((item, -1));
    }

    [RelayCommand]
    private void QueueDown(QueueItemViewModel? item)
    {
        if (item is not null) MoveQueueItemCommand.Execute((item, +1));
    }

    /// <summary>Copies the head of the queue — the command the reset toast invokes for you.</summary>
    [RelayCommand]
    private async Task CopyNextQueued()
    {
        var entry = _store.State.Queue.FirstOrDefault();
        if (entry is null)
        {
            Status = "Queue is empty";
            return;
        }

        var text = ResolveQueuedText(entry);
        if (text is null)
        {
            Status = "Queued prompt no longer exists — dropping it";
            _store.State.Queue.Remove(entry);
            _store.MarkDirty();
            RebuildQueue();
            return;
        }

        await CopyText(text);
        MarkSent(entry.File, entry.Hash);

        if (string.Equals(entry.File, CurrentKey, StringComparison.OrdinalIgnoreCase))
        {
            var prompt = Prompts.FirstOrDefault(p => p.Hash == entry.Hash);
            if (prompt is not null) prompt.Status = PromptStatus.Sent;
        }

        RebuildQueue();
        Status = "Next queued prompt is on the clipboard";
    }

    private string? ResolveQueuedText(QueueEntry entry)
    {
        try
        {
            if (string.Equals(entry.File, CurrentKey, StringComparison.OrdinalIgnoreCase) && _doc is not null)
            {
                return _doc.Prompts.FirstOrDefault(p => p.Hash == entry.Hash)?.Text;
            }

            var path = Path.Combine(_settings.LogRoot, entry.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return null;

            var mode = _store.PeekFileMode(entry.File) ?? ParseMode.Legacy;
            return SessionDocument.Load(path, mode).Prompts.FirstOrDefault(p => p.Hash == entry.Hash)?.Text;
        }
        catch (Exception ex)
        {
            Log.Warn($"queue resolve {entry.File}: {ex.Message}");
            return null;
        }
    }

    private void RebuildQueue()
    {
        Queue.Clear();
        foreach (var entry in _store.State.Queue)
        {
            var text = ResolveQueuedText(entry);
            Queue.Add(new QueueItemViewModel
            {
                FileKey = entry.File,
                Hash = entry.Hash,
                Preview = text is null
                    ? "(missing)"
                    : new Prompt { Index = 0, StartLine = 0, EndLine = 0, Text = text }.Preview,
            });
        }

        HasQueue = Queue.Count > 0;
        UpdateSubtitle();
    }

    // ---------------------------------------------------------- reset time

    [ObservableProperty] private string _countdownText = "No limit pending";
    [ObservableProperty] private string _resetDetail = "Watching Claude Code transcripts";
    [ObservableProperty] private bool _hasReset;
    [ObservableProperty] private string _manualResetInput = string.Empty;

    /// <summary>
    /// The manual override wins over what was detected. It deliberately stays effective for the
    /// moment after it passes: the countdown crossing zero is what fires the reset, so dropping
    /// an expired override here would mean it silently never fires. OnResetReached clears it.
    /// </summary>
    private DateTimeOffset? EffectiveReset
    {
        get
        {
            var manual = _store.State.ManualResetAt;
            var detected = _quota.Latest?.ResetsAt;

            if (manual is null) return detected;
            if (detected > DateTimeOffset.Now && manual <= DateTimeOffset.Now) return detected;
            return manual;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        UpdateReset();
        _store.SaveIfDirty();
    }

    private void UpdateReset()
    {
        var reset = EffectiveReset;
        if (reset is null)
        {
            HasReset = false;
            CountdownText = "No limit pending";
            ResetDetail = _quota.Latest is null
                ? "Watching Claude Code transcripts"
                : $"Last seen {_quota.Latest.RateLimitType} limit, already reset";
            _lastRemaining = null;
            return;
        }

        var remaining = reset.Value - DateTimeOffset.Now;
        HasReset = true;
        CountdownText = remaining <= TimeSpan.Zero
            ? "Limit reset"
            : $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m {remaining.Seconds:00}s";

        var manual = _store.State.ManualResetAt is not null && _store.State.ManualResetAt > DateTimeOffset.Now;
        ResetDetail = $"{(manual ? "Manual" : _quota.Latest?.RateLimitType ?? "detected")} · " +
                      $"resets {reset.Value.LocalDateTime:ddd HH:mm}";

        if (_lastRemaining > TimeSpan.Zero && remaining <= TimeSpan.Zero) OnResetReached();
        _lastRemaining = remaining;
    }

    private async void OnResetReached()
    {
        try
        {
            _store.State.ManualResetAt = null;
            _store.MarkDirty();

            if (_settings.FlashOnReset) FlashWindow?.Invoke();

            var queued = Queue.Count;
            if (_settings.StageClipboardOnReset && queued > 0)
            {
                await CopyNextQueued();
                Notify?.Invoke("Session limit reset",
                    $"Next queued prompt is on the clipboard. {queued - 1} still queued.");
            }
            else
            {
                Notify?.Invoke("Session limit reset", queued == 0 ? "Nothing queued." : $"{queued} prompts queued.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"reset handling failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SetManualReset()
    {
        var parsed = ParseResetInput(ManualResetInput);
        if (parsed is null)
        {
            Status = $"Couldn't read \"{ManualResetInput}\" as a time — try 9pm, 21:00, or 2026-08-31 21:00";
            return;
        }

        _store.State.ManualResetAt = parsed;
        _store.MarkDirty();
        _lastRemaining = parsed - DateTimeOffset.Now;
        UpdateReset();
        Status = $"Reset set to {parsed.Value.LocalDateTime:ddd HH:mm}";
    }

    [RelayCommand]
    private void ClearManualReset()
    {
        _store.State.ManualResetAt = null;
        _store.MarkDirty();
        ManualResetInput = string.Empty;
        UpdateReset();
        Status = "Manual reset cleared";
    }

    /// <summary>Accepts what Claude Code actually prints — "9pm" — as well as a full date.</summary>
    public static DateTimeOffset? ParseResetInput(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;

        if (!DateTime.TryParse(input, out var parsed))
        {
            if (!DateTime.TryParse(DateTime.Now.ToString("yyyy-MM-dd ") + input, out parsed)) return null;
        }

        var result = new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
        if (result <= DateTimeOffset.Now) result = result.AddDays(1);
        return result;
    }

    // ------------------------------------------------------------ commands

    [RelayCommand]
    private void ConvertToModern()
    {
        if (_doc is null || CurrentKey is null) return;
        if (_doc.Mode == ParseMode.Modern)
        {
            Status = "Already using --- separators";
            return;
        }

        try
        {
            SaveEditorIfDirty();
            Backups.Snapshot(_doc.Path);
            _doc.ConvertToModern();
            _doc.Save();
            _docStamp = File.GetLastWriteTimeUtc(_doc.Path);
            _store.ForFile(CurrentKey).Mode = ParseMode.Modern;
            _store.MarkDirty();
            Mode = ParseMode.Modern;
            ReloadPrompts();
            Status = $"Converted to --- separators ({Prompts.Count} prompts)";
        }
        catch (Exception ex)
        {
            Status = $"Convert failed: {ex.Message}";
        }
    }

    /// <summary>Flips how the open file is split, without touching the file.</summary>
    [RelayCommand]
    private void ToggleMode()
    {
        if (_doc is null || CurrentKey is null) return;
        SaveEditorIfDirty();

        Mode = _doc.Mode == ParseMode.Legacy ? ParseMode.Modern : ParseMode.Legacy;
        _doc.SetMode(Mode);
        _store.ForFile(CurrentKey).Mode = Mode;
        _store.MarkDirty();
        ReloadPrompts();
        SelectPromptAt(0);
        Status = $"Reading this file with {ModeLabel}";
    }

    [RelayCommand]
    private void MergeWithNext(PromptViewModel? prompt)
    {
        prompt ??= SelectedPrompt;
        if (_doc is null || prompt is null || prompt.Index + 1 >= _doc.Prompts.Count) return;

        SaveEditorIfDirty();
        _doc.MergeWithNext(prompt.Index);
        _doc.Save();
        _docStamp = File.GetLastWriteTimeUtc(_doc.Path);
        ReloadPrompts();
        SelectPromptAt(prompt.Index);
        Status = "Merged with the next prompt";
    }

    [RelayCommand]
    private void DeletePrompt(PromptViewModel? prompt)
    {
        prompt ??= SelectedPrompt;
        if (_doc is null || prompt is null) return;

        var index = prompt.Index;
        Backups.Snapshot(_doc.Path);
        _doc.DeletePrompt(index);
        _doc.Save();
        _docStamp = File.GetLastWriteTimeUtc(_doc.Path);
        IsDirty = false;
        ReloadPrompts();
        SelectPromptAt(index);
        Status = "Deleted prompt";
    }

    [RelayCommand]
    private void OpenLogRoot() => Shell.OpenFolder(_settings.LogRoot);

    [RelayCommand]
    private void OpenSettingsFile()
    {
        _settings.Save();
        Shell.OpenFile(Paths.SettingsFile);
    }

    [RelayCommand]
    private void RevealSession()
    {
        if (_doc is not null) Shell.RevealFile(_doc.Path);
    }

    /// <summary>Opens the code this project's prompts are about, per settings.json ProjectSources.</summary>
    [RelayCommand]
    private void OpenProjectSource()
    {
        var project = Shortcuts.FirstOrDefault(s => s.Kind == NodeKind.Project);
        if (project is null) return;

        if (_settings.ProjectSources.TryGetValue(project.Name, out var path) && Directory.Exists(path))
        {
            Shell.OpenFolder(path);
        }
        else
        {
            Status = $"No source folder mapped for {project.Name} — add one in settings.json";
        }
    }

    public void Shutdown()
    {
        SaveEditorIfDirty();
        _store.Save();
        Dispose();
    }

    public void Dispose()
    {
        _timer.Stop();
        _quota.Dispose();
        _treeWatcher?.Dispose();
    }
}
