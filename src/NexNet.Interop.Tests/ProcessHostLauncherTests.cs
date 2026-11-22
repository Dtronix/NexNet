using NexNet.Interop.Hosts;

namespace NexNet.Interop.Tests;

public class ProcessHostLauncherTests
{
    [Test]
    public async Task KillTree_EndsTheProcessAndKeepsTheStderrTail()
    {
        var windows = OperatingSystem.IsWindows();
        var config = new HostLaunchConfig { Command = windows ? "cmd.exe" : "/bin/sh", WorkingDir = AppContext.BaseDirectory };
        IReadOnlyList<string> arguments = windows
            ? ["/c", "echo boom 1>&2 & ping -n 60 127.0.0.1 >nul"]
            : ["-c", "echo boom 1>&2; sleep 60 & wait"];

        await using var process = ProcessHostLauncher.Instance.Launch("shell", config, arguments);
        await Assert.That(process.Id).IsNotNull();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!process.StderrTail().Contains("boom") && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await Assert.That(process.StderrTail()).Contains("boom");
        await Assert.That(process.Exited.IsCompleted).IsFalse();

        process.KillTree();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(process.ExitCode).IsNotNull();
    }
}
