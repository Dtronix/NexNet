using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using NexNet.Messages;
using NexNet.Pools;

namespace NexNetBenchmarks;

/// <summary>
/// Benchmarks for MessagePool performance.
/// Tests single-threaded and multi-threaded Rent/Return operations
/// with the hybrid thread-local + shared pool strategy.
/// </summary>
public class MessagePoolBenchmarks
{
    private MessagePool<InvocationCancellationMessage> _messagePool = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _messagePool = new MessagePool<InvocationCancellationMessage>();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _messagePool.Clear();
    }

    /// <summary>
    /// Single rent/return cycle - baseline performance.
    /// </summary>
    [Benchmark(Baseline = true)]
    public void SingleRentReturn()
    {
        var msg = _messagePool.Rent();
        _messagePool.Return(msg);
    }

    /// <summary>
    /// Batch of 100 rent/return cycles on single thread.
    /// Tests thread-local stack performance.
    /// </summary>
    [Benchmark]
    public void BatchRentReturn_100()
    {
        for (int i = 0; i < 100; i++)
        {
            var msg = _messagePool.Rent();
            _messagePool.Return(msg);
        }
    }

    /// <summary>
    /// Rent 10 items, then return all - simulates holding multiple messages.
    /// </summary>
    [Benchmark]
    public void RentMultipleThenReturnAll_10()
    {
        var items = new InvocationCancellationMessage[10];

        for (int i = 0; i < 10; i++)
        {
            items[i] = _messagePool.Rent();
        }

        for (int i = 0; i < 10; i++)
        {
            _messagePool.Return(items[i]);
        }
    }

    /// <summary>
    /// Parallel rent/return from 4 threads - tests thread-local isolation.
    /// </summary>
    [Benchmark]
    [Arguments(4)]
    [Arguments(8)]
    public void ParallelRentReturn(int threadCount)
    {
        const int opsPerThread = 1000;

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, _ =>
        {
            for (int i = 0; i < opsPerThread; i++)
            {
                var msg = _messagePool.Rent();
                _messagePool.Return(msg);
            }
        });
    }

    /// <summary>
    /// Simulates producer-consumer pattern with cross-thread returns.
    /// One thread rents, another returns - tests ConcurrentBag fallback.
    /// </summary>
    [Benchmark]
    public void CrossThreadRentReturn()
    {
        const int count = 100;
        var items = new InvocationCancellationMessage[count];
        var rentDone = new ManualResetEventSlim(false);
        var returnDone = new ManualResetEventSlim(false);

        // Rent on thread 1
        var rentTask = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
            {
                items[i] = _messagePool.Rent();
            }
            rentDone.Set();
        });

        // Return on thread 2
        var returnTask = Task.Run(() =>
        {
            rentDone.Wait();
            for (int i = 0; i < count; i++)
            {
                _messagePool.Return(items[i]);
            }
            returnDone.Set();
        });

        returnDone.Wait();
        Task.WaitAll(rentTask, returnTask);
    }

    /// <summary>
    /// High contention scenario - many threads competing for same pool.
    /// </summary>
    [Benchmark]
    public void HighContention_16Threads()
    {
        const int opsPerThread = 500;

        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (int i = 0; i < opsPerThread; i++)
            {
                var msg = _messagePool.Rent();
                // Simulate some work
                Thread.SpinWait(10);
                _messagePool.Return(msg);
            }
        });
    }
}
