using System.Text.Json.Serialization;
using NexNet.Inspector.Core.Format;
using NexNet.Messages;

namespace NexNet.Inspector.Cli;

/// <summary>What <c>info</c> reports about a recording; also its <c>--json</c> document.</summary>
internal sealed record RecordingSummary(
    string File,
    long Length,
    int FormatVersion,
    DateTime WallClockUtc,
    string Producer,
    string ProducerVersion,
    string? Host,
    NntlIndexSource IndexSource,
    long Blocks,
    DamageSummary[] Damage,
    string[] Schemas,
    SessionSummary[] Sessions);

/// <summary>A damaged region found by a scan.</summary>
internal readonly record struct DamageSummary(NntlDamageKind Kind, long Offset, long Length);

/// <summary>One recorded session.</summary>
/// <param name="Id">Session ID in the recording.</param>
/// <param name="Role">Server or client side; null when the session-open block is missing (damaged file).</param>
/// <param name="StartUtc">When recording of the session began (session-open block).</param>
/// <param name="EndUtc">The session-close block, or the last recorded block when there is none.</param>
/// <param name="DurationSeconds">From <paramref name="StartUtc"/> to <paramref name="EndUtc"/>.</param>
/// <param name="FromInception">True when the recording holds the connection preamble: the session was recorded from its first byte.</param>
/// <param name="Snapshot">True when the recording starts with a state snapshot (attached mid-session).</param>
/// <param name="SchemaHash">Hex of the schema hash the session names; null without one.</param>
/// <param name="SchemaEmbedded">True when a schema block with that hash is in the file.</param>
/// <param name="Close">How the session's recording ended; null when it has no close block.</param>
/// <param name="Messages">Assembled-message counts; only with <c>info --messages</c>.</param>
internal readonly record struct SessionSummary(
    long Id,
    NntlSessionRole? Role,
    string? Transport,
    string? Endpoint,
    string? LocalEndpoint,
    string? RemoteEndpoint,
    DateTime? StartUtc,
    DateTime? EndUtc,
    double DurationSeconds,
    bool FromInception,
    bool Snapshot,
    string? SchemaHash,
    bool SchemaEmbedded,
    CloseSummary? Close,
    DirectionSummary Inbound,
    DirectionSummary Outbound,
    GapSummary Gaps,
    MessageSummary? Messages);

/// <summary>The session-close block.</summary>
internal readonly record struct CloseSummary(NntlCloseKind Kind, DisconnectReason Reason);

/// <summary>One direction of a session, as recorded and as framed by the I02 parser.</summary>
/// <param name="Ops">Transport op blocks.</param>
/// <param name="Bytes">Bytes those ops carried.</param>
/// <param name="RedactedOps">Ops with redacted ranges (the auth token).</param>
/// <param name="Frames">Complete NexNet frames, not counting the preamble.</param>
/// <param name="Preamble">True when the direction starts with the connection preamble.</param>
/// <param name="MalformedRanges">Ranges the parser could not frame.</param>
/// <param name="TransportSplitFrames">Frames that arrived over more than one transport op (flag T).</param>
/// <param name="Framing">Whether framing covered the whole direction.</param>
internal readonly record struct DirectionSummary(
    long Ops,
    long Bytes,
    long RedactedOps,
    long Frames,
    bool Preamble,
    long MalformedRanges,
    long TransportSplitFrames,
    FramingState Framing);

/// <summary>How far framing of one direction got.</summary>
internal enum FramingState : byte
{
    /// <summary>Every recorded byte was framed.</summary>
    Complete = 0,

    /// <summary>The recording ends inside a frame.</summary>
    EndsMidFrame = 1,

    /// <summary>A gap (dropped ops) broke the byte stream; frames after it are not counted.</summary>
    StoppedAtGap = 2,

    /// <summary>The parser met bytes it could not frame (for example an attach that began mid-frame).</summary>
    Faulted = 3,
}

/// <summary>Gap blocks of a session (ops dropped by the Drop overflow mode), both directions.</summary>
internal readonly record struct GapSummary(long Count, long DroppedOps, long DroppedBytes);

/// <summary>What the I03 message assembler made of a session.</summary>
internal readonly record struct MessageSummary(
    int Greetings,
    int Invocations,
    int AssembledMessages,
    int Pipes,
    int ChannelItems,
    int CollectionMessages);

/// <summary>
/// Source-generated System.Text.Json metadata for <c>info --json</c>: no reflection, so it works under Native AOT.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RecordingSummary))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
