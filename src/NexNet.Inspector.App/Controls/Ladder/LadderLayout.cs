namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>
/// The ladder's vertical layout: where each row starts and how tall it is. Compact rows are 18 px each
/// (ladder-row.height). Time-scaled rows (OQ6) add a gap above each row that grows with the log of the time since the row
/// before: nothing up to <see cref="GapFloorMs"/>, then <see cref="PixelsPerDecade"/> per tenfold, capped at
/// <see cref="MaxGap"/> — a burst stays dense, a pause shows, and an idle minute costs no more than a tenth of a second.
/// </summary>
/// <remarks>
/// Incremental: appended rows extend the offsets from where the last update stopped, so a live capture pays for what
/// arrived. Compact mode keeps no offsets at all (row <c>i</c> starts at <c>18 i</c>).
/// </remarks>
public sealed class LadderLayout
{
    /// <summary>A row's own height, without its gap.</summary>
    public const double RowHeight = 18;

    /// <summary>The smallest time-scaled gap.</summary>
    public const double MinGap = 0;

    /// <summary>The largest time-scaled gap (OQ6: 0–48 px).</summary>
    public const double MaxGap = 48;

    /// <summary>The time between rows below which no gap is drawn: 10 µs.</summary>
    public const double GapFloorMs = 0.01;

    /// <summary>Pixels added per tenfold of time above the floor: 100 µs = 12, 1 ms = 24, 10 ms = 36, 100 ms or more = 48.</summary>
    public const double PixelsPerDecade = 12;

    private double[] _tops = new double[1];
    private int _count;
    private LadderModel? _model;
    private int _modelVersion = -1;
    private int _generation = -1;

    /// <summary>The mode the last update laid out.</summary>
    public LadderMode Mode { get; private set; }

    /// <summary>Rows laid out.</summary>
    public int Count => _count;

    /// <summary>The height of every row together.</summary>
    public double TotalHeight => Mode == LadderMode.Compact ? _count * RowHeight : _tops[_count];

    /// <summary>The time-scaled gap for <paramref name="deltaTicks"/> between two rows, in whole pixels.</summary>
    public static double GapFor(long deltaTicks, long frequency)
    {
        if (deltaTicks <= 0 || frequency <= 0) return MinGap;
        double ms = deltaTicks * 1000.0 / frequency;
        if (ms <= GapFloorMs) return MinGap;
        return Math.Clamp(Math.Round(PixelsPerDecade * Math.Log10(ms / GapFloorMs)), MinGap, MaxGap);
    }

    /// <summary>Lays out <paramref name="model"/>'s rows in <paramref name="mode"/>; only new rows unless the mode or model changed.</summary>
    /// <returns>True when anything moved or was added.</returns>
    public bool Update(LadderModel? model, LadderMode mode)
    {
        int count = model?.Count ?? 0;
        bool restart = !ReferenceEquals(model, _model) || mode != Mode || count < _count || (model?.Generation ?? -1) != _generation;
        if (!restart && count == _count && (model is null || model.Version == _modelVersion)) return false;

        _model = model;
        _modelVersion = model?.Version ?? -1;
        _generation = model?.Generation ?? -1;
        int from = restart ? 0 : _count;
        Mode = mode;
        if (mode == LadderMode.TimeScaled && model is not null)
        {
            if (_tops.Length < count + 1) Array.Resize(ref _tops, Math.Max(count + 1, _tops.Length * 2));
            ReadOnlySpan<LadderRow> rows = model.Rows;
            if (from == 0) _tops[0] = 0;
            for (int i = from; i < count; i++)
            {
                double gap = i == 0 ? 0 : GapFor(rows[i].Ticks - rows[i - 1].Ticks, model.Frequency);
                _tops[i + 1] = _tops[i] + gap + RowHeight;
            }
        }

        _count = count;
        return true;
    }

    /// <summary>The gap above row <paramref name="index"/> (inside its height).</summary>
    public double Gap(int index) => Mode == LadderMode.Compact ? 0 : _tops[index + 1] - _tops[index] - RowHeight;

    /// <summary>The top of row <paramref name="index"/>, its gap included; <see cref="TotalHeight"/> for <see cref="Count"/>.</summary>
    public double Top(int index) => Mode == LadderMode.Compact ? index * RowHeight : _tops[index];

    /// <summary>The height of row <paramref name="index"/>: 18 px plus its gap.</summary>
    public double Height(int index) => Mode == LadderMode.Compact ? RowHeight : _tops[index + 1] - _tops[index];

    /// <summary>The row at <paramref name="y"/>, clamped to the rows; -1 when there are none.</summary>
    public int IndexAt(double y)
    {
        if (_count == 0) return -1;
        if (y <= 0) return 0;
        if (Mode == LadderMode.Compact) return Math.Min(_count - 1, (int)(y / RowHeight));

        int lo = 0, hi = _count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_tops[mid] <= y) lo = mid;
            else hi = mid - 1;
        }

        return lo;
    }
}
