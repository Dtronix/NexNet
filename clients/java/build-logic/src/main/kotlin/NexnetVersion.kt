import org.gradle.api.GradleException
import org.gradle.api.Project

/**
 * The NexNet version, read from the C# library's project file so the NuGet packages and the Java and JavaScript clients
 * stay in lockstep (D19). The clients are versioned but never published (D30); .github/scripts/release-guard.ps1
 * fails the release when the versions differ.
 * The Java clients live in clients/java; the C# project is ../../src/NexNet/NexNet.csproj from there.
 */
fun Project.nexnetVersion(): String {
    val csproj = rootProject.layout.projectDirectory.file("../../src/NexNet/NexNet.csproj")
    val text = providers.fileContents(csproj).asText.orNull
        ?: throw GradleException("Cannot read the NexNet version: ${csproj.asFile} not found.")
    return Regex("<Version>\\s*([^<\\s]+)\\s*</Version>").find(text)?.groupValues?.get(1)
        ?: throw GradleException("No <Version> element in ${csproj.asFile}.")
}
