using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Format;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The session-ended strip under the title bar (screens/session-ended, controls/banner SessionEnded): shown while the
/// active document is a live session that ended. "Session 0x3A21 ended", then what ended it and what was captured
/// ("DisconnectGraceful (21) from client · 12:04 attached · 18,248 events · 2.4 MB · not saved"), the reminder that the
/// timeline stays open, and its actions: Save .nntl… (the tab becomes the saved file), Attach to another session ▾ (the
/// session popover) and Discard (closes the tab without asking: the answer is given). The data stays navigable.
/// </summary>
public sealed partial class SessionEndedViewModel : ObservableObject
{
    /// <summary>The strip's reminder at the right, before its actions.</summary>
    public const string StaysOpenHint = "The timeline stays open until you close it.";

    private readonly DocumentsViewModel _documents;
    private readonly Action _attachElsewhere;

    /// <param name="documents">The documents; the strip follows the active one.</param>
    /// <param name="attachElsewhere">Opens the session list (the title bar's session popover).</param>
    public SessionEndedViewModel(DocumentsViewModel documents, Action attachElsewhere)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(attachElsewhere);
        _documents = documents;
        _attachElsewhere = attachElsewhere;
        documents.PropertyChanged += OnDocumentsChanged;
        Follow();
    }

    /// <summary>The live document the strip speaks for: the active one, when live; null otherwise.</summary>
    [ObservableProperty]
    public partial LiveDocumentViewModel? Live { get; private set; }

    /// <summary>True while the active live document's session has ended.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>"Session 0x3A21 ended" (or "Client connection ended").</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>"DisconnectGraceful (21) from client · 12:04 attached · 18,248 events · 2.4 MB · not saved".</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = "";

    /// <summary>Why the last save failed; null when it did not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HasError))]
    public partial string? Error { get; private set; }

    /// <summary>The reminder, or why the last save failed.</summary>
    public string Hint => Error ?? StaysOpenHint;

    /// <summary>True when <see cref="Hint"/> reports a failed save (it takes the error colour).</summary>
    public bool HasError => Error is not null;

    /// <summary>True while a save runs (its button waits).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>
    /// The strip's detail line for a live document. Invariant culture, so a capture reads the same everywhere.
    /// </summary>
    public static string Describe(LiveDocumentViewModel live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var text = live.IsDetachedByUser
            ? LiveDocumentViewModel.DetachedReason
            : live.EndReason is { } reason
            ? "Disconnect" + reason + " (" + ((int)reason).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
            : live.Error ?? "The stream ended";
        if (live.EndReason is not null && live.EndedBy is { } side)
            text += " from " + side;
        if (live.AttachedAt is { } attached)
            text += " · " + SessionAreaViewModel.Clock((live.EndedAt ?? attached) - attached) + " attached";
        text += " · " + RecordingText.Plural(live.FrameCount, "event") + " · " + RecordingText.Size(live.ByteCount) + " · ";
        text += live.IsDirty ? "not saved" : live.SavedPath is { } path ? "saved as " + Path.GetFileName(path) : "nothing to save";
        return text;
    }

    /// <summary>"Session 0x3A21 ended"; a client target's single connection: "Client connection ended".</summary>
    public static string Headline(LiveDocumentViewModel live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.IsDetachedByUser)
            return live.AttachedSession is { } detached ? "Detached from " + LiveDocumentViewModel.SessionLabel(detached.Id) : "Detached";
        if (live.AttachedSession is { } session)
            return LiveDocumentViewModel.SessionLabel(session.Id) + " ended";
        return live.Session is { Role: NntlSessionRole.Client } ? "Client connection ended" : "Session ended";
    }

    /// <summary>Save .nntl…: saves the session, then the tab becomes the saved file.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (Live is not { } live)
            return;
        IsSaving = true;
        try
        {
            if (await _documents.SaveAsync(live).ConfigureAwait(true) is not { } path)
            {
                Error = _documents.SaveError;
                return;
            }

            // A session that streams again (attached elsewhere meanwhile) keeps its live tab; only ended data becomes a file.
            if (!live.IsStreaming)
                await _documents.ReplaceWithRecordingAsync(live, path).ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or NntlFormatException or UnauthorizedAccessException)
        {
            Error = "Saved, but the file did not open: " + e.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving;

    /// <summary>Attach to another session ▾: the session list, where picking one attaches this tab to it.</summary>
    [RelayCommand]
    private void AttachElsewhere() => _attachElsewhere();

    /// <summary>Discard: closes the tab and drops its data without asking again.</summary>
    [RelayCommand]
    private Task Discard() => Live is { } live ? _documents.CloseAsync(live, ask: false) : Task.CompletedTask;

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentsViewModel.Active)) Follow();
    }

    private void Follow() => Live = _documents.Active as LiveDocumentViewModel;

    partial void OnLiveChanged(LiveDocumentViewModel? oldValue, LiveDocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnLivePropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnLivePropertyChanged;
        Error = null;
        Refresh();
    }

    private void OnLivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LiveDocumentViewModel.State):
                // A new attach starts clean: the last save's error belonged to the ended session.
                Error = null;
                Refresh();
                break;
            case nameof(LiveDocumentViewModel.FrameCount) or nameof(LiveDocumentViewModel.ByteCount) or nameof(LiveDocumentViewModel.IsDirty)
                or nameof(LiveDocumentViewModel.SavedPath) or nameof(LiveDocumentViewModel.EndReason) or nameof(LiveDocumentViewModel.EndedBy)
                or nameof(LiveDocumentViewModel.EndedAt) or nameof(LiveDocumentViewModel.AttachedSession)
                or nameof(LiveDocumentViewModel.IsDetachedByUser):
                // Counts tick with every refresh while live; the strip only shows once ended.
                if (IsShown) Refresh();
                break;
        }
    }

    private void Refresh()
    {
        var live = Live;
        IsShown = live is { State: LiveState.Ended };
        if (live is null || !IsShown)
            return;
        Title = Headline(live);
        Detail = Describe(live);
    }
}
