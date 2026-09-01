using System.Collections.ObjectModel;
using ClaudeLog.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeLog.ViewModels;

public enum NodeKind
{
    Project,
    Session,

    /// <summary>An attachment folder — Examples, Plans. Shown, opened in Explorer, never parsed.</summary>
    Folder,
}

public sealed partial class TreeNodeViewModel : ViewModelBase
{
    public required NodeKind Kind { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? Key { get; init; }
    public string Detail { get; init; } = string.Empty;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>
    /// The window this node belongs to. A ContextMenu lives in its own popup tree, so a binding
    /// inside one can't walk up to the TreeView and reach the main view model — the node itself is
    /// the only thing in scope. These commands exist to forward from there.
    /// </summary>
    public MainWindowViewModel? Owner { get; init; }

    public bool IsSession => Kind == NodeKind.Session;
    public bool IsFolder => Kind == NodeKind.Folder;

    /// <summary>New sessions belong to a project, so both a project and its files offer it.</summary>
    public bool CanAddSession => Kind is NodeKind.Project or NodeKind.Session;
    public string Icon => Kind switch
    {
        NodeKind.Project => "\U0001F4C1",
        NodeKind.Folder => "\U0001F4CE",
        _ => "\U0001F4C4",
    };

    /// <summary>
    /// Projects and attachment folders open in Explorer; a session file is revealed with itself
    /// selected. This is the shortcut into Examples/ and Plans/, which hold the HARs, screenshots
    /// and plan documents that go with a session.
    /// </summary>
    [RelayCommand]
    private void OpenInExplorer()
    {
        if (Kind == NodeKind.Session) Shell.RevealFile(Path);
        else Shell.OpenFolder(Path);
    }

    [RelayCommand]
    private Task NewSession() => Owner?.NewSessionIn(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task RenameSession() => Owner?.RenameSession(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void UseLegacyMode() => Owner?.SetMode(this, ParseMode.Legacy);

    [RelayCommand]
    private void UseModernMode() => Owner?.SetMode(this, ParseMode.Modern);

    [RelayCommand]
    private void ConvertToModern() => Owner?.ConvertToModern(this);
}
