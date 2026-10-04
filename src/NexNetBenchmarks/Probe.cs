using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace NexNetBenchmarks;

// TEMPORARY profiling harness (not part of the final change).
public static class Probe
{
    public static async Task Run(string[] args)
    {
        var which = args.Length > 1 ? args[1] : "Persons";
        var iters = args.Length > 2 ? int.Parse(args[2]) : 1000;
        var modes = args.Length > 3 ? (args[3] == "nf" ? new[] { false } : new[] { true }) : new[] { false, true, false, true, false, true };
        foreach (var frag in modes)
        {
            var b = new ChannelThroughputBenchmarks { Fragmented = frag };
            await b.GlobalSetup();
            Func<Task> op = which switch { "Ints" => b.Ints, "IntArrays256" => b.IntArrays256, _ => b.Persons };
            for (int i = 0; i < 1500; i++) await op();
            var alloc0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++) await op();
            sw.Stop();
            var alloc = (GC.GetTotalAllocatedBytes(true) - alloc0) / iters;
            Console.WriteLine($"{which} frag={frag}: {sw.Elapsed.TotalMicroseconds / iters:F1} us/op, alloc {alloc} B/op");
            await b.GlobalCleanup();
        }
    }
}
