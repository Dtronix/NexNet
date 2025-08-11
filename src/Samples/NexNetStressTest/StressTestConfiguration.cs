namespace NexNetStressTest;

public class StressTestConfiguration
{
    public int ConcurrentConnections { get; set; } = 100;
    public int FireAndForgetInvocationsPerConnection { get; set; } = 1000;
    public int ReturnValueInvocationsPerConnection { get; set; } = 500;
    public int WarmupDurationSeconds { get; set; } = 5;
    public int TestDurationSeconds { get; set; } = 30;
    public string ServerAddress { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 8765;
    public bool UseTls { get; set; } = false;
    public bool ReportProgress { get; set; } = true;
    public int ProgressReportIntervalSeconds { get; set; } = 5;
    public int ComplexOperationIterations { get; set; } = 1000;
    public int MaxConnectionAttempts { get; set; } = 3;
    public int ConnectionTimeoutMs { get; set; } = 5000;

    public void PrintConfiguration()
    {
        Console.WriteLine("=== Stress Test Configuration ===");
        Console.WriteLine($"Concurrent Connections: {ConcurrentConnections}");
        Console.WriteLine($"Fire-and-Forget per Connection: {FireAndForgetInvocationsPerConnection}");
        Console.WriteLine($"Return Value per Connection: {ReturnValueInvocationsPerConnection}");
        Console.WriteLine($"Total Expected Invocations: {(FireAndForgetInvocationsPerConnection + ReturnValueInvocationsPerConnection) * ConcurrentConnections:N0}");
        Console.WriteLine($"Warmup Duration: {WarmupDurationSeconds}s");
        Console.WriteLine($"Test Duration: {TestDurationSeconds}s");
        Console.WriteLine($"Server: {ServerAddress}:{ServerPort} (TLS: {UseTls})");
        Console.WriteLine($"Progress Reporting: {ReportProgress} (every {ProgressReportIntervalSeconds}s)");
        Console.WriteLine("================================");
    }
}