using System.Runtime.InteropServices;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Compare;

/// <summary>One row of the aligned view, laid out top to bottom across both files.</summary>
/// <param name="Kind">What the row is.</param>
/// <param name="Side">The file it belongs to.</param>
/// <param name="Category">Its category (Transport for a file header).</param>
/// <param name="Group">For a group row, its index in <see cref="CompareSession.Groups"/>; -1 otherwise.</param>
/// <param name="Top">Its top in content coordinates (0 is the first file's header).</param>
/// <param name="Height">Its height.</param>
/// <param name="HasDelta">True when an entity it shows differs from the other file (the gutter's 3 px Δ flag).</param>
/// <param name="Label">The gutter label.</param>
/// <param name="Count">The gutter's mono count (entities, frames or the pinned entity's duration).</param>
public readonly record struct CompareRow(CompareRowKind Kind, CompareSide Side, CompareCategory Category, int Group, double Top,
    double Height, bool HasDelta, string Label, string Count);

/// <summary>A request to open a place of the compare in Inspect (a double-click).</summary>
/// <param name="Side">The file to open.</param>
/// <param name="Match">The entity, its index in <see cref="CompareSession.Matches"/>; -1 for a time only.</param>
/// <param name="Time">The compare time clicked.</param>
public readonly record struct CompareOpenRequest(CompareSide Side, int Match, long Time);

/// <summary>
/// The state of the aligned view (screens/compare, centre): the compare, the time window over compare time, the
/// playhead, the selected (pinned) entity, which categories are expanded, which groups show, the row mode and the
/// "Only differences" filter; and the rows those make, laid out once per change so a redraw only reads them. The
/// view-model drives it; <see cref="CompareRowsControl"/> draws it and reports clicks back through it. UI thread only.
/// </summary>
public sealed class CompareRowsModel
{
    /// <summary>A file header's height.</summary>
    public const double HeaderHeight = 24;

    /// <summary>A heat row's height (compare.heat-row.height).</summary>
    public const double HeatHeight = 16;

    /// <summary>A packed row's height (compare.packed-row.height).</summary>
    public const double PackedHeight = 18;

    private const int Categories = (int)CompareCategory.Handshake + 1;

    private static readonly CompareCategory[] EntityCategories =
        [CompareCategory.Invocations, CompareCategory.Pipes, CompareCategory.Collections, CompareCategory.Control, CompareCategory.Handshake];

    private readonly List<CompareRow> _rows = [];
    private readonly bool[] _expanded = new bool[Categories];
    private readonly (int First, int Count)[] _categoryGroups = new (int, int)[Categories];
    private readonly bool[] _categoryDelta = new bool[Categories];
    private readonly string[] _categoryCountA = new string[Categories];
    private readonly string[] _categoryCountB = new string[Categories];
    private string[] _groupLabelA = [];
    private string[] _groupLabelB = [];
    private string[] _groupFramesA = [];
    private string[] _groupFramesB = [];
    private bool[] _groupHidden = [];
    private string _pinnedLabelA = "";
    private string _pinnedLabelB = "";
    private string _pinnedValueA = "";
    private string _pinnedValueB = "";
    private CompareRowMode _rowMode;
    private bool _onlyDifferences;

    public CompareRowsModel()
    {
        // The package's screen: pipes and channels open, the busy categories as heat.
        _expanded[(int)CompareCategory.Pipes] = true;
        for (int i = 0; i < Categories; i++)
        {
            _categoryCountA[i] = "";
            _categoryCountB[i] = "";
        }
    }

    /// <summary>Raised after a change, with what it touched.</summary>
    public event Action<CompareChanges>? Changed;

    /// <summary>Raised by a double-click: the view-model opens the place in Inspect.</summary>
    public event Action<CompareOpenRequest>? OpenRequested;

    /// <summary>Raised when a click picks an entity (the view-model shows its detail).</summary>
    public event Action<int>? SelectRequested;

    /// <summary>The compare drawn; null shows nothing.</summary>
    public CompareSession? Session { get; private set; }

    /// <summary>Document A's frames by category.</summary>
    public CompareDensity DensityA { get; private set; } = CompareDensity.Empty;

    /// <summary>Document B's frames by category.</summary>
    public CompareDensity DensityB { get; private set; } = CompareDensity.Empty;

    /// <summary>The window over compare time (TimeSpan ticks); the whole compare by default.</summary>
    public TimeWindow Window { get; private set; }

    /// <summary>The playhead, in compare time.</summary>
    public long Playhead { get; private set; }

    /// <summary>The selected entity (pinned under its category, the diff band around it); -1 for none.</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>The file headers' names (the documents' tab titles).</summary>
    public string NameA { get; private set; } = "";

    /// <summary>See <see cref="NameA"/>.</summary>
    public string NameB { get; private set; } = "";

    /// <summary>The file headers' "client side · recorded … · offset …".</summary>
    public string DetailA { get; private set; } = "";

    /// <summary>See <see cref="DetailA"/>.</summary>
    public string DetailB { get; private set; } = "";

    /// <summary>Each file's colour: its tab marker's.</summary>
    public ThemeBrush BrushA { get; private set; } = ThemeBrush.DirectionInbound;

    /// <summary>See <see cref="BrushA"/>.</summary>
    public ThemeBrush BrushB { get; private set; } = ThemeBrush.DirectionOutbound;

    /// <summary>How collapsed categories draw.</summary>
    public CompareRowMode RowMode
    {
        get => _rowMode;
        set
        {
            if (_rowMode == value) return;
            _rowMode = value;
            Relayout();
        }
    }

    /// <summary>"Only differences": hides categories and groups whose entities match.</summary>
    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set
        {
            if (_onlyDifferences == value) return;
            _onlyDifferences = value;
            Relayout();
        }
    }

    /// <summary>The rows, top to bottom.</summary>
    public ReadOnlySpan<CompareRow> Rows => CollectionsMarshal.AsSpan(_rows);

    /// <summary>The rows' total height.</summary>
    public double Height { get; private set; }

    /// <summary>The whole compare timeline; empty without a compare.</summary>
    public TimeWindow Bounds => Session?.Range ?? default;

    /// <summary>
    /// Shows a compare (or none). The expansion and the window carry over when the same documents are compared again
    /// (an alignment, a refresh); a new pair starts on its whole range.
    /// </summary>
    /// <param name="session">The compare.</param>
    /// <param name="densityA">Document A's frames.</param>
    /// <param name="densityB">Document B's frames.</param>
    /// <param name="keepWindow">True to keep the window (clamped to the new range).</param>
    public void SetSession(CompareSession? session, CompareDensity? densityA, CompareDensity? densityB, bool keepWindow)
    {
        // Another compare's ticks do not carry over; a refresh of the same one keeps them.
        if (!keepWindow) _groupHidden = [];
        Session = session;
        DensityA = densityA ?? CompareDensity.Empty;
        DensityB = densityB ?? CompareDensity.Empty;
        if (session is null || (uint)Selected >= (uint)session.Matches.Length) Selected = -1;
        Summarize();
        if (session is null) Window = default;
        else if (!keepWindow || Window.IsEmpty) Window = session.Range;
        else Window = ClampWindow(Window);
        UpdatePinned();
        Relayout(CompareChanges.Rows | CompareChanges.Window | CompareChanges.Selection | CompareChanges.Alignment);
    }

    /// <summary>The file headers' text and colours.</summary>
    public void SetFiles(string nameA, string detailA, ThemeBrush brushA, string nameB, string detailB, ThemeBrush brushB)
    {
        NameA = nameA;
        NameB = nameB;
        DetailA = detailA;
        DetailB = detailB;
        BrushA = brushA;
        BrushB = brushB;
        // The headers' rows carry the names.
        Relayout();
    }

    /// <summary>After the compare was re-aligned: timing differences and the Δ list moved.</summary>
    public void OnAligned()
    {
        Summarize();
        Relayout(CompareChanges.Rows | CompareChanges.Alignment);
    }

    /// <summary>Sets the window, clamped to the compare's range.</summary>
    public void SetWindow(TimeWindow window)
    {
        if (Session is null || window.IsEmpty) return;
        window = ClampWindow(window);
        if (window == Window) return;
        Window = window;
        Changed?.Invoke(CompareChanges.Window);
    }

    /// <summary>Ctrl+Wheel: zooms both files around a compare time.</summary>
    public void ZoomAround(long anchor, double factor) => SetWindow(Window.ZoomAround(anchor, factor));

    /// <summary>Shift+Wheel: moves the window by a span of compare time.</summary>
    public void Pan(long delta) => SetWindow(Window.Pan(delta));

    /// <summary>Moves the playhead.</summary>
    public void SetPlayhead(long time)
    {
        if (Playhead == time) return;
        Playhead = time;
        Changed?.Invoke(CompareChanges.Playhead);
    }

    /// <summary>Selects (pins) an entity; -1 clears.</summary>
    public void Select(int match)
    {
        if (Session is not { } session || (uint)match >= (uint)session.Matches.Length) match = -1;
        if (match == Selected) return;
        Selected = match;
        UpdatePinned();
        Relayout(CompareChanges.Rows | CompareChanges.Selection);
    }

    /// <summary>A click picked an entity: asks the view-model, which selects it here and shows its detail.</summary>
    public void RequestSelect(int match)
    {
        if (SelectRequested is { } handler) handler(match);
        else Select(match);
    }

    /// <summary>A double-click: asks the view-model to open the place in Inspect.</summary>
    public void RequestOpen(CompareSide side, int match, long time) => OpenRequested?.Invoke(new CompareOpenRequest(side, match, time));

    /// <summary>True when a category lists its groups.</summary>
    public bool IsExpanded(CompareCategory category) => _expanded[(int)category];

    /// <summary>Expands or collapses a category in both files.</summary>
    public void SetExpanded(CompareCategory category, bool expanded)
    {
        if (category == CompareCategory.Transport || _expanded[(int)category] == expanded) return;
        _expanded[(int)category] = expanded;
        Relayout();
    }

    /// <summary>The gutter caret.</summary>
    public void ToggleExpanded(CompareCategory category) => SetExpanded(category, !IsExpanded(category));

    /// <summary>True when a group has a row of its own in its expanded category (the sidebar's checkbox).</summary>
    public bool IsGroupShown(int group) => (uint)group < (uint)_groupHidden.Length && !_groupHidden[group];

    /// <summary>Shows or hides a group's row.</summary>
    public void SetGroupShown(int group, bool shown)
    {
        if ((uint)group >= (uint)_groupHidden.Length || _groupHidden[group] == !shown) return;
        _groupHidden[group] = !shown;
        Relayout();
    }

    /// <summary>The groups of a category: a contiguous run of <see cref="CompareSession.Groups"/>.</summary>
    public (int First, int Count) GroupsOf(CompareCategory category) => _categoryGroups[(int)category];

    /// <summary>True when any entity of a category differs.</summary>
    public bool CategoryDiffers(CompareCategory category) => _categoryDelta[(int)category];

    /// <summary>The category of an entity kind.</summary>
    public static CompareCategory CategoryOf(CompareEntityKind kind) => kind switch
    {
        CompareEntityKind.Invocation => CompareCategory.Invocations,
        CompareEntityKind.Pipe => CompareCategory.Pipes,
        CompareEntityKind.Collection => CompareCategory.Collections,
        CompareEntityKind.Control => CompareCategory.Control,
        _ => CompareCategory.Handshake,
    };

    /// <summary>A category's name in the gutter and the sidebar.</summary>
    public static string CategoryLabel(CompareCategory category) => category switch
    {
        // Both directions' transport ops in one heat row.
        CompareCategory.Transport => "Transport ▼▲",
        CompareCategory.Invocations => "Invocations",
        CompareCategory.Pipes => "Pipes · channels",
        CompareCategory.Collections => "Collections",
        CompareCategory.Control => "Control · ping · credit · cancel",
        _ => "Handshake",
    };

    /// <summary>A group's label as one file shows it ("SendMessage ×1,204", "Pipe 3·1 FileUpload").</summary>
    public string GroupLabel(int group, CompareSide side)
    {
        string[] labels = side == CompareSide.A ? _groupLabelA : _groupLabelB;
        return (uint)group < (uint)labels.Length ? labels[group] : "";
    }

    /// <summary>
    /// An entity's title as one file names it: "#812 SendAttachment", "Pipe 3·1 FileUpload", "Collection Users", or the
    /// control or handshake message. The ids are that file's, so the two sides of one entity can differ.
    /// </summary>
    public static string EntityTitle(CompareSession session, int match, CompareSide side)
    {
        ArgumentNullException.ThrowIfNull(session);
        EntityMatch row = session.Matches[match];
        string label = session.GetLabel(match);
        EntitySide entity = row.Get(side);
        if (!entity.IsPresent) entity = row.Get(side == CompareSide.A ? CompareSide.B : CompareSide.A);
        CaptureSession document = session.Get(row.Get(side).IsPresent ? side : side == CompareSide.A ? CompareSide.B : CompareSide.A);
        lock (document.Gate)
        {
            MessageAssembler assembler = document.Assembler;
            switch (row.Key.Kind)
            {
                case CompareEntityKind.Invocation when (uint)entity.Index < (uint)assembler.Invocations.Length:
                    return "#" + assembler.Invocations[entity.Index].InvocationId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + label;
                case CompareEntityKind.Pipe when (uint)entity.Index < (uint)assembler.Pipes.Length:
                    return PipeTitle(document, entity.Index, label);
                case CompareEntityKind.Collection:
                    return PipeText.CollectionGlyph + " Collection " + label;
            }
        }

        return label;
    }

    /// <summary>The time under a fraction of the track, in compare time.</summary>
    public long TimeAt(double x, double width) => Window.FromPixel(x, width);

    // "⇄ Pipe 3·1 FileUpload" / "≋ Channel<LogLine> 36·36 TailLogs", as the timeline's lanes name them: a channel by
    // what it carries and the method that opened it (the label is its item type, already in the title).
    private static string PipeTitle(CaptureSession document, int index, string label)
    {
        PipeRow pipe = document.Assembler.Pipes[index];
        string title = PipeText.Glyph(pipe.Content) + " " + PipeText.Title(document, index);
        if (pipe.Content == PipeContent.Raw) return title + " " + label;
        MessageAssembler assembler = document.Assembler;
        return (uint)pipe.OpenerInvocation < (uint)assembler.Invocations.Length
               && document.Decoder.TryGetMethod(assembler.Invocations[pipe.OpenerInvocation], out SchemaMethod? opener)
            ? title + " " + opener.Name
            : title;
    }

    private TimeWindow ClampWindow(TimeWindow window)
    {
        TimeWindow range = Bounds;
        if (range.IsEmpty) return window;
        // Not narrower than 10 µs, not wider than the whole compare.
        long duration = Math.Clamp(window.Duration, 100, range.Duration);
        window = TimeWindow.FromDuration(window.Start, duration);
        return window.Clamp(range.Start, range.End);
    }

    // Per-session facts the layout reads: category runs, their Δ, counts and labels per side.
    private void Summarize()
    {
        Array.Clear(_categoryGroups);
        Array.Clear(_categoryDelta);
        if (Session is not { } session)
        {
            _groupLabelA = _groupLabelB = _groupFramesA = _groupFramesB = [];
            _groupHidden = [];
            return;
        }

        ReadOnlySpan<EntityGroupRow> groups = session.Groups;
        ReadOnlySpan<EntityMatch> matches = session.Matches;
        if (_groupHidden.Length != groups.Length)
        {
            // A new union starts with its invocation groups unticked (screens/compare): calls are many and short, so
            // they read best as the category's heat row and the pinned entity; pipes and the rest get rows.
            _groupHidden = new bool[groups.Length];
            for (int g = 0; g < groups.Length; g++) _groupHidden[g] = groups[g].Key.Kind == CompareEntityKind.Invocation;
        }
        if (_groupLabelA.Length != groups.Length)
        {
            _groupLabelA = new string[groups.Length];
            _groupLabelB = new string[groups.Length];
            _groupFramesA = new string[groups.Length];
            _groupFramesB = new string[groups.Length];
        }

        long[] entitiesA = new long[Categories], entitiesB = new long[Categories], framesA = new long[Categories], framesB = new long[Categories];
        for (int g = 0; g < groups.Length; g++)
        {
            EntityGroupRow group = groups[g];
            CompareCategory category = CategoryOf(group.Key.Kind);
            ref (int First, int Count) run = ref _categoryGroups[(int)category];
            if (run.Count == 0) run.First = g;
            run.Count++;
            if (group.Differing > 0) _categoryDelta[(int)category] = true;

            long gfA = 0, gfB = 0;
            int presentA = 0, presentB = 0;
            foreach (ref readonly EntityMatch match in matches.Slice(group.FirstMatch, group.Count))
            {
                gfA += match.A.Frames;
                gfB += match.B.Frames;
                if (match.A.IsPresent) presentA++;
                if (match.B.IsPresent) presentB++;
            }

            entitiesA[(int)category] += presentA;
            entitiesB[(int)category] += presentB;
            framesA[(int)category] += gfA;
            framesB[(int)category] += gfB;
            _groupFramesA[g] = CompareFormat.Compact(gfA);
            _groupFramesB[g] = CompareFormat.Compact(gfB);
            _groupLabelA[g] = GroupTitle(session, group, CompareSide.A, presentA);
            _groupLabelB[g] = GroupTitle(session, group, CompareSide.B, presentB);
        }

        for (int c = 0; c < Categories; c++)
        {
            // Control frames belong to no entity: their count is frames, as the sidebar shows it.
            bool frames = (CompareCategory)c == CompareCategory.Control;
            _categoryCountA[c] = frames ? CompareFormat.Compact(framesA[c]) : CompareFormat.Count(entitiesA[c]);
            _categoryCountB[c] = frames ? CompareFormat.Compact(framesB[c]) : CompareFormat.Count(entitiesB[c]);
        }

        _categoryCountA[(int)CompareCategory.Transport] = CompareFormat.Compact(DensityA.Total(CompareCategory.Transport));
        _categoryCountB[(int)CompareCategory.Transport] = CompareFormat.Compact(DensityB.Total(CompareCategory.Transport));
    }

    // One entity: its title; several: the shared label and how many this file holds.
    private static string GroupTitle(CompareSession session, in EntityGroupRow group, CompareSide side, int present)
    {
        if (group.Count == 1) return EntityTitle(session, group.FirstMatch, side);
        return session.GetLabel(group.FirstMatch) + " ×" + CompareFormat.Count(present);
    }

    private void UpdatePinned()
    {
        if (Session is not { } session || Selected < 0)
        {
            _pinnedLabelA = _pinnedLabelB = _pinnedValueA = _pinnedValueB = "";
            return;
        }

        EntityMatch match = session.Matches[Selected];
        string prefix = match.Key.Kind == CompareEntityKind.Invocation ? "Inv " : "";
        _pinnedLabelA = prefix + EntityTitle(session, Selected, CompareSide.A);
        _pinnedLabelB = prefix + EntityTitle(session, Selected, CompareSide.B);
        _pinnedValueA = match.A.IsPresent ? CompareFormat.Duration(Milliseconds(match.A.DurationTicks, session.A.Header.StopwatchFrequency)) : "absent";
        _pinnedValueB = match.B.IsPresent ? CompareFormat.Duration(Milliseconds(match.B.DurationTicks, session.B.Header.StopwatchFrequency)) : "absent";
    }

    private static double Milliseconds(long ticks, long frequency) => ticks * 1000.0 / Math.Max(1, frequency);

    private void Relayout(CompareChanges changes = CompareChanges.Rows)
    {
        _rows.Clear();
        double top = 0;
        if (Session is { } session)
        {
            ReadOnlySpan<EntityGroupRow> groups = session.Groups;
            CompareCategory pinned = Selected >= 0 ? CategoryOf(session.Matches[Selected].Key.Kind) : CompareCategory.Transport;
            for (int s = 0; s < 2; s++)
            {
                CompareSide side = (CompareSide)s;
                string[] counts = side == CompareSide.A ? _categoryCountA : _categoryCountB;
                Add(ref top, CompareRowKind.FileHeader, side, CompareCategory.Transport, -1, HeaderHeight, false,
                    side == CompareSide.A ? NameA : NameB, "");
                // Transport is the session at a glance: a heat row in either mode, never filtered away.
                Add(ref top, CompareRowKind.Collapsed, side, CompareCategory.Transport, -1, HeatHeight, false,
                    CategoryLabel(CompareCategory.Transport), counts[(int)CompareCategory.Transport]);

                foreach (CompareCategory category in EntityCategories)
                {
                    (int first, int count) = _categoryGroups[(int)category];
                    if (count == 0) continue;
                    bool delta = _categoryDelta[(int)category];
                    bool holdsPinned = pinned == category && Selected >= 0;
                    if (_onlyDifferences && !delta && !holdsPinned) continue;

                    bool expanded = _expanded[(int)category];
                    double height = expanded || _rowMode == CompareRowMode.Packed ? PackedHeight : HeatHeight;
                    Add(ref top, expanded ? CompareRowKind.Expanded : CompareRowKind.Collapsed, side, category, -1, height, delta,
                        CategoryLabel(category), counts[(int)category]);
                    if (holdsPinned)
                    {
                        EntityMatch match = session.Matches[Selected];
                        Add(ref top, CompareRowKind.Pinned, side, category, -1, PackedHeight, match.HasDifferences,
                            side == CompareSide.A ? _pinnedLabelA : _pinnedLabelB, side == CompareSide.A ? _pinnedValueA : _pinnedValueB);
                    }

                    if (!expanded) continue;
                    for (int g = first; g < first + count; g++)
                    {
                        if (_groupHidden[g] || (_onlyDifferences && groups[g].Differing == 0)) continue;
                        Add(ref top, CompareRowKind.Group, side, category, g, PackedHeight, groups[g].Differing > 0,
                            side == CompareSide.A ? _groupLabelA[g] : _groupLabelB[g], side == CompareSide.A ? _groupFramesA[g] : _groupFramesB[g]);
                    }
                }
            }
        }

        Height = top;
        Changed?.Invoke(changes);
    }

    private void Add(ref double top, CompareRowKind kind, CompareSide side, CompareCategory category, int group, double height, bool delta,
        string label, string count)
    {
        _rows.Add(new CompareRow(kind, side, category, group, top, height, delta, label, count));
        top += height;
    }

    /// <summary>The row at a content y; -1 for none.</summary>
    public int RowAt(double y)
    {
        ReadOnlySpan<CompareRow> rows = Rows;
        int low = 0, high = rows.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) >>> 1;
            if (y < rows[mid].Top) high = mid - 1;
            else if (y >= rows[mid].Top + rows[mid].Height) low = mid + 1;
            else return mid;
        }

        return -1;
    }
}
