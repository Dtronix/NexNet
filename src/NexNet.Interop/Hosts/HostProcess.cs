using System.Diagnostics;

namespace NexNet.Interop.Hosts;

/// <summary>A launched language host process.</summary>
public interface IHostProcess : IAsyncDisposable
{
    /// <summary>The process id, if any.</summary>
    int? Id { get; }

    /// <summary>Completes when the process ends.</summary>
    Task Exited { get; }

    /// <summary>The exit code once the process ended.</summary>
    int? ExitCode { get; }

    /// <summary>The last lines the process wrote to stderr.</summary>
    string StderrTail();

    /// <summary>Kills the process and every process it started.</summary>
    void KillTree();
}

/// <summary>Launches language host processes.</summary>
public interface IHostLauncher
{
    /// <summary>Starts a host with <paramref name="arguments"/> (the complete argument list).</summary>
    IHostProcess Launch(string language, HostLaunchConfig config, IReadOnlyList<string> arguments);
}

/// <summary>Launches real processes; stderr and stdout are captured (the last lines of stderr are kept).</summary>
public sealed class ProcessHostLauncher : IHostLauncher
{
    /// <summary>The shared instance.</summary>
    public static ProcessHostLauncher Instance { get; } = new();

    /// <inheritdoc />
    public IHostProcess Launch(string language, HostLaunchConfig config, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(config.Command)
        {
            WorkingDirectory = config.WorkingDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        foreach (var (name, value) in config.Env)
            info.Environment[name] = value;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var host = new HostProcess(process);
        if (!process.Start())
            throw new InvalidOperationException($"The {language} host process ({config.Command}) did not start.");
        host.BeginCapture();
        return host;
    }

    private sealed class HostProcess : IHostProcess
    {
        private const int TailLines = 200;
        private readonly Process _process;
        private readonly Queue<string> _stderr = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HostProcess(Process process)
        {
            _process = process;
            _process.Exited += (_, _) => _exited.TrySetResult();
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;
                lock (_stderr)
                {
                    _stderr.Enqueue(e.Data);
                    while (_stderr.Count > TailLines)
                        _stderr.Dequeue();
                }
            };
            _process.OutputDataReceived += (_, _) => { };
        }

        public int? Id
        {
            get
            {
                try
                {
                    return _process.Id;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public Task Exited => _exited.Task;

        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        public void BeginCapture()
        {
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
            if (_process.HasExited)
                _exited.TrySetResult();
        }

        public string StderrTail()
        {
            lock (_stderr)
                return string.Join(Environment.NewLine, _stderr);
        }

        public void KillTree()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Already exiting.
            }
        }

        public async ValueTask DisposeAsync()
        {
            KillTree();
            try
            {
                await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Reported by the supervisor; nothing more to do.
            }

            _process.Dispose();
        }
    }
}
