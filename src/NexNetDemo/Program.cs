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
using NexNet;
using NexNet.Transports;

namespace NexNetDemo;

interface IClientNexus
{
}

interface IServerNexus
{
    ValueTask ServerTaskWithParam(int id, INexusDuplexPipe pipe, string myValue);
}

/*
/// <summary>
/// Nexus used for handling all Client communications.
/// </summary>
partial class ClientNexus : global::NexNet.Invocation.ClientNexusBase<global::NexNetDemo.ClientNexus.ServerProxy>, global::NexNetDemo.IClientNexus, global::NexNet.Invocation.IInvocationMethodHash
{
    /// <summary>
    /// Creates an instance of the client for this nexus and matching server.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="nexus">Nexus used for this client while communicating with the server. Useful to pass parameters to the nexus.</param>
    /// <returns>NexusClient for connecting to the matched NexusServer.</returns>
    public static global::NexNet.NexusClient<global::NexNetDemo.ClientNexus, global::NexNetDemo.ClientNexus.ServerProxy> CreateClient(global::NexNet.Transports.ClientConfig config, ClientNexus nexus)
    {
        return new global::NexNet.NexusClient<global::NexNetDemo.ClientNexus, global::NexNetDemo.ClientNexus.ServerProxy>(config, nexus);
    }

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        global::NexNet.NexusPipe? pipe = null;
        global::NexNet.INexusDuplexPipe? duplexPipe = null;
        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<global::NexNetDemo.ClientNexus.ServerProxy>>(this);
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
                methodInvoker.ReturnCancellationToken(message.InvocationId);
            }

            if (pipe != null)
            {
                await methodInvoker.ReturnPipeReader(message.InvocationId);
            }

            if (duplexPipe != null)
            {
                await methodInvoker.ReturnDuplexPipe(duplexPipe);
            }
        }

    }

    /// <summary>
    /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
    /// </summary>
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 0; }

    /// <summary>
    /// Proxy invocation implementation for the matching nexus.
    /// </summary>
    public class ServerProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNetDemo.IServerNexus, global::NexNet.Invocation.IInvocationMethodHash
    {
        public global::System.Threading.Tasks.ValueTask ServerTaskWithParam(global::System.Int32 id, global::NexNet.INexusDuplexPipe pipe, global::System.String myValue)
        {
            var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::System.Int32, global::System.Byte, global::System.String>>(new(id, ProxyGetDuplexPipeInitialId(pipe), myValue));
            return ProxyInvokeAndWaitForResultCore(0, arguments, null, null);
        }

        /// <summary>
        /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => -1285947807; }
    }
}


/// <summary>
/// Nexus used for handling all Server communications.
/// </summary>
partial class ServerNexus : global::NexNet.Invocation.ServerNexusBase<global::NexNetDemo.ServerNexus.ClientProxy>, global::NexNetDemo.IServerNexus, global::NexNet.Invocation.IInvocationMethodHash
{
    /// <summary>
    /// Creates an instance of the server for this nexus and matching client.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="nexusFactory">Factory used to instance nexuses for the server on each client connection. Useful to pass parameters to the nexus.</param>
    /// <returns>NexusServer for handling incoming connections.</returns>
    public static global::NexNet.NexusServer<global::NexNetDemo.ServerNexus, global::NexNetDemo.ServerNexus.ClientProxy> CreateServer(global::NexNet.Transports.ServerConfig config, global::System.Func<global::NexNetDemo.ServerNexus> nexusFactory)
    {
        return new global::NexNet.NexusServer<global::NexNetDemo.ServerNexus, global::NexNetDemo.ServerNexus.ClientProxy>(config, nexusFactory);
    }

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        global::NexNet.NexusPipe? pipe = null;
        global::NexNet.INexusDuplexPipe? duplexPipe = null;
        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<global::NexNetDemo.ServerNexus.ClientProxy>>(this);
        try
        {
            switch (message.MethodId)
            {
                case 0:
                    {
                        // ValueTask ServerTaskWithParam(int id, NexusDuplexPipe pipe, string myValue)
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32, global::System.Byte, global::System.String>>();
                        duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item2);
                        await ServerTaskWithParam(arguments.Item1, duplexPipe, arguments.Item3);
                        break;
                    }
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
                await methodInvoker.ReturnPipeReader(message.InvocationId);
            }

            if (duplexPipe != null)
            {
                await methodInvoker.ReturnDuplexPipe(duplexPipe);
            }
        }

    }

    /// <summary>
    /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
    /// </summary>
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => -1285947807; }

    /// <summary>
    /// Proxy invocation implementation for the matching nexus.
    /// </summary>
    public class ClientProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNetDemo.IClientNexus, global::NexNet.Invocation.IInvocationMethodHash
    {

        /// <summary>
        /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 0; }
    }
}*/


[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
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
            else if (direction < 0 && delta == 0)
            {
                direction = -1;
            }
            data[i] = (byte)delta;
        }

        Task.Run(async () =>
        {

            var pipe = this.Context.CreatePipe(async pipe =>
            {
                Memory<byte> randomData = data;
                var length = 1024 * 32;

                var loopNumber = 0;
                while (true)
                {
                    //var size = Random.Shared.Next(1, 1024 * 32);
                    randomData.Slice(0, length).CopyTo(pipe.Output.GetMemory(length));
                    pipe.Output.Advance(length);
                    await pipe.Output.FlushAsync();
                    //await Task.Delay(10);

                    if (loopNumber == 100000)
                    {
                        await pipe.Output.CompleteAsync();
                        return;
                    }
                    //await writer.WriteAsync(randomData.Slice(0, 1024 * 60), ct);
                }
            });

            await this.Context.Proxy.ServerTaskWithParam(214, pipe, "Vl");
        });

        return base.OnConnected(isReconnected);
    }
}

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    private long _readData = 0;

    double ApproxRollingAverage(double avg, double newSample)
    {

        avg -= avg / 100;
        avg += newSample / 100;

        return avg;
    }
    public async ValueTask ServerTaskWithParam(int id, INexusDuplexPipe pipe, string myValue)
    {
        long sentBytes = 0;
        int loopNumber = 0;
        double average = 0;
        var sw = new Stopwatch();
        while (true)
        {
            sw.Start();
            var data = await pipe.Input.ReadAsync();

            if (data.IsCanceled || data.IsCompleted)
                return;

            pipe.Input.AdvanceTo(data.Buffer.End);

            _readData += data.Buffer.Length;

            //Console.Write($"{sentBytes:D} Read from Pipe");
            //Console.SetCursorPosition(0, 0);

            sentBytes += data.Buffer.Length;
            if (loopNumber++ == 800)
            {
                var ellapsedms = sw.ElapsedMilliseconds;
                var value = ((sentBytes / 1024d / 1024d) / (ellapsedms / 1000d));

                average = ApproxRollingAverage(average, value);
                Console.Write($"{average:F} MBps");
                Console.SetCursorPosition(0,0);
                sw.Restart();
                sentBytes = 0;
                loopNumber = 0;
            }
        }
    }
}

class Logger : INexusLogger
{
    private readonly string _prefix;

    public Logger(string prefix)
    {
        _prefix = prefix;
    }
    public void Log(INexusLogger.LogLevel logLevel, Exception? exception, string message)
    {
        if (logLevel < INexusLogger.LogLevel.Trace)
            return;

        Console.WriteLine($"{_prefix} {logLevel}: {message} {exception}");
    }
}

internal class Program
{
  static async Task Main(string[] args)
  {
      var path = "test.sock";
        if (File.Exists(path))
            File.Delete(path);
        
        var serverConfig = new UdsServerConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path), 
            //Logger = new Logger("SV")
        };
        var clientConfig = new UdsClientConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            //Logger = new Logger("CL")
        };
        /*
        var serverConfig = new TcpServerConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
            //Logger = new Logger("SV")
        };
        var clientConfig = new TcpClientConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
            //Logger = new Logger("CL")
        };
        
        
        var serverConfig = new TcpTlsServerConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
            //Logger = new Logger("SV"),
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
            //Logger = new Logger("CL"),
            SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
            }
        };*/


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
