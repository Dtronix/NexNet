using NexNet.Inspector.Cli;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>The command table every part of the CLI reads: parsing, help and dispatch.</summary>
public class CliCommandTableTests
{
    [Test]
    public async Task Every_command_has_one_row_that_parses_helps_and_dispatches()
    {
        var names = new HashSet<string>();
        foreach (var command in Enum.GetValues<CliCommand>())
        {
            if (command == CliCommand.None)
                continue;
            var spec = CliCommands.Get(command);
            await Assert.That(spec).IsNotNull().Because(command.ToString());
            await Assert.That(names.Add(spec!.Name)).IsTrue();
            await Assert.That(CliCommands.TryParse(spec.Name, out var parsed)).IsTrue();
            await Assert.That(parsed).IsEqualTo(command);
            await Assert.That(CliArguments.Name(command)).IsEqualTo(spec.Name);
            await Assert.That(CliArguments.Allowed(command)).IsEqualTo(spec.Allowed);

            // help <command> prints the row's usage (the overview for help and version).
            using var output = new StringWriter();
            var cli = new TapCli(output, TextWriter.Null);
            await Assert.That(await cli.RunAsync(["help", spec.Name], null, CancellationToken.None)).IsEqualTo((int)ExitCode.Ok);
            if (spec.Usage is not null)
                await Assert.That(output.ToString()).IsEqualTo(spec.Usage);
            else
                await Assert.That(output.ToString()).StartsWith("nexnet-tap: record and summarise");
        }

        await Assert.That(CliCommands.All.Length).IsEqualTo(names.Count);
        await Assert.That(CliCommands.TryParse("nope", out _)).IsFalse();
    }

    [Test]
    public async Task Bare_arguments_are_limited_to_what_the_command_takes()
    {
        await Assert.That(CliArguments.TryParse(["info", "a.nntl"], null, out var info, out _)).IsTrue();
        await Assert.That(info!.File).IsEqualTo("a.nntl");
        await Assert.That(info.Positionals.Count).IsEqualTo(1);

        await Assert.That(CliArguments.TryParse(["info", "a.nntl", "b.nntl"], null, out _, out var error)).IsFalse();
        await Assert.That(error).IsEqualTo("Unexpected argument 'b.nntl'.");
        await Assert.That(CliArguments.TryParse(["list", "x", "--endpoint", "tcp://h:1", "--token", "t"], null, out _, out error)).IsFalse();
        await Assert.That(error).IsEqualTo("Unexpected argument 'x'.");
    }
}
