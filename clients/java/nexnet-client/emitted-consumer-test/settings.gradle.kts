// Stand-alone consumer build of the generator-emitted Java runtime (K37). Not part of the main build: it compiles the
// sources NexNet.Generator writes into a project's Java output directory (D31), the way a consumer's build would, with
// no NexNet artifact on the class path. Run by :nexnet-client:emittedConsumerTest, which passes the emitted directory.
//
//   jvm      the core runtime and the generated code with no dependency, then the okhttp, micrometer and kotlin
//            modules (G10), each against its own third-party library only
//   android  the core runtime plus the android module in an Android library build; included only when ANDROID_HOME is
//            set (D26), as in the main build

pluginManagement {
    repositories {
        gradlePluginPortal()
        mavenCentral()
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

val androidHome = providers.environmentVariable("ANDROID_HOME")
    .orElse(providers.environmentVariable("ANDROID_SDK_ROOT"))
    .orNull
if (androidHome.isNullOrBlank()) {
    logger.lifecycle("Emitted android module skipped: ANDROID_HOME is not set (only the nightly Android job builds it).")
} else {
    include("android")
}
