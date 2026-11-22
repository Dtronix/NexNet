using NexNet.Interop;
using NexNet.Interop.Hosts;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// DisableAsync returns once the collection's pipe is freed (S23): with a single pipe slot, an enable right after a
/// disable always finds the slot available. On every row of the client matrix: the client's pipe limit is
/// <see cref="ClientSpec.MaxPipes"/>, and its open pipes are those of its session snapshot.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionDisableFreesPipeTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    private const int Cycles = 1000;

    /// <summary>
    /// Why the Java and JavaScript rows are skipped: a runtime gap with a follow-up card (Q05 report), listed in
    /// <c>ClientParityExceptions</c>.
    /// </summary>
    internal const string DisableReturnsEarly =
        "the Java and JavaScript clients return from DisableAsync before the collection's pipe is freed, so with one pipe slot "
        + "an enable right after it is refused (Busy) and the snapshot still shows the pipe (S23 parity, follow-up card)";

    /// <summary>The pipes open on the client's session.</summary>
    private static async Task<int> OpenPipesAsync(IClientUnderTest client)
        => (await client.GetSnapshotAsync().Timeout(T(1)))!.Pipes.Count;

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task DisableAsync_FreesThePipeBeforeReturning(ClientTarget target)
    {
        RequireInProc(target, DisableReturnsEarly);
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, configure: spec => spec.MaxPipes = 1);
        var list = client.List<int>(IntListBi);

        await Assert.That(await list.EnableAsync().AsTask().Timeout(T(5))).IsTrue();
        await Assert.That(await OpenPipesAsync(client)).IsEqualTo(1);
        await list.DisableAsync().AsTask().Timeout(T(5));

        await Assert.That(await OpenPipesAsync(client)).IsZero();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task EnableDisableCycles_WithOnePipeSlot_AllSucceed(ClientTarget target)
    {
        RequireInProc(target, DisableReturnsEarly);
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, configure: spec => spec.MaxPipes = 1);
        var list = client.List<int>(IntListBi);

        for (var i = 0; i < Cycles; i++)
        {
            await Assert.That(await list.EnableAsync().AsTask().Timeout(T(5))).IsTrue();
            await list.DisableAsync().AsTask().Timeout(T(5));
        }

        await Assert.That(await OpenPipesAsync(client)).IsZero();
        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Connected);
        await EndAsync(client);
    }

    /// <summary>
    /// S25: the server also has a single pipe slot. The client frees its pipe once the server confirmed the close,
    /// which can be before the server's collection handler released the server's side; the next enable must not be
    /// refused for that.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task EnableDisableCycles_WithOnePipeSlotOnBothSides_AllSucceed(ClientTarget target)
    {
        RequireInProc(target, DisableReturnsEarly);
        await using var server = await StartServerAsync(configure: config => config.MaxPipes = 1);
        await using var client = await ConnectAsync(target, server, configure: spec => spec.MaxPipes = 1);
        var list = client.List<int>(IntListBi);

        for (var i = 0; i < Cycles; i++)
        {
            await Assert.That(await list.EnableAsync().AsTask().Timeout(T(5))).IsTrue();
            await list.DisableAsync().AsTask().Timeout(T(5));
        }

        await Assert.That(await OpenPipesAsync(client)).IsZero();
        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Connected);
        await EndAsync(client);
    }
}
