using System.Collections.Generic;
using System.Diagnostics;

namespace NexNet.Diagnostics;

/// <summary>
/// One combination of the fixed session tags (role, transport and, on a server, endpoint) within a
/// <see cref="NexusMetrics"/>, with the values summed for it. One per server endpoint or client configuration type, so
/// their number is fixed by the program.
/// </summary>
internal sealed class MetricsTagSet
{
    public NexusMetrics Metrics { get; }

    public bool IsServer { get; }
    public string TransportName { get; }

    /// <summary>Server endpoint name (<c>nexnet.endpoint</c>); null for clients.</summary>
    public string? EndpointName { get; }

    public KeyValuePair<string, object?> Role { get; }
    public KeyValuePair<string, object?> Transport { get; }
    public KeyValuePair<string, object?> Endpoint { get; }

    // Precomputed tag arrays for observable measurements.
    public KeyValuePair<string, object?>[] Tags { get; }
    public KeyValuePair<string, object?>[] SentTags { get; }
    public KeyValuePair<string, object?>[] ReceivedTags { get; }
    public KeyValuePair<string, object?>[] InboundTags { get; }
    public KeyValuePair<string, object?>[] OutboundTags { get; }
    public KeyValuePair<string, object?>[] NormalPoolTags { get; }
    public KeyValuePair<string, object?>[] SeparatePoolTags { get; }

    /// <summary>
    /// Sum over the live sessions, as of the last collection. Written under the owner's lock.
    /// </summary>
    public SessionMetricsTotals Live;

    /// <summary>
    /// Monotonic totals of the sessions that have retired. Written under the owner's lock.
    /// </summary>
    public SessionMetricsTotals Retired;

    public MetricsTagSet(NexusMetrics metrics, bool isServer, string transport, string? endpoint = null)
    {
        Metrics = metrics;
        IsServer = isServer;
        TransportName = transport;
        EndpointName = endpoint;
        Role = new(NexusMetrics.TagRole, isServer ? "server" : "client");
        Transport = new(NexusMetrics.TagTransport, transport);
        Endpoint = new(NexusMetrics.TagEndpoint, endpoint);
        Tags = With();
        SentTags = With(new(NexusMetrics.TagDirection, "sent"));
        ReceivedTags = With(new(NexusMetrics.TagDirection, "received"));
        InboundTags = With(new(NexusMetrics.TagDirection, "inbound"));
        OutboundTags = With(new(NexusMetrics.TagDirection, "outbound"));
        NormalPoolTags = With(NexusMetrics.PoolNormal);
        SeparatePoolTags = With(NexusMetrics.PoolSeparate);
    }

    private KeyValuePair<string, object?>[] With(KeyValuePair<string, object?>? extra = null)
    {
        var list = new List<KeyValuePair<string, object?>>(4) { Role, Transport };
        if (EndpointName != null)
            list.Add(Endpoint);
        if (extra is { } tag)
            list.Add(tag);
        return list.ToArray();
    }

    /// <summary>
    /// The fixed tags of this set, for a counter or histogram measurement that adds its own.
    /// </summary>
    public TagList ToTagList()
    {
        var list = new TagList { Role, Transport };
        if (EndpointName != null)
            list.Add(Endpoint);
        return list;
    }
}
