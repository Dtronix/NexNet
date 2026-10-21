using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using NexNet;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNetBenchmarks;

public enum BroadcastMode
{
    All,
    Group,
    Clients,
}

public enum BroadcastSide
{
    /// <summary>This process is the server; the clients run in a peer process.</summary>
    Sender,

    /// <summary>This process holds the clients; the server runs in a peer process.</summary>
    Receivers,
}

/// <summary>
/// Server-to-clients fire-and-forget broadcasts to <see cref="ClientCount"/> clients. Mean and Allocated are per
/// broadcast. The other side runs in a peer process, so Allocated is only the measured side: the server
/// (<see cref="BroadcastSide.Sender"/>) or all clients together (<see cref="BroadcastSide.Receivers"/>; divide by
/// <see cref="ClientCount"/> for one received invocation).
/// </summary>
public class BroadcastBenchmarks
{
    public const int ClientCount = 16;
    private const int Batch = 1000;

    private BroadcastPeer _peer = null!;
    private NexusServer<BroadcastServerNexus, BroadcastServerNexus.ClientProxy>? _server;
    private NexusClient<BroadcastClientNexus, BroadcastClientNexus.ServerProxy>[]? _clients;
    private long _target;

    [Params(BroadcastMode.All, BroadcastMode.Group, BroadcastMode.Clients)]
    public BroadcastMode Mode { get; set; }

    [Params(BroadcastSide.Sender, BroadcastSide.Receivers)]
    public BroadcastSide Side { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        // Relative: the peer runs in the same working directory, and socket paths are limited to 108 characters.
        var path = $"broadcast-{Mode}-{Side}.sock";
        if (File.Exists(path))
            File.Delete(path);

        BroadcastClientNexus.Reset();
        BroadcastServerNexus.Joined = 0;
        _target = 0;

        if (Side == BroadcastSide.Sender)
        {
            _server = BroadcastHost.StartServer(path);
            await _server.StartAsync();
            _peer = BroadcastPeer.Start("clients", path);
            await BroadcastHost.WaitForSessions(_server);
        }
        else
        {
            _peer = BroadcastPeer.Start("server", path);
            _clients = await BroadcastHost.ConnectClients(path);
        }
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        _peer.Dispose();

        if (_clients != null)
        {
            foreach (var client in _clients)
                await client.DisconnectAsync();
        }

        if (_server != null)
            await _server.StopAsync();
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public async Task Broadcast()
    {
        _target += Batch;

        if (Side == BroadcastSide.Sender)
        {
            await BroadcastHost.Broadcast(_server!, Mode, Batch);

            // The peer answers once every client has received the batch.
            _peer.Send(_target.ToString());
            _peer.Expect("ok");
        }
        else
        {
            var received = BroadcastClientNexus.WaitFor(_target * ClientCount);
            _peer.Send($"broadcast {Mode} {Batch}");
            await received;
        }
    }

    /// <summary>
    /// Entry point of the peer process: <c>--broadcast-peer clients|server &lt;socket path&gt;</c>. Reads commands
    /// from stdin until it closes or receives <c>exit</c>.
    /// </summary>
    public static async Task RunPeer(string role, string path)
    {
        var input = Console.In;
        var output = Console.Out;

        if (role == "clients")
        {
            var clients = await BroadcastHost.ConnectClients(path);
            output.WriteLine("ready");
            output.Flush();

            while (input.ReadLine() is { } line && line != "exit")
            {
                await BroadcastClientNexus.WaitFor(long.Parse(line) * ClientCount);
                output.WriteLine("ok");
                output.Flush();
            }

            foreach (var client in clients)
                await client.DisconnectAsync();
        }
        else
        {
            var server = BroadcastHost.StartServer(path);
            await server.StartAsync();
            output.WriteLine("ready");
            output.Flush();

            var sessionsReady = false;
            while (input.ReadLine() is { } line && line != "exit")
            {
                if (!sessionsReady)
                {
                    await BroadcastHost.WaitForSessions(server);
                    sessionsReady = true;
                }

                var parts = line.Split(' ');
                await BroadcastHost.Broadcast(server, Enum.Parse<BroadcastMode>(parts[1]), int.Parse(parts[2]));
            }

            await server.StopAsync();
        }
    }
}

internal static class BroadcastHost
{
    public const string GroupName = "broadcast";

    public static NexusServer<BroadcastServerNexus, BroadcastServerNexus.ClientProxy> StartServer(string path)
    {
        var config = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint(path) };
        return BroadcastServerNexus.CreateServer(config, static () => new BroadcastServerNexus());
    }

    public static async Task<NexusClient<BroadcastClientNexus, BroadcastClientNexus.ServerProxy>[]> ConnectClients(string path)
    {
        var clients = new NexusClient<BroadcastClientNexus, BroadcastClientNexus.ServerProxy>[BroadcastBenchmarks.ClientCount];
        for (var i = 0; i < clients.Length; i++)
        {
            var config = new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(path) };
            clients[i] = BroadcastClientNexus.CreateClient(config, new BroadcastClientNexus());

            // The peer server may still be starting.
            for (var attempt = 0; ; attempt++)
            {
                var result = await clients[i].TryConnectAsync();
                if (result.Success)
                    break;
                if (attempt == 100)
                    throw new InvalidOperationException($"Client {i} could not connect: {result.State}");
                await Task.Delay(50);
            }
        }

        return clients;
    }

    /// <summary>
    /// Waits until every client has connected and joined <see cref="GroupName"/>.
    /// </summary>
    public static async Task WaitForSessions(NexusServer<BroadcastServerNexus, BroadcastServerNexus.ClientProxy> server)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (Volatile.Read(ref BroadcastServerNexus.Joined) >= BroadcastBenchmarks.ClientCount)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Clients did not connect to the broadcast server.");
    }

    public static async Task Broadcast(
        NexusServer<BroadcastServerNexus, BroadcastServerNexus.ClientProxy> server,
        BroadcastMode mode,
        int count)
    {
        using var owner = server.ContextProvider.Rent();
        var proxy = mode switch
        {
            BroadcastMode.All => owner.Proxy.All,
            BroadcastMode.Group => owner.Proxy.Group(GroupName),
            _ => owner.Proxy.Clients(owner.Proxy.GetIds().ToArray()),
        };

        for (var i = 0; i < count; i++)
            await proxy.Receive(i);
    }
}

/// <summary>
/// The other side of a <see cref="BroadcastBenchmarks"/> run: this benchmark assembly started again with
/// <c>--broadcast-peer</c>, controlled through its stdin and stdout.
/// </summary>
internal sealed class BroadcastPeer : IDisposable
{
    private readonly Process _process;

    private BroadcastPeer(Process process)
    {
        _process = process;
    }

    public static BroadcastPeer Start(string role, string path)
    {
        var info = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        // The entry assembly's runtime config also fits this assembly when it is hosted by BenchmarkDotNet.
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add("--runtimeconfig");
        info.ArgumentList.Add(Path.ChangeExtension(Assembly.GetEntryAssembly()!.Location, ".runtimeconfig.json"));
        info.ArgumentList.Add(typeof(BroadcastPeer).Assembly.Location);
        info.ArgumentList.Add("--broadcast-peer");
        info.ArgumentList.Add(role);
        info.ArgumentList.Add(path);

        var peer = new BroadcastPeer(Process.Start(info) ?? throw new InvalidOperationException("Peer did not start."));
        peer.Expect("ready");
        return peer;
    }

    public void Send(string line)
    {
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    public void Expect(string expected)
    {
        var line = _process.StandardOutput.ReadLine();
        if (line != expected)
            throw new InvalidOperationException($"Broadcast peer answered '{line}', expected '{expected}'.");
    }

    public void Dispose()
    {
        try
        {
            Send("exit");
            if (!_process.WaitForExit(10_000))
                _process.Kill();
        }
        catch (Exception)
        {
            if (!_process.HasExited)
                _process.Kill();
        }

        _process.Dispose();
    }

    private static string DotnetHost()
    {
        var current = Environment.ProcessPath;
        if (current != null && Path.GetFileNameWithoutExtension(current) == "dotnet")
            return current;

        return Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    }
}

interface IBroadcastClientNexus
{
    ValueTask Receive(int value);
}

interface IBroadcastServerNexus
{
}

[Nexus<IBroadcastClientNexus, IBroadcastServerNexus>(NexusType = NexusType.Client)]
partial class BroadcastClientNexus
{
    // Shared by every client in the process.
    private static long _received;
    private static long _waitTarget = long.MaxValue;
    private static TaskCompletionSource? _waiter;

    public ValueTask Receive(int value)
    {
        var received = Interlocked.Increment(ref _received);
        if (received >= Volatile.Read(ref _waitTarget))
            Volatile.Read(ref _waiter)?.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public static void Reset()
    {
        _received = 0;
        _waitTarget = long.MaxValue;
        _waiter = null;
    }

    /// <summary>
    /// Completes once the clients of this process have received <paramref name="target"/> broadcasts in total.
    /// </summary>
    public static Task WaitFor(long target)
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _waiter, waiter);
        Volatile.Write(ref _waitTarget, target);

        if (Interlocked.Read(ref _received) >= target)
            waiter.TrySetResult();

        return waiter.Task;
    }
}

[Nexus<IBroadcastServerNexus, IBroadcastClientNexus>(NexusType = NexusType.Server)]
partial class BroadcastServerNexus
{
    /// <summary>
    /// Sessions that have joined <see cref="BroadcastHost.GroupName"/>.
    /// </summary>
    public static int Joined;

    protected override async ValueTask OnConnected(bool isReconnected)
    {
        await Context.Groups.AddAsync(BroadcastHost.GroupName);
        Interlocked.Increment(ref Joined);
    }
}
