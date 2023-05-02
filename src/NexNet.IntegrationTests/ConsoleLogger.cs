namespace NexNet.IntegrationTests;

public class ConsoleLogger : INexNetLogger
{
    private readonly string _prefix;
    private DateTime _startTime = DateTime.Now;

    public ConsoleLogger(string prefix)
    {
        _prefix = prefix;
    }
    public void Log(INexNetLogger.LogLevel logLevel, Exception? exception, string message)
    {
        Console.WriteLine($"[{DateTime.Now - _startTime:c}]{_prefix}: {message}");
    }
}
