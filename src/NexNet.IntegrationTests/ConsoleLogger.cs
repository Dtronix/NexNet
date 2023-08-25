using System.Diagnostics;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

public class ConsoleLogger : INexusLogger
{
    private readonly string _prefix = "";
    private readonly Stopwatch _sw;
    private readonly TextWriter _outWriter;

    public string? Category { get; }

    public bool LogEnabled { get; set; } = true;

    private readonly ConsoleLogger _baseLogger;

    public ConsoleLogger()
    {
        _baseLogger = this;
        _sw = Stopwatch.StartNew();
        _outWriter = TestContext.Out;
    }

    private ConsoleLogger(ConsoleLogger baseLogger, string? category, string prefix = "")
    {
        _baseLogger = baseLogger;
        _prefix = prefix;
        Category = category;
        _sw = baseLogger._sw;
        _outWriter = baseLogger._outWriter;
    }


    public void Log(INexusLogger.LogLevel logLevel, string? category, Exception? exception, string message)
    {
        if (!_baseLogger.LogEnabled)
            return;

        _outWriter.WriteLine($"[{_sw.ElapsedTicks/(double)Stopwatch.Frequency:0.000000}]{_prefix} [{category}]: {message} {exception}");
    }

    public INexusLogger CreateLogger(string? category)
    {
        return new ConsoleLogger(_baseLogger, category, _prefix);
    }

    public INexusLogger CreateLogger(string? category, string prefix)
    {
        return new ConsoleLogger(_baseLogger, category, prefix);
    }
}
