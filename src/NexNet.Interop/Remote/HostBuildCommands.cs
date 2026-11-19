namespace NexNet.Interop.Remote;

/// <summary>The commands that build each language host (control protocol §10); J12 and K12 provide them.</summary>
public static class HostBuildCommands
{
    /// <summary>Builds the Node and browser hosts.</summary>
    public const string JavaScript = "npm ci --prefix clients/js && npm run build:interop-host --prefix clients/js";

    /// <summary>Builds the JVM host.</summary>
    public const string Java = "clients/java/gradlew -p clients/java :interop-host:installInteropHost";

    /// <summary>The build command of <paramref name="language"/>.</summary>
    public static string For(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (language == ClientLanguages.Node || language.StartsWith("browser", StringComparison.Ordinal))
            return JavaScript;
        if (language == ClientLanguages.Java)
            return Java;
        return $"(no build command is known for the language '{language}')";
    }
}
