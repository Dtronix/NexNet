using System.Diagnostics;

namespace NexNetDemo.Samples.DuplexPipeStreaming;

public class DuplexPipeStreamingSample : SampleBase
{
    public DuplexPipeStreamingSample(TransportMode transport = TransportMode.Uds) 
        : base(false, transport)
    {
    }

    public async Task DuplexStreamingSample()
    {
        var client = DuplexPipeStreamingClientNexus.CreateClient(ClientConfig, new DuplexPipeStreamingClientNexus());
        var server = DuplexPipeStreamingServerNexus.CreateServer(ServerConfig, () => new DuplexPipeStreamingServerNexus());
        await server.StartAsync();
        await client.ConnectAsync();

        // Create the client pipe.
        var pipe = client.CreatePipe();

        // Invoke the method on the server and pass the pipe.
        await client.Proxy.StreamToAndFrom(pipe);

        // Wait for the pipe to be ready for wiring & reading.
        await pipe.ReadyTask;

        // Task to run parallel to the sending pipe.
        _ = Task.Run(async () =>
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

                sentBytes += data.Buffer.Length;

                if (loopNumber++ == 800)
                {
                    var value = ((sentBytes / 1024d / 1024d) / (sw.ElapsedMilliseconds / 1000d));
                    sw.Restart();
                    sentBytes = 0;
                    loopNumber = 0;

                    Console.WriteLine($"Server Rec:{DuplexPipeStreamingServerNexus.AverageRate:F} MBps; Client Rec:{value:F} MBps;");
                }
            }
        });

        ReadOnlyMemory<byte> randomData = new byte[1024 * 16];

        while (true)
        {
            var result = await pipe.Output.WriteAsync(randomData);

            if (result.IsCanceled || result.IsCompleted)
                return;
        }
    }
}
