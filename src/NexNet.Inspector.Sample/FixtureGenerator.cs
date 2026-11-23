using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Sample;

/// <summary>
/// Records the fixture files by running the Chat scenarios against real in-process servers and clients over loopback
/// TCP, then checks each recording with <see cref="Fixtures.Check"/>. A run whose timing missed (no ping between the
/// attachment's chunks, no gap) is repeated.
/// </summary>
public static class FixtureGenerator
{
    /// <summary>Token of the inspector endpoint used for the record-all fixture.</summary>
    public static readonly byte[] EndpointToken = "sample-endpoint-token"u8.ToArray();

    // Ring of the server recorder in the Chat run: larger than any single op (a 65 KB chunk read), smaller than the
    // burst, so stalling the recorder during the burst drops ops into a gap.
    private const int ServerRingCapacity = 96 * 1024;
    private const int BurstLines = 768;
    private const int Attempts = 5;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>Records every fixture.</summary>
    /// <param name="log">Progress output.</param>
    /// <param name="cancellationToken">Stops the runs.</param>
    public static async Task<Dictionary<FixtureKind, byte[]>> GenerateAsync(TextWriter log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var files = new Dictionary<FixtureKind, byte[]>();

        var (server, client) = await RetryAsync(log, "chat", async () =>
        {
            var run = await RecordChatAsync(cancellationToken).ConfigureAwait(false);
            return (run, Problems(FixtureKind.ChatServer, run.Server).Concat(Problems(FixtureKind.ChatClient, run.Client)).ToList());
        }).ConfigureAwait(false);
        files[FixtureKind.ChatServer] = server;
        files[FixtureKind.ChatClient] = client;

        files[FixtureKind.ServerAll] = await RetryAsync(log, "server-all", async () =>
        {
            var bytes = await RecordServerAllAsync(cancellationToken).ConfigureAwait(false);
            return (bytes, Problems(FixtureKind.ServerAll, bytes));
        }).ConfigureAwait(false);

        files[FixtureKind.LegacySchema] = await RetryAsync(log, "legacy-schema", async () =>
        {
            var bytes = await RecordLegacyAsync(cancellationToken).ConfigureAwait(false);
            return (bytes, Problems(FixtureKind.LegacySchema, bytes));
        }).ConfigureAwait(false);

        var truncated = Truncate(files[FixtureKind.ServerAll]);
        var truncatedProblems = Problems(FixtureKind.Truncated, truncated);
        if (truncatedProblems.Count > 0)
            throw new InvalidOperationException("The truncated fixture fails its checks: " + string.Join("; ", truncatedProblems));
        files[FixtureKind.Truncated] = truncated;
        return files;
    }

    /// <summary>Records every fixture into <paramref name="directory"/>.</summary>
    public static async Task WriteAsync(string directory, TextWriter log, CancellationToken cancellationToken = default)
    {
        var files = await GenerateAsync(log, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        foreach (var kind in Fixtures.All)
        {
            var path = Path.Combine(directory, Fixtures.FileName(kind));
            await File.WriteAllBytesAsync(path, files[kind], cancellationToken).ConfigureAwait(false);
            log.WriteLine($"{path}: {files[kind].Length:N0} bytes");
        }
    }

    /// <summary>
    /// A crashed copy of a recording: no footer, and the last block cut in half (a torn write), as a recorder killed
    /// mid-write leaves it.
    /// </summary>
    public static byte[] Truncate(byte[] complete)
    {
        using var reader = NntlReader.Open(new MemoryStream(complete, writable: false));
        var blocks = reader.Index.ToFileOrder();
        var last = reader.ReadBlock(blocks[^1].Offset);
        return complete.AsSpan(0, (int)(last.Offset + (last.Length / 2))).ToArray();
    }

    /// <summary>
    /// The Full scenario with both sides recording: the server into a Drop-mode recording that is stalled during the
    /// burst (the gap), the client into a Block-mode recording (lossless, for Compare).
    /// </summary>
    public static async Task<(byte[] Server, byte[] Client)> RecordChatAsync(CancellationToken cancellationToken = default)
    {
        var address = SampleAddress.Loopback(SampleTransport.Tcp);
        var serverConfig = address.CreateServerConfig();
        serverConfig.Authenticate = true;
        var serverInspector = serverConfig.UseInspector<ChatServerNexus>(
            new InspectorOptions { AllowRecording = true, RingCapacity = ServerRingCapacity });
        var server = ChatServerNexus.CreateServer(serverConfig, static () => new ChatServerNexus());
        serverInspector.UseSnapshotsOf(server);
        var serverFile = new GateStream();
        var serverRecording = serverInspector.RecordTo(serverFile, leaveOpen: true, InspectorOverflow.Drop);

        var clientConfig = CreateClientConfig(address);
        var clientInspector = clientConfig.UseInspector<ChatClientNexus>(new InspectorOptions { AllowRecording = true });
        var pacer = new ChunkPacer(clientInspector);
        clientConfig.Tap = pacer;
        var client = ChatClientNexus.CreateClient(clientConfig, new ChatClientNexus());
        clientInspector.UseSnapshotsOf(client);
        var clientFile = new GateStream();
        var clientRecording = clientInspector.RecordTo(clientFile, leaveOpen: true, InspectorOverflow.Block);

        try
        {
            await server.StartAsync(cancellationToken).WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(cancellationToken).WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await ChatScenario.RunAsync(client, pacer, new ChatScenarioOptions
            {
                Kind = ChatScenarioKind.Full,
                AttachmentBytes = Fixtures.AttachmentBytes,
                LogLines = Fixtures.LogLines,
                BurstLines = BurstLines,
                BeforeBurst = () =>
                {
                    serverFile.Hold();
                    return Task.CompletedTask;
                },
                AfterBurst = () => serverFile.ReleaseAndDrainAsync(TimeSpan.FromMilliseconds(100), cancellationToken),
            }, cancellationToken).ConfigureAwait(false);

            await client.DisconnectAsync().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await WaitForNoSessionsAsync(serverInspector, cancellationToken).ConfigureAwait(false);
            await WaitForNoSessionsAsync(clientInspector, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            serverFile.Release();
            await serverRecording.StopAsync().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await clientRecording.StopAsync().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            await server.StopAsync().ConfigureAwait(false);
            await serverInspector.DisposeAsync().ConfigureAwait(false);
            await clientInspector.DisposeAsync().ConfigureAwait(false);
        }

        return (serverFile.ToArray(), clientFile.ToArray());
    }

    /// <summary>
    /// Record-all through an inspector endpoint, as the app's "Record all sessions" does: one session already connected
    /// (it starts with a snapshot), two more recorded from their first byte.
    /// </summary>
    public static async Task<byte[]> RecordServerAllAsync(CancellationToken cancellationToken = default)
    {
        var address = SampleAddress.Loopback(SampleTransport.Tcp);
        var endpointAddress = SampleAddress.Loopback(SampleTransport.Tcp);
        var serverConfig = address.CreateServerConfig();
        serverConfig.Authenticate = true;        var inspector = serverConfig.UseInspector<ChatServerNexus>(new InspectorOptions { AllowRecording = true });
        var server = ChatServerNexus.CreateServer(serverConfig, static () => new ChatServerNexus());
        inspector.UseSnapshotsOf(server);
        var clients = new List<NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy>>();
        InspectorEndpoint? endpoint = null;
        InspectorClient? viewer = null;
        InspectorStream? stream = null;
        var file = new MemoryStream();

        try
        {
            await server.StartAsync(cancellationToken).WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            endpoint = await inspector.StartEndpointAsync(endpointAddress.CreateServerConfig(), EndpointToken, cancellationToken)
                .WaitAsync(Wait, cancellationToken).ConfigureAwait(false);

            var first = await ConnectAsync(address, clients, cancellationToken).ConfigureAwait(false);
            await first.Proxy.Users.EnableAsync(cancellationToken).AsTask().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await first.Proxy.Join("grace").AsTask().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);

            viewer = await InspectorClient.ConnectAsync(endpointAddress.CreateClientConfig(), EndpointToken, cancellationToken)
                .WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            stream = await viewer.RecordAllAsync(new InspectorRecordRequest(InspectorOverflow.Block, Snapshot: true), cancellationToken)
                .WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            var copy = stream.CopyToAsync(file, cancellationToken);

            var second = await ConnectAsync(address, clients, cancellationToken).ConfigureAwait(false);
            var third = await ConnectAsync(address, clients, cancellationToken).ConfigureAwait(false);
            await ChatScenario.RunAsync(second, null, new ChatScenarioOptions { Kind = ChatScenarioKind.Light, User = "ada" }, cancellationToken)
                .ConfigureAwait(false);
            await ChatScenario.RunAsync(third, null, new ChatScenarioOptions { Kind = ChatScenarioKind.Light, User = "linus" }, cancellationToken)
                .ConfigureAwait(false);
            await first.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "grace", Text = "welcome both" }, cancellationToken)
                .AsTask().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);

            foreach (var client in clients)
                await client.DisconnectAsync().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await WaitForNoSessionsAsync(inspector, cancellationToken).ConfigureAwait(false);

            // Detaching makes the endpoint finish the stream: close blocks, then the index footer.
            await viewer.DetachAsync().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await copy.WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (stream != null)
                await stream.DisposeAsync().ConfigureAwait(false);
            if (viewer != null)
                await viewer.DisposeAsync().ConfigureAwait(false);
            foreach (var client in clients)
                await client.DisposeAsync().ConfigureAwait(false);
            if (endpoint != null)
                await endpoint.DisposeAsync().ConfigureAwait(false);
            await server.StopAsync().ConfigureAwait(false);
            await inspector.DisposeAsync().ConfigureAwait(false);
        }

        return file.ToArray();
    }

    /// <summary>Two sessions of the legacy Chat API, recorded server side from inception.</summary>
    public static async Task<byte[]> RecordLegacyAsync(CancellationToken cancellationToken = default)
    {
        var address = SampleAddress.Loopback(SampleTransport.Tcp);
        var serverConfig = address.CreateServerConfig();
        serverConfig.Authenticate = true;
        var inspector = serverConfig.UseInspector<LegacyChatServerNexus>(new InspectorOptions { AllowRecording = true });
        var server = LegacyChatServerNexus.CreateServer(serverConfig, static () => new LegacyChatServerNexus());
        var file = new GateStream();
        var recording = inspector.RecordTo(file, leaveOpen: true, InspectorOverflow.Block);

        try
        {
            await server.StartAsync(cancellationToken).WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < 2; i++)
            {
                var config = CreateClientConfig(address);
                await using var client = LegacyChatClientNexus.CreateClient(config, new LegacyChatClientNexus());
                await client.ConnectAsync(cancellationToken).WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
                await client.Proxy.Users.EnableAsync(cancellationToken).AsTask().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
                await client.Proxy.SendMessage("general", "hello from v1", cancellationToken).AsTask()
                    .WaitAsync(Wait, cancellationToken).ConfigureAwait(false);

                await using (var pipe = client.CreatePipe())
                {
                    await client.Proxy.UploadFile("notes.txt", pipe).AsTask().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
                    await pipe.ReadyTask.WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
                    await pipe.Output.WriteAsync("legacy upload"u8.ToArray(), cancellationToken).AsTask()
                        .WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
                    await pipe.Output.CompleteAsync().ConfigureAwait(false);
                    await pipe.CompleteTask.WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
                }

                await client.Proxy.SendMessage("general", "bye from v1", cancellationToken).AsTask()
                    .WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
                await client.DisconnectAsync().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
                await WaitForNoSessionsAsync(inspector, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await recording.StopAsync().WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            await server.StopAsync().ConfigureAwait(false);
            await inspector.DisposeAsync().ConfigureAwait(false);
        }

        return file.ToArray();
    }

    /// <summary>A scenario client's configuration: the 96-byte token and the scenario's ping interval.</summary>
    public static Transports.ClientConfig CreateClientConfig(SampleAddress address)
    {
        var config = address.CreateClientConfig();
        config.Authenticate = static () => ChatScenario.Token;
        config.PingInterval = ChatScenario.PingInterval;
        return config;
    }

    private static List<string> Problems(FixtureKind kind, byte[] file) => Fixtures.Check(kind, RecordingAnalysis.Read(file));

    private static async Task<NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy>> ConnectAsync(SampleAddress address,
        List<NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy>> clients, CancellationToken cancellationToken)
    {
        var client = ChatClientNexus.CreateClient(CreateClientConfig(address), new ChatClientNexus());
        clients.Add(client);
        await client.ConnectAsync(cancellationToken).WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
        return client;
    }

    private static async Task WaitForNoSessionsAsync(NexusInspector inspector, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (inspector.GetSessions().Length > 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("A session did not close.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> RetryAsync<T>(TextWriter log, string name, Func<Task<(T Result, List<string> Problems)>> run)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (result, problems) = await run().ConfigureAwait(false);
            if (problems.Count == 0)
                return result;
            log.WriteLine($"{name}: attempt {attempt} missed: {string.Join("; ", problems)}");
            if (attempt == Attempts)
                throw new InvalidOperationException($"{name}: no attempt produced a valid recording.");
        }
    }
}
