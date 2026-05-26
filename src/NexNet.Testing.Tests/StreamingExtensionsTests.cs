using System;
using System.Threading.Tasks;
using NexNet.Testing.Streaming;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class StreamingExtensionsTests
{
    private static Task<NexusTestHost<DemoServerNexus, DemoServerNexus.ClientProxy, DemoClientNexus, DemoClientNexus.ServerProxy>> CreateHost()
        => NexusTestHost.CreateAsync<
            DemoServerNexus, DemoServerNexus.ClientProxy,
            DemoClientNexus, DemoClientNexus.ServerProxy>();

    [SetUp]
    public void ClearServerNexusStatics()
    {
        DemoServerNexus.LastUploadedBytes = null;
        DemoServerNexus.LastCollectedItems = null;
    }

    [Test]
    public async Task PipeUpload_DeliversFullPayload()
    {
        await using var host = await CreateHost();
        var client = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        var payload = new byte[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 3);

        await using var pipe = client.CreatePipe();
        var serverCall = client.Server.Upload(pipe).AsTask();
        await pipe.PipeUploadAsync(payload);
        await serverCall.WaitAsync(TimeSpan.FromSeconds(2));
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(DemoServerNexus.LastUploadedBytes, Is.EqualTo(payload));
    }

    [Test]
    public async Task PipeDownload_ReceivesFullPayload()
    {
        await using var host = await CreateHost();
        var client = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        await using var pipe = client.CreatePipe();
        var serverCall = client.Server.Download(pipe, byteCount: 128).AsTask();
        var bytes = await pipe.PipeDownloadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await serverCall.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(bytes.Length, Is.EqualTo(128));
        for (int i = 0; i < bytes.Length; i++)
            Assert.That(bytes[i], Is.EqualTo((byte)i));
    }

    [Test]
    public async Task ChannelPublish_DeliversTypedItems()
    {
        await using var host = await CreateHost();
        var client = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        var items = new[] { "alpha", "beta", "gamma", "delta" };

        await using var pipe = client.CreatePipe();
        var serverCall = client.Server.CollectStrings(pipe).AsTask();
        await pipe.ChannelPublishAsync(items);
        await serverCall.WaitAsync(TimeSpan.FromSeconds(2));
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(DemoServerNexus.LastCollectedItems, Is.EqualTo(items));
    }

    [Test]
    public async Task ChannelCollect_ReceivesTypedItems()
    {
        await using var host = await CreateHost();
        var client = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        var items = new[] { "uno", "dos", "tres" };

        await using var pipe = client.CreatePipe();
        var serverCall = client.Server.PublishStrings(pipe, items).AsTask();
        var collected = await pipe.ChannelCollectAsync<string>().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await serverCall.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(collected, Is.EqualTo(items));
    }
}
