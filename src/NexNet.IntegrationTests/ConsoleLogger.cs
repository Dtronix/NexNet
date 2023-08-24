using System.Diagnostics;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

public class ConsoleLogger : INexusLogger
{
    private readonly string _prefix;
    private DateTime _startTime = DateTime.Now;
    public bool LogEnabled = true;
    public string? Category { get; }

    public ConsoleLogger(string prefix)
    {
        _prefix = prefix;
    }

    private ConsoleLogger(string prefix, string? category)
    {
        _prefix = prefix;
        Category = category;
    }


    public void Log(INexusLogger.LogLevel logLevel, string? category, Exception? exception, string message)
    {
        if (!LogEnabled)
            return;

        TestContext.Out.WriteLine($"[{DateTime.Now - _startTime:c}]{_prefix} [{category}]: {message} {exception}");
    }

    public INexusLogger CreateLogger(string? category)
    {
        return new ConsoleLogger(_prefix, category);
    }
}
