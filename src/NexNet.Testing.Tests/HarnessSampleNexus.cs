using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NexNet;
using NexNet.Invocation;
using NexNet.Pipes;

namespace NexNet.Testing.Tests;

[Nexus<IDemoServerNexus, IDemoClientNexus>(NexusType = NexusType.Server)]
internal partial class DemoServerNexus : ServerNexusBase<DemoServerNexus.ClientProxy>, IDemoServerNexus
{
    public int PingCount;

    public ValueTask<int> Ping(int value)
    {
        Interlocked.Increment(ref PingCount);
        return ValueTask.FromResult(value);
    }

    public ValueTask Notify(string message)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask JoinGroup(string groupName)
    {
        await Context.Groups.AddAsync(groupName);
    }

    public async ValueTask BroadcastToGroup(string groupName, string message)
    {
        await Context.Clients.Group(groupName).ReceiveBroadcast(message);
    }

    // Per-session demo nexuses are constructed by the harness factory, so these statics let
    // tests inspect server-side side effects without per-session access. Tests that use them
    // must clear them at the top of the test to avoid cross-test bleed (e.g., AssertionTests
    // does the same with PingCount via per-instance state).
    public static byte[]? LastUploadedBytes;
    public static List<string>? LastCollectedItems;

    public async ValueTask Upload(INexusDuplexPipe pipe)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await pipe.Input.ReadAsync();
            foreach (var segment in result.Buffer)
                ms.Write(segment.Span);
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        LastUploadedBytes = ms.ToArray();
    }

    public async ValueTask Download(INexusDuplexPipe pipe, int byteCount)
    {
        var buf = new byte[byteCount];
        for (int i = 0; i < byteCount; i++) buf[i] = (byte)i;
        await pipe.Output.WriteAsync(buf);
        await pipe.CompleteAsync();
    }

    public async ValueTask CollectStrings(INexusDuplexPipe pipe)
    {
        var reader = await pipe.GetChannelReader<string>();
        var items = new List<string>();
        await foreach (var item in reader)
            items.Add(item);
        LastCollectedItems = items;
    }

    public async ValueTask PublishStrings(INexusDuplexPipe pipe, string[] items)
    {
        var writer = await pipe.GetChannelWriter<string>();
        foreach (var item in items)
            await writer.WriteAsync(item);
        await writer.CompleteAsync();
    }
}

[Nexus<IDemoClientNexus, IDemoServerNexus>(NexusType = NexusType.Client)]
internal partial class DemoClientNexus : ClientNexusBase<DemoClientNexus.ServerProxy>, IDemoClientNexus
{
    public string? LastMessage;

    public ValueTask ReceiveBroadcast(string message)
    {
        LastMessage = message;
        return ValueTask.CompletedTask;
    }
}

internal partial interface IDemoServerNexus
{
    ValueTask<int> Ping(int value);
    ValueTask Notify(string message);
    ValueTask JoinGroup(string groupName);
    ValueTask BroadcastToGroup(string groupName, string message);
    ValueTask Upload(INexusDuplexPipe pipe);
    ValueTask Download(INexusDuplexPipe pipe, int byteCount);
    ValueTask CollectStrings(INexusDuplexPipe pipe);
    ValueTask PublishStrings(INexusDuplexPipe pipe, string[] items);
}

internal partial interface IDemoClientNexus
{
    ValueTask ReceiveBroadcast(string message);
}
