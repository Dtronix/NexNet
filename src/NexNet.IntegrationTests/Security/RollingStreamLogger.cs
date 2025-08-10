using NexNet.Logging;
using StreamStruct;
using LogLevel = StreamStruct.LogLevel;

namespace NexNet.IntegrationTests.Security;

public class RollingStreamLogger : IStreamLogger
{
    private readonly LogLevel _minimumLogLevel;
    private readonly CoreLogger _logger;

    public RollingStreamLogger(RollingLogger logger, bool isServer, LogLevel minimumLogLevel)
    {
        _minimumLogLevel = minimumLogLevel;
        _logger = logger.CreatePrefixedLogger("", isServer ? "SV" : "CL");
    }

    public void Log(LogLevel level, string message)
    {
        if (level < _minimumLogLevel)
            return;
        
        NexusLogLevel logLevel = level switch
        {
            LogLevel.Trace => NexusLogLevel.Trace,
            LogLevel.Debug => NexusLogLevel.Debug,
            LogLevel.Info => NexusLogLevel.Information,
            LogLevel.Warning => NexusLogLevel.Warning,
            LogLevel.Error => NexusLogLevel.Error,
            LogLevel.Fatal => NexusLogLevel.Critical,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };
        
        var levelText = level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Fatal => "FATAL",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };
        _logger.Log(logLevel, "", null, $"[{levelText}] {message}" );
    }
}
