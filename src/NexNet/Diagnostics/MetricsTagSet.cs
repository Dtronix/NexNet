using System.Collections.Generic;

namespace NexNet.Diagnostics;

/// <summary>
/// One combination of the fixed session tags (role and transport) within a <see cref="NexusMetrics"/>, with the
/// values summed for it. One per server or client configuration type, so their number is fixed by the program.
/// </summary>
internal sealed class MetricsTagSet
{
    public NexusMetrics Metrics { get; }

    public bool IsServer { get; }
    public string TransportName { get; }

    public KeyValuePair<string, object?> Role { get; }
    public KeyValuePair<string, object?> Transport { get; }

    // Precomputed tag arrays for observable measurements.
    public KeyValuePair<string, object?>[] Tags { get; }
    public KeyValuePair<string, object?>[] SentTags { get; }
    public KeyValuePair<string, object?>[] ReceivedTags { get; }
    public KeyValuePair<string, object?>[] InboundTags { get; }
    public KeyValuePair<string, object?>[] OutboundTags { get; }

    /// <summary>
    /// Sum over the live sessions, as of the last collection. Written under the owner's lock.
    /// </summary>
    public SessionMetricsTotals Live;

    /// <summary>
    /// Monotonic totals of the sessions that have retired. Written under the owner's lock.
    /// </summary>
    public SessionMetricsTotals Retired;

    public MetricsTagSet(NexusMetrics metrics, bool isServer, string transport)
    {
        Metrics = metrics;
        IsServer = isServer;
        TransportName = transport;
        Role = new(NexusMetrics.TagRole, isServer ? "server" : "client");
        Transport = new(NexusMetrics.TagTransport, transport);
        Tags = [Role, Transport];
        SentTags = [Role, Transport, new(NexusMetrics.TagDirection, "sent")];
        ReceivedTags = [Role, Transport, new(NexusMetrics.TagDirection, "received")];
        InboundTags = [Role, Transport, new(NexusMetrics.TagDirection, "inbound")];
        OutboundTags = [Role, Transport, new(NexusMetrics.TagDirection, "outbound")];
    }
}
