import org.gradle.api.GradleException
import org.gradle.api.Project
import org.gradle.api.artifacts.repositories.ArtifactRepository
import org.gradle.api.artifacts.repositories.UrlArtifactRepository
import org.gradle.api.publish.PublishingExtension

/**
 * The Java clients are built and tested but never published (D30): NuGet is the only release channel, and
 * NexNet.Generator emits the runtime sources into the consumer's build (D31). This guard fails the configuration of a
 * project that could publish anywhere else:
 *
 * - a signing or Maven Central / Nexus publishing plugin is applied;
 * - a publishing repository is not a local directory inside the project's build directory (the K28 consumer-test
 *   repository is the only one today).
 *
 * .github/scripts/release-guard.ps1 checks the same rules textually over every build script, including the Android
 * module, which does not apply this convention.
 */
internal val forbiddenPublishingPlugins = listOf(
    "signing",
    "com.vanniktech.maven.publish",
    "com.vanniktech.maven.publish.base",
    "io.github.gradle-nexus.publish-plugin",
    "org.jreleaser",
    "com.gradleup.nmcp",
    "com.gradleup.nmcp.aggregation",
    "tech.yanand.maven-central-publish",
    "com.gradle.plugin-publish",
)

fun Project.forbidRemotePublishing() {
    for (id in forbiddenPublishingPlugins) {
        pluginManager.withPlugin(id) {
            throw GradleException(
                "Plugin '$id' is applied to $path, but the NexNet Java clients are never published (D30). " +
                    "NuGet is the only release channel; see llm-release.md."
            )
        }
    }
    // Repositories are configured after the plugin is applied, so check them once the build script has run.
    afterEvaluate {
        val publishing = extensions.findByType(PublishingExtension::class.java) ?: return@afterEvaluate
        val buildDir = layout.buildDirectory.get().asFile.canonicalFile
        for (repository in publishing.repositories) {
            checkLocalRepository(repository, buildDir)
        }
    }
}

private fun Project.checkLocalRepository(repository: ArtifactRepository, buildDir: java.io.File) {
    val url = (repository as? UrlArtifactRepository)?.url
    val local = url != null && url.scheme == "file" &&
        java.io.File(url).canonicalFile.toPath().startsWith(buildDir.toPath())
    if (!local) {
        throw GradleException(
            "Publishing repository '${repository.name}' (${url ?: "no URL"}) in $path is not a local directory under " +
                "$buildDir. The NexNet Java clients are never published to a remote repository (D30)."
        )
    }
}
