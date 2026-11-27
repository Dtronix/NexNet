namespace NexNet.Inspector.Cli;

/// <summary>Runs one parsed command line.</summary>
internal delegate Task<ExitCode> CliRunner(TapCli cli, CliArguments args, CancellationToken cancellationToken);

/// <summary>
/// One `nexnet-tap` command: its verb, the options it accepts, how many bare arguments it takes (a recording file,
/// an ID), its usage text and how it runs.
/// </summary>
/// <param name="Command">The verb.</param>
/// <param name="Name">What is typed.</param>
/// <param name="Allowed">Options it accepts.</param>
/// <param name="Positionals">Bare arguments it takes after the verb (the first is <see cref="CliArguments.File"/>).</param>
/// <param name="Usage">Its `help &lt;command&gt;` text; null for the overview.</param>
/// <param name="Run">How it runs.</param>
internal sealed record CliCommandSpec(CliCommand Command, string Name, CliOption Allowed, int Positionals, string? Usage, CliRunner Run);

/// <summary>
/// The command table: parsing, help and dispatch all read it, so a new command is one <see cref="CliCommand"/> value,
/// one row here, its usage text and its runner (plus any new <see cref="CliOption"/>s and their validation).
/// </summary>
internal static class CliCommands
{
    public const CliOption ConnectOptions =
        CliOption.Endpoint | CliOption.Token | CliOption.Insecure | CliOption.ServerName | CliOption.ConnectTimeout;

    /// <summary>Every command, in the order the overview lists them.</summary>
    public static readonly CliCommandSpec[] All =
    [
        new(CliCommand.List, "list", ConnectOptions, 0, TapCli.ListUsage,
            static (cli, args, ct) => new ListCommand(cli).RunAsync(args, ct)),
        new(CliCommand.Record, "record", ConnectOptions | CliOption.Session | CliOption.Duration | CliOption.Out | CliOption.Overflow
                                         | CliOption.IncludeSecrets | CliOption.NoSnapshot | CliOption.Wait | CliOption.Force, 0, TapCli.RecordUsage,
            static (cli, args, ct) => new RecordCommand(cli).RunAsync(args, ct)),
        new(CliCommand.Info, "info", CliOption.Json | CliOption.Messages, 1, TapCli.InfoUsage,
            static (cli, args, _) => Task.FromResult(InfoCommand.Run(cli, args))),
        new(CliCommand.Repair, "repair", CliOption.Force, 1, TapCli.RepairUsage,
            static (cli, args, _) => Task.FromResult(RepairCommand.Run(cli, args))),
        new(CliCommand.Tail, "tail", CliArguments.TailOptions, 1, TapCli.TailUsage,
            static (cli, args, ct) => new TailCommand(cli).RunAsync(args, ct)),
        new(CliCommand.Summary, "summary", QueryCommand.Live, 1, QueryCommand.SummaryUsage, QueryCommand.RunAsync),
        new(CliCommand.Show, "show", QueryCommand.Live | CliOption.Bytes, 2, QueryCommand.ShowUsage, QueryCommand.RunAsync),
        new(CliCommand.Trace, "trace", QueryCommand.Live, 2, QueryCommand.TraceUsage, QueryCommand.RunAsync),
        new(CliCommand.Around, "around", QueryCommand.Live | CliOption.Level, 3, QueryCommand.AroundUsage, QueryCommand.RunAsync),
        new(CliCommand.Errors, "errors", QueryCommand.Live | CliOption.Level, 1, QueryCommand.ErrorsUsage, QueryCommand.RunAsync),
        new(CliCommand.Slow, "slow", QueryCommand.Live | CliOption.Over, 1, QueryCommand.SlowUsage, QueryCommand.RunAsync),
        new(CliCommand.Pipes, "pipes", QueryCommand.Live, 1, QueryCommand.PipesUsage, QueryCommand.RunAsync),
        new(CliCommand.Collections, "collections", QueryCommand.Live | CliOption.Contents, 1, QueryCommand.CollectionsUsage, QueryCommand.RunAsync),
        new(CliCommand.Schema, "schema", QueryCommand.Live, 1, QueryCommand.SchemaUsage, QueryCommand.RunAsync),
        new(CliCommand.Diff, "diff", QueryCommand.Output | CliOption.Session | CliOption.Top, 2, QueryCommand.DiffUsage, QueryCommand.RunAsync),
        new(CliCommand.Export, "export", QueryCommand.Live | CliOption.From | CliOption.To | CliOption.Out | CliOption.Force, 1,
            QueryCommand.ExportUsage, QueryCommand.RunAsync),
        new(CliCommand.Format, "format", CliOption.None, 0, Core.Text.TextLegend.Text,
            static (cli, _, _) => Task.FromResult(QueryCommand.WriteLegend(cli))),
        new(CliCommand.Help, "help", CliOption.None, 0, null,
            static (cli, args, _) => Task.FromResult(cli.WriteUsage(args.HelpTopic))),
        new(CliCommand.Version, "version", CliOption.None, 0, null,
            static (cli, _, _) => Task.FromResult(cli.WriteVersion())),
        new(CliCommand.Findings, "findings", CliOption.Session | CliOption.Slow | CliOption.Json, 1, TapCli.FindingsUsage,
            static (cli, args, _) => Task.FromResult(FindingsCommand.Run(cli, args))),
    ];

    /// <summary>The row of a command; null for <see cref="CliCommand.None"/> or a value without a row.</summary>
    public static CliCommandSpec? Get(CliCommand command)
    {
        foreach (var spec in All)
        {
            if (spec.Command == command)
                return spec;
        }

        return null;
    }

    /// <summary>The command a verb names.</summary>
    public static bool TryParse(string text, out CliCommand command)
    {
        foreach (var spec in All)
        {
            if (spec.Name == text)
            {
                command = spec.Command;
                return true;
            }
        }

        command = CliCommand.None;
        return false;
    }
}
