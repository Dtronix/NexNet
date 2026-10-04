namespace NexNet.Fuzz;

/// <summary>
/// Runs every harness over the seed corpus plus deterministic mutations, without a fuzzer.
/// Used by CI-less smoke tests; libFuzzer is used for real fuzzing.
/// </summary>
public static class SmokeRunner
{
    public static int Run(string corpusDirectory, TextWriter? log = null)
    {
        var inputs = LoadCorpus(corpusDirectory).Concat(GeneratedSeeds()).ToList();
        var mutated = new List<byte[]>();
        var random = new Random(1234);
        foreach (var input in inputs)
        {
            mutated.Add(input);

            // Truncations, single-byte flips and appended garbage.
            for (var cut = 0; cut < input.Length; cut += Math.Max(1, input.Length / 8))
                mutated.Add(input.AsSpan(0, cut).ToArray());

            for (var i = 0; i < 8 && input.Length > 0; i++)
            {
                var copy = (byte[])input.Clone();
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
                mutated.Add(copy);
            }

            var extended = new byte[input.Length + 4];
            input.CopyTo(extended, 0);
            random.NextBytes(extended.AsSpan(input.Length));
            mutated.Add(extended);
        }

        foreach (var (name, harness) in Harnesses.All)
        {
            foreach (var input in mutated)
                harness(input);

            log?.WriteLine($"  {name}: {mutated.Count} inputs OK");
        }

        return mutated.Count;
    }

    /// <summary>
    /// Valid serialized DTO graphs, so mutations exercise deep formatter paths.
    /// </summary>
    public static IEnumerable<byte[]> GeneratedSeeds()
    {
        var order = new FuzzOrder
        {
            Id = 42,
            Customer = "customer",
            Lines = [new FuzzLine { Quantity = 2, Price = 9.99m }, new FuzzLine { Quantity = 1, Price = 100m }],
            Totals = new Dictionary<string, double> { ["net"] = 119.98, ["tax"] = 9.6 },
            Shape = new FuzzSquare { Nested = new FuzzOrder { Id = 1, Shape = new FuzzCircle { Radius = 3 } } },
            Codes = [1, -2, long.MaxValue]
        };

        yield return NexNet.Serialization.NexusSerializer.Serialize(order);
        yield return NexNet.Serialization.NexusSerializer.Serialize<IFuzzShape>(new FuzzCircle { Radius = 1.5 });
        yield return NexNet.Serialization.NexusSerializer.Serialize(new FuzzEnvelope<FuzzOrder> { Value = order, Items = [order, order] });
        yield return NexNet.Serialization.NexusSerializer.Serialize(new[] { "a", "b", "c" });
        yield return NexNet.Serialization.NexusSerializer.Serialize(new[] { 1.0, 2.0, 3.0 });
    }

    public static IEnumerable<byte[]> LoadCorpus(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(directory, "*.hex").OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;
                yield return Convert.FromHexString(trimmed);
            }
        }
    }
}
