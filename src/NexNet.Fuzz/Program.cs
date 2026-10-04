using NexNet.Fuzz;
using SharpFuzz;

// Usage:
//   NexNet.Fuzz <harness>            run under libFuzzer (SharpFuzz instrumentation required)
//   NexNet.Fuzz --smoke [corpusDir]  run every harness over the corpus without a fuzzer
// Harnesses: reader, messages, channel, formatters, builtins.

if (args.Length == 0)
{
    Console.WriteLine("Usage: NexNet.Fuzz <harness>|--smoke [corpusDir]");
    Console.WriteLine("Harnesses: " + string.Join(", ", Harnesses.All.Keys));
    return 1;
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
