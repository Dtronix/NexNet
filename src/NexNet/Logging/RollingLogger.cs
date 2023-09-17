using System;
using System.Diagnostics;
using System.IO;

namespace NexNet.Logging;

/// <summary>
/// The RollingLogger class extends the CoreLogger abstract class and provides functionality for logging with a rolling line buffer.
/// It maintains a buffer of log lines, and when the buffer is full, it discards the oldest log lines to make room for new ones.
/// </summary>
public class RollingLogger : CoreLogger
{
    private readonly string[]? _lines;
    private int _currentLineIndex = 0;
    private readonly RollingLogger _baseLogger;
    private int _totalLinesWritten;

    /// <summary>
    /// Gets the total number of lines that have been written by the logger.
    /// This count includes all log entries, regardless of their log level or category.
    /// The count is reset to 0 after the log entries are written to a TextWriter using the Flush method.
    /// </summary>
    public int TotalLinesWritten
    {
        get => _totalLinesWritten;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RollingLogger"/> class.
    /// </summary>
    /// <param name="maxLines">The maximum number of lines that can be stored in the logger. Default value is 200.</param>
    public RollingLogger(int maxLines = 200)
    {
        _lines = new string[maxLines];
        _baseLogger = this;
        SessionDetails = "";
    }

    private RollingLogger(RollingLogger baseLogger, string? category, string? prefix = null,
        string? sessionDetails = null)
    {
        _baseLogger = baseLogger;
        Prefix = prefix;
        SessionDetails = sessionDetails ?? "";
        Category = category;
    }

    /// <inheritdoc/>
    public override void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        if (!_baseLogger.LogEnabled)
            return;

        if (logLevel < MinLogLevel)
            return;
        var time = _baseLogger.Sw.ElapsedTicks / (double)Stopwatch.Frequency;

        lock (_baseLogger._lines!)
        {
            _baseLogger._lines[_baseLogger._currentLineIndex] = Prefix != null
                ? $"[{time:0.000000}]{Prefix} [{category}:{SessionDetails}] {message} {exception}"
                : $"[{time:0.000000}] [{category}:{SessionDetails}] {message} {exception}";
            _baseLogger._currentLineIndex = (_baseLogger._currentLineIndex + 1) % _baseLogger._lines.Length;
            _baseLogger._totalLinesWritten++;
        }
    }
    /// <inheritdoc/>
    public override INexusLogger CreateLogger(string? category, string? sessionDetails = null)
    {
        return new RollingLogger(_baseLogger, category, Prefix, sessionDetails ?? SessionDetails);
    }
    /// <inheritdoc/>
    public override CoreLogger CreatePrefixedLogger(string? category, string prefix, string? sessionDetails = null)
    {
        return new RollingLogger(_baseLogger, category, prefix, sessionDetails ?? SessionDetails);
    }

    /// <summary>
    /// Writes the log entries to the provided TextWriter.
    /// </summary>
    /// <param name="writer">The TextWriter to which the log entries will be written.</param>
    /// <remarks>
    /// If the total number of lines written is greater than the maximum lines that can be stored, 
    /// a truncation message will be written to the TextWriter before the log entries. 
    /// After the log entries are written, the total lines written and current line index are reset to 0.
    /// </remarks>
    public void Flush(TextWriter writer)
    {
        if (_baseLogger._totalLinesWritten == 0)
            return;

        var startIndex = _baseLogger._currentLineIndex;
        var maxLines = _baseLogger._lines!.Length;

        if (_baseLogger._totalLinesWritten > maxLines)
        {
            writer.WriteLine(
                $"Truncating Log. Showing only last {maxLines} out of {_baseLogger._totalLinesWritten} total lines written.");
        }

        var readingIndexStart = _baseLogger._totalLinesWritten >= maxLines ? startIndex : 0;
        var loops = _baseLogger._totalLinesWritten >= maxLines ? maxLines : _baseLogger._totalLinesWritten;
        for (int i = 0; i < loops; i++)
        {
            writer.WriteLine(_baseLogger._lines![(readingIndexStart + i) % maxLines]);
        }

        _baseLogger._currentLineIndex = 0;
        _baseLogger._totalLinesWritten = 0;
    }
}
