using Avalonia.Media;
using ClaudeLog.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeLog.ViewModels;

public sealed partial class PromptViewModel : ViewModelBase
{
    public required int Index { get; init; }
    public required string Hash { get; init; }
    public required string Text { get; init; }
    public required string Preview { get; init; }
    public required int LineCount { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    private PromptStatus _status;

    [ObservableProperty] private bool _isSelected;

    public int Number => Index + 1;

    public string Meta => LineCount == 1 ? "1 line" : $"{LineCount} lines";

    public bool ShowStatus => Status != PromptStatus.Draft;

    public string StatusLabel => Status switch
    {
        PromptStatus.Sent => "sent",
        PromptStatus.Queued => "queued",
        _ => "draft",
    };

    public IBrush StatusBrush => Status switch
    {
        PromptStatus.Sent => new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0x4F)),
        PromptStatus.Queued => new SolidColorBrush(Color.FromRgb(0x7A, 0x5A, 0x1E)),
        _ => new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
    };
}
