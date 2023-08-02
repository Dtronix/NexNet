using NexNet;

namespace NexNetDemo;

class SampleLogger : INexusLogger
{
    private readonly string _prefix;

    public SampleLogger(string prefix)
    {
        _prefix = prefix;
    }
    public void Log(INexusLogger.LogLevel logLevel, Exception? exception, string message)
    {
        if (logLevel < INexusLogger.LogLevel.Information)
            return;

        Console.WriteLine($"{_prefix} {logLevel}: {message} {exception}");
    }
}
