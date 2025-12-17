using System;
using System.Diagnostics;
using System.IO;

namespace NexNet.Logging;

/// <summary>
/// The RollingLogger class extends the CoreLogger abstract class and provides functionality for logging with a rolling line buffer.
/// It maintains a buffer of log lines, and when the buffer is full, it discards the oldest log lines to make room for new ones.
/// </summary>
public class RollingLogger : CoreLogger<RollingLogger>
{
    private readonly string[]? _lines;
    private int _currentLineIndex = 0;
    private int _totalLinesWritten;
    private readonly RollingLogger _baseLogger;

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
    /// Initializes a new instance of the <see cref="RollingLogger"/> class with a rolling buffer for log lines.
    /// </summary>
    /// <param name="maxLines">Maximum number of log lines to keep in the buffer before rolling.</param>
    /// <param name="parentLogger">Parent logger for creating a logger hierarchy.</param>
    /// <param name="pathSegment">Path segment to append to the logger's category path.</param>
    public RollingLogger(int maxLines = 200, RollingLogger? parentLogger = null, string? pathSegment = null)
        : base(parentLogger, pathSegment)
    {
        if (parentLogger == null)
        {
            _baseLogger = this; 
            _lines = new string[maxLines];
        }
        else
        {
            // Get the root logger
            var current = parentLogger;

            while (current != null && current != current.ParentLogger)
                current = current.ParentLogger;

            _baseLogger = current ?? this;
        }
    }


    /// <inheritdoc/>
    public override void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        var log = GetFormattedLogString(logLevel, category, exception, message);

        if (log == null)
            return;
        
        lock (_baseLogger._lines!)
        {
            _baseLogger._lines[_baseLogger._currentLineIndex] = log;
            _baseLogger._currentLineIndex = (_baseLogger._currentLineIndex + 1) % _baseLogger._lines.Length;
            _baseLogger._totalLinesWritten++;
        }
    }
    
    /// <inheritdoc />
    public override INexusLogger CreateLogger(string? pathSegment = null)
    {
        return new RollingLogger(parentLogger: this, pathSegment: pathSegment);
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
        lock (_baseLogger._lines!)
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
}
