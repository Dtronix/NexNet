using System.Text;
using NexNet;
using NexNet.InteropServer;
using NexNet.TestNexus.Interop;

// Usage: NexNet.InteropServer.Cli [--token <token>] [--origin <origin>]... [--timeout <ms>]
// Starts a dedicated interop server with the interop test nexus (InteropServerNexus, which Java and JavaScript export),
// prints each endpoint's address and runs until Ctrl+C. The Android CI job runs its emulator smoke test against it.
var options = new InteropServerOptions();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--token" when i + 1 < args.Length:
            var expected = args[++i];
            options.TokenValidator = (token, _) => ValueTask.FromResult<IIdentity?>(
                token is { } t && Encoding.UTF8.GetString(t.Span) == expected ? new DefaultIdentity { DisplayName = "cli" } : null);
            break;
        case "--origin" when i + 1 < args.Length:
            options.AllowedOrigins.Add(args[++i]);
            break;
        case "--timeout" when i + 1 < args.Length:
            options.Timeout = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
            Console.Error.WriteLine("Usage: NexNet.InteropServer.Cli [--token <token>] [--origin <origin>]... [--timeout <ms>]");
            return 2;
    }
}

await using var server = await InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(
    () => new InteropServerNexus(), options);

foreach (var (kind, address) in server.Endpoints.ToDictionary())
    Console.WriteLine($"{kind.GetEndpointName(),-15} {address}");
Console.WriteLine($"{"test-ca",-15} {InteropCertificates.TestCaPemPath}");
Console.WriteLine("Running; press Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
return 0;
