using BenchmarkDotNet.Running;

namespace NexNetBenchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // If arguments are available use BenchmarkSwitcher to run benchmarks
            if (args.Length > 0)
            {
                var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                    .Run(args, BenchmarkConfig.Get());
                return;
            }
            // Else, use BenchmarkRunner
            var summary = BenchmarkRunner.Run<ChannelBenchmarks>(BenchmarkConfig.Get());
        }
    }
}
