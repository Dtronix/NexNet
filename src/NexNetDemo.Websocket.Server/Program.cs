using NexNet.Logging;
using NexNet.Transports.Websocket;
using NexNet.Transports.WebSocket.Asp;

namespace NexNetDemo.Websocket.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        
        app.MapGet("/", () => "Hello World!");

        var serverConfig = new WebSocketServerConfig()
        {
            Logger = new LoggerNexusBridge(app.Logger),
        };

        var nexusServer = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());
        _ = Task.Run(async () => await nexusServer.StartAsync(app.Lifetime.ApplicationStopping));

        app.UseAuthorization();
        app.UseWebSockets();
        app.Use(serverConfig.Middleware);

        
        
        app.Run();
    }

}

public class LoggerNexusBridge : INexusLogger
{
    private readonly ILogger _logger;

    public LoggerNexusBridge(ILogger logger)
    {
        _logger = logger;
    }
    public string? Category { get; set; }
    public string? SessionDetails { get; set; }
    public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        _logger.Log((LogLevel)logLevel, exception, message);
    }

    public INexusLogger CreateLogger(string? category, string? sessionDetails = null)
    {
        return this;
    }
}
