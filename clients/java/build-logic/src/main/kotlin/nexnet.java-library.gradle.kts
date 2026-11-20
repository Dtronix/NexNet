// Convention for every NexNet Java library: Java 11 bytecode built with JDK 21, warnings are errors,
// Android API-24 enforcement (Animal Sniffer) in check, JUnit 5 on a selectable test runtime (11 or 21).

plugins {
    `java-library`
}

val libs = the<VersionCatalogsExtension>().named("libs")

group = "com.dtronix"
version = nexnetVersion()

// Built and tested, never published (D30): no signing or Central plugin, no remote publishing repository.
forbidRemotePublishing()

java {
    toolchain {
        languageVersion = JavaLanguageVersion.of(21)
    }
}

tasks.withType<JavaCompile>().configureEach {
    options.release = 11
    options.encoding = "UTF-8"
    options.compilerArgs.addAll(listOf("-Xlint:all", "-Werror"))
}

tasks.withType<Javadoc>().configureEach {
    (options as StandardJavadocDocletOptions).apply {
        encoding = "UTF-8"
        addStringOption("Xdoclint:all,-missing", "-quiet")
        addBooleanOption("Werror", true)
    }
}

// Android API level 24 (Android 7.0): the floor for every library (D3, D26).
val androidSignature = configurations.dependencyScope("androidSignature")
val androidSignatureFiles = configurations.resolvable("androidSignatureFiles") {
    extendsFrom(androidSignature.get())
}

dependencies {
    androidSignature(libs.findLibrary("android-api24-signature").get()) {
        artifact { type = "signature" }
    }

    testImplementation(platform(libs.findLibrary("junit-bom").get()))
    testImplementation(libs.findLibrary("junit-jupiter").get())
    testRuntimeOnly(libs.findLibrary("junit-platform-launcher").get())
}

// Production code only: tests run on the JVM and may use any JDK API.
val mainSourceSet = the<SourceSetContainer>()["main"]
val animalSnifferMain = tasks.register<AnimalSnifferCheck>("animalSnifferMain") {
    group = "verification"
    description = "Checks the main classes against the Android API level 24 signature."
    classesDirs.from(mainSourceSet.output.classesDirs)
    compileClasspath.from(mainSourceSet.compileClasspath)
    signatures.from(androidSignatureFiles)
    report = layout.buildDirectory.file("reports/animalsniffer/main.txt")
}

tasks.named("check") {
    dependsOn(animalSnifferMain)
}

val testJava = providers.gradleProperty("nexnet.testJava").map { it.toInt() }.orElse(21)

tasks.withType<Test>().configureEach {
    useJUnitPlatform()
    javaLauncher = javaToolchains.launcherFor {
        languageVersion = testJava.map { JavaLanguageVersion.of(it) }
    }
    testLogging {
        events("failed")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}
