// Stand-alone consumer build of the generator-emitted Java runtime (K37). Not part of the main build: it compiles the
// sources NexNet.Generator writes into a project's Java output directory (D31), the way a consumer's build would, with
// no NexNet artifact on the class path. Run by :nexnet-client:emittedConsumerTest, which passes the emitted directory.
//
//   jvm      the core runtime and the generated code with no dependency, then the okhttp, micrometer and kotlin
//            modules (G10), each against its own third-party library only
//   android  the core runtime plus the android module in an Android library build; included only with
//            -Pnexnet.emittedConsumer.android=true, which only the Android CI job passes (D26, K39). It does not
//            work in the nested build of emittedConsumerTest (the Android Gradle plugin fails there with "Included
//            builds are not yet available for this build"), so the Android CI job runs this build on its own:
//              clients/java/gradlew -p clients/java/nexnet-client/emitted-consumer-test consumerCheck \
//                -Pnexnet.emitted.dir=<emitted dir> -Pnexnet.emittedConsumer.android=true
//            ANDROID_HOME alone does not include it: CI runners set it in jobs that have no use for it.

pluginManagement {
    // Maven Central before the plugin portal (K41), as in clients/java/settings.gradle.kts.
    repositories {
        mavenCentral()
        gradlePluginPortal()
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
    }
}

dependencyResolutionManagement {
    repositoriesMode = RepositoriesMode.FAIL_ON_PROJECT_REPOS
    repositories {
        mavenCentral()
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
    }
    // The versions the modules are built and tested with (docs/internals/generator-emitted-runtime.md §3.3).
    versionCatalogs {
        create("libs") {
            from(files("../../gradle/libs.versions.toml"))
        }
    }
}

rootProject.name = "nexnet-emitted-consumer-test"

include("jvm")

val androidRequested = providers.gradleProperty("nexnet.emittedConsumer.android")
    .map { it.toBoolean() }
    .getOrElse(false)
if (androidRequested) {
    include("android")
} else {
    logger.lifecycle("Emitted android module skipped: -Pnexnet.emittedConsumer.android=true not set (only the Android CI job builds it).")
}
