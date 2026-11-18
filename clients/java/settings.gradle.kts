// NexNet Java clients: Gradle multi-project build (see docs/articles/contributing-multilang.md).
// Run from the repository root: clients/java/gradlew -p clients/java check

pluginManagement {
    includeBuild("build-logic")
    repositories {
        gradlePluginPortal()
        mavenCentral()
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
    }
}

rootProject.name = "nexnet-java"

include("nexnet-client")
include("interop-host")

// K13 adds the optional modules: nexnet-client-android (only when ANDROID_HOME is set), -kotlin, -okhttp, -micrometer.
