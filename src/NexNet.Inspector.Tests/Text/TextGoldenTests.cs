using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Sample;

namespace NexNet.Inspector.Tests.Text;

/// <summary>The renderer's output for every fixture at every level matches the checked-in goldens, and is deterministic.</summary>
public class TextGoldenTests
{
    public static IEnumerable<(FixtureKind, TextLevel)> Cases()
    {
        foreach (var kind in Sample.Fixtures.All)
        {
            foreach (var level in new[] { TextLevel.Summary, TextLevel.Messages, TextLevel.Frames, TextLevel.Bytes })
                yield return (kind, level);
        }
    }

    // Frames and bytes stop at a line budget, so the goldens stay small and also pin the budget line.
    public static TextOptions OptionsFor(TextLevel level) => new()
    {
        Level = level,
        MaxLines = level >= TextLevel.Frames ? 200 : 0,
    };

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Output_matches_the_golden(FixtureKind kind, TextLevel level)
    {
        var actual = TextGolden.Render(kind, OptionsFor(level));
        var path = TextGolden.PathOf(kind, level.ToString().ToLowerInvariant());
        if (TextGolden.Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        await Assert.That(File.Exists(path)).IsTrue().Because($"golden {path} is missing; run with {TextGolden.UpdateVariable}=1");
        var expected = TextGolden.Normalize(await File.ReadAllTextAsync(path));
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task Ndjson_output_matches_the_golden()
    {
        var actual = TextGolden.Render(FixtureKind.ChatClient, new TextOptions { Format = TextFormat.Ndjson });
        var path = TextGolden.PathOf(FixtureKind.ChatClient, "messages.ndjson");
        if (TextGolden.Updating)
            await File.WriteAllTextAsync(path, actual);

        var expected = TextGolden.Normalize(await File.ReadAllTextAsync(path));
        await Assert.That(actual).IsEqualTo(expected);
    }
}
