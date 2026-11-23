using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Events pane's view-model: the active document's frames as an <see cref="EventsTableSource"/>, kept in step with
/// the shared <see cref="SyncBus"/> both ways (a row picked here selects its frame and moves the playhead; a frame
/// selected elsewhere selects and scrolls to its row), the empty and loading states, and following the live edge.
/// </summary>
public sealed partial class EventsViewModel : ObservableObject, IDisposable
{
    /// <summary>The empty hint while attached (data-table--empty).</summary>
    public const string LiveEmptyText = "No frames in view · waiting for traffic";

    /// <summary>The empty hint of a recording without frames.</summary>
    public const string FileEmptyText = "No frames in this session";

    /// <summary>The empty hint with no document open.</summary>
    public const string NoDocumentText = "No session open";

    private readonly DocumentsViewModel? _documents;
    private bool _fromBus;

    /// <summary>Events that follow the documents' active tab and share their sync bus.</summary>
    public EventsViewModel(DocumentsViewModel documents)
        : this((documents ?? throw new ArgumentNullException(nameof(documents))).Sync)
    {
        _documents = documents;
        documents.PropertyChanged += OnDocumentsChanged;
        Document = documents.Active;
    }

    /// <summary>Events over <paramref name="sync"/> whose document is set by hand.</summary>
    public EventsViewModel(SyncBus sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        Sync = sync;
        sync.Changed += OnSyncChanged;
    }

    /// <summary>The window, playhead and selection every view follows.</summary>
    public SyncBus Sync { get; }

    /// <summary>The rows: one instance for the pane's life, re-attached per document.</summary>
    public EventsTableSource Rows { get; } = new();

    /// <summary>The Events columns.</summary>
    public static IReadOnlyList<DataTableColumn> Columns => EventColumns.All;

    /// <summary>The document shown; null shows nothing.</summary>
    [ObservableProperty]
    public partial DocumentViewModel? Document { get; set; }

    /// <summary>The selected row; -1 for none. The table binds it two-way.</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    /// <summary>True while the document's file is still indexing (skeleton rows, OQ2).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The hint shown when there are no rows.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = NoDocumentText;

    /// <summary>
    /// True while the table keeps the newest row in view: a live document following its live edge (the sync bus's
    /// follow-live flag). The table turns it off when the user scrolls up, which stops following everywhere.
    /// </summary>
    public bool FollowLive
    {
        get => Document is LiveDocumentViewModel && Sync.FollowLive;
        set
        {
            if (Document is LiveDocumentViewModel) Sync.SetFollowLive(value);

            // Always told: a table that tried to follow a recording reads false back.
            OnPropertyChanged();
        }
    }

    /// <summary>The traced message, whose rows get the trace tint (I25 sets it from its trace; for now the selection's).</summary>
    public MessageRef Trace
    {
        get => Rows.Trace;
        set
        {
            if (value == Rows.Trace) return;
            Rows.Trace = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Brings the rows up to date with the document (also run on every data change).</summary>
    public void Refresh()
    {
        DocumentViewModel? document = Document;
        if (!ReferenceEquals(Rows.Session, document?.Session))
        {
            Rows.Attach(document?.Session);
            ApplySelection(Sync.Selection);
        }
        else
        {
            Rows.Refresh();
        }

        IsLoading = document is FileDocumentViewModel { IsIndexing: true };
        EmptyText = document switch
        {
            null => NoDocumentText,
            LiveDocumentViewModel => LiveEmptyText,
            _ => FileEmptyText,
        };
    }

    /// <summary>Traces the selected row's message (Enter, double-click): it becomes the selection, so every view follows.</summary>
    [RelayCommand]
    private void TraceSelected()
    {
        if (!Rows.TryGetRow(SelectedIndex, out EventRow row)) return;
        Sync.Select(Selection.OfMessage(row.Message, row.Frame), row.Ticks);
    }

    public void Dispose()
    {
        if (_documents is not null) _documents.PropertyChanged -= OnDocumentsChanged;
        Sync.Changed -= OnSyncChanged;
        Document = null;
    }

    partial void OnDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.DataChanged -= OnDataChanged;
        if (newValue is not null) newValue.DataChanged += OnDataChanged;
        Refresh();
        OnPropertyChanged(nameof(FollowLive));
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_fromBus || value < 0 || !Rows.TryGetRow(value, out EventRow row)) return;
        Sync.Select(Selection.OfFrame(row.Frame), row.Ticks);
        Trace = TraceOf(row.Message);
    }

    private void OnDataChanged(DocumentViewModel document) => Refresh();

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentsViewModel.Active)) Document = _documents!.Active;
    }

    private void OnSyncChanged(SyncChanges changes)
    {
        if ((changes & SyncChanges.Selection) != 0) ApplySelection(Sync.Selection);
        if ((changes & SyncChanges.FollowLive) != 0) OnPropertyChanged(nameof(FollowLive));
    }

    // A frame or message selected anywhere selects its row (the table scrolls it into view); anything else clears it.
    private void ApplySelection(Selection selection)
    {
        int index = selection.Kind is SelectionKind.Frame or SelectionKind.Message ? Rows.IndexOf(selection.Frame) : -1;
        _fromBus = true;
        try
        {
            SelectedIndex = index;
        }
        finally
        {
            _fromBus = false;
        }

        Trace = selection.Kind switch
        {
            SelectionKind.Message => selection.Message,
            SelectionKind.Frame when index >= 0 && Rows.TryGetRow(index, out EventRow row) => TraceOf(row.Message),
            _ => MessageRef.None,
        };
    }

    // The tint marks the other frames of a message split across several: a frame that is its own message has none.
    private static MessageRef TraceOf(MessageRef message) => message.Kind == MessageRefKind.Frame ? MessageRef.None : message;
}
