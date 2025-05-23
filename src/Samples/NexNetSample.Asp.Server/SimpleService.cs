using NexNet.Invocation;

namespace NexNetSample.Asp.Server;

public class SimpleService : BackgroundService
{
    private readonly ServerNexusContextProvider<ServerNexus.ClientProxy> _contextProvider;

    public SimpleService(ServerNexusContextProvider<ServerNexus.ClientProxy> contextProvider)
    {
        _contextProvider = contextProvider;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var context = _contextProvider.Rent();

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(4000, stoppingToken);
            await context.Proxy.All.ClientTaskWithParam(54321);
        }
    }
}
