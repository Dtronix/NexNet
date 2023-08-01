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
using NexNetDemo.Samples.DuplexPipeStreaming;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNetDemo;

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

    /// <summary>
    /// Rolling average function that takes a new sample and returns the average of the last 100 samples.
    /// </summary>
    /// <param name="avg"></param>
    /// <param name="newSample"></param>
    /// <returns></returns>
    public static double ApproxRollingAverage(double avg, double newSample)
    {
        avg -= avg / 100;
        avg += newSample / 100;
        return avg;
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
        //await new DuplexPipeSimpleSample().UploadDownloadSample();
        await new DuplexPipeStreamingSample().DuplexStreamingSample();


        Console.ReadLine();

    }
    
}
