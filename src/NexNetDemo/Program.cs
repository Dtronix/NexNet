using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NexNet;
using NexNet.Transports;

namespace NexNetDemo;

partial interface IClientHub
{
    void Update();
    ValueTask<int> GetTask();
    ValueTask<int> GetTaskAgain();
}

partial interface IServerHub
{
    void ServerVoid();
    void ServerVoidWithParam(int id);
    ValueTask ServerTask();
    ValueTask ServerTaskWithParam(int data);
    ValueTask<int> ServerTaskValue();
    ValueTask<int> ServerTaskValueWithParam(int data);
    ValueTask ServerTaskWithCancellation(CancellationToken cancellationToken);
    ValueTask ServerTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask<int> ServerTaskValueWithCancellation(CancellationToken cancellationToken);
    ValueTask<int> ServerTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
}





[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub
{
    private int i = 0;
    public void Update()
    {
        global::System.Int32 t = 25;

        //Console.WriteLine("ClientHub Update called and invoked properly.");
    }

    public ValueTask<int> GetTask()
    {
        //Console.WriteLine(i++);
        return ValueTask.FromResult(i);
    }
    public ValueTask<int> GetTaskAgain()
    {
        return ValueTask.FromResult(Interlocked.Increment(ref i));
    }

    protected override async ValueTask OnConnected(bool isReconnected)
    {
        for (int j = 0; j < 10000; j++)
        {
  
            switch (Random.Shared.Next(0, 5))
            {
                case 0:
                    //Console.WriteLine("ServerVoid()");
                    Context.Proxy.ServerVoid();
                    break;
                case 1:
                    //Console.WriteLine("ServerVoidWithParam(10)");
                    Context.Proxy.ServerVoidWithParam(10);
                    break;
                case 2:
                    //Console.WriteLine("ServerTaskWithParam(20)");
                    await Context.Proxy.ServerTaskWithParam(20);
                    break;
                case 3:
                    //Console.WriteLine("ServerTaskValue()");
                    await Context.Proxy.ServerTaskValue();
                    break;    
                case 4:
                    // Problem
                    //Console.WriteLine("ServerTaskValueWithParam(30)");
                    await Context.Proxy.ServerTaskValueWithParam(30);
                    break;
                case 5:
                    //Console.WriteLine("ServerTaskWithCancellation(CancellationToken.None)");
                    await Context.Proxy.ServerTaskWithCancellation(CancellationToken.None);
                    break;
                case 6:
                    //Console.WriteLine("ServerTaskWithValueAndCancellation(40, CancellationToken.None)");
                    await Context.Proxy.ServerTaskWithValueAndCancellation(40, CancellationToken.None);
                    break;
                case 7:
                    //Console.WriteLine("ServerTaskValueWithCancellation(CancellationToken.None)");
                    await Context.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
                    break;
                case 8:
                    //Console.WriteLine("ServerTaskValueWithValueAndCancellation(40, CancellationToken.None)");
                    await Context.Proxy.ServerTaskValueWithValueAndCancellation(40, CancellationToken.None);
                    break;
            }

        }

    }

}

[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
{
    private int i = 0;
    public void ServerVoid()
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + ") ServerVoid()");
    }

    public void ServerVoidWithParam(int id)
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerVoidWithParam({id})");
    }

    public ValueTask ServerTask()
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerTask()");
        return ValueTask.CompletedTask;
    }

    public ValueTask ServerTaskWithParam(int data)
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerTaskWithParam({data})");
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ServerTaskValue()
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerTaskValue()");
        return ValueTask.FromResult(i);
    }

    public ValueTask<int> ServerTaskValueWithParam(int data)
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerTaskValueWithParam({data})");
        return ValueTask.FromResult(i);
    }

    public async ValueTask ServerTaskWithCancellation(CancellationToken cancellationToken)
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerTaskWithCancellation(CancellationToken)");
        try
        {
            await Task.Delay(10, cancellationToken);
        }
        catch (TaskCanceledException e)
        {
            throw;
        }
    }

    public async ValueTask ServerTaskWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerTaskWithValueAndCancellation({value}, CancellationToken)");
        try
        {
            await Task.Delay(10, cancellationToken);
        }
        catch (TaskCanceledException e)
        {
            throw;
        }
    }

    public async ValueTask<int> ServerTaskValueWithCancellation(CancellationToken cancellationToken)
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerTaskWithCancellation(CancellationToken)");
        try
        {
            await Task.Delay(10, cancellationToken);
        }
        catch (TaskCanceledException e)
        {
            throw;
        }

        return i2;
    }

    public async ValueTask<int> ServerTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        var i2 = Interlocked.Increment(ref i);
        Console.WriteLine(i2 + $") ServerTaskWithValueAndCancellation({value}, CancellationToken)");
        try
        {
            await Task.Delay(10, cancellationToken);
        }
        catch (TaskCanceledException e)
        {
            throw;
        }
        return i2;
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        return ValueTask.CompletedTask;
    }
}

class LoggerAdapter : INexNetLogger
{
    private readonly ILogger _logger;

    public LoggerAdapter(ILogger logger)
    {
        _logger = logger;
    }
    public void Log(INexNetLogger.LogLevel logLevel, Exception? exception, string message)
    {
        _logger.Log((LogLevel)logLevel, exception, message);
    }
}

internal class Program
{
  static async Task Main(string[] args)
  {

      var type = typeof(NexNetHubAttribute<,>);
        var path = "test.sock";
        if (File.Exists(path))
            File.Delete(path);

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddFilter(level => true).AddConsole();
        });
        
        var serverConfig = new UdsServerConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            //Logger = new LoggerAdapter(loggerFactory.CreateLogger("SV"))
        };
        var clientConfig = new UdsClientConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            //Logger = new LoggerAdapter(loggerFactory.CreateLogger("CL"))
        };
        /*
        var serverConfig = new TcpServerConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
            //Logger = loggerFactory.CreateLogger("SV")
        };
        var clientConfig = new TcpClientConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
            //Logger = loggerFactory.CreateLogger("CL")
        };
        *//*
        var serverConfig = new TcpTlsServerConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
            //Logger = new LoggerAdapter(loggerFactory.CreateLogger("SV")),
            SslServerAuthenticationOptions = new SslServerAuthenticationOptions()
            {
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ClientCertificateRequired = false,
                AllowRenegotiation = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ServerCertificate = new X509Certificate2("server.pfx", "certPass"),
            },
        };
        var clientConfig = new TcpTlsClientConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
            //Logger = new LoggerAdapter(loggerFactory.CreateLogger("CL")),
            SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
            }
        };
        */

        var server = ServerHub.CreateServer(serverConfig, () => new ServerHub());

        server.Start();

        var client = ClientHub.CreateClient(clientConfig, new ClientHub());

        try
        {
            await client.ConnectAsync();
        }
        catch (Exception e)
        {
            //Console.WriteLine(e);
            throw;
        }

        Console.ReadLine();
        
    }
}
