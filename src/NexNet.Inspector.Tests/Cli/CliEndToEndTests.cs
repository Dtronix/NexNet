using System.Text;
using System.Text.Json;
using NexNet.Inspector.Cli;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;
using NexNet.Inspector.Tests.Tap;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>nexnet-tap list/record/info against an in-process tapped server and its inspector endpoint.</summary>
public class CliEndToEndTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly string Token = Encoding.UTF8.GetString(TapHarness.EndpointToken);

    /// <summary>One CLI invocation running in the background, with its own output and error writers.</summary>
    private sealed class CliRun
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CliRun(string[] args, CancellationToken cancellationToken = default)
        {
            var cli = new TapCli(Output, Error) { RecordingStarted = () => _started.TrySetResult() };
            Completion = Task.Run(() => cli.RunAsync(args, null, cancellationToken));
        }

        public StringWriter Output { get; } = new();

        public StringWriter Error { get; } = new();

        public Task<int> Completion { get; }

        /// <summary>Waits until record has its stream, or fails with what the CLI printed if it ended first.</summary>
        public async Task StartedAsync()
        {
            var first = await Task.WhenAny(_started.Task, Completion).WaitAsync(Wait);
            if (first == Completion)
                throw new InvalidOperationException($"nexnet-tap exited {Completion.Result}: {Error}{Output}");
        }

        public async Task<int> ExitAsync() => await Completion.WaitAsync(Wait);
    }

    private static string[] Connect(TapHarness h, TapTransport transport, params string[] args)
    {
        string[] common = transport == TapTransport.TcpTls
            ? ["--endpoint", h.EndpointAddress!, "--token", Token, "--insecure"]
            : ["--endpoint", h.EndpointAddress!, "--token", Token];
        return [args[0], .. common, .. args[1..]];
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"nntap-cli-{Guid.NewGuid():N}.nntl");

    private static RecordingSummary Info(string path, bool messages = true)
    {
        using var stream = File.OpenRead(path);
        return RecordingAnalyzer.Analyze(stream, Path.GetFileName(path), messages);
    }

    /// <summary>Counts a session's op blocks independently of the analyzer, straight from the reader.</summary>
    private static (long Ops, long Bytes) CountOps(string path, long sessionId, TapDirection direction)
    {
        using var reader = NntlReader.Open(File.OpenRead(path));
        long ops = 0, bytes = 0;
        foreach (var op in reader.ReadTransportOps(sessionId))
        {
            if (op.Direction != direction)
                continue;
            ops++;
            bytes += op.Bytes.Length;
        }

        return (ops, bytes);
    }

    private static async Task AssertMatchesReader(string path, SessionSummary session)
    {
        await Assert.That((session.Inbound.Ops, session.Inbound.Bytes)).IsEqualTo(CountOps(path, session.Id, TapDirection.Inbound));
        await Assert.That((session.Outbound.Ops, session.Outbound.Bytes)).IsEqualTo(CountOps(path, session.Id, TapDirection.Outbound));
    }

    [Test]
    [Arguments(TapTransport.Tcp)]
    [Arguments(TapTransport.TcpTls)]
    public async Task List_shows_the_policy_and_the_sessions(TapTransport transport)
    {
        await using var h = new TapHarness(transport);
        await h.StartAsync();
        await h.StartEndpointAsync(transport);
        await h.ConnectClientAsync();
        await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync(2);

        var run = new CliRun(Connect(h, transport, "list"));
        await Assert.That(await run.ExitAsync()).IsEqualTo((int)ExitCode.Ok);

        var text = run.Output.ToString();
        await Assert.That(text).Contains("Inspected  server");
        await Assert.That(text).Contains("Recording disabled by server config");
        await Assert.That(text).Contains("Secrets    redacted");
        await Assert.That(text).Contains("Sessions   2");
        await Assert.That(text).Contains(session.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(text).Contains(Convert.ToHexStringLower(SchemaHashOf()));
    }

    [Test]
    public async Task A_wrong_token_fails_with_1()
    {
        await using var h = new TapHarness(TapTransport.Tcp);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);

        var run = new CliRun(["list", "--endpoint", h.EndpointAddress!, "--token", "wrong"]);
        await Assert.That(await run.ExitAsync()).IsEqualTo((int)ExitCode.Failed);
        await Assert.That(run.Error.ToString()).Contains("refused the token");
    }

    [Test]
    public async Task An_unvalidated_tls_certificate_is_not_reported_as_a_bad_token()
    {
        await using var h = new TapHarness(TapTransport.TcpTls);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.TcpTls);

        var run = new CliRun(["list", "--endpoint", h.EndpointAddress!, "--token", Token]);
        await Assert.That(await run.ExitAsync()).IsEqualTo((int)ExitCode.Failed);
        await Assert.That(run.Error.ToString()).Contains("TLS handshake");
        await Assert.That(run.Error.ToString()).Contains("--insecure");
    }

    [Test]
    [Arguments(TapTransport.Tcp)]
    [Arguments(TapTransport.TcpTls)]
    public async Task Record_first_runs_until_the_session_closes(TapTransport transport)
    {
        await using var h = new TapHarness(transport, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync(transport);
        var path = TempFile();
        try
        {
            // Started before any session exists: "first" waits for one.
            var run = new CliRun(Connect(h, transport, "record", "--session", "first", "--out", path, "--overflow", "block"));
            await Task.Delay(200);
            var client = await h.ConnectClientAsync();
            var session = await h.WaitForSessionAsync();
            await run.StartedAsync();
            for (var i = 0; i < 5; i++)
                await client.Proxy.Echo([(byte)i, 1, 2]);
            await client.DisconnectAsync();

            await Assert.That(await run.ExitAsync()).IsEqualTo((int)ExitCode.Ok);
            await Assert.That(run.Output.ToString()).Contains("Stopped: the session closed.");

            var info = Info(path);
            await Assert.That(info.IndexSource).IsEqualTo(NntlIndexSource.Footer);
            await Assert.That(info.Damage.Length).IsEqualTo(0);
            await Assert.That(info.Producer.Length).IsGreaterThan(0);
            await Assert.That(info.Sessions.Length).IsEqualTo(1);
            var s = info.Sessions[0];
            await Assert.That(s.Id).IsEqualTo(session.Id);
            await Assert.That(s.Role).IsEqualTo(NntlSessionRole.Server);
            // "first" takes the first listed session, which can still be handshaking (sessions are listed from their
            // first byte): that attach has no state to snapshot (the server registers the session once the handshake
            // completes) and the stream carries the handshake's greetings instead. Attached later, the snapshot is there.
            if (!s.Snapshot)
                await Assert.That(s.Messages!.Value.Greetings).IsGreaterThan(0);
            await Assert.That(s.Close?.Kind).IsEqualTo(NntlCloseKind.Disconnected);
            await Assert.That(s.SchemaEmbedded).IsTrue();
            await Assert.That(s.SchemaHash).IsEqualTo(Convert.ToHexStringLower(SchemaHashOf()));
            await Assert.That(s.Gaps.Count).IsEqualTo(0);
            await Assert.That(s.Inbound.Ops).IsGreaterThan(0);
            await Assert.That(s.Outbound.Ops).IsGreaterThan(0);
            await Assert.That(s.Inbound.Framing).IsEqualTo(FramingState.Complete);
            await Assert.That(s.Inbound.Frames).IsGreaterThanOrEqualTo(5);
            await Assert.That(s.Outbound.Frames).IsGreaterThanOrEqualTo(5);
            await Assert.That(s.Messages!.Value.Invocations).IsEqualTo(5);
            await AssertMatchesReader(path, s);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments(TapTransport.Tcp)]
    [Arguments(TapTransport.TcpTls)]
    public async Task Record_by_id_stops_after_the_duration(TapTransport transport)
    {
        await using var h = new TapHarness(transport, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync(transport);
        var client = await h.ConnectClientAsync();
        await h.ConnectClientAsync();
        await h.WaitForSessionAsync(2);
        var target = h.Inspector.GetSessions()[0];
        var path = TempFile();
        try
        {
            var run = new CliRun(Connect(h, transport, "record", "--session",
                target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "--out", path, "--duration", "1500ms"));
            await run.StartedAsync();
            await client.Proxy.Echo([9]);

            await Assert.That(await run.ExitAsync()).IsEqualTo((int)ExitCode.Ok);
            await Assert.That(run.Output.ToString()).Contains("Stopped: duration reached.");

            var info = Info(path);
            await Assert.That(info.IndexSource).IsEqualTo(NntlIndexSource.Footer);
            await Assert.That(info.Sessions.Length).IsEqualTo(1);
            var s = info.Sessions[0];
            await Assert.That(s.Id).IsEqualTo(target.Id);
            await Assert.That(s.Close?.Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
            await Assert.That(s.DurationSeconds).IsGreaterThanOrEqualTo(1.0);
            await AssertMatchesReader(path, s);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments(TapTransport.Tcp)]
    [Arguments(TapTransport.TcpTls)]
    public async Task Record_all_captures_new_sessions_from_inception(TapTransport transport)
    {
        await using var h = new TapHarness(transport, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync(transport);
        var first = await h.ConnectClientAsync();
        var current = await h.WaitForSessionAsync();
        var path = TempFile();
        using var interrupt = new CancellationTokenSource();
        try
        {
            var run = new CliRun(Connect(h, transport, "record", "--session", "all", "--out", path, "--overflow", "block"),
                interrupt.Token);
            await run.StartedAsync();
            var second = await h.ConnectClientAsync();
            var joined = await h.WaitForSessionAsync(2);
            await first.Proxy.Echo([1]);
            await second.Proxy.Echo([2]);
            await second.Proxy.Echo([3]);
            await second.DisconnectAsync();
            while (h.Inspector.GetSessions().Length > 1)
                await Task.Delay(10);

            // Ctrl+C: a clean stop, with the footer.
            interrupt.Cancel();
            await Assert.That(await run.ExitAsync()).IsEqualTo((int)ExitCode.Ok);
            await Assert.That(run.Output.ToString()).Contains("Stopped: interrupted.");

            var info = Info(path);
            await Assert.That(info.IndexSource).IsEqualTo(NntlIndexSource.Footer);
            await Assert.That(info.Sessions.Select(s => s.Id)).IsEquivalentTo(new[] { current.Id, joined.Id });

            var old = info.Sessions.Single(s => s.Id == current.Id);
            await Assert.That(old.FromInception).IsFalse();
            await Assert.That(old.Snapshot).IsTrue();
            await Assert.That(old.Close?.Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
            await Assert.That(old.Messages!.Value.Invocations).IsEqualTo(1);

            var fresh = info.Sessions.Single(s => s.Id == joined.Id);
            await Assert.That(fresh.FromInception).IsTrue();
            await Assert.That(fresh.Inbound.Preamble).IsTrue();
            await Assert.That(fresh.Outbound.Preamble).IsTrue();
            await Assert.That(fresh.Snapshot).IsFalse();
            await Assert.That(fresh.Close?.Kind).IsEqualTo(NntlCloseKind.Disconnected);
            await Assert.That(fresh.Inbound.Framing).IsEqualTo(FramingState.Complete);
            await Assert.That(fresh.Inbound.RedactedOps).IsEqualTo(0);
            await Assert.That(fresh.Messages!.Value.Greetings).IsEqualTo(2);
            await Assert.That(fresh.Messages!.Value.Invocations).IsEqualTo(2);
            foreach (var s in info.Sessions)
                await AssertMatchesReader(path, s);

            // The text form names both sessions and how each was recorded.
            var text = new StringWriter();
            InfoCommand.Write(text, info);
            await Assert.That(text.ToString()).Contains("from the connection's first byte (preamble present)");
            await Assert.That(text.ToString()).Contains("from attach, with a state snapshot");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments("first")]
    [Arguments("all")]
    public async Task Record_is_refused_when_the_server_config_disallows_it(string selector)
    {
        await using var h = new TapHarness(TapTransport.Tcp, new InspectorOptions { AllowRecording = false });
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        await h.ConnectClientAsync();
        await h.WaitForSessionAsync();
        var path = TempFile();

        var run = new CliRun(Connect(h, TapTransport.Tcp, "record", "--session", selector, "--out", path));
        await Assert.That(await run.ExitAsync()).IsEqualTo((int)ExitCode.Refused);
        await Assert.That(run.Error.ToString()).Contains("Recording disabled by server config");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Record_of_an_unknown_session_exits_with_4()
    {
        await using var h = new TapHarness(TapTransport.Tcp, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var path = TempFile();

        var missing = new CliRun(Connect(h, TapTransport.Tcp, "record", "--session", "424242", "--out", path));
        await Assert.That(await missing.ExitAsync()).IsEqualTo((int)ExitCode.NotFound);
        var none = new CliRun(Connect(h, TapTransport.Tcp, "record", "--session", "first", "--wait", "300ms", "--out", path));
        await Assert.That(await none.ExitAsync()).IsEqualTo((int)ExitCode.NotFound);
        await Assert.That(none.Error.ToString()).Contains("no session appeared");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Include_secrets_needs_the_server_to_include_them()
    {
        await using var h = new TapHarness(TapTransport.Tcp, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var path = TempFile();

        var run = new CliRun(Connect(h, TapTransport.Tcp, "record", "--session", "all", "--out", path, "--include-secrets"));
        await Assert.That(await run.ExitAsync()).IsEqualTo((int)ExitCode.Refused);
        await Assert.That(run.Error.ToString()).Contains("IncludeSecrets");
    }

    private static byte[] SchemaHashOf() => Core.Schema.SchemaHash.ToBytes(TapTestData.SchemaHash<TapServerNexus>());
}
