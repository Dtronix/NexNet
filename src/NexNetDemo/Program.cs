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
using System.Text;
using NexNet;
using NexNet.Messages;
using NexNet.Transports;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNetDemo;

interface IClientNexus
{

}

interface IServerNexus
{
    ValueTask ServerTaskWithParam(int id, INexusDuplexPipe pipe, string myValue);
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus
{
    protected override async ValueTask OnConnected(bool isReconnected)
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

                if (loopNumber++ == 1000000)
                {
                    await pipe.Output.CompleteAsync();
                    return;
                }
                //await writer.WriteAsync(randomData.Slice(0, 1024 * 60), ct);
            }
        });

        await this.Context.Proxy.ServerTaskWithParam(214, pipe, "Vl");
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
    static void RunTest(string testName, Action action)
    {
        Console.WriteLine($"Running {testName}");
        var sw = Stopwatch.StartNew();
        action();
        Console.WriteLine($"Completed {testName} in {sw.ElapsedMilliseconds}");
    }

    static async Task Main(string[] args)
    {

        //RoughBenchmark();
        //Console.ReadLine();
        //return;
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

    static void RoughBenchmark()
    {
        var runs = 10000000;

        RunTest("New", () =>
        {
            var bufferWriter = BufferWriter<byte>.Create(128);
            var bufferSize = 0;

            var increaseSize = 201;
            var decreaseSize = 67;

            for (int i = 0; i < runs; i++)
            {
                bufferSize += increaseSize;
                var span = bufferWriter.GetSpan(increaseSize).Slice(0, increaseSize);
                bufferWriter.Advance(increaseSize);

                var addBufferLen = bufferWriter.GetBuffer().Length;
                if (bufferSize != addBufferLen)
                    Console.WriteLine($"bufferSize: {bufferSize} != addBufferLen: {addBufferLen}");

                bufferWriter.ReleaseTo(decreaseSize);

                bufferSize -= decreaseSize;

                var bufferLen = bufferWriter.GetBuffer().Length;

                if (bufferSize != bufferLen)
                    Console.WriteLine($"bufferSize: {bufferSize} != bufferLen: {bufferLen}");

                if (bufferLen > 1024 * 32)
                    decreaseSize = 529;
                else if (bufferLen < 1024 * 16)
                    decreaseSize = 67;
            }
        });

        RunTest("Old", () =>
        {
            var bufferWriter = BufferWriter<byte>.Create(128);
            var bufferSize = 0;

            var increaseSize = 201;
            var decreaseSize = 67;

            for (int i = 0; i < runs; i++)
            {
                bufferSize += increaseSize;
                var span = bufferWriter.GetSpan(increaseSize).Slice(0, increaseSize);
                bufferWriter.Advance(increaseSize);

                var addBuffer = bufferWriter.GetBuffer();
                var addBufferLen = addBuffer.Length;
                if (bufferSize != addBufferLen)
                    Console.WriteLine($"bufferSize: {bufferSize} != addBufferLen: {addBufferLen}");

                bufferWriter.Deallocate(addBuffer.Slice(0, decreaseSize));

                bufferSize -= decreaseSize;

                var bufferLen = bufferWriter.GetBuffer().Length;

                if (bufferSize != bufferLen)
                    Console.WriteLine($"bufferSize: {bufferSize} != bufferLen: {bufferLen}");

                if (bufferLen > 1024 * 32)
                    decreaseSize = 529;
                else if (bufferLen < 1024 * 16)
                    decreaseSize = 67;
            }
        });

        RunTest("Original", () =>
        {
            var bufferWriter = BufferWriter<byte>.Create(128);
            var bufferSize = 0;

            var increaseSize = 201;
            var decreaseSize = 67;

            for (int i = 0; i < runs; i++)
            {
                bufferSize += increaseSize;
                var span = bufferWriter.GetSpan(increaseSize).Slice(0, increaseSize);
                bufferWriter.Advance(increaseSize);

                var addBuffer = bufferWriter.GetBuffer();
                var addBufferLen = addBuffer.Length;
                if (bufferSize != addBufferLen)
                    Console.WriteLine($"bufferSize: {bufferSize} != addBufferLen: {addBufferLen}");

                bufferWriter.Flush(decreaseSize).Dispose();

                bufferSize -= decreaseSize;

                var bufferLen = bufferWriter.GetBuffer().Length;

                if (bufferSize != bufferLen)
                    Console.WriteLine($"bufferSize: {bufferSize} != bufferLen: {bufferLen}");

                if (bufferLen > 1024 * 32)
                    decreaseSize = 529;
                else if (bufferLen < 1024 * 16)
                    decreaseSize = 67;
            }
        });
    }
}
