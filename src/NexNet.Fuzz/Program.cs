using NexNet.Fuzz;
using SharpFuzz;

// Usage:
//   NexNet.Fuzz <harness>            run under libFuzzer (SharpFuzz instrumentation required)
//   NexNet.Fuzz --smoke [corpusDir]  run every harness over the corpus without a fuzzer
//   NexNet.Fuzz --repro <harness> <hex>  run one input (reproduce a failure)
// Harnesses: reader, messages, channel, formatters, builtins, session.

if (args.Length == 0)
{
    Console.WriteLine("Usage: NexNet.Fuzz <harness>|--smoke [corpusDir]");
    Console.WriteLine("Harnesses: " + string.Join(", ", Harnesses.All.Keys));
    return 1;
}

if (args[0] == "--repro" && args.Length > 2)
{
        // Optional repeat count: NexNet.Fuzz --repro <harness> <hex> [count]
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

if (args[0] == "--smoke")
{
    var corpus = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "Corpus");
    var count = SmokeRunner.Run(corpus, Console.Out);
    Console.WriteLine($"Smoke run complete: {count} inputs per harness.");
    return 0;
}

if (!Harnesses.All.TryGetValue(args[0], out var harness))
{
    Console.Error.WriteLine($"Unknown harness '{args[0]}'.");
    return 1;
}

Fuzzer.LibFuzzer.Run(span => harness(span.ToArray()));
return 0;
