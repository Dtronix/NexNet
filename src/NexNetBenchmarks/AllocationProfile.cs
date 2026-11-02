using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;

namespace NexNetBenchmarks;

/// <summary>
/// <c>--alloc-profile [transport]</c>: runs calls on a client/server connection in this process and reports the
/// allocated bytes per call (client and server together) and, from the runtime's sampled allocation events
/// (GCAllocationTick, one per ~100 KB allocated), the types that account for them.
/// </summary>
internal static class AllocationProfile
{
    private const int Calls = 3200;
    private const int Batches = 200;

    public static async Task Run(string[] args)
    {
        var transport = args.Length > 1 ? Enum.Parse<BenchTransport>(args[1], true) : BenchTransport.Tcp;
        await using var connection = await BenchConnection.Start(transport, $"alloc-{transport}");
        var proxy = connection.Client.Proxy;

        await Profile("FireAndForget (void calls + 1 round trip)", async () =>
        {
            for (var i = 0; i < Calls; i++)
                proxy.InvocationFireAndForget(i);
            await proxy.InvocationNoArgumentWithResult();
        });

        await Profile("AwaitedVoid (ValueTask, awaited)", async () =>
        {
            for (var i = 0; i < Calls; i++)
                await proxy.InvocationUnmanagedArgument(i);
        });

        await Profile("RoundTrip (ValueTask<int>)", async () =>
        {
            for (var i = 0; i < Calls; i++)
                await proxy.InvocationNoArgumentWithResult();
        });
    }

    private static async Task Profile(string name, Func<Task> batch)
    {
        // Warm up: pools, JIT, tiering.
        for (var i = 0; i < 20; i++)
            await batch();

        using var listener = new AllocationListener();
        var before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < Batches; i++)
            await batch();
        var allocated = GC.GetTotalAllocatedBytes(true) - before;

        var calls = (long)Calls * Batches;
        Console.WriteLine();
        Console.WriteLine($"== {name}: {allocated / (double)calls:F0} B per call ({allocated / 1024 / 1024} MiB total)");
        var sampled = listener.Bytes.Values.Sum();
        foreach (var (type, bytes) in listener.Bytes.OrderByDescending(p => p.Value).Take(15))
            Console.WriteLine($"  {bytes * 100.0 / sampled,5:F1}%  ~{bytes / (double)calls,6:F0} B/call  {type}");
    }

    private sealed class AllocationListener : EventListener
    {
        private const EventKeywords GcKeyword = (EventKeywords)0x1;

        public ConcurrentDictionary<string, long> Bytes { get; } = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, GcKeyword);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload == null)
                return;

            var names = e.PayloadNames!;
            var typeIndex = names.IndexOf("TypeName");
            var amountIndex = names.IndexOf("AllocationAmount64");
            if (amountIndex < 0)
                amountIndex = names.IndexOf("AllocationAmount");
            if (typeIndex < 0 || amountIndex < 0)
                return;

            var type = e.Payload[typeIndex] as string ?? "?";
            var amount = Convert.ToInt64(e.Payload[amountIndex]);
            Bytes.AddOrUpdate(type, amount, (_, total) => total + amount);
        }
    }
}
