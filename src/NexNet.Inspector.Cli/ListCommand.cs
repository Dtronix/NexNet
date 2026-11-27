using System.Globalization;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Cli;

/// <summary><c>nexnet-tap list</c>: the endpoint's policy and its live sessions.</summary>
internal sealed class ListCommand(TapCli cli)
{
    public async Task<ExitCode> RunAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var client = await EndpointSession.ConnectAsync(cli, args, cancellationToken).ConfigureAwait(false);
        if (client is null)
            return ExitCode.Failed;

        await using (client.ConfigureAwait(false))
        {
            var info = await client.GetServerInfoAsync().ConfigureAwait(false);
            var sessions = await client.ListSessionsAsync().ConfigureAwait(false);
            Write(cli.Output, args.Endpoint, info, sessions);
        }

        return ExitCode.Ok;
    }

    public static void Write(TextWriter o, EndpointAddress endpoint, InspectorServerInfo info, InspectorSessionInfo[] sessions)
    {
        o.WriteLine($"Endpoint   {endpoint}");
        o.WriteLine($"Inspected  {Text.Role(info.Role)} (endpoint API v{info.ApiVersion})");
        o.WriteLine($"Recording  {(info.AllowRecording ? "allowed" : "Recording disabled by server config")}");
        o.WriteLine($"Secrets    {(info.IncludeSecrets ? "included (auth tokens recorded raw)" : "redacted")}");
        o.WriteLine($"Schema     {(info.SchemaHash is { Length: > 0 } hash ? Convert.ToHexStringLower(hash) : "none")}");
        o.WriteLine($"Sessions   {sessions.Length.ToString(CultureInfo.InvariantCulture)}");
        if (sessions.Length == 0)
            return;

        o.WriteLine();
        string[] head = ["ID", "CONNECTED ID", "ROLE", "STATE", "TRANSPORT", "ENDPOINT", "REMOTE", "STARTED (UTC)"];
        var rows = new string[sessions.Length][];
        for (var i = 0; i < sessions.Length; i++)
        {
            var s = sessions[i];
            rows[i] =
            [
                s.Id.ToString(CultureInfo.InvariantCulture),
                s.ConnectedId == 0 ? "-" : s.ConnectedId.ToString(CultureInfo.InvariantCulture),
                Text.Role(s.Role),
                s.State == InspectorSessionState.Connected ? "connected" : "handshaking",
                s.TransportName,
                s.Endpoint ?? "-",
                s.RemoteEndpoint ?? "-",
                Text.Utc(new DateTime(s.StartedUtc, DateTimeKind.Utc)),
            ];
        }

        Text.WriteTable(o, head, rows);
    }
}

/// <summary>Formatting shared by the commands. Invariant culture: output is parsed by scripts.</summary>
internal static class Text
{
    public static string Role(NntlSessionRole role) => role == NntlSessionRole.Server ? "server" : "client";

    public static string Utc(DateTime utc) =>
        utc.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Seconds(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";

    public static void WriteTable(TextWriter o, string[] head, string[][] rows)
    {
        var widths = new int[head.Length];
        for (var c = 0; c < head.Length; c++)
        {
            widths[c] = head[c].Length;
            foreach (var row in rows)
                widths[c] = Math.Max(widths[c], row[c].Length);
        }

        WriteRow(o, head, widths);
        foreach (var row in rows)
            WriteRow(o, row, widths);
    }

    private static void WriteRow(TextWriter o, string[] cells, int[] widths)
    {
        for (var c = 0; c < cells.Length; c++)
        {
            if (c == cells.Length - 1)
            {
                o.WriteLine(cells[c]);
                break;
            }

            o.Write(cells[c].PadRight(widths[c] + 2));
        }
    }
}
