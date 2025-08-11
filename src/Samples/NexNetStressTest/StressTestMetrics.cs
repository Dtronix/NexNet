using System.Collections.Concurrent;
using System.Diagnostics;

namespace NexNetStressTest;

public class StressTestMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentQueue<double> _latencies = new();
    private readonly object _lock = new();
    private DateTime _startTime;
    private DateTime _endTime;

    public void Start()
    {
        _startTime = DateTime.UtcNow;
    }

    public void Stop()
    {
        _endTime = DateTime.UtcNow;
    }

    public void IncrementCounter(string counterName)
    {
        _counters.AddOrUpdate(counterName, 1, (key, value) => value + 1);
    }

    public void RecordLatency(double latencyMs)
    {
        _latencies.Enqueue(latencyMs);
    }

    public void RecordConnectionTime(double connectionTimeMs)
    {
        _latencies.Enqueue(connectionTimeMs);
    }

    public long GetCounter(string counterName)
    {
        return _counters.GetValueOrDefault(counterName, 0);
    }

    public TestResults GetResults()
    {
        var totalDuration = _endTime - _startTime;
        var latencyArray = _latencies.ToArray();
        
        var results = new TestResults
        {
            TestDuration = totalDuration,
            StartTime = _startTime,
            EndTime = _endTime,
            ConnectionsSuccessful = GetCounter("ConnectionsSuccessful"),
            ConnectionsFailed = GetCounter("ConnectionsFailed"),
            FireAndForgetInvocations = GetCounter("FireAndForgetInvocations"),
            ReturnValueInvocations = GetCounter("ReturnValueInvocations"),
            InvocationErrors = GetCounter("InvocationErrors"),
            TotalInvocations = GetCounter("FireAndForgetInvocations") + GetCounter("ReturnValueInvocations")
        };

        if (latencyArray.Length > 0)
        {
            Array.Sort(latencyArray);
            results.AverageLatencyMs = latencyArray.Average();
            results.MinLatencyMs = latencyArray.Min();
            results.MaxLatencyMs = latencyArray.Max();
            results.P50LatencyMs = GetPercentile(latencyArray, 0.5);
            results.P95LatencyMs = GetPercentile(latencyArray, 0.95);
            results.P99LatencyMs = GetPercentile(latencyArray, 0.99);
        }

        results.InvocationsPerSecond = totalDuration.TotalSeconds > 0 
            ? results.TotalInvocations / totalDuration.TotalSeconds 
            : 0;

        return results;
    }

    private static double GetPercentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        
        var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        index = Math.Max(0, Math.Min(sortedValues.Length - 1, index));
        return sortedValues[index];
    }
}

public class TestResults
{
    public TimeSpan TestDuration { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long ConnectionsSuccessful { get; set; }
    public long ConnectionsFailed { get; set; }
    public long FireAndForgetInvocations { get; set; }
    public long ReturnValueInvocations { get; set; }
    public long TotalInvocations { get; set; }
    public long InvocationErrors { get; set; }
    public double InvocationsPerSecond { get; set; }
    public double AverageLatencyMs { get; set; }
    public double MinLatencyMs { get; set; }
    public double MaxLatencyMs { get; set; }
    public double P50LatencyMs { get; set; }
    public double P95LatencyMs { get; set; }
    public double P99LatencyMs { get; set; }

    public void PrintResults()
    {
        Console.WriteLine("\n=== Stress Test Results ===");
        Console.WriteLine($"Test Duration: {TestDuration:mm\\:ss\\.fff}");
        Console.WriteLine($"Start Time: {StartTime:HH:mm:ss.fff}");
        Console.WriteLine($"End Time: {EndTime:HH:mm:ss.fff}");
        Console.WriteLine();
        
        Console.WriteLine("--- Connections ---");
        Console.WriteLine($"Successful: {ConnectionsSuccessful:N0}");
        Console.WriteLine($"Failed: {ConnectionsFailed:N0}");
        Console.WriteLine($"Success Rate: {(ConnectionsSuccessful / (double)(ConnectionsSuccessful + ConnectionsFailed) * 100):F2}%");
        Console.WriteLine();
        
        Console.WriteLine("--- Invocations ---");
        Console.WriteLine($"Fire-and-Forget: {FireAndForgetInvocations:N0}");
        Console.WriteLine($"Return Value: {ReturnValueInvocations:N0}");
        Console.WriteLine($"Total: {TotalInvocations:N0}");
        Console.WriteLine($"Errors: {InvocationErrors:N0}");
        Console.WriteLine($"Error Rate: {(InvocationErrors / (double)Math.Max(1, TotalInvocations) * 100):F4}%");
        Console.WriteLine();
        
        Console.WriteLine("--- Performance ---");
        Console.WriteLine($"Invocations/Second: {InvocationsPerSecond:N0}");
        Console.WriteLine($"Average Latency: {AverageLatencyMs:F2} ms");
        Console.WriteLine($"Min Latency: {MinLatencyMs:F2} ms");
        Console.WriteLine($"Max Latency: {MaxLatencyMs:F2} ms");
        Console.WriteLine($"P50 Latency: {P50LatencyMs:F2} ms");
        Console.WriteLine($"P95 Latency: {P95LatencyMs:F2} ms");
        Console.WriteLine($"P99 Latency: {P99LatencyMs:F2} ms");
        Console.WriteLine("==========================");
    }
}