namespace NexNet.Fuzz;

/// <summary>
/// Runs one harness on randomly mutated seeds for a fixed time, without a fuzzer. Stands in for libFuzzer where it is
/// not available; every input is reproducible from the printed hex.
/// </summary>
public static class SoakRunner
{
    public static int Run(string harnessName, TimeSpan duration, int seed, string corpusDirectory, TextWriter log)
    {
        var harness = Harnesses.All[harnessName];
        var seeds = harnessName switch
        {
            "flow" => FlowHarness.Seeds().Concat(FlowHarness.Reproductions().Select(r => r.Input))
                .Concat(FlowHarness.HostileCases().Select(c => c.Input)).ToList(),
            "session" => SessionHarness.Seeds().ToList(),
            _ => SmokeRunner.LoadCorpus(corpusDirectory).Concat(SmokeRunner.GeneratedSeeds()).ToList(),
        };

        var random = new Random(seed);
        var deadline = DateTime.UtcNow + duration;
        var runs = 0;
        while (DateTime.UtcNow < deadline)
        {
            var input = Mutate(seeds[random.Next(seeds.Count)], seeds, random);
            try
            {
                harness(input);
            }
            catch (Exception e)
            {
                log.WriteLine($"{harnessName}: FAILED after {runs} runs on input {Convert.ToHexString(input)}");
                log.WriteLine(e);
                return 1;
            }

            runs++;
        }

        log.WriteLine($"{harnessName}: {runs} inputs OK in {duration.TotalSeconds:F0} s (seed {seed}).");
        return 0;
    }

    /// <summary>
    /// One to eight random edits: byte changes, truncation, duplicated or spliced ranges.
    /// </summary>
    private static byte[] Mutate(byte[] input, List<byte[]> seeds, Random random)
    {
        var data = new List<byte>(input);
        var edits = random.Next(1, 9);
        for (var i = 0; i < edits; i++)
        {
            switch (random.Next(5))
            {
                case 0 when data.Count > 0:
                    data[random.Next(data.Count)] = (byte)random.Next(256);
                    break;
                case 1 when data.Count > 1:
                    data.RemoveRange(random.Next(data.Count), 1);
                    break;
                case 2 when data.Count > 0:
                {
                    var start = random.Next(data.Count);
                    var length = Math.Min(random.Next(1, 16), data.Count - start);
                    data.InsertRange(random.Next(data.Count + 1), data.GetRange(start, length));
                    break;
                }
                case 3:
                {
                    var other = seeds[random.Next(seeds.Count)];
                    if (other.Length == 0)
                        break;
                    var start = random.Next(other.Length);
                    var length = Math.Min(random.Next(1, 32), other.Length - start);
                    data.InsertRange(random.Next(data.Count + 1), other.AsSpan(start, length).ToArray());
                    break;
                }
                case 4 when data.Count > 1:
                {
                    var keep = random.Next(1, data.Count);
                    data.RemoveRange(keep, data.Count - keep);
                    break;
                }
            }
        }

        return data.ToArray();
    }
}
