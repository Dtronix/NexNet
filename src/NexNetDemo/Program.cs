using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NexNet;
using NexNet.Transports;

namespace NexNetDemo;

partial interface IClientNexus
{
}

partial interface IServerNexus
{
    ValueTask ServerTaskWithParam(NexusPipe pipe);
}



/// <summary>
/// Hub used for handling all Client communications.
/// </summary>
partial class ClientNexus : global::NexNet.Invocation.ClientNexusBase<global::NexNetDemo.ClientNexus.ServerProxy>, global::NexNetDemo.IClientNexus, global::NexNet.Invocation.IInvocationMethodHash
{
    /// <summary>
    /// Creates an instance of the client for this hub and matching server.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="nexus">Hub used for this client while communicating with the server. Useful to pass parameters to the hub.</param>
    /// <returns>NexNetClient for connecting to the matched NexNetServer.</returns>
    public static global::NexNet.NexusClient<global::NexNetDemo.ClientNexus, global::NexNetDemo.ClientNexus.ServerProxy> CreateClient(global::NexNet.Transports.ClientConfig config, ClientNexus nexus)
    {
        return new global::NexNet.NexusClient<global::NexNetDemo.ClientNexus, global::NexNetDemo.ClientNexus.ServerProxy>(config, nexus);
    }

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationRequestMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        try
        {
            switch (message.MethodId)
            {
            }
        }
        finally
        {
            if (cts != null)
            {
                var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<global::NexNetDemo.ClientNexus.ServerProxy>>(this);
                methodInvoker.ReturnCancellationToken(message.InvocationId);
            }
        }

    }

    /// <summary>
    /// Hash for this the methods on this proxy or hub.  Used to perform a simple client and server match check.
    /// </summary>
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 0; }

    /// <summary>
    /// Proxy invocation implementation for the matching hub.
    /// </summary>
    public class ServerProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNetDemo.IServerNexus, global::NexNet.Invocation.IInvocationMethodHash
    {
        public global::System.Threading.Tasks.ValueTask ServerTaskWithParam(global::NexNet.NexusPipe pipe)
        {
            //var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::NexNet.NexNetPipe>>(new(pipe));
            return ProxyInvokeAndWaitForResultCore(0, null, pipe, null);
        }

        /// <summary>
        /// Hash for this the methods on this proxy or hub.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 0; }
    }
}


partial class ClientNexus
{
    protected override ValueTask OnConnected(bool isReconnected)
    {
        var data = new byte[1 << 15];
        var number = 0;
        var direction = 1;
        for (int i = 0; i < data.Length; i++)
        {
            var delta = number += direction;
            if (direction > 0 && delta == 256)
            {
                direction = -1;
            }
            else if(direction < 0 && delta == 0)
            {
                direction = -1;
            }
            data[i] = (byte)delta;
        }

        Task.Run(async () =>
        {
            var pipe = NexusPipe.Create(async (writer, ct) =>
            {
                Memory<byte> randomData = data;
                var length = 1 << 14;
                while (true)
                {
                    //var size = Random.Shared.Next(1, 1024 * 32);
                    randomData.Slice(0, length).CopyTo(writer.GetMemory(length));
                    writer.Advance(length);
                    await writer.FlushAsync(ct);
                    //await writer.WriteAsync(randomData.Slice(0, 1024 * 60), ct);
                }
            });

            await this.Context.Proxy.ServerTaskWithParam(pipe);
        });

        return base.OnConnected(isReconnected);
    }
}


/// <summary>
/// Hub used for handling all Server communications.
/// </summary>
partial class ServerNexus : global::NexNet.Invocation.ServerNexusBase<global::NexNetDemo.ServerNexus.ClientProxy>, global::NexNetDemo.IServerNexus, global::NexNet.Invocation.IInvocationMethodHash
{
    /// <summary>
    /// Creates an instance of the server for this hub and matching client.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="hubFactory">Factory used to instance hubs for the server on each client connection. Useful to pass parameters to the hub.</param>
    /// <returns>NexNetServer for handling incoming connections.</returns>
    public static global::NexNet.NexusServer<global::NexNetDemo.ServerNexus, global::NexNetDemo.ServerNexus.ClientProxy> CreateServer(global::NexNet.Transports.ServerConfig config, global::System.Func<global::NexNetDemo.ServerNexus> hubFactory)
    {
        return new global::NexNet.NexusServer<global::NexNetDemo.ServerNexus, global::NexNetDemo.ServerNexus.ClientProxy>(config, hubFactory);
    }

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationRequestMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        NexusPipe? pipe = null;
        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<global::NexNetDemo.ServerNexus.ClientProxy>>(this);
        try
        {
            switch (message.MethodId)
            {
                case 0:
                {
                    pipe = methodInvoker.RegisterPipe(message.InvocationId);
                    await ServerTaskWithParam(pipe);
                }
                    break;
            }
        }
        finally
        {
            if (cts != null)
            {
                methodInvoker.ReturnCancellationToken(message.InvocationId);
            }

            if (pipe != null)
            {
                methodInvoker.ReturnPipe(message.InvocationId);
            }
        }

    }

    /// <summary>
    /// Hash for this the methods on this proxy or hub.  Used to perform a simple client and server match check.
    /// </summary>
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 0; }

    /// <summary>
    /// Proxy invocation implementation for the matching hub.
    /// </summary>
    public class ClientProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNetDemo.IClientNexus, global::NexNet.Invocation.IInvocationMethodHash
    {

        /// <summary>
        /// Hash for this the methods on this proxy or hub.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 0; }
    }
}



partial class ServerNexus : IServerNexus
{
    private long _readData = 0;

    double ApproxRollingAverage(double avg, double new_sample)
    {

        avg -= avg / 100;
        avg += new_sample / 100;

        return avg;
    }
    public async ValueTask ServerTaskWithParam(NexusPipe pipe)
    {
        long sentBytes = 0;
        int loopNumber = 0;
        double average = 0;
        var sw = new Stopwatch();
        while (true)
        {
            sw.Start();
            var data = await pipe.Reader.ReadAsync();


            if (data.IsCanceled || data.IsCompleted)
                return;

            pipe.Reader.AdvanceTo(data.Buffer.End);

            _readData += data.Buffer.Length;

            sentBytes += data.Buffer.Length;
            if (loopNumber++ == 300)
            {
                var ellapsedms = sw.ElapsedMilliseconds;
                var value = ((sentBytes / 1024d / 1024d) / (ellapsedms / 1000d));

                average = ApproxRollingAverage(average, value);
                Console.WriteLine($"{average:F} MBps");
                Console.SetCursorPosition(0,0);
                sw.Restart();
                sentBytes = 0;
                loopNumber = 0;
            }
        }
    }
}

class LoggerAdapter : INexusLogger
{
    private readonly ILogger _logger;

    public LoggerAdapter(ILogger logger)
    {
        _logger = logger;
    }
    public void Log(INexusLogger.LogLevel logLevel, Exception? exception, string message)
    {
        _logger.Log((LogLevel)logLevel, exception, message);
    }
}

internal class Program
{
  static async Task Main(string[] args)
  {
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
        */
        /*
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

        var server = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());

        server.Start();

        var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());

        try
        {
            await client.ConnectAsync();
        }
        catch (Exception)
        {
            //Console.WriteLine(e);
            throw;
        }

        Console.ReadLine();
        
    }
}
