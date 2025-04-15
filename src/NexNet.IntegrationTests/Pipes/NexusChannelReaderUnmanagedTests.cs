using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusChannelReaderUnmanagedTests
{
    [TestCase((sbyte)-54)]
    [TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase(65122584)]
    [TestCase((uint)616322584)]
    [TestCase(92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase('n')]
    [TestCase((float)9873571.1922)]
    [TestCase(9851512573571.198422)]
    public async Task ReadsData<T>(T inputData)
        where T : unmanaged
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);

        var reader = new NexusChannelReaderUnmanaged<T>(pipeReader);
        await pipeReader.BufferData(Utilities.GetBytes(inputData));

        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(inputData));
    }

    public async Task CancelsReadDelayed<T>()
        where T : unmanaged
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);
        var cts = new CancellationTokenSource(100);
        var result = await reader.ReadAsync(cts.Token).Timeout(1);

        Assert.That(cts.IsCancellationRequested, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task CancelsReadImmediate()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);
        var cts = new CancellationTokenSource(100);
        cts.Cancel();
        var result = await reader.ReadAsync(cts.Token).Timeout(1);

        Assert.That(cts.IsCancellationRequested, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Completes()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);

        // ReSharper disable once MethodHasAsyncOverload
        await pipeReader.CompleteAsync();
        var result = await reader.ReadAsync().Timeout(1);

        Assert.That(reader.IsComplete, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }


    [TestCase((sbyte)-54)]
    [TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase(65122584)]
    [TestCase((uint)616322584)]
    [TestCase(92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase('n')]
    [TestCase((float)9873571.1922)]
    [TestCase(9851512573571.198422)]
    public async Task WaitsForFullData<T>(T inputData)
        where T : unmanaged
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReaderUnmanaged<T>(pipeReader);

        _ = Task.Run(async () =>
        {
            await tcs.Task.Timeout(1);
            var data = Utilities.GetBytes(inputData);
            for (var i = 0; i < data.Length; i++)
            {
                await pipeReader.BufferData(data.Slice(i, 1)).Timeout(1);
            }
        });

        tcs.SetResult();
        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(inputData));
    }

    [TestCase((sbyte)-54)]
    [TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase(65122584)]
    [TestCase((uint)616322584)]
    [TestCase(92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase('n')]
    [TestCase((float)9873571.1922)]
    [TestCase(9851512573571.198422)]
    public async Task ReadsMultiple<T>(T inputData)
        where T : unmanaged
    {
        const int iterations = 1000;
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReaderUnmanaged<T>(pipeReader);
        var data = Utilities.GetBytes(inputData);
        var count = 0;

        for (var i = 0; i < iterations; i++)
        {
            await pipeReader.BufferData(data).Timeout(1);
        }
        var results = await reader.ReadAsync(CancellationToken.None).Timeout(1);
        foreach (var result in results)
        {
            count++;
            Assert.That(result, Is.EqualTo(inputData));
        }

        Assert.That(count, Is.EqualTo(iterations));
    }

    [TestCase((sbyte)-54)]
    [TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase(65122584)]
    [TestCase((uint)616322584)]
    [TestCase(92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase('n')]
    [TestCase((float)9873571.1922)]
    [TestCase(9851512573571.198422)]
    public async Task ReadsMultipleParallel<T>(T inputData)
        where T : unmanaged
    {
        const int iterations = 1000;
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReaderUnmanaged<T>(pipeReader);
        var data = Utilities.GetBytes(inputData);
        var count = 0;

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await pipeReader.BufferData(data).Timeout(1);
            }
        });

        await Task.Run(async () =>
        {
            while (true)
            {
                var results = await reader.ReadAsync(CancellationToken.None).Timeout(1);
                foreach (var result in results)
                {
                    count++;
                    Assert.That(result, Is.EqualTo(inputData));
                }

                if (count == iterations)
                    break;
            }
        }).Timeout(1);

        Assert.That(count, Is.EqualTo(iterations));
    }

    // This does not apply to single byte types.
    //[TestCase((sbyte)-54)]
    //[TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase(65122584)]
    [TestCase((uint)616322584)]
    [TestCase(92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase('n')]
    [TestCase((float)9873571.1922)]
    [TestCase(9851512573571.198422)]
    public async Task ReadsWithPartialWrites<T>(T inputData)
        where T : unmanaged
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReaderUnmanaged<T>(pipeReader);
        var data = Utilities.GetBytes(inputData);

        await pipeReader.BufferData(data).Timeout(1);

        // Provide the next data short one byte.
        await pipeReader.BufferData(data.Slice(0, data.Length - 1)).Timeout(1);
        var results = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(results.Count(), Is.EqualTo(1));
    }

    private class DummyPipeStateManager : IPipeStateManager
    {
        public ushort Id { get; } = 0;
        public ValueTask NotifyState()
        {
            return default;
        }

        public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
        {
            CurrentState |= updatedState;
            return true;
        }

        public NexusDuplexPipe.State CurrentState { get; private set; } = NexusDuplexPipe.State.Ready;
    }
}
