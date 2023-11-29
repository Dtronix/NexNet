using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
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
            return ManualConfig.CreateEmpty()
                // Jobs
                .AddJob(Job.Default
                    .WithRuntime(CoreRuntime.Core80)
                    .WithPlatform(Platform.X64)
                    .WithMinWarmupCount(1)
                    .WithMaxWarmupCount(3)
                    .WithMinIterationCount(3)
                    .WithMaxIterationCount(5))
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
