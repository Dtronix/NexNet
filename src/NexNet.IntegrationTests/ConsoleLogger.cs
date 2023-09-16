using System;
using System.Diagnostics;
using System.IO;

namespace NexNet.IntegrationTests;

public class ConsoleLogger : INexusLogger
{
    private readonly string _prefix = "";
    private readonly Stopwatch _sw;
    private readonly string[]? _lines;
    private int _currentLineIndex = 0;
    private int _totalLinesWritten = 0;

    public string? Category { get; set; }

    public bool LogEnabled { get; set; } = true;

    private readonly ConsoleLogger _baseLogger;

    public INexusLogger.LogLevel MinLogLevel { get; set; } = INexusLogger.LogLevel.Trace;

    public int TotalLinesWritten
    {
        get => _totalLinesWritten;
    }

    public ConsoleLogger(int maxLines = 200)
    {
        _lines = new string[maxLines];
        _baseLogger = this;
        _sw = Stopwatch.StartNew();
    }

    private ConsoleLogger(ConsoleLogger baseLogger, string? category, string prefix = "")
    {
        _baseLogger = baseLogger;
        _prefix = prefix;
        Category = category;
        _sw = baseLogger._sw;
    }


    public void Log(INexusLogger.LogLevel logLevel, string? category, Exception? exception, string message)
    {
        if (!_baseLogger.LogEnabled)
            return;

        if (logLevel < MinLogLevel)
            return;

        lock (_baseLogger._lines!)
        {
            _baseLogger._lines[_baseLogger._currentLineIndex] =
                $"[{_sw.ElapsedTicks / (double)Stopwatch.Frequency:0.000000}]{_prefix} [{category}]: {message} {exception}";
            _baseLogger._currentLineIndex = (_baseLogger._currentLineIndex + 1) % _baseLogger._lines.Length;
            _baseLogger._totalLinesWritten++;
        }
    }

    public INexusLogger CreateLogger(string? category)
    {
        return new ConsoleLogger(_baseLogger, category, _prefix) { MinLogLevel = MinLogLevel };
    }

    public INexusLogger CreateLogger(string? category, string prefix)
    {
        return new ConsoleLogger(_baseLogger, category, prefix) { MinLogLevel = MinLogLevel };
    }

    public void Flush(TextWriter writer)
    {
        if (_baseLogger._totalLinesWritten == 0)
            return;

        var startIndex = _baseLogger._currentLineIndex;
        var maxLines = _baseLogger._lines!.Length;

        if (_baseLogger._totalLinesWritten > maxLines)
        {
            writer.WriteLine($"Truncating Log. Showing only last {maxLines} out of {_baseLogger._totalLinesWritten} total lines written.");
        }
        for (int i = 0; i < maxLines; i++)
        {
            writer.WriteLine(_baseLogger._lines![(startIndex + i) % maxLines]);
        }

        _baseLogger._currentLineIndex = 0;
        _baseLogger._totalLinesWritten = 0;
    }
}
