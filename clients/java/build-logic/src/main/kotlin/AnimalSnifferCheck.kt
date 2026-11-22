import java.io.File
import org.codehaus.mojo.animal_sniffer.ClassListBuilder
import org.codehaus.mojo.animal_sniffer.SignatureChecker
import org.codehaus.mojo.animal_sniffer.logging.Logger
import org.gradle.api.DefaultTask
import org.gradle.api.GradleException
import org.gradle.api.file.ConfigurableFileCollection
import org.gradle.api.file.RegularFileProperty
import org.gradle.api.tasks.CacheableTask
import org.gradle.api.tasks.Classpath
import org.gradle.api.tasks.CompileClasspath
import org.gradle.api.tasks.InputFiles
import org.gradle.api.tasks.OutputFile
import org.gradle.api.tasks.PathSensitive
import org.gradle.api.tasks.PathSensitivity
import org.gradle.api.tasks.TaskAction

/**
 * Checks compiled classes against an Animal Sniffer API signature (here: Android API level 24), the same check as
 * the Animal Sniffer Maven plugin. A reference to a JDK class, method or field that the signature lacks fails the
 * build. References to the project's own classes and to its compile classpath are not checked.
 *
 * Calls the org.codehaus.mojo:animal-sniffer library directly instead of a third-party Gradle plugin, so the build
 * stays free of Gradle deprecations.
 */
@CacheableTask
abstract class AnimalSnifferCheck : DefaultTask() {
    /** The class directories to check. */
    @get:Classpath
    abstract val classesDirs: ConfigurableFileCollection

    /** The compile classpath: its classes may be referenced freely. */
    @get:CompileClasspath
    abstract val compileClasspath: ConfigurableFileCollection

    /** The .signature file(s). */
    @get:InputFiles
    @get:PathSensitive(PathSensitivity.NONE)
    abstract val signatures: ConfigurableFileCollection

    /** The report: one line per violation, empty when the check passes. */
    @get:OutputFile
    abstract val report: RegularFileProperty

    @TaskAction
    fun check() {
        val classDirs = classesDirs.files.filter { it.exists() }
        val violations = mutableListOf<String>()
        val logger = SnifferLogger(violations)

        // Own classes and classpath classes are "ignored packages": references to them are not checked.
        val known = ClassListBuilder(logger)
        classDirs.forEach { known.process(it) }
        compileClasspath.files.filter { it.exists() }.forEach { known.process(it) }

        for (signature in signatures.files) {
            val checker = signature.inputStream().use { SignatureChecker(it, known.packages, logger) }
            checker.setCheckJars(false)
            checker.setSourcePath(emptyList<File>())
            classDirs.forEach { checker.process(it) }
        }

        val reportFile = report.get().asFile
        reportFile.parentFile.mkdirs()
        reportFile.writeText(violations.joinToString(separator = "\n", postfix = if (violations.isEmpty()) "" else "\n"))
        if (violations.isNotEmpty()) {
            throw GradleException(
                "${violations.size} Android API-24 violation(s) (signature ${signatures.files.joinToString { it.name }}):\n" +
                    violations.joinToString("\n") { "  $it" } +
                    "\nThese JDK APIs are missing on Android 7.0; see docs/articles/contributing-multilang.md for the forbidden list."
            )
        }
    }

    private class SnifferLogger(private val violations: MutableList<String>) : Logger {
        override fun info(message: String) {}
        override fun info(message: String, t: Throwable) {}
        override fun debug(message: String) {}
        override fun debug(message: String, t: Throwable) {}
        override fun warn(message: String) {}
        override fun warn(message: String, t: Throwable) {}
        override fun error(message: String) {
            violations += message
        }
        override fun error(message: String, t: Throwable) {
            violations += "$message: $t"
        }
    }
}
