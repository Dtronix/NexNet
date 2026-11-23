using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
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
    private readonly List<HexPosition> _bookmarks = [];

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

    /// <summary>
    /// Shows a dialog over the window (the hex menu's "Go to offset…"); false when another one is open. Without a host
    /// the dialog cannot open.
    /// </summary>
    public Func<GoToOffsetDialogViewModel, bool>? ShowDialog { get; set; }

    /// <summary>The bookmarked offsets ("Bookmark offset"), in the order they were added.</summary>
    public IReadOnlyList<HexPosition> Bookmarks => _bookmarks;

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

    /// <summary>True when <paramref name="position"/> is bookmarked.</summary>
    public bool IsBookmarked(HexPosition position) => _bookmarks.Contains(position);

    /// <summary>Bookmarks an offset, or drops its bookmark; returns whether it is bookmarked now.</summary>
    public bool ToggleBookmark(HexPosition position)
    {
        bool added = !_bookmarks.Remove(position);
        if (added) _bookmarks.Add(position);
        OnPropertyChanged(nameof(Bookmarks));
        return added;
    }

    /// <summary>The offsets a direction's stream holds now, [Start, End); empty without a document.</summary>
    public (long Start, long End) BoundsOf(TapDirection direction)
    {
        if (Document?.Session is not { } session) return (0, 0);
        lock (session.Gate)
        {
            TransportOpStore store = session.GetStore(direction);
            return (store.StartOffset, store.EndOffset);
        }
    }

    /// <summary>
    /// Selects the byte at <paramref name="position"/> (with the frame that holds it, so the inspector follows) and the
    /// hex view scrolls to it; false when the stream does not hold it.
    /// </summary>
    public bool GoTo(HexPosition position)
    {
        if (Document?.Session is not { } session) return false;
        FrameKey frame = new(position.Direction, -1);
        long? ticks = null;
        lock (session.Gate)
        {
            TransportOpStore store = session.GetStore(position.Direction);
            if (position.Offset < store.StartOffset || position.Offset >= store.EndOffset) return false;
            ReadOnlySpan<Core.Framing.FrameRecord> frames = session.Assembler.GetFrames(position.Direction);
            int at = HexLayout.FrameAt(frames, position.Offset);
            if (at >= 0)
            {
                frame = new FrameKey(position.Direction, frames[at].Seq);
                ticks = frames[at].StartTicks;
            }
        }

        Sync.Select(Selection.OfBytes(position.Direction, new StreamRange(position.Offset, 1)) with { Frame = frame }, ticks);
        return true;
    }

    /// <summary>"Go to offset…": asks for an offset (starting at <paramref name="from"/>), then goes there.</summary>
    /// <returns>The position gone to; null when canceled or no dialog could open.</returns>
    public async Task<HexPosition?> PromptGoToAsync(HexPosition from)
    {
        GoToOffsetDialogViewModel dialog = new(from, BoundsOf);
        if (ShowDialog is not { } show || !show(dialog)) return null;
        HexPosition? chosen = await dialog.Result.ConfigureAwait(true);
        return chosen is { } position && GoTo(position) ? position : null;
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
