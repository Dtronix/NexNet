using System.Text.Json.Nodes;

namespace NexNet.Interop.Remote;

/// <summary>The JSON form of a <see cref="LeakReport"/> (control protocol §5.1).</summary>
public static class LeakReports
{
    /// <summary>Parses a leak report object.</summary>
    public static LeakReport Parse(JsonObject report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var details = report["details"] is JsonArray array
            ? array.Select(n => n?.GetValue<string>() ?? "").ToArray()
            : [];
        return new LeakReport(
            Int(report, "openClients"),
            Int(report, "openPipes"),
            Int(report, "timers"),
            Int(report, "pendingInvocations"),
            Int(report, "subscriptions"),
            details);
    }

    /// <summary>The JSON of a leak report.</summary>
    public static JsonObject ToJson(LeakReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new JsonObject
        {
            ["openClients"] = report.OpenClients,
            ["openPipes"] = report.OpenPipes,
            ["timers"] = report.Timers,
            ["pendingInvocations"] = report.PendingInvocations,
            ["subscriptions"] = report.Subscriptions,
            ["details"] = new JsonArray(report.Details.Select(d => (JsonNode?)d).ToArray()),
        };
    }

    /// <summary>The sum of two reports.</summary>
    public static LeakReport Add(LeakReport a, LeakReport b)
        => new(
            a.OpenClients + b.OpenClients,
            a.OpenPipes + b.OpenPipes,
            a.Timers + b.Timers,
            a.PendingInvocations + b.PendingInvocations,
            a.Subscriptions + b.Subscriptions,
            [.. a.Details, .. b.Details]);

    private static int Int(JsonObject report, string name) => report[name]?.GetValue<int>() ?? 0;
}
