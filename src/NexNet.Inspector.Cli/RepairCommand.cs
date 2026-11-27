using NexNet.Inspector.Core.Format;

namespace NexNet.Inspector.Cli;

/// <summary><c>nexnet-tap repair</c>: rebuilds a recording's index footer in place (OQ3: the only place it exists).</summary>
internal static class RepairCommand
{
    public static ExitCode Run(TapCli cli, CliArguments args)
    {
        var path = args.File!;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, args.Force ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
            if (!args.Force)
            {
                using (var reader = NntlReader.Open(stream, NntlOpenMode.PreferFooter, leaveOpen: true))
                {
                    if (reader.IndexSource == NntlIndexSource.Footer)
                    {
                        cli.Output.WriteLine($"{path}: the index footer is intact ({Text.Count(reader.Index.BlockCount)} blocks); nothing to repair. Pass --force to rebuild it anyway.");
                        return ExitCode.Ok;
                    }
                }

                // Damaged: reopen for writing only now, so an intact file is never opened writable.
                stream.Dispose();
                using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                Report(cli.Output, path, NntlFile.RebuildIndex(writable));
                return ExitCode.Ok;
            }

            Report(cli.Output, path, NntlFile.RebuildIndex(stream));
            return ExitCode.Ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NntlFormatException)
        {
            cli.Error.WriteLine($"nexnet-tap: cannot repair {path}: {e.Message}");
            return ExitCode.Failed;
        }
    }

    private static void Report(TextWriter o, string path, NntlRebuildResult result)
    {
        o.WriteLine($"{path}: rebuilt the index footer over {Text.Count(result.BlockCount)} blocks.");
        o.WriteLine(result.TruncatedBytes == 0
            ? "Cut       nothing"
            : $"Cut       {Text.Count(result.TruncatedBytes)} bytes from the end (torn tail and old footer)");
        if (result.Damage.Count == 0)
        {
            o.WriteLine("Damage    none");
            return;
        }

        o.WriteLine($"Damage    {result.Damage.Count} region(s):");
        foreach (var d in result.Damage)
        {
            var note = d.Kind == NntlDamageKind.TornTail ? "cut off" : "left in place, not indexed";
            o.WriteLine($"          {d.Kind} at offset {Text.Count(d.Offset)}, {Text.Count(d.Length)} bytes ({note})");
        }
    }
}
