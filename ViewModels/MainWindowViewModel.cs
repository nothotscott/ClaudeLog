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

    /// <summary>Asks for one line of text: title, label, initial value, how much of it to select,
    /// and a validator that runs before the dialog will close. Null means cancelled.</summary>
    public Func<string, string, string, int, Func<string, string?>, Task<string?>>? AskForText { get; set; }

    public MainWindowViewModel()
    {
        _settings = Settings.Load();
        _store = StateStore.Load();

        // The field, not the property: going through the setter would write settings.json back on
        // every launch just to store what it already said.
        _showManualReset = _settings.ShowManualReset;

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
                Owner = this,
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
                    Owner = this,
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
                    Owner = this,
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

    public string ModeLabel => Describe(Mode);

    public static string Describe(ParseMode mode) =>
        mode == ParseMode.Modern ? "--- separators" : "blank-line (legacy)";

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
            // Shortcuts first: the project node in it is what names the project whose session
            // directory the terminal is resolved against.
            BuildShortcuts(node);
            RestoreTerminal();
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
        if (_suppressEditorSync) return;
        IsDirty = true;

        // The prompt list shows each prompt in full, so it has to be a view of the text being
        // typed, not of the last save — otherwise the card directly above the caret contradicts it.
        if (!IsNewPrompt && SelectedPrompt is not null) SelectedPrompt.Text = value;
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

    // ----------------------------------------------------------- terminal

    /// <summary>
    /// The Claude Code session this file's prompts go to. Null until one has been started, and
    /// re-resolved on every load, because the terminal can be closed from under the app.
    /// </summary>
    private TerminalSession? _terminal;

    [ObservableProperty] private bool _terminalRunning;
    [ObservableProperty] private string _terminalLabel = "No terminal";
    [ObservableProperty] private string _terminalTooltip = "Start Claude Code for this session";
    [ObservableProperty] private bool _terminalBusy;

    /// <summary>
    /// The directory this session's Claude Code runs in: what the file already used if it has run
    /// before, otherwise the project's configured default. Sticking to the stored one matters —
    /// it is half the path to the transcript, so changing a project's default must not orphan the
    /// sessions started under the old one.
    /// </summary>
    private string SessionDir
    {
        get
        {
            if (CurrentKey is null) return "";
            var stored = _store.ForFile(CurrentKey).SessionDir;
            if (stored is { Length: > 0 }) return stored;

            var project = Shortcuts.FirstOrDefault(s => s.Kind == NodeKind.Project)?.Name;
            return project is null ? "" : _settings.SessionDirFor(project);
        }
    }

    /// <summary>
    /// Picks up a terminal this file already has: one started earlier in this run, or one that
    /// outlived a restart of the app and is still holding its PID file.
    /// </summary>
    private void RestoreTerminal()
    {
        _terminal = null;

        if (CurrentKey is not null)
        {
            var file = _store.ForFile(CurrentKey);
            if (file.ClaudeSessionId is { Length: > 0 } id && file.SessionDir is { Length: > 0 } dir)
            {
                var pid = ClaudeTerminal.Reattach(id, file.TerminalPid);
                if (pid is not null) _terminal = new TerminalSession(id, dir, pid.Value);

                if (file.TerminalPid != pid)
                {
                    file.TerminalPid = pid;
                    _store.MarkDirty();
                }
            }
        }

        UpdateTerminalLabel();
    }

    private void UpdateTerminalLabel()
    {
        TerminalRunning = _terminal is not null && ClaudeTerminal.IsAlive(_terminal.Pid);

        if (!TerminalRunning) _terminal = null;

        if (CurrentKey is null)
        {
            TerminalLabel = "No terminal";
            TerminalTooltip = "Open a session first";
            return;
        }

        var id = _store.ForFile(CurrentKey).ClaudeSessionId;
        var dir = SessionDir;

        if (TerminalRunning && _terminal is not null)
        {
            TerminalLabel = $"● {Path.GetFileName(_terminal.Dir.TrimEnd('\\'))} · {_terminal.SessionId[..8]}";
            TerminalTooltip = $"Claude Code is running in {_terminal.Dir}\nSession {_terminal.SessionId}\n" +
                              "Click to bring its window to the front";
        }
        else if (id is { Length: > 0 })
        {
            TerminalLabel = $"○ resume {id[..8]}";
            TerminalTooltip = $"This session's conversation is {id}.\nStart the terminal to resume it in {dir}";
        }
        else
        {
            TerminalLabel = "○ No terminal";
            TerminalTooltip = dir.Length == 0
                ? "No session directory for this project — set ProjectSessionDirs or DefaultSessionDir in settings.json"
                : $"Start Claude Code in {dir}";
        }
    }

    /// <summary>
    /// Opens a terminal for this session, resuming its conversation if it has one. The GUID is
    /// minted here, before anything runs, so state.json can record which conversation a log file
    /// belongs to rather than guessing afterwards from whichever transcript appeared last.
    /// </summary>
    [RelayCommand]
    private async Task StartTerminal()
    {
        if (CurrentKey is null || _doc is null) return;

        if (TerminalRunning)
        {
            ShowTerminal();
            return;
        }

        var dir = SessionDir;
        if (dir.Length == 0)
        {
            Status = "No session directory for this project — set DefaultSessionDir in settings.json";
            return;
        }

        if (!Directory.Exists(dir))
        {
            Status = $"Session directory {dir} does not exist";
            return;
        }

        var file = _store.ForFile(CurrentKey);
        var id = file.ClaudeSessionId is { Length: > 0 } existing ? existing : ClaudeTerminal.NewSessionId();
        var title = $"{Path.GetFileNameWithoutExtension(_doc.Path)} · ClaudeLog";

        TerminalBusy = true;
        Status = $"Starting Claude Code in {dir}…";

        try
        {
            var session = await ClaudeTerminal.StartAsync(_settings, id, dir, title);
            _terminal = session;

            file.ClaudeSessionId = session.SessionId;
            file.SessionDir = session.Dir;
            file.TerminalPid = session.Pid;
            file.SessionStartedAt = DateTimeOffset.Now;
            _store.MarkDirty();
            _store.Save();

            Status = $"Claude Code running in {dir} · session {session.SessionId[..8]}";
        }
        catch (Exception ex)
        {
            Status = $"Could not start the terminal: {ex.Message}";
            Log.Warn($"terminal start {id}: {ex}");
        }
        finally
        {
            TerminalBusy = false;
            UpdateTerminalLabel();
        }
    }

    [RelayCommand]
    private void ShowTerminal()
    {
        if (_terminal is null)
        {
            Status = "No terminal running for this session";
            return;
        }
        ClaudeTerminal.Show(_settings, _terminal);
    }

    /// <summary>
    /// Forgets the conversation without touching it, so the next start opens a fresh one. The old
    /// transcript stays on disk and `claude --resume` can still reach it.
    /// </summary>
    [RelayCommand]
    private void NewClaudeSession()
    {
        if (CurrentKey is null) return;

        var file = _store.ForFile(CurrentKey);
        if (file.ClaudeSessionId is { Length: > 0 } old) ClaudeTerminal.Forget(old);

        file.ClaudeSessionId = null;
        file.TerminalPid = null;
        file.SessionStartedAt = null;
        _store.MarkDirty();

        _terminal = null;
        UpdateTerminalLabel();
        Status = "Next start opens a new Claude Code conversation for this session";
    }

    /// <summary>Changes where this session's Claude Code runs, for this file only.</summary>
    [RelayCommand]
    private async Task ChangeSessionDir()
    {
        if (CurrentKey is null || AskForText is null) return;

        var current = SessionDir;
        var answer = await AskForText("Session directory", "Claude Code runs in", current, current.Length,
            value => Directory.Exists(value.Trim()) ? null : "That folder doesn't exist.");

        if (answer is null) return;

        _store.ForFile(CurrentKey).SessionDir = answer.Trim();
        _store.MarkDirty();
        UpdateTerminalLabel();
        Status = $"This session's Claude Code will run in {answer.Trim()}";
    }

    // --------------------------------------------------------------- send

    /// <summary>
    /// Sends a prompt straight into the session's terminal — the thing this app exists to make
    /// unremarkable. Delivery goes through the console rather than the clipboard, so it doesn't
    /// take focus and doesn't disturb whatever is on the clipboard.
    /// </summary>
    [RelayCommand]
    private async Task SendPrompt(PromptViewModel? prompt)
    {
        prompt ??= SelectedPrompt;
        if (prompt is null || CurrentKey is null) return;

        if (!TerminalRunning)
        {
            if (!_settings.AutoStartTerminal)
            {
                Status = "No terminal for this session — start one first";
                return;
            }

            await StartTerminal();
            if (!TerminalRunning) return;
        }

        await Deliver(prompt.Text, CurrentKey, prompt.Hash, $"prompt {prompt.Number}");
    }

    /// <summary>
    /// Writes one prompt to the terminal and then checks Claude Code's own transcript to see
    /// whether it was taken as a prompt. The write succeeding only means the terminal is alive:
    /// at a permission prompt the same keystrokes answer that instead, and the transcript is the
    /// only place that tells the two apart.
    /// </summary>
    private async Task Deliver(string text, string fileKey, string hash, string label)
    {
        if (_terminal is null) return;

        var session = _terminal;
        var sentAt = DateTimeOffset.Now;

        var error = await Task.Run(() =>
            ConsoleInput.SendPrompt(session.Pid, text, _settings.SubmitDelayMs));

        if (error is not null)
        {
            Status = $"Could not send {label}: {error}";
            UpdateTerminalLabel();
            return;
        }

        if (_settings.MarkSentOnSend)
        {
            MarkSent(fileKey, hash);
            if (string.Equals(fileKey, CurrentKey, StringComparison.OrdinalIgnoreCase))
            {
                var shown = Prompts.FirstOrDefault(p => p.Hash == hash);
                if (shown is not null) shown.Status = PromptStatus.Sent;
            }
            RebuildQueue();
        }

        Status = $"Sent {label} to {session.SessionId[..8]}";
        Confirm(session, sentAt, label);
    }

    /// <summary>
    /// Watches for the prompt to show up in the transcript and says so, without holding up the
    /// command that sent it. Claude Code queues input while it's working, so the next prompt can
    /// go the moment this one is written — waiting for confirmation before re-enabling Send would
    /// put an eight-second pause between prompts for no reason.
    /// </summary>
    private void Confirm(TerminalSession session, DateTimeOffset sentAt, string label)
    {
        var transcript = ClaudeTerminal.TranscriptPath(_settings.ClaudeProjectsDir, session.Dir, session.SessionId);

        _ = Task.Run(async () =>
        {
            var confirmed = await SessionTranscript.WaitForPromptAsync(transcript, sentAt, TimeSpan.FromSeconds(10));

            Dispatcher.UIThread.Post(() =>
            {
                // Only speak up if nothing else has happened since; a later send's message is
                // newer news than this one's confirmation.
                if (Status != $"Sent {label} to {session.SessionId[..8]}") return;

                Status = confirmed
                    ? $"Sent {label} · Claude Code has it"
                    : $"Sent {label} · not confirmed in the transcript — check the terminal";
            });
        });
    }

    // --------------------------------------------------------------- copy

    /// <summary>
    /// Copying is the act of sending by hand: it marks the prompt sent, drops it from the queue
    /// and timestamps it. It stays alongside Send for the times the prompt is going somewhere
    /// ClaudeLog didn't start.
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

    /// <summary>
    /// Sends the head of the queue to the open session's terminal. The queue spans files, so this
    /// only sends what belongs to the session that is open — a prompt from another file has its
    /// own terminal, and firing it into this one would put it in the wrong conversation.
    /// </summary>
    [RelayCommand]
    private async Task SendNextQueued()
    {
        var entry = _store.State.Queue.FirstOrDefault();
        if (entry is null)
        {
            Status = "Queue is empty";
            return;
        }

        if (!string.Equals(entry.File, CurrentKey, StringComparison.OrdinalIgnoreCase))
        {
            Status = $"Next queued prompt belongs to {entry.File} — open it to send it";
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

        if (!TerminalRunning)
        {
            if (!_settings.AutoStartTerminal)
            {
                Status = "No terminal for this session — start one first";
                return;
            }

            await StartTerminal();
            if (!TerminalRunning) return;
        }

        await Deliver(text, entry.File, entry.Hash, "the queued prompt");
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

    /// <summary>Whether the manual entry is wanted at all — a setting, toggled from the panel's menu.</summary>
    [ObservableProperty] private bool _showManualReset;

    /// <summary>Whether it's actually on screen: wanted, or currently holding an override.</summary>
    [ObservableProperty] private bool _manualResetVisible;

    partial void OnShowManualResetChanged(bool value)
    {
        _settings.ShowManualReset = value;
        _settings.Save();
        RefreshManualResetVisibility();
    }

    /// <summary>
    /// Called every tick as well as on the toggle. An override set while the entry is hidden would
    /// otherwise be unclearable — the countdown would say "Manual" with no way to take it back.
    /// </summary>
    private void RefreshManualResetVisibility() =>
        ManualResetVisible = ShowManualReset || _store.State.ManualResetAt is not null;

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

        // The terminal can be closed from under the app at any moment, and a Send button that
        // still looks armed after that is worse than no button. Checked on the second, not on the
        // click, so the pill goes grey when the window closes rather than when it is next used.
        if (TerminalRunning != (_terminal is not null && ClaudeTerminal.IsAlive(_terminal.Pid)))
        {
            UpdateTerminalLabel();
        }

        _store.SaveIfDirty();
    }

    private void UpdateReset()
    {
        RefreshManualResetVisibility();

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

            // Off by default. The reset usually lands while Scott is away from the machine, and a
            // prompt that sends itself into a session nobody is watching is not a favour.
            if (_settings.AutoSendOnReset && queued > 0 && TerminalRunning)
            {
                await SendNextQueued();
                Notify?.Invoke("Session limit reset",
                    $"Sent the next queued prompt. {queued - 1} still queued.");
            }
            else if (_settings.StageClipboardOnReset && queued > 0)
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

    // ------------------------------------------------------- session files

    /// <summary>
    /// Makes a tree node the open session, so the commands below can act on "the open file".
    /// Right-clicking a TreeViewItem doesn't select it, so a context-menu command can arrive for a
    /// file that isn't open — opening it first is both correct and what you'd expect to see happen.
    /// </summary>
    private bool Open(TreeNodeViewModel node)
    {
        if (node.Kind != NodeKind.Session) return false;
        if (!string.Equals(node.Key, CurrentKey, StringComparison.OrdinalIgnoreCase)) SelectedNode = node;
        return _doc is not null && CurrentKey is not null;
    }

    private TreeNodeViewModel? ProjectOf(TreeNodeViewModel node) =>
        node.Kind == NodeKind.Project ? node : Tree.FirstOrDefault(p => p.Children.Contains(node));

    private void SelectByPath(string path) =>
        SelectedNode = Tree.SelectMany(p => p.Children)
            .FirstOrDefault(c => c.Kind == NodeKind.Session &&
                                 string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates an empty session file in a project and leaves you in the editor.</summary>
    public async Task NewSessionIn(TreeNodeViewModel node)
    {
        var project = ProjectOf(node);
        if (project is null || AskForText is null) return;

        var name = await AskForText("New session", $"Name of the new session file in {project.Name}:",
            string.Empty, 0, typed => LogTree.ValidateSessionName(typed, project.Path));
        if (name is null) return;

        var path = Path.Combine(project.Path, LogTree.NormalizeSessionName(name));

        try
        {
            SaveEditorIfDirty();
            SessionDocument.CreateEmpty(path, _settings.NewFileMode).Save();

            // An empty file looks like neither mode, so LoadSession would guess Legacy and quietly
            // ignore NewFileMode. Record the intended mode before the file is ever opened.
            _store.ForFile(Paths.RelativeKey(_settings.LogRoot, path)).Mode = _settings.NewFileMode;
            _store.MarkDirty();

            RefreshTree();
            SelectByPath(path);
            Status = $"Created {Path.GetFileName(path)}";
            NewPrompt();
        }
        catch (Exception ex)
        {
            Status = $"Could not create {Path.GetFileName(path)}: {ex.Message}";
            Log.Warn($"new session {path}: {ex}");
        }
    }

    /// <summary>Renames a session file on disk, carrying its state with it.</summary>
    public async Task RenameSession(TreeNodeViewModel node)
    {
        if (node is not { Kind: NodeKind.Session, Key: not null } || AskForText is null) return;
        if (Path.GetDirectoryName(node.Path) is not { } folder) return;

        var name = await AskForText("Rename session", "New name for this session file:", node.Name,
            Path.GetFileNameWithoutExtension(node.Name).Length,
            typed => LogTree.ValidateSessionName(typed, folder, node.Path));
        if (name is null) return;

        var target = Path.Combine(folder, LogTree.NormalizeSessionName(name));
        if (string.Equals(target, node.Path, StringComparison.OrdinalIgnoreCase)) return;

        var wasOpen = string.Equals(node.Key, CurrentKey, StringComparison.OrdinalIgnoreCase);

        try
        {
            if (wasOpen) SaveEditorIfDirty();

            File.Move(node.Path, target);
            _store.RenameFile(node.Key, Paths.RelativeKey(_settings.LogRoot, target));
            _store.Save();

            RefreshTree();
            if (wasOpen) SelectByPath(target);
            Status = $"Renamed to {Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            Status = $"Rename failed: {ex.Message}";
            Log.Warn($"rename {node.Path}: {ex}");
        }
    }

    /// <summary>Changes how a file is split, without touching the file.</summary>
    public void SetMode(TreeNodeViewModel node, ParseMode mode)
    {
        if (!Open(node) || CurrentKey is null) return;
        if (_doc!.Mode == mode)
        {
            Status = $"Already reading this file with {Describe(mode)}";
            return;
        }

        SaveEditorIfDirty();

        Mode = mode;
        _doc.SetMode(mode);
        _store.ForFile(CurrentKey).Mode = mode;
        _store.MarkDirty();
        ReloadPrompts();
        SelectPromptAt(0);
        Status = $"Reading this file with {Describe(mode)}";
    }

    /// <summary>Rewrites a legacy file with explicit `---` separators. Snapshotted first.</summary>
    public void ConvertToModern(TreeNodeViewModel node)
    {
        if (!Open(node) || CurrentKey is null) return;
        if (_doc!.Mode == ParseMode.Modern)
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

    // ------------------------------------------------------------ commands

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
