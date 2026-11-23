using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.App.Controls.PropertyTree;

/// <summary>
/// The property tree's data: sections and fields in flat pre-order (<see cref="PropertyNode"/>), the decoded values
/// they render from, and the visible rows after expand / collapse (<see cref="Rows"/>, what the virtualized list shows).
/// Row objects are made only for rows that become visible, and a decoded value's text only when its row is read, so a
/// tree of thousands of fields costs what the realized rows cost.
/// </summary>
/// <remarks>Build it with <see cref="Section"/>, <see cref="Field"/> and <see cref="Decoded"/>, then <see cref="Complete"/>. UI thread.</remarks>
public sealed class PropertyTreeModel
{
    /// <summary>Indent of each nesting level (property-tree: 12 px per level).</summary>
    public const double IndentStep = 12;

    private readonly List<PropertyNode> _nodes = [];
    private readonly List<DecodedValue> _values = [];
    private readonly List<int> _visible = [];
    private PropertyRow?[] _rows = [];
    private int _sectionDepth = -1;
    private bool _complete;

    public PropertyTreeModel() => Rows = new PropertyRowList(this);

    /// <summary>The visible rows, in order; raises collection changes as sections and fields expand or collapse.</summary>
    public PropertyRowList Rows { get; }

    /// <summary>Every node.</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>Visible rows.</summary>
    public int VisibleCount => _visible.Count;

    /// <summary>How many decoded values have been rendered to text so far (only rows that were read are).</summary>
    public int FormattedCount { get; private set; }

    /// <summary>Row objects made so far (only for rows that became visible and were read).</summary>
    public int RowsMade { get; private set; }

    /// <summary>A node by index.</summary>
    public ref readonly PropertyNode Node(int index) => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes)[index];

    /// <summary>Adds a section (expanded); following fields belong to it until the next section.</summary>
    /// <param name="title">"Frame header", "Transport", "Invocation #812", ….</param>
    /// <param name="summary">The secondary text beside the title.</param>
    /// <returns>The node index.</returns>
    public int Section(string title, string? summary)
    {
        ThrowIfComplete();
        _sectionDepth = 0;
        _nodes.Add(new PropertyNode
        {
            Kind = PropertyRowKind.Section, Depth = 0, Name = title, Value = summary ?? "", Decoded = -1, FieldIndex = -1,
            Tone = PropertyTone.Normal, Bytes = PropertyBytes.None, Expanded = true,
        });
        return _nodes.Count - 1;
    }

    /// <summary>Adds a fixed field to the current section.</summary>
    /// <param name="name">The name column.</param>
    /// <param name="value">The value column.</param>
    /// <param name="tone">The value's colour.</param>
    /// <param name="bytes">The field's bytes, for hover and click linking.</param>
    /// <param name="depth">Extra nesting below the section's fields.</param>
    /// <returns>The node index.</returns>
    public int Field(string name, string value, PropertyTone tone = PropertyTone.Normal, PropertyBytes bytes = default, int depth = 0)
    {
        ThrowIfComplete();
        _nodes.Add(new PropertyNode
        {
            Kind = PropertyRowKind.Field, Depth = _sectionDepth + 1 + depth, Name = name, Value = value, Decoded = -1,
            FieldIndex = -1, Tone = tone, Bytes = bytes.Length > 0 ? bytes : PropertyBytes.None, Expanded = false,
        });
        return _nodes.Count - 1;
    }

    /// <summary>
    /// Adds the fields of a decoded value to the current section, nested by their depth. An arguments list is
    /// flattened (its parameters are the section's fields), and a raw value (no schema, unknown method) is one field
    /// named <paramref name="rawName"/> showing its size and leading bytes.
    /// </summary>
    /// <param name="value">The decoded value.</param>
    /// <param name="map">Maps its payload coordinates to the stream.</param>
    /// <param name="rawName">The field name of a raw value.</param>
    /// <param name="depth">Extra nesting below the section's fields.</param>
    public void Decoded(DecodedValue value, in PayloadMap map, string rawName, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(value);
        ThrowIfComplete();
        ReadOnlySpan<DecodedField> fields = value.Fields;
        if (fields.IsEmpty) return;

        int source = _values.Count;
        _values.Add(value);
        long origin = value.Range.IsNone ? 0 : value.Range.Offset;
        int lift = fields[0].Kind == FieldKind.Arguments && fields[0].Parent < 0 ? 1 : 0;
        int firstNode = _nodes.Count;
        int topLevel = 0;
        for (int i = lift; i < fields.Length; i++)
        {
            ref readonly DecodedField field = ref fields[i];
            if (field.Depth - lift <= 0) topLevel++;
            string name = value.IsRaw ? rawName : field.Name ?? (field.ElementIndex >= 0 ? "[" + field.ElementIndex.ToString(CultureInfo.InvariantCulture) + "]" : "value");
            _nodes.Add(new PropertyNode
            {
                Kind = PropertyRowKind.Field,
                Depth = _sectionDepth + 1 + depth + Math.Max(0, field.Depth - lift),
                Name = name,
                Value = null,
                Decoded = source,
                FieldIndex = i,
                Tone = value.IsRaw ? PropertyTone.Muted : PropertyTone.Normal,
                Bytes = map.Map(origin + field.Offset, field.Length),
                // Nested values open on demand: the first level is what the screens show.
                Expanded = false,
            });
        }

        // A single object argument opens one level, so its members show (inspect-live: attachment's roomId, fileName,
        // data rows) instead of one closed row.
        var nodes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        if (topLevel == 1 && nodes.Length > firstNode + 1 && nodes[firstNode + 1].Depth > nodes[firstNode].Depth)
            nodes[firstNode].Expanded = true;
    }

    /// <summary>Ends the build: computes subtrees and the visible rows.</summary>
    public void Complete()
    {
        ThrowIfComplete();
        _complete = true;
        var nodes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        // Pre-order: a node's subtree ends at the next node that is not deeper.
        Span<int> open = nodes.Length <= 256 ? stackalloc int[nodes.Length] : new int[nodes.Length];
        int top = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            while (top > 0 && nodes[open[top - 1]].Depth >= nodes[i].Depth) nodes[open[--top]].SubtreeEnd = i;
            open[top++] = i;
        }

        while (top > 0) nodes[open[--top]].SubtreeEnd = nodes.Length;
        _rows = new PropertyRow?[nodes.Length];
        Flatten(0, nodes.Length, _visible);
    }

    /// <summary>The visible row at a position, made on first use.</summary>
    public PropertyRow RowAt(int position) => RowOf(_visible[position]);

    /// <summary>The row of a node (made on first use; it may not be visible).</summary>
    public PropertyRow RowOf(int node)
    {
        if (_rows[node] is { } row) return row;
        RowsMade++;
        return _rows[node] = new PropertyRow(this, node);
    }

    /// <summary>The visible position of a node; -1 when a collapsed ancestor hides it.</summary>
    public int PositionOf(int node) => _visible.BinarySearch(node) is var at and >= 0 ? at : -1;

    /// <summary>Expands or collapses a node with children.</summary>
    public void Toggle(int node) => SetExpanded(node, !Node(node).Expanded);

    /// <summary>Expands or collapses a node with children; the visible rows change by one range.</summary>
    public void SetExpanded(int node, bool expanded)
    {
        var nodes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        ref PropertyNode target = ref nodes[node];
        if (!target.HasChildren(node) || target.Expanded == expanded) return;
        target.Expanded = expanded;
        _rows[node]?.OnExpandedChanged();

        int at = PositionOf(node);
        if (at < 0) return; // hidden: the state applies when an ancestor opens

        if (expanded)
        {
            List<int> shown = [];
            Flatten(node + 1, target.SubtreeEnd, shown);
            _visible.InsertRange(at + 1, shown);
            Rows.RaiseRange(NotifyCollectionChangedAction.Add, at + 1, shown);
        }
        else
        {
            int count = 0;
            while (at + 1 + count < _visible.Count && _visible[at + 1 + count] < target.SubtreeEnd) count++;
            List<int> hidden = _visible.GetRange(at + 1, count);
            _visible.RemoveRange(at + 1, count);
            Rows.RaiseRange(NotifyCollectionChangedAction.Remove, at + 1, hidden);
        }
    }

    /// <summary>
    /// The deepest field whose stream bytes hold <paramref name="offset"/> of <paramref name="direction"/>: how a hex
    /// selection finds its row. Its ancestors are expanded so it shows.
    /// </summary>
    /// <returns>The node, or -1 when no field covers the byte.</returns>
    public int Reveal(TapDirection direction, long offset)
    {
        int found = -1;
        int foundDepth = -1;
        long foundLength = long.MaxValue;
        var nodes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        for (int i = 0; i < nodes.Length; i++)
        {
            ref readonly PropertyNode n = ref nodes[i];
            if (n.Kind != PropertyRowKind.Field || n.Bytes.IsNone || n.Bytes.Direction != direction) continue;
            StreamRange s = n.Bytes.Stream;
            if (offset < s.Offset || offset >= s.End) continue;
            if (n.Depth > foundDepth || (n.Depth == foundDepth && s.Length < foundLength))
            {
                found = i;
                foundDepth = n.Depth;
                foundLength = s.Length;
            }
        }

        if (found >= 0) ExpandAncestors(found);
        return found;
    }

    /// <summary>
    /// The visible row whose bytes a hovered field names (the hex view's hover, in the same message coordinates the
    /// rows publish): the row with exactly those bytes, else the deepest visible row that holds the field's first byte.
    /// Collapsed rows are not opened: a hover only lights what shows.
    /// </summary>
    /// <returns>The node, or -1 when no visible row holds the bytes.</returns>
    public int FindVisible(in MessageRef message, long offset, int length)
    {
        int found = -1;
        int foundDepth = -1;
        long foundLength = long.MaxValue;
        foreach (int i in _visible)
        {
            ref readonly PropertyNode n = ref Node(i);
            if (n.Kind != PropertyRowKind.Field || n.Bytes.IsNone || n.Bytes.Message != message) continue;
            if (n.Bytes.Offset == offset && n.Bytes.Length == length) return i;
            if (offset < n.Bytes.Offset || offset >= n.Bytes.Offset + n.Bytes.Length) continue;
            if (n.Depth > foundDepth || (n.Depth == foundDepth && n.Bytes.Length < foundLength))
            {
                found = i;
                foundDepth = n.Depth;
                foundLength = n.Bytes.Length;
            }
        }

        return found;
    }

    /// <summary>Opens every collapsed ancestor of a node.</summary>
    public void ExpandAncestors(int node)
    {
        // Outermost first, so each opening inserts the next ancestor's row.
        List<int> ancestors = [];
        int depth = Node(node).Depth;
        for (int i = node - 1; i >= 0 && depth > 0; i--)
        {
            if (Node(i).Depth < depth)
            {
                ancestors.Add(i);
                depth = Node(i).Depth;
            }
        }

        for (int i = ancestors.Count - 1; i >= 0; i--) SetExpanded(ancestors[i], true);
    }

    /// <summary>The value text of a node, rendering a decoded field on first use.</summary>
    internal string ValueOf(int node)
    {
        ref readonly PropertyNode n = ref Node(node);
        if (n.Value is { } fixedValue) return fixedValue;
        FormattedCount++;
        DecodedValue value = _values[n.Decoded];
        return Format(value, value.Fields[n.FieldIndex]);
    }

    /// <summary>The decoded value a node renders from, or null for a fixed node.</summary>
    public DecodedValue? DecodedOf(int node) => Node(node).Decoded is var d and >= 0 ? _values[d] : null;

    /// <summary>A decoded field's display text: strings quoted, binary as its length, raw as its size and first bytes.</summary>
    public static string Format(DecodedValue value, in DecodedField field)
    {
        ArgumentNullException.ThrowIfNull(value);
        CultureInfo invariant = CultureInfo.InvariantCulture;
        switch (field.Kind)
        {
            case FieldKind.String:
                return "\"" + value.GetText(field) + "\"";
            case FieldKind.Binary:
                return "byte[" + field.ContentLength.ToString("N0", invariant) + "]";
            case FieldKind.Raw:
            {
                ReadOnlySpan<byte> bytes = value.GetBytes(field);
                const int preview = 12;
                string hex = Convert.ToHexString(bytes[..Math.Min(bytes.Length, preview)]);
                return field.Length.ToString("N0", invariant) + " B · " + hex + (bytes.Length > preview ? "…" : "");
            }
            default:
                return value.FormatValue(field);
        }
    }

    private void Flatten(int from, int to, List<int> into)
    {
        var nodes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        int i = from;
        while (i < to)
        {
            into.Add(i);
            i = nodes[i].Expanded ? i + 1 : nodes[i].SubtreeEnd;
        }
    }

    private void ThrowIfComplete()
    {
        if (_complete) throw new InvalidOperationException("the tree is complete");
    }

    internal IReadOnlyList<int> Visible => _visible;
}

/// <summary>
/// The visible rows of a <see cref="PropertyTreeModel"/> as the list a virtualized ListBox reads: an indexable list
/// (rows are made when read, so only realized ones exist) that reports expand / collapse as one range change.
/// </summary>
public sealed class PropertyRowList : IReadOnlyList<PropertyRow>, IList, INotifyCollectionChanged
{
    private readonly PropertyTreeModel _model;

    internal PropertyRowList(PropertyTreeModel model) => _model = model;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _model.VisibleCount;

    public PropertyRow this[int index] => _model.RowAt(index);

    object? IList.this[int index]
    {
        get => _model.RowAt(index);
        set => throw new NotSupportedException();
    }

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public IEnumerator<PropertyRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(PropertyRow row) => row is not null && ReferenceEquals(row.Model, _model) ? _model.PositionOf(row.Node) : -1;

    int IList.IndexOf(object? value) => value is PropertyRow row ? IndexOf(row) : -1;

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }

    internal void RaiseRange(NotifyCollectionChangedAction action, int at, List<int> nodes)
    {
        if (CollectionChanged is not { } handler || nodes.Count == 0) return;
        List<PropertyRow> rows = new(nodes.Count);
        foreach (int node in nodes) rows.Add(_model.RowOf(node));
        handler(this, new NotifyCollectionChangedEventArgs(action, rows, at));
    }
}

/// <summary>
/// One row of the property tree as the list shows it: the node's name, its value (rendered when first read), its
/// indent and expand glyph. Made only for rows that become visible.
/// </summary>
public sealed class PropertyRow : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs ExpandedArgs = new(nameof(IsExpanded));
    private static readonly PropertyChangedEventArgs GlyphArgs = new(nameof(Glyph));
    private string? _value;

    internal PropertyRow(PropertyTreeModel model, int node)
    {
        Model = model;
        Node = node;
        ref readonly PropertyNode n = ref model.Node(node);
        Kind = n.Kind;
        Name = n.Name;
        Tone = n.Tone;
        Bytes = n.Bytes;
        HasChildren = n.HasChildren(node);
        // Sections sit at the left edge; fields start one step in, children a step further per level.
        Indent = n.Kind == PropertyRowKind.Section ? 0 : Math.Max(0, n.Depth - 1) * PropertyTreeModel.IndentStep;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The tree it belongs to.</summary>
    public PropertyTreeModel Model { get; }

    /// <summary>Its node index.</summary>
    public int Node { get; }

    /// <summary>Section or field.</summary>
    public PropertyRowKind Kind { get; }

    /// <summary>True for a section row.</summary>
    public bool IsSection => Kind == PropertyRowKind.Section;

    /// <summary>The name column, or the section title.</summary>
    public string Name { get; }

    /// <summary>The value column, or the section summary.</summary>
    public string Value => _value ??= Model.ValueOf(Node);

    /// <summary>The value's colour.</summary>
    public PropertyTone Tone { get; }

    /// <summary>True for a transport range.</summary>
    public bool IsTransport => Tone == PropertyTone.Transport;

    /// <summary>True for a NexNet split or redacted value.</summary>
    public bool IsNexNet => Tone == PropertyTone.NexNet;

    /// <summary>True for a muted (raw, missing) value.</summary>
    public bool IsMuted => Tone == PropertyTone.Muted;

    /// <summary>The row's bytes.</summary>
    public PropertyBytes Bytes { get; }

    /// <summary>The name column's left indent.</summary>
    public double Indent { get; }

    /// <summary>The name column's indent as a margin.</summary>
    public Avalonia.Thickness IndentMargin => new(Indent, 0, 0, 0);

    /// <summary>True when the row has children to expand.</summary>
    public bool HasChildren { get; }

    /// <summary>True while the children show.</summary>
    public bool IsExpanded => Model.Node(Node).Expanded;

    /// <summary>▾ open, ▸ closed, nothing without children.</summary>
    public string Glyph => !HasChildren ? "" : IsExpanded ? "▾" : "▸";

    internal void OnExpandedChanged()
    {
        PropertyChanged?.Invoke(this, ExpandedArgs);
        PropertyChanged?.Invoke(this, GlyphArgs);
    }
}
