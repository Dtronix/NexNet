using System.Diagnostics;
using NexNet;

namespace NexNetDemo.Samples.DuplexPipeStreaming;

interface IDuplexPipeStreamingClientNexus
{

}

interface IDuplexPipeStreamingServerNexus
{
    ValueTask StreamToAndFrom(INexusDuplexPipe pipe);
    ValueTask StreamTo(INexusDuplexPipe pipe);
}

[Nexus<IDuplexPipeStreamingClientNexus, IDuplexPipeStreamingServerNexus>(NexusType = NexusType.Client)]
partial class DuplexPipeStreamingClientNexus
{

}

[Nexus<IDuplexPipeStreamingServerNexus, IDuplexPipeStreamingClientNexus>(NexusType = NexusType.Server)]
partial class DuplexPipeStreamingServerNexus
{
    public static double AverageRate { get; private set; }
    public async ValueTask StreamToAndFrom(INexusDuplexPipe pipe)
    {
        long sentBytes = 0;
        int loopNumber = 0;
        AverageRate = 0;
        var sw = new Stopwatch();
        ReadOnlyMemory<byte> data = new byte[1024 * 16];

        _ = Task.Run(async () =>
        {
            while (true)
            {
                var result = await pipe.Output.WriteAsync(data);

                if (result.IsCanceled || result.IsCompleted)
                    return;
            }
        });

        while (true)
        {
            sw.Start();
            var readData = await pipe.Input.ReadAsync();

            if (readData.IsCanceled || readData.IsCompleted)
                return;

            if (readData.Buffer.Length == 0)
            {
                continue;
            }

            pipe.Input.AdvanceTo(readData.Buffer.End);

            //Console.Write($"{sentBytes:D} Read from Pipe");
            //Console.SetCursorPosition(0, 0);

            sentBytes += readData.Buffer.Length;
            if (loopNumber++ == 800)
            {
                var value = ((sentBytes / 1024d / 1024d) / (sw.ElapsedMilliseconds / 1000d));
                AverageRate = value;// Program.ApproxRollingAverage(AverageRate, value);
                sw.Restart();
                sentBytes = 0;
                loopNumber = 0;
                //Console.SetCursorPosition(0, 0);
            }
        }
    }

    public async ValueTask StreamTo(INexusDuplexPipe pipe)
    {
        int loopNumber = 0;
        AverageRate = 0;
        var sw = new Stopwatch();
        var receivedBytes = 0L;
        ReadOnlyMemory<byte> data = new byte[1024 * 16];
        while (true)
        {
            sw.Start();
            var readData = await pipe.Input.ReadAsync();

            if (readData.IsCanceled || readData.IsCompleted)
                return;

            var length = readData.Buffer.Length;
            if (length == 0)
            {
                continue;
            }
            receivedBytes += length;
            pipe.Input.AdvanceTo(readData.Buffer.End);

            if (loopNumber++ == 3000)
            {
                var value = ((receivedBytes / 1024d / 1024d) / (sw.ElapsedMilliseconds / 1000d));
                sw.Restart();
                receivedBytes = 0;
                loopNumber = 0;

                Console.WriteLine($"Server Rec:{DuplexPipeStreamingServerNexus.AverageRate:F} MBps; Client Rec:{value:F} MBps;");
            }
        }
    }
}
