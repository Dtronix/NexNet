using System.Text;
using NexNet.Inspector.Cli;
using NexNet.Inspector.Tap;
using NexNet.Inspector.Tests.Tap;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>nexnet-tap tail against an in-process tapped server and its inspector endpoint.</summary>
public class TailLiveTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly string Token = Encoding.UTF8.GetString(TapHarness.EndpointToken);

    /// <summary>A tail running in the background with its own writers.</summary>
    private sealed class TailRun
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TailRun(TapHarness h, CancellationToken cancellationToken, params string[] args)
        {
            _output = TextWriter.Synchronized(_text);
            var cli = new TapCli(_output, Error) { TailStarted = () => _started.TrySetResult() };
            Completion = Task.Run(() => cli.RunAsync(["tail", h.EndpointAddress!, "--token", Token, .. args], null, cancellationToken));
        }

        // The tail writes from its own task while the test reads: every access goes through the synchronized writer.
        private readonly StringWriter _text = new() { NewLine = "\n" };
        private readonly TextWriter _output;

        /// <summary>What the tail has printed so far.</summary>
        public string Output
        {
            get
            {
                // TextWriter.Synchronized locks on the wrapper itself for every call.
                lock (_output)
                    return _text.ToString();
            }
        }

        public StringWriter Error { get; } = new();

        public Task<int> Completion { get; }

        public async Task StartedAsync()
        {
            var first = await Task.WhenAny(_started.Task, Completion).WaitAsync(Wait);
            if (first == Completion)
                throw new InvalidOperationException($"nexnet-tap exited {Completion.Result}: {Error}{Output}");
        }

        public string[] Lines => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        public async Task WaitForAsync(Func<string, bool> line)
        {
            for (var i = 0; i < 3000 && !Lines.Any(line); i++)
            {
                if (Completion.IsCompleted)
                    break;
                await Task.Delay(10);
            }
        }
    }

    [Test]
    public async Task A_live_tail_prints_calls_as_they_happen_until_the_session_closes()
    {
        await using var h = new TapHarness(TapTransport.Tcp);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var client = await h.ConnectClientAsync();
        await h.WaitForSessionAsync();

        var run = new TailRun(h, CancellationToken.None, "--session", "first");
        await run.StartedAsync();
        await client.Proxy.Echo([1]);
        await run.WaitForAsync(l => l.Contains("Echo(", StringComparison.Ordinal));
        await Assert.That(run.Lines.Count(l => l.Contains("Echo(", StringComparison.Ordinal))).IsEqualTo(1);

        await client.Proxy.Echo([2, 2]);
        await client.Proxy.Echo([3, 3, 3]);
        await client.DisconnectAsync();

        await Assert.That(await run.Completion.WaitAsync(Wait)).IsEqualTo((int)ExitCode.Ok);
        var lines = run.Lines;
        await Assert.That(lines[0]).StartsWith("@0x");
        await Assert.That(lines[0]).Contains("server tcp");
        // Each call's outcome once: on its call line, or on a later res line when the call line was printed before the
        // result arrived (`-> …`, text format §6), as a slow viewer under load can.
        await Assert.That(lines.Count(l => l.Contains(" Echo", StringComparison.Ordinal) && l.Contains("-> ok", StringComparison.Ordinal))).IsEqualTo(3);
        await Assert.That(lines[^1]).Contains("END ");
        await Assert.That(run.Error.ToString()).Contains("Tailing session");
    }

    [Test]
    public async Task A_live_tail_stops_when_until_holds()
    {
        await using var h = new TapHarness(TapTransport.Tcp);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var client = await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();

        var run = new TailRun(h, CancellationToken.None, "--session", session.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--until", "method=Echo", "--context", "1", "--format", "ndjson");
        await run.StartedAsync();
        await client.Proxy.Echo([7]);

        await Assert.That(await run.Completion.WaitAsync(Wait)).IsEqualTo((int)ExitCode.Ok);
        var lines = run.Lines;
        await Assert.That(lines[^2]).Contains("\"n\":\"Echo\"");
        await Assert.That(lines[^1]).StartsWith("{\"k\":\"stop\"");
        await Assert.That(lines[^1]).Contains("until method=Echo matched");
        // A live attach cannot be resumed: no cursor.
        await Assert.That(lines[^1]).DoesNotContain("after");
    }

    [Test]
    public async Task Tailing_every_session_tags_lines_and_needs_recording_allowed()
    {
        await using (var refused = new TapHarness(TapTransport.Tcp))
        {
            await refused.StartAsync();
            await refused.StartEndpointAsync(TapTransport.Tcp);
            var no = new TailRun(refused, CancellationToken.None, "--session", "all");
            await Assert.That(await no.Completion.WaitAsync(Wait)).IsEqualTo((int)ExitCode.Refused);
            await Assert.That(no.Error.ToString()).Contains("Recording disabled by server config");
        }

        await using var h = new TapHarness(TapTransport.Tcp, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        using var interrupt = new CancellationTokenSource();
        var run = new TailRun(h, interrupt.Token, "--session", "all");
        await run.StartedAsync();
        var first = await h.ConnectClientAsync();
        var second = await h.ConnectClientAsync();
        await h.WaitForSessionAsync(2);
        await first.Proxy.Echo([1]);
        await second.Proxy.Echo([2, 2]);
        await run.WaitForAsync(l => l.Contains("Echo([2", StringComparison.Ordinal) || l.Contains("Echo(bin", StringComparison.Ordinal));
        await run.WaitForAsync(l => run.Lines.Count(x => x.Contains("Echo(", StringComparison.Ordinal)) >= 2);

        await interrupt.CancelAsync();
        await Assert.That(await run.Completion.WaitAsync(Wait)).IsEqualTo((int)ExitCode.Ok);
        var lines = run.Lines;
        var echoes = lines.Where(l => l.Contains("Echo(", StringComparison.Ordinal)).ToArray();
        await Assert.That(echoes.Length).IsEqualTo(2);
        // Interleaved sessions: every line but a header names its session.
        foreach (var line in lines)
            await Assert.That(line).StartsWith("@0x").Because(run.Output);
        await Assert.That(echoes[0].Split(' ')[0]).IsNotEqualTo(echoes[1].Split(' ')[0]);
        await Assert.That(lines.Count(l => l.Contains("… stopped: interrupted", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(lines.Any(l => l.Contains("--after", StringComparison.Ordinal))).IsFalse();
    }
}
