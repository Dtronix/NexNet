using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
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
    private MessageTrace _trace = MessageTrace.None;
    private TimeWindow _traceScope;
    private CaptureSession? _handshakeSession;
    private long _handshakeVersion = -1;
    private bool _publishing;
    private SelectionDecode _shownDecode = SelectionDecode.Auto;

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
            _trace = MessageTrace.None;
            RefreshTrace();
        }
    }

    /// <summary>The Message trace tab's hint when nothing is traced.</summary>
    public const string TraceHint = "Select a message on the timeline or in a view to follow it across frames.";

    /// <summary>What is shown; null when nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tree), nameof(Rows), nameof(HasContent), nameof(FrameLabel), nameof(Assembly), nameof(HasAssembly), nameof(TabLabels),
        nameof(ShowsTree), nameof(ShowsHint), nameof(ShowsAssembly), nameof(IsHandshake), nameof(ShowsHandshake), nameof(ShowsTraceList),
        nameof(ShowsTraceHint))]
    public partial InspectorContent? Content { get; private set; }

    /// <summary>True for a preamble or greeting: the second tab is the Handshake tab.</summary>
    public bool IsHandshake => Content is { IsHandshake: true };

    /// <summary>The trace the Message trace tab lists (the sync bus's).</summary>
    public MessageTrace Trace => _trace;

    /// <summary>The Message trace tab's rows, one per step in wire order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrace), nameof(ShowsTraceList), nameof(ShowsTraceHint))]
    public partial IReadOnlyList<TraceStepRow> TraceRows { get; private set; } = [];

    /// <summary>True when a message is traced.</summary>
    public bool HasTrace => TraceRows.Count > 0;

    /// <summary>The Message trace tab's title ("Invocation #812 · SendAttachment").</summary>
    [ObservableProperty]
    public partial string TraceTitle { get; private set; } = "";

    /// <summary>The Message trace tab's summary ("7 frames · 15.0 ms · 6 interleaved").</summary>
    [ObservableProperty]
    public partial string TraceSummary { get; private set; } = "";

    /// <summary>The current step: the selected frame's; selecting another selects its frame (the playhead jumps).</summary>
    [ObservableProperty]
    public partial TraceStepRow? SelectedTraceRow { get; set; }

    /// <summary>The Handshake tab's tree: every handshake frame decoded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HandshakeRows))]
    public partial PropertyTreeModel? HandshakeTree { get; private set; }

    /// <summary>The Handshake tree's visible rows.</summary>
    public PropertyRowList? HandshakeRows => HandshakeTree?.Rows;

    /// <summary>The Handshake tree's selected row; a click selects the field's bytes.</summary>
    [ObservableProperty]
    public partial PropertyRow? HandshakeSelectedRow { get; set; }

    /// <summary>The Handshake tree's row under the pointer: published as the hovered field.</summary>
    [ObservableProperty]
    public partial PropertyRow? HandshakeHoveredRow { get; set; }

    /// <summary>The Decoded tree's row whose bytes the hex view hovers.</summary>
    [ObservableProperty]
    public partial PropertyRow? LinkedRow { get; private set; }

    /// <summary>The Handshake tree's row whose bytes the hex view hovers.</summary>
    [ObservableProperty]
    public partial PropertyRow? HandshakeLinkedRow { get; private set; }

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
    [NotifyPropertyChangedFor(nameof(Tab), nameof(IsDecoded), nameof(IsTrace), nameof(ShowsTree), nameof(ShowsHint), nameof(ShowsAssembly),
        nameof(ShowsHandshake), nameof(ShowsTraceList), nameof(ShowsTraceHint))]
    public partial int TabIndex { get; set; }

    /// <summary>The selected tab.</summary>
    public InspectorTab Tab => TabIndex == 1 ? InspectorTab.Trace : InspectorTab.Decoded;

    /// <summary>True on the Decoded tab.</summary>
    public bool IsDecoded => Tab == InspectorTab.Decoded;

    /// <summary>True on the Message trace / Handshake tab.</summary>
    public bool IsTrace => Tab == InspectorTab.Trace;

    /// <summary>True when the Decoded tab shows a frame's tree.</summary>
    public bool ShowsTree => HasContent && IsDecoded;

    /// <summary>True when the Decoded tab shows the empty hint.</summary>
    public bool ShowsHint => !HasContent && IsDecoded;

    /// <summary>True when the assembly footer shows: under the Decoded tree and under the trace alike.</summary>
    public bool ShowsAssembly => HasAssembly;

    /// <summary>True when the second tab shows the Handshake tree.</summary>
    public bool ShowsHandshake => IsTrace && IsHandshake;

    /// <summary>True when the second tab lists the traced message's steps.</summary>
    public bool ShowsTraceList => IsTrace && !IsHandshake && HasTrace;

    /// <summary>True when the second tab shows its hint: nothing traced.</summary>
    public bool ShowsTraceHint => IsTrace && !IsHandshake && !HasTrace;

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

    partial void OnSelectedRowChanged(PropertyRow? value) => SelectBytes(value);

    partial void OnHandshakeSelectedRowChanged(PropertyRow? value) => SelectBytes(value);

    partial void OnHoveredRowChanged(PropertyRow? value) => PublishHover(value);

    partial void OnHandshakeHoveredRowChanged(PropertyRow? value) => PublishHover(value);

    partial void OnSelectedTraceRowChanged(TraceStepRow? value)
    {
        if (_publishing || value is null) return;
        TraceStep step = value.Step;
        // The traced message stays the selection (picked from the step's frame), so the trace and its list stay put
        // while the inspector, the panes and the playhead move to the step.
        Selection selection = _trace.Message.IsNone ? Selection.OfFrame(step.Frame) : Selection.OfMessage(_trace.Message, step.Frame);
        _publishing = true;
        try
        {
            Sync.Select(selection, step.Ticks);
        }
        finally
        {
            _publishing = false;
        }
    }

    private void SelectBytes(PropertyRow? value)
    {
        if (_publishing || value is null || value.Bytes.IsNone || Content is not { } content) return;
        PropertyBytes bytes = value.Bytes;
        // The byte range keeps the frame and message it came from, so this inspector stays on its frame; a row of a
        // frame's own bytes (a handshake row of another frame) names that frame.
        FrameKey frame = bytes.Message.Kind == MessageRefKind.Frame ? new FrameKey(bytes.Message.Direction, bytes.Message.Seq) : content.Frame;
        MessageRef message = frame == content.Frame ? content.Message : bytes.Message;
        Selection selection = Selection.OfBytes(bytes.Direction, bytes.Stream) with { Frame = frame, Message = message };
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

    private void PublishHover(PropertyRow? value)
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
        if ((changes & SyncChanges.Decode) != 0) Refresh(force: true);
        else if ((changes & SyncChanges.Selection) != 0) Refresh(force: false);
        if ((changes & (SyncChanges.Selection | SyncChanges.Trace | SyncChanges.Scope)) != 0) RefreshTrace();
        if ((changes & (SyncChanges.Selection | SyncChanges.Hover)) != 0) RefreshLinked();
    }

    // The hex view hovers a field: light the row with its bytes in whichever tree shows (hex → tree linking).
    private void RefreshLinked()
    {
        FieldHover hover = Sync.Hover;
        LinkedRow = LinkedIn(Tree, hover);
        HandshakeLinkedRow = LinkedIn(HandshakeTree, hover);
    }

    private static PropertyRow? LinkedIn(PropertyTreeModel? tree, in FieldHover hover)
    {
        if (tree is null || hover.IsNone || hover.Length <= 0) return null;
        int node = tree.FindVisible(hover.Message, hover.Offset, hover.Length);
        return node >= 0 ? tree.RowOf(node) : null;
    }

    // The bus's trace as the Message trace tab's rows; the current step follows the frame shown.
    private void RefreshTrace()
    {
        MessageTrace trace = Sync.Trace;
        if (!ReferenceEquals(trace, _trace) || Sync.Scope != _traceScope)
        {
            _trace = trace;
            _traceScope = Sync.Scope;
            OnPropertyChanged(nameof(Trace));
            TraceStepRow[] rows = [];
            string summary = "";
            if (trace.IsActive && _document?.Session is { } session)
            {
                long frequency = session.Header.StopwatchFrequency;
                ReadOnlySpan<TraceStep> steps = trace.Steps;
                rows = new TraceStepRow[steps.Length];
                lock (session.Gate)
                {
                    long origin = session.StartTicks;
                    for (int i = 0; i < steps.Length; i++)
                        rows[i] = new TraceStepRow(steps[i], origin, frequency, TraceStepRow.PipeKindOf(session, steps[i]), _traceScope);
                }
                CultureInfo invariant = CultureInfo.InvariantCulture;
                int members = trace.MemberCount;
                int interleaved = trace.InterleavedCount;
                double ms = (trace.End - trace.Start) * 1000.0 / Math.Max(1, frequency);
                int outside = 0;
                foreach (TraceStepRow row in rows) outside += row.IsOutOfScope ? 1 : 0;
                summary = members.ToString(invariant) + (members == 1 ? " frame" : " frames") + " · " + ms.ToString("N1", invariant) + " ms"
                          + (interleaved > 0 ? " · " + interleaved.ToString(invariant) + " interleaved" : "")
                          + (outside > 0 ? " · " + outside.ToString(invariant) + " outside the scope" : "");
            }

            _publishing = true;
            try
            {
                SelectedTraceRow = null;
                TraceRows = rows;
                TraceTitle = trace.IsActive ? trace.Title : "";
                TraceSummary = summary;
            }
            finally
            {
                _publishing = false;
            }
        }

        FollowTraceStep();
    }

    private void FollowTraceStep()
    {
        TraceStepRow? current = null;
        if (Content is { } content)
        {
            foreach (TraceStepRow row in TraceRows)
            {
                if (row.Step.Frame == content.Frame)
                {
                    current = row;
                    break;
                }
            }
        }

        if (ReferenceEquals(current, SelectedTraceRow)) return;
        _publishing = true;
        try
        {
            SelectedTraceRow = current;
        }
        finally
        {
            _publishing = false;
        }
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
            _shownDecode = SelectionDecode.Auto;
            Show(null);
            return;
        }

        InspectorContent? content;
        SelectionDecode decode = Sync.Decode;
        bool decoding = decode.Covers(selection);
        lock (session.Gate)
        {
            // "Decode selection as ▸": the picked reading of the bytes replaces the schema's tree while the selection
            // stays inside them; leaving it rebuilds the frame's own tree.
            bool wasDecoding = !_shownDecode.IsAuto;
            _shownDecode = decoding ? decode : SelectionDecode.Auto;
            if (decoding)
            {
                content = !force && wasDecoding && Content is { } shown ? shown : SelectionProjection.Build(session, selection, decode);
            }
            else if (FrameOf(session, selection) is not { } frame)
            {
                content = null;
            }
            else if (!force && !wasDecoding && Content is { } current && current.Frame == frame)
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
            HandshakeSelectedRow = null;
            HandshakeHoveredRow = null;
            Content = content;
            HandshakeTree = content is { IsHandshake: true } ? BuildHandshake() : null;
        }
        finally
        {
            _publishing = false;
        }

        RefreshLinked();
        FollowTraceStep();
    }

    // The Handshake tab's tree, kept while the frames it was built from stay the same.
    private PropertyTreeModel? BuildHandshake()
    {
        if (_document?.Session is not { } session) return null;
        lock (session.Gate)
        {
            if (HandshakeTree is { } current && ReferenceEquals(_handshakeSession, session) && _handshakeVersion == session.Version) return current;
            _handshakeSession = session;
            _handshakeVersion = session.Version;
            return InspectorProjection.BuildHandshake(session);
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
