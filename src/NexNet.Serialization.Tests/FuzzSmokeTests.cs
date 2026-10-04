#if !NEXNET_MEMORYPACK
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
#endif
