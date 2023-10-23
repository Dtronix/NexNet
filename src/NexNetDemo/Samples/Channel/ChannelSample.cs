﻿using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet;
using NexNet.Pipes;
using NexNet.Quic;

namespace NexNetDemo.Samples.Channel;

public class ChannelSample : SampleBase
{
    public ChannelSample(TransportMode transportMode = TransportMode.Uds)
        : base(false, transportMode)
    {

    }

    public async Task UnmanagedChannelSample()
    {
        var(server, client) = await Setup();

        await using var channel = client.CreateUnmanagedChannel<int>();

        await client.Proxy.IntegerChannel(channel);

        var reader = await channel.GetReaderAsync();

        while (!reader.IsComplete)
        {
            foreach (var integer in await reader.ReadAsync())
            {
                Console.WriteLine(integer);
            } 
        }
    }

    public async Task ChannelStructSample()
    {
        var (server, client) = await Setup();

        await using var pipe = client.CreateUnmanagedChannel<ChannelSampleStruct>();

        await client.Proxy.StructChannel(pipe);

        var reader = await pipe.GetReaderAsync();

        await reader.ReadBatchUntilComplete(batch =>
        {
            foreach (var channelSampleStruct in batch)
            {
                Console.WriteLine(channelSampleStruct);
            }
        });

        // ---OR---
        /*
        var list = new List<ChannelSampleStruct>();
        while (await reader.ReadAsync(list, null))
        {
            foreach (var channelSampleStruct in list)
            {
                Console.WriteLine(channelSampleStruct);
            }
            list.Clear();
        }
        */
    }

    private class ChannelSampleStruct2
    {
        public int Id { get; set; }
        public long Counts { get; set; }

        public override string ToString()
        {
            return $"ChannelSampleStruct2 Id: {Id}, Counts: {Counts}";
        }
    
    }
    public async Task ChannelStructConvertSample()
    {
        var (server, client) = await Setup();

        await using var pipe = client.CreateUnmanagedChannel<ChannelSampleStruct>();


        await client.Proxy.StructChannel(pipe);

        var reader = await pipe.GetReaderAsync();

        static ChannelSampleStruct2 Convert(in ChannelSampleStruct s) => new ChannelSampleStruct2()
        {
            Id = s.Id,
            Counts = s.Counts
        };

        await reader.ReadBatchUntilComplete(Convert, batch =>
        {
            foreach (var channelSampleStruct in batch)
            {
                Console.WriteLine(channelSampleStruct);
            }
        });

        // ---OR---

        /*
        var list = new List<ChannelSampleStruct2>();
        while (await reader.ReadAsync(list, Convert))
        {
            foreach (var channelSampleStruct in list)
            {
                Console.WriteLine(channelSampleStruct);
            }
            list.Clear();
        }
        */
    }

    public async Task ClassSample()
    {
        var (server, client) = await Setup();

        await using var pipe = client.CreateChannel<ComplexMessage>();

        await client.Proxy.ClassChannel(pipe);
        
        var reader = await pipe.GetReaderAsync();

        await reader.ReadBatchUntilComplete(batch =>
        {
            foreach (var channelSampleStruct in batch)
            {
                Console.WriteLine(channelSampleStruct);
            }
        });

        // ---OR---

        /*
        var list = new List<ComplexMessage>();
        while (await reader.ReadAsync(list, null))
        {
            foreach (var channelSampleStruct in list)
            {
                Console.WriteLine(channelSampleStruct);
            }
            list.Clear();
        }
        */
    }

    public async Task ClassChannelBatchSample()
    {
        var (server, client) = await Setup();

        await using var pipe = client.CreateChannel<ComplexMessage>();

        await client.Proxy.ClassChannelBatch(pipe);

        var result = await pipe.ReadUntilComplete(1000);

        foreach (var complexMessage in result)
        {
            Console.WriteLine(complexMessage);
        }
    }

    private async Task<(NexusServer<ChannelSampleServerNexus, ChannelSampleServerNexus.ClientProxy> server, NexusClient<ChannelSampleClientNexus, ChannelSampleClientNexus.ServerProxy> client)> Setup()
    {
        var server = ChannelSampleServerNexus.CreateServer(ServerConfig, () => new ChannelSampleServerNexus());
        await server.StartAsync();

        var client = ChannelSampleClientNexus.CreateClient(ClientConfig, new ChannelSampleClientNexus());
        await client.ConnectAsync();

        return (server, client);
    }

}
