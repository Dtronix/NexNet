using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.DataTable;

/// <summary>
/// The rows of a <see cref="DataTableControl"/>, as the <see cref="IList"/> its virtualizing panel indexes. The list
/// holds no row objects: every item is one shared placeholder, and a realized row container asks the source to
/// <see cref="Prepare"/> its <see cref="DataTableRowVisual"/> for its index. So a million-row session costs the panel
/// nothing per row, and scrolling allocates nothing beyond what the visible containers draw.
/// </summary>
/// <remarks>
/// Rows are addressed by index only (selection, scrolling into view); <see cref="IList.IndexOf"/> cannot find a row by
/// item, so bind the table's <c>SelectedIndex</c>, never its <c>SelectedItem</c>. UI thread only.
/// </remarks>
public abstract class DataTableSource : IList, INotifyCollectionChanged
{
    // The one item every index returns: the container is prepared from its index, never from the item.
    private static readonly object Placeholder = new();

    /// <summary>Raised when rows are added at the end, or all of them replaced.</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Raised when rows already shown changed (flags arrived, the trace moved): realized rows prepare again.</summary>
    public event Action? RowsChanged;

    /// <summary>The number of rows.</summary>
    public abstract int Count { get; }

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => (uint)index < (uint)Count ? Placeholder : throw new ArgumentOutOfRangeException(nameof(index));
        set => throw new NotSupportedException();
    }

    /// <summary>A visual for one row container; the container keeps it for its life and re-prepares it per index.</summary>
    public abstract DataTableRowVisual CreateVisual();

    /// <summary>Fills <paramref name="visual"/> with the row at <paramref name="index"/>.</summary>
    public abstract void Prepare(DataTableRowVisual visual, int index);

    /// <summary>Tells the table that <paramref name="count"/> rows were appended from <paramref name="start"/>.</summary>
    protected void NotifyAdded(int start, int count)
    {
        if (count <= 0) return;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new Placeholders(count), start));
    }

    /// <summary>Tells the table that every row was replaced (another session).</summary>
    protected void NotifyReset() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    /// <summary>Tells the table that rows it shows changed.</summary>
    protected void NotifyRowsChanged() => RowsChanged?.Invoke();

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    bool IList.Contains(object? value) => Count > 0 && ReferenceEquals(value, Placeholder);

    int IList.IndexOf(object? value) => -1;

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < Count; i++) array.SetValue(Placeholder, index + i);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return Placeholder;
    }

    // The NewItems of an Add: only its count is read.
    private sealed class Placeholders(int count) : IList
    {
        public object? this[int index]
        {
            get => Placeholder;
            set => throw new NotSupportedException();
        }

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public int Count => count;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value) => ReferenceEquals(value, Placeholder);

        public int IndexOf(object? value) => ReferenceEquals(value, Placeholder) && count > 0 ? 0 : -1;

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);
            for (int i = 0; i < count; i++) array.SetValue(Placeholder, index + i);
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < count; i++) yield return Placeholder;
        }
    }
}

/// <summary>
/// What one realized row draws over its state background (hover, selection, trace tint), per column slot. A variant
/// keeps its shaped text here so a row that is drawn again (hover, selection) shapes nothing.
/// </summary>
public abstract class DataTableRowVisual
{
    /// <summary>The row's index in its source; -1 before the first prepare.</summary>
    public int Index { get; protected set; } = -1;

    /// <summary>True when the row belongs to the traced message (the trace tint).</summary>
    public bool IsTraced { get; protected set; }

    /// <summary>Draws the cells; <paramref name="size"/> excludes the row's bottom border.</summary>
    public abstract void Render(DrawingContext context, ThemePalette palette, ReadOnlySpan<DataTableColumnSlot> columns, Size size);

    /// <summary>The tooltip over <paramref name="column"/> (a cell cut by an ellipsis, a flag), or null.</summary>
    public virtual string? ToolTipAt(int column) => null;

    /// <summary>What assistive technology announces for the row.</summary>
    public virtual string? AccessibleName => null;
}
