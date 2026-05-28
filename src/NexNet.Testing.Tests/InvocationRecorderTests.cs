using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Testing.Recording;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class InvocationRecorderTests
{
    [Test]
    public void AppendIncreasesCount()
    {
        var recorder = new InvocationRecorder();
        Assert.That(recorder.Count, Is.Zero);

        recorder.Append(new InvocationRecord(1, ReadOnlyMemory<byte>.Empty, null));
        Assert.That(recorder.Count, Is.EqualTo(1));
    }

    [Test]
    public void SnapshotReturnsStableCopy()
    {
        var recorder = new InvocationRecorder();
        recorder.Append(new InvocationRecord(1, ReadOnlyMemory<byte>.Empty, null));
        var first = recorder.Snapshot();

        recorder.Append(new InvocationRecord(2, ReadOnlyMemory<byte>.Empty, null));
        Assert.That(first, Has.Count.EqualTo(1), "Earlier snapshot must not see later appends");
        Assert.That(recorder.Snapshot(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task WaitForChangeAsync_CompletesOnAppend()
    {
        var recorder = new InvocationRecorder();
        var task = recorder.WaitForChangeAsync(CancellationToken.None);
        Assert.That(task.IsCompleted, Is.False);

        recorder.Append(new InvocationRecord(1, ReadOnlyMemory<byte>.Empty, null));

        await task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(task.IsCompleted, Is.True);
    }

    [Test]
    public async Task ConcurrentAppends_AreAllRecorded()
    {
        var recorder = new InvocationRecorder();
        const int writers = 8;
        const int perWriter = 250;

        await Task.WhenAll(Enumerable_Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
                recorder.Append(new InvocationRecord((ushort)w, ReadOnlyMemory<byte>.Empty, null));
        })));

        Assert.That(recorder.Count, Is.EqualTo(writers * perWriter));
    }

    private static System.Collections.Generic.IEnumerable<int> Enumerable_Range(int start, int count)
    {
        for (int i = 0; i < count; i++) yield return start + i;
    }
}
