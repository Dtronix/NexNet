using BenchmarkDotNet.Running;

namespace NexNetBenchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--probe")
            {
                Probe.Run(args).GetAwaiter().GetResult();
                return;
            }

#if !NEXNET_MEMORYPACK
            if (args.Length > 0 && args[0] == "--micro")
            {
                ProbeMicro.Run();
                return;
            }
#endif

            if (args.Length > 0 && args[0] == "--sizes")
            {
                SerializerBenchmarks.PrintWireSizes();
                return;
            }

            // If arguments are available use BenchmarkSwitcher to run benchmarks
            if (args.Length > 0)
            {
                var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                    .Run(args, BenchmarkConfig.Get());
                return;
            }
            // Else, use BenchmarkRunner
            var summary = BenchmarkRunner.Run<InvocationBenchmarks>(BenchmarkConfig.Get());
        }
    }
}
