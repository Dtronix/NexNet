// Compiles the generator-emitted Java runtime (D31) and its optional modules (G10) as a consumer gets them: the core
// runtime and the generated code with nothing on the class path (no nexnet-client, no JSpecify: Nullness=none), then
// each module against the emitted core and its own third-party library only. Warnings are errors, as in the modules'
// own builds: javac --release 11 -Xlint:all -Werror; Kotlin explicit API mode, JVM 11, allWarningsAsErrors.

import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    java
    alias(libs.plugins.kotlin.jvm)
}

val emittedDir = file(providers.gradleProperty("nexnet.emitted.dir").get())
val runtimeRoot = "com/dtronix/nexnet"
val modules = listOf("android", "kotlin", "micrometer", "okhttp")

java {
    toolchain {
        languageVersion = JavaLanguageVersion.of(21)
    }
}

// The emitted tree has the core runtime, the generated code and every listed module in one source root. Each part is
// copied to its own folder, so each compilation sees only its own sources.
fun stage(name: String, configure: Sync.() -> Unit) = tasks.register<Sync>("stage" + name.replaceFirstChar { it.uppercase() }) {
    from(emittedDir)
    into(layout.buildDirectory.dir("emitted/" + name))
    includeEmptyDirs = false
    configure()
}

val stageCore = stage("core") {
    include("**/*.java")
    modules.forEach { exclude("$runtimeRoot/$it/**") }
}
val stageOkhttp = stage("okhttp") { include("$runtimeRoot/okhttp/**/*.java") }
val stageMicrometer = stage("micrometer") { include("$runtimeRoot/micrometer/**/*.java") }
val stageCoroutines = stage("coroutines") { include("$runtimeRoot/kotlin/**/*.kt") }

val core = sourceSets.main.get()
core.java.setSrcDirs(listOf(stageCore))
core.kotlin.setSrcDirs(emptyList<Any>())

fun moduleSourceSet(name: String) = sourceSets.create(name) {
    java.setSrcDirs(emptyList<Any>())
    kotlin.setSrcDirs(emptyList<Any>())
    compileClasspath += core.output
}

val okhttp = moduleSourceSet("okhttp").apply { java.srcDir(stageOkhttp) }
val micrometer = moduleSourceSet("micrometer").apply { java.srcDir(stageMicrometer) }
val coroutines = moduleSourceSet("coroutines").apply { kotlin.srcDir(stageCoroutines) }

dependencies {
    okhttp.implementationConfigurationName(libs.okhttp)
    micrometer.implementationConfigurationName(libs.micrometer.core)
    coroutines.implementationConfigurationName(libs.kotlinx.coroutines.core)
}

tasks.withType<JavaCompile>().configureEach {
    options.release = 11
    options.encoding = "UTF-8"
    options.compilerArgs.addAll(listOf("-Xlint:all", "-Werror"))
}

kotlin {
    compilerOptions {
        jvmTarget = JvmTarget.JVM_11
        // explicitApi() covers only the main compilation; the module is compiled by its own source set.
        freeCompilerArgs.addAll("-Xjdk-release=11", "-Xexplicit-api=strict")
        allWarningsAsErrors = true
    }
}

// A module the build did not emit would compile as an empty source set; fail instead, naming the missing module.
val required = listOf("kotlin", "micrometer", "okhttp")
val checkEmitted = tasks.register("checkEmitted") {
    val root = emittedDir
    doLast {
        val missingCore = !root.resolve("$runtimeRoot/NexusRuntime.java").isFile
        val missing = required.filter { !root.resolve("$runtimeRoot/$it").isDirectory }
        if (missingCore || missing.isNotEmpty()) {
            throw GradleException(
                "The emitted sample in $root lacks " + (if (missingCore) "the core runtime" else "the modules $missing") +
                    ". Rebuild it: dotnet build src/NexNet.Generator.IntegrationSample \"-p:NexNetJavaEmitRuntimeModules=kotlin%3Bokhttp%3Bmicrometer\""
            )
        }
    }
}

tasks.register("consumerCheck") {
    dependsOn(checkEmitted)
    dependsOn(sourceSets.map { it.classesTaskName })
}

tasks.withType<AbstractCompile>().configureEach {
    mustRunAfter(checkEmitted)
}
