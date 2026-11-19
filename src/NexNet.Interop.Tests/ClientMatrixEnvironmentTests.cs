using System.Diagnostics;
using System.Text;

namespace NexNet.Interop.Tests;

/// <summary>
/// <c>NEXNET_TEST_TRANSPORTS</c> as TUnit reports it: a test session of this test app, started with the variable set and
/// filtered to <see cref="EnvironmentTransportsRow"/>, a matrix test whose transports come from the variable.
/// </summary>
public class ClientMatrixEnvironmentTests
{
    private const string ProbeFilter = "/*/*/" + nameof(ClientMatrixEnvironmentTests) + "/" + nameof(EnvironmentTransportsRow);

    /// <summary>The probe: it names no transports, so <c>NEXNET_TEST_TRANSPORTS</c> (or the default) decides its rows.</summary>
    [Test]
    [ClientMatrix(Languages = ["csharp"])]
    public async Task EnvironmentTransportsRow(ClientTarget target)
    {
        await Assert.That(target.Language).IsEqualTo(ClientLanguages.CSharp);
    }

    [Test]
    public async Task UnknownTransport_IsReportedAsAFailedTest()
    {
        var (exitCode, output) = await RunProbeAsync("Tcp,Smoke");

        // Exit code 2: at least one test failed. Not 8 (no test ran): the name is not silently dropped.
        await Assert.That(exitCode).IsEqualTo(2).Because(output);
        await Assert.That(output).Contains("unknown transport 'Smoke'");
        await Assert.That(output).Contains(ClientTransports.EnvironmentVariable);
    }

    [Test]
    public async Task KnownTransport_RunsTheProbe()
    {
        var (exitCode, output) = await RunProbeAsync("Tcp");
        await Assert.That(exitCode).IsEqualTo(0).Because(output);
    }

    /// <summary>Runs <see cref="EnvironmentTransportsRow"/> in a new process of this test app.</summary>
    private static async Task<(int ExitCode, string Output)> RunProbeAsync(string transports)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var info = new ProcessStartInfo(processPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Under "dotnet exec" the test app is the assembly; otherwise it is its apphost.
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("exec");
            info.ArgumentList.Add(typeof(ClientMatrixEnvironmentTests).Assembly.Location);
        }

        var results = Directory.CreateTempSubdirectory("nexnet-matrix-env-");
        foreach (var argument in new[] { "--treenode-filter", ProbeFilter, "--no-ansi", "--no-progress", "--results-directory", results.FullName })
            info.ArgumentList.Add(argument);

        // The child is a plain test session: none of the parent's test-platform settings, only the variable under test.
        foreach (var name in info.Environment.Keys.ToArray())
        {
            if (name.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DOTNET_TEST", StringComparison.OrdinalIgnoreCase))
                info.Environment.Remove(name);
        }

        info.Environment.Remove(ClientLanguages.EnvironmentVariable);
        info.Environment[ClientTransports.EnvironmentVariable] = transports;

        using var process = Process.Start(info) ?? throw new InvalidOperationException("The test app did not start.");
        var output = new StringBuilder();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The probe test session did not end within 2 minutes.");
        }
        finally
        {
            try
            {
                results.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }

        output.Append(await stdout).Append(await stderr);
        return (process.ExitCode, output.ToString());
    }
}
