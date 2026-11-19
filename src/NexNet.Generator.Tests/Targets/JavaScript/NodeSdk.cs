using System.Diagnostics;
using System.Text;

namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>The result of one external process.</summary>
internal sealed record NodeProcessResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Node and the TypeScript compiler for the SDK-gated JavaScript checks (D27). They run only when
/// <c>NEXNET_REQUIRE_SDKS=1</c> or <c>NEXNET_TEST_LANGS</c> lists <c>node</c> (or <c>js</c>/<c>javascript</c>);
/// otherwise the tests are skipped with the reason. Under <c>NEXNET_REQUIRE_SDKS=1</c> a missing tool fails the test.
/// </summary>
internal sealed class NodeSdk
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    private NodeSdk(string node, string tsc)
    {
        Node = node;
        Tsc = tsc;
    }

    public string Node { get; }

    /// <summary>The TypeScript compiler script of the npm workspace (<c>clients/js/node_modules/typescript/bin/tsc</c>).</summary>
    public string Tsc { get; }

    public static bool Required => Environment.GetEnvironmentVariable("NEXNET_REQUIRE_SDKS") == "1";

    private static bool Enabled
        => Required || (Environment.GetEnvironmentVariable("NEXNET_TEST_LANGS") ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(l => l.Trim().ToLowerInvariant() is "node" or "js" or "javascript" or "browser");

    public static string JsRoot => Path.Combine(JsTestRunner.RepoRoot(), "clients", "js");

    /// <summary>The runtime package (<c>@dtronix/nexnet</c>) the generated code imports.</summary>
    public static string RuntimePackage => Path.Combine(JsRoot, "packages", "nexnet");

    /// <summary>The tools, or a skip (a failure under <c>NEXNET_REQUIRE_SDKS=1</c>) with the reason.</summary>
    public static async Task<NodeSdk> Get()
    {
        if (!Enabled)
            Skip.Test("JavaScript SDK checks run only with NEXNET_REQUIRE_SDKS=1 or NEXNET_TEST_LANGS containing node (required in CI).");

        var node = FindTool("node");
        if (node == null)
            Unavailable("node not found on PATH; install Node 22 or later (set NEXNET_REQUIRE_SDKS=1 in CI).");

        var tsc = await TscPath();
        if (tsc == null)
            Unavailable("the TypeScript compiler is not installed and npm ci --prefix clients/js failed.");

        return new NodeSdk(node!, tsc!);
    }

    private static void Unavailable(string reason)
    {
        if (Required)
            throw new InvalidOperationException("NEXNET_REQUIRE_SDKS=1: " + reason);
        Skip.Test(reason);
    }

    private static string? FindTool(string name)
    {
        var files = OperatingSystem.IsWindows() ? new[] { name + ".exe", name + ".cmd" } : new[] { name };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            foreach (var file in files)
            {
                var candidate = Path.Combine(dir.Trim('"'), file);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string TscScript => Path.Combine(JsRoot, "node_modules", "typescript", "bin", "tsc");

    /// <summary>The TypeScript compiler, installed once with <c>npm ci</c> when the workspace has no node_modules.</summary>
    private static async Task<string?> TscPath()
    {
        await InstallLock.WaitAsync();
        try
        {
            if (File.Exists(TscScript))
                return TscScript;

            var npm = FindTool("npm");
            if (npm == null)
                return null;
            var result = OperatingSystem.IsWindows()
                ? await Run("cmd.exe", new[] { "/c", npm, "ci", "--prefix", JsRoot }, JsRoot, TimeSpan.FromMinutes(10))
                : await Run(npm, new[] { "ci", "--prefix", JsRoot }, JsRoot, TimeSpan.FromMinutes(10));
            return result.Success && File.Exists(TscScript) ? TscScript : null;
        }
        finally
        {
            InstallLock.Release();
        }
    }

    /// <summary>Runs <c>tsc -p &lt;project&gt;</c>.</summary>
    public Task<NodeProcessResult> TypeCheck(string project)
        => Run(Node, new[] { Tsc, "-p", project, "--pretty", "false" }, Path.GetDirectoryName(project)!, TimeSpan.FromMinutes(5));

    /// <summary>Runs a script with Node in its directory.</summary>
    public Task<NodeProcessResult> RunScript(string script)
        => Run(Node, new[] { script }, Path.GetDirectoryName(script)!, TimeSpan.FromMinutes(5));

    /// <summary>
    /// Makes <c>&lt;dir&gt;/node_modules/@dtronix/nexnet</c> point at the runtime package: a junction on Windows (no
    /// privilege needed), a symbolic link elsewhere.
    /// </summary>
    public static async Task LinkRuntime(string dir)
    {
        var scope = Path.Combine(dir, "node_modules", "@dtronix");
        Directory.CreateDirectory(scope);
        var link = Path.Combine(scope, "nexnet");
        if (OperatingSystem.IsWindows())
        {
            var result = await Run("cmd.exe", new[] { "/c", "mklink", "/J", link, RuntimePackage }, dir, TimeSpan.FromMinutes(1));
            if (!result.Success)
                throw new InvalidOperationException("mklink /J failed: " + result.Output);
        }
        else
        {
            Directory.CreateSymbolicLink(link, RuntimePackage);
        }
    }

    /// <summary>Removes the runtime link first, so deleting the directory never follows it.</summary>
    public static void UnlinkRuntime(string dir)
    {
        var link = Path.Combine(dir, "node_modules", "@dtronix", "nexnet");
        try
        {
            if (Directory.Exists(link))
                Directory.Delete(link, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<NodeProcessResult> Run(string file, IEnumerable<string> args, string workingDirectory, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new NodeProcessResult(-1, output + "\n(timed out after " + timeout + ")");
        }

        process.WaitForExit();
        lock (output)
            return new NodeProcessResult(process.ExitCode, output.ToString());
    }
}
