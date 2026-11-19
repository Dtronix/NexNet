using System.Diagnostics;
using System.Text.Json.Nodes;
using NexNet.Interop.InProc;
using NexNet.Interop.Remote;
using NexNet.Interop.Scripting;
using NexNet.Pipes;

namespace NexNet.Interop.Tests;

/// <summary>The C# reference implementation of the <c>channel.pump</c> named handler (control protocol §6.9).</summary>
public class ChannelPumpTests
{
    private static async Task<RecordingChannel<T>> Pump<T>(JsonObject? options, CancellationToken cancellationToken = default)
    {
        var channel = new RecordingChannel<T>();
        await InProcNamedHandlers.RunAsync(new NamedStep("channel.pump", options), new ScriptCall(null!, [], cancellationToken, channel));
        return channel;
    }

    [Test]
    public async Task Int64_WritesTheIndexes_ThenCompletes()
    {
        var channel = await Pump<long>(new JsonObject { ["count"] = 5 });
        await Assert.That(channel.Items).IsEquivalentTo(new long[] { 0, 1, 2, 3, 4 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(channel.Completed).IsTrue();
        await Assert.That(channel.ReaderRequested).IsFalse();
    }

    [Test]
    public async Task IntegerTypes_WrapToTheirWidth()
    {
        var sbytes = await Pump<sbyte>(new JsonObject { ["count"] = 130 });
        await Assert.That(sbytes.Items[127]).IsEqualTo((sbyte)127);
        await Assert.That(sbytes.Items[128]).IsEqualTo((sbyte)-128);
        await Assert.That(sbytes.Items[129]).IsEqualTo((sbyte)-127);

        var bytes = await Pump<byte>(new JsonObject { ["count"] = 258 });
        await Assert.That(bytes.Items[255]).IsEqualTo((byte)255);
        await Assert.That(bytes.Items[256]).IsEqualTo((byte)0);

        await Assert.That((await Pump<short>(new JsonObject { ["count"] = 3 })).Items[2]).IsEqualTo((short)2);
        await Assert.That((await Pump<ushort>(new JsonObject { ["count"] = 3 })).Items[2]).IsEqualTo((ushort)2);
        await Assert.That((await Pump<int>(new JsonObject { ["count"] = 3 })).Items[2]).IsEqualTo(2);
        await Assert.That((await Pump<uint>(new JsonObject { ["count"] = 3 })).Items[2]).IsEqualTo(2u);
        await Assert.That((await Pump<ulong>(new JsonObject { ["count"] = 3 })).Items[2]).IsEqualTo(2ul);
    }

    [Test]
    public async Task Strings_AreSizeLettersStartingAtTheIndex()
    {
        var channel = await Pump<string>(new JsonObject { ["count"] = 27, ["size"] = 3 });
        await Assert.That(channel.Items[0]).IsEqualTo("abc");
        await Assert.That(channel.Items[1]).IsEqualTo("bcd");
        await Assert.That(channel.Items[24]).IsEqualTo("yza");
        await Assert.That(channel.Items[25]).IsEqualTo("zab");
        await Assert.That(channel.Items[26]).IsEqualTo("abc");

        var empty = await Pump<string>(new JsonObject { ["count"] = 2 });
        await Assert.That(empty.Items).IsEquivalentTo(new[] { "", "" });
    }

    [Test]
    public async Task ByteArrays_AreThePipePatternOfTheIndex()
    {
        var channel = await Pump<byte[]>(new JsonObject { ["count"] = 3, ["size"] = 100 });
        await Assert.That(channel.Items.Count).IsEqualTo(3);
        for (var i = 0; i < 3; i++)
            await Assert.That(channel.Items[i]).IsEquivalentTo(PipePattern.Create(100, i), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ZeroCount_OnlyCompletes()
    {
        var channel = await Pump<long>(new JsonObject { ["count"] = 0, ["delayMs"] = 10_000 });
        await Assert.That(channel.Items.Count).IsEqualTo(0);
        await Assert.That(channel.Completed).IsTrue();
    }

    [Test]
    public async Task DelayMs_WaitsAfterEachItemButTheLast()
    {
        var watch = Stopwatch.StartNew();
        var channel = await Pump<int>(new JsonObject { ["count"] = 3, ["delayMs"] = 150 });
        watch.Stop();
        await Assert.That(channel.Items.Count).IsEqualTo(3);

        // Two delays, not three.
        await Assert.That(watch.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(280));
        await Assert.That(channel.WriteTimes[2] - channel.WriteTimes[1]).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140));

        // No delay after the last item: a single item with a minute's delay completes at once.
        var single = await Pump<int>(new JsonObject { ["count"] = 1, ["delayMs"] = 60_000 }).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(single.Completed).IsTrue();
    }

    [Test]
    public async Task Cancellation_StopsTheDelay()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await Pump<int>(new JsonObject { ["count"] = 3, ["delayMs"] = 60_000 }, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public async Task OtherItemTypes_AreNotSupported_AndNothingIsWritten()
    {
        var channel = new RecordingChannel<Guid>();
        var error = await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await InProcNamedHandlers.RunAsync(
            new NamedStep("channel.pump", new JsonObject { ["count"] = 1 }), new ScriptCall(null!, [], default, channel)));
        await Assert.That(error!.Message).Contains("System.Guid");
        await Assert.That(channel.WriterRequested).IsFalse();
        await Assert.That(ControlErrors.FromException(error)["kind"]!.GetValue<string>()).IsEqualTo(ControlErrors.NotSupported);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await Pump<char>(new JsonObject { ["count"] = 1 }));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await Pump<DayOfWeek>(new JsonObject { ["count"] = 1 }));
    }

    [Test]
    public async Task BadOptions_AreBadRequests()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await Pump<long>(null));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await Pump<long>(new JsonObject { ["size"] = 1 }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await Pump<long>(new JsonObject { ["count"] = -1 }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await Pump<long>(new JsonObject { ["count"] = 1, ["delayMs"] = -5 }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await Pump<long>(new JsonObject { ["count"] = "many" }));
    }

    [Test]
    public async Task APipe_IsNotAChannel()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await InProcNamedHandlers.RunAsync(
            new NamedStep("channel.pump", new JsonObject { ["count"] = 1 }), new ScriptCall(null!, [], default, null)));
    }

    /// <summary>A channel that records what is written to it.</summary>
    private sealed class RecordingChannel<T> : INexusDuplexChannel<T>, INexusChannelWriter<T>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public List<T> Items { get; } = new();

        public List<TimeSpan> WriteTimes { get; } = new();

        public bool Completed { get; private set; }

        public bool WriterRequested { get; private set; }

        public bool ReaderRequested { get; private set; }

        public INexusDuplexPipe? BasePipe => null;

        public bool IsComplete { get; set; }

        public ValueTask<INexusChannelWriter<T>> GetWriterAsync()
        {
            WriterRequested = true;
            return new(this);
        }

        public ValueTask<INexusChannelReader<T>> GetReaderAsync()
        {
            ReaderRequested = true;
            throw new InvalidOperationException("channel.pump does not read.");
        }

        public ValueTask<bool> WriteAsync(T item, CancellationToken cancellationToken = default)
        {
            Items.Add(item);
            WriteTimes.Add(_clock.Elapsed);
            return new(true);
        }

        public ValueTask<bool> WriteAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("channel.pump writes one item at a time.");

        public ValueTask CompleteAsync()
        {
            Completed = true;
            return default;
        }

        public ValueTask DisposeAsync() => default;
    }
}
