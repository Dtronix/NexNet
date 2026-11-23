using System.Diagnostics;
using NexNet.Inspector.App.Checks;

namespace NexNet.Inspector.App.Tests;

/// <summary><c>--headless --self-check</c>, in process on the test session and as the real executable.</summary>
[NotInParallel("avalonia")]
public sealed class SelfCheckTests
{
    private static string Executable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "nexnet-inspector.exe" : "nexnet-inspector");

    [Test]
    public async Task Every_step_passes_in_the_test_session()
    {
        SelfCheckResult[] results = await HeadlessSession.Run(() => SelfCheck.Run(HeadlessSession.App));

        await Assert.That(results.Select(r => r.Step)).IsEquivalentTo(Enum.GetValues<SelfCheckStep>());
        foreach (SelfCheckResult r in results) await Assert.That(r.Passed).IsTrue().Because($"{r.Step}: {r.Detail}");
    }

    [Test]
    public async Task The_report_names_each_step_and_fails_on_any_failure()
    {
        StringWriter passed = new(), failed = new();
        int ok = SelfCheck.Report([new(SelfCheckStep.Render, true, "drawn"), new(SelfCheckStep.Fonts, true, "fine")], passed);
        int bad = SelfCheck.Report([new(SelfCheckStep.Render, true, "drawn"), new(SelfCheckStep.Editor, false, "no template")], failed);
        int none = SelfCheck.Report([], new StringWriter());

        await Assert.That(ok).IsEqualTo(0);
        await Assert.That(passed.ToString()).Contains("self-check: PASS");
        await Assert.That(bad).IsEqualTo(1);
        await Assert.That(failed.ToString()).Contains("editor      FAIL no template");
        await Assert.That(failed.ToString()).Contains("self-check: FAIL");
        await Assert.That(none).IsEqualTo(1);
    }

    [Test]
    public async Task The_executable_passes_its_headless_self_check()
    {
        (int exit, string output) = await RunAsync("--headless", "--self-check");

        await Assert.That(exit).IsEqualTo(0).Because(output);
        await Assert.That(output).Contains("self-check: PASS");
    }

    [Test]
    public async Task The_executable_refuses_bad_arguments_without_opening_a_window()
    {
        (int exit, string output) = await RunAsync("--self-check");

        await Assert.That(exit).IsEqualTo(64);
        await Assert.That(output).Contains("--self-check needs --headless");
    }

    private static async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        ProcessStartInfo start = new(Executable, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("the app did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("the self-check did not end within 2 minutes");
        }

        return (process.ExitCode, await stdout + await stderr);
    }
}
