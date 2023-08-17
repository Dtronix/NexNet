using NexNet;

namespace NexNetDemo;

class SampleLogger : INexusLogger
{
    private readonly string _prefix;

    public string? Category { get; }

    public SampleLogger(string prefix, string? category)
    {
        _prefix = prefix;
        Category = category;
    }

    public void Log(INexusLogger.LogLevel logLevel, string? category, Exception? exception, string message)
    {
        //if (logLevel < INexusLogger.LogLevel.Information)
        //    return;

        Console.WriteLine($"{_prefix} {logLevel}: {message} {exception}");
    }

    public INexusLogger CreateLogger(string? category)
    {
        return new SampleLogger(_prefix, category);
    }
}
