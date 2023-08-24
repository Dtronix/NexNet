using System.Diagnostics;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

public class ConsoleLogger : INexusLogger
{
    private readonly string _prefix = "";
    private readonly Stopwatch _sw;
    private readonly TextWriter _outWriter;

    public string? Category { get; }



    prop

    public ConsoleLogger()
    {
        _sw = Stopwatch.StartNew();
        _outWriter = TestContext.Out;
    }

    private ConsoleLogger(ConsoleLogger logger, string? category, string prefix = "")
    {
        _prefix = prefix;
        Category = category;
        _sw = logger._sw;
        _outWriter = logger._outWriter;
    }


    public void Log(INexusLogger.LogLevel logLevel, string? category, Exception? exception, string message)
    {
        _outWriter.WriteLine($"[{_sw.ElapsedTicks/(double)Stopwatch.Frequency:0.000000}]{_prefix} [{category}]: {message} {exception}");
    }

    public INexusLogger CreateLogger(string? category)
    {
        return new ConsoleLogger(this, category);
    }

    public INexusLogger CreateLogger(string? category, string prefix)
    {
        return new ConsoleLogger(this, category, prefix);
    }
}
