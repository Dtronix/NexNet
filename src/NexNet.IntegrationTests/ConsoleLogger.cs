using System.Diagnostics;

namespace NexNet.IntegrationTests;

public class ConsoleLogger : INexusLogger
{
    private readonly string _prefix;
    private DateTime _startTime = DateTime.Now;
    public bool LogEnabled = true;

    public ConsoleLogger(string prefix)
    {
        _prefix = prefix;
    }
    public void Log(INexusLogger.LogLevel logLevel, Exception? exception, string message)
    {
        if (!LogEnabled)
            return;

        Console.WriteLine($"[{DateTime.Now - _startTime:c}]{_prefix}: {message} {exception}");
    }
}


public class StreamLogger : INexusLogger
{
    public Stream? BaseStream { get; }
    private readonly string _prefix;
    //private DateTime _startTime = DateTime.Now;
    private readonly Stopwatch _sw;
    private readonly StreamWriter _logFile;

    public StreamLogger(Stream? baseStream = null)
    {
        _prefix = "";
        baseStream ??= new MemoryStream(new byte[1024 * 1024]);
        BaseStream = baseStream;
        _sw = Stopwatch.StartNew();
        _logFile = new StreamWriter(baseStream);
    }

    private StreamLogger(string prefix, StreamWriter sw, Stopwatch stopwatch)
    {
        _sw = stopwatch;
        _logFile = sw;
        _prefix = prefix;
    }
    public void Log(INexusLogger.LogLevel logLevel, Exception? exception, string message)
    {
        lock (_logFile)
        {
            _logFile.WriteLine($"[{_sw.ElapsedTicks:000000000}]{_prefix}: {message} {exception}");
        }
    }

    public void Flush()
    {
        _logFile.Flush();
    }

    public StreamLogger CreateSubLogger(string prefix)    
    {
        return new StreamLogger(prefix, _logFile, _sw);
    }
}
