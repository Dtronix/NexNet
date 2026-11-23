using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Cli;

/// <summary>Builds the <c>info</c> summary of a recording in one pass over its blocks in file order.</summary>
internal static class RecordingAnalyzer
{
    /// <summary>Summarises a recording.</summary>
    /// <param name="stream">Readable, seekable stream holding the file from offset 0; left open.</param>
    /// <param name="fileName">Name shown in the summary.</param>
    /// <param name="messages">
    /// Also run the I03 message assembler. It keeps every recorded byte in memory, so it is opt-in.
    /// </param>
    /// <exception cref="NntlFormatException">The stream is not a <c>.nntl</c> recording.</exception>
    public static RecordingSummary Analyze(Stream stream, string fileName, bool messages)
    {
        using var reader = NntlReader.Open(stream, NntlOpenMode.PreferFooter, leaveOpen: true);
        var header = reader.Header;
        List<string> schemas = [];
        Dictionary<long, SessionTally> sessions = [];
        List<SessionTally> order = [];

        SessionTally Get(long id)
        {
            if (!sessions.TryGetValue(id, out var tally))
            {
                tally = new SessionTally(id, messages);
                sessions.Add(id, tally);
                order.Add(tally);
            }

            return tally;
        }

        foreach (var entry in reader.Index.ToFileOrder())
        {
            if (!reader.TryReadBlock(entry.Offset, out var block))
                continue;
            try
            {
                switch (block.Kind)
                {
                    case NntlBlockKind.Schema:
                        var hash = Convert.ToHexStringLower(NntlCodec.DecodeSchema(block.Body).Hash.Span);
                        if (!schemas.Contains(hash))
                            schemas.Add(hash);
                        break;
                    case NntlBlockKind.SessionOpen:
                        var open = NntlCodec.DecodeSessionOpen(block.Body);
                        Get(open.SessionId).Open(open, entry.Ticks);
                        break;
                    case NntlBlockKind.TransportOp:
                        var op = NntlCodec.DecodeTransportOp(block.Body);
                        Get(op.SessionId).AddOp(op);
                        break;
                    case NntlBlockKind.Gap:
                        var gap = NntlCodec.DecodeGap(block.Body);
                        Get(gap.SessionId).AddGap(gap);
                        break;
                    case NntlBlockKind.Snapshot:
                        var snapshot = NntlCodec.DecodeSnapshot(block.Body);
                        Get(snapshot.SessionId).AddSnapshot(snapshot.Ticks);
                        break;
                    case NntlBlockKind.SessionClose:
                        var close = NntlCodec.DecodeSessionClose(block.Body);
                        Get(close.SessionId).SetClose(close);
                        break;
                }
            }
            catch (NntlFormatException)
            {
                // The block passed its checksum but does not decode; the index scan already reported it as damage.
            }
        }

        var summaries = new SessionSummary[order.Count];
        for (var i = 0; i < summaries.Length; i++)
            summaries[i] = order[i].ToSummary(header, schemas);

        var damage = new DamageSummary[reader.Damage.Count];
        for (var i = 0; i < damage.Length; i++)
            damage[i] = new DamageSummary(reader.Damage[i].Kind, reader.Damage[i].Offset, reader.Damage[i].Length);

        return new RecordingSummary(fileName, reader.Length, header.FormatVersion,
            new DateTime(header.WallClockUtcTicks, DateTimeKind.Utc), header.Producer, header.ProducerVersion, header.Host,
            reader.IndexSource, reader.Index.BlockCount, damage, schemas.ToArray(), summaries);
    }

    private sealed class SessionTally(long id, bool messages)
    {
        private readonly DirectionTally _inbound = new(TapDirection.Inbound, messages);
        private readonly DirectionTally _outbound = new(TapDirection.Outbound, messages);
        private NntlSessionOpen? _open;
        private NntlSessionClose? _close;
        private long _startTicks = long.MaxValue;
        private long _lastTicks = long.MinValue;
        private bool _snapshot;
        private long _gaps;
        private long _droppedOps;
        private long _droppedBytes;
        private MessageAssembler? _assembler;

        public void Open(in NntlSessionOpen open, long ticks)
        {
            _open = open with { SchemaHash = open.SchemaHash.ToArray() };
            _startTicks = Math.Min(_startTicks, ticks);
            Touch(ticks);
        }

        public void AddOp(in NntlTransportOp op)
        {
            Touch(op.Ticks);
            if (messages && _assembler is null)
                _assembler = new MessageAssembler(_inbound.Stream!, _outbound.Stream!, _open?.Role);
            (op.Direction == TapDirection.Inbound ? _inbound : _outbound).Add(op, _assembler);
        }

        public void AddGap(in NntlGap gap)
        {
            Touch(gap.ToTicks);
            _gaps++;
            _droppedOps += gap.DroppedOps;
            _droppedBytes += gap.DroppedBytes;
            (gap.Direction == TapDirection.Inbound ? _inbound : _outbound).StopAtGap();
        }

        public void AddSnapshot(long ticks)
        {
            _snapshot = true;
            Touch(ticks);
        }

        public void SetClose(in NntlSessionClose close)
        {
            _close = close;
            Touch(close.Ticks);
        }

        public SessionSummary ToSummary(in NntlFileHeader header, List<string> schemas)
        {
            _assembler?.Complete();
            var start = _startTicks != long.MaxValue ? _startTicks : _lastTicks;
            var end = _close?.Ticks ?? _lastTicks;
            var hasTime = _lastTicks != long.MinValue;
            string? schemaHash = _open is { SchemaHash.Length: > 0 } o ? Convert.ToHexStringLower(o.SchemaHash.Span) : null;
            var inbound = _inbound.ToSummary();
            var outbound = _outbound.ToSummary();
            MessageSummary? messageSummary = _assembler is { } a
                ? new MessageSummary(a.Greetings.Length, a.Invocations.Length, a.Messages.Length, a.Pipes.Length,
                    a.ChannelItems.Length, a.CollectionMessages.Length)
                : messages ? new MessageSummary() : null;

            return new SessionSummary(
                id,
                _open?.Role,
                _open?.TransportName,
                _open?.EndpointName,
                _open?.LocalEndpoint,
                _open?.RemoteEndpoint,
                hasTime ? header.ToUtc(start) : null,
                hasTime ? header.ToUtc(end) : null,
                hasTime ? Math.Max(0, header.ToTimeSpan(end - start).TotalSeconds) : 0,
                inbound.Preamble || outbound.Preamble,
                _snapshot,
                schemaHash,
                schemaHash is not null && schemas.Contains(schemaHash),
                _close is { } c ? new CloseSummary(c.Kind, c.DisconnectReason) : null,
                inbound,
                outbound,
                new GapSummary(_gaps, _droppedOps, _droppedBytes),
                messageSummary);
        }

        private void Touch(long ticks)
        {
            if (ticks > _lastTicks)
                _lastTicks = ticks;
        }
    }

    private sealed class DirectionTally(TapDirection direction, bool keepBytes)
    {
        private readonly FrameTally _frames = new();
        private FrameParser? _parser;
        private bool _stopped;
        private long _ops;
        private long _bytes;
        private long _redacted;

        /// <summary>The direction's bytes, kept for the message assembler.</summary>
        public InMemoryStreamBytes? Stream { get; } = keepBytes ? new InMemoryStreamBytes() : null;

        public void Add(in NntlTransportOp op, MessageAssembler? assembler)
        {
            _ops++;
            _bytes += op.Bytes.Length;
            if ((op.Flags & NntlTransportOpFlags.Redacted) != 0)
                _redacted++;
            if (_stopped)
                return;

            var bytes = op.Bytes.Span;
            // Only a recording that began with the connection carries the preamble; an attach starts mid-stream.
            _parser ??= new FrameParser(direction, StartsWithPreamble(bytes));
            Stream?.AppendCopy(bytes);
            _parser.Push(op.OpId, op.Ticks, bytes, new TallySink(_frames, assembler));
        }

        public void StopAtGap() => _stopped = true;

        public DirectionSummary ToSummary()
        {
            var framing = _stopped ? FramingState.StoppedAtGap
                : _parser is { IsFaulted: true } ? FramingState.Faulted
                : _parser is { HasPending: true } ? FramingState.EndsMidFrame
                : FramingState.Complete;
            return new DirectionSummary(_ops, _bytes, _redacted, _frames.Frames, _frames.Preamble, _frames.Malformed,
                _frames.TransportSplit, framing);
        }

        private static bool StartsWithPreamble(ReadOnlySpan<byte> bytes)
        {
            var magic = FrameLayout.PreambleMagic;
            var n = Math.Min(bytes.Length, magic.Length);
            return n > 0 && bytes[..n].SequenceEqual(magic[..n]);
        }
    }

    private sealed class FrameTally
    {
        public long Frames;
        public bool Preamble;
        public long Malformed;
        public long TransportSplit;
    }

    /// <summary>A struct sink so the parser's generic Push calls it without boxing.</summary>
    private readonly struct TallySink(FrameTally tally, MessageAssembler? assembler) : IFrameSink
    {
        public void Add(in FrameRecord frame)
        {
            switch (frame.Kind)
            {
                case FrameKind.Frame:
                    tally.Frames++;
                    if ((frame.Flags & FrameFlags.TransportSplit) != 0)
                        tally.TransportSplit++;
                    break;
                case FrameKind.Preamble:
                    tally.Preamble = true;
                    break;
                default:
                    tally.Malformed++;
                    break;
            }

            assembler?.Add(frame);
        }
    }
}
