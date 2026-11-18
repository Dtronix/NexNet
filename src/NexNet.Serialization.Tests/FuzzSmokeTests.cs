using NexNet.Fuzz;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Runs every fuzz harness over the golden-vector corpus and deterministic mutations (no libFuzzer).
/// A harness may only throw <see cref="NexusSerializationException"/>; anything else fails the test.
/// </summary>
public class FuzzSmokeTests
{
    [Test]
    public async Task AllHarnessesHandleCorpusAndMutations()
    {
        var corpus = Path.Combine(AppContext.BaseDirectory, "Corpus");
        await Assert.That(Directory.Exists(corpus)).IsTrue().Because("Corpus directory missing from test output.");

        var count = SmokeRunner.Run(corpus);
        await Assert.That(count).IsGreaterThan(100);
    }

    /// <summary>
    /// Last step of the flow control work sequence (docs/internals/flow-control.md, section 8) that has landed.
    /// Reproductions fixed by a later step are skipped; each step raises this.
    /// </summary>
    private const int CompletedFlowControlStep = 6;

    public static IEnumerable<Func<TestDataRow<byte[]>>> FlowReproductions()
    {
        foreach (var (name, input, fixedInStep) in FlowHarness.Reproductions())
        {
            var skip = fixedInStep > CompletedFlowControlStep ? $"Fails until flow control step {fixedInStep}." : null;
            yield return () => new TestDataRow<byte[]>(input, DisplayName: $"FlowReproduction({name})", Skip: skip);
        }
    }

    public static IEnumerable<Func<TestDataRow<(byte[] Input, NexNet.Messages.DisconnectReason? Expected)>>> FlowHostileCases() =>
        FlowHarness.HostileCases().Select(c => (Func<TestDataRow<(byte[], NexNet.Messages.DisconnectReason?)>>)(() =>
            new TestDataRow<(byte[], NexNet.Messages.DisconnectReason?)>((c.Input, c.Expected), DisplayName: $"FlowHostile({c.Name})")));

    [Test]
    [MethodDataSource(nameof(FlowHostileCases))]
    public async Task FlowHostile(byte[] input, NexNet.Messages.DisconnectReason? expected)
    {
        await Assert.That(FlowHarness.Execute(input)).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(FlowReproductions))]
    public void FlowReproduction(byte[] input)
    {
        FlowHarness.Run(input);
    }

    [Test]
    public void GeneratedSeedsRoundTripThroughHarnesses()
    {
        foreach (var seed in SmokeRunner.GeneratedSeeds())
        {
            foreach (var harness in Harnesses.All.Values)
                harness(seed);
        }
    }
}
