using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Cli;

/// <summary><c>nexnet-tap record</c>: copies an endpoint stream verbatim into a <c>.nntl</c> file.</summary>
internal sealed class RecordCommand(TapCli cli)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private enum StopReason : byte
    {
        SessionClosed,
        StreamEnded,
        Duration,
        Interrupted,
    }

    public async Task<ExitCode> RunAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(args.Out!);
        if (File.Exists(path) && !args.Force)
        {
            await cli.Error.WriteLineAsync($"nexnet-tap: {path} exists; pass --force to overwrite it.").ConfigureAwait(false);
            return ExitCode.Failed;
        }

        var client = await EndpointSession.ConnectAsync(cli, args, cancellationToken).ConfigureAwait(false);
        if (client is null)
            return ExitCode.Failed;

        await using (client.ConfigureAwait(false))
        {
            InspectorStream stream;
            string what;
            try
            {
                var info = await client.GetServerInfoAsync().ConfigureAwait(false);
                // Checked before any request so the refusal reads the same whatever --session asked for.
                if (!info.AllowRecording)
                {
                    await cli.Error.WriteLineAsync("Recording disabled by server config").ConfigureAwait(false);
                    return ExitCode.Refused;
                }

                if (args.IncludeSecrets && !info.IncludeSecrets)
                {
                    await cli.Error.WriteLineAsync(
                        "nexnet-tap: --include-secrets: the server config redacts auth tokens (InspectorOptions.IncludeSecrets is off).")
                        .ConfigureAwait(false);
                    return ExitCode.Refused;
                }

                if (!args.IncludeSecrets && info.IncludeSecrets)
                    await cli.Error.WriteLineAsync("nexnet-tap: warning: the server config includes secrets; auth tokens are recorded raw.")
                        .ConfigureAwait(false);

                if (args.Selector == SessionSelectorKind.All)
                {
                    stream = await client.RecordAllAsync(new InspectorRecordRequest(args.Overflow, args.Snapshot), cancellationToken)
                        .ConfigureAwait(false);
                    what = "all sessions";
                }
                else
                {
                    var session = await ResolveSessionAsync(client, args, cancellationToken).ConfigureAwait(false);
                    if (session is not { } found)
                        return ExitCode.NotFound;

                    var target = info.Role == NntlSessionRole.Client ? InspectorTargetKind.Client : InspectorTargetKind.Session;
                    stream = await client.AttachAsync(
                        new InspectorAttachRequest(target, found.Id, args.Overflow, args.Snapshot, Record: true),
                        cancellationToken).ConfigureAwait(false);
                    what = $"session {found.Id}";
                }
            }
            catch (InspectorRequestException e)
            {
                var message = e.Status == InspectorStatus.RecordingDisabled
                    ? EndpointSession.Describe(e.Status)
                    : "nexnet-tap: the endpoint refused: " + EndpointSession.Describe(e.Status);
                await cli.Error.WriteLineAsync(message).ConfigureAwait(false);
                return EndpointSession.ToExitCode(e.Status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await cli.Error.WriteLineAsync("nexnet-tap: interrupted before recording started.").ConfigureAwait(false);
                return ExitCode.Cancelled;
            }

            return await RecordAsync(client, stream, what, path, args, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<InspectorSessionInfo?> ResolveSessionAsync(InspectorClient client, CliArguments args,
        CancellationToken cancellationToken)
    {
        var sessions = await client.ListSessionsAsync().ConfigureAwait(false);
        if (args.Selector == SessionSelectorKind.Id)
        {
            foreach (var s in sessions)
            {
                // A client-side session is listed by connection number; the server's ID is accepted too.
                if (s.Id == args.SessionId || s.ConnectedId == args.SessionId)
                    return s;
            }

            await cli.Error.WriteLineAsync($"nexnet-tap: no live session {args.SessionId} on {args.Endpoint}; see 'nexnet-tap list'.")
                .ConfigureAwait(false);
            return null;
        }

        if (sessions.Length > 0)
            return sessions[0];

        await cli.Output.WriteLineAsync($"Waiting up to {Text.Seconds(args.Wait.TotalSeconds)} for a session...").ConfigureAwait(false);
        var deadline = DateTime.UtcNow + args.Wait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            sessions = await client.ListSessionsAsync().ConfigureAwait(false);
            if (sessions.Length > 0)
                return sessions[0];
        }

        await cli.Error.WriteLineAsync($"nexnet-tap: no session appeared within {Text.Seconds(args.Wait.TotalSeconds)}.")
            .ConfigureAwait(false);
        return null;
    }

    private async Task<ExitCode> RecordAsync(InspectorClient client, InspectorStream stream, string what, string path,
        CliArguments args, CancellationToken cancellationToken)
    {
        StopReason reason;
        Exception? failure = null;
        var drained = true;
        long written;
        try
        {
            FileStream file;
            try
            {
                file = new FileStream(path, args.Force ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await cli.Error.WriteLineAsync($"nexnet-tap: cannot create {path}: {e.Message}").ConfigureAwait(false);
                await TryDetachAsync(client, cli.DrainTimeout).ConfigureAwait(false);
                return ExitCode.Failed;
            }

            await using (file.ConfigureAwait(false))
            {
                var until = args.Duration is { } d ? $" for {Text.Seconds(d.TotalSeconds)}" : "";
                await cli.Output.WriteLineAsync($"Recording {what} from {args.Endpoint} to {path}{until}. Press Ctrl+C to stop.")
                    .ConfigureAwait(false);
                cli.RecordingStarted?.Invoke();

                // Not cancelable on purpose: stopping asks the endpoint to detach, and the endpoint then ends the stream
                // with the close blocks and the index footer, which the copy must still write.
                var copy = stream.CopyToAsync(file, CancellationToken.None);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (args.Duration is { } duration)
                    stop.CancelAfter(duration);
                var stopped = Task.Delay(Timeout.Infinite, stop.Token);

                if (await Task.WhenAny(copy, stopped).ConfigureAwait(false) == copy)
                {
                    reason = args.Selector == SessionSelectorKind.All ? StopReason.StreamEnded : StopReason.SessionClosed;
                }
                else
                {
                    reason = cancellationToken.IsCancellationRequested ? StopReason.Interrupted : StopReason.Duration;
                    await TryDetachAsync(client, cli.DrainTimeout).ConfigureAwait(false);
                    drained = await Task.WhenAny(copy, Task.Delay(cli.DrainTimeout, CancellationToken.None)).ConfigureAwait(false) == copy;
                    if (!drained)
                    {
                        // The endpoint never finished; release the pipe so the copy ends with what arrived.
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                }

                try
                {
                    await copy.ConfigureAwait(false);
                }
                catch (Exception e) when (drained)
                {
                    failure = e;
                }
                catch (Exception)
                {
                    // Expected: the pipe was released under the copy.
                }

                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                written = file.Length;
            }
        }
        finally
        {
            if (drained)
                await stream.DisposeAsync().ConfigureAwait(false);
        }

        await cli.Output.WriteLineAsync("Stopped: " + reason switch
        {
            StopReason.SessionClosed => "the session closed.",
            StopReason.StreamEnded => "the endpoint ended the stream.",
            StopReason.Duration => "duration reached.",
            _ => "interrupted.",
        }).ConfigureAwait(false);
        if (failure is not null)
            await cli.Error.WriteLineAsync($"nexnet-tap: the stream failed: {failure.Message}").ConfigureAwait(false);

        return await ReportAsync(path, written).ConfigureAwait(false);
    }

    private async Task<ExitCode> ReportAsync(string path, long written)
    {
        try
        {
            using var reader = NntlReader.Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var complete = reader.IndexSource == NntlIndexSource.Footer;
            await cli.Output.WriteLineAsync(
                $"Wrote {Text.Count(written)} bytes, {Text.Count(reader.Sessions.Count)} session(s), {Text.Count(reader.Index.BlockCount)} blocks to {path}.")
                .ConfigureAwait(false);
            if (complete)
                return ExitCode.Ok;
            await cli.Error.WriteLineAsync($"nexnet-tap: the recording has no index footer; run 'nexnet-tap repair {path}'.")
                .ConfigureAwait(false);
            return ExitCode.Failed;
        }
        catch (NntlFormatException e)
        {
            await cli.Error.WriteLineAsync($"nexnet-tap: wrote {Text.Count(written)} bytes, but no readable recording: {e.Message}")
                .ConfigureAwait(false);
            return ExitCode.Failed;
        }
    }

    private static async Task TryDetachAsync(InspectorClient client, TimeSpan timeout)
    {
        try
        {
            await client.DetachAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The endpoint is gone or the stream already ended; whatever arrived is still copied.
        }
    }
}
