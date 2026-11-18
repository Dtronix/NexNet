import org.gradle.api.GradleException
import org.gradle.api.Project

/**
 * The NexNet version, read from the C# library's project file so NuGet, npm and Maven stay in lockstep (D19).
 * The Java clients live in clients/java; the C# project is ../../src/NexNet/NexNet.csproj from there.
 */
fun Project.nexnetVersion(): String {
    val csproj = rootProject.layout.projectDirectory.file("../../src/NexNet/NexNet.csproj")
    val text = providers.fileContents(csproj).asText.orNull
        ?: throw GradleException("Cannot read the NexNet version: ${csproj.asFile} not found.")
    return Regex("<Version>\\s*([^<\\s]+)\\s*</Version>").find(text)?.groupValues?.get(1)
        ?: throw GradleException("No <Version> element in ${csproj.asFile}.")
}
