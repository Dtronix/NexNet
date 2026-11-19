using NexNet.GoldenVectors;

// Usage: NexNet.GoldenVectors [--out <dir>] [--corpus <file> | --no-corpus]
//   --out     where the .jsonl files go (default: tests/golden)
//   --corpus  the fuzz seed corpus to rewrite (default: src/NexNet.Fuzz/Corpus/golden.hex of the repository that
//             contains the output directory, when there is one)
string outDirectory = Path.Combine("tests", "golden");
string? corpus = null;
var writeCorpus = true;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            outDirectory = args[++i];
            break;
        case "--corpus" when i + 1 < args.Length:
            corpus = args[++i];
            break;
        case "--no-corpus":
            writeCorpus = false;
            break;
        default:
            Console.Error.WriteLine("Usage: NexNet.GoldenVectors [--out <dir>] [--corpus <file> | --no-corpus]");
            return 2;
    }
}

if (writeCorpus && corpus == null)
{
    // The repository root is the first directory above the output that holds src/NexNet.slnx.
    for (var dir = new DirectoryInfo(Path.GetFullPath(outDirectory)); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "NexNet.slnx")))
        {
            corpus = Path.Combine(dir.FullName, GoldenGenerator.CorpusPath);
            break;
        }
    }
}

foreach (var path in GoldenGenerator.Write(outDirectory, writeCorpus ? corpus : null))
    Console.WriteLine("wrote " + path);
return 0;
