using System.Collections.Concurrent;
using NexNet.Interop.Hosts;

namespace NexNet.Interop.Tests.Fake;

/// <summary>Launches <see cref="FakeHost"/>s in process, as <see cref="LanguageHost"/> launches host processes.</summary>
internal sealed class FakeHostLauncher(Func<int, FakeHostOptions>? options = null) : IHostLauncher
{
    private int _launches;

    /// <summary>The hosts launched, in order (null while a host is still connecting).</summary>
    public ConcurrentQueue<FakeHostProcess> Launched { get; } = new();

    /// <summary>The arguments of each launch.</summary>
    public ConcurrentQueue<IReadOnlyList<string>> Arguments { get; } = new();

    /// <summary>A launch configuration for the fake launcher (nothing is executed).</summary>
    public static HostLaunchConfig Config(int maxConcurrency = 4)
        => new() { Command = "fake-host", WorkingDir = AppContext.BaseDirectory, MaxConcurrency = maxConcurrency, StartupTimeout = TimeSpan.FromSeconds(20) };

    public IHostProcess Launch(string language, HostLaunchConfig config, IReadOnlyList<string> arguments)
    {
        Arguments.Enqueue(arguments);
        var generation = Interlocked.Increment(ref _launches);
        var control = arguments[IndexOf(arguments, "--control") + 1];
        var token = arguments[IndexOf(arguments, "--token") + 1];
        var hostOptions = options?.Invoke(generation) ?? new FakeHostOptions { Language = language };
        var process = new FakeHostProcess(FakeHost.ConnectAsync(control, token, hostOptions));
        Launched.Enqueue(process);
        return process;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return i;
        }

        throw new ArgumentException("No " + value + " argument.");
    }
}

/// <summary>The "process" of a fake host.</summary>
internal sealed class FakeHostProcess : IHostProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeHostProcess(Task<FakeHost> host)
    {
        Host = host;
        host.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Exited.ContinueWith(e => _exited.TrySetResult(e.Result), TaskScheduler.Default);
                else
                    _exited.TrySetResult(-2);
            },
            TaskScheduler.Default);
    }

    public Task<FakeHost> Host { get; }

    public int? Id => null;

    public Task Exited => _exited.Task;

    public int? ExitCode => _exited.Task.IsCompleted ? _exited.Task.Result : null;

    public bool Killed { get; private set; }

    public string StderrTail() => "fake stderr line";

    public void KillTree()
    {
        Killed = true;
        if (Host.IsCompletedSuccessfully)
            Host.Result.Crash(-9);
        _exited.TrySetResult(-9);
    }

    public ValueTask DisposeAsync()
    {
        KillTree();
        return default;
    }
}
