using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Services;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The timeline's view-model: the <see cref="TimelineModel"/> projected from the active document's capture session
/// (refreshed whenever the document reports new data, at most once per UI turn), the shared <see cref="SyncBus"/>, and
/// the pin and collapse commands the control and the lane menu (I26) run.
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject, IDisposable
{
    private readonly TimelineProjection _projection;
    private readonly DocumentsViewModel? _documents;

    /// <summary>A timeline that follows the documents' active tab and shares their sync bus.</summary>
    public TimelineViewModel(DocumentsViewModel documents)
        : this((documents ?? throw new ArgumentNullException(nameof(documents))).Sync)
    {
        _documents = documents;
        documents.PropertyChanged += OnDocumentsChanged;
        Document = documents.Active;
    }

    /// <summary>A timeline over <paramref name="sync"/> whose document is set by hand.</summary>
    public TimelineViewModel(SyncBus sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        Sync = sync;
        _projection = new TimelineProjection(Model);
    }

    /// <summary>The window, playhead and selection every view follows.</summary>
    public SyncBus Sync { get; }

    /// <summary>The lanes drawn; one instance for the timeline's life, refilled per document.</summary>
    public TimelineModel Model { get; } = new();

    /// <summary>The document shown; null shows nothing.</summary>
    [ObservableProperty]
    public partial DocumentViewModel? Document { get; set; }

    /// <summary>Projects the document's session again (also run on every data change).</summary>
    public void Refresh()
    {
        if (Document?.Session is { } session)
            _projection.Update(session);
        else if (_projection.Session is not null)
            _projection.Reset();
    }

    /// <summary>Pins or unpins a lane: a pinned lane stays and expands.</summary>
    [RelayCommand]
    private void TogglePin(LaneKey lane) => Model.TogglePinned(lane);

    /// <summary>Collapses or expands a lane group.</summary>
    [RelayCommand]
    private void ToggleGroup(LaneGroupKind group) => Model.ToggleCollapsed(group);

    public void Dispose()
    {
        if (_documents is not null) _documents.PropertyChanged -= OnDocumentsChanged;
        Document = null;
    }

    partial void OnDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.DataChanged -= OnDataChanged;
        if (newValue is not null) newValue.DataChanged += OnDataChanged;
        _projection.Reset();
        Refresh();
    }

    private void OnDataChanged(DocumentViewModel document) => Refresh();

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentsViewModel.Active)) Document = _documents!.Active;
    }
}
