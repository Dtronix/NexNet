// com.dtronix:nexnet-client-android: Android integration for the NexNet Java client (K13): main-thread callbacks,
// android.util.Log logging, a lifecycle- and network-aware connection keeper and java.time converters. An AAR built with
// the Android Gradle plugin; settings.gradle.kts includes it only when ANDROID_HOME is set (D26), so the gate
// (check) needs no Android SDK. The nightly CI job builds it and runs src/androidTest on an API-24 emulator.
// Built and tested, not published (D30).

plugins {
    alias(libs.plugins.android.library)
}

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
}

// Warnings are errors, as in the other modules (-options: the Android boot class path replaces the JDK's).
tasks.withType<JavaCompile>().configureEach {
    options.encoding = "UTF-8"
    options.compilerArgs.addAll(listOf("-Xlint:all,-options,-processing,-classfile", "-Werror"))
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
