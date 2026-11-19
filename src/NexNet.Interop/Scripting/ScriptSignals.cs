using System.Collections.Concurrent;

namespace NexNet.Interop.Scripting;

/// <summary>Named signals raised by the test and awaited by script steps. A raised signal stays raised.</summary>
public sealed class ScriptSignals
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new(StringComparer.Ordinal);

    private TaskCompletionSource Get(string name)
        => _signals.GetOrAdd(name, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Raises <paramref name="name"/>.</summary>
    public void Raise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Get(name).TrySetResult();
    }

    /// <summary>True when <paramref name="name"/> has been raised.</summary>
    public bool IsRaised(string name) => _signals.TryGetValue(name, out var signal) && signal.Task.IsCompleted;

    /// <summary>Waits until <paramref name="name"/> is raised.</summary>
    public Task WaitAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Get(name).Task.WaitAsync(cancellationToken);
    }
}
