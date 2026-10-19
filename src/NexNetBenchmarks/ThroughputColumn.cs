using System.Globalization;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace NexNetBenchmarks;

/// <summary>
/// MB/s column: the benchmark's <c>SizeMiB</c> parameter divided by its mean time. Shown for benchmarks in the
/// <see cref="Category"/> category only.
/// </summary>
public class ThroughputColumn : IColumn
{
    public const string Category = "Throughput";

    public string Id => nameof(ThroughputColumn);
    public string ColumnName => "MB/s";
    public bool AlwaysShow => true;
    ColumnCategory IColumn.Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Megabytes (10^6) per second, from the SizeMiB parameter and the mean";

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public bool IsAvailable(Summary summary) => true;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var size = benchmarkCase.Parameters.Items.FirstOrDefault(p => p.Name == "SizeMiB")?.Value;
        var mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
        if (!benchmarkCase.Descriptor.HasCategory(Category) || size is not int mib || mean is not > 0)
            return "-";

        var bytes = mib * 1024.0 * 1024.0;
        return (bytes / (mean.Value / 1e9) / 1e6).ToString("N1", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Adds <see cref="ThroughputColumn"/>.
/// </summary>
public class ThroughputConfig : ManualConfig
{
    public ThroughputConfig()
    {
        AddColumn(new ThroughputColumn());
    }
}
