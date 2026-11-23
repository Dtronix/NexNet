using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The prompt before unsaved captured data goes (OQ5: closing a live tab, attaching it to another session): the same
/// choice as the session-ended strip — Save .nntl… (Enter), Discard, or Cancel (Esc), which keeps the tab as it is.
/// <see cref="Result"/> completes with the answer.
/// </summary>
public sealed partial class UnsavedCloseDialogViewModel
{
    private readonly TaskCompletionSource<CloseDecision> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="document">The document whose data would be lost.</param>
    public UnsavedCloseDialogViewModel(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        Title = "Save " + document.Title + "?";
        var captured = RecordingText.Plural(document.FrameCount, "event") + " · " + RecordingText.Size(document.ByteCount);
        Body = document is LiveDocumentViewModel { SavedPath: { } saved }
            ? "More arrived since the save as " + Path.GetFileName(saved) + " (now " + captured + "). Without another save it is gone."
            : captured + " captured in this tab are not saved. Without a save they are gone: there is no history before an attach.";
    }

    /// <summary>Raised once answered: the host removes the dialog.</summary>
    public event Action<UnsavedCloseDialogViewModel>? Closed;

    /// <summary>The document asked about.</summary>
    public DocumentViewModel Document { get; }

    /// <summary>"Save Live · 0x3A21?".</summary>
    public string Title { get; }

    /// <summary>What would be lost.</summary>
    public string Body { get; }

    public string Hint => "Enter to save · Esc to cancel";

    /// <summary>The answer.</summary>
    public Task<CloseDecision> Result => _result.Task;

    /// <summary>Enter and Save .nntl….</summary>
    [RelayCommand]
    private void Save() => Finish(CloseDecision.Save);

    /// <summary>Discard: drop the data.</summary>
    [RelayCommand]
    private void Discard() => Finish(CloseDecision.Discard);

    /// <summary>Esc and Cancel: keep the tab.</summary>
    [RelayCommand]
    private void Cancel() => Finish(CloseDecision.Cancel);

    private void Finish(CloseDecision decision)
    {
        if (!_result.TrySetResult(decision)) return;
        Closed?.Invoke(this);
    }
}
