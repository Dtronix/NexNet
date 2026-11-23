using System.Buffers;
using NexNet.Diagnostics;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.Sample;

/// <summary>The checked-in fixture recordings (src/NexNet.Inspector.Tests/Fixtures).</summary>
public enum FixtureKind : byte
{
    /// <summary>Server-side recording of one Full Chat session, with a Drop-mode gap.</summary>
    ChatServer = 0,

    /// <summary>The same session recorded on the client, for Compare.</summary>
    ChatClient = 1,

    /// <summary>Record-all from an inspector endpoint: three sessions, one already connected (snapshot).</summary>
    ServerAll = 2,

    /// <summary>Two sessions of the legacy Chat API: their schema hash differs from the current Chat schema.</summary>
    LegacySchema = 3,

    /// <summary><see cref="ServerAll"/> cut inside its last block, without footer: a crashed recording.</summary>
    Truncated = 4,

    /// <summary>
    /// Another run of the Full scenario recorded on the client, with a changed first message and one extra SendMessage:
    /// the Compare pair with real differences against <see cref="ChatClient"/>.
    /// </summary>
    ChatClientVariant = 5,
}

/// <summary>Names and checks of the fixture recordings.</summary>
public static class Fixtures
{
    /// <summary>Every fixture, in generation order.</summary>
    public static readonly FixtureKind[] All =
    [
        FixtureKind.ChatServer, FixtureKind.ChatClient, FixtureKind.ServerAll, FixtureKind.LegacySchema, FixtureKind.Truncated,
        FixtureKind.ChatClientVariant,
    ];

    /// <summary>The first message of the variant run (the Full scenario's is "hello everyone").</summary>
    public const string VariantHelloText = "hello everyone!";

    /// <summary>The SendMessage the variant run inserts after its first one.</summary>
    public const string VariantExtraMessage = "one more thing";

    /// <summary>Attachment size of the Full scenario in the fixtures.</summary>
    public const int AttachmentBytes = 300_000;

    /// <summary>Log lines of the Full scenario's TailLogs channel.</summary>
    public const int LogLines = 12;

    /// <summary>
    /// The longest the SendAttachment call may take, from its MessageStart to its result: the inspect-live screen shows
    /// it whole in a 50 ms window. A run on a loaded machine that took longer is recorded again.
    /// </summary>
    public const double MaxAttachmentMilliseconds = 80;

    /// <summary>How long an invocation took on the wire: its request's first frame to its result's last.</summary>
    public static double AttachmentMilliseconds(MessageAssembler assembler, in InvocationRow invocation, long frequency)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        if (invocation.Request.IsNone || invocation.Result.IsNone
            || !assembler.TryGetFrame(invocation.Request.Direction, invocation.Request.Seq, out var first))
            return double.PositiveInfinity;
        var result = new List<TraceFragment>();
        assembler.GetFragments(invocation.Result, result);
        long end = first.EndTicks;
        foreach (var fragment in result)
        {
            if (assembler.TryGetFrame(fragment.Direction, fragment.Seq, out var frame))
                end = Math.Max(end, frame.EndTicks);
        }

        return (end - first.StartTicks) * 1000.0 / Math.Max(1, frequency);
    }

    /// <summary>File name of a fixture.</summary>
    public static string FileName(FixtureKind kind) => kind switch
    {
        FixtureKind.ChatServer => "chat-server.nntl",
        FixtureKind.ChatClient => "chat-client.nntl",
        FixtureKind.ServerAll => "server-all.nntl",
        FixtureKind.LegacySchema => "legacy-schema.nntl",
        FixtureKind.Truncated => "server-all-truncated.nntl",
        FixtureKind.ChatClientVariant => "chat-client-variant.nntl",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// What is wrong with a fixture recording; empty when it holds everything the screens and tests rely on. The
    /// generator retries a run whose timing missed (no ping between the chunks, no gap).
    /// </summary>
    public static List<string> Check(FixtureKind kind, RecordingAnalysis recording)
    {
        var problems = new List<string>();
        var complete = kind != FixtureKind.Truncated;
        Expect(problems, recording.IndexSource == (complete ? NntlIndexSource.Footer : NntlIndexSource.Scan),
            $"index source {recording.IndexSource}");
        if (complete)
            Expect(problems, recording.Damage.Count == 0, $"{recording.Damage.Count} damaged regions");
        else
            Expect(problems, recording.Damage.Count == 1 && recording.Damage[0].Kind == NntlDamageKind.TornTail, "no torn tail");
        Expect(problems, recording.Length < 1024 * 1024, $"{recording.Length} bytes: fixtures stay under 1 MiB");
        Expect(problems, recording.SchemaBlocks == 1, $"{recording.SchemaBlocks} schema blocks");

        switch (kind)
        {
            case FixtureKind.ChatServer:
            case FixtureKind.ChatClient:
            case FixtureKind.ChatClientVariant:
                Expect(problems, recording.Sessions.Count == 1, $"{recording.Sessions.Count} sessions");
                if (recording.Sessions.Count == 1)
                {
                    CheckFullSession(problems, recording.Sessions[0], kind == FixtureKind.ChatServer, kind == FixtureKind.ChatClientVariant,
                        recording.Header.StopwatchFrequency);
                }

                break;
            case FixtureKind.ServerAll:
            case FixtureKind.Truncated:
                Expect(problems, recording.Sessions.Count == 3, $"{recording.Sessions.Count} sessions");
                Expect(problems, recording.Sessions.Count(s => s.HasSnapshot) == 1, "not exactly one session with a snapshot");
                foreach (var session in recording.Sessions)
                {
                    CheckCommon(problems, session, NntlSessionRole.Server, SchemaHashOf<ChatServerNexus>());
                    Expect(problems, session.ClientCalls("SendMessage").Length >= 1, $"session {session.Open.SessionId}: no SendMessage");
                    if (!session.HasSnapshot)
                        CheckHandshake(problems, session, NntlSessionRole.Server);
                }

                break;
            case FixtureKind.LegacySchema:
                Expect(problems, recording.Sessions.Count == 2, $"{recording.Sessions.Count} sessions");
                foreach (var session in recording.Sessions)
                {
                    CheckCommon(problems, session, NntlSessionRole.Server, SchemaHashOf<LegacyChatServerNexus>());
                    CheckHandshake(problems, session, NntlSessionRole.Server);
                    Expect(problems, !SchemaHash.Matches(session.Open.SchemaHash.Span, SchemaHashOf<ChatServerNexus>()),
                        "the legacy schema hash matches the current Chat schema");
                    Expect(problems, session.ClientCalls("SendMessage").Length >= 1, "no SendMessage");
                }

                break;
        }

        return problems;
    }

    private static void CheckFullSession(List<string> problems, SessionAnalysis session, bool serverSide, bool variant, long frequency)
    {
        var role = serverSide ? NntlSessionRole.Server : NntlSessionRole.Client;
        CheckCommon(problems, session, role, serverSide ? SchemaHashOf<ChatServerNexus>() : SchemaHashOf<ChatClientNexus>());
        CheckHandshake(problems, session, role);
        var assembler = session.Assembler;
        var decoder = session.Decoder;

        // Handshake + cancel.
        var sends = session.ClientCalls("SendMessage");
        Expect(problems, sends.Length == ChatScenario.FullMessages + (variant ? 1 : 0), $"{sends.Length} SendMessage calls");
        if (sends.Length > 1)
        {
            var hello = decoder.DecodeArguments(sends[0]);
            Expect(problems, SessionAnalysis.TryFindField(hello, "Text", out var helloText)
                             && hello.GetText(helloText) == (variant ? VariantHelloText : "hello everyone"), "the first message's text");
            if (variant)
            {
                var extra = decoder.DecodeArguments(sends[1]);
                Expect(problems, SessionAnalysis.TryFindField(extra, "Text", out var extraText) && extra.GetText(extraText) == VariantExtraMessage,
                    "the variant's extra message");
            }
        }
        var cancelled = session.ClientCalls("SendMessage").Where(i => i.CancellationSeq >= 0).ToArray();
        Expect(problems, cancelled.Length == 1, $"{cancelled.Length} canceled SendMessage calls");
        if (cancelled.Length == 1)
        {
            var callerDirection = cancelled[0].Direction;
            Expect(problems, assembler.TryGetFrame(callerDirection, cancelled[0].CancellationSeq, out var cancel)
                             && cancel.Type == MessageType.InvocationCancellation && (cancel.Flags & FrameFlags.Cancel) != 0,
                "the cancellation frame has no X flag");
            var args = decoder.DecodeArguments(cancelled[0]);
            Expect(problems, SessionAnalysis.TryFindField(args, "Text", out var text) && args.GetText(text) == ChatScenario.SlowText,
                "the canceled message does not decode");
        }

        // SendAttachment: five chunks with the FileUpload pipe and a ping in between.
        var attachments = session.ClientCalls("SendAttachment");
        Expect(problems, attachments.Length == 1, $"{attachments.Length} SendAttachment calls");
        if (attachments.Length == 1)
        {
            var request = attachments[0].Request;
            var large = request.Kind == MessageRefKind.Assembled ? assembler.Messages[request.Index] : default;
            Expect(problems, request.Kind == MessageRefKind.Assembled && large.Kind == AssembledMessageKind.Large
                             && large.State == AssemblyState.Complete, "SendAttachment is not a complete large message");
            if (request.Kind == MessageRefKind.Assembled)
            {
                var fragments = new List<TraceFragment>();
                assembler.GetFragments(request, fragments);
                var chunks = fragments.Count(f => f.Length > 0);
                Expect(problems, chunks == 5, $"SendAttachment has {chunks} chunks");
                var between = new List<FrameRecord>();
                assembler.GetInterleaved(request, between);
                Expect(problems, between.Any(f => f.Target == FrameTarget.Pipe), "no pipe frame between the chunks");
                Expect(problems, between.Any(f => f.Type == MessageType.Ping), "no ping between the chunks");
                var span = AttachmentMilliseconds(assembler, attachments[0], frequency);
                Expect(problems, span <= MaxAttachmentMilliseconds, $"SendAttachment took {span:N1} ms");
                var args = decoder.DecodeArguments(attachments[0]);
                Expect(problems, args.Problem == DecodeProblem.None && SessionAnalysis.TryFindField(args, "Data", out var data)
                                 && data.ContentLength == Fixtures.AttachmentBytes, "the attachment does not decode");
            }

            Expect(problems, attachments[0].State == InvocationState.Completed, $"SendAttachment is {attachments[0].State}");
        }

        // FileUpload pipes, the LogLine channel and the Users collection.
        Expect(problems, session.ClientCalls("UploadFile").Length == 2, "not two UploadFile calls");
        var tails = session.ClientCalls("TailLogs");
        Expect(problems, tails.Length == 2, $"{tails.Length} TailLogs calls");
        if (tails.Length > 0 && tails[0].Pipe >= 0)
        {
            var pipe = assembler.Pipes[tails[0].Pipe];
            Expect(problems, pipe.Content == PipeContent.Channel, "TailLogs did not open a channel");
            var items = assembler.ChannelItems.ToArray().Where(i => i.Pipe == pipe.Index).ToArray();
            Expect(problems, items.Length == Fixtures.LogLines, $"{items.Length} log lines on the channel");
            Expect(problems, items.All(i => decoder.DecodeChannelItem(i) is { Problem: DecodeProblem.None } value
                                            && SessionAnalysis.TryFindField(value, "Text", out _)), "a log line does not decode");
        }
        else
            problems.Add("the first TailLogs opened no pipe");

        Expect(problems, assembler.Collections.Length == 1, $"{assembler.Collections.Length} collections");
        var messages = assembler.CollectionMessages.ToArray();
        Expect(problems, messages.Any(m => m.Kind == CollectionMessageKind.ResetComplete), "no Users reset");
        Expect(problems, messages.Any(m => m.Kind == CollectionMessageKind.Insert), "no Users insert");
        Expect(problems, messages.All(m => decoder.DecodeCollectionMessage(m).Problem == DecodeProblem.None),
            "a Users message does not decode");
        Expect(problems, assembler.Pipes.Length >= 5, $"{assembler.Pipes.Length} pipes");

        // The gap: only the stalled server recorder dropped ops.
        if (serverSide)
            Expect(problems, session.Gaps.Count > 0 && session.Gaps.Sum(g => g.DroppedOps) > 0, "no gap");
        else
            Expect(problems, session.Gaps.Count == 0, "the client recording has a gap");
    }

    // Embedded schema, parsers, decoding of every complete client call.
    private static void CheckCommon(List<string> problems, SessionAnalysis session, NntlSessionRole role, ulong schemaHash)
    {
        var id = session.Open.SessionId;
        Expect(problems, session.Open.Role == role, $"session {id}: role {session.Open.Role}");
        Expect(problems, SchemaHash.Matches(session.Open.SchemaHash.Span, schemaHash), $"session {id}: schema hash differs from the nexus");
        Expect(problems, session.Schema != null, $"session {id}: embedded schema missing");
        Expect(problems, !session.InboundFaulted && !session.OutboundFaulted, $"session {id}: a direction stopped framing");
        foreach (var invocation in session.Assembler.Invocations)
        {
            if (!invocation.RequestParsed || invocation.Request.IsNone || session.Decoder.IsClientCall(invocation) != true)
                continue;
            var args = session.Decoder.DecodeArguments(invocation);
            Expect(problems, args.Problem == DecodeProblem.None, $"session {id}: invocation {invocation.InvocationId} decodes with {args.Problem}");
        }
    }

    // Preamble both ways, both greetings, and the client's token redacted to its marker.
    private static void CheckHandshake(List<string> problems, SessionAnalysis session, NntlSessionRole role)
    {
        var id = session.Open.SessionId;
        Expect(problems, session.InboundFromInception && session.OutboundFromInception, $"session {id}: not recorded from its preamble");
        var greetings = session.Assembler.Greetings;
        Expect(problems, greetings.Length == 2, $"session {id}: {greetings.Length} greetings");
        var clientToServer = role == NntlSessionRole.Server ? TapDirection.Inbound : TapDirection.Outbound;
        foreach (var greeting in greetings)
        {
            if (greeting.Kind != GreetingKind.Client)
                continue;
            Expect(problems, greeting.Direction == clientToServer, $"session {id}: ClientGreeting direction");
            var marker = new byte[ChatScenario.Token.Length];
            InspectorRedaction.WriteMarker(ChatScenario.Token, marker);
            Expect(problems, session.Assembler.TryGetPayload(greeting.Token, out var token) && token.ToArray().AsSpan().SequenceEqual(marker),
                $"session {id}: the auth token is not the redaction marker");
        }

        var redacted = role == NntlSessionRole.Server ? session.InboundRedactedBytes : session.OutboundRedactedBytes;
        Expect(problems, redacted == ChatScenario.Token.Length, $"session {id}: {redacted} bytes flagged as redacted");
    }

    /// <summary>The schema hash a generated nexus embeds.</summary>
    public static ulong SchemaHashOf<TNexus>() where TNexus : IInspectorSchemaSource => TNexus.InspectorSchemaHash;

    private static void Expect(List<string> problems, bool condition, string problem)
    {
        if (!condition)
            problems.Add(problem);
    }
}
