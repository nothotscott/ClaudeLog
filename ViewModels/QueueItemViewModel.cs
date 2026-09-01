namespace ClaudeLog.ViewModels;

public sealed class QueueItemViewModel : ViewModelBase
{
    public required string FileKey { get; init; }
    public required string Hash { get; init; }
    public required string Preview { get; init; }

    /// <summary>"CallTree / sms.md" — the queue spans files, so each entry says where it came from.</summary>
    public string Location => FileKey.Replace("/", " / ");
}
