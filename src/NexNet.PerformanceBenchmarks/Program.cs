using System.CommandLine;
using System.CommandLine.Invocation;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Reporting;
using NexNet.PerformanceBenchmarks.Scenarios.Latency;
using NexNet.PerformanceBenchmarks.Scenarios.Scalability;
using NexNet.PerformanceBenchmarks.Scenarios.Stress;
using NexNet.PerformanceBenchmarks.Scenarios.Collections;
using NexNet.PerformanceBenchmarks.Scenarios.Overhead;
using NexNet.PerformanceBenchmarks.Scenarios.Throughput;

namespace NexNet.PerformanceBenchmarks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("NexNet Performance Benchmark Suite")
        {
            Description = "Comprehensive benchmark suite for NexNet networking library. " +
                         "Measures latency, throughput, scalability, and stress characteristics across all transport types."
        };

        // Define options
        var fullOption = new Option<bool>(
            ["--full", "-f"],
            "Run the complete benchmark suite");

        var quickOption = new Option<bool>(
            ["--quick", "-q"],
            "Run with fewer iterations for quick validation");

        var categoryOption = new Option<string?>(
            ["--category", "-c"],
            "Run specific category (Latency, Throughput, Scalability, Stress, Collections, Overhead)");

        var scenarioOption = new Option<string?>(
            ["--scenario", "-s"],
            "Run specific scenario by name");

        var transportOption = new Option<string?>(
            ["--transport", "-t"],
            "Comma-separated transports (Uds, Tcp, Tls, WebSocket, HttpSocket, Quic)");

        var outputOption = new Option<string?>(
            ["--output", "-o"],
            () => "Results",
            "Output directory for results");

        var payloadOption = new Option<string?>(
            ["--payload", "-p"],
            "Comma-separated payload sizes (Tiny, Small, Medium, Large, XLarge)");

        var listOption = new Option<bool>(
            ["--list", "-l"],
            "List available scenarios without running them");

        rootCommand.AddOption(fullOption);
        rootCommand.AddOption(quickOption);
        rootCommand.AddOption(categoryOption);
        rootCommand.AddOption(scenarioOption);
        rootCommand.AddOption(transportOption);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(payloadOption);
        rootCommand.AddOption(listOption);

        rootCommand.SetHandler(async (InvocationContext context) =>
        {
            var options = new CliOptions
            {
                Full = context.ParseResult.GetValueForOption(fullOption),
                Quick = context.ParseResult.GetValueForOption(quickOption),
                Category = context.ParseResult.GetValueForOption(categoryOption),
                Scenario = context.ParseResult.GetValueForOption(scenarioOption),
                Transport = context.ParseResult.GetValueForOption(transportOption),
                Output = context.ParseResult.GetValueForOption(outputOption),
                Payload = context.ParseResult.GetValueForOption(payloadOption),
                List = context.ParseResult.GetValueForOption(listOption)
            };

            var exitCode = await RunBenchmarksAsync(options, context.GetCancellationToken());
            context.ExitCode = exitCode;
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<int> RunBenchmarksAsync(CliOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine("NexNet Performance Benchmark Suite");
        Console.WriteLine("===================================");
        Console.WriteLine();

        // Determine settings based on mode
        BenchmarkSettings settings;
        if (options.Quick)
        {
            Console.WriteLine("Mode: Quick validation (fewer iterations)");
            settings = BenchmarkSettings.CreateQuick();
        }
        else if (options.Full)
        {
            Console.WriteLine("Mode: Full benchmark suite");
            settings = BenchmarkSettings.CreateFull();
        }
        else
        {
            Console.WriteLine("Mode: Standard");
            settings = new BenchmarkSettings();
        }

        if (!string.IsNullOrEmpty(options.Output))
        {
            settings.OutputDirectory = options.Output;
        }

        Console.WriteLine($"Output directory: {settings.OutputDirectory}");
        Console.WriteLine();

        // Print environment info
        PrintEnvironmentInfo();

        // Create the scenario runner
        var runner = new ScenarioRunner(settings, options, Console.WriteLine);

        // Register scenarios (placeholder - scenarios will be added in later phases)
        RegisterScenarios(runner);

        // Handle list option
        if (options.List)
        {
            runner.ListScenarios();
            return 0;
        }

        // Check if there are any scenarios to run
        var scenarios = runner.GetFilteredScenarios().ToList();
        if (scenarios.Count == 0)
        {
            Console.WriteLine("No scenarios registered or all scenarios filtered out.");
            Console.WriteLine();
            Console.WriteLine("Note: Scenarios will be implemented in Phase 2+.");
            Console.WriteLine("Use --list to see available scenarios.");
            Console.WriteLine();

            // For Phase 1, show what transports are available
            Console.WriteLine("Available Transports:");
            foreach (TransportType transport in Enum.GetValues<TransportType>())
            {
                var available = TransportFactory.IsTransportAvailable(transport);
                var status = available ? "Available" : TransportFactory.GetUnavailableReason(transport);
                Console.WriteLine($"  {transport}: {status}");
            }

            return 0;
        }

        try
        {
            // Run the benchmarks
            var run = await runner.RunAsync(cancellationToken);

            // Add git metadata to the run
            var commitHash = GetCommitHash();
            run = run with
            {
                CommitHash = commitHash,
                CommitMessage = GetCommitMessage(),
                Branch = GetBranch()
            };

            // Ensure output directory exists
            var outputDir = Path.Combine(settings.OutputDirectory, commitHash ?? "local");
            Directory.CreateDirectory(outputDir);

            // Generate reports
            Console.WriteLine();
            Console.WriteLine("Generating reports...");

            var jsonPath = Path.Combine(outputDir, "results.json");
            var markdownPath = Path.Combine(outputDir, "summary.md");

            await JsonReporter.WriteReportAsync(run, jsonPath, cancellationToken);
            Console.WriteLine($"  JSON report: {jsonPath}");

            await MarkdownReporter.WriteReportAsync(run, markdownPath, cancellationToken);
            Console.WriteLine($"  Markdown summary: {markdownPath}");

            Console.WriteLine();
            Console.WriteLine($"Results saved to: {outputDir}");

            return run.Results.All(r => r.Success) ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Benchmark run cancelled.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            // Cleanup
            TransportFactory.Cleanup();
        }
    }

    private static void RegisterScenarios(ScenarioRunner runner)
    {
        // === Latency Scenarios (Phase 3) ===
        runner.RegisterScenario(new InvocationLatencyScenario());
        runner.RegisterScenario(new PipeLatencyScenario());
        runner.RegisterScenario(new ChannelLatencyScenario());

        // === Throughput Scenarios (Phase 4) ===
        runner.RegisterScenario(new PipeThroughputScenario());
        runner.RegisterScenario(new ChannelThroughputScenario());
        runner.RegisterScenario(new InvocationThroughputScenario());

        // === Scalability Scenarios (Phase 5) ===
        runner.RegisterScenario(new MultiClientScenario());
        runner.RegisterScenario(new BroadcastScenario());
        runner.RegisterScenario(new GroupMessagingScenario());

        // === Stress Scenarios (Phase 6) ===
        runner.RegisterScenario(new ConnectionChurnScenario());
        runner.RegisterScenario(new MemoryPressureScenario());
        runner.RegisterScenario(new BackpressureScenario());

        // === Collection Scenarios (Phase 7) ===
        runner.RegisterScenario(new LargeListSyncScenario());
        runner.RegisterScenario(new HighFrequencyUpdateScenario());

        // === Overhead Scenarios (Phase 8) ===
        runner.RegisterScenario(new CancellationOverheadScenario());
        runner.RegisterScenario(new ReconnectionScenario());
    }

    private static void PrintEnvironmentInfo()
    {
        Console.WriteLine("Environment:");
        Console.WriteLine($"  .NET Runtime: {Environment.Version}");
        Console.WriteLine($"  OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"  Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"  Processor Count: {Environment.ProcessorCount}");

        var commitHash = GetCommitHash();
        if (!string.IsNullOrEmpty(commitHash))
        {
            Console.WriteLine($"  Git Commit: {commitHash}");
        }

        Console.WriteLine();
    }

    private static string? GetCommitHash()
    {
        return RunGitCommand("rev-parse --short HEAD");
    }

    private static string? GetCommitMessage()
    {
        return RunGitCommand("log -1 --format=%s");
    }

    private static string? GetBranch()
    {
        return RunGitCommand("rev-parse --abbrev-ref HEAD");
    }

    private static string? RunGitCommand(string arguments)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(1000);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
