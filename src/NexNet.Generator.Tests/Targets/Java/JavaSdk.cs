using System.Diagnostics;
using System.Text;

namespace NexNet.Generator.Tests.Targets.Java;

/// <summary>The result of one external process.</summary>
internal sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// The JDK tools of the SDK-gated Java checks (D27). They run only when <c>NEXNET_REQUIRE_SDKS=1</c> or
/// <c>NEXNET_TEST_LANGS</c> lists <c>java</c>; otherwise the tests are skipped with the reason. Under
/// <c>NEXNET_REQUIRE_SDKS=1</c> a missing tool fails the test.
/// </summary>
internal sealed class JavaSdk
{
    private static readonly SemaphoreSlim JarLock = new(1, 1);

    private JavaSdk(string javac, string java, string runtimeJar, string? jspecifyJar)
    {
        Javac = javac;
        Java = java;
        RuntimeJar = runtimeJar;
        JSpecifyJar = jspecifyJar;
    }

    public string Javac { get; }

    public string Java { get; }

    /// <summary>The <c>nexnet-client</c> jar (K01/K02 runtime) the generated code compiles against.</summary>
    public string RuntimeJar { get; }

    /// <summary>The JSpecify jar from the Gradle cache, or null (a stub annotation is compiled instead).</summary>
    public string? JSpecifyJar { get; }

    public static bool Required => Environment.GetEnvironmentVariable("NEXNET_REQUIRE_SDKS") == "1";

    private static bool Enabled
        => Required || (Environment.GetEnvironmentVariable("NEXNET_TEST_LANGS") ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(l => string.Equals(l.Trim(), "java", StringComparison.OrdinalIgnoreCase));

    /// <summary>The tools, or a skip (a failure under <c>NEXNET_REQUIRE_SDKS=1</c>) with the reason.</summary>
    public static async Task<JavaSdk> Get()
    {
        if (!Enabled)
            Skip.Test("Java SDK checks run only with NEXNET_REQUIRE_SDKS=1 or NEXNET_TEST_LANGS containing java (required in CI).");

        var javac = FindTool("javac");
        var java = FindTool("java");
        if (javac == null || java == null)
            Unavailable("javac/java not found on JAVA_HOME or PATH; install JDK 21 (set NEXNET_REQUIRE_SDKS=1 in CI).");

        var jar = await RuntimeJarPath();
        if (jar == null)
            Unavailable("the nexnet-client jar could not be built (clients/java: gradlew :nexnet-client:jar).");

        return new JavaSdk(javac!, java!, jar!, FindJSpecify());
    }

    /// <summary>
    /// Only <c>javac</c> and <c>java</c>, for checks that must not see the nexnet-client jar (the emitted runtime, D31).
    /// Skips (or fails) like <see cref="Get"/>.
    /// </summary>
    public static (string Javac, string Java) Tools()
    {
        if (!Enabled)
            Skip.Test("Java SDK checks run only with NEXNET_REQUIRE_SDKS=1 or NEXNET_TEST_LANGS containing java (required in CI).");

        var javac = FindTool("javac");
        var java = FindTool("java");
        if (javac == null || java == null)
            Unavailable("javac/java not found on JAVA_HOME or PATH; install JDK 21 (set NEXNET_REQUIRE_SDKS=1 in CI).");
        return (javac!, java!);
    }

    /// <summary>Runs a process and collects its output (stdout and stderr), killing it after <paramref name="timeout"/>.</summary>
    internal static Task<ProcessResult> RunProcess(string file, IEnumerable<string> args, string workingDirectory, TimeSpan timeout)
        => Run(file, args, workingDirectory, timeout);

    private static void Unavailable(string reason)
    {
        if (Required)
            throw new InvalidOperationException("NEXNET_REQUIRE_SDKS=1: " + reason);
        Skip.Test(reason);
    }

    private static string? FindTool(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome) && System.IO.File.Exists(Path.Combine(javaHome, "bin", file)))
            return Path.Combine(javaHome, "bin", file);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            var candidate = Path.Combine(dir.Trim('"'), file);
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string JavaRoot => Path.Combine(JavaTestRunner.RepoRoot(), "clients", "java");

    private static string? ExistingJar()
    {
        var libs = Path.Combine(JavaRoot, "nexnet-client", "build", "libs");
        return Directory.Exists(libs)
            ? Directory.GetFiles(libs, "nexnet-client-*.jar")
                .Where(f => !f.EndsWith("-sources.jar", StringComparison.Ordinal) && !f.EndsWith("-javadoc.jar", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .LastOrDefault()
            : null;
    }

    /// <summary>The runtime jar, built once with the Gradle wrapper when it is missing.</summary>
    private static async Task<string?> RuntimeJarPath()
    {
        await JarLock.WaitAsync();
        try
        {
            var jar = ExistingJar();
            if (jar != null)
                return jar;

            var result = OperatingSystem.IsWindows()
                ? await Run("cmd.exe", new[] { "/c", Path.Combine(JavaRoot, "gradlew.bat"), "-p", JavaRoot, ":nexnet-client:jar", "--console=plain" }, JavaRoot, TimeSpan.FromMinutes(10))
                : await Run("sh", new[] { Path.Combine(JavaRoot, "gradlew"), "-p", JavaRoot, ":nexnet-client:jar", "--console=plain" }, JavaRoot, TimeSpan.FromMinutes(10));
            return result.Success ? ExistingJar() : null;
        }
        finally
        {
            JarLock.Release();
        }
    }

    private static string? FindJSpecify()
    {
        var home = Environment.GetEnvironmentVariable("GRADLE_USER_HOME");
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle");
        var dir = Path.Combine(home, "caches", "modules-2", "files-2.1", "org.jspecify", "jspecify");
        if (!Directory.Exists(dir))
            return null;
        return Directory.GetFiles(dir, "jspecify-*.jar", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-sources.jar", StringComparison.Ordinal) && !f.EndsWith("-javadoc.jar", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .LastOrDefault();
    }

    /// <summary>
    /// Compiles the sources (paths relative to <paramref name="sourceDir"/>) into <paramref name="classesDir"/> with
    /// <c>--release 11 -Xlint:all -Werror</c> and doclint, against the runtime jar and JSpecify.
    /// </summary>
    public async Task<ProcessResult> Compile(string sourceDir, IEnumerable<string> sources, string classesDir)
    {
        var all = sources.Select(s => Path.Combine(sourceDir, s)).ToList();
        if (JSpecifyJar == null)
        {
            // Same declaration as JSpecify's: a type-use annotation, so placement errors still fail.
            var stub = Path.Combine(sourceDir, "org", "jspecify", "annotations", "Nullable.java");
            Directory.CreateDirectory(Path.GetDirectoryName(stub)!);
            await System.IO.File.WriteAllTextAsync(stub, """
                package org.jspecify.annotations;

                /** Stub of the JSpecify annotation. */
                @java.lang.annotation.Documented
                @java.lang.annotation.Retention(java.lang.annotation.RetentionPolicy.RUNTIME)
                @java.lang.annotation.Target(java.lang.annotation.ElementType.TYPE_USE)
                public @interface Nullable {
                }
                """);
            all.Add(stub);
        }

        Directory.CreateDirectory(classesDir);
        var classPath = string.Join(Path.PathSeparator.ToString(), new[] { RuntimeJar, JSpecifyJar }.Where(p => p != null));
        var args = new List<string>
        {
            "--release", "11", "-encoding", "UTF-8", "-Xlint:all", "-Werror", "-Xdoclint:all,-missing",
            "-cp", classPath, "-d", classesDir,
        };
        args.AddRange(all);

        var argFile = Path.Combine(sourceDir, "javac.args");
        await System.IO.File.WriteAllLinesAsync(argFile, args.Select(Quote));
        return await Run(Javac, new[] { "@" + argFile }, sourceDir, TimeSpan.FromMinutes(5));
    }

    /// <summary>Runs a main class with the compiled classes and the runtime jar on the class path.</summary>
    public Task<ProcessResult> RunMain(string classesDir, string mainClass)
    {
        var classPath = string.Join(Path.PathSeparator.ToString(), new[] { classesDir, RuntimeJar });
        return Run(Java, new[] { "-cp", classPath, mainClass }, classesDir, TimeSpan.FromMinutes(5));
    }

    private static string Quote(string arg) => "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static async Task<ProcessResult> Run(string file, IEnumerable<string> args, string workingDirectory, TimeSpan timeout)
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
            return new ProcessResult(-1, output + "\n(timed out after " + timeout + ")");
        }

        process.WaitForExit();
        lock (output)
            return new ProcessResult(process.ExitCode, output.ToString());
    }
}
