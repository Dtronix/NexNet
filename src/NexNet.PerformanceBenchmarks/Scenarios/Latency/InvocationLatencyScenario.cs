using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Latency;

/// <summary>
/// Measures round-trip latency for method invocations across different payload sizes.
/// This is the most basic latency test - call a method and wait for response.
/// Note: NexNet has a 65521 byte limit for method arguments, so only Tiny/Small/Medium payloads are supported.
/// </summary>
public sealed class InvocationLatencyScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "InvocationLatency";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Latency;

    /// <inheritdoc />
    public override string Description => "Round-trip method invocation latency (Echo)";

    /// <summary>
    /// Only Tiny/Small/Medium payloads are supported due to NexNet's 65521 byte limit for method arguments.
    /// For larger data transfers, use Pipe or Channel scenarios.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } =
        [PayloadSize.Tiny, PayloadSize.Small, PayloadSize.Medium];

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            // Create and connect client
            var clientNexus = new BenchmarkClientNexus();
            var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);
            await client.ConnectAsync(cancellationToken);

            try
            {
                // Create the warmup manager
                var warmup = new WarmupManager(context.Settings);

                // Perform warmup and measurement based on payload size
                List<double> measurements = context.PayloadSize switch
                {
                    PayloadSize.Tiny => await MeasureTinyAsync(client.Proxy, context, warmup),
                    PayloadSize.Small => await MeasureSmallAsync(client.Proxy, context, warmup),
                    PayloadSize.Medium => await MeasureMediumAsync(client.Proxy, context, warmup),
                    PayloadSize.Large => await MeasureLargeAsync(client.Proxy, context, warmup),
                    PayloadSize.XLarge => await MeasureXLargeAsync(client.Proxy, context, warmup),
                    _ => await MeasureRawAsync(client.Proxy, context, warmup)
                };

                return ScenarioResult.FromLatencyMeasurements(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    measurements);
            }
            finally
            {
                await client.DisconnectAsync();
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<List<double>> MeasureTinyAsync(
        BenchmarkClientNexus.ServerProxy proxy,
        ScenarioContext context,
        WarmupManager warmup)
    {
        var payload = PayloadFactory.CreateTiny(context.Settings.RandomSeed);
        await warmup.WarmupAsync(async () => { await proxy.EchoTiny(payload); }, "TinyPayload Echo");
        return await warmup.MeasureAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            await proxy.EchoTiny(payload);
            sw.Stop();
            return sw.Elapsed.TotalMicroseconds;
        }, "TinyPayload Echo");
    }

    private static async Task<List<double>> MeasureSmallAsync(
        BenchmarkClientNexus.ServerProxy proxy,
        ScenarioContext context,
        WarmupManager warmup)
    {
        var payload = PayloadFactory.CreateSmall(context.Settings.RandomSeed);
        await warmup.WarmupAsync(async () => { await proxy.EchoSmall(payload); }, "SmallPayload Echo");
        return await warmup.MeasureAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            await proxy.EchoSmall(payload);
            sw.Stop();
            return sw.Elapsed.TotalMicroseconds;
        }, "SmallPayload Echo");
    }

    private static async Task<List<double>> MeasureMediumAsync(
        BenchmarkClientNexus.ServerProxy proxy,
        ScenarioContext context,
        WarmupManager warmup)
    {
        var payload = PayloadFactory.CreateMedium(context.Settings.RandomSeed);
        await warmup.WarmupAsync(async () => { await proxy.EchoMedium(payload); }, "MediumPayload Echo");
        return await warmup.MeasureAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            await proxy.EchoMedium(payload);
            sw.Stop();
            return sw.Elapsed.TotalMicroseconds;
        }, "MediumPayload Echo");
    }

    private static async Task<List<double>> MeasureLargeAsync(
        BenchmarkClientNexus.ServerProxy proxy,
        ScenarioContext context,
        WarmupManager warmup)
    {
        var payload = PayloadFactory.CreateLarge(context.Settings.RandomSeed);
        await warmup.WarmupAsync(async () => { await proxy.EchoLarge(payload); }, "LargePayload Echo");
        return await warmup.MeasureAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            await proxy.EchoLarge(payload);
            sw.Stop();
            return sw.Elapsed.TotalMicroseconds;
        }, "LargePayload Echo");
    }

    private static async Task<List<double>> MeasureXLargeAsync(
        BenchmarkClientNexus.ServerProxy proxy,
        ScenarioContext context,
        WarmupManager warmup)
    {
        var payload = PayloadFactory.CreateXLarge(context.Settings.RandomSeed);
        await warmup.WarmupAsync(async () => { await proxy.EchoXLarge(payload); }, "XLargePayload Echo");
        return await warmup.MeasureAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            await proxy.EchoXLarge(payload);
            sw.Stop();
            return sw.Elapsed.TotalMicroseconds;
        }, "XLargePayload Echo");
    }

    private static async Task<List<double>> MeasureRawAsync(
        BenchmarkClientNexus.ServerProxy proxy,
        ScenarioContext context,
        WarmupManager warmup)
    {
        var payload = context.GeneratePayload();
        await warmup.WarmupAsync(async () => { await proxy.Echo(payload); }, "RawPayload Echo");
        return await warmup.MeasureAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            await proxy.Echo(payload);
            sw.Stop();
            return sw.Elapsed.TotalMicroseconds;
        }, "RawPayload Echo");
    }
}
