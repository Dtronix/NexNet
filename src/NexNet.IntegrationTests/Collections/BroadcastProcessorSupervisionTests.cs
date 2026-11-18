using System.Collections.Concurrent;
using System.Threading.Channels;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Logging;
using NexNet.Pipes.Broadcast;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// After an unexpected error stops a broadcast message processor (S09), the server restarts it and resets its
/// clients, and the client closes the collection and reports the failure, so no collection stays silently dead (S11).
/// </summary>
internal class BroadcastProcessorSupervisionTests : NexusCollectionBaseTests
{
    [Test]
    public async Task Server_ProcessorStoppedByError_RestartsAndProcesses()
    {
        var logger = new FaultInjectingLogger();
        var list = new NexusListServer<int>(0, NexusCollectionMode.BiDirectional, logger);
        list.Start();

        try
        {
            FailNextProcess(list);
            Assert.That(await list.AddAsync(1).Timeout(1), Is.False);

            // Rejected while the processor is down, then processed again once it has been restarted.
            Assert.That(await AddWhenRunning(list, 2), Is.True);
            Assert.That(list, Is.EquivalentTo([2]));
            Assert.That(logger.Errors.Any(m => m.Contains("stopped unexpectedly")), Is.True);
        }
        finally
        {
            list.Stop();
        }
    }

    [Test]
    public async Task Server_ProcessorStoppingRepeatedly_GivesUpAndStops()
    {
        var logger = new FaultInjectingLogger();
        var list = new NexusListServer<int>(0, NexusCollectionMode.BiDirectional, logger);
        var broadcaster = (NexusBroadcastServer<INexusCollectionListMessage>)(object)list;
        broadcaster.ProcessorRestartBackoff = TimeSpan.FromMilliseconds(1);
        list.Start();

        try
        {
            // One stop more than the restart budget allows within the window.
            for (var i = 0; i <= NexusBroadcastServer<INexusCollectionListMessage>.MaxProcessorRestartsPerWindow; i++)
            {
                FailNextProcess(list);
                Assert.That(await AddWhenRunning(list, i), Is.False);
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (broadcaster.IsRunning && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.That(broadcaster.IsRunning, Is.False);
            Assert.That(logger.Errors.Any(m => m.Contains("stopping the collection")), Is.True);

            // Stopped for good: the collection rejects changes instead of silently dropping them.
            await Task.Delay(50);
            await AssertThrows<ChannelClosedException>(() => list.AddAsync(100).Timeout(1));

            // A new start brings it back.
            list.Stop();
            list.Start();
            Assert.That(await AddWhenRunning(list, 200), Is.True);
        }
        finally
        {
            list.Stop();
        }
    }

    [Test]
    public async Task Server_ProcessorStoppedByError_ClientsResynchronize()
    {
        var serverLogger = new FaultInjectingLogger(Logger);
        var (server, client, _) = CreateServerClient(
            CreateServerConfigWithLog(Type.Uds, serverLogger),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        var serverNexus = server.NexusCreatedQueue.First();
        var list = client.Proxy.IntListBi;

        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        await list.ReadyTask.Timeout(1);
        Assert.That(await list.AddAsync(1).Timeout(1), Is.True);

        var disabled = list.DisabledTask;
        FailNextProcess(serverNexus.IntListBi);

        // The change is lost with the processor; the client is reset instead of waiting forever for its ack.
        Assert.That(await list.AddAsync(2).Timeout(2), Is.False);
        await disabled.Timeout(2);
        Assert.That(serverLogger.Errors.Any(m => m.Contains("stopped unexpectedly")), Is.True);

        // Re-enabling resynchronizes against the restarted processor.
        await EnableUntilWorking(list, 3);

        Assert.Multiple(() =>
        {
            Assert.That(list, Is.EquivalentTo([1, 3]));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 3]));
        });
    }

    [Test]
    public async Task Client_ProcessorStoppedByError_DisablesCollection()
    {
        var clientLogger = new FaultInjectingLogger(Logger);
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfigWithLog(Type.Uds, clientLogger));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        var serverNexus = server.NexusCreatedQueue.First();
        var list = client.Proxy.IntListBi;

        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        await list.ReadyTask.Timeout(1);

        var disabled = list.DisabledTask;
        FailNextProcess(list);

        // The broadcast of this change stops the client's processor.
        Assert.That(await serverNexus.IntListBi.AddAsync(5).Timeout(1), Is.True);

        await disabled.Timeout(2);
        Assert.That(clientLogger.Errors.Any(m => m.Contains("stopped unexpectedly")), Is.True);

        // Enabling again resynchronizes.
        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        await list.ReadyTask.Timeout(1);
        Assert.That(list, Is.EquivalentTo([5]));
    }

    [Test]
    public async Task Client_ProcessorStoppedByErrorDuringEnable_EnableThrows()
    {
        var clientLogger = new FaultInjectingLogger(Logger);
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfigWithLog(Type.Uds, clientLogger));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        var serverNexus = server.NexusCreatedQueue.First();
        await serverNexus.IntListBi.AddAsync(7).Timeout(1);
        var list = client.Proxy.IntListBi;

        // The first message of the initial synchronization stops the client's processor.
        FailNextProcess(list);
        await AssertThrows<InvalidOperationException>(() => list.EnableAsync().AsTask().Timeout(1));
        Assert.That(clientLogger.Errors.Any(m => m.Contains("stopped unexpectedly")), Is.True);

        // A retry is a fresh enable.
        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        await list.ReadyTask.Timeout(1);
        Assert.That(list, Is.EquivalentTo([7]));
    }

    /// <summary>
    /// Makes the next message processed by the collection's broadcast processor throw. With
    /// <see cref="FaultInjectingLogger"/> the error then escapes the per-message handling and stops the processor.
    /// </summary>
    private static void FailNextProcess(INexusList<int> list)
    {
        var armed = 1;
        Action<INexusCollectionListMessage> hook = _ =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
                throw new InjectedProcessingException();
        };

        switch ((object)list)
        {
            case NexusBroadcastServer<INexusCollectionListMessage> server:
                server.ProcessTestHook = hook;
                break;
            case NexusBroadcastClient<INexusCollectionListMessage> client:
                client.ProcessTestHook = hook;
                break;
            default:
                throw new InvalidOperationException($"Unexpected collection type {list.GetType()}.");
        }
    }

    private sealed class InjectedProcessingException() : Exception("Injected processing failure.");

    /// <summary>
    /// Adds an item, retrying while the processor is down (between its stop and its restart).
    /// </summary>
    private static async Task<bool> AddWhenRunning(INexusList<int> list, int item)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return await list.AddAsync(item).Timeout(1);
            }
            catch (ChannelClosedException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
    }

    /// <summary>
    /// Enables the collection and adds an item, again if the server reset the collection meanwhile (it resets its
    /// clients once more after the restart).
    /// </summary>
    private static async Task EnableUntilWorking(INexusList<int> list, int item)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                if (await list.EnableAsync().Timeout(1))
                {
                    await list.ReadyTask.Timeout(1);
                    if (await list.AddAsync(item).Timeout(1))
                        return;
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
            }

            if (DateTime.UtcNow >= deadline)
                Assert.Fail("The collection did not recover after the server processor restarted.");

            await list.DisabledTask.Timeout(1);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Forwards to an optional inner logger and records errors. Its broadcast processor loggers throw when they log
    /// an <see cref="InjectedProcessingException"/>: that throw escapes the processor's per-message error handling
    /// and stops its loop, the way an unexpected error does.
    /// </summary>
    private sealed class FaultInjectingLogger : INexusLogger
    {
        private readonly SharedState _state;
        private readonly INexusLogger? _inner;
        private readonly bool _isProcessor;

        public FaultInjectingLogger(INexusLogger? inner = null) : this(new SharedState(), inner, false)
        {
        }

        private FaultInjectingLogger(SharedState state, INexusLogger? inner, bool isProcessor)
        {
            _state = state;
            _inner = inner;
            _isProcessor = isProcessor;
        }

        public IEnumerable<string> Errors => _state.Errors;
        public NexusLogBehaviors Behaviors { get; set; }
        public string FormattedPath => _inner?.FormattedPath ?? PathSegment ?? "";
        public string? PathSegment { get; set; }

        public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
        {
            if (logLevel >= NexusLogLevel.Error)
                _state.Errors.Enqueue(message);

            _inner?.Log(logLevel, category, exception, message);

            if (_isProcessor && exception is InjectedProcessingException)
                throw new InvalidOperationException("Injected logger failure.");
        }

        public INexusLogger CreateLogger(string? pathSegment = null) =>
            new FaultInjectingLogger(_state, _inner?.CreateLogger(pathSegment), _isProcessor || pathSegment == "Processor")
            {
                PathSegment = pathSegment
            };

        private sealed class SharedState
        {
            public readonly ConcurrentQueue<string> Errors = new();
        }
    }
}
