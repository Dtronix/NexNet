using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Timeline;

/// <summary>
/// Builds a <see cref="TimelineModel"/> by hand, in milliseconds on a microsecond clock, for layout, input, allocation
/// and gallery tests: lanes are added the way <see cref="TimelineProjection"/> adds them, without a capture.
/// </summary>
internal sealed class TimelineDataBuilder
{
    /// <summary>Ticks per second: one tick per microsecond keeps the arithmetic readable.</summary>
    public const long Frequency = 1_000_000;

    private readonly TimelineModel _model = new() { Frequency = Frequency };
    private long _inboundSeq;
    private long _outboundSeq;

    public TimelineDataBuilder(double durationMs = 100) => _model.Bounds = new TimeWindow(0, Ticks(durationMs));

    public static long Ticks(double ms) => (long)Math.Round(ms * Frequency / 1000.0);

    public static TimeWindow Window(double fromMs, double toMs) => new(Ticks(fromMs), Ticks(toMs));

    public TimelineModel Model => _model;

    /// <summary>The fixed groups a session always shows.</summary>
    public TimelineDataBuilder SessionGroups()
    {
        _model.AlwaysShow(LaneGroupKind.Transport);
        _model.AlwaysShow(LaneGroupKind.Frames);
        _model.AlwaysShow(LaneGroupKind.Invocations);
        _model.AlwaysShow(LaneGroupKind.Control);
        return this;
    }

    public TimelineDataBuilder Transport(string name = "TCP")
    {
        _model.TransportName = name;
        return this;
    }

    /// <summary>A read (or write) op block from <paramref name="ms"/>, <paramref name="widthMs"/> wide.</summary>
    public TimelineDataBuilder Op(TapDirection direction, double ms, double widthMs = 0, int bytes = 64)
    {
        TimelineLane lane = direction == TapDirection.Inbound
            ? _model.GetOrAddLane(new LaneKey(LaneKind.Reads, 0), LaneGroupKind.Transport, LaneGlyph.Inbound, "reads")
            : _model.GetOrAddLane(new LaneKey(LaneKind.Writes, 0), LaneGroupKind.Transport, LaneGlyph.Outbound, "writes");
        lane.Add(new TimelineMark(Ticks(ms), Ticks(ms + widthMs), MarkKind.Block, direction, MarkFlags.Op, EntityKind.None, -1, -1, bytes, -1));
        return this;
    }

    public TimelineDataBuilder TransportSplit(TapDirection direction, double ms)
    {
        LaneKey key = new(direction == TapDirection.Inbound ? LaneKind.Reads : LaneKind.Writes, 0);
        _model.FindLane(key)!.Add(TimelineMark.Line(MarkKind.TransportSplit, Ticks(ms), Ticks(ms), direction));
        return this;
    }

    /// <summary>The next frame of a direction (sequence numbers count up from 0).</summary>
    public TimelineDataBuilder Frame(TapDirection direction, double startMs, double endMs, int bytes = 32,
        EntityKind entity = EntityKind.None, int entityIndex = -1)
    {
        long seq = direction == TapDirection.Inbound ? _inboundSeq++ : _outboundSeq++;
        TimelineLane lane = direction == TapDirection.Inbound
            ? _model.GetOrAddLane(new LaneKey(LaneKind.InboundFrames, 0), LaneGroupKind.Frames, LaneGlyph.Inbound, "inbound")
            : _model.GetOrAddLane(new LaneKey(LaneKind.OutboundFrames, 0), LaneGroupKind.Frames, LaneGlyph.Outbound, "outbound");
        lane.Add(TimelineMark.Frame(Ticks(startMs), Ticks(endMs), direction, seq, bytes, _model.BytesLabel(bytes), entity, entityIndex));
        return this;
    }

    public TimelineDataBuilder Invocation(int index, string label, double startMs, double endMs, bool cancelled = false,
        TapDirection direction = TapDirection.Inbound)
    {
        _model.AddInvocation(new InvocationSpan(index, Ticks(startMs), Ticks(endMs), _model.AddLabel(label),
            cancelled ? MarkFlags.Cancelled : MarkFlags.None, direction));
        return this;
    }

    /// <summary>A pipe lane living from <paramref name="openMs"/> to <paramref name="closeMs"/> (null: still open).</summary>
    public TimelineDataBuilder Pipe(int index, string label, string? sub, double openMs, double? closeMs,
        LaneGroupKind group = LaneGroupKind.Pipes, bool openMark = false)
    {
        TimelineLane lane = _model.GetOrAddLane(new LaneKey(LaneKind.Pipe, index), group,
            group == LaneGroupKind.Collections ? LaneGlyph.Collection : LaneGlyph.Pipe, label, LaneVisibility.Lifetime);
        lane.SubLabel = sub;
        lane.LifeStart = Ticks(openMs);
        lane.LifeEnd = closeMs is { } close ? Ticks(close) : long.MaxValue;
        if (openMark)
            lane.Add(new TimelineMark(Ticks(openMs), Ticks(openMs), MarkKind.Open, TapDirection.Inbound, MarkFlags.None, EntityKind.Pipe, index, -1, 0, -1));
        if (closeMs is { } end)
            lane.Add(new TimelineMark(Ticks(end), Ticks(end), MarkKind.Close, TapDirection.Inbound, MarkFlags.None, EntityKind.Pipe, index, -1, 0, -1));
        return this;
    }

    public TimelineDataBuilder PipeData(int index, double startMs, double widthMs, int bytes, TapDirection direction = TapDirection.Inbound)
    {
        _model.FindLane(new LaneKey(LaneKind.Pipe, index))!.Add(TimelineMark.Frame(Ticks(startMs), Ticks(startMs + widthMs), direction,
            -1, bytes, _model.BytesLabel(bytes), EntityKind.Pipe, index));
        return this;
    }

    public TimelineDataBuilder PipeSplit(int index, double ms)
    {
        _model.FindLane(new LaneKey(LaneKind.Pipe, index))!.Add(TimelineMark.Line(MarkKind.NexNetSplit, Ticks(ms), Ticks(ms)));
        return this;
    }

    public TimelineDataBuilder Control(double ms, TapDirection direction = TapDirection.Inbound, bool cancel = false)
    {
        TimelineLane lane = _model.GetOrAddLane(new LaneKey(LaneKind.Control, 0), LaneGroupKind.Control, LaneGlyph.Control, "Control");
        lane.SubLabel ??= "ping · credit · window · cancel";
        lane.Add(new TimelineMark(Ticks(ms), Ticks(ms), MarkKind.Block, direction, cancel ? MarkFlags.Cancelled : MarkFlags.None,
            EntityKind.None, -1, -1, 3, -1));
        return this;
    }

    public TimelineDataBuilder Gap(double fromMs, double toMs)
    {
        _model.AddGap(new TimelineGap(Ticks(fromMs), Ticks(toMs), TapDirection.Inbound, 12, 4096));
        return this;
    }

    public TimelineDataBuilder Pin(LaneKey lane)
    {
        _model.SetPinned(lane, true);
        return this;
    }

    public TimelineModel Build()
    {
        _model.PackInvocations();
        _model.NotifyChanged();
        return _model;
    }

    /// <summary>The package's states (timeline-lane.html): reads, three packed invocation rows, three pipes, a gap.</summary>
    public static TimelineModel Demo(bool pinned = true)
    {
        TimelineDataBuilder b = new TimelineDataBuilder(100).Transport();
        for (int k = 0; k <= 46; k++)
        {
            double at = 0.5 + (2.1 * k);
            if (at is > 81 and < 86) continue;
            b.Op(TapDirection.Inbound, at, 1.1);
        }

        b.TransportSplit(TapDirection.Inbound, 40.8).TransportSplit(TapDirection.Inbound, 45.8).TransportSplit(TapDirection.Inbound, 57.6);
        b.Invocation(809, "#809 Join", 0, 9)
            .Invocation(810, "#810 SendMessage", 11, 22)
            .Invocation(811, "#811", 17.8, 20.8)
            .Invocation(812, "#812 SendAttachment", 36.2, 66.8)
            .Invocation(814, "#814 SetTyping", 38.8, 46.6)
            .Invocation(813, "#813 GetHistory", 68, 75)
            .Invocation(815, "#815 Typing", 74.2, 78.8, cancelled: true);
        b.Pipe(2, "Pipe 2·1", "Sync · closed", 0, 23)
            .Pipe(3, "Pipe 3·1", "FileUpload · raw · 16 KB frames", 0, null)
            .Pipe(5, "Pipe 5·1", "Download · opened", 73, null, openMark: true);
        foreach (double at in (ReadOnlySpan<double>)[6, 41.6, 47.2, 53.4, 72.4, 93.8])
            b.PipeData(3, at, 3.4, 16_384);
        b.PipeSplit(3, 47.2).Gap(81, 86);
        if (pinned) b.Pin(new LaneKey(LaneKind.Pipe, 3));
        return b.Build();
    }

    /// <summary>The empty state: an INVOCATIONS group with nothing in the window.</summary>
    public static TimelineModel EmptyInvocations()
    {
        TimelineDataBuilder b = new(100);
        b.Model.AlwaysShow(LaneGroupKind.Invocations);
        return b.Build();
    }

    /// <summary>
    /// A busy session for the allocation test and the axis demo: every fixed group, <paramref name="frames"/> frames a
    /// direction, ops, invocations, pipes and a gap over <paramref name="durationMs"/>.
    /// </summary>
    public static TimelineModel Busy(int frames, double durationMs = 1000)
    {
        TimelineDataBuilder b = new TimelineDataBuilder(durationMs).SessionGroups().Transport();
        double step = durationMs / frames;
        for (int i = 0; i < frames; i++)
        {
            double at = i * step;
            int invocation = i / 8;
            b.Op(TapDirection.Inbound, at).Op(TapDirection.Outbound, at + (step / 2));
            b.Frame(TapDirection.Inbound, at, at + (step / 3), 64 + (i % 7), EntityKind.Invocation, invocation);
            b.Frame(TapDirection.Outbound, at + (step / 2), at + (step * 0.8), 40, EntityKind.Invocation, invocation);
            if (i % 8 == 0)
                b.Invocation(invocation, "#" + invocation + " SendMessage", at, at + (step * (12 + (i % 5))), cancelled: i % 40 == 0);
            if (i % 16 == 0) b.Control(at + (step / 4));
        }

        b.Pipe(1, "Pipe 1·1", "FileUpload · raw", durationMs * 0.1, durationMs * 0.6, openMark: true)
            .Pipe(2, "Chan 2·1", "LogLine · open", durationMs * 0.3, null, openMark: true)
            .Pipe(3, "Users", "214 messages", 0, null, LaneGroupKind.Collections);
        for (double at = durationMs * 0.12; at < durationMs * 0.58; at += durationMs / 40)
            b.PipeData(1, at, durationMs / 100, 16_384);
        b.Gap(durationMs * 0.8, durationMs * 0.83);
        return b.Build();
    }
}
