using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NexNet.Asp;

/// <summary>
/// Runs a Nexus Server with the application: starts it once the application has started (after Kestrel binds, so
/// ASP.NET endpoints are reachable when the server accepts), and stops it when the application begins stopping.
/// </summary>
internal sealed class NexusServerHostedService : IHostedService
{
    private readonly INexusServer _server;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private CancellationTokenRegistration _startedRegistration;
    private CancellationTokenRegistration _stoppingRegistration;
    private Task? _startTask;
    private Task? _stopTask;

    public NexusServerHostedService(INexusServer server, IHostApplicationLifetime lifetime, ILogger? logger)
    {
        _server = server;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startedRegistration = _lifetime.ApplicationStarted.Register(static state =>
        {
            var service = (NexusServerHostedService)state!;
            lock (service._gate)
            {
                // Stopping before the application finished starting: never start.
                if (service._stopTask == null)
                    service._startTask = service.StartServerAsync();
            }
        }, this);

        _stoppingRegistration = _lifetime.ApplicationStopping.Register(
            static state => ((NexusServerHostedService)state!).BeginStop(), this);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _startedRegistration.DisposeAsync().ConfigureAwait(false);
        await _stoppingRegistration.DisposeAsync().ConfigureAwait(false);
        await BeginStop().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task BeginStop()
    {
        lock (_gate)
            return _stopTask ??= StopServerAsync(_startTask);
    }

    private async Task StartServerAsync()
    {
        try
        {
            await _server.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger?.LogCritical(e, "The Nexus server failed to start; stopping the application.");
            _lifetime.StopApplication();
        }
    }

    private async Task StopServerAsync(Task? startTask)
    {
        if (startTask != null)
            await startTask.ConfigureAwait(false);

        if (_server.State != NexusServerState.Running)
            return;

        try
        {
            await _server.StopAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error while stopping the Nexus server.");
        }
    }
}
