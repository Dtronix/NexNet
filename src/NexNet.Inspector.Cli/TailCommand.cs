using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Cli;

/// <summary>
/// <c>nexnet-tap tail</c>: the text renderer (Core <c>Text/</c>) over a live endpoint or a recording, with filters,
/// budgets, sampling and <c>--until</c>, one flushed line at a time.
/// </summary>
/// <remarks>
/// A recording is replayed session by session in file order (indexed, as the app opens it); <c>--follow</c> instead
/// reads it front to back as it grows. A live tail attaches to one session (or records all) and renders what
/// arrives every <see cref="RenderInterval"/>. Interleaved output (several sessions streamed at once) tags every
/// line with its session ID. Ctrl+C and <c>--duration</c> end with a stop line and, for a file, the resume cursor.
/// </remarks>
internal sealed class TailCommand(TapCli cli)
{
    /// <summary>How often a streamed tail renders what arrived.</summary>
    internal static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DetachTimeout = TimeSpan.FromSeconds(5);

    public async Task<ExitCode> RunAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var options = new TextOptions
        {
            Level = args.Level,
            Format = args.Format,
            Collapse = args.Collapse,
            ValueWidth = args.ValueWidth,
            Depth = args.Depth,
            Filter = TailFilter.Create(args, null, out _),
        };

        if (args.After is { } after && after.Filter != options.Filter.Fingerprint())
        {
            await cli.Error.WriteLineAsync("nexnet-tap: --after: the cursor was made with different filters (--method, --pipe, --dir, --only, --slow, --grep).")
                .ConfigureAwait(false);
            return ExitCode.Usage;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (args.Duration is { } duration)
            stop.CancelAfter(duration);

        var printer = new TailPrinter(cli.Output, options, args, resumable: !args.IsLiveTail);
        if (args.IsLiveTail)
            return await LiveAsync(args, options, printer, cancellationToken, stop.Token).ConfigureAwait(false);
        if (args.Follow)
            return await FollowAsync(args, options, printer, cancellationToken, stop.Token).ConfigureAwait(false);
        return await ReplayAsync(args, options, printer, cancellationToken, stop.Token).ConfigureAwait(false);
    }

    private static TextRenderer CreateRenderer(CaptureSession session, CliArguments args, TextOptions options, TextCursor? after)
    {
        var filter = TailFilter.Create(args, session, out var tail);
        var renderer = new TextRenderer(session, options with { Filter = filter, After = after });
        if (tail is not null)
            tail.Renderer = renderer;
        return renderer;
    }

    private static string Reason(CancellationToken user) => user.IsCancellationRequested ? "interrupted" : "duration reached";

    // ----- Recording replay -----

    private async Task<ExitCode> ReplayAsync(CliArguments args, TextOptions options, TailPrinter printer, CancellationToken user,
        CancellationToken stop)
    {
        var path = args.TailFile!;
        RecordingFile recording;
        try
        {
            recording = RecordingFile.Open(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NntlFormatException)
        {
            await cli.Error.WriteLineAsync($"nexnet-tap: cannot read {path}: {e.Message}").ConfigureAwait(false);
            return ExitCode.Failed;
        }

        using (recording)
        {
            var ids = new List<long>();
            foreach (var open in recording.Sessions)
            {
                if (!args.Has(CliOption.Session) || args.Selector == SessionSelectorKind.All
                    || (args.Selector == SessionSelectorKind.First && ids.Count == 0)
                    || (args.Selector == SessionSelectorKind.Id && open.SessionId == args.SessionId))
                    ids.Add(open.SessionId);
            }

            if (ids.Count == 0)
            {
                await cli.Error.WriteLineAsync(args.Selector == SessionSelectorKind.Id && args.Has(CliOption.Session)
                    ? $"nexnet-tap: {path} has no session {args.SessionId}; see 'nexnet-tap info {path}'."
                    : $"nexnet-tap: {path} holds no session.").ConfigureAwait(false);
                return ExitCode.NotFound;
            }

            var start = 0;
            if (args.After is { } after)
            {
                start = ids.IndexOf(after.SessionId);
                if (start < 0)
                {
                    await cli.Error.WriteLineAsync($"nexnet-tap: --after: the cursor belongs to session {after.SessionId}, which this tail does not read.")
                        .ConfigureAwait(false);
                    return ExitCode.Usage;
                }
            }

            var records = new List<TextRecord>();
            TextRenderer? last = null;
            for (var i = start; i < ids.Count && !stop.IsCancellationRequested; i++)
            {
                var session = recording.CreateSession(ids[i]);
                try
                {
                    recording.Index(session, cancellationToken: stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                TextRenderer renderer;
                try
                {
                    renderer = CreateRenderer(session, args, options, i == start ? args.After : null);
                }
                catch (ArgumentException e)
                {
                    await cli.Error.WriteLineAsync("nexnet-tap: --after: " + e.Message).ConfigureAwait(false);
                    return ExitCode.Usage;
                }

                last = renderer;
                records.Clear();
                renderer.Collect(records);
                if (!printer.Write(records, renderer, null, stop))
                    break;
            }

            return Finish(printer, args, user, stop, printer.CancelledAt ?? last?.Position, null);
        }
    }

    // What a tail that ended returns, writing the stop line it still owes.
    private ExitCode Finish(TailPrinter printer, CliArguments args, CancellationToken user, CancellationToken stop, TextCursor? cursor,
        string? tag)
    {
        switch (printer.Stopped)
        {
            case TailStop.Until:
                return ExitCode.Ok;
            case TailStop.Budget:
                return args.Until is not null ? ExitCode.NotFound : ExitCode.Ok;
        }

        if (stop.IsCancellationRequested)
        {
            printer.WriteStop(Reason(user), cursor, tag);
            return args.Until is not null && !user.IsCancellationRequested ? ExitCode.NotFound : ExitCode.Ok;
        }

        if (args.Until is { } until)
        {
            printer.WriteStop("until " + until.Text + " not met", null, tag);
            return ExitCode.NotFound;
        }

        return ExitCode.Ok;
    }

    // ----- Streamed: a growing recording, or a live endpoint -----

    private async Task<ExitCode> FollowAsync(CliArguments args, TextOptions options, TailPrinter printer, CancellationToken user,
        CancellationToken stop)
    {
        var path = args.TailFile!;
        FollowFileSource source;
        try
        {
            source = new FollowFileSource(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await cli.Error.WriteLineAsync($"nexnet-tap: cannot read {path}: {e.Message}").ConfigureAwait(false);
            return ExitCode.Failed;
        }

        await using (source.ConfigureAwait(false))
        {
            // Without --session, sessions are followed one after the other (the next once the previous one ended), as
            // a replay prints them; --session all interleaves them, each line tagged with its session.
            var selector = args.Has(CliOption.Session) ? args.Selector : SessionSelectorKind.All;
            return await StreamAsync(source, args, options, printer, selector, interleaved: args.Has(CliOption.Session) && selector == SessionSelectorKind.All,
                user, stop).ConfigureAwait(false);
        }
    }

    private async Task<ExitCode> StreamAsync(ITailSource source, CliArguments args, TextOptions options, TailPrinter printer,
        SessionSelectorKind selector, bool interleaved, CancellationToken user, CancellationToken stop)
    {
        var set = new LiveCaptureSet();
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var pump = Task.Run(() => PumpAsync(source, set, pumpStop.Token), CancellationToken.None);

        var summary = args.Level == TextLevel.Summary;
        var renderers = new List<(TextRenderer Renderer, string? Tag)>();
        var known = new HashSet<long>();
        var sessions = new List<CaptureSession>();
        var records = new List<TextRecord>();
        var reachedCursor = args.After is null;
        var stopped = false;
        while (!stopped)
        {
            var ended = pump.IsCompleted;
            var cancelled = stop.IsCancellationRequested;
            if (!summary || ended || cancelled)
            {
                set.CopySessions(sessions);
                foreach (var session in sessions)
                {
                    if (!known.Add(session.SessionId))
                        continue;
                    var take = selector switch
                    {
                        SessionSelectorKind.First => known.Count == 1,
                        SessionSelectorKind.Id => session.SessionId == args.SessionId,
                        _ => true,
                    };
                    TextCursor? after = null;
                    if (!reachedCursor)
                    {
                        if (session.SessionId != args.After!.Value.SessionId)
                            continue;
                        reachedCursor = true;
                        after = args.After;
                    }

                    if (take)
                        renderers.Add((CreateRenderer(session, args, options, after), interleaved ? $"@0x{session.SessionId:X}" : null));
                }

                foreach (var (renderer, tag) in renderers)
                {
                    if (renderer.IsComplete)
                        continue;
                    if (!interleaved && renderer != renderers[0].Renderer && !Previous(renderers, renderer).IsComplete)
                        break;
                    records.Clear();
                    renderer.Collect(records);
                    if (!printer.Write(records, renderer, tag, cancelled ? CancellationToken.None : stop))
                    {
                        stopped = true;
                        break;
                    }
                }
            }

            if (ended || cancelled)
                break;
            await Task.WhenAny(pump, Task.Delay(RenderInterval, stop)).ConfigureAwait(false);
        }

        await pumpStop.CancelAsync().ConfigureAwait(false);
        var failure = await pump.ConfigureAwait(false);
        if (failure is not null)
            await cli.Error.WriteLineAsync("nexnet-tap: the stream failed: " + failure).ConfigureAwait(false);

        if (printer.Stopped is TailStop.None or TailStop.Cancelled && stop.IsCancellationRequested)
        {
            // One stop line per session still running, each with its cursor (a file) and tag.
            var wrote = false;
            foreach (var (renderer, tag) in renderers)
            {
                var cut = ReferenceEquals(renderer, printer.CancelledIn);
                if (renderer.IsComplete && !cut)
                    continue;
                printer.WriteStop(Reason(user), cut ? printer.CancelledAt : renderer.Position, tag);
                wrote = true;
            }

            if (!wrote)
                printer.WriteStop(Reason(user), null, null);
            return args.Until is not null && !user.IsCancellationRequested ? ExitCode.NotFound : ExitCode.Ok;
        }

        if (printer.Stopped == TailStop.None && renderers.Count == 0 && failure is null && args.Until is null)
        {
            await cli.Error.WriteLineAsync("nexnet-tap: the stream ended before a session to tail opened.").ConfigureAwait(false);
            return ExitCode.NotFound;
        }

        var code = Finish(printer, args, user, stop, null, null);
        return failure is not null && code == ExitCode.Ok ? ExitCode.Failed : code;
    }

    private static TextRenderer Previous(List<(TextRenderer Renderer, string? Tag)> renderers, TextRenderer renderer)
    {
        for (var i = 1; i < renderers.Count; i++)
        {
            if (ReferenceEquals(renderers[i].Renderer, renderer))
                return renderers[i - 1].Renderer;
        }

        return renderer;
    }

    // Reads blocks into the set until the stream ends; returns why it failed, or null.
    private static async Task<string?> PumpAsync(ITailSource source, LiveCaptureSet set, CancellationToken cancellationToken)
    {
        try
        {
            while (await source.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (set.Accept(source.Current) == LiveCaptureEvent.StreamEnded)
                    return null;
            }

            // A read canceled by Ctrl+C may end quietly too: the sessions are then not over, only no longer read.
            if (!cancellationToken.IsCancellationRequested)
                set.Abandon();
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is NntlFormatException or IOException or InvalidOperationException)
        {
            set.Abandon();
            return e.Message;
        }
    }

    // ----- Live endpoint -----

    private async Task<ExitCode> LiveAsync(CliArguments args, TextOptions options, TailPrinter printer, CancellationToken user,
        CancellationToken stop)
    {
        var client = await EndpointSession.ConnectAsync(cli, args, user).ConfigureAwait(false);
        if (client is null)
            return ExitCode.Failed;

        await using (client.ConfigureAwait(false))
        {
            InspectorStream stream;
            string what;
            try
            {
                var info = await client.GetServerInfoAsync().ConfigureAwait(false);
                if (args.Selector == SessionSelectorKind.All)
                {
                    // Every session (new ones from their first byte) comes only as a record-all stream.
                    if (!info.AllowRecording)
                    {
                        await cli.Error.WriteLineAsync("Recording disabled by server config (tail --session all records every session; tail one session instead)")
                            .ConfigureAwait(false);
                        return ExitCode.Refused;
                    }

                    stream = await client.RecordAllAsync(new InspectorRecordRequest(InspectorOverflow.Drop, Snapshot: true), user)
                        .ConfigureAwait(false);
                    what = "all sessions";
                }
                else
                {
                    var session = await ResolveSessionAsync(client, args, user).ConfigureAwait(false);
                    if (session is not { } found)
                        return ExitCode.NotFound;
                    var target = info.Role == NntlSessionRole.Client ? InspectorTargetKind.Client : InspectorTargetKind.Session;
                    stream = await client.AttachAsync(
                        new InspectorAttachRequest(target, found.Id, InspectorOverflow.Drop, Snapshot: true, Record: false), user)
                        .ConfigureAwait(false);
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
            catch (OperationCanceledException) when (user.IsCancellationRequested)
            {
                await cli.Error.WriteLineAsync("nexnet-tap: interrupted before the tail started.").ConfigureAwait(false);
                return ExitCode.Cancelled;
            }

            // Status goes to stderr: stdout carries only the rendered lines.
            await cli.Error.WriteLineAsync($"Tailing {what} from {args.Endpoint}. Press Ctrl+C to stop.").ConfigureAwait(false);
            cli.TailStarted?.Invoke();
            var source = new EndpointTailSource(stream);
            await using (source.ConfigureAwait(false))
            {
                var code = await StreamAsync(source, args, options, printer, args.Selector, interleaved: args.Selector == SessionSelectorKind.All,
                    user, stop).ConfigureAwait(false);
                try
                {
                    await client.DetachAsync().WaitAsync(DetachTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The endpoint is gone or the stream already ended.
                }

                return code;
            }
        }
    }

    private async Task<InspectorSessionInfo?> ResolveSessionAsync(InspectorClient client, CliArguments args, CancellationToken cancellationToken)
    {
        var sessions = await client.ListSessionsAsync().ConfigureAwait(false);
        if (args.Selector == SessionSelectorKind.Id)
        {
            foreach (var s in sessions)
            {
                if (s.Id == args.SessionId || s.ConnectedId == args.SessionId)
                    return s;
            }

            await cli.Error.WriteLineAsync($"nexnet-tap: no live session {args.SessionId} on {args.Endpoint}; see 'nexnet-tap list'.")
                .ConfigureAwait(false);
            return null;
        }

        if (sessions.Length > 0)
            return sessions[0];

        await cli.Error.WriteLineAsync($"Waiting up to {Text.Seconds(args.Wait.TotalSeconds)} for a session...").ConfigureAwait(false);
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

    private sealed class EndpointTailSource(InspectorStream stream) : ITailSource
    {
        public NntlBlock Current => stream.Current;

        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken) => stream.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
