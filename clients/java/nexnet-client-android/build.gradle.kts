// com.dtronix:nexnet-client-android: Android integration for the NexNet Java client (K13): main-thread callbacks,
// android.util.Log logging, a lifecycle- and network-aware connection keeper and java.time converters. An AAR built with
// the Android Gradle plugin; settings.gradle.kts includes it only when ANDROID_HOME is set (D26), so the gate
// (check) needs no Android SDK. The nightly CI job builds it and runs src/androidTest on an API-24 emulator.
// Built and tested, not published (D30).
//
// The device tests also compile the Java client of the interop test nexus (src/NexNet.TestNexus), which the nightly job's
// interop server (src/NexNet.InteropServer/Cli) hosts. The generator writes it to artifacts/interop/java/generated when
// TestNexus builds, so run "dotnet build src/NexNet.InteropServer/Cli" before the device tests. The AAR does not need it.

plugins {
    alias(libs.plugins.android.library)
}

val repoRoot: File = rootDir.parentFile.parentFile
val interopClientDir: File = repoRoot.resolve("artifacts/interop/java/generated")
val interopClientMarker: File = interopClientDir.resolve("com/dtronix/nexnet/testnexus/InteropClient.java")

description = "Android integration for the NexNet Java client"

android {
    namespace = "com.dtronix.nexnet.android"
    compileSdk = 36

    defaultConfig {
        minSdk = 24
        consumerProguardFiles("consumer-rules.pro")
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }

    sourceSets {
        getByName("androidTest") {
            java.srcDir(interopClientDir)
        }
    }
}

// Warnings are errors, as in the other modules (-options: the Android boot class path replaces the JDK's).
tasks.withType<JavaCompile>().configureEach {
    options.encoding = "UTF-8"
    options.compilerArgs.addAll(listOf("-Xlint:all,-options,-processing,-classfile", "-Werror"))
    if (name.contains("AndroidTest")) {
        val marker = interopClientMarker
        doFirst {
            if (!marker.exists()) {
                throw GradleException("The interop test nexus Java client is not generated (" + marker + " is missing). " +
                    "Run: dotnet build src/NexNet.InteropServer/Cli")
            }
        }
    }
}

dependencies {
    api(project(":nexnet-client"))
    implementation(libs.androidx.lifecycle.process)
    implementation(libs.androidx.annotation)
    compileOnly(libs.jspecify)

    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.ext.junit)
    androidTestCompileOnly(libs.jspecify)
}
