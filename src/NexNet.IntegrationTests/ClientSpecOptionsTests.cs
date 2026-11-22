using NexNet.IntegrationTests.Metrics;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Logging;
using NexNet.Pipes;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The client configuration fields of <see cref="ClientSpec"/> on every row of the client matrix: session budget, pipe
/// limits, log level, certificate verification and the invocation queue warning. What each field changes is observed
/// on the server or through <see cref="IClientUnderTest"/>.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ClientSpecOptionsTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task SessionBudget_IsTheLimitOfTheClientsBudget(ClientTarget target)
    {
        const int budget = 32 * 1024 * 1024;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, configure: spec => spec.SessionBudget = budget);

        var snapshot = await client.GetBudgetAsync().Timeout(T(1));
        await Assert.That(snapshot).IsNotNull();
        await Assert.That(snapshot!.Limit).IsEqualTo(budget);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task PipeWindow_IsReservedForEachOpenPipe(ClientTarget target)
    {
        const int window = 32 * 1024;
        await using var server = await StartServerAsync(nexus =>
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask);
        await using var client = await ConnectAsync(target, server, configure: spec => spec.PipeWindow = window);
        var baseline = (await client.GetBudgetAsync().Timeout(T(1)))!.Reserved;

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await Assert.That(await WaitForReservedAsync(client, baseline + window)).IsEqualTo(baseline + window);
        }

        // Released once both sides closed the pipe.
        await Assert.That(await WaitForReservedAsync(client, baseline)).IsEqualTo(baseline);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task MaxPipes_RefusesAPipeOverTheLimit_AndTheSessionStaysUp(ClientTarget target)
    {
        await using var server = await StartServerAsync(nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask;
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await using var client = await ConnectAsync(target, server, configure: spec => spec.MaxPipes = 1);
        var baseline = (await client.GetBudgetAsync().Timeout(T(1)))!.Reserved;

        await using (var first = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(first).Timeout(T(1));
            await first.WhenReadyAsync().AsTask().Timeout(T(1));

            // The second pipe is refused locally: it never reaches the server.
            await Assert.ThrowsExactlyAsync<NexusBusyException>(async () =>
            {
                await using var second = await client.CreatePipeAsync();
                await Proxy(client).ServerTaskValueWithDuplexPipe(second).Timeout(T(1));
            });
            await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(1);
        }

        // The slot frees once both sides closed the first pipe, which also releases its window.
        await WaitForReservedAsync(client, baseline);
        await using (var again = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(again).Timeout(T(5));
            await again.WhenReadyAsync().AsTask().Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task LogLevel_Warning_DeliversThePlaintextWarning(ClientTarget target)
    {
        var logger = new CapturingLogger();
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            spec.Logger = logger;
            spec.LogLevel = NexusLogLevel.Warning;
        });

        await WaitUntil(() => logger.Warnings.Any(w => w.Contains("unencrypted transport", StringComparison.Ordinal)));
        await Assert.That(logger.Warnings.Count(w => w.Contains("unencrypted transport", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(logger.Messages.Any(m => m.Level < NexusLogLevel.Warning)).IsFalse();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task LogLevel_Error_FiltersOutWarnings(ClientTarget target)
    {
        var logger = new CapturingLogger();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1));
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            spec.Logger = logger;
            spec.LogLevel = NexusLogLevel.Error;
        });

        // A round trip after the connect: a warning logged at connect would have arrived by now.
        await RoundTripAsync(client);
        await Assert.That(logger.Messages.Where(m => m.Level < NexusLogLevel.Error).Select(m => m.Message)).IsEmpty();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls], Requires = ClientCapabilities.CustomCa)]
    public async Task InsecureSkipVerify_AcceptsACertificateTheClientDoesNotTrust(ClientTarget target)
    {
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1));

        // No CA: the test server's certificate chains to the F08 test CA, which no system store trusts.
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            spec.CaPem = null;
            spec.InsecureSkipVerify = true;
        });

        await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task InvocationQueueWarningThreshold_WarnsWhenAnInvocationWaitsLonger(ClientTarget target)
    {
        if (target.Language == ClientLanguages.Java)
            Skip.Test("The Java client has no invocation queue warning (invocationQueueWarningThreshold); follow-up card.");

        var logger = new CapturingLogger();
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            script => script.ClientTaskWithParam.Delay(300),
            spec =>
            {
                spec.Logger = logger;
                spec.LogLevel = NexusLogLevel.Warning;
                spec.MaxConcurrentConnectionInvocations = 1;
                spec.InvocationQueueWarningThresholdMs = 50;
            });

        // One execution slot: the second call waits about 300 ms for it.
        using (var context = server.Server.ContextProvider.Rent())
        {
            await context.Proxy.All.ClientTaskWithParam(1).Timeout(T(1));
            await context.Proxy.All.ClientTaskWithParam(2).Timeout(T(1));
        }

        await Script(client).ClientTaskWithParam.NextCallAsync().AsTask().Timeout(T(1));
        await Script(client).ClientTaskWithParam.NextCallAsync().AsTask().Timeout(T(1));
        await WaitUntil(() => logger.Warnings.Any(w => w.Contains("for an execution slot", StringComparison.Ordinal)));
        await Assert.That(logger.Warnings.Any(w => w.Contains("threshold 50 ms", StringComparison.Ordinal))).IsTrue();
        await EndAsync(client);
    }

    /// <summary>Waits, for at most <c>T(1)</c>, until the client's budget reserves <paramref name="expected"/>; returns the last value.</summary>
    private static async Task<long> WaitForReservedAsync(IClientUnderTest client, long expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(T(1));
        var reserved = (await client.GetBudgetAsync().Timeout(T(1)))!.Reserved;
        while (reserved != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            reserved = (await client.GetBudgetAsync().Timeout(T(1)))!.Reserved;
        }

        return reserved;
    }
}
