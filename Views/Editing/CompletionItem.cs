using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace ClaudeLog.Views.Editing;

/// <summary>One row in the suggestion popup: a completion, a spelling fix, or a dictionary action.</summary>
public sealed class CompletionItem : ICompletionData
{
    private readonly Action<TextArea, ISegment>? _action;

    public CompletionItem(string text, string description, double priority, Action<TextArea, ISegment>? action = null)
    {
        Text = text;
        Description = description;
        Priority = priority;
        _action = action;
    }

    public IImage? Image => null;

    public string Text { get; }

    public object Content => Text;

    public object Description { get; }

    public double Priority { get; }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        if (_action is not null)
        {
            _action(textArea, completionSegment);
            return;
        }

        textArea.Document.Replace(completionSegment, Text);
    }
}
