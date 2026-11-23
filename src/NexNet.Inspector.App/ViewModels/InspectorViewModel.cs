using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The inspector pane (property-tree): the selected frame of the active document decoded field by field, its
/// transport ops and the reassembled message, plus the assembly bar. It follows the <see cref="SyncBus"/>: a frame,
/// message, entity or byte-range selection picks the frame shown, a byte range (a hex selection) selects the field
/// that holds it; hovering a field publishes it as the hovered field (the hex view lights its bytes) and clicking one
/// selects its bytes.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject, IDisposable
{
    /// <summary>The empty state's hint (property-tree--empty).</summary>
    public const string EmptyHint = "Select a frame on the timeline or in a view to decode it.";

    private static readonly string[] DecodedTabs = ["Decoded", "Message trace"];
    private static readonly string[] HandshakeTabs = ["Decoded", "Handshake"];

    private readonly DocumentsViewModel? _documents;
    private DocumentViewModel? _document;
    private bool _publishing;

    /// <summary>An inspector following the documents' active tab and their sync bus.</summary>
    public InspectorViewModel(DocumentsViewModel documents)
        : this((documents ?? throw new ArgumentNullException(nameof(documents))).Sync)
    {
        _documents = documents;
        documents.PropertyChanged += OnDocumentsChanged;
        Document = documents.Active;
    }

    /// <summary>An inspector over <paramref name="sync"/> whose document is set by hand.</summary>
    public InspectorViewModel(SyncBus sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        Sync = sync;
        Sync.Changed += OnSyncChanged;
    }

    /// <summary>The selection and hover every view shares.</summary>
    public SyncBus Sync { get; }

    /// <summary>The document inspected; null shows the empty hint.</summary>
    public DocumentViewModel? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(value, _document)) return;
            if (_document is not null) _document.DataChanged -= OnDataChanged;
            _document = value;
            if (_document is not null) _document.DataChanged += OnDataChanged;
            OnPropertyChanged();
            Refresh(force: true);
        }
    }

    /// <summary>What is shown; null when nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tree), nameof(Rows), nameof(HasContent), nameof(FrameLabel), nameof(Assembly), nameof(HasAssembly), nameof(TabLabels),
        nameof(ShowsTree), nameof(ShowsHint), nameof(ShowsAssembly))]
    public partial InspectorContent? Content { get; private set; }

    /// <summary>The Decoded tab's tree.</summary>
    public PropertyTreeModel? Tree => Content?.Tree;

    /// <summary>The tree's visible rows (the list's items).</summary>
    public PropertyRowList? Rows => Content?.Tree.Rows;

    /// <summary>True while a frame is shown.</summary>
    public bool HasContent => Content is not null;

    /// <summary>The header's frame id.</summary>
    public string? FrameLabel => Content?.FrameLabel;

    /// <summary>The assembly footer; null for a single-frame message.</summary>
    public AssemblyInfo? Assembly => Content?.Assembly;

    /// <summary>True when the assembly footer shows.</summary>
    public bool HasAssembly => Content?.Assembly is not null;

    /// <summary>Decoded | Message trace, or Decoded | Handshake on a handshake frame.</summary>
    public IReadOnlyList<string> TabLabels => Content is { IsHandshake: true } ? HandshakeTabs : DecodedTabs;

    /// <summary>The selected tab (the header's tabs bind it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tab), nameof(IsDecoded), nameof(IsTrace), nameof(ShowsTree), nameof(ShowsHint), nameof(ShowsAssembly))]
    public partial int TabIndex { get; set; }

    /// <summary>The selected tab.</summary>
    public InspectorTab Tab => TabIndex == 1 ? InspectorTab.Trace : InspectorTab.Decoded;

    /// <summary>True on the Decoded tab.</summary>
    public bool IsDecoded => Tab == InspectorTab.Decoded;

    /// <summary>True on the Message trace / Handshake tab (its content is I25's).</summary>
    public bool IsTrace => Tab == InspectorTab.Trace;

    /// <summary>True when the Decoded tab shows a frame's tree.</summary>
    public bool ShowsTree => HasContent && IsDecoded;

    /// <summary>True when the Decoded tab shows the empty hint.</summary>
    public bool ShowsHint => !HasContent && IsDecoded;

    /// <summary>True when the Decoded tab shows the assembly footer.</summary>
    public bool ShowsAssembly => HasAssembly && IsDecoded;

    /// <summary>The selected row: follows a byte-range selection; set by a click, it selects the row's bytes.</summary>
    [ObservableProperty]
    public partial PropertyRow? SelectedRow { get; set; }

    /// <summary>The row under the pointer: published to the sync bus as the hovered field.</summary>
    [ObservableProperty]
    public partial PropertyRow? HoveredRow { get; set; }

    /// <summary>The header's link badge: the inspector always follows the timeline.</summary>
    public PaneSyncState SyncState => PaneSyncState.Synced;

    /// <summary>Expands or collapses a row (a section's title, a nested field's ▸).</summary>
    public void Toggle(PropertyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.Model.Toggle(row.Node);
    }

    public void Dispose()
    {
        if (_documents is not null) _documents.PropertyChanged -= OnDocumentsChanged;
        Sync.Changed -= OnSyncChanged;
        Document = null;
    }

    partial void OnSelectedRowChanged(PropertyRow? value)
    {
        if (_publishing || value is null || value.Bytes.IsNone || Content is not { } content) return;
        PropertyBytes bytes = value.Bytes;
        // The byte range keeps the frame and message it came from, so this inspector stays on its frame.
        Selection selection = Selection.OfBytes(bytes.Direction, bytes.Stream) with { Frame = content.Frame, Message = content.Message };
        _publishing = true;
        try
        {
            Sync.Select(selection);
        }
        finally
        {
            _publishing = false;
        }
    }

    partial void OnHoveredRowChanged(PropertyRow? value)
    {
        if (value is null || value.Bytes.IsNone)
        {
            Sync.SetHover(FieldHover.None);
            return;
        }

        PropertyBytes bytes = value.Bytes;
        // A decoded field names its index in the decoded value; a header or transport row its node (the row's identity).
        int field = value.Model.Node(value.Node).FieldIndex is var index and >= 0 ? index : value.Node;
        Sync.SetHover(new FieldHover(bytes.Message, field, bytes.Offset, bytes.Length));
    }

    private void OnSyncChanged(SyncChanges changes)
    {
        if ((changes & SyncChanges.Selection) != 0) Refresh(force: false);
    }

    private void OnDataChanged(DocumentViewModel document)
    {
        // A live frame can still be growing (its chunks arriving, its result pending): rebuild only such content.
        if (Content is { IsFinal: false }) Refresh(force: true);
    }

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentsViewModel.Active)) Document = _documents!.Active;
    }

    private void Refresh(bool force)
    {
        Selection selection = Sync.Selection;
        if (_document?.Session is not { } session || selection.IsNone)
        {
            Show(null);
            return;
        }

        InspectorContent? content;
        lock (session.Gate)
        {
            if (FrameOf(session, selection) is not { } frame)
            {
                content = null;
            }
            else if (!force && Content is { } current && current.Frame == frame)
            {
                content = current;
            }
            else
            {
                content = InspectorProjection.Build(session, frame);
            }

            if (content is not null && selection.Kind == SelectionKind.ByteRange)
            {
                int node = content.Tree.Reveal(selection.Direction, selection.Bytes.Offset);
                Show(content);
                Follow(node >= 0 ? content.Tree.RowOf(node) : null);
                return;
            }
        }

        Show(content);
    }

    private void Show(InspectorContent? content)
    {
        if (ReferenceEquals(content, Content)) return;
        _publishing = true;
        try
        {
            SelectedRow = null;
            HoveredRow = null;
            Content = content;
        }
        finally
        {
            _publishing = false;
        }
    }

    private void Follow(PropertyRow? row)
    {
        _publishing = true;
        try
        {
            SelectedRow = row;
        }
        finally
        {
            _publishing = false;
        }
    }

    /// <summary>The frame a selection names: the frame itself, a message's frame, an entity's first frame, the frame holding a byte.</summary>
    internal static FrameKey? FrameOf(CaptureSession session, in Selection selection)
    {
        MessageAssembler assembler = session.Assembler;
        switch (selection.Kind)
        {
            case SelectionKind.Frame:
                return selection.Frame;
            case SelectionKind.Message:
                return selection.Frame.Seq >= 0 ? selection.Frame : new FrameKey(selection.Message.Direction, selection.Message.Seq);
            case SelectionKind.Entity when selection.Entity == EntityKind.Invocation && (uint)selection.EntityIndex < (uint)assembler.Invocations.Length:
            {
                InvocationRow invocation = assembler.Invocations[selection.EntityIndex];
                return invocation.HasRequest ? new FrameKey(invocation.Request.Direction, invocation.Request.Seq) : null;
            }
            case SelectionKind.Entity when selection.Entity == EntityKind.Pipe && (uint)selection.EntityIndex < (uint)assembler.Pipes.Length:
            {
                // The Ready (or Refused) frame: its direction is not stored, so the one whose link names the pipe.
                PipeRow pipe = assembler.Pipes[selection.EntityIndex];
                if (pipe.OpenSeq < 0) return null;
                foreach (Core.Model.TapDirection d in (ReadOnlySpan<Core.Model.TapDirection>)[Core.Model.TapDirection.Inbound, Core.Model.TapDirection.Outbound])
                {
                    FrameLink link = assembler.GetLink(d, pipe.OpenSeq);
                    if (link.Entity == EntityKind.Pipe && link.EntityIndex == selection.EntityIndex) return new FrameKey(d, pipe.OpenSeq);
                }

                return null;
            }
            case SelectionKind.Entity when selection.Entity == EntityKind.Greeting && (uint)selection.EntityIndex < (uint)assembler.Greetings.Length:
            {
                GreetingRow greeting = assembler.Greetings[selection.EntityIndex];
                return new FrameKey(greeting.Direction, greeting.Seq);
            }
            case SelectionKind.ByteRange:
                return selection.Frame.Seq >= 0 ? selection.Frame : FrameAt(assembler, selection.Direction, selection.Bytes.Offset);
            default:
                return null;
        }
    }

    // The frame of a direction that holds a stream byte: frames are in stream order, so a binary search.
    private static FrameKey? FrameAt(MessageAssembler assembler, Core.Model.TapDirection direction, long offset)
    {
        ReadOnlySpan<Core.Framing.FrameRecord> frames = assembler.GetFrames(direction);
        int lo = 0;
        int hi = frames.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (frames[mid].Offset + frames[mid].Length <= offset) lo = mid + 1;
            else if (frames[mid].Offset > offset) hi = mid - 1;
            else return new FrameKey(direction, frames[mid].Seq);
        }

        return null;
    }
}
