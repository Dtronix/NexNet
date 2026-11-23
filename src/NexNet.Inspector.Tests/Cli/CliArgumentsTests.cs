using NexNet.Inspector.Cli;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>The hand-written, enum-based command-line parser of nexnet-tap.</summary>
public class CliArgumentsTests
{
    private static CliArguments Parse(params string[] args) => ParseWithEnvironment(null, args);

    private static CliArguments ParseWithEnvironment(string? environmentToken, params string[] args)
    {
        if (!CliArguments.TryParse(args, environmentToken, out var parsed, out var error))
            throw new InvalidOperationException(error);
        return parsed;
    }

    private static string Fail(params string[] args) =>
        CliArguments.TryParse(args, null, out _, out var error) ? throw new InvalidOperationException("parsed") : error;

    [Test]
    public async Task Record_with_every_option()
    {
        var a = Parse("record", "--endpoint", "tls://example.org:7001", "--token=secret", "--insecure", "--server-name", "inspector.local",
            "--connect-timeout", "3s", "-s", "first", "--duration", "5m", "-o", "x.nntl", "--overflow", "block",
            "--include-secrets", "--no-snapshot", "--wait", "1m", "--force");

        await Assert.That(a.Command).IsEqualTo(CliCommand.Record);
        await Assert.That(a.Endpoint).IsEqualTo(new EndpointAddress(EndpointScheme.Tls, "example.org", 7001));
        await Assert.That(a.Token).IsEqualTo("secret");
        await Assert.That(a.Insecure).IsTrue();
        await Assert.That(a.ServerName).IsEqualTo("inspector.local");
        await Assert.That(a.ConnectTimeout).IsEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(a.Selector).IsEqualTo(SessionSelectorKind.First);
        await Assert.That(a.Duration).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(a.Out).IsEqualTo("x.nntl");
        await Assert.That(a.Overflow).IsEqualTo(InspectorOverflow.Block);
        await Assert.That(a.IncludeSecrets).IsTrue();
        await Assert.That(a.Snapshot).IsFalse();
        await Assert.That(a.Wait).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(a.Force).IsTrue();
    }

    [Test]
    public async Task Record_defaults()
    {
        var a = Parse("record", "-e", "tcp://127.0.0.1:7000", "-t", "tok", "--session", "all", "--out", "a.nntl");

        await Assert.That(a.Selector).IsEqualTo(SessionSelectorKind.All);
        await Assert.That(a.Duration).IsNull();
        await Assert.That(a.Overflow).IsEqualTo(InspectorOverflow.Drop);
        await Assert.That(a.Snapshot).IsTrue();
        await Assert.That(a.IncludeSecrets).IsFalse();
        await Assert.That(a.Force).IsFalse();
        await Assert.That(a.ConnectTimeout).IsEqualTo(CliArguments.DefaultConnectTimeout);
        await Assert.That(a.Wait).IsEqualTo(CliArguments.DefaultWait);
    }

    [Test]
    [Arguments("12345", 12345L)]
    [Arguments("-42", -42L)]
    [Arguments("9223372036854775807", long.MaxValue)]
    public async Task Session_ids_parse(string text, long id)
    {
        var a = Parse("record", "-e", "tcp://h:1", "-t", "t", "-s", text, "-o", "x");
        await Assert.That(a.Selector).IsEqualTo(SessionSelectorKind.Id);
        await Assert.That(a.SessionId).IsEqualTo(id);
    }

    [Test]
    public async Task The_token_falls_back_to_the_environment_variable()
    {
        await Assert.That(ParseWithEnvironment("from-env", "list", "-e", "uds:/tmp/x.sock").Token).IsEqualTo("from-env");
        await Assert.That(ParseWithEnvironment("from-env", "list", "-e", "uds:/tmp/x.sock", "--token", "explicit").Token).IsEqualTo("explicit");
        await Assert.That(Fail("list", "-e", "uds:/tmp/x.sock")).Contains(CliArguments.TokenVariable);
    }

    [Test]
    [Arguments("tcp://127.0.0.1:7000", (byte)EndpointScheme.Tcp, "127.0.0.1", 7000)]
    [Arguments("tcp://localhost:1/", (byte)EndpointScheme.Tcp, "localhost", 1)]
    [Arguments("tls://[::1]:65535", (byte)EndpointScheme.Tls, "::1", 65535)]
    [Arguments("uds:/run/nexnet/tap.sock", (byte)EndpointScheme.Uds, "/run/nexnet/tap.sock", 0)]
    [Arguments("uds:C:\\temp\\tap.sock", (byte)EndpointScheme.Uds, "C:\\temp\\tap.sock", 0)]
    [Arguments("uds:///tmp/tap.sock", (byte)EndpointScheme.Uds, "/tmp/tap.sock", 0)]
    public async Task Endpoints_parse(string text, byte scheme, string host, int port)
    {
        await Assert.That(EndpointAddress.TryParse(text, out var address, out _)).IsTrue();
        await Assert.That(address).IsEqualTo(new EndpointAddress((EndpointScheme)scheme, host, port));
    }

    [Test]
    [Arguments("http://h:1")]
    [Arguments("tcp:h:1")]
    [Arguments("tcp://h")]
    [Arguments("tcp://h:0")]
    [Arguments("tcp://h:70000")]
    [Arguments("tcp://:7000")]
    [Arguments("tls://[::1]")]
    [Arguments("uds:")]
    [Arguments("h:1")]
    public async Task Bad_endpoints_are_rejected(string text)
    {
        await Assert.That(EndpointAddress.TryParse(text, out _, out var error)).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    [Arguments("30s", 30_000)]
    [Arguments("5m", 300_000)]
    [Arguments("1h", 3_600_000)]
    [Arguments("1h30m", 5_400_000)]
    [Arguments("250ms", 250)]
    [Arguments("1.5s", 1_500)]
    [Arguments("2m10s", 130_000)]
    [Arguments("45", 45_000)]
    public async Task Durations_parse(string text, int milliseconds)
    {
        await Assert.That(CliArguments.TryParseDuration(text, out var duration)).IsTrue();
        await Assert.That(duration).IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));
    }

    [Test]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("0s")]
    [Arguments("-5s")]
    [Arguments("s")]
    [Arguments("5 m")]
    [Arguments("5d")]
    [Arguments("5M")]
    [Arguments("m5")]
    public async Task Bad_durations_are_rejected(string text)
    {
        await Assert.That(CliArguments.TryParseDuration(text, out _)).IsFalse();
    }

    [Test]
    [Arguments(new[] { "frobnicate" }, "Unknown command")]
    [Arguments(new[] { "list", "--bogus" }, "Unknown option")]
    [Arguments(new[] { "list", "-t", "t" }, "--endpoint")]
    [Arguments(new[] { "list", "-e", "tcp://h:1", "-t", "t", "--out", "x" }, "does not apply")]
    [Arguments(new[] { "list", "-e", "tcp://h:1", "-t", "t", "-e", "tcp://h:2" }, "more than once")]
    [Arguments(new[] { "list", "-e", "tcp://h:1", "-t", "t", "--insecure" }, "tls://")]
    [Arguments(new[] { "list", "-e", "tcp://h:1", "-t", "t", "extra" }, "Unexpected argument")]
    [Arguments(new[] { "list", "-e" }, "needs a value")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "t", "-o", "x" }, "--session")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "t", "-s", "first" }, "--out")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "t", "-s", "latest", "-o", "x" }, "--session must be")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "t", "-s", "0", "-o", "x" }, "--session must be")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "t", "-s", "all", "-o", "x", "--wait", "5s" }, "--wait")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "t", "-s", "all", "-o", "x", "--duration", "5x" }, "not a duration")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "t", "-s", "all", "-o", "x", "--overflow", "wait" }, "drop")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "t", "-s", "all", "-o", "x", "--force=yes" }, "takes no value")]
    [Arguments(new[] { "record", "-e", "tcp://h:1", "-t", "", "-s", "all", "-o", "x" }, "empty")]
    [Arguments(new[] { "info" }, "recording file")]
    [Arguments(new[] { "info", "a.nntl", "b.nntl" }, "Unexpected argument")]
    [Arguments(new[] { "info", "a.nntl", "--force" }, "does not apply")]
    [Arguments(new[] { "repair", "a.nntl", "--json" }, "does not apply")]
    public async Task Invalid_command_lines_explain_themselves(string[] args, string expected)
    {
        await Assert.That(Fail(args)).Contains(expected);
    }

    [Test]
    public async Task Info_and_repair_take_a_file()
    {
        var info = Parse("info", "--json", "x.nntl", "--messages");
        await Assert.That(info.Command).IsEqualTo(CliCommand.Info);
        await Assert.That(info.File).IsEqualTo("x.nntl");
        await Assert.That(info.Json).IsTrue();
        await Assert.That(info.Messages).IsTrue();

        var repair = Parse("repair", "-f", "y.nntl");
        await Assert.That(repair.Command).IsEqualTo(CliCommand.Repair);
        await Assert.That(repair.File).IsEqualTo("y.nntl");
        await Assert.That(repair.Force).IsTrue();
    }

    [Test]
    public async Task Help_is_reachable_everywhere()
    {
        await Assert.That(Parse().Command).IsEqualTo(CliCommand.Help);
        await Assert.That(Parse("--help").Command).IsEqualTo(CliCommand.Help);
        await Assert.That(Parse("-h").Command).IsEqualTo(CliCommand.Help);
        var topic = Parse("help", "record");
        await Assert.That(topic.HelpTopic).IsEqualTo(CliCommand.Record);
        var inline = Parse("record", "--help");
        await Assert.That(inline.Command).IsEqualTo(CliCommand.Help);
        await Assert.That(inline.HelpTopic).IsEqualTo(CliCommand.Record);
        await Assert.That(Parse("--version").Command).IsEqualTo(CliCommand.Version);
    }

    [Test]
    public async Task Usage_errors_exit_with_2_and_help_with_0()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new TapCli(output, error);

        await Assert.That(await cli.RunAsync(["list"], null, CancellationToken.None)).IsEqualTo((int)ExitCode.Usage);
        await Assert.That(error.ToString()).Contains("nexnet-tap help");
        await Assert.That(await cli.RunAsync(["help", "record"], null, CancellationToken.None)).IsEqualTo((int)ExitCode.Ok);
        await Assert.That(output.ToString()).Contains("--session <sel>");
        await Assert.That(output.ToString()).Contains("Exit codes:");
    }
}
