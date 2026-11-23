using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Hex stream pane's view-model: the <see cref="HexLayout"/> of the active document's capture session (updated
/// whenever the document reports new data, at most once per UI turn), the pane header's ▼ IN | ▲ OUT toggles (never
/// both off) and the shared <see cref="SyncBus"/> the view links hover and selection through.
/// </summary>
public sealed partial class HexViewModel : ObservableObject, IDisposable
{
    private readonly DocumentsViewModel? _documents;

    /// <summary>A hex pane that follows the documents' active tab and shares their sync bus.</summary>
    public HexViewModel(DocumentsViewModel documents)
        : this((documents ?? throw new ArgumentNullException(nameof(documents))).Sync)
    {
        _documents = documents;
        documents.PropertyChanged += OnDocumentsChanged;
        Document = documents.Active;
    }

    /// <summary>A hex pane over <paramref name="sync"/> whose document is set by hand.</summary>
    public HexViewModel(SyncBus sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        Sync = sync;
    }

    /// <summary>The selection, hovered field, playhead and window every view follows.</summary>
    public SyncBus Sync { get; }

    /// <summary>The lines shown; one instance for the pane's life, laid out again per document and filter.</summary>
    public HexLayout Layout { get; } = new();

    /// <summary>Bytes per row, for the pane header ("16 B/row").</summary>
    public int BytesPerRow => HexLayout.BytesPerRow;

    /// <summary>The pane header's row-width label.</summary>
    public string RowLabel => "16 B/row";

    /// <summary>The document shown; null shows the empty hint.</summary>
    [ObservableProperty]
    public partial DocumentViewModel? Document { get; set; }

    /// <summary>The ▼ IN toggle: show the bytes the tapped endpoint read.</summary>
    [ObservableProperty]
    public partial bool ShowsInbound { get; set; } = true;

    /// <summary>The ▲ OUT toggle: show the bytes it wrote.</summary>
    [ObservableProperty]
    public partial bool ShowsOutbound { get; set; } = true;

    /// <summary>True when there are no bytes to show (the view shows <see cref="HexView.EmptyText"/>).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>The directions shown.</summary>
    public HexDirections Directions =>
        (ShowsInbound ? HexDirections.Inbound : HexDirections.None) | (ShowsOutbound ? HexDirections.Outbound : HexDirections.None);

    /// <summary>Brings the layout up to date with the document's session.</summary>
    public void Refresh()
    {
        CaptureSession? session = Document?.Session;
        if (session is null)
        {
            Layout.Update(null, Directions);
        }
        else
        {
            lock (session.Gate)
                Layout.Update(session, Directions);
        }

        IsEmpty = Layout.LineCount == 0;
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
        Refresh();
    }

    // Turning the last direction off turns the other one on: the view never shows nothing by filter.
    partial void OnShowsInboundChanged(bool value)
    {
        if (!value && !ShowsOutbound)
            ShowsOutbound = true;
        OnPropertyChanged(nameof(Directions));
        Refresh();
    }

    partial void OnShowsOutboundChanged(bool value)
    {
        if (!value && !ShowsInbound)
            ShowsInbound = true;
        OnPropertyChanged(nameof(Directions));
        Refresh();
    }

    private void OnDataChanged(DocumentViewModel document) => Refresh();

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentsViewModel.Active)) Document = _documents!.Active;
    }
}
