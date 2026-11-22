// JVM language host of the cross-language test harness (K12). The C# runner (src/NexNet.Interop) launches it and drives
// real Java clients over the control protocol: docs/internals/interop-control-protocol.md.
//
// Build it with:  clients/java/gradlew -p clients/java :interop-host:installInteropHost
// That installs the host and writes artifacts/interop/java/host.json (control protocol §10). The host compiles the Java
// code the generator writes for src/NexNet.TestNexus (with its test scaffolding) into artifacts/interop/java/generated,
// so run "dotnet build src/NexNet.TestNexus" (or "dotnet build src") first.
//
// The JVM that runs the host: -Pnexnet.testRuntime=11|21 (default: nexnet.testJava, then 21), so CI can run the
// host, like the unit tests, on Java 11.
//
// NEXNET_JAVA_WEBSOCKET=okhttp in the environment of the test run (the runner's environment reaches the host) runs the
// WebSocket clients on the OkHttp connector of nexnet-client-okhttp instead of the built-in transport (Q08). The host
// then reports only the WebSocket and WebSocketTls transports, so the other rows are skipped. CI runs it as the
// "java-okhttp" interop job.

plugins {
    id("nexnet.java-library")
    application
}

description = "NexNet JVM interop test host (not published)"

val repoRoot: File = rootDir.parentFile.parentFile
val generatedDir: File = repoRoot.resolve("artifacts/interop/java/generated")
val generatedMarkerFile: File = generatedDir.resolve("com/dtronix/nexnet/testnexus/scaffolding/Scaffolding.java")
val hostJsonFile: File = repoRoot.resolve("artifacts/interop/java/host.json")
val hostMainClass = "com.dtronix.nexnet.interop.host.Main"
val hostJvmArgs = listOf("-XX:+UseSerialGC", "-Xshare:auto")

sourceSets {
    main {
        java.srcDir(generatedDir)
    }
}

dependencies {
    implementation(project(":nexnet-client"))
    implementation(project(":nexnet-client-okhttp"))
    // ClientSpec.metrics = Micrometer (Q05): the client's metrics through NexusMicrometerBinder.
    implementation(project(":nexnet-client-micrometer"))
}

application {
    mainClass = hostMainClass
    applicationDefaultJvmArgs = hostJvmArgs
}

// A JVM test tool, not Android code: the API-24 check does not apply.
tasks.named("animalSnifferMain") {
    enabled = false
}

// Without the generated TestNexus code (dotnet build src/NexNet.TestNexus) there is nothing to compile; the Java gate
// (check) then skips this project instead of failing.
tasks.withType<JavaCompile>().configureEach {
    val marker = generatedMarkerFile
    onlyIf("the TestNexus Java code is generated (dotnet build src/NexNet.TestNexus)") { marker.exists() }
}
tasks.withType<Test>().configureEach {
    val marker = generatedMarkerFile
    onlyIf("the TestNexus Java code is generated (dotnet build src/NexNet.TestNexus)") { marker.exists() }
}

val testRuntime = providers.gradleProperty("nexnet.testRuntime")
    .orElse(providers.gradleProperty("nexnet.testJava"))
    .map { it.toInt() }
    .orElse(21)
val hostLauncher = javaToolchains.launcherFor {
    languageVersion = testRuntime.map { JavaLanguageVersion.of(it) }
}

tasks.withType<Test>().configureEach {
    javaLauncher = hostLauncher
}

/** Writes host.json (control protocol §10): the runner launches the installed host with it. */
abstract class WriteHostJson : DefaultTask() {
    @get:Input
    abstract val javaExecutable: Property<String>

    @get:Input
    abstract val installDir: Property<String>

    @get:Input
    abstract val jvmArgs: ListProperty<String>

    @get:Input
    abstract val mainClass: Property<String>

    @get:Input
    abstract val generatedMarker: Property<String>

    @get:OutputFile
    abstract val output: RegularFileProperty

    @TaskAction
    fun write() {
        if (!File(generatedMarker.get()).exists()) {
            throw GradleException("The TestNexus Java code is not generated (" + generatedMarker.get() + " is missing). " +
                "Run: dotnet build src/NexNet.TestNexus")
        }
        fun q(s: String) = "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
        val install = File(installDir.get())
        val windows = System.getProperty("os.name").lowercase().contains("windows")
        val script = install.resolve(if (windows) "bin/interop-host.bat" else "bin/interop-host")
        val args = jvmArgs.get() + listOf("-cp", install.resolve("lib").absolutePath + File.separator + "*", mainClass.get())
        val json = buildString {
            append("{\n")
            append("  \"command\": ").append(q(javaExecutable.get())).append(",\n")
            append("  \"args\": [").append(args.joinToString(", ") { q(it) }).append("],\n")
            append("  \"workingDir\": \".\",\n")
            append("  \"env\": {\"JAVA_OPTS\": ").append(q(jvmArgs.get().joinToString(" "))).append("},\n")
            append("  \"startScript\": ").append(q(script.absolutePath)).append(",\n")
            append("  \"maxConcurrency\": 8,\n")
            append("  \"startupTimeoutMs\": 60000\n")
            append("}\n")
        }
        val file = output.get().asFile
        file.parentFile.mkdirs()
        file.writeText(json)
        logger.lifecycle("Wrote " + file.absolutePath)
    }
}

val installDist = tasks.named<Sync>("installDist")
tasks.register<WriteHostJson>("installInteropHost") {
    group = "distribution"
    description = "Installs the JVM interop host and writes artifacts/interop/java/host.json."
    dependsOn(installDist)
    javaExecutable = hostLauncher.map { it.executablePath.asFile.absolutePath }
    installDir = installDist.map { it.destinationDir.absolutePath }
    jvmArgs = hostJvmArgs
    mainClass = hostMainClass
    generatedMarker = generatedMarkerFile.absolutePath
    output = hostJsonFile
}
