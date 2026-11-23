using NexNet.Pipes;

namespace NexNet.Inspector.Sample;

/// <summary>How much of the Chat scenario a client runs.</summary>
public enum ChatScenarioKind : byte
{
    /// <summary>
    /// Everything the inspect-live and inspect-session-start screens show: Users collection, Join, a SendMessage
    /// canceled mid-call, a small upload, a five-chunk SendAttachment interleaved with a FileUpload pipe and pings, a
    /// LogLine channel and an optional burst that a stalled Drop-mode recorder turns into a gap.
    /// </summary>
    Full = 0,

    /// <summary>A short session: Users collection, Join, messages (one canceled), a small upload and a few log lines.</summary>
    Light = 1,
}

/// <summary>Settings of one scenario run.</summary>
public sealed class ChatScenarioOptions
{
    /// <summary>Which steps run.</summary>
    public ChatScenarioKind Kind { get; init; } = ChatScenarioKind.Full;

    /// <summary>The user name the client joins with.</summary>
    public string User { get; init; } = "ada";

    /// <summary>Attachment size: above 4 × 65,535 bytes it streams as five MessageChunks (spec §6.1).</summary>
    public int AttachmentBytes { get; init; } = 300_000;

    /// <summary>FileUpload writes made while the attachment streams.</summary>
    public int UploadWrites { get; init; } = 6;

    /// <summary>Bytes per FileUpload write.</summary>
    public int UploadWriteBytes { get; init; } = 4096;

    /// <summary>Pause between FileUpload writes.</summary>
    public TimeSpan UploadInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Log lines of the TailLogs channel.</summary>
    public int LogLines { get; init; } = 12;

    /// <summary>Log lines of the burst; 0 skips it.</summary>
    public int BurstLines { get; init; }

    /// <summary>Called before the burst (the fixture generator stalls the server recorder here).</summary>
    public Func<Task>? BeforeBurst { get; init; }

    /// <summary>Called after the burst was read; the run goes on when it completes.</summary>
    public Func<Task>? AfterBurst { get; init; }

    /// <summary>How long one step may take before the run fails.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Drives a connected Chat client through the screen scenarios.</summary>
/// <remarks>The Full scenario makes four SendMessage calls (<see cref="FullMessages"/>), the second one canceled.</remarks>
public static class ChatScenario
{
    /// <summary>
    /// The clients' auth token: 96 bytes, so the server's first read (preamble and ClientGreeting) carries 96 redacted
    /// bytes, as in the inspect-session-start screen.
    /// </summary>
    public static readonly byte[] Token = CreateToken();

    /// <summary>Ping interval of the scenario clients: short enough that a ping lands inside the paced attachment.</summary>
    public const int PingInterval = 400;

    /// <summary>SendMessage calls of every scenario run.</summary>
    public const int FullMessages = 4;

    /// <summary>The text whose SendMessage the server holds until the client cancels it.</summary>
    public const string SlowText = "/slow typing…";

    /// <summary>Runs the scenario on a connected client.</summary>
    /// <param name="client">The connected client.</param>
    /// <param name="pacer">The client's chunk pacer, or null to send the attachment at full speed.</param>
    /// <param name="options">What to run.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    public static async Task RunAsync(NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy> client, ChunkPacer? pacer,
        ChatScenarioOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var proxy = client.Proxy;
        var timeout = options.StepTimeout;

        // Pipe 1: the Users collection opens first, so later joins arrive as Insert messages.
        if (!await proxy.Users.EnableAsync(cancellationToken).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The Users collection did not enable.");
        await proxy.Users.ReadyTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        await proxy.Join(options.User).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        await SendAsync(proxy, options, "hello everyone", cancellationToken).ConfigureAwait(false);

        // The canceled call: the server holds it until the client's InvocationCancellation arrives.
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var slow = proxy.SendMessage(Message(options, SlowText), cancel.Token).AsTask();
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            await cancel.CancelAsync().ConfigureAwait(false);
            try
            {
                await slow.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Expected: the call was canceled.
            }
        }

        // Pipe 2: a small upload that opens and closes.
        await UploadAsync(client, options, "avatar.png", writes: 1, options.UploadWriteBytes / 2, TimeSpan.Zero, cancellationToken)
            .ConfigureAwait(false);

        if (options.Kind == ChatScenarioKind.Full)
            await AttachmentAsync(client, pacer, options, cancellationToken).ConfigureAwait(false);

        await SendAsync(proxy, options, "logs, anyone?", cancellationToken).ConfigureAwait(false);

        // Pipe 4: the LogLine channel.
        var lines = options.Kind == ChatScenarioKind.Full ? options.LogLines : Math.Min(options.LogLines, 4);
        await TailAsync(client, ChatLogLevel.Info, lines, timeout, cancellationToken).ConfigureAwait(false);

        if (options.Kind == ChatScenarioKind.Full && options.BurstLines > 0)
        {
            if (options.BeforeBurst != null)
                await options.BeforeBurst().ConfigureAwait(false);
            try
            {
                await TailAsync(client, ChatLogLevel.Trace, options.BurstLines, timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (options.AfterBurst != null)
                    await options.AfterBurst().ConfigureAwait(false);
            }
        }

        await SendAsync(proxy, options, "bye", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The attachment, deterministic for a given size: a repeating pattern compresses well in git.</summary>
    public static byte[] AttachmentData(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);
        return data;
    }

    // Pipe 3 streams while the attachment's chunks go out, paced so the two interleave on the wire. The pipe opens
    // first, so its writes, not its open handshake, are what lands between the chunks.
    private static async Task AttachmentAsync(NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy> client,
        ChunkPacer? pacer, ChatScenarioOptions options, CancellationToken cancellationToken)
    {
        await using var pipe = await OpenUploadAsync(client, options, "trace.bin", cancellationToken).ConfigureAwait(false);
        var upload = WriteUploadAsync(pipe, options, options.UploadWrites, options.UploadWriteBytes, options.UploadInterval,
            cancellationToken);
        if (pacer != null)
            pacer.Enabled = true;
        try
        {
            var attachment = new Attachment
            {
                Room = "general",
                FileName = "screenshot.png",
                ContentType = "image/png",
                Data = AttachmentData(options.AttachmentBytes),
            };
            var receipt = await client.Proxy.SendAttachment(attachment).AsTask()
                .WaitAsync(options.StepTimeout, cancellationToken).ConfigureAwait(false);
            if (receipt.Length != options.AttachmentBytes)
                throw new InvalidOperationException($"The server stored {receipt.Length} attachment bytes.");
        }
        finally
        {
            if (pacer != null)
                pacer.Enabled = false;
        }

        await upload.ConfigureAwait(false);
    }

    private static async Task UploadAsync(NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy> client,
        ChatScenarioOptions options, string fileName, int writes, int bytesPerWrite, TimeSpan interval,
        CancellationToken cancellationToken)
    {
        await using var pipe = await OpenUploadAsync(client, options, fileName, cancellationToken).ConfigureAwait(false);
        await WriteUploadAsync(pipe, options, writes, bytesPerWrite, interval, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IRentedNexusDuplexPipe> OpenUploadAsync(NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy> client,
        ChatScenarioOptions options, string fileName, CancellationToken cancellationToken)
    {
        var timeout = options.StepTimeout;
        var pipe = client.CreatePipe();
        try
        {
            await client.Proxy.UploadFile(fileName, pipe).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            await pipe.ReadyTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteUploadAsync(IRentedNexusDuplexPipe pipe, ChatScenarioOptions options, int writes, int bytesPerWrite,
        TimeSpan interval, CancellationToken cancellationToken)
    {
        var timeout = options.StepTimeout;
        var chunk = new byte[bytesPerWrite];
        for (var i = 0; i < writes; i++)
        {
            chunk.AsSpan().Fill((byte)('a' + (i % 26)));
            await pipe.Output.WriteAsync(chunk, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (interval > TimeSpan.Zero && i < writes - 1)
                await PreciseDelay.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
        }

        await pipe.Output.CompleteAsync().ConfigureAwait(false);
        await pipe.CompleteTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TailAsync(NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy> client, ChatLogLevel minimum,
        int lines, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var channel = client.CreateChannel<LogLine>();
        await using (channel.ConfigureAwait(false))
        {
            await client.Proxy.TailLogs(minimum, lines, channel).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            var read = await channel.ReadUntilComplete(lines, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            if (read.Count != lines)
                throw new InvalidOperationException($"Read {read.Count} of {lines} log lines.");
        }
    }

    private static async Task SendAsync(ChatClientNexus.ServerProxy proxy, ChatScenarioOptions options, string text,
        CancellationToken cancellationToken)
    {
        await proxy.SendMessage(Message(options, text), cancellationToken).AsTask()
            .WaitAsync(options.StepTimeout, cancellationToken).ConfigureAwait(false);
    }

    // Fixed timestamps keep the recorded bytes the same from run to run; only timing differs.
    private static ChatMessage Message(ChatScenarioOptions options, string text) => new()
    {
        Room = "general",
        Author = options.User,
        Text = text,
        SentUtc = new DateTime(2026, 11, 22, 12, 4, 0, DateTimeKind.Utc),
    };

    private static byte[] CreateToken()
    {
        var token = new byte[96];
        var seed = "nexnet-inspector-chat-sample-token:"u8;
        for (var i = 0; i < token.Length; i++)
            token[i] = (byte)(seed[i % seed.Length] ^ (i * 7));
        return token;
    }
}
