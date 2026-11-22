using System.Collections.Concurrent;
using NexNet.Interop;
using NexNet.IntegrationTests.TestInterfaces;

namespace NexNet.IntegrationTests;

internal partial class AspClientServerTests
{
    /// <summary>
    /// A custom header of the WebSocket or HttpSocket upgrade request (<see cref="ClientSpec.UpgradeHeaders"/>) reaches
    /// the ASP.NET request (H43). Java and Node only (<see cref="ClientCapabilities.UpgradeHeaders"/>): the C# client
    /// sends only an <c>Authorization</c> header, and a browser cannot send headers.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.UpgradeHeaders)]
    public async Task CustomUpgradeHeaderReachesTheServer(ClientTarget target)
    {
        const string name = "X-NexNet-Test";
        var seen = new ConcurrentQueue<string>();
        var type = MatrixTestBase.ToType(target.Transport);
        var server = CreateServer(CreateServerConfig(type), null, null, app => app.Use(async (context, next) =>
        {
            seen.Enqueue(context.Request.Headers[name].ToString());
            await next(context).ConfigureAwait(false);
        }));
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var spec = MatrixTestBase.Spec(CreateClientConfig(type), target.Transport, Logger);
        spec.UpgradeHeaders = new Dictionary<string, string> { [name] = "value 1" };
        await using var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(seen).Contains("value 1");
        await MatrixTestBase.EndAsync(client);
    }
}
