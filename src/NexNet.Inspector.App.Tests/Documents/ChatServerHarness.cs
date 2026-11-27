using NexNet.Inspector.Sample;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Documents;

/// <summary>
/// An in-process Chat server with an inspector and its endpoint on loopback TCP, and helpers for Chat clients: the
/// live server the attach tests inspect (after the inspector tests' TapHarness).
/// </summary>
internal sealed class ChatServerHarness : IAsyncDisposable
{
    public const string Token = "app-test-endpoint-token";

    private readonly List<IAsyncDisposable> _owned = [];
    private readonly SampleAddress _serverAddress;

    private ChatServerHarness(SampleAddress serverAddress, NexusInspector inspector,
        NexusServer<ChatServerNexus, ChatServerNexus.ClientProxy> server)
    {
        _serverAddress = serverAddress;
        Inspector = inspector;
        Server = server;
    }

    public NexusInspector Inspector { get; }

    public NexusServer<ChatServerNexus, ChatServerNexus.ClientProxy> Server { get; }

    public InspectorEndpoint? Endpoint { get; private set; }

    /// <summary>The endpoint as the Connect dialog takes it.</summary>
    public string EndpointAddress { get; private set; } = "";

    public static async Task<ChatServerHarness> StartAsync(bool allowRecording)
    {
        var address = SampleAddress.Loopback(SampleTransport.Tcp);
        var config = address.CreateServerConfig();
        config.Authenticate = true;
        config.Timeout = 300_000;
        var inspector = config.UseInspector<ChatServerNexus>(new InspectorOptions { AllowRecording = allowRecording });
        var server = ChatServerNexus.CreateServer(config, static () => new ChatServerNexus());
        inspector.UseSnapshotsOf(server);
        await server.StartAsync();
        var harness = new ChatServerHarness(address.BoundTo(server), inspector, server);

        var endpointAddress = SampleAddress.Loopback(SampleTransport.Tcp);
        harness.Endpoint = await inspector.StartEndpointAsync(endpointAddress.CreateServerConfig(), "app-test-endpoint-token"u8.ToArray());
        harness.EndpointAddress = endpointAddress.BoundTo(harness.Endpoint.Server).ToString();
        return harness;
    }

    public async Task<NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy>> ConnectClientAsync()
    {
        var config = _serverAddress.CreateClientConfig();
        config.Authenticate = static () => ChatScenario.Token;
        config.PingInterval = 600_000;
        config.Timeout = 300_000;
        var client = ChatClientNexus.CreateClient(config, new ChatClientNexus());
        _owned.Add(client);
        await client.ConnectAsync();
        return client;
    }

    /// <summary>The newest connected session once <paramref name="count"/> are connected.</summary>
    public async Task<InspectorSessionInfo> WaitForSessionAsync(int count = 1)
    {
        for (var i = 0; i < 1000; i++)
        {
            var sessions = Inspector.GetSessions();
            if (sessions.Length >= count && sessions.All(s => s.State == InspectorSessionState.Connected))
                return sessions[^1];
            await Task.Delay(10);
        }

        throw new TimeoutException("The Chat session did not connect.");
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                await _owned[i].DisposeAsync();
            }
            catch (Exception)
            {
                // A client the test already disconnected.
            }
        }

        if (Endpoint is not null)
            await Endpoint.DisposeAsync();
        await Server.DisposeAsync();
        await Inspector.DisposeAsync();
    }
}
