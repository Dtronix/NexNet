using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using BenchmarkDotNet;
using BenchmarkDotNet.Attributes;
using NexNet;
using NexNet.IntegrationTests;
using NexNet.Transports;

namespace NexNetBenchmarks;

public class InvocationBenchmarks
{
    private NexusClient<ClientNexus, ClientNexus.ServerProxy> _client = null!;
    private NexusServer<ServerNexus, ServerNexus.ClientProxy> _server = null!;
    private ReadOnlyMemory<byte> _uploadBuffer;
    private ConsoleLogger _log = null!;
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _uploadBuffer = new byte[(1024 * 16)];
        var path = $"test.sock";
        if (File.Exists(path))
            File.Delete(path);

        _log = new ConsoleLogger() { };
        
        var serverConfig = new UdsServerConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            //Logger = _log.CreateLogger(null, "SV"),
        };
        var clientConfig = new UdsClientConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            //Logger = _log.CreateLogger(null, "CL"),
        };

        _client = ClientNexus.CreateClient(clientConfig, new ClientNexus());
        _server = ServerNexus.CreateServer(serverConfig, static () => new ServerNexus());
        await _server.StartAsync();
        await _client.ConnectAsync();
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _client.DisconnectAsync();
        await _server.StopAsync();
    }

    [Benchmark]
    public async ValueTask InvocationNoArgument()
    {
        await _client.Proxy.InvocationNoArgument();
    }

    [Benchmark]
    public async ValueTask InvocationUnmanagedArgument()
    {
        await _client.Proxy.InvocationUnmanagedArgument(12345);
    }

    [Benchmark]
    public async ValueTask InvocationUnmanagedMultipleArguments()
    {
        await _client.Proxy.InvocationUnmanagedMultipleArguments(12345, 128475129847, 24812, 298471920875185871, 19818479124.12871924821d);
    }

    [Benchmark]
    public async ValueTask InvocationNoArgumentWithResult()
    {
        await _client.Proxy.InvocationNoArgumentWithResult();
    }  
        
    [Benchmark]
    public async ValueTask InvocationWithDuplexPipe_Upload()
    {
        await using var pipe = _client.CreatePipe();
        await _client.Proxy.InvocationWithDuplexPipe_Upload(pipe);
        await pipe.ReadyTask.WaitAsync(TimeSpan.FromSeconds(1));
        await pipe.Output.WriteAsync(_uploadBuffer);
    }
}
