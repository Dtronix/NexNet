using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.RateLimiting;
using NexNet.Transports;

namespace NexNet.Diagnostics;

/// <summary>
/// NexNet's instruments on one <see cref="Meter"/>, and the live sessions they read.
/// </summary>
/// <remarks>
/// Values that change per frame live in plain fields on each session. Observable instruments sum them when a listener
/// collects, in one pass over the sessions shared by every instrument; the per-frame paths pay nothing for metrics.
/// Counters (<see cref="Counter{T}.Add(T, KeyValuePair{string, object}, KeyValuePair{string, object})"/>) are used
/// only for rare events. Tags are fixed, low-cardinality values; never a session, pipe or method ID, an address or
/// anything the peer supplies.
/// </remarks>
internal sealed class NexusMetrics
{
    public const string TagRole = "nexnet.role";
    public const string TagTransport = "nexnet.transport";
    public const string TagDirection = "nexnet.direction";
    public const string TagDisconnectReason = "nexnet.disconnect.reason";
    public const string TagPhase = "nexnet.session.phase";
    public const string TagRejectionReason = "nexnet.rejection.reason";
    public const string TagResetReason = "nexnet.reset.reason";
    public const string TagRefusalSide = "nexnet.refusal.side";
    public const string TagAbortReason = "nexnet.abort.reason";

    public const string RejectionMaxPendingHandshakes = "max_pending_handshakes";

    private static readonly string? Version = typeof(NexusMetrics).Assembly.GetName().Version?.ToString();
    private static readonly Meter DefaultMeter = new(NexusMeter.Name, Version);
    private static readonly ConditionalWeakTable<Meter, NexusMetrics> ByMeter = new();

    private static readonly string[] DisconnectReasonNames = CreateDisconnectReasonNames();
    private static readonly KeyValuePair<string, object?> PhaseHandshake = new(TagPhase, "handshake");
    private static readonly KeyValuePair<string, object?> PhaseEstablished = new(TagPhase, "established");
    private static readonly KeyValuePair<string, object?> ResetInvalidItem = new(TagResetReason, "invalid_item");
    private static readonly KeyValuePair<string, object?> ResetTooLarge = new(TagResetReason, "too_large");
    private static readonly KeyValuePair<string, object?> RefusalLocal = new(TagRefusalSide, "local");
    private static readonly KeyValuePair<string, object?> RefusalRemote = new(TagRefusalSide, "remote");
    private static readonly KeyValuePair<string, object?> DirectionInbound = new(TagDirection, "inbound");
    private static readonly KeyValuePair<string, object?> DirectionOutbound = new(TagDirection, "outbound");
    private static readonly KeyValuePair<string, object?> AbortBusy = new(TagAbortReason, "busy");
    private static readonly KeyValuePair<string, object?> AbortTooLarge = new(TagAbortReason, "too_large");
    private static readonly KeyValuePair<string, object?> AbortCancelled = new(TagAbortReason, "cancelled");

    private readonly Lock _lock = new();
    private readonly MetricsSessionTable _sessions = new();
    private readonly List<IMeteredSession> _stale = new();
    private MetricsTagSet[] _tagSets = [];
    private long _lastRefresh;

    private readonly Counter<long> _disconnects;
    private readonly Histogram<double> _sessionDuration;
    private readonly Counter<long> _connectionRejections;
    private readonly Counter<long> _pipeResets;
    private readonly Counter<long> _pipeRefusals;
    private readonly Counter<long> _invocationBusy;
    private readonly Counter<long> _creditDrops;
    private readonly Counter<long> _largeMessageAborts;

    /// <summary>
    /// Shortest time between two passes over the sessions, in <see cref="Stopwatch"/> ticks: the instruments of one
    /// collection share a pass. Tests set it to 0.
    /// </summary>
    internal long RefreshInterval = Stopwatch.Frequency / 10;

    public Meter Meter { get; }

    private NexusMetrics(Meter meter)
    {
        Meter = meter;

        meter.CreateObservableUpDownCounter("nexnet.session.active",
            () => Observe(static s => s.Live.Sessions), "{session}",
            "Sessions open, from transport connection until disconnect.");
        meter.CreateObservableUpDownCounter("nexnet.session.handshaking",
            () => Observe(static s => s.Live.Handshaking), "{session}",
            "Sessions that have not completed the handshake (and, on a server, authentication).");

        meter.CreateObservableCounter("nexnet.io.frames",
            () => ObserveDirections(static (s, sent) => sent
                ? s.Live.FramesSent + s.Retired.FramesSent
                : s.Live.FramesReceived + s.Retired.FramesReceived), "{frame}",
            "Frames written to and read from the transport.");
        meter.CreateObservableCounter("nexnet.io.bytes",
            () => ObserveDirections(static (s, sent) => sent
                ? s.Live.BytesSent + s.Retired.BytesSent
                : s.Live.BytesReceived + s.Retired.BytesReceived), "By",
            "Bytes written to and read from the transport, frame headers included.");

        meter.CreateObservableUpDownCounter("nexnet.budget.reserved",
            () => Observe(static s => s.Live.BudgetReserved), "By",
            "Session budget reserved: pipe windows, invocation queues and large values.");
        meter.CreateObservableUpDownCounter("nexnet.budget.limit",
            () => Observe(static s => s.Live.BudgetLimit), "By",
            "Session budget in total.");
        meter.CreateObservableUpDownCounter("nexnet.pipe.open",
            () => Observe(static s => s.Live.PipesOpen), "{pipe}",
            "Pipes holding a receive window: open, or closing until both sides confirmed.");
        meter.CreateObservableUpDownCounter("nexnet.pipe.window",
            () => Observe(static s => s.Live.PipeWindow), "By",
            "Receive windows reserved by open pipes.");
        meter.CreateObservableUpDownCounter("nexnet.invocation.queued",
            () => Observe(static s => s.Live.InvocationsQueued), "{invocation}",
            "Invocations received and waiting for an execution slot.");
        meter.CreateObservableUpDownCounter("nexnet.invocation.executing",
            () => Observe(static s => s.Live.InvocationsExecuting), "{invocation}",
            "Invocations running.");
        meter.CreateObservableUpDownCounter("nexnet.invocation.credit_waits",
            () => Observe(static s => s.Live.CreditWaits), "{invocation}",
            "Calls waiting for the peer to return invocation credit.");
        meter.CreateObservableUpDownCounter("nexnet.large_message.in_flight",
            () => ObserveMany(static (set, i, _) => i == 0
                    ? new Measurement<long>(set.Live.LargeMessagesInbound, set.InboundTags)
                    : new Measurement<long>(set.Live.LargeMessagesOutbound, set.OutboundTags),
                2, 0), "{message}",
            "Arguments and results larger than one frame being received or sent.");

        _disconnects = meter.CreateCounter<long>("nexnet.session.disconnects", "{session}",
            "Sessions ended, by disconnect reason and by whether the handshake had completed.");
        _sessionDuration = meter.CreateHistogram("nexnet.session.duration", "s",
            "Time from transport connection to disconnect.",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.1, 1, 10, 60, 300, 1800, 3600, 4 * 3600, 24 * 3600]
            });
        _connectionRejections = meter.CreateCounter<long>("nexnet.connection.rejections", "{connection}",
            "Connections a server closed before starting a session: handshake limit or rate limiting.");
        _pipeResets = meter.CreateCounter<long>("nexnet.pipe.resets", "{pipe}",
            "Pipes reset because the peer sent a channel item that breaks the framing or exceeds MaxMessageSize.");
        _pipeRefusals = meter.CreateCounter<long>("nexnet.pipe.refusals", "{pipe}",
            "Pipe opens refused because a session limit was reached.");
        _invocationBusy = meter.CreateCounter<long>("nexnet.invocation.busy", "{invocation}",
            "Invocations to several sessions that found a session busy on the first pass.");
        _creditDrops = meter.CreateCounter<long>("nexnet.invocation.credit_drops", "{invocation}",
            "Invocations skipped because the peer returned no invocation credit in time.");
        _largeMessageAborts = meter.CreateCounter<long>("nexnet.large_message.aborts", "{message}",
            "Arguments and results larger than one frame stopped before completing.");
    }

    /// <summary>
    /// The instance for the meter <paramref name="factory"/> creates, or for the shared meter when it is null.
    /// </summary>
    public static NexusMetrics Get(IMeterFactory? factory)
    {
        var meter = factory == null
            ? DefaultMeter
            : factory.Create(new MeterOptions(NexusMeter.Name) { Version = Version });
        return ByMeter.GetValue(meter, static m => new NexusMetrics(m));
    }

    /// <summary>
    /// The tag set for sessions of <paramref name="config"/>'s role and transport.
    /// </summary>
    public static MetricsTagSet GetTagSet(ConfigBase config)
        => Get(config.MeterFactory).GetTagSet(config is ServerConfig, config.TransportName);

    /// <summary>
    /// The tag set for <paramref name="isServer"/> and <paramref name="transport"/>; created on first use.
    /// </summary>
    public MetricsTagSet GetTagSet(bool isServer, string transport)
    {
        lock (_lock)
        {
            foreach (var set in _tagSets)
            {
                if (set.IsServer == isServer && set.TransportName == transport)
                    return set;
            }

            var created = new MetricsTagSet(this, isServer, transport);
            _tagSets = [.._tagSets, created];
            return created;
        }
    }

    /// <summary>
    /// Number of sessions in the table and the length of its slot array. For tests.
    /// </summary>
    internal (int Count, int Capacity) TableSize
    {
        get
        {
            lock (_lock)
                return (_sessions.Count, _sessions.Capacity);
        }
    }

    /// <summary>
    /// Starts counting <paramref name="session"/>. Called once its transport connection is up.
    /// </summary>
    public void Register(IMeteredSession session)
    {
        lock (_lock)
        {
            session.MetricsStartTimestamp = Stopwatch.GetTimestamp();
            _sessions.Add(session);
        }
    }

    /// <summary>
    /// Stops counting <paramref name="session"/>: adds its final monotonic values to the retired totals and records
    /// its disconnect. Only the first call for a session does anything.
    /// </summary>
    public void Retire(IMeteredSession session, DisconnectReason reason, bool established)
    {
        lock (_lock)
        {
            if (!RetireLocked(session))
                return;
        }

        RecordDisconnect(session, reason, established);
    }

    private bool RetireLocked(IMeteredSession session)
    {
        if (!_sessions.Remove(session))
            return false;

        var totals = default(SessionMetricsTotals);
        session.CollectMetrics(ref totals);
        session.MetricsTags.Retired.AddMonotonic(totals);

        // The cached live sums still include this session: sum again on the next collection, or its totals would be
        // counted twice until then and the counters would go back afterwards.
        _lastRefresh = 0;
        return true;
    }

    private void RecordDisconnect(IMeteredSession session, DisconnectReason reason, bool established)
    {
        var tags = session.MetricsTags;
        var reasonTag = new KeyValuePair<string, object?>(TagDisconnectReason, DisconnectReasonNames[(byte)reason]);
        _disconnects.Add(1, tags.Role, tags.Transport, reasonTag, established ? PhaseEstablished : PhaseHandshake);
        if (_sessionDuration.Enabled)
        {
            _sessionDuration.Record(Stopwatch.GetElapsedTime(session.MetricsStartTimestamp).TotalSeconds,
                tags.Role, tags.Transport, reasonTag);
        }
    }

    /// <summary>
    /// Records a connection a server closed before starting a session.
    /// </summary>
    public void RecordRejection(MetricsTagSet tags, string reason)
        => _connectionRejections.Add(1, tags.Role, tags.Transport, new(TagRejectionReason, reason));

    public void RecordPipeReset(MetricsTagSet tags, PipeResetReason reason)
        => _pipeResets.Add(1, tags.Role, tags.Transport, reason == PipeResetReason.TooLarge ? ResetTooLarge : ResetInvalidItem);

    public void RecordPipeRefusal(MetricsTagSet tags, bool local)
        => _pipeRefusals.Add(1, tags.Role, tags.Transport, local ? RefusalLocal : RefusalRemote);

    public void RecordInvocationBusy(MetricsTagSet tags)
        => _invocationBusy.Add(1, tags.Role, tags.Transport);

    public void RecordCreditDrop(MetricsTagSet tags)
        => _creditDrops.Add(1, tags.Role, tags.Transport);

    public void RecordLargeMessageAbort(MetricsTagSet tags, bool inbound, LargeMessageAbortReason reason)
    {
        var reasonTag = reason switch
        {
            LargeMessageAbortReason.Busy => AbortBusy,
            LargeMessageAbortReason.TooLarge => AbortTooLarge,
            _ => AbortCancelled,
        };
        _largeMessageAborts.Add(1, tags.Role, tags.Transport, inbound ? DirectionInbound : DirectionOutbound, reasonTag);
    }

    /// <summary>
    /// Sums the live sessions into each tag set, unless a pass ran within <see cref="RefreshInterval"/>. Sessions found
    /// disconnected are retired and returned, for their disconnects to be recorded outside the lock. Called under the
    /// lock.
    /// </summary>
    private IMeteredSession[]? Refresh()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastRefresh != 0 && now - _lastRefresh < RefreshInterval)
            return null;
        _lastRefresh = now;

        foreach (var set in _tagSets)
            set.Live = default;

        foreach (var session in _sessions.Slots)
        {
            if (session == null)
                continue;

            if (session.IsDisconnected)
            {
                _stale.Add(session);
                continue;
            }

            session.CollectMetrics(ref session.MetricsTags.Live);
        }

        if (_stale.Count == 0)
            return null;

        // Disconnected sessions whose disconnect did not retire them: retire them here so none stays counted. Their
        // disconnects are recorded after the lock is released.
        foreach (var session in _stale)
            RetireLocked(session);

        var retired = _stale.ToArray();
        _stale.Clear();
        return retired;
    }

    /// <summary>
    /// One measurement per tag set of the value <paramref name="select"/> reads.
    /// </summary>
    private Measurement<long>[] Observe(Func<MetricsTagSet, long> select)
        => ObserveMany(static (set, _, select) => new Measurement<long>(select(set), set.Tags), 1, select);

    /// <summary>
    /// Two measurements per tag set, sent and received, of the value <paramref name="select"/> reads.
    /// </summary>
    private Measurement<long>[] ObserveDirections(Func<MetricsTagSet, bool, long> select)
        => ObserveMany(static (set, i, select) => i == 0
                ? new Measurement<long>(select(set, true), set.SentTags)
                : new Measurement<long>(select(set, false), set.ReceivedTags),
            2, select);

    /// <summary>
    /// <paramref name="perSet"/> measurements per tag set, each made by <paramref name="measure"/> once the sessions
    /// are summed. Disconnects of sessions the pass retired are recorded after the lock is released.
    /// </summary>
    private Measurement<long>[] ObserveMany<TState>(
        Func<MetricsTagSet, int, TState, Measurement<long>> measure, int perSet, TState state)
    {
        IMeteredSession[]? retired;
        Measurement<long>[] measurements;
        lock (_lock)
        {
            retired = Refresh();
            var sets = _tagSets;
            measurements = new Measurement<long>[sets.Length * perSet];
            for (var i = 0; i < sets.Length; i++)
            {
                for (var j = 0; j < perSet; j++)
                    measurements[i * perSet + j] = measure(sets[i], j, state);
            }
        }

        if (retired != null)
        {
            foreach (var session in retired)
                RecordDisconnect(session, session.DisconnectReason, session.IsEstablished);
        }

        return measurements;
    }

    /// <summary>
    /// The <c>nexnet.rejection.reason</c> value for a rate limiter refusal.
    /// </summary>
    public static string RateLimitReasonName(ConnectionRateLimitResult result) => result switch
    {
        ConnectionRateLimitResult.MaxConcurrentConnectionsExceeded => "max_concurrent_connections",
        ConnectionRateLimitResult.GlobalRateExceeded => "global_rate",
        ConnectionRateLimitResult.PerIpConcurrentLimitExceeded => "per_ip_concurrent",
        ConnectionRateLimitResult.PerIpRateExceeded => "per_ip_rate",
        ConnectionRateLimitResult.IpBanned => "ip_banned",
        _ => "unknown",
    };

    private static string[] CreateDisconnectReasonNames()
    {
        var names = new string[256];
        Array.Fill(names, "unknown");
        foreach (var reason in Enum.GetValues<DisconnectReason>())
            names[(byte)reason] = ToSnakeCase(reason.ToString());
        return names;
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
