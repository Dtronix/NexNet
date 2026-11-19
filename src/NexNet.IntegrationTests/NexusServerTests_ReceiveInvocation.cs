using System.Buffers;
using System.Numerics;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
using NexNet.Serialization;
using NexNet.TestNexus.Interop;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The server receives invocations from a client under test (P01b): every row of the client matrix calls the server
/// nexus, which observes the call and its arguments.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusServerTests_ReceiveInvocation(LanguageHostPool hosts) : InvocationMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerVoid(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerVoidEvent = _ => tcs.TrySetResult(),
            proxy =>
            {
                proxy.ServerVoid();
                return ValueTask.CompletedTask;
            });
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerVoidWithParam(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerVoidWithParamEvent = (_, param) =>
            {
                if (param == 12345)
                    tcs.TrySetResult();
            },
            proxy =>
            {
                proxy.ServerVoidWithParam(12345);
                return ValueTask.CompletedTask;
            });
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerTask(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerTaskEvent = _ =>
            {
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            },
            proxy => proxy.ServerTask());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerTaskWithParam(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerTaskWithParamEvent = (_, param) =>
            {
                if (param == 12345)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            },
            proxy => proxy.ServerTaskWithParam(12345));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerTaskValue(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerTaskValueEvent = _ =>
            {
                tcs.TrySetResult();
                return ValueTask.FromResult(54321);
            },
            async proxy => await proxy.ServerTaskValue());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerTaskValue_ReturnedValue(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) =>
            {
                sNexus.ServerTaskValueEvent = _ => ValueTask.FromResult(54321);
                tcs.TrySetResult();
            },
            async proxy => await Assert.That(await proxy.ServerTaskValue()).IsEqualTo(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerTaskValueWithParam(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerTaskValueWithParamEvent = (_, param) =>
            {
                if (param == 12345)
                    tcs.TrySetResult();
                return ValueTask.FromResult(54321);
            },
            async proxy => await proxy.ServerTaskValueWithParam(12345));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerTaskValueWithParam_ReturnedValue(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) =>
            {
                sNexus.ServerTaskValueWithParamEvent = (_, param) => ValueTask.FromResult(param == 12345 ? 54321 : -1);
                tcs.TrySetResult();
            },
            async proxy => await Assert.That(await proxy.ServerTaskValueWithParam(12345)).IsEqualTo(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerTaskWithCancellation(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerTaskWithCancellationEvent = (_, _) =>
            {
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            },
            proxy => proxy.ServerTaskWithCancellation(CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ServerTaskWithValueAndCancellation(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerTaskWithValueAndCancellationEvent = (_, param, _) =>
            {
                if (param == 12345)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            },
            proxy => proxy.ServerTaskWithValueAndCancellation(12345, CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ClientTaskValueWithCancellation(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerTaskValueWithCancellationEvent = (_, _) =>
            {
                tcs.TrySetResult();
                return ValueTask.FromResult(54321);
            },
            async proxy => await proxy.ServerTaskValueWithCancellation(CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ClientTaskValueWithCancellation_ReturnedValue(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) =>
            {
                sNexus.ServerTaskValueWithCancellationEvent = (_, _) => ValueTask.FromResult(54321);
                tcs.TrySetResult();
            },
            async proxy => await Assert.That(await proxy.ServerTaskValueWithCancellation(CancellationToken.None)).IsEqualTo(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ClientTaskValueWithValueAndCancellation(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) => sNexus.ServerTaskValueWithValueAndCancellationEvent = (_, param, _) =>
            {
                if (param == 12345)
                    tcs.TrySetResult();
                return ValueTask.FromResult(54321);
            },
            async proxy => await proxy.ServerTaskValueWithValueAndCancellation(12345, CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerReceivesInvocation_ClientTaskValueWithValueAndCancellation_ReturnedValue(ClientTarget target)
    {
        return ServerReceivesInvocation(target, (sNexus, tcs) =>
            {
                sNexus.ServerTaskValueWithValueAndCancellationEvent = (_, param, _) => ValueTask.FromResult(param == 12345 ? 54321 : -1);
                tcs.TrySetResult();
            },
            async proxy => await Assert.That(await proxy.ServerTaskValueWithValueAndCancellation(12345, CancellationToken.None)).IsEqualTo(54321));
    }

    /// <summary>
    /// The live counterpart of the golden vectors: every echo method of the interop type zoo, called by the client
    /// under test with seeded random values, returns its argument unchanged (compared as NexNet bytes, or as sets and
    /// maps where a language may reorder them).
    /// </summary>
    [Test]
    [ClientMatrix]
    public async Task ServerReceivesInvocation_EveryZooType_EchoesSeededValues(ClientTarget target)
    {
        await using var server = await InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus())
            .Timeout(T(TestTimeouts.Connect));
        await using var client = await Harness.CreateClientAsync(target, ClientSpec.ForEndpoint(server.Endpoints, target.Transport));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var proxy = client.Proxy<IInteropServerNexus>();

        var failures = new List<string>();
        foreach (var seed in new[] { 1, 2, 3 })
            await ZooEcho.RunAsync(proxy, new Random(seed), "seed " + seed, failures).Timeout(T(30));

        await Assert.That(failures).IsEmpty().Because(string.Join(Environment.NewLine, failures));
        await DisconnectAndAudit(client);
    }

    private async Task ServerReceivesInvocation(
        ClientTarget target,
        Action<ServerNexus, TaskCompletionSource> configureNexus,
        Func<Remote_IServerNexus, ValueTask> invoke)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(nexus => configureNexus(nexus, tcs));
        await using var client = await Connect(target, server);

        await invoke(Proxy(client)).AsTask().Timeout(T(1));
        await tcs.Task.Timeout(T(1));
        await DisconnectAndAudit(client);
    }

    // ----- C#-only QUIC companions: the interop server has no QUIC endpoint yet, so the C# client keeps its QUIC
    // coverage of these tests here until the client matrix gains C#-only rows (H16). -----

    
    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ServerVoid_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = (nexus, _) =>
            {
                nexus.Context.Proxy.ServerVoid();
                return ValueTask.CompletedTask;
            };
            sNexus.ServerVoidEvent = nexus => tcs.SetResult();
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ServerVoidWithParam_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = (nexus, _) =>
            {
                nexus.Context.Proxy.ServerVoidWithParam(12345);
                return ValueTask.CompletedTask;
            };
            sNexus.ServerVoidWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ServerTask_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = (nexus, _) => nexus.Context.Proxy.ServerTask();
            sNexus.ServerTaskEvent = nexus =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ServerTaskWithParam_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = (nexus, _) => nexus.Context.Proxy.ServerTaskWithParam(12345);
            sNexus.ServerTaskWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ServerTaskValue_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskValue();
            sNexus.ServerTaskValueEvent = nexus =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ServerReceivesInvocation_ServerTaskValue_ReturnedValue_Quic(Type type)
    {
        await ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) =>
            {
                var value = await nexus.Context.Proxy.ServerTaskValue();
                await Assert.That(value).IsEqualTo(54321);
                tcs.SetResult();
            };
            sNexus.ServerTaskValueEvent = nexus => ValueTask.FromResult(54321);
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ServerTaskValueWithParam_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskValueWithParam(12345);
            sNexus.ServerTaskValueWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return ValueTask.FromResult(54321);
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ServerReceivesInvocation_ServerTaskValueWithParam_ReturnedValue_Quic(Type type)
    {
        await ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) =>
            {
                var value = await nexus.Context.Proxy.ServerTaskValueWithParam(12345);
                await Assert.That(value).IsEqualTo(54321);
                tcs.SetResult();
            };
            sNexus.ServerTaskValueWithParamEvent = (nexus, param) => ValueTask.FromResult(54321);
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ServerTaskWithCancellation_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskWithCancellation(CancellationToken.None);
            sNexus.ServerTaskWithCancellationEvent = (nexus, ct) =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ServerTaskWithValueAndCancellation_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskWithValueAndCancellation(12345, CancellationToken.None);
            sNexus.ServerTaskWithValueAndCancellationEvent = (nexus, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ClientTaskValueWithCancellation_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
            sNexus.ServerTaskValueWithCancellationEvent = (nexus, ct) =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ServerReceivesInvocation_ClientTaskValueWithCancellation_ReturnedValue_Quic(Type type)
    {
        await ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) =>
            {
                var value = await nexus.Context.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
                await Assert.That(value).IsEqualTo(54321);
                tcs.SetResult();
            };
            sNexus.ServerTaskValueWithCancellationEvent = (nexus, ct) => ValueTask.FromResult(54321);
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ServerReceivesInvocation_ClientTaskValueWithValueAndCancellation_Quic(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskValueWithValueAndCancellation(12345, CancellationToken.None);
            sNexus.ServerTaskValueWithValueAndCancellationEvent = (nexus, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return ValueTask.FromResult(54321);
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ServerReceivesInvocation_ClientTaskValueWithValueAndCancellation_ReturnedValue_Quic(Type type)
    {
        await ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) =>
            {
                var value = await nexus.Context.Proxy.ServerTaskValueWithValueAndCancellation(12345, CancellationToken.None);
                await Assert.That(value).IsEqualTo(54321);
                tcs.SetResult();
            };
            sNexus.ServerTaskValueWithValueAndCancellationEvent = (nexus, param, ct) => ValueTask.FromResult(54321);
        });
    }

    private async Task ServerReceivesInvocation(Type type, Action<ServerNexus, ClientNexus, TaskCompletionSource> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));
        
        server.OnNexusCreated = nexus => action(nexus, clientNexus, tcs);
        
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await tcs.Task.Timeout(T(1));
    }
}

/// <summary>
/// Base of the invocation tests on the client matrix (P01b): a dedicated interop server of the integration tests'
/// <see cref="ServerNexus"/> on every interop endpoint, and clients under test of <see cref="ClientNexus"/> created
/// through <see cref="Harness"/> (the C# client in process, or a language host's client).
/// </summary>
/// <remarks>
/// Derived classes take the <see cref="LanguageHostPool"/> (<c>[ClassDataSource&lt;LanguageHostPool&gt;(Shared =
/// SharedType.PerTestSession)]</c>), so the hosts of NEXNET_TEST_LANGS are probed before their matrix rows are built.
/// </remarks>
internal abstract class InvocationMatrixTests(LanguageHostPool hosts) : BaseTests
{
    /// <summary>The language hosts of the test session; empty when only the C# row runs.</summary>
    protected LanguageHostPool Hosts { get; } = hosts;

    /// <summary>
    /// Starts a dedicated interop server of <see cref="ServerNexus"/>. Every server method completes with its default
    /// result unless <paramref name="configureNexus"/> sets its handler.
    /// </summary>
    /// <param name="configureNexus">Configures the nexus of each session.</param>
    /// <param name="configureEndpoint">Configures each endpoint's server configuration (limits, taps).</param>
    protected static Task<InteropServer<ServerNexus, ServerNexus.ClientProxy>> StartServer(
        Action<ServerNexus>? configureNexus = null,
        Action<ServerConfig>? configureEndpoint = null)
    {
        return InteropServerHost.StartAsync<ServerNexus, ServerNexus.ClientProxy>(
            () =>
            {
                var nexus = new ServerNexus();
                CompleteByDefault(nexus);
                configureNexus?.Invoke(nexus);
                return nexus;
            },
            new InteropServerOptions { ConfigureEndpoint = configureEndpoint == null ? null : (_, config) => configureEndpoint(config) })
            .Timeout(T(TestTimeouts.Connect));
    }

    /// <summary>Creates a client under test of <paramref name="server"/>'s endpoint for the row; it is not connected.</summary>
    protected static Task<IClientUnderTest> CreateClient(
        ClientTarget target,
        InteropServer<ServerNexus, ServerNexus.ClientProxy> server,
        Action<ClientSpec>? configure = null)
    {
        var spec = ClientSpec.ForEndpoint(server.Endpoints, target.Transport);
        configure?.Invoke(spec);
        return Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);
    }

    /// <summary>Creates a client under test and connects it.</summary>
    protected static async Task<IClientUnderTest> Connect(
        ClientTarget target,
        InteropServer<ServerNexus, ServerNexus.ClientProxy> server,
        Action<ClientSpec>? configure = null)
    {
        var client = await CreateClient(target, server, configure);
        try
        {
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }

        return client;
    }

    /// <summary>The server proxy of <paramref name="client"/>.</summary>
    protected static Remote_IServerNexus Proxy(IClientUnderTest client) => client.Proxy<Remote_IServerNexus>();

    /// <summary>What <paramref name="client"/> does when the server invokes its client nexus.</summary>
    protected static ClientNexusScript Script(IClientUnderTest client) => client.Callbacks<ClientNexusScript>();

    /// <summary>Asserts that <paramref name="client"/> reaches (or stays in) <paramref name="state"/>.</summary>
    protected static async Task AssertState(IClientUnderTest client, ConnectionState state)
    {
        try
        {
            await client.WaitForStateAsync(state, T(1));
        }
        catch (TimeoutException)
        {
            // Reported by the assertion below.
        }

        await Assert.That(await client.GetStateAsync()).IsEqualTo(state);
    }

    /// <summary>Disconnects <paramref name="client"/> and asserts that it holds nothing more (leak audit).</summary>
    protected static async Task DisconnectAndAudit(IClientUnderTest client)
    {
        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
        var report = await client.EndTestAuditAsync().Timeout(T(5));
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }

    /// <summary>The single session of <paramref name="server"/>'s first connected client.</summary>
    protected static long FirstSessionId(InteropServer<ServerNexus, ServerNexus.ClientProxy> server)
    {
        using var context = server.Server.ContextProvider.Rent();
        return context.Proxy.GetIds().First();
    }

    private static void CompleteByDefault(ServerNexus nexus)
    {
        nexus.ServerVoidEvent = _ => { };
        nexus.ServerVoidWithParamEvent = (_, _) => { };
        nexus.ServerTaskEvent = _ => ValueTask.CompletedTask;
        nexus.ServerTaskWithParamEvent = (_, _) => ValueTask.CompletedTask;
        nexus.ServerTaskValueEvent = _ => ValueTask.FromResult(0);
        nexus.ServerTaskValueWithParamEvent = (_, _) => ValueTask.FromResult(0);
        nexus.ServerTaskWithCancellationEvent = (_, _) => ValueTask.CompletedTask;
        nexus.ServerTaskWithValueAndCancellationEvent = (_, _, _) => ValueTask.CompletedTask;
        nexus.ServerTaskValueWithCancellationEvent = (_, _) => ValueTask.FromResult(0);
        nexus.ServerTaskValueWithValueAndCancellationEvent = (_, _, _) => ValueTask.FromResult(0);
    }
}

/// <summary>
/// Seeded random values for every echo method of <see cref="IInteropServerNexus"/>, and the comparison of what came
/// back. Values stay inside what every language represents exactly (finite floats, valid UTF-16 strings, UTC or
/// unspecified dates).
/// </summary>
internal static class ZooEcho
{
    private const string Alphabet = "abcXYZ019 _-éß中Ж";

    public static async Task RunAsync(IInteropServerNexus proxy, Random random, string context, List<string> failures)
    {
        async Task Echo<T>(string method, Func<T, ValueTask<T>> echo, T value, Func<T, T, bool>? equal = null)
        {
            T echoed;
            try
            {
                echoed = await echo(value).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                failures.Add($"{context}: {method} threw {e.GetType().Name}: {e.Message}");
                return;
            }

            if (!(equal?.Invoke(value, echoed) ?? Bytes(value).AsSpan().SequenceEqual(Bytes(echoed))))
                failures.Add($"{context}: {method} returned {Describe(echoed)} for {Describe(value)}");
        }

        var r = random;

        // ----- Built-in types -----
        await Echo("EchoBool", proxy.EchoBool, r.Next(2) == 1);
        await Echo("EchoSByte", proxy.EchoSByte, (sbyte)r.Next(sbyte.MinValue, sbyte.MaxValue + 1));
        await Echo("EchoByte", proxy.EchoByte, (byte)r.Next(256));
        await Echo("EchoInt16", proxy.EchoInt16, (short)r.Next(short.MinValue, short.MaxValue + 1));
        await Echo("EchoUInt16", proxy.EchoUInt16, (ushort)r.Next(ushort.MaxValue + 1));
        await Echo("EchoInt32", proxy.EchoInt32, Int32(r));
        await Echo("EchoUInt32", proxy.EchoUInt32, (uint)Int64(r));
        await Echo("EchoInt64", proxy.EchoInt64, Int64(r));
        await Echo("EchoUInt64", proxy.EchoUInt64, (ulong)Int64(r));
        await Echo("EchoSingle", proxy.EchoSingle, Single(r));
        await Echo("EchoDouble", proxy.EchoDouble, Double(r));
        await Echo("EchoHalf", proxy.EchoHalf, (Half)Single(r, 60000));
        await Echo("EchoChar", proxy.EchoChar, (char)r.Next(char.MaxValue + 1));
        await Echo("EchoDecimal", proxy.EchoDecimal, Decimal(r), (a, b) => decimal.GetBits(a).SequenceEqual(decimal.GetBits(b)));
        await Echo("EchoString", proxy.EchoString, NullOr(r, String(r)));
        await Echo("EchoBytes", proxy.EchoBytes, NullOr(r, ByteArray(r)));
        await Echo("EchoMemory", proxy.EchoMemory, new Memory<byte>(ByteArray(r)), (a, b) => a.Span.SequenceEqual(b.Span));
        await Echo("EchoReadOnlyMemory", proxy.EchoReadOnlyMemory, new ReadOnlyMemory<byte>(ByteArray(r)), (a, b) => a.Span.SequenceEqual(b.Span));
        await Echo("EchoReadOnlySequence", proxy.EchoReadOnlySequence, new ReadOnlySequence<byte>(ByteArray(r)), (a, b) => a.ToArray().AsSpan().SequenceEqual(b.ToArray()));
        var segmentSource = ByteArray(r, 8);
        var segmentStart = r.Next(segmentSource.Length + 1);
        await Echo("EchoArraySegment", proxy.EchoArraySegment, new ArraySegment<byte>(segmentSource, segmentStart, r.Next(segmentSource.Length - segmentStart + 1)),
            (a, b) => a.AsSpan().SequenceEqual(b.AsSpan()));
        await Echo("EchoDateTime", proxy.EchoDateTime, DateTime(r), (a, b) => a.Ticks == b.Ticks && a.Kind == b.Kind);
        await Echo("EchoDateTimeOffset", proxy.EchoDateTimeOffset, DateTimeOffset(r), (a, b) => a.Ticks == b.Ticks && a.Offset == b.Offset);
        await Echo("EchoTimeSpan", proxy.EchoTimeSpan, new TimeSpan(Int64(r)));
        await Echo("EchoDateOnly", proxy.EchoDateOnly, DateOnly.FromDayNumber(r.Next(DateOnly.MaxValue.DayNumber + 1)));
        await Echo("EchoTimeOnly", proxy.EchoTimeOnly, new TimeOnly(r.NextInt64(TimeSpan.TicksPerDay)));
        await Echo("EchoGuid", proxy.EchoGuid, Guid(r));
        await Echo("EchoBigInteger", proxy.EchoBigInteger, BigInteger(r));
        await Echo("EchoUri", proxy.EchoUri, NullOr(r, Uri(r)), (a, b) => a?.OriginalString == b?.OriginalString);
        await Echo("EchoVersion", proxy.EchoVersion, NullOr(r, Version(r)));
        await Echo("EchoValueTuple", proxy.EchoValueTuple, (Int32(r), NullOr(r, String(r)), Double(r)));
        await Echo("EchoValueTuple7", proxy.EchoValueTuple7, (Int32(r), Int64(r), String(r), r.Next(2) == 1, Double(r), Guid(r), (byte)r.Next(256)));
        await Echo("EchoTuple", proxy.EchoTuple, NullOr(r, Tuple.Create(Int32(r), String(r))));
        await Echo("EchoKeyValuePair", proxy.EchoKeyValuePair, new KeyValuePair<string, int>(String(r), Int32(r)));

        // ----- Primitive arrays -----
        await Echo("EchoSByteArray", proxy.EchoSByteArray, NullOr(r, Array(r, x => (sbyte)x.Next(sbyte.MinValue, sbyte.MaxValue + 1))));
        await Echo("EchoInt16Array", proxy.EchoInt16Array, NullOr(r, Array(r, x => (short)x.Next(short.MinValue, short.MaxValue + 1))));
        await Echo("EchoUInt16Array", proxy.EchoUInt16Array, NullOr(r, Array(r, x => (ushort)x.Next(ushort.MaxValue + 1))));
        await Echo("EchoInt32Array", proxy.EchoInt32Array, NullOr(r, Array(r, Int32)));
        await Echo("EchoUInt32Array", proxy.EchoUInt32Array, NullOr(r, Array(r, x => (uint)Int64(x))));
        await Echo("EchoInt64Array", proxy.EchoInt64Array, NullOr(r, Array(r, Int64)));
        await Echo("EchoUInt64Array", proxy.EchoUInt64Array, NullOr(r, Array(r, x => (ulong)Int64(x))));
        await Echo("EchoSingleArray", proxy.EchoSingleArray, NullOr(r, Array(r, x => Single(x))));
        await Echo("EchoDoubleArray", proxy.EchoDoubleArray, NullOr(r, Array(r, Double)));
        await Echo("EchoCharArray", proxy.EchoCharArray, NullOr(r, Array(r, x => (char)x.Next(char.MaxValue + 1))));
        await Echo("EchoHalfArray", proxy.EchoHalfArray, NullOr(r, Array(r, x => (Half)Single(x, 60000))));
        await Echo("EchoInt32List", proxy.EchoInt32List, NullOr(r, Array(r, Int32).ToList()));
        await Echo("EchoInt64Memory", proxy.EchoInt64Memory, new Memory<long>(Array(r, Int64)), (a, b) => a.Span.SequenceEqual(b.Span));
        await Echo("EchoDoubleReadOnlyMemory", proxy.EchoDoubleReadOnlyMemory, new ReadOnlyMemory<double>(Array(r, Double)), (a, b) => a.Span.SequenceEqual(b.Span));

        // ----- Containers -----
        await Echo("EchoStringArray", proxy.EchoStringArray, NullOr(r, Array(r, String)));
        await Echo("EchoStringList", proxy.EchoStringList, NullOr(r, Array(r, String).ToList()));
        await Echo("EchoStringIList", proxy.EchoStringIList, NullOr<IList<string>>(r, Array(r, String).ToList()));
        await Echo("EchoItemReadOnlyList", proxy.EchoItemReadOnlyList, NullOr<IReadOnlyList<InteropItem>>(r, Array(r, Item).ToList()));
        await Echo("EchoStringSet", proxy.EchoStringSet, NullOr(r, Array(r, String).ToHashSet()), SameSet);
        await Echo("EchoInt32Set", proxy.EchoInt32Set, NullOr(r, Array(r, Int32).ToHashSet()), SameSet);
        await Echo("EchoInt32Queue", proxy.EchoInt32Queue, NullOr(r, new Queue<int>(Array(r, Int32))));
        await Echo("EchoStringStack", proxy.EchoStringStack, NullOr(r, new Stack<string>(Array(r, String))));
        await Echo("EchoStringDictionary", proxy.EchoStringDictionary, NullOr(r, Map(r, String, String)), SameMap);
        await Echo("EchoInt32Dictionary", proxy.EchoInt32Dictionary, NullOr(r, Map(r, Int32, Item)), SameMap);
        await Echo("EchoGuidDictionary", proxy.EchoGuidDictionary, NullOr(r, Map(r, Guid, Int64)), SameMap);
        await Echo("EchoStringIDictionary", proxy.EchoStringIDictionary, NullOr<IDictionary<string, int>>(r, Map(r, String, Int32)), SameMap);
        await Echo("EchoInt32ReadOnlyDictionary", proxy.EchoInt32ReadOnlyDictionary, NullOr<IReadOnlyDictionary<int, string>>(r, Map(r, Int32, String)), SameMap);
        await Echo("EchoNestedContainers", proxy.EchoNestedContainers, NullOr(r, Map(r, String, x => Array(x, y => Array(y, Int32), 3).ToList(), 1)));

        // ----- Nullable variants -----
        await Echo("EchoNullableInt32", proxy.EchoNullableInt32, Maybe(r, Int32(r)));
        await Echo("EchoNullableInt64", proxy.EchoNullableInt64, Maybe(r, Int64(r)));
        await Echo("EchoNullableDouble", proxy.EchoNullableDouble, Maybe(r, Double(r)));
        await Echo("EchoNullableGuid", proxy.EchoNullableGuid, Maybe(r, Guid(r)));
        await Echo("EchoNullableDateTime", proxy.EchoNullableDateTime, Maybe(r, DateTime(r)),
            (a, b) => a?.Ticks == b?.Ticks && a?.Kind == b?.Kind);
        await Echo("EchoNullableEnum", proxy.EchoNullableEnum, Maybe(r, Enum<InteropEnum>(r)));
        await Echo("EchoNullableStruct", proxy.EchoNullableStruct, Maybe(r, Struct(r)));
        await Echo("EchoNullableInt32List", proxy.EchoNullableInt32List, NullOr(r, Array(r, x => x.Next(3) == 0 ? (int?)null : Int32(x)).ToList()));

        // ----- Enums -----
        await Echo("EchoEnum", proxy.EchoEnum, Enum<InteropEnum>(r));
        await Echo("EchoFlags", proxy.EchoFlags, (InteropFlags)(r.Next(16) & 7 | (r.Next(2) == 1 ? 0x8000 : 0)));
        await Echo("EchoLongEnum", proxy.EchoLongEnum, Enum<InteropLongEnum>(r));
        await Echo("EchoEnumArray", proxy.EchoEnumArray, NullOr(r, Array(r, Enum<InteropEnum>)));

        // ----- Objects, unions, generics, user formatter -----
        await Echo("EchoAllTypes", proxy.EchoAllTypes, NullOr(r, AllTypes(r)));
        await Echo("EchoStruct", proxy.EchoStruct, Struct(r));
        await Echo("EchoRecord", proxy.EchoRecord, NullOr(r, Record(r)));
        await Echo("EchoRecordStruct", proxy.EchoRecordStruct, new InteropRecordStruct(Int64(r), Guid(r)));
        await Echo("EchoSparse", proxy.EchoSparse, NullOr(r, new InteropSparse { First = Int32(r), Fourth = NullOr(r, String(r)) }));
        await Echo("EchoItem", proxy.EchoItem, NullOr(r, Item(r)));
        await Echo("EchoNested", proxy.EchoNested, NullOr(r, Nested(r, 2)));
        await Echo("EchoShape", proxy.EchoShape, NullOr(r, Shape(r)));
        await Echo("EchoShapes", proxy.EchoShapes, NullOr(r, Array(r, Shape).ToList()));
        await Echo("EchoMessage", proxy.EchoMessage, NullOr(r, Message(r)));
        await Echo("EchoStringBox", proxy.EchoStringBox, NullOr(r, new InteropBox<string> { Value = String(r) }));
        await Echo("EchoItemBox", proxy.EchoItemBox, NullOr(r, new InteropBox<InteropItem> { Value = Item(r) }));
        await Echo("EchoPair", proxy.EchoPair, NullOr(r, new InteropPair<int, InteropItem> { First = Int32(r), Second = NullOr(r, Array(r, Item).ToList()) }));
        await Echo("EchoPoint", proxy.EchoPoint, new InteropPoint(Int32(r), Int32(r)));
        await Echo("EchoPoints", proxy.EchoPoints, NullOr(r, Array(r, x => new InteropPoint(Int32(x), Int32(x))).ToList()));
    }

    private static byte[] Bytes<T>(T value) => NexusSerializer.Serialize(value);

    private static string Describe<T>(T value)
    {
        try
        {
            return Convert.ToHexString(Bytes(value));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "null";
        }
    }

    private static bool SameSet<T>(HashSet<T>? a, HashSet<T>? b) => a == null ? b == null : b != null && a.SetEquals(b);

    private static bool SameMap<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>>? a, IEnumerable<KeyValuePair<TKey, TValue>>? b)
        where TKey : notnull
    {
        if (a == null || b == null)
            return a == null && b == null;
        var left = a.ToDictionary(p => p.Key, p => Bytes(p.Value));
        var right = b.ToDictionary(p => p.Key, p => Bytes(p.Value));
        return left.Count == right.Count
               && left.All(p => right.TryGetValue(p.Key, out var other) && p.Value.AsSpan().SequenceEqual(other));
    }

    private static T? NullOr<T>(Random r, T value) where T : class => r.Next(5) == 0 ? null : value;

    private static T? Maybe<T>(Random r, T value) where T : struct => r.Next(3) == 0 ? null : value;

    private static int Int32(Random r) => r.Next(4) switch
    {
        0 => r.Next(-32, 128), // fixints
        1 => r.Next(short.MinValue, short.MaxValue),
        _ => (int)r.NextInt64(int.MinValue, (long)int.MaxValue + 1),
    };

    private static long Int64(Random r) => r.Next(3) switch
    {
        0 => r.Next(-1000, 1000),
        1 => r.NextInt64(-(1L << 53), 1L << 53),
        _ => BitConverter.ToInt64(ByteArray(r, 8, 8)),
    };

    private static float Single(Random r, float scale = 1e6f) => (float)((r.NextDouble() * 2 - 1) * scale);

    private static double Double(Random r) => r.Next(3) switch
    {
        0 => r.Next(-1000, 1000) / 8.0,
        1 => (r.NextDouble() * 2 - 1) * Math.Pow(10, r.Next(-300, 300)),
        _ => r.NextDouble(),
    };

    private static decimal Decimal(Random r) => new(r.Next(), r.Next(), r.Next(), r.Next(2) == 1, (byte)r.Next(29));

    private static string String(Random r)
    {
        var length = r.Next(0, 24);
        var chars = new System.Text.StringBuilder(length + 2);
        for (var i = 0; i < length; i++)
            chars.Append(Alphabet[r.Next(Alphabet.Length)]);
        if (r.Next(4) == 0)
            chars.Append(char.ConvertFromUtf32(0x1F600 + r.Next(64))); // a surrogate pair
        return chars.ToString();
    }

    private static byte[] ByteArray(Random r, int min = 0, int max = 64)
    {
        var bytes = new byte[r.Next(min, max + 1)];
        r.NextBytes(bytes);
        return bytes;
    }

    private static T[] Array<T>(Random r, Func<Random, T> item, int max = 8)
    {
        var items = new T[r.Next(0, max + 1)];
        for (var i = 0; i < items.Length; i++)
            items[i] = item(r);
        return items;
    }

    private static Dictionary<TKey, TValue> Map<TKey, TValue>(Random r, Func<Random, TKey> key, Func<Random, TValue> value, int max = 6)
        where TKey : notnull
    {
        var map = new Dictionary<TKey, TValue>();
        var count = r.Next(0, max + 1);
        for (var i = 0; i < count; i++)
            map[key(r)] = value(r);
        return map;
    }

    private static DateTime DateTime(Random r)
        => new(r.NextInt64(System.DateTime.MaxValue.Ticks + 1), r.Next(2) == 0 ? DateTimeKind.Utc : DateTimeKind.Unspecified);

    private static DateTimeOffset DateTimeOffset(Random r)
    {
        var offset = TimeSpan.FromMinutes(r.Next(-14 * 60, 14 * 60 + 1));
        var min = Math.Max(0, -offset.Ticks);
        var max = System.DateTime.MaxValue.Ticks - Math.Max(0, offset.Ticks);
        return new DateTimeOffset(r.NextInt64(min, max + 1) + offset.Ticks, offset);
    }

    private static Guid Guid(Random r) => new(ByteArray(r, 16, 16));

    private static BigInteger BigInteger(Random r) => new(ByteArray(r, 1, 40));

    private static Uri Uri(Random r) => r.Next(3) switch
    {
        0 => new Uri("https://example.com/p" + r.Next(1000) + "?q=" + r.Next()),
        1 => new Uri("urn:interop:" + r.Next()),
        _ => new Uri("ws://127.0.0.1:" + r.Next(1, 65536) + "/x"),
    };

    private static Version Version(Random r) => r.Next(3) switch
    {
        0 => new Version(r.Next(100), r.Next(100)),
        1 => new Version(r.Next(100), r.Next(100), r.Next(1000)),
        _ => new Version(r.Next(100), r.Next(100), r.Next(1000), r.Next(int.MaxValue)),
    };

    private static T Enum<T>(Random r) where T : struct, Enum
    {
        var values = System.Enum.GetValues<T>();
        return values[r.Next(values.Length)];
    }

    private static InteropItem Item(Random r) => new(Int32(r), NullOr(r, String(r)));

    private static InteropStruct Struct(Random r) => new() { X = Int32(r), Y = Double(r), Label = NullOr(r, String(r)) };

    private static InteropRecord Record(Random r) => new(String(r), Int32(r), Struct(r));

    private static InteropShape Shape(Random r) => r.Next(2) == 0
        ? new InteropCircle { Radius = Double(r) }
        : new InteropRectangle { Width = Double(r), Height = Double(r) };

    private static IInteropMessage Message(Random r) => r.Next(2) == 0
        ? new InteropTextMessage { Text = NullOr(r, String(r)) }
        : new InteropBinaryMessage { Data = NullOr(r, ByteArray(r)), Flags = (InteropFlags)(r.Next(8)) };

    private static InteropNested Nested(Random r, int depth) => new()
    {
        Record = NullOr(r, Record(r)),
        Items = NullOr(r, Array(r, Item).ToList()),
        // One entry at most: the bytes of a map depend on its order, which a language may not keep.
        ByIndex = NullOr(r, Map(r, Int32, Struct, 1)),
        Shape = NullOr(r, Shape(r)),
        Child = depth > 0 ? NullOr(r, Nested(r, depth - 1)) : null,
    };

    private static InteropAllTypes AllTypes(Random r) => new()
    {
        BoolValue = r.Next(2) == 1,
        SByteValue = (sbyte)r.Next(sbyte.MinValue, sbyte.MaxValue + 1),
        ByteValue = (byte)r.Next(256),
        Int16Value = (short)r.Next(short.MinValue, short.MaxValue + 1),
        UInt16Value = (ushort)r.Next(ushort.MaxValue + 1),
        Int32Value = Int32(r),
        UInt32Value = (uint)Int64(r),
        Int64Value = Int64(r),
        UInt64Value = (ulong)Int64(r),
        SingleValue = Single(r),
        DoubleValue = Double(r),
        HalfValue = (Half)Single(r, 60000),
        CharValue = (char)r.Next(char.MaxValue + 1),
        DecimalValue = Decimal(r),
        StringValue = NullOr(r, String(r)),
        BytesValue = NullOr(r, ByteArray(r)),
        DateTimeValue = DateTime(r),
        DateTimeOffsetValue = DateTimeOffset(r),
        TimeSpanValue = new TimeSpan(Int64(r)),
        DateOnlyValue = DateOnly.FromDayNumber(r.Next(DateOnly.MaxValue.DayNumber + 1)),
        TimeOnlyValue = new TimeOnly(r.NextInt64(TimeSpan.TicksPerDay)),
        GuidValue = Guid(r),
        BigIntegerValue = BigInteger(r),
        UriValue = NullOr(r, Uri(r)),
        VersionValue = NullOr(r, Version(r)),
        EnumValue = Enum<InteropEnum>(r),
        FlagsValue = (InteropFlags)(r.Next(8)),
        LongEnumValue = Enum<InteropLongEnum>(r),
        NullableInt32Value = Maybe(r, Int32(r)),
        Int32ArrayValue = NullOr(r, Array(r, Int32)),
        StringListValue = NullOr(r, Array(r, String).ToList()),
        // One entry at most: see Nested.
        StringMapValue = NullOr(r, Map(r, String, Int32, 1)),
    };
}
