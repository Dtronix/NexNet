using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Schema;
using NexNet.Serialization;

namespace NexNet.Inspector.Tap;

/// <summary>What an inspector endpoint serves and allows, from <c>GetServerInfo</c>.</summary>
/// <remarks>
/// Clients read the recording policy here before offering to record: with <see cref="AllowRecording"/> false the
/// record controls stay visible but disabled ("Recording disabled by server config").
/// </remarks>
/// <param name="ApiVersion">Version of the endpoint API (<see cref="NexusInspector.ApiVersion"/>).</param>
/// <param name="Role">Whether the inspected side is a server (many sessions) or a client (one connection).</param>
/// <param name="AllowRecording">True when the inspected side's configuration allows recording.</param>
/// <param name="IncludeSecrets">True when auth tokens are streamed raw instead of redacted.</param>
/// <param name="SchemaHash">The inspected nexus' schema hash (<see cref="NexNet.Inspector.Core.Schema.SchemaHash"/> form); null without a schema.</param>
/// <param name="Sessions">Live tapped sessions.</param>
[NexusObject]
public sealed record InspectorServerInfo(
    [property: NexusKey(0)] int ApiVersion,
    [property: NexusKey(1)] NntlSessionRole Role,
    [property: NexusKey(2)] bool AllowRecording,
    [property: NexusKey(3)] bool IncludeSecrets,
    [property: NexusKey(4)] byte[]? SchemaHash,
    [property: NexusKey(5)] int Sessions);

/// <summary>One live tapped session, from <c>ListSessions</c>.</summary>
/// <param name="Id">
/// The session's ID in tap streams and recordings. For a server session it is the NexNet session ID. A client learns
/// its session ID from the server's greeting, after the session started, so a client session is keyed by its
/// connection number on this client (1, 2, … across reconnections) and <paramref name="ConnectedId"/> carries
/// the server's ID.
/// </param>
/// <param name="Role">Server or client side.</param>
/// <param name="State">Handshaking or connected.</param>
/// <param name="ConnectedId">The NexNet session ID once connected; 0 before.</param>
/// <param name="TransportName">Transport name (the <c>nexnet.transport</c> metric tag).</param>
/// <param name="Endpoint">Server endpoint name; null on a client.</param>
/// <param name="RemoteEndpoint">Remote address and port, when the transport reports them.</param>
/// <param name="StartedTicks">When the session started, in Stopwatch ticks of the inspected machine.</param>
/// <param name="StartedUtc">When the session started, as UTC <see cref="DateTime.Ticks"/>.</param>
[NexusObject]
public readonly record struct InspectorSessionInfo(
    [property: NexusKey(0)] long Id,
    [property: NexusKey(1)] NntlSessionRole Role,
    [property: NexusKey(2)] InspectorSessionState State,
    [property: NexusKey(3)] long ConnectedId,
    [property: NexusKey(4)] string TransportName,
    [property: NexusKey(5)] string? Endpoint,
    [property: NexusKey(6)] string? RemoteEndpoint,
    [property: NexusKey(7)] long StartedTicks,
    [property: NexusKey(8)] long StartedUtc);

/// <summary>An attach to one session.</summary>
/// <param name="Target">One server session, or the client's connection.</param>
/// <param name="SessionId"><see cref="InspectorSessionInfo.Id"/> of the session, for a session target.</param>
/// <param name="Overflow">What happens when the viewer falls behind.</param>
/// <param name="Snapshot">True to start the stream with a state snapshot (open pipes, pending invocations, collections).</param>
/// <param name="Record">
/// True when the stream will be kept as a recording (the CLI's <c>record</c>, the app's Record); refused with
/// <see cref="InspectorStatus.RecordingDisabled"/> unless the inspected side allows recording.
/// </param>
[NexusObject]
public sealed record InspectorAttachRequest(
    [property: NexusKey(0)] InspectorTargetKind Target,
    [property: NexusKey(1)] long SessionId,
    [property: NexusKey(2)] InspectorOverflow Overflow,
    [property: NexusKey(3)] bool Snapshot,
    [property: NexusKey(4)] bool Record);

/// <summary>A recording of every session: the current ones (from now, with a snapshot) and every new one from its first byte.</summary>
/// <param name="Overflow">What happens when the recorder falls behind.</param>
/// <param name="Snapshot">True to snapshot each current session when recording starts.</param>
[NexusObject]
public sealed record InspectorRecordRequest(
    [property: NexusKey(0)] InspectorOverflow Overflow,
    [property: NexusKey(1)] bool Snapshot);
