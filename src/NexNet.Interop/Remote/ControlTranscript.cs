using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace NexNet.Interop.Remote;

/// <summary>
/// The last messages of each test scope on a control connection (requests, responses, events, with times), attached to
/// a failed test as an artifact (control protocol §9).
/// </summary>
public sealed class ControlTranscript
{
    /// <summary>The scope name of host-level messages.</summary>
    public const string HostScope = "(host)";

    private readonly ConcurrentDictionary<string, Queue<string>> _scopes = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Creates a transcript.</summary>
    /// <param name="capacity">Messages kept per scope.</param>
    /// <param name="maxMessageLength">Characters kept of each message.</param>
    public ControlTranscript(int capacity = 500, int maxMessageLength = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        MaxMessageLength = maxMessageLength;
    }

    /// <summary>Messages kept per scope.</summary>
    public int Capacity { get; }

    /// <summary>Characters kept of each message.</summary>
    public int MaxMessageLength { get; }

    /// <summary>Records a message.</summary>
    /// <param name="scope">The test id, or null for host-level messages.</param>
    /// <param name="direction">"&gt;&gt;" for runner to host, "&lt;&lt;" for host to runner, "!!" for notes.</param>
    /// <param name="text">The message.</param>
    public void Record(string? scope, string direction, string text)
    {
        if (text.Length > MaxMessageLength)
            text = text[..MaxMessageLength] + "…(" + text.Length + " chars)";

        var line = (_clock.Elapsed.TotalMilliseconds).ToString("F1", CultureInfo.InvariantCulture).PadLeft(10) + " ms " + direction + " " + text;
        var queue = _scopes.GetOrAdd(scope ?? HostScope, static _ => new Queue<string>());
        lock (queue)
        {
            queue.Enqueue(line);
            while (queue.Count > Capacity)
                queue.Dequeue();
        }
    }

    /// <summary>The lines of a scope, oldest first.</summary>
    public IReadOnlyList<string> Get(string? scope)
    {
        if (!_scopes.TryGetValue(scope ?? HostScope, out var queue))
            return [];
        lock (queue)
            return queue.ToArray();
    }

    /// <summary>Forgets a scope and returns its lines.</summary>
    public IReadOnlyList<string> Remove(string scope)
    {
        if (!_scopes.TryRemove(scope, out var queue))
            return [];
        lock (queue)
            return queue.ToArray();
    }
}
