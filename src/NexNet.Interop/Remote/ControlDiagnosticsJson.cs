using System.Globalization;
using System.Text.Json.Nodes;
using NexNet.Diagnostics;

namespace NexNet.Interop.Remote;

/// <summary>
/// Reads the results of <c>client.snapshot</c> and <c>client.metrics</c> (control protocol §6.4) into the C# snapshot
/// records and <see cref="ClientMetrics"/>.
/// </summary>
public static class ControlDiagnosticsJson
{
    /// <summary>
    /// A session snapshot: the field names of <c>tests/golden/snapshot-fields.json</c>, durations in milliseconds, the
    /// session id as decimal text, enums by name. A null or absent field keeps the record's default.
    /// </summary>
    public static NexusSessionSnapshot ReadSnapshot(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new NexusSessionSnapshot
        {
            Id = Id(json["id"]),
            IsServer = Bool(json["isServer"]),
            Transport = json["transport"]?.GetValue<string>() ?? "",
            Endpoint = json["endpoint"]?.GetValue<string>(),
            IsSecure = json["isSecure"] is { } secure ? secure.GetValue<bool>() : null,
            PingInterval = Duration(json["pingInterval"]),
            State = json["state"] is { } state ? Enum.Parse<ConnectionState>(state.GetValue<string>()) : default,
            IsEstablished = Bool(json["isEstablished"]),
            Connected = Duration(json["connected"]) ?? TimeSpan.Zero,
            SinceLastReceived = Duration(json["sinceLastReceived"]) ?? TimeSpan.Zero,
            SinceLastSent = Duration(json["sinceLastSent"]),
            FlushInProgress = Duration(json["flushInProgress"]),
            Budget = json["budget"] is JsonObject budget
                ? new NexusBudgetSnapshot { Limit = Long(budget["limit"]), Reserved = Long(budget["reserved"]) }
                : new NexusBudgetSnapshot(),
            Invocations = json["invocations"] is JsonObject invocations ? ReadInvocations(invocations) : new NexusInvocationSnapshot(),
            Pipes = (json["pipes"] as JsonArray)?.OfType<JsonObject>().Select(ReadPipe).ToArray() ?? [],
            LargeMessages = (json["largeMessages"] as JsonArray)?.OfType<JsonObject>().Select(ReadLargeMessage).ToArray() ?? [],
            FramesSent = Long(json["framesSent"]),
            BytesSent = Long(json["bytesSent"]),
            FramesReceived = Long(json["framesReceived"]),
            BytesReceived = Long(json["bytesReceived"]),
        };
    }

    /// <summary>
    /// The result of <c>client.metrics</c>: <c>{events, observed}</c>, each a list of <c>{instrument, value, tags, count?}</c>.
    /// </summary>
    public static ClientMetrics ReadMetrics(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new ClientMetrics(ReadMeasurements(json["events"]), ReadMeasurements(json["observed"]));
    }

    private static MetricMeasurement[] ReadMeasurements(JsonNode? node)
        => (node as JsonArray)?.OfType<JsonObject>().Select(m => new MetricMeasurement(
            m["instrument"]?.GetValue<string>() ?? "",
            Number(m["value"]) ?? 0,
            (m["tags"] as JsonObject)?.ToDictionary(t => t.Key, t => t.Value?.GetValue<string>() ?? "", StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal),
            m["count"] is { } count ? Long(count) : 1)).ToArray() ?? [];

    private static NexusInvocationSnapshot ReadInvocations(JsonObject json) => new()
    {
        Queued = Int(json["queued"]),
        Executing = Int(json["executing"]),
        MaxExecuting = Int(json["maxExecuting"]),
        InboundCredits = Int(json["inboundCredits"]),
        OutboundCredits = Int(json["outboundCredits"]),
        CreditWaits = Int(json["creditWaits"]),
        AwaitingResults = Int(json["awaitingResults"]),
        CreditDrops = Long(json["creditDrops"]),
        OldestQueuedWait = Duration(json["oldestQueuedWait"]),
        SeparatePoolQueued = Int(json["separatePoolQueued"]),
        SeparatePoolExecuting = Int(json["separatePoolExecuting"]),
        SeparatePoolMaxExecuting = Int(json["separatePoolMaxExecuting"]),
        SeparatePoolOldestQueuedWait = Duration(json["separatePoolOldestQueuedWait"]),
    };

    private static NexusPipeSnapshot ReadPipe(JsonObject json) => new()
    {
        Id = (ushort)Long(json["id"]),
        IsReady = Bool(json["isReady"]),
        ReaderCompleted = Bool(json["readerCompleted"]),
        WriterCompleted = Bool(json["writerCompleted"]),
        Closing = Duration(json["closing"]),
        Window = Int(json["window"]),
        ReceiveCredit = Long(json["receiveCredit"]),
        Buffered = Long(json["buffered"]),
        ConsumedNotGranted = Long(json["consumedNotGranted"]),
        ExtraOutstanding = Long(json["extraOutstanding"]),
        SinceLastData = Duration(json["sinceLastData"]),
        PartialItem = json["partialItem"] is JsonObject partial
            ? new NexusPartialItemSnapshot { Length = Long(partial["length"]), Received = Long(partial["received"]) }
            : null,
        SendCredit = Long(json["sendCredit"]),
        WaitingForCredit = Duration(json["waitingForCredit"]),
        WaitingBytes = Long(json["waitingBytes"]),
    };

    private static NexusLargeMessageSnapshot ReadLargeMessage(JsonObject json) => new()
    {
        Direction = Enum.Parse<NexusDirection>(json["direction"]!.GetValue<string>(), ignoreCase: true),
        Kind = Enum.Parse<NexusLargeMessageKind>(json["kind"]!.GetValue<string>(), ignoreCase: true),
        InvocationId = (ushort)Long(json["invocationId"]),
        Length = Long(json["length"]),
        Transferred = Long(json["transferred"]),
        SinceLastProgress = Duration(json["sinceLastProgress"]) ?? TimeSpan.Zero,
        Aborting = Bool(json["aborting"]),
    };

    private static long Id(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return Long(node);
    }

    private static bool Bool(JsonNode? node) => node?.GetValue<bool>() ?? false;

    private static int Int(JsonNode? node) => (int)Long(node);

    private static long Long(JsonNode? node) => Number(node) is { } n ? (long)Math.Round(n) : 0;

    private static TimeSpan? Duration(JsonNode? node) => Number(node) is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<double>(out var d))
            return d;
        if (value.TryGetValue<long>(out var l))
            return l;
        if (value.TryGetValue<int>(out var i))
            return i;
        throw new FormatException($"Not a number: {node.ToJsonString()}.");
    }
}
