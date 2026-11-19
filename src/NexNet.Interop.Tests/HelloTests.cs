using NexNet.Interop.Remote;
using NexNet.Interop.Tests.Fake;

namespace NexNet.Interop.Tests;

public class HelloTests
{
    private static async Task<Exception?> AcceptWith(FakeHostOptions options, string? expectedHash = null, string? language = null)
    {
        using var listener = ControlListener.Start();
        var token = ControlListener.CreateToken();
        var accept = listener.AcceptAsync(token, language, expectedHash, "build-the-fake-host");
        await using var host = await FakeHost.ConnectAsync(listener.ControlArgument, token, options);
        try
        {
            await using var connection = await accept.WaitAsync(TimeSpan.FromSeconds(10));
            return null;
        }
        catch (ControlProtocolException e)
        {
            return e;
        }
    }

    [Test]
    public async Task Hello_IsParsedAndRegistrable()
    {
        await using var fake = await FakeConnection.OpenAsync(new FakeHostOptions
        {
            Language = "fake",
            SchemaHash = "ab12",
            MaxConcurrency = 3,
            Capabilities = ["Tcp", "FakeTime", "NotACapability"],
            Transports = ["Tcp", "WebSocketTls", "Carrier pigeon"],
        });

        var hello = fake.Connection.Hello;
        await Assert.That(hello.Proto).IsEqualTo(1);
        await Assert.That(hello.Lang).IsEqualTo("fake");
        await Assert.That(hello.SchemaHash).IsEqualTo("ab12");
        await Assert.That(hello.MaxConcurrency).IsEqualTo(3);
        await Assert.That(hello.Pid).IsEqualTo(Environment.ProcessId);
        await Assert.That(hello.Capabilities).IsEqualTo(ClientCapabilities.Tcp | ClientCapabilities.FakeTime);
        await Assert.That(hello.Transports).IsEquivalentTo(new[] { ClientTransport.Tcp, ClientTransport.WebSocketTls });

        var info = hello.ToLanguageInfo("fake");
        await Assert.That(info.GetSkipReason(ClientTransport.Tcp, ClientCapabilities.None)).IsNull();
        await Assert.That(info.GetSkipReason(ClientTransport.Uds, ClientCapabilities.None)).IsNotNull();
        await Assert.That(info.GetSkipReason(ClientTransport.Tcp, ClientCapabilities.Pinning)).IsNotNull();
    }

    [Test]
    public async Task Hello_WithTheWrongToken_IsRefused()
    {
        var error = await AcceptWith(new FakeHostOptions { Token = "not-the-token" });
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Message).Contains("wrong token");
    }

    [Test]
    public async Task Hello_WithAnotherProtocolVersion_IsRefused()
    {
        var error = await AcceptWith(new FakeHostOptions { Proto = 2 });
        await Assert.That(error!.Message).Contains("protocol 2");
    }

    [Test]
    public async Task Hello_OfAnotherLanguage_IsRefused()
    {
        var error = await AcceptWith(new FakeHostOptions { Language = "node" }, language: "java");
        await Assert.That(error!.Message).Contains("'node'");
    }

    [Test]
    public async Task SchemaHashMismatch_NamesBothHashesAndTheRebuildCommand()
    {
        var error = await AcceptWith(new FakeHostOptions { SchemaHash = "0123abcd" }, expectedHash: "ffff9999");
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Message).Contains("0123abcd");
        await Assert.That(error.Message).Contains("ffff9999");
        await Assert.That(error.Message).Contains("Rebuild it: build-the-fake-host");

        await Assert.That(await AcceptWith(new FakeHostOptions { SchemaHash = "FFFF9999" }, expectedHash: "ffff9999")).IsNull();
    }

    [Test]
    public async Task SchemaHashMismatch_DefaultsToTheLanguageBuildCommand()
    {
        var hello = new HostHello { Proto = 1, Token = "t", Lang = "java", SchemaHash = "aa" };
        var error = Assert.Throws<ControlProtocolException>(() => ControlListener.Check(hello, "t", "java", "bb", null));
        await Assert.That(error!.Message).Contains(HostBuildCommands.Java);
        await Assert.That(HostBuildCommands.For("browser-firefox")).IsEqualTo(HostBuildCommands.JavaScript);
        await Assert.That(HostBuildCommands.For("node")).IsEqualTo(HostBuildCommands.JavaScript);
    }
}
