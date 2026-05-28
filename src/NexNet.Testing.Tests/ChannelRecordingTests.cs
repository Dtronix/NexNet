using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Testing.Streaming;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class ChannelRecordingTests
{
    [Test]
    public void RecordItem_AppendsToItems()
    {
        var rec = new ChannelRecording<int>();
        rec.RecordItem(1);
        rec.RecordItem(2);
        Assert.That(rec.Items, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task WaitForCountAsync_CompletesAtThreshold()
    {
        var rec = new ChannelRecording<int>();
        var task = rec.WaitForCountAsync(3);
        Assert.That(task.IsCompleted, Is.False);

        rec.RecordItem(1);
        rec.RecordItem(2);
        Assert.That(task.IsCompleted, Is.False);

        rec.RecordItem(3);
        await task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Completion_NotifiesWaiter()
    {
        var rec = new ChannelRecording<string>();
        var waiter = rec.WaitForCompletionAsync();
        rec.RecordCompletion(null);
        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(rec.IsCompleted, Is.True);
    }

    [Test]
    public void Completion_CarriesException()
    {
        var rec = new ChannelRecording<string>();
        var error = new InvalidOperationException("boom");
        rec.RecordCompletion(error);
        Assert.That(rec.FaultedWith, Is.SameAs(error));
    }
}
