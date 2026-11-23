using System.Text;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Sample;

/// <summary>Sample commands.</summary>
internal enum SampleCommand : byte
{
    Help = 0,
    Serve = 1,
    Client = 2,
    Fixtures = 3,
    Check = 4,
}

/// <summary>Sample options; every option is <c>--name value</c> except the flags.</summary>
internal enum SampleOption : byte
{
    Transport = 0,
    Host = 1,
    Port = 2,
    Path = 3,
    Endpoint = 4,
    Token = 5,
    AllowRecording = 6,
    IncludeSecrets = 7,
    Record = 8,
    Cert = 9,
    CertPassword = 10,
    Iterations = 11,
    Delay = 12,
    Burst = 13,
}

/// <summary>
/// The "Chat" sample of the NexNet Inspector: a server and client that produce the traffic the inspector screens show,
/// and the generator of the test fixtures. Run without arguments for usage.
/// </summary>
internal static class Program
{
    private const string Usage = """
        NexNet Inspector sample ("Chat")

        Usage:
          serve    [--transport tcp|tls|uds] [--host 127.0.0.1] [--port 7711] [--path SOCKET]
                   [--endpoint tcp://127.0.0.1:7712|tls://HOST:PORT|uds:PATH] [--token sample-token]
                   [--allow-recording] [--include-secrets] [--record FILE.nntl] [--cert FILE.pfx --cert-password P]
                   Chat server with the inspector tap and its endpoint (the endpoint is never tapped). TLS uses a
                   self-signed certificate unless --cert is given. --record writes every session to FILE (needs
                   --allow-recording). Ctrl+C stops.
          client   [--transport tcp|tls|uds] [--host 127.0.0.1] [--port 7711] [--path SOCKET]
                   [--iterations 0] [--delay 1000] [--burst 0]
                   Connects a Chat client and runs the Full scenario (Users, Join, a canceled SendMessage, uploads, a
                   five-chunk SendAttachment interleaved with a FileUpload pipe and pings, the LogLine channel, then
                   --burst extra log lines), disconnects, waits --delay ms and repeats; 0 iterations repeats forever.
          fixtures [DIRECTORY]   (default src/NexNet.Inspector.Tests/Fixtures)
                   Records the checked-in fixture files and checks each one.
          check    FILE.nntl...
                   Reads recordings with the inspector core and prints what it found.

        Phase-1 gate (from the repository root; the CLI is src/NexNet.Inspector.Cli, "nexnet-tap"):
          dotnet run --project src/NexNet.Inspector.Sample -c Release -- serve --transport tcp --port 7711 --endpoint tcp://127.0.0.1:7712 --token sample-token --allow-recording
          dotnet run --project src/NexNet.Inspector.Sample -c Release -- serve --transport tls --port 7721 --endpoint tcp://127.0.0.1:7722 --token sample-token --allow-recording
          dotnet run --project src/NexNet.Inspector.Sample -c Release -- client --transport tcp --port 7711
          dotnet run --project src/NexNet.Inspector.Sample -c Release -- client --transport tls --port 7721
          then record from the endpoint with nexnet-tap record (endpoint tcp://127.0.0.1:7712 or :7722, token
          sample-token) and inspect the file with nexnet-tap info FILE.nntl; check FILE.nntl replays it here.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || !TryParseCommand(args[0], out var command) || command == SampleCommand.Help)
        {
            Console.WriteLine(Usage);
            return args.Length == 0 || args[0] is "help" or "--help" or "-h" ? 0 : 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return command switch
            {
                SampleCommand.Serve => await ServeAsync(Options.Parse(args.AsSpan(1)), cts.Token),
                SampleCommand.Client => await ClientAsync(Options.Parse(args.AsSpan(1)), cts.Token),
                SampleCommand.Fixtures => await FixturesAsync(args.Length > 1 ? args[1] : Path.Combine("src", "NexNet.Inspector.Tests", "Fixtures"), cts.Token),
                _ => Check(args.AsSpan(1)),
            };
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine("Run without arguments for usage.");
            return 2;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static async Task<int> ServeAsync(Options options, CancellationToken cancellationToken)
    {
        var address = options.Address(SampleTransport.Tcp, 7711);
        var endpointAddress = options.Endpoint ?? new SampleAddress(SampleTransport.Tcp, System.Net.IPAddress.Loopback, 7712, null);
        using var certificate = address.Transport == SampleTransport.Tls || endpointAddress.Transport == SampleTransport.Tls
            ? SampleCertificates.Load(options.Cert, options.CertPassword)
            : null;

        // A server killed earlier leaves its socket file behind, which would fail the bind.
        address.Cleanup();
        var config = address.CreateServerConfig(certificate);
        config.Authenticate = true;
        var inspector = config.UseInspector<ChatServerNexus>(new InspectorOptions
        {
            AllowRecording = options.AllowRecording,
            IncludeSecrets = options.IncludeSecrets,
        });
        var server = ChatServerNexus.CreateServer(config, static () => new ChatServerNexus());
        inspector.UseSnapshotsOf(server);

        InspectorRecording? recording = null;
        FileStream? recordFile = null;
        if (options.Record != null)
        {
            if (!options.AllowRecording)
                throw new ArgumentException("--record needs --allow-recording.");
            // Readable while it grows: a crashed or still-running recording can be opened (and checked) at any time.
            recordFile = new FileStream(options.Record, FileMode.Create, FileAccess.Write, FileShare.Read);
            recording = inspector.RecordTo(recordFile);
        }

        await server.StartAsync(cancellationToken);
        var endpoint = await inspector.StartEndpointAsync(endpointAddress.CreateServerConfig(certificate),
            Encoding.UTF8.GetBytes(options.Token), cancellationToken);
        Console.WriteLine($"Chat server on {address}; inspector endpoint on {endpointAddress} (token \"{options.Token}\", "
                          + $"recording {(options.AllowRecording ? "allowed" : "disabled")}, "
                          + $"secrets {(options.IncludeSecrets ? "included" : "redacted")}). Ctrl+C stops.");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        if (recording != null)
            await recording.StopAsync();
        await endpoint.DisposeAsync();
        await server.StopAsync();
        await inspector.DisposeAsync();
        recordFile?.Dispose();
        address.Cleanup();
        endpointAddress.Cleanup();
        return 0;
    }

    private static async Task<int> ClientAsync(Options options, CancellationToken cancellationToken)
    {
        var address = options.Address(SampleTransport.Tcp, 7711);
        for (var i = 1; options.Iterations == 0 || i <= options.Iterations; i++)
        {
            var config = FixtureGenerator.CreateClientConfig(address);
            var pacer = new ChunkPacer();
            config.Tap = pacer;
            await using (var client = ChatClientNexus.CreateClient(config, new ChatClientNexus()))
            {
                await client.ConnectAsync(cancellationToken);
                await ChatScenario.RunAsync(client, pacer, new ChatScenarioOptions
                {
                    Kind = ChatScenarioKind.Full,
                    User = $"user{i}",
                    BurstLines = options.Burst,
                }, cancellationToken);
                await client.DisconnectAsync();
            }

            Console.WriteLine($"Scenario {i} done.");
            await Task.Delay(options.Delay, cancellationToken);
        }

        return 0;
    }

    private static async Task<int> FixturesAsync(string directory, CancellationToken cancellationToken)
    {
        await FixtureGenerator.WriteAsync(directory, Console.Out, cancellationToken);
        return 0;
    }

    private static int Check(ReadOnlySpan<string> paths)
    {
        if (paths.IsEmpty)
            throw new ArgumentException("check needs at least one file.");

        var failed = false;
        foreach (var path in paths)
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            var recording = RecordingAnalysis.Read(bytes);
            Console.WriteLine($"{path}: {recording.Length:N0} bytes, index {recording.IndexSource}, {recording.Damage.Count} damaged regions, "
                              + $"{recording.Sessions.Count} sessions");
            foreach (var session in recording.Sessions)
            {
                var a = session.Assembler;
                Console.WriteLine($"  session {session.Open.SessionId:X} {session.Open.Role} {session.Open.TransportName}: "
                                  + $"ops {session.InboundOps} in / {session.OutboundOps} out, frames {a.GetFrames(Core.Model.TapDirection.Inbound).Length} in / "
                                  + $"{a.GetFrames(Core.Model.TapDirection.Outbound).Length} out, {a.Invocations.Length} invocations, "
                                  + $"{a.Pipes.Length} pipes, {a.Messages.Length} assembled messages, {session.Gaps.Count} gaps, "
                                  + $"schema {(session.Schema != null ? "embedded" : "missing")}"
                                  + (session.InboundFaulted || session.OutboundFaulted ? ", FRAMING FAULT" : ""));
                failed |= session.InboundFaulted || session.OutboundFaulted;
            }

            if (recording.IndexSource != NntlIndexSource.Footer)
                Console.WriteLine("  no footer: read by scanning (nexnet-tap repair rebuilds it)");
        }

        return failed ? 1 : 0;
    }

    private static bool TryParseCommand(string text, out SampleCommand command) =>
        Enum.TryParse(text, ignoreCase: true, out command) && Enum.IsDefined(command);

    /// <summary>Parsed options with their defaults.</summary>
    private sealed class Options
    {
        public SampleTransport? Transport { get; private set; }
        public string Host { get; private set; } = "127.0.0.1";
        public int? Port { get; private set; }
        public string? Path { get; private set; }
        public SampleAddress? Endpoint { get; private set; }
        public string Token { get; private set; } = "sample-token";
        public bool AllowRecording { get; private set; }
        public bool IncludeSecrets { get; private set; }
        public string? Record { get; private set; }
        public string? Cert { get; private set; }
        public string? CertPassword { get; private set; }
        public int Iterations { get; private set; }
        public int Delay { get; private set; } = 1000;
        public int Burst { get; private set; }

        public SampleAddress Address(SampleTransport defaultTransport, int defaultPort)
        {
            var transport = Transport ?? defaultTransport;
            if (transport == SampleTransport.Uds)
                return new SampleAddress(transport, System.Net.IPAddress.Loopback, 0,
                    Path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexnet-chat.sock"));
            if (!System.Net.IPAddress.TryParse(Host, out var host))
                throw new ArgumentException($"--host {Host} is not an IP address.");
            return new SampleAddress(transport, host, Port ?? defaultPort, null);
        }

        public static Options Parse(ReadOnlySpan<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var name = args[i];
                if (!name.StartsWith("--", StringComparison.Ordinal)
                    || !Enum.TryParse<SampleOption>(name.AsSpan(2).ToString().Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out var option)
                    || !Enum.IsDefined(option))
                    throw new ArgumentException($"Unknown option {name}.");

                switch (option)
                {
                    case SampleOption.AllowRecording:
                        options.AllowRecording = true;
                        continue;
                    case SampleOption.IncludeSecrets:
                        options.IncludeSecrets = true;
                        continue;
                }

                if (++i >= args.Length)
                    throw new ArgumentException($"{name} needs a value.");
                var value = args[i];
                switch (option)
                {
                    case SampleOption.Transport:
                        options.Transport = Enum.TryParse<SampleTransport>(value, ignoreCase: true, out var transport) && Enum.IsDefined(transport)
                            ? transport
                            : throw new ArgumentException($"--transport {value}: expected tcp, tls or uds.");
                        break;
                    case SampleOption.Host:
                        options.Host = value;
                        break;
                    case SampleOption.Port:
                        options.Port = ParseInt(name, value, 1);
                        break;
                    case SampleOption.Path:
                        options.Path = value;
                        break;
                    case SampleOption.Endpoint:
                        options.Endpoint = SampleAddress.TryParse(value, out var endpoint)
                            ? endpoint
                            : throw new ArgumentException($"--endpoint {value}: expected tcp://HOST:PORT, tls://HOST:PORT or uds:PATH.");
                        break;
                    case SampleOption.Token:
                        options.Token = value.Length > 0 ? value : throw new ArgumentException("--token is empty.");
                        break;
                    case SampleOption.Record:
                        options.Record = value;
                        break;
                    case SampleOption.Cert:
                        options.Cert = value;
                        break;
                    case SampleOption.CertPassword:
                        options.CertPassword = value;
                        break;
                    case SampleOption.Iterations:
                        options.Iterations = ParseInt(name, value, 0);
                        break;
                    case SampleOption.Delay:
                        options.Delay = ParseInt(name, value, 0);
                        break;
                    case SampleOption.Burst:
                        options.Burst = ParseInt(name, value, 0);
                        break;
                }
            }

            return options;
        }

        private static int ParseInt(string name, string value, int minimum) =>
            int.TryParse(value, out var number) && number >= minimum
                ? number
                : throw new ArgumentException($"{name} {value}: expected an integer of at least {minimum}.");
    }
}
