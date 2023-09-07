﻿using NexNetDemo.Samples;
using NexNetDemo.Samples.Channel;
using NexNetDemo.Samples.DuplexPipeStreaming;
using NexNetDemo.Samples.InvocationSample;
using NexNetDemo.Samples.Messenger;

namespace NexNetDemo;

internal class Program
{
    static async Task Main(string[] args)
    {
        //await new DuplexPipeSimpleSample().UploadSample();
        //await new DuplexPipeStreamingSample(SampleBase.TransportMode.Quic).UploadStreamingSample();
        //await new DuplexPipeStreamingSample(SampleBase.TransportMode.Quic).DuplexStreamingSample();
        //await new ChannelSample(SampleBase.TransportMode.Uds).UnmanagedChannelSample();
        //await new ChannelSample(SampleBase.TransportMode.Uds).ChannelStructSample();
        await new ChannelSample(SampleBase.TransportMode.Uds).ClassSample();
        //await new DuplexPipeStreamingSample().DuplexStreamingSample();
        //await new InvocationSample().UpdateInfo();

        /*
        var messengerSample = new MessengerSample("127.0.0.1");

        if (args.Length == 1 && args[0] == "server")
        {
            await messengerSample.RunServer();
        }
        else if (args.Length == 1 && args[0] == "client")
        {
            await messengerSample.RunClient();
        }
        else
        {
            _ = Task.Factory.StartNew(() => messengerSample.RunServer(), TaskCreationOptions.LongRunning);
            await messengerSample.RunClient();
        }
        */


        Console.ReadLine();
    }
    
}
