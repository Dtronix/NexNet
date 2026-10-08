using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace NexNetBenchmarks
{
    public class BenchmarkConfig
    {
        /// <summary>
        /// Get a custom configuration
        /// </summary>
        /// <returns></returns>
        public static IConfig Get()
        {
            // NEXNET_BENCH_SHORT=1 selects a short run (fewer iterations) for quick comparisons.
            // NEXNET_BENCH_INPROC=1 selects the default (full) job.
            // Both run in-process, so the already built benchmark assembly is measured as-is without rebuilding the
            // project for an out-of-process job.
            var inProcess = BenchmarkDotNet.Toolchains.InProcess.Emit.InProcessEmitToolchain.Instance;
            var job = System.Environment.GetEnvironmentVariable("NEXNET_BENCH_SHORT") == "1"
                ? Job.ShortRun.WithToolchain(inProcess)
                // NEXNET_BENCH_MEDIUM=1: fixed 10 warmups and 15 iterations, for A/B comparisons during optimization.
                : System.Environment.GetEnvironmentVariable("NEXNET_BENCH_MEDIUM") == "1"
                    ? Job.Default.WithWarmupCount(10).WithIterationCount(15).WithToolchain(inProcess)
                : System.Environment.GetEnvironmentVariable("NEXNET_BENCH_INPROC") == "1"
                    // Full jobs on the slower channel benchmarks can exceed the default 5-minute in-process timeout.
                    ? Job.Default.WithToolchain(new BenchmarkDotNet.Toolchains.InProcess.Emit.InProcessEmitToolchain(System.TimeSpan.FromMinutes(30), logOutput: true))
                    : Job.Default;

            return ManualConfig.CreateEmpty()
                // Jobs (host runtime: the benchmarks target the same framework as the library)
                .AddJob(job.WithPlatform(Platform.X64))
                    //.WithMinWarmupCount(1)
                    //.WithMaxWarmupCount(3)
                    //.WithMinIterationCount(3)
                    //.WithMaxIterationCount(5))
                .AddDiagnoser(MemoryDiagnoser.Default)
                .AddColumnProvider(DefaultColumnProviders.Instance)
                .AddColumn(StatisticColumn.OperationsPerSecond)
                .AddLogger(ConsoleLogger.Default)
                //.AddExporter(CsvExporter.Default)
                //.AddExporter(HtmlExporter.Default)
                .AddAnalyser(GetAnalysers().ToArray());
        }

        /// <summary>
        /// Get analyser for the cutom configuration
        /// </summary>
        /// <returns></returns>
        private static IEnumerable<IAnalyser> GetAnalysers()
        {
            yield return EnvironmentAnalyser.Default;
            yield return OutliersAnalyser.Default;
            yield return MinIterationTimeAnalyser.Default;
            //yield return MultimodalDistributionAnalyzer.Default;
            yield return RuntimeErrorAnalyser.Default;
            yield return ZeroMeasurementAnalyser.Default;
            //yield return BaselineCustomAnalyzer.Default;
        }
    }
}
