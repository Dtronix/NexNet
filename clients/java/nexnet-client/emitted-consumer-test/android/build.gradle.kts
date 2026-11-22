// Compiles the generator-emitted core runtime plus its android module (G10) in an Android library build, as an Android
// consumer gets them (docs/internals/generator-emitted-runtime.md §3.3): compileSdk 36, minSdk 24, AndroidX lifecycle
// and annotation, no NexNet artifact and no JSpecify (Nullness=none). Warnings are errors, as in nexnet-client-android.
// Included only with -Pnexnet.emittedConsumer.android=true, in a build of its own (the Android CI job, D26, K39).

plugins {
    alias(libs.plugins.android.library)
}

val emittedDir = file(providers.gradleProperty("nexnet.emitted.dir").get())
val runtimeRoot = "com/dtronix/nexnet"

// The emitted core runtime, the generated code and the android module; the JVM-only modules are built by :jvm.
val stagedDir = layout.buildDirectory.dir("emitted/android")
val stageAndroid = tasks.register<Sync>("stageAndroid") {
    from(emittedDir)
    into(stagedDir)
    includeEmptyDirs = false
    include("**/*.java")
    listOf("kotlin", "micrometer", "okhttp").forEach { exclude("$runtimeRoot/$it/**") }
}

android {
    namespace = "com.dtronix.nexnet.emitted.consumer"
    compileSdk = 36

    defaultConfig {
        minSdk = 24
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }

    sourceSets {
        getByName("main") {
            java.directories.add(stagedDir.get().asFile.path)
        }
    }
}

dependencies {
    implementation(libs.androidx.lifecycle.process)
    implementation(libs.androidx.annotation)
}

val checkEmitted = tasks.register("checkEmitted") {
    val marker = emittedDir.resolve("$runtimeRoot/android/NexusAndroid.java")
    doLast {
        if (!marker.isFile) {
            throw GradleException(
                "The emitted sample has no android module ($marker is missing). Rebuild it: dotnet build " +
                    "src/NexNet.Generator.IntegrationSample \"-p:NexNetJavaEmitRuntimeModules=android%3Bkotlin%3Bokhttp%3Bmicrometer\""
            )
        }
    }
}

stageAndroid.configure { dependsOn(checkEmitted) }

tasks.named("preBuild") {
    dependsOn(stageAndroid)
}

tasks.withType<JavaCompile>().configureEach {
    dependsOn(stageAndroid)
    options.encoding = "UTF-8"
    options.compilerArgs.addAll(listOf("-Xlint:all,-options,-processing,-classfile", "-Werror"))
}

tasks.register("consumerCheck") {
    dependsOn("assembleRelease")
}
