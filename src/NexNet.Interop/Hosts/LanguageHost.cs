using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NexNet.Interop.Remote;

namespace NexNet.Interop.Hosts;

/// <summary>Supervision settings of a <see cref="LanguageHost"/>.</summary>
public sealed record LanguageHostOptions
{
    /// <summary>How often the runner pings the host.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A ping without a response for this long is a crash.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The expected schema hash; null skips the check.</summary>
    public string? ExpectedSchemaHash { get; init; }

    /// <summary>The command that rebuilds the host, for messages; null uses <see cref="HostBuildCommands.For"/>.</summary>
    public string? RebuildCommand { get; init; }

    /// <summary>Restarts the host after a crash.</summary>
    public bool RestartOnCrash { get; init; } = true;

    /// <summary>Registers the host's transports and capabilities with <see cref="ClientLanguages"/> after each hello.</summary>
    public bool RegisterLanguage { get; init; } = true;

    /// <summary>Control messages kept per test for the transcript.</summary>
    public int TranscriptCapacity { get; init; } = 500;
}

/// <summary>What a crash left behind.</summary>
/// <param name="Generation">The launch that crashed (1 for the first).</param>
/// <param name="Reason">Why the runner declared the crash.</param>
/// <param name="ThreadDump">The thread dump, if one could be captured.</param>
/// <param name="StderrTail">The last lines of stderr.</param>
/// <param name="Exception">The exception in-flight requests failed with.</param>
public sealed record HostCrash(int Generation, string Reason, string? ThreadDump, string? StderrTail, HostCrashedException Exception);

/// <summary>
/// One language host process and its control connection (control protocol §9): launches it, checks its hello, runs the
/// warm-up self-test, pings it every second, and on a crash (no pong for 5 s, or the process ends) captures a thread
/// dump and the stderr tail, fails the requests in flight with <see cref="HostCrashedException"/>, kills the process
/// tree and launches a new host.
/// </summary>
public sealed class LanguageHost : IAsyncDisposable
{
    private readonly HostLaunchConfig _config;
    private readonly IHostLauncher _launcher;
    private readonly ControlListener _listener = ControlListener.Start();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<HostCrash> _crashes = new();
    private TaskCompletionSource<ControlConnection> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHostProcess? _process;
    private ControlConnection? _connection;
    private int _generation;
    private int _disposed;

    /// <summary>Creates a host; <see cref="StartAsync"/> launches it.</summary>
    public LanguageHost(string language, HostLaunchConfig config, IHostLauncher? launcher = null, LanguageHostOptions? options = null)
    {
        Language = language ?? throw new ArgumentNullException(nameof(language));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _launcher = launcher ?? ProcessHostLauncher.Instance;
        Options = options ?? new LanguageHostOptions();
        DriverFactory = new RemoteClientDriverFactory(language, GetConnectionAsync);
    }

    /// <summary>The language.</summary>
    public string Language { get; }

    /// <summary>The supervision settings.</summary>
    public LanguageHostOptions Options { get; }

    /// <summary>The driver factory to register with <see cref="Harness.RegisterDriver"/>.</summary>
    public RemoteClientDriverFactory DriverFactory { get; }

    /// <summary>How many times the host was launched.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>The hello of the current host, once it connected.</summary>
    public HostHello? Hello => Volatile.Read(ref _connection)?.Hello;

    /// <summary>The crashes so far.</summary>
    public IReadOnlyList<HostCrash> Crashes
    {
        get
        {
            lock (_gate)
                return _crashes.ToArray();
        }
    }

    /// <summary>Raised after each crash, before the restart.</summary>
    public event Action<HostCrash>? Crashed;

    /// <summary>Launches the host and waits until it passed its hello and warm-up.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _ = LaunchAsync();
        await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The control connection of the current host; waits for a launch or restart in progress.</summary>
    /// <exception cref="HostNotBuiltException">The host is not built.</exception>
    /// <exception cref="ControlProtocolException">The host's hello was refused (wrong schema hash, token, protocol).</exception>
    public async Task<ControlConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task<ControlConnection> ready;
            lock (_gate)
                ready = _ready.Task;
            var connection = await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!connection.IsClosed)
                return connection;

            // Crashed after it was ready; the restart replaces _ready.
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(LanguageHost));
            lock (_gate)
            {
                if (ReferenceEquals(ready, _ready.Task) && !Options.RestartOnCrash)
                    throw connection.Failure ?? new HostCrashedException($"The {Language} host is gone.");
            }
        }
    }

    /// <summary>Stops the host: <c>host.shutdown</c>, then kills its process tree.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        var connection = Volatile.Read(ref _connection);
        if (connection is { IsClosed: false })
        {
            try
            {
                await connection.RequestAsync(null, "host.shutdown", deadline: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Killed below.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_process != null)
        {
            try
            {
                await _process.Exited.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Killed by DisposeAsync.
            }

            await _process.DisposeAsync().ConfigureAwait(false);
        }

        _listener.Dispose();
        lock (_gate)
            _ready.TrySetException(new ObjectDisposedException(nameof(LanguageHost)));
        _lifetime.Dispose();
    }

    private async Task LaunchAsync()
    {
        TaskCompletionSource<ControlConnection> ready;
        lock (_gate)
            ready = _ready;

        var generation = Interlocked.Increment(ref _generation);
        IHostProcess? process = null;
        try
        {
            var token = ControlListener.CreateToken();
            var transcript = new ControlTranscript(Options.TranscriptCapacity);
            process = _launcher.Launch(Language, _config, _config.BuildArguments(Language, _listener.ControlArgument, token));
            _process = process;

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            startup.CancelAfter(_config.StartupTimeout);
            var accept = _listener.AcceptAsync(token, Language, Options.ExpectedSchemaHash, Options.RebuildCommand, transcript, startup.Token);
            var first = await Task.WhenAny(accept, process.Exited).ConfigureAwait(false);
            if (first != accept)
            {
                // A host whose hello is refused (schema hash, token, protocol) exits at once, and its exit can be
                // observed before the accept reports the refusal; the refusal is the more useful error.
                await Task.WhenAny(accept, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                if (accept.IsFaulted && accept.Exception?.InnerException is ControlProtocolException { HelloRefused: true } refusal)
                    ExceptionDispatchInfo.Throw(refusal);

                await startup.CancelAsync().ConfigureAwait(false);
                if (accept.IsCompletedSuccessfully)
                    await accept.Result.DisposeAsync().ConfigureAwait(false);
                throw new HostCrashedException(
                    $"The {Language} host exited (code {process.ExitCode}) before it connected.",
                    stderrTail: process.StderrTail());
            }

            ControlConnection connection;
            try
            {
                connection = await accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
                throw new HostCrashedException(
                    $"The {Language} host did not connect within {_config.StartupTimeout.TotalSeconds:F0} s.",
                    stderrTail: process.StderrTail());
            }

            await WarmUpAsync(connection).ConfigureAwait(false);
            Volatile.Write(ref _connection, connection);
            if (Options.RegisterLanguage)
                ClientLanguages.Register(connection.Hello.ToLanguageInfo(Language));
            _ = SuperviseAsync(generation, connection, process);
            ready.TrySetResult(connection);
        }
        catch (Exception e)
        {
            if (process != null)
                await process.DisposeAsync().ConfigureAwait(false);
            ready.TrySetException(e);
        }
    }

    private static async Task WarmUpAsync(ControlConnection connection)
    {
        const string warmup = "__warmup";
        await connection.RequestAsync(null, "ping", deadline: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await connection.BeginTestAsync(warmup).ConfigureAwait(false);
        var report = await connection.EndTestAsync(warmup).ConfigureAwait(false);
        connection.Transcript.Remove(warmup);
        if (report is { IsClean: false })
            throw new ControlProtocolException($"The {connection.Hello.Lang} host failed its warm-up: test.end of an empty scope reported {report}.");
    }

    private async Task SuperviseAsync(int generation, ControlConnection connection, IHostProcess process)
    {
        string reason;
        try
        {
            while (true)
            {
                var tick = Task.Delay(Options.PingInterval, _lifetime.Token);
                var ended = await Task.WhenAny(tick, process.Exited, connection.Completion).ConfigureAwait(false);
                if (_lifetime.IsCancellationRequested)
                    return;
                if (ended == process.Exited)
                {
                    reason = $"the process exited (code {process.ExitCode})";
                    break;
                }

                if (ended == connection.Completion)
                {
                    reason = await PreferExitAsync(process, "the control connection closed: " + connection.Failure?.Message).ConfigureAwait(false);
                    break;
                }

                try
                {
                    await connection.RequestAsync(null, "ping", deadline: Options.PingTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    reason = $"no response to ping within {Options.PingTimeout.TotalSeconds:F1} s";
                    break;
                }
                catch (Exception) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    // The ping can be the first to see the closed connection of a process that is dying.
                    reason = await PreferExitAsync(process, "ping failed: " + e.Message).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await HandleCrashAsync(generation, connection, process, reason).ConfigureAwait(false);
    }

    /// <summary>
    /// A process that dies closes its connection before its exit is observed; when the exit follows within a moment it
    /// is the reason, otherwise <paramref name="otherwise"/> is.
    /// </summary>
    private static async Task<string> PreferExitAsync(IHostProcess process, string otherwise)
    {
        await Task.WhenAny(process.Exited, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
        return process.Exited.IsCompleted ? $"the process exited (code {process.ExitCode})" : otherwise;
    }

    private async Task HandleCrashAsync(int generation, ControlConnection connection, IHostProcess process, string reason)
    {
        if (_lifetime.IsCancellationRequested)
            return;

        lock (_gate)
            _ready = new TaskCompletionSource<ControlConnection>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dump = await CaptureThreadDumpAsync(connection, process).ConfigureAwait(false);
        var stderr = process.StderrTail();
        var exception = new HostCrashedException($"The {Language} host crashed: {reason}.", dump, stderr);
        connection.Fail(exception);
        process.KillTree();
        await process.DisposeAsync().ConfigureAwait(false);

        var crash = new HostCrash(generation, reason, dump, stderr, exception);
        lock (_gate)
            _crashes.Add(crash);
        try
        {
            Crashed?.Invoke(crash);
        }
        catch (Exception)
        {
            // A diagnostic listener must not stop the restart.
        }

        if (Options.RestartOnCrash && !_lifetime.IsCancellationRequested)
        {
            await LaunchAsync().ConfigureAwait(false);
        }
        else
        {
            lock (_gate)
                _ready.TrySetException(exception);
        }
    }

    private async Task<string?> CaptureThreadDumpAsync(ControlConnection connection, IHostProcess process)
    {
        var parts = new List<string>();
        if (!connection.IsClosed)
        {
            try
            {
                var dump = await connection.RequestAsync(null, "host.dumpThreads", deadline: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (dump["text"]?.GetValue<string>() is { Length: > 0 } text)
                    parts.Add(text);
            }
            catch (Exception)
            {
                // The host does not answer; that is why it is declared crashed.
            }
        }

        if (Language == ClientLanguages.Java && process.Id is { } pid && !process.Exited.IsCompleted)
        {
            var jcmd = await RunJcmdAsync(pid).ConfigureAwait(false);
            if (jcmd != null)
                parts.Add(jcmd);
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static async Task<string?> RunJcmdAsync(int pid)
    {
        try
        {
            var info = new ProcessStartInfo("jcmd")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("Thread.print");
            using var jcmd = Process.Start(info);
            if (jcmd == null)
                return null;
            var output = jcmd.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await jcmd.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // jcmd is optional (a JRE has none).
            return null;
        }
    }
}
