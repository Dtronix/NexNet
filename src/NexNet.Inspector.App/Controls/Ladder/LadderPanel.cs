using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>
/// The ladder's virtualizing panel, the content of its scroll viewer: as tall as every row together, with a
/// <see cref="LadderRowControl"/> only for the rows in its effective viewport. Rows that scroll out return to a pool
/// (hidden, still children) and are bound to the rows that scroll in, so scrolling creates controls only until the pool
/// covers a screenful.
/// </summary>
internal sealed class LadderPanel : Panel
{
    // Rows realized above and below the viewport, so a wheel notch shows rows that are already shaped.
    private const int Overscan = 4;

    private readonly LadderView _owner;
    private readonly List<LadderRowControl> _active = [];
    private readonly Stack<LadderRowControl> _pool = new();
    private Rect _viewport;
    private int _first;

    public LadderPanel(LadderView owner)
    {
        _owner = owner;
        EffectiveViewportChanged += OnViewportChanged;
    }

    /// <summary>The realized rows, in row order from <see cref="First"/>.</summary>
    public IReadOnlyList<LadderRowControl> Active => _active;

    /// <summary>The first realized row's index.</summary>
    public int First => _first;

    /// <summary>Row controls ever created (realized and pooled).</summary>
    public int Created => Children.Count;

    /// <summary>The viewport last reported, in panel coordinates.</summary>
    public Rect Viewport => _viewport;

    /// <summary>Binds every realized row again (data, selection, hover or trace changed); each redraws only if it differs.</summary>
    public void Rebind()
    {
        for (int i = 0; i < _active.Count; i++) _owner.BindRow(_active[i], _first + i);
    }

    /// <summary>The realized control of row <paramref name="index"/>; null when it is not in view.</summary>
    public LadderRowControl? Realized(int index)
    {
        int at = index - _first;
        return (uint)at < (uint)_active.Count ? _active[at] : null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        LadderLayout layout = _owner.Layout;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        Realize(layout);
        for (int i = 0; i < _active.Count; i++)
            _active[i].Measure(new Size(width, layout.Height(_first + i)));
        return new Size(width, layout.TotalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LadderLayout layout = _owner.Layout;
        for (int i = 0; i < _active.Count; i++)
        {
            int index = _first + i;
            _active[i].Arrange(new Rect(0, layout.Top(index), finalSize.Width, layout.Height(index)));
        }

        return finalSize;
    }

    private void Realize(LadderLayout layout)
    {
        int count = layout.Count;
        if (count == 0)
        {
            Recycle(0, _active.Count);
            _first = 0;
            return;
        }

        // Before the first viewport report, fill what the scroll viewer will most likely show.
        Rect viewport = _viewport.Height > 0 ? _viewport : new Rect(0, 0, 0, Math.Max(_owner.Bounds.Height, 600));
        int first = Math.Max(0, layout.IndexAt(viewport.Top) - Overscan);
        int last = Math.Min(count - 1, layout.IndexAt(viewport.Bottom) + Overscan);
        int end = _first + _active.Count;

        if (_active.Count == 0 || last < _first || first >= end)
        {
            Recycle(0, _active.Count);
            _first = first;
        }
        else
        {
            // Drop what left the range from either end, keep the overlap bound as it is.
            if (end - 1 > last) Recycle(last + 1 - _first, _active.Count);
            if (first > _first)
            {
                Recycle(0, first - _first);
                _first = first;
            }
        }

        for (int index = _first - 1; index >= first; index--)
        {
            _active.Insert(0, Take());
            _first = index;
            _owner.BindRow(_active[0], index);
        }

        for (int index = _first + _active.Count; index <= last; index++)
        {
            LadderRowControl row = Take();
            _active.Add(row);
            _owner.BindRow(row, index);
        }
    }

    private void Recycle(int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            _active[i].Unbind();
            _pool.Push(_active[i]);
        }

        _active.RemoveRange(from, to - from);
    }

    private LadderRowControl Take()
    {
        if (_pool.TryPop(out LadderRowControl? row))
        {
            row.IsVisible = true;
            return row;
        }

        row = new LadderRowControl();
        Children.Add(row);
        return row;
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        Rect before = _viewport;
        _viewport = e.EffectiveViewport;
        if (_viewport == before) return;

        // Only a viewport that reaches past the realized rows needs another measure.
        LadderLayout layout = _owner.Layout;
        int count = layout.Count;
        if (count == 0) return;
        int first = layout.IndexAt(_viewport.Top);
        int last = layout.IndexAt(_viewport.Bottom);
        if (_active.Count == 0 || first < _first || last >= _first + _active.Count || _viewport.Height != before.Height)
            InvalidateMeasure();
    }
}
