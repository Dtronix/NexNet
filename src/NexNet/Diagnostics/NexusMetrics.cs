using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using NexNet.Messages;
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

    public const string RejectionMaxPendingHandshakes = "max_pending_handshakes";

    private static readonly string? Version = typeof(NexusMetrics).Assembly.GetName().Version?.ToString();
    private static readonly Meter DefaultMeter = new(NexusMeter.Name, Version);
    private static readonly ConditionalWeakTable<Meter, NexusMetrics> ByMeter = new();

    private static readonly string[] DisconnectReasonNames = CreateDisconnectReasonNames();
    private static readonly KeyValuePair<string, object?> PhaseHandshake = new(TagPhase, "handshake");
    private static readonly KeyValuePair<string, object?> PhaseEstablished = new(TagPhase, "established");

    private readonly Lock _lock = new();
    private readonly MetricsSessionTable _sessions = new();
    private readonly List<IMeteredSession> _stale = new();
    private MetricsTagSet[] _tagSets = [];
    private long _lastRefresh;

    private readonly Counter<long> _disconnects;
    private readonly Histogram<double> _sessionDuration;
    private readonly Counter<long> _connectionRejections;

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
    /// One measurement per tag set of the value <paramref name="select"/> reads, live plus retired.
    /// </summary>
    private Measurement<long>[] Observe(Func<MetricsTagSet, long> select)
    {
        IMeteredSession[]? retired;
        Measurement<long>[] measurements;
        lock (_lock)
        {
            retired = Refresh();
            var sets = _tagSets;
            measurements = new Measurement<long>[sets.Length];
            for (var i = 0; i < sets.Length; i++)
                measurements[i] = new Measurement<long>(select(sets[i]), sets[i].Tags);
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
