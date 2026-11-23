using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Services;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Ladder pane's view-model: the <see cref="LadderModel"/> projected from the active document's capture session
/// (refreshed whenever the document reports new data, at most once per UI turn), the shared <see cref="SyncBus"/>, the
/// row mode the pane header's select chooses (OQ6: "18 px rows" or "Time-scaled"), and whether the pane follows the
/// shared time window (the header's "synced").
/// </summary>
public sealed partial class LadderViewModel : ObservableObject, IDisposable
{
    private readonly LadderProjection _projection;
    private readonly DocumentsViewModel? _documents;

    /// <summary>A ladder that follows the documents' active tab and shares their sync bus.</summary>
    public LadderViewModel(DocumentsViewModel documents)
        : this((documents ?? throw new ArgumentNullException(nameof(documents))).Sync)
    {
        _documents = documents;
        documents.PropertyChanged += OnDocumentsChanged;
        Document = documents.Active;
    }

    /// <summary>A ladder over <paramref name="sync"/> whose document is set by hand.</summary>
    public LadderViewModel(SyncBus sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        Sync = sync;
        _projection = new LadderProjection(Model);
        Mode = new EnumChoice<LadderMode>([(LadderMode.Compact, "18 px rows"), (LadderMode.TimeScaled, "Time-scaled")], LadderMode.Compact);
    }

    /// <summary>The window, playhead and selection every view follows.</summary>
    public SyncBus Sync { get; }

    /// <summary>The rows drawn; one instance for the pane's life, refilled per document.</summary>
    public LadderModel Model { get; } = new();

    /// <summary>Compact or time-scaled rows: bind the pane header's select to it and the view's Mode to its Value.</summary>
    public EnumChoice<LadderMode> Mode { get; }

    /// <summary>True while the ladder and the shared time window scroll together.</summary>
    [ObservableProperty]
    public partial bool IsSynced { get; set; } = true;

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
