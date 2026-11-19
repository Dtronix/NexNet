// NexNet Java clients: Gradle multi-project build (see docs/articles/contributing-multilang.md).
// Run from the repository root: clients/java/gradlew -p clients/java check

pluginManagement {
    includeBuild("build-logic")
    repositories {
        gradlePluginPortal()
        mavenCentral()
        // The Android Gradle plugin (nexnet-client-android only).
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
    }
}

plugins {
    // Provisions the JDK 11 test runtime on demand (-Pnexnet.testJava=11). The build itself runs on the local JDK 21.
    id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0"
}

dependencyResolutionManagement {
    repositoriesMode = RepositoriesMode.FAIL_ON_PROJECT_REPOS
    repositories {
        mavenCentral()
        // AndroidX and the Android tool chain (nexnet-client-android only).
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
    }
}

rootProject.name = "nexnet-java"

include("nexnet-client")
include("interop-host")

// Optional modules (K13). They are built and tested, never published (D30).
include("nexnet-client-kotlin")
include("nexnet-client-okhttp")
include("nexnet-client-micrometer")

// The Android AAR needs the Android SDK (D26): only the nightly CI job and Android work set ANDROID_HOME. Without it the
// module is left out, so check needs no SDK.
val androidHome = providers.environmentVariable("ANDROID_HOME")
    .orElse(providers.environmentVariable("ANDROID_SDK_ROOT"))
    .orNull
if (androidHome.isNullOrBlank()) {
    logger.lifecycle("nexnet-client-android skipped: ANDROID_HOME is not set (only the Android module needs the SDK).")
} else {
    include("nexnet-client-android")
}
