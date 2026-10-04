using NexNet.Fuzz;
using SharpFuzz;

// Usage:
//   NexNet.Fuzz <harness>                     run under libFuzzer (SharpFuzz instrumentation required)
//   NexNet.Fuzz                               same, with the harness taken from NEXNET_FUZZ_HARNESS (for
//                                             libfuzzer-dotnet, which passes only the assembly path)
//   NexNet.Fuzz --smoke [corpusDir]           run every harness over the corpus without a fuzzer
//   NexNet.Fuzz --repro <harness> <hex> [n]   run one input n times (reproduce a failure)
//   NexNet.Fuzz --export-corpus <dir>         write every seed input as a file, for a libFuzzer corpus
// Harnesses: reader, messages, channel, formatters, builtins, session.

var harnessName = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("NEXNET_FUZZ_HARNESS");
if (harnessName == null)
{
    Console.WriteLine("Usage: NexNet.Fuzz <harness>|--smoke [corpusDir]|--repro <harness> <hex> [count]|--export-corpus <dir>");
    Console.WriteLine("Harnesses: " + string.Join(", ", Harnesses.All.Keys));
    return 1;
}

if (harnessName == "--repro" && args.Length > 2)
{
    var input = Convert.FromHexString(args[2]);
    var count = args.Length > 3 ? int.Parse(args[3]) : 1;
    for (var i = 0; i < count; i++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Harnesses.All[args[1]](input);
        if (count == 1 || sw.ElapsedMilliseconds > 1000)
            Console.WriteLine($"Run {i}: passed in {sw.ElapsedMilliseconds} ms.");
    }

    Console.WriteLine($"{count} run(s) passed.");
    return 0;
}

if (harnessName == "--smoke")
{
    var corpus = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "Corpus");
    var count = SmokeRunner.Run(corpus, Console.Out);
    Console.WriteLine($"Smoke run complete: {count} inputs per harness.");
    return 0;
}

if (harnessName == "--export-corpus" && args.Length > 1)
{
    var seeds = SmokeRunner.LoadCorpus(Path.Combine(AppContext.BaseDirectory, "Corpus"))
        .Concat(SmokeRunner.GeneratedSeeds())
        .Concat(SessionHarness.Seeds())
        .ToList();
    Directory.CreateDirectory(args[1]);
    for (var i = 0; i < seeds.Count; i++)
        File.WriteAllBytes(Path.Combine(args[1], $"seed-{i:D4}"), seeds[i]);
    Console.WriteLine($"Wrote {seeds.Count} seed inputs to {args[1]}.");
    return 0;
}

if (!Harnesses.All.TryGetValue(harnessName, out var harness))
{
    Console.Error.WriteLine($"Unknown harness '{harnessName}'.");
    return 1;
}

Fuzzer.LibFuzzer.Run(span => harness(span.ToArray()));
return 0;
