using NexNet;
using System.Diagnostics;

namespace NexNetStressTest;

interface IStressTestClientNexus
{
    // Methods the server can call on the client
    void NotifyClientResult(string message);
}

interface IStressTestServerNexus
{
    // Fire-and-forget methods (void return)
    void FireAndForgetSimple(int value);
    void FireAndForgetWithString(string message);
    void FireAndForgetComplex(int id, string data, DateTime timestamp);

    // Return value methods
    ValueTask<int> GetNextNumber();
    ValueTask<string> ProcessData(string input);
    ValueTask<StressTestResult> ComplexOperation(int iterations, string payload);
}

public record StressTestResult(
    int ProcessedItems,
    TimeSpan ProcessingTime,
    string Status
);

[Nexus<IStressTestClientNexus, IStressTestServerNexus>(NexusType = NexusType.Client)]
public partial class StressTestClientNexus
{
    public void NotifyClientResult(string message)
    {
        // Handle server notifications
    }
}

[Nexus<IStressTestServerNexus, IStressTestClientNexus>(NexusType = NexusType.Server)]
public partial class StressTestServerNexus
{
    private long _fireAndForgetCounter = 0;
    private long _returnValueCounter = 0;
    private int _nextNumber = 1;

    public void FireAndForgetSimple(int value)
    {
        Interlocked.Increment(ref _fireAndForgetCounter);
    }

    public void FireAndForgetWithString(string message)
    {
        Interlocked.Increment(ref _fireAndForgetCounter);
    }

    public void FireAndForgetComplex(int id, string data, DateTime timestamp)
    {
        Interlocked.Increment(ref _fireAndForgetCounter);
    }

    public ValueTask<int> GetNextNumber()
    {
        Interlocked.Increment(ref _returnValueCounter);
        return new ValueTask<int>(Interlocked.Increment(ref _nextNumber));
    }

    public ValueTask<string> ProcessData(string input)
    {
        Interlocked.Increment(ref _returnValueCounter);
        return new ValueTask<string>($"Processed: {input} at {DateTime.UtcNow:HH:mm:ss.fff}");
    }

    public ValueTask<StressTestResult> ComplexOperation(int iterations, string payload)
    {
        Interlocked.Increment(ref _returnValueCounter);
        var stopwatch = Stopwatch.StartNew();
        
        // Simulate some work
        var sum = 0;
        for (int i = 0; i < iterations; i++)
        {
            sum += i;
        }
        
        stopwatch.Stop();
        
        var result = new StressTestResult(
            ProcessedItems: iterations,
            ProcessingTime: stopwatch.Elapsed,
            Status: $"Completed processing {payload}"
        );
        
        return new ValueTask<StressTestResult>(result);
    }

    public long GetFireAndForgetCount() => _fireAndForgetCounter;
    public long GetReturnValueCount() => _returnValueCounter;
}