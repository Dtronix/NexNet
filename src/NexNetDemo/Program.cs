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
using NexNetDemo.Samples.DuplexPipe;
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
    public static double AverageRate;
    protected override async ValueTask OnConnected(bool isReconnected)
    {


        var pipe = this.Context.CreatePipe(async pipe =>
        {
            Memory<byte> randomData = Program.Data;
            var length = 1024 * 32;

            /*Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var sentBytes = 0L;
                var loopNumber = 0;

                while (true)
                {
                    var data = await pipe.Input.ReadAsync();

                    if (data.IsCanceled || data.IsCompleted)
                        return;

                    pipe.Input.AdvanceTo(data.Buffer.End);

                    //Console.Write($"{sentBytes:D} Read from Pipe");
                    //Console.SetCursorPosition(0, 0);

                    sentBytes += data.Buffer.Length;

                    if (loopNumber++ == 800)
                    {
                        var value = ((sentBytes / 1024d / 1024d) / (sw.ElapsedMilliseconds / 1000d));
                        AverageRate = Program.ApproxRollingAverage(AverageRate, value);
                        sw.Restart();
                        sentBytes = 0;
                        loopNumber = 0;
                    }
                }
            });*/

            var loopNumber = 0;
            while (true)
            {
                var result = await pipe.Output.WriteAsync(Program.Data);

                if (result.IsCanceled || result.IsCompleted)
                    return;

                if (loopNumber == 1000000)
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

    public static double AverageRate;

    public async ValueTask ServerTaskWithParam(int id, INexusDuplexPipe pipe, string myValue)
    {
        long sentBytes = 0;
        int loopNumber = 0;
        AverageRate = 0;
        var sw = new Stopwatch();
        /*
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var result = await pipe.Output.WriteAsync(Program.Data);

                if(result.IsCanceled || result.IsCompleted)
                    return;
            }
        });*/

        while (true)
        {
            sw.Start();
            var data = await pipe.Input.ReadAsync();

            if (data.IsCanceled || data.IsCompleted)
                return;

            if (data.Buffer.Length == 0)
            {
                continue;
            }

            pipe.Input.AdvanceTo(data.Buffer.End);

            _readData += data.Buffer.Length;

            //Console.Write($"{sentBytes:D} Read from Pipe");
            //Console.SetCursorPosition(0, 0);

            sentBytes += data.Buffer.Length;
            if (loopNumber++ == 800)
            {
                var value = ((sentBytes / 1024d / 1024d) / (sw.ElapsedMilliseconds / 1000d));
                AverageRate = Program.ApproxRollingAverage(AverageRate, value);
                sw.Restart();
                sentBytes = 0;
                loopNumber = 0;

                Console.WriteLine($"Server Rec:{ServerNexus.AverageRate:F} MBps; Client Rec:{ClientNexus.AverageRate:F} MBps;");
                //Console.SetCursorPosition(0, 0);
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
        if (logLevel < INexusLogger.LogLevel.Information)
            return;

        Console.WriteLine($"{_prefix} {logLevel}: {message} {exception}");
    }
}

internal class Program
{
    public static byte[] Data;

    public static double ApproxRollingAverage(double avg, double newSample)
    {

        avg -= avg / 100;
        avg += newSample / 100;

        return avg;
    }

    static Program()
    {
        Data = new byte[1 << 15];
        var number = 0;
        var direction = 1;
        for (int i = 0; i < Data.Length; i++)
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
            Data[i] = (byte)delta;
        }

    }
    static void RunTest(string testName, Action action)
    {
        Console.WriteLine($"Running {testName}");
        var sw = Stopwatch.StartNew();
        action();
        Console.WriteLine($"Completed {testName} in {sw.ElapsedMilliseconds}");
    }

    static async Task Main(string[] args)
    {
        //await new DuplexPipeSample().UploadDownloadSample();
        //Console.ReadLine();
        //return;
        //RoughBenchmark();
        //Console.ReadLine();
        //return;
        var path = "test.sock";
        if (File.Exists(path))
            File.Delete(path);

        var serverConfig = new UdsServerConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            Logger = new Logger("SV"),
        };
        var clientConfig = new UdsClientConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            Logger = new Logger("CL"),
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
