# Metrics and Diagnostics

NexNet reports what its sessions are doing through a `System.Diagnostics.Metrics` meter, and can return a snapshot of one session's live state on demand. Use the meter for dashboards and alerts across a fleet, and the snapshot to see why one session is stuck.

## Meter

The meter is named `NexNet` (`NexusMeter.Name`). Any listener for `System.Diagnostics.Metrics` can collect it: OpenTelemetry, `dotnet-counters`, or a `MeterListener`.

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("NexNet"));
```

```
dotnet-counters monitor --counters NexNet -n MyServer
```

`ConfigBase.MeterFactory` selects the `IMeterFactory` the meter is created from. When it is null, one meter shared by the whole process is used. NexNet.Asp sets it to the application's `IMeterFactory`. Set it before the server is configured or the client is created.

```csharp
var config = new TcpServerConfig
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    MeterFactory = serviceProvider.GetRequiredService<IMeterFactory>(),
};
```

### Cost

Values that change per frame are plain fields on each session, written by the thread that already owns them. The observable instruments add them up when a listener collects, in one pass over the sessions shared by every instrument of that collection. Sending and receiving pay nothing extra whether or not a listener is attached. Counters are only used for rare events such as disconnects.

A collection takes time proportional to the number of sessions and allocates a few small arrays per instrument. It reads each session without taking its write lock or any pipe's buffer lock. Connecting and disconnecting take a short lock on the meter's session table.

### Tags

Every instrument carries two tags. Their values are fixed by the program, so the number of time series does not grow with connections.

| Tag | Values |
|-----|--------|
| `nexnet.role` | `server`, `client` |
| `nexnet.transport` | `tcp`, `tls`, `uds`, `websocket`, `httpsocket`, `quic`; `custom` for a configuration type that does not set `ConfigBase.TransportName` |

No instrument is tagged with a session, pipe or method ID, an address, or any value the peer sends.

### Sessions

| Instrument | Type | Unit | Extra tags | Measures |
|------------|------|------|------------|----------|
| `nexnet.session.active` | UpDownCounter | `{session}` | | Sessions open, from the transport connection until disconnect. |
| `nexnet.session.handshaking` | UpDownCounter | `{session}` | | Sessions that have not completed the handshake (and, on a server, authentication). |
| `nexnet.session.disconnects` | Counter | `{session}` | `nexnet.disconnect.reason`, `nexnet.session.phase` | Sessions ended. The reason is the `DisconnectReason` in snake case (`graceful`, `timeout`, `stalled`, `flow_control`, ...). The phase is `handshake` or `established`. |
| `nexnet.session.duration` | Histogram | `s` | `nexnet.disconnect.reason` | Time from the transport connection to disconnect. |
| `nexnet.connection.rejections` | Counter | `{connection}` | `nexnet.rejection.reason` | Connections a server closed before starting a session: `max_pending_handshakes`, or a rate limiting result (`max_concurrent_connections`, `global_rate`, `per_ip_concurrent`, `per_ip_rate`, `ip_banned`). |

### Transport

| Instrument | Type | Unit | Extra tags | Measures |
|------------|------|------|------------|----------|
| `nexnet.io.frames` | Counter | `{frame}` | `nexnet.direction` (`sent`, `received`) | Frames written to and read from the transport. |
| `nexnet.io.bytes` | Counter | `By` | `nexnet.direction` (`sent`, `received`) | Bytes written to and read from the transport, frame headers included. |

### Flow control

See [Limits and Flow Control](limits-and-flow-control.md) for what these quantities are.

| Instrument | Type | Unit | Extra tags | Measures |
|------------|------|------|------------|----------|
| `nexnet.budget.reserved` | UpDownCounter | `By` | | Session budget reserved: pipe windows, invocation queues, large values. |
| `nexnet.budget.limit` | UpDownCounter | `By` | | Session budget available in total (`SessionBudget` per session). |
| `nexnet.pipe.open` | UpDownCounter | `{pipe}` | | Pipes holding a receive window: open, or closing until both sides confirmed. |
| `nexnet.pipe.window` | UpDownCounter | `By` | | Receive windows reserved by those pipes. |
| `nexnet.pipe.resets` | Counter | `{pipe}` | `nexnet.reset.reason` (`invalid_item`, `too_large`) | Pipes reset because the peer sent a channel item that breaks the framing or exceeds `MaxMessageSize`. |
| `nexnet.pipe.refusals` | Counter | `{pipe}` | `nexnet.refusal.side` (`local`, `remote`) | Pipe opens refused because a session limit was reached, on this side or by the peer. |
| `nexnet.invocation.queued` | UpDownCounter | `{invocation}` | | Invocations received and waiting for an execution slot. |
| `nexnet.invocation.executing` | UpDownCounter | `{invocation}` | | Invocations running. One with a pipe or channel argument stops counting once its pipe is registered. |
| `nexnet.invocation.credit_waits` | UpDownCounter | `{invocation}` | | Calls waiting for the peer to return invocation credit. |
| `nexnet.invocation.busy` | Counter | `{invocation}` | | Invocations sent to several sessions that found a session busy on the first pass. |
| `nexnet.invocation.credit_drops` | Counter | `{invocation}` | | Invocations skipped because the peer returned no credit within `ProgressTimeout`. |
| `nexnet.large_message.in_flight` | UpDownCounter | `{message}` | `nexnet.direction` (`inbound`, `outbound`) | Arguments and results larger than one frame being received or sent. |
| `nexnet.large_message.aborts` | Counter | `{message}` | `nexnet.direction`, `nexnet.abort.reason` (`busy`, `too_large`, `cancelled`) | Large values stopped before completing. |

## Session snapshot

`GetSnapshot()` returns a `NexusSessionSnapshot` of one session's live state:

- `SessionContext.GetSnapshot()` inside a nexus method, for the calling session.
- `NexusClient.GetSnapshot()`, or null when the client is not connected.
- `NexusServer.GetSnapshot()` returns a `NexusServerSnapshot` with a snapshot of every authenticated session, the pending handshake count and the rate limiter statistics.

A snapshot holds:

| Part | Contents |
|------|----------|
| Session | ID, role, transport, state, time connected, time since data was last received and sent, whether an outbound flush is in progress and for how long. |
| Budget | Limit and reserved bytes. |
| Invocations | Queued, executing, credits the peer has left here, credits this side may still spend, calls waiting for credit, calls waiting for a result, invocations dropped for lack of credit. |
| Pipes | Per pipe: ID, state, receive window, credit the peer still has, bytes buffered, bytes consumed and not yet granted back, extra credit outstanding, time since data last arrived, partial channel item (bytes received of its total), send credit, how long the writer has waited for credit and how many bytes it holds, how long the pipe has been closing. |
| Large messages | Per value: direction, kind (argument or result), invocation ID, bytes transferred of the total, time since the last chunk. |
| Transport | Frames and bytes sent and received. |

The values are read one at a time from a running session, so they are not an atomic picture: two values in one snapshot can come from moments microseconds apart. Taking a snapshot takes the per-pipe buffer lock briefly and allocates; it is meant for diagnostics, not for every request.

```csharp
public async ValueTask Diagnostics()
{
    var snapshot = Context.GetSnapshot();
    foreach (var pipe in snapshot.Pipes)
    {
        if (pipe.PartialItem is { } item)
            Context.Logger?.LogInfo($"P{pipe.Id}: item {item.Received} of {item.Length} bytes, idle {pipe.SinceLastData}");
    }
}
```

> [!WARNING]
> A snapshot exposes internal state: buffer sizes, timings and the IDs of sessions, pipes and invocations. Do not serve it to unauthenticated callers.

## Log messages

When a session disconnects with `Stalled`, the warning names what made no progress, for example:

```
Session 12 made no progress within 30000 ms: pipe P00513 item 182 KiB of 300 KiB, no data for 30.0 s.
```

A pipe reset logs a warning with its reason, at most once every 10 seconds per session, with the number of resets since the last warning.
