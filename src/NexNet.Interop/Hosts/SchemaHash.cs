using System.Security.Cryptography;

namespace NexNet.Interop.Hosts;

/// <summary>
/// The expected <c>schemaHash</c> of every language host (control protocol §4.6): the SHA-256 of the TestNexus
/// <c>nexnet-ir.json</c>.
/// </summary>
public static class SchemaHash
{
    /// <summary>The environment variable that overrides the IR path.</summary>
    public const string IrPathVariable = "NEXNET_INTEROP_IR";

    /// <summary>The path of the TestNexus IR: <c>NEXNET_INTEROP_IR</c>, or <c>src/NexNet.TestNexus/obj/nexnet-ir.json</c>.</summary>
    public static string IrPath(string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        return Environment.GetEnvironmentVariable(IrPathVariable) is { Length: > 0 } fromEnvironment
            ? Path.GetFullPath(fromEnvironment)
            : Path.Combine(repoRoot, "src", "NexNet.TestNexus", "obj", "nexnet-ir.json");
    }

    /// <summary>Lower-case hex SHA-256 of a file's bytes.</summary>
    public static string OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The expected schema hash.</summary>
    /// <exception cref="HostNotBuiltException">The IR file does not exist.</exception>
    public static string Expected(string repoRoot)
    {
        var path = IrPath(repoRoot);
        if (!File.Exists(path))
            throw new HostNotBuiltException($"The TestNexus IR ({path}) is missing. Build it: dotnet build src/NexNet.TestNexus");
        return OfFile(path);
    }
}
