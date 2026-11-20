// Maven Central first: the kotlin-dsl plugin's Kotlin artifacts come from Central directly instead of through the
// plugin portal's redirect to Central (a 429 from Central behind the redirect surfaces as "Could not find" and disables
// the portal for the rest of the build). The portal only serves what Central does not have (plugin markers).
pluginManagement {
    repositories {
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositories {
        mavenCentral()
        gradlePluginPortal()
    }
    versionCatalogs {
        create("libs") {
            from(files("../gradle/libs.versions.toml"))
        }
    }
}

rootProject.name = "build-logic"
