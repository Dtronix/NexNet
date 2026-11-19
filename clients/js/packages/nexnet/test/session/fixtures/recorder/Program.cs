// Records the session fixtures in this folder from the C# implementation:
//   messages.jsonl  protocol message bodies serialized by the C# message classes, with their fields;
//   frames.jsonl    complete byte streams, both directions, of real C# client/server sessions over TCP,
//                   captured by a forwarding proxy.
// The assembly is named NexNet.Fuzz to reach the internal message classes (InternalsVisibleTo).
// Regenerate (from the repository root):
//   dotnet run --project clients/js/packages/nexnet/test/session/fixtures/recorder -c Release -- clients/js/packages/nexnet/test/session/fixtures
// Session IDs in frames.jsonl differ on every run; the tests read them from the recording.
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NexNet;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;
using NexNet.Transports;

namespace Recorder;

public interface IRecServerNexus { ValueTask<int> Echo(int value); }
public interface IRecServerNexus2 { ValueTask<int> Echo2(int value, int other); }
public interface IRecClientNexus { ValueTask Hello(); }
public interface IRecClientNexus2 { ValueTask Hello2(string text); }

[Nexus<IRecServerNexus, IRecClientNexus>(NexusType = NexusType.Server)]
public partial class RecServerNexus
{
    public static bool RejectAuth;
    public ValueTask<int> Echo(int value) => new(value);
    protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken)
        => new(RejectAuth ? null : new DefaultIdentity { DisplayName = "x" });
}

[Nexus<IRecServerNexus2, IRecClientNexus>(NexusType = NexusType.Server)]
public partial class RecServerNexus2 { public ValueTask<int> Echo2(int value, int other) => new(value + other); }

[Nexus<IRecClientNexus, IRecServerNexus>(NexusType = NexusType.Client)]
public partial class RecClientNexus { public ValueTask Hello() => default; }

[Nexus<IRecClientNexus2, IRecServerNexus>(NexusType = NexusType.Client)]
public partial class RecClientNexus2 { public ValueTask Hello2(string text) => default; }

[Nexus<IRecClientNexus, IRecServerNexus2>(NexusType = NexusType.Client)]
public partial class RecClientNexus3 { public ValueTask Hello() => default; }

public static class Program
{
    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    static string Body<T>(T m) where T : IMessageBase
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buf);
        m.Serialize(ref w);
        w.Flush();
        return Hex(buf.WrittenSpan);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static async Task Main(string[] args)
    {
        var outDir = args[0];
        Directory.CreateDirectory(outDir);
        var opts = new JsonSerializerOptions { WriteIndented = false };

        // ---------------- messages.jsonl ----------------
        var msgs = new List<string>();
        void Add(string name, MessageType type, object fields, string body)
            => msgs.Add(JsonSerializer.Serialize(new { name, type = (int)type, fields, body }, opts));

        foreach (var (name, version, sh, ch, token, mms, ic) in new (string, string?, int, int, byte[], int, int)[]
                 {
                     ("unversioned-no-token", null, -1288190245, 401298355, Array.Empty<byte>(), 4 * 1024 * 1024, 32),
                     ("versioned-token", "v1.2", 123456789, -987654321, new byte[] { 1, 2, 3, 4, 5 }, 16 * 1024 * 1024, 128),
                     ("max-version-small-limits", "abcdefghijklmnop", 0, 1, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), 65526, 1),
                 })
        {
            var g = new ClientGreetingMessage { Version = version, ServerNexusHash = sh, ClientNexusHash = ch, AuthenticationToken = token, MaxMessageSize = mms, InvocationCredits = ic };
            Add("ClientGreeting/" + name, MessageType.ClientGreeting,
                new { version, serverNexusHash = sh, clientNexusHash = ch, authenticationToken = Hex(token), maxMessageSize = mms, invocationCredits = ic }, Body(g));
            var r = new ClientGreetingReconnectionMessage { Version = version, ServerNexusHash = sh, ClientNexusHash = ch, AuthenticationToken = token, MaxMessageSize = mms, InvocationCredits = ic };
            Add("ClientGreetingReconnection/" + name, MessageType.ClientGreetingReconnection,
                new { version, serverNexusHash = sh, clientNexusHash = ch, authenticationToken = Hex(token), maxMessageSize = mms, invocationCredits = ic }, Body(r));
        }

        foreach (var (name, v, id, mms, ic) in new (string, int, long, int, int)[]
                 {
                     ("defaults", 401298355, 1, 4 * 1024 * 1024, 32),
                     ("large-id", -5, long.MaxValue, 16 * 1024 * 1024, 128),
                     ("negative-id", 0, -2, 65526, 1),
                     ("big-session-id", 7, 1L << 40, 1 << 30, 10000),
                 })
        {
            var s = new ServerGreetingMessage { Version = v, ClientId = id, MaxMessageSize = mms, InvocationCredits = ic };
            Add("ServerGreeting/" + name, MessageType.ServerGreeting,
                new { version = v, clientId = id.ToString(), maxMessageSize = mms, invocationCredits = ic }, Body(s));
        }

        foreach (var c in new ushort[] { 1, 16, 127, 128, 255, 256, 65535 })
            Add("InvocationCredit/" + c, MessageType.InvocationCredit, new { credits = (int)c }, Body(new InvocationCreditMessage { Credits = c }));
        foreach (var c in new ushort[] { 0, 1, 300, 65535 })
            Add("InvocationCancellation/" + c, MessageType.InvocationCancellation, new { invocationId = (int)c }, Body(new InvocationCancellationMessage { InvocationId = c }));
        foreach (var (id, kind, reason) in new (ushort, byte, byte)[] { (0, 0, 0), (513, 1, 2), (65535, 0, 3), (7, 1, 1) })
            Add($"MessageAbort/{id}-{kind}-{reason}", MessageType.MessageAbort, new { invocationId = (int)id, kind = (int)kind, reason = (int)reason },
                Body(new MessageAbortMessage { InvocationId = id, Kind = (LargeMessageKind)kind, Reason = (LargeMessageAbortReason)reason }));
        foreach (var (pid, st, win) in new (ushort, byte, uint)[] { (0x0201, 4, 262144), (0x0001, 8, 0), (0xFFFF, 7, 0), (0x0A05, 1, 0), (0x0102, 4, 1048576), (3, 2, 4294967295) })
            Add($"DuplexPipeUpdateState/{pid}-{st}-{win}", MessageType.DuplexPipeUpdateState, new { pipeId = (int)pid, state = (int)st, window = (long)win },
                Body(new DuplexPipeUpdateStateMessage { PipeId = pid, State = (NexusDuplexPipe.State)st, Window = win }));

        await File.WriteAllLinesAsync(Path.Combine(outDir, "messages.jsonl"), msgs);

        // ---------------- frames.jsonl (live recordings) ----------------
        var frames = new List<string>();
        var serverHash = IInvocationMethodHash.GetMethodHash<RecServerNexus>();

        frames.Add(await Record("handshake-ping-graceful", false, async (cfg) =>
        {
            cfg.PingInterval = 250;
            cfg.Authenticate = () => Encoding.UTF8.GetBytes("token-123");
            var client = RecClientNexus.CreateClient(cfg, new RecClientNexus());
            await client.ConnectAsync();
            await Task.Delay(700);
            await client.DisconnectAsync();
            return (int)DisconnectReason.Graceful;
        }, null, IInvocationMethodHash.GetMethodHash<RecClientNexus>(), serverHash));

        frames.Add(await Record("server-shutdown", false, async (cfg) =>
        {
            cfg.PingInterval = 60_000;
            var client = RecClientNexus.CreateClient(cfg, new RecClientNexus());
            await client.ConnectAsync();
            await Task.Delay(100);
            return -1; // server stops after
        }, "stop", IInvocationMethodHash.GetMethodHash<RecClientNexus>(), serverHash));

        frames.Add(await Record("client-mismatch", false, async (cfg) =>
        {
            var client = RecClientNexus2.CreateClient(cfg, new RecClientNexus2());
            var r = await client.TryConnectAsync();
            return (int)r.DisconnectReason;
        }, null, IInvocationMethodHash.GetMethodHash<RecClientNexus2>(), serverHash));

        frames.Add(await Record("server-mismatch", false, async (cfg) =>
        {
            var client = RecClientNexus3.CreateClient(cfg, new RecClientNexus3());
            var r = await client.TryConnectAsync();
            return (int)r.DisconnectReason;
        }, null, IInvocationMethodHash.GetMethodHash<RecClientNexus3>(), IInvocationMethodHash.GetMethodHash<RecServerNexus2>()));

        RecServerNexus.RejectAuth = true;
        frames.Add(await Record("authentication-rejected", true, async (cfg) =>
        {
            cfg.Authenticate = () => new byte[] { 0xde, 0xad };
            var client = RecClientNexus.CreateClient(cfg, new RecClientNexus());
            var r = await client.TryConnectAsync();
            return (int)r.DisconnectReason;
        }, null, IInvocationMethodHash.GetMethodHash<RecClientNexus>(), serverHash));

        await File.WriteAllLinesAsync(Path.Combine(outDir, "frames.jsonl"), frames);
        Console.WriteLine($"wrote {msgs.Count} messages, {frames.Count} recordings");
    }

    static async Task<string> Record(string scenario, bool auth, Func<TcpClientConfig, Task<int>> run, string? after,
        int clientNexusHash, int serverNexusHash)
    {
        var serverPort = FreePort();
        var server = RecServerNexus.CreateServer(new TcpServerConfig
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, serverPort),
            Authenticate = auth,
        }, () => new RecServerNexus());
        await server.StartAsync();

        var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var c2s = new MemoryStream();
        var s2c = new MemoryStream();
        var pumpTask = Task.Run(async () =>
        {
            using var inbound = await proxy.AcceptTcpClientAsync();
            using var outbound = new TcpClient();
            await outbound.ConnectAsync(IPAddress.Loopback, serverPort);
            async Task Pump(NetworkStream from, NetworkStream to, MemoryStream rec, TcpClient toClient)
            {
                var buf = new byte[65536];
                try
                {
                    while (true)
                    {
                        var n = await from.ReadAsync(buf);
                        if (n == 0) break;
                        lock (rec) rec.Write(buf, 0, n);
                        await to.WriteAsync(buf.AsMemory(0, n));
                    }
                }
                catch { }
                try { toClient.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
            await Task.WhenAll(Pump(inbound.GetStream(), outbound.GetStream(), c2s, outbound),
                Pump(outbound.GetStream(), inbound.GetStream(), s2c, inbound));
        });

        var cfg = new TcpClientConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, proxyPort) };
        var reason = await run(cfg);
        if (after == "stop")
        {
            await server.StopAsync();
            reason = (int)DisconnectReason.ServerShutdown;
        }
        await Task.WhenAny(pumpTask, Task.Delay(5000));
        if (after != "stop")
            await server.StopAsync();
        proxy.Stop();

        return JsonSerializer.Serialize(new
        {
            scenario,
            clientNexusHash,
            serverNexusHash,
            clientDisconnectReason = reason,
            clientToServer = Hex(c2s.ToArray()),
            serverToClient = Hex(s2c.ToArray()),
        });
    }
}
