using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Tests.Assembly;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Findings;

/// <summary>
/// A session crafted op by op for the findings detectors: frames written with <see cref="WireScript"/> in NexNet's wire
/// layout, fed through the session's own parsers and assembler on a microsecond clock, with gaps and a close. Every
/// step can call back (a live engine updates after each one).
/// </summary>
internal sealed class CraftedSession
{
    public const long Frequency = 1_000_000;
    public static readonly NntlFileHeader Header = new(NntlFormat.Version, 0, Frequency, 0, "tests", "1", null);

    private long _inboundOps;
    private long _outboundOps;

    public CraftedSession(NntlSessionRole role = NntlSessionRole.Client, long id = 1, string? remote = "10.0.0.2:5000", double startMs = 0)
    {
        NntlSessionOpen open = new(id, role, Ticks(startMs), "Tcp", null, null, remote, ReadOnlyMemory<byte>.Empty);
        Session = new CaptureSession(CaptureSource.Live, Header, open, new LiveOpStore(long.MaxValue), new LiveOpStore(long.MaxValue),
            new SchemaResolution(SchemaStatus.Missing, SchemaSource.None, null, null));
    }

    public CaptureSession Session { get; }

    /// <summary>Called after every step.</summary>
    public Action? AfterStep { get; set; }

    /// <summary>The direction the client writes, seen from the tapped endpoint.</summary>
    public TapDirection ClientToServer => Session.Role == NntlSessionRole.Client ? TapDirection.Outbound : TapDirection.Inbound;

    public TapDirection ServerToClient => Session.Role == NntlSessionRole.Client ? TapDirection.Inbound : TapDirection.Outbound;

    public static long Ticks(double ms) => (long)Math.Round(ms * 1000);

    /// <summary>Frames at <paramref name="ms"/>: each run of one direction is one transport op.</summary>
    public CraftedSession At(double ms, Action<WireScript> frames)
    {
        var script = new WireScript();
        frames(script);
        var i = 0;
        while (i < script.Frames.Count)
        {
            var direction = script.Frames[i].Direction;
            var bytes = new List<byte>();
            while (i < script.Frames.Count && script.Frames[i].Direction == direction)
                bytes.AddRange(script.Frames[i++].Bytes);
            Op(direction, ms, bytes.ToArray());
        }

        AfterStep?.Invoke();
        return this;
    }

    /// <summary>Raw bytes as one op.</summary>
    public CraftedSession Raw(TapDirection direction, double ms, byte[] bytes)
    {
        Op(direction, ms, bytes);
        AfterStep?.Invoke();
        return this;
    }

    /// <summary>Preambles both ways and both greetings: the session captured from its first byte.</summary>
    public CraftedSession Handshake(double ms = 0, int maxMessageSize = 4 * 1024 * 1024, int credits = 32, bool reconnect = false)
    {
        Op(ClientToServer, ms, Preamble);
        Op(ServerToClient, ms, Preamble);
        return At(ms, s => s
            .Message(ClientToServer, reconnect ? MessageType.ClientGreetingReconnection : MessageType.ClientGreeting, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(6);
                w.Write("v1");
                w.Write(300);
                w.Write(-5);
                w.WriteBinary("token"u8.ToArray());
                w.Write(maxMessageSize);
                w.Write(credits);
            })
            .Message(ServerToClient, MessageType.ServerGreeting, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(5);
                w.Write(-5);
                w.Write(42L);
                w.Write(maxMessageSize);
                w.Write(credits);
                w.Write(30000u);
            }));
    }

    public CraftedSession Gap(TapDirection direction, double fromMs, double toMs, long ops = 3, long bytes = 300)
    {
        lock (Session.Gate)
            Session.AddGap(new NntlGap(Session.SessionId, direction, Ticks(fromMs), Ticks(toMs), ops, bytes));
        AfterStep?.Invoke();
        return this;
    }

    public CraftedSession Close(double ms, DisconnectReason reason = DisconnectReason.Graceful)
    {
        lock (Session.Gate)
            Session.AddClose(new NntlSessionClose(Session.SessionId, Ticks(ms), NntlCloseKind.Disconnected, reason));
        AfterStep?.Invoke();
        return this;
    }

    public CraftedSession Truncate()
    {
        lock (Session.Gate)
            Session.Finish(CaptureState.Truncated);
        AfterStep?.Invoke();
        return this;
    }

    public static byte[] Preamble => [0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1];

    /// <summary>Arguments: one integer.</summary>
    public static void IntArgument(ref MsgPackWriter w)
    {
        w.WriteArrayHeader(1);
        w.Write(1);
    }

    public static void Nil(ref MsgPackWriter w) => w.WriteNil();

    public static WireScript Credit(WireScript script, TapDirection direction, ushort credits)
        => script.Message(direction, MessageType.InvocationCredit, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.Write(credits);
        });

    private void Op(TapDirection direction, double ms, byte[] bytes)
    {
        var id = direction == TapDirection.Inbound ? ++_inboundOps : ++_outboundOps;
        NntlTransportOp op = new(Session.SessionId, direction, id, Ticks(ms), NntlTransportOpFlags.None, ReadOnlyMemory<NntlByteRange>.Empty, bytes);
        lock (Session.Gate)
            Session.AddOp(op, 0);
    }
}
