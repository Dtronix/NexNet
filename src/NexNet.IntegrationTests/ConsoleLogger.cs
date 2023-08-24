using System.Diagnostics;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

public class ConsoleLogger : INexusLogger
{
    private readonly string _prefix = "";
    private readonly Stopwatch _sw;
    private readonly TextWriter _outWriter;

    public string? Category { get; }

    public ConsoleLogger()
    {
        _sw = Stopwatch.StartNew();
        _outWriter = TestContext.Out;
    }

    private ConsoleLogger(string prefix, string? category, Stopwatch sw, TextWriter textWriter)
    {
        _prefix = prefix;
        Category = category;
        _sw = sw;
        _outWriter = textWriter;
    }


    public void Log(INexusLogger.LogLevel logLevel, string? category, Exception? exception, string message)
    {
        _outWriter.WriteLine($"[{_sw.ElapsedTicks/(double)Stopwatch.Frequency:0.000000}]{_prefix} [{category}]: {message} {exception}");
    }

    public INexusLogger CreateLogger(string? category)
    {
        return new ConsoleLogger(_prefix, category, _sw, _outWriter);
    }
}
