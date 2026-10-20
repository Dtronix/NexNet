using NexNet.Fuzz;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Runs every fuzz harness over the golden-vector corpus and deterministic mutations (no libFuzzer).
/// A harness may only throw <see cref="NexusSerializationException"/>; anything else fails the test.
/// </summary>
[TestFixture]
public class FuzzSmokeTests
{
    [Test]
    public void AllHarnessesHandleCorpusAndMutations()
    {
        var corpus = Path.Combine(AppContext.BaseDirectory, "Corpus");
        Assert.That(Directory.Exists(corpus), Is.True, "Corpus directory missing from test output.");

        var count = SmokeRunner.Run(corpus);
        Assert.That(count, Is.GreaterThan(100));
    }

    /// <summary>
    /// Last step of the flow control v2 work sequence (docs/internals/flow-control-v2.md, section 8) that has landed.
    /// Reproductions fixed by a later step are ignored; each step raises this.
    /// </summary>
    private const int CompletedFlowControlStep = 3;

    private static IEnumerable<TestCaseData> FlowReproductions()
    {
        foreach (var (name, input, fixedInStep) in FlowHarness.Reproductions())
        {
            var data = new TestCaseData(input).SetName($"FlowReproduction({name})");
            yield return fixedInStep > CompletedFlowControlStep
                ? data.Ignore($"Fails until flow control v2 step {fixedInStep}.")
                : data;
        }
    }

    [TestCaseSource(nameof(FlowReproductions))]
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
