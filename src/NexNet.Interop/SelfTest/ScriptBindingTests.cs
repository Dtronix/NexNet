using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop.InProc;

namespace NexNet.Interop.SelfTest;

/// <summary>
/// The in-process driver binds the script facade to the <c>…Event</c> hooks of the integration tests' ClientNexus (the
/// nexus is the hooks' first parameter); these tests call the hooks directly, without a connection.
/// </summary>
public class ScriptBindingTests
{
    private static InProcClientDriver<ClientNexus, ClientNexus.ServerProxy> CreateDriver()
        => new(ClientTarget.CSharp(ClientTransport.Tcp), new ClientSpec { Address = "tcp://127.0.0.1:1" });

    [Test]
    public async Task EventHooks_RunTheScript()
    {
        await using var driver = CreateDriver();
        var nexus = driver.Nexus;
        var script = driver.Callbacks<ClientNexusScript>();

        await Assert.That(await nexus.ClientTaskValueWithParamEvent(nexus, 5)).IsEqualTo(0);
        script.ClientTaskValueWithParam.Returns(9).Times(2);
        await Assert.That(await nexus.ClientTaskValueWithParamEvent(nexus, 6)).IsEqualTo(9);
        await Assert.That(await nexus.ClientTaskValueWithParamEvent(nexus, 7)).IsEqualTo(9);
        await Assert.That(await nexus.ClientTaskValueWithParamEvent(nexus, 8)).IsEqualTo(0);

        var seen = new List<int>();
        for (var i = 0; i < 4; i++)
            seen.Add((await script.ClientTaskValueWithParam.NextCallAsync()).data);
        await Assert.That(seen).IsEquivalentTo(new[] { 5, 6, 7, 8 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        nexus.ClientVoidWithParamEvent(nexus, 3);
        await Assert.That((await script.ClientVoidWithParam.NextCallAsync()).id).IsEqualTo(3);

        await nexus.OnConnectedEvent!(nexus, true);
        await Assert.That((await script.OnConnected.NextCallAsync()).IsReconnected).IsTrue();
    }

    [Test]
    public async Task AwaitCancel_And_Throws()
    {
        await using var driver = CreateDriver();
        var nexus = driver.Nexus;
        var script = driver.Callbacks<ClientNexusScript>();

        script.ClientTaskValueWithCancellation.AwaitCancel().Returns(4);
        using var cts = new CancellationTokenSource();
        var call = nexus.ClientTaskValueWithCancellationEvent(nexus, cts.Token).AsTask();
        await script.ClientTaskValueWithCancellation.NextCallAsync();
        await Assert.That(call.IsCompleted).IsFalse();
        await cts.CancelAsync();
        await Assert.That(await call.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(4);

        script.ClientTask.Throws(ErrorKind.Canceled, "stop");
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await nexus.ClientTaskEvent(nexus));
    }

    [Test]
    public async Task WrongFacade_Throws()
    {
        await using var driver = CreateDriver();
        await Assert.That(() => driver.Callbacks<NexNet.TestNexus.Interop.InteropClientNexusScript>()).Throws<InvalidOperationException>();
    }
}
