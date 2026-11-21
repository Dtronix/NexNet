using System.Text.Json;
using System.Text.Json.Nodes;
using NexNet.Interop.Remote;

namespace NexNet.Interop.Hosts;

/// <summary>A language host could not be launched because it was not built (no <c>host.json</c>, no IR).</summary>
public sealed class HostNotBuiltException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HostNotBuiltException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// How to launch a language host: <c>artifacts/interop/&lt;lang&gt;/host.json</c>, written by the J12/K12 builds
/// (control protocol §10).
/// </summary>
public sealed record HostLaunchConfig
{
    /// <summary>The default of <see cref="MaxConcurrency"/>.</summary>
    public const int DefaultMaxConcurrency = 4;

    /// <summary>The executable.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments before <c>--control … --token …</c>.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The absolute working directory.</summary>
    public required string WorkingDir { get; init; }

    /// <summary>Extra environment variables.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>How many tests run at once on this language.</summary>
    public int MaxConcurrency { get; init; } = DefaultMaxConcurrency;

    /// <summary>How long the runner waits for the hello.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The environment variable that overrides the repository root.</summary>
    public const string RepoRootVariable = "NEXNET_REPO_ROOT";

    /// <summary>
    /// The repository root: <c>NEXNET_REPO_ROOT</c>, or the first directory above <paramref name="start"/> (default: the
    /// test assembly's directory) that holds <c>src/NexNet.slnx</c>; null when there is none.
    /// </summary>
    public static string? FindRepoRoot(string? start = null)
    {
        if (Environment.GetEnvironmentVariable(RepoRootVariable) is { Length: > 0 } fromEnvironment)
            return Path.GetFullPath(fromEnvironment);

        for (var directory = new DirectoryInfo(start ?? AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "NexNet.slnx")))
                return directory.FullName;
        }

        return null;
    }

    /// <summary>The <c>host.json</c> paths tried for <paramref name="language"/>, in order.</summary>
    public static IReadOnlyList<string> CandidatePaths(string language, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(repoRoot);
        var interop = Path.Combine(repoRoot, "artifacts", "interop");
        var paths = new List<string> { Path.Combine(interop, language, "host.json") };
        if (language.StartsWith("browser-", StringComparison.Ordinal))
            paths.Add(Path.Combine(interop, "browser", "host.json"));
        return paths;
    }

    /// <summary>Loads the launch configuration of <paramref name="language"/>.</summary>
    /// <exception cref="HostNotBuiltException">No <c>host.json</c>: the message names the build command.</exception>
    public static HostLaunchConfig Load(string language, string? repoRoot = null)
    {
        repoRoot ??= FindRepoRoot() ?? throw new HostNotBuiltException(
            $"The repository root (holding src/NexNet.slnx) was not found above {AppContext.BaseDirectory}; set {RepoRootVariable}.");
        var paths = CandidatePaths(language, repoRoot);
        var path = paths.FirstOrDefault(File.Exists);
        if (path == null)
        {
            throw new HostNotBuiltException(
                $"The {language} language host is not built ({string.Join(" or ", paths)} is missing). Build it: {HostBuildCommands.For(language)}");
        }

        return Parse(File.ReadAllText(path), repoRoot, path);
    }

    /// <summary>Parses a <c>host.json</c>.</summary>
    /// <param name="json">The file content.</param>
    /// <param name="repoRoot">The repository root, against which <c>workingDir</c> resolves.</param>
    /// <param name="path">The file path, for messages.</param>
    /// <exception cref="FormatException">The file is malformed.</exception>
    public static HostLaunchConfig Parse(string json, string repoRoot, string path = "host.json")
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(repoRoot);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException(path + " does not hold a JSON object.");
        }
        catch (JsonException e)
        {
            throw new FormatException(path + " is not valid JSON: " + e.Message, e);
        }

        try
        {
            var command = root["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(command))
                throw new FormatException(path + " has no 'command'.");

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root["env"] is JsonObject envObject)
            {
                foreach (var (name, value) in envObject)
                    env[name] = value?.GetValue<string>() ?? "";
            }

            return new HostLaunchConfig
            {
                Command = command,
                Args = root["args"] is JsonArray args ? args.Select(a => a?.GetValue<string>() ?? "").ToArray() : [],
                WorkingDir = Path.GetFullPath(Path.Combine(repoRoot, root["workingDir"]?.GetValue<string>() ?? ".")),
                Env = env,
                MaxConcurrency = Math.Max(1, root["maxConcurrency"]?.GetValue<int>() ?? DefaultMaxConcurrency),
                StartupTimeout = TimeSpan.FromMilliseconds(Math.Max(1, root["startupTimeoutMs"]?.GetValue<int>() ?? 60_000)),
            };
        }
        catch (Exception e) when (e is InvalidOperationException)
        {
            throw new FormatException(path + " is malformed: " + e.Message, e);
        }
    }

    /// <summary>The full argument list of a launch: <see cref="Args"/>, then <c>--control</c>, <c>--token</c> and, for browsers, <c>--browser</c>.</summary>
    public IReadOnlyList<string> BuildArguments(string language, string control, string token)
    {
        var arguments = new List<string>(Args) { "--control", control, "--token", token };
        if (language.StartsWith("browser-", StringComparison.Ordinal))
        {
            arguments.Add("--browser");
            arguments.Add(language["browser-".Length..]);
        }

        return arguments;
    }
}
