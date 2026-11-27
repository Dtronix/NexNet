namespace NexNet.Inspector.Tap;

/// <summary>A running inspector endpoint (<see cref="NexusInspector.StartEndpointAsync"/>).</summary>
public sealed class InspectorEndpoint : IAsyncDisposable
{
    internal InspectorEndpoint(INexusServer server) => Server = server;

    /// <summary>The endpoint's NexNet server (its endpoints, state and bound address).</summary>
    public INexusServer Server { get; }

    /// <summary>Stops the endpoint: viewers are disconnected and their streams end.</summary>
    public Task StopAsync() => Server.StopAsync();

    /// <summary>Stops and disposes the endpoint.</summary>
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
