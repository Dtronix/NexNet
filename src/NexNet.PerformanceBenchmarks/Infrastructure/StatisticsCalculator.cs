namespace NexNet.PerformanceBenchmarks.Infrastructure;

/// <summary>
/// Calculates statistical metrics from measurement data.
/// </summary>
public static class StatisticsCalculator
{
    /// <summary>
    /// Calculates all statistics from a set of measurements.
    /// </summary>
    public static StatisticsResult Calculate(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return new StatisticsResult
            {
                Mean = 0,
                StdDev = 0,
                Min = 0,
                Max = 0,
                P50 = 0,
                P95 = 0,
                P99 = 0,
                Count = 0
            };
        }

        if (values.Count == 1)
        {
            var single = values[0];
            return new StatisticsResult
            {
                Mean = single,
                StdDev = 0,
                Min = single,
                Max = single,
                P50 = single,
                P95 = single,
                P99 = single,
                Count = 1
            };
        }

        var sorted = values.OrderBy(x => x).ToArray();
        var mean = sorted.Average();
        var variance = sorted.Sum(x => Math.Pow(x - mean, 2)) / (sorted.Length - 1);
        var stdDev = Math.Sqrt(variance);

        return new StatisticsResult
        {
            Mean = mean,
            StdDev = stdDev,
            Min = sorted[0],
            Max = sorted[^1],
            P50 = Percentile(sorted, 50),
            P95 = Percentile(sorted, 95),
            P99 = Percentile(sorted, 99),
            Count = sorted.Length
        };
    }

    /// <summary>
    /// Calculates a specific percentile from sorted values.
    /// </summary>
    public static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return 0;

        if (sortedValues.Length == 1)
            return sortedValues[0];

        var n = sortedValues.Length;
        var rank = (percentile / 100.0) * (n - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);

        if (lowerIndex == upperIndex || upperIndex >= n)
            return sortedValues[Math.Min(lowerIndex, n - 1)];

        var fraction = rank - lowerIndex;
        return sortedValues[lowerIndex] * (1 - fraction) + sortedValues[upperIndex] * fraction;
    }

    /// <summary>
    /// Calculates the mean of a collection of values.
    /// </summary>
    public static double Mean(IReadOnlyList<double> values)
    {
        return values.Count == 0 ? 0 : values.Average();
    }

    /// <summary>
    /// Calculates the standard deviation of a collection of values.
    /// </summary>
    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count <= 1)
            return 0;

        var mean = values.Average();
        var variance = values.Sum(x => Math.Pow(x - mean, 2)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }

    /// <summary>
    /// Removes outliers using the IQR method.
    /// </summary>
    public static IReadOnlyList<double> RemoveOutliers(IReadOnlyList<double> values, double factor = 1.5)
    {
        if (values.Count < 4)
            return values;

        var sorted = values.OrderBy(x => x).ToArray();
        var q1 = Percentile(sorted, 25);
        var q3 = Percentile(sorted, 75);
        var iqr = q3 - q1;
        var lowerBound = q1 - factor * iqr;
        var upperBound = q3 + factor * iqr;

        return sorted.Where(x => x >= lowerBound && x <= upperBound).ToArray();
    }

    /// <summary>
    /// Calculates the coefficient of variation (CV) as a percentage.
    /// </summary>
    public static double CoefficientOfVariation(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;

        var mean = values.Average();
        if (Math.Abs(mean) < double.Epsilon)
            return 0;

        var stdDev = StandardDeviation(values);
        return (stdDev / mean) * 100;
    }
}

/// <summary>
/// Result of statistical calculations.
/// </summary>
public sealed class StatisticsResult
{
    /// <summary>Arithmetic mean.</summary>
    public double Mean { get; init; }

    /// <summary>Standard deviation (sample).</summary>
    public double StdDev { get; init; }

    /// <summary>Minimum value.</summary>
    public double Min { get; init; }

    /// <summary>Maximum value.</summary>
    public double Max { get; init; }

    /// <summary>50th percentile (median).</summary>
    public double P50 { get; init; }

    /// <summary>95th percentile.</summary>
    public double P95 { get; init; }

    /// <summary>99th percentile.</summary>
    public double P99 { get; init; }

    /// <summary>Number of values.</summary>
    public int Count { get; init; }

    /// <summary>Range (Max - Min).</summary>
    public double Range => Max - Min;

    /// <summary>Coefficient of variation as percentage.</summary>
    public double CoefficientOfVariation => Mean != 0 ? (StdDev / Mean) * 100 : 0;
}
