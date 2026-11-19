// com.dtronix:nexnet-client-kotlin: coroutine and Flow adapters for the NexNet Java client (K13). Built and tested, not
// published (D30).

import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("nexnet.java-library")
    alias(libs.plugins.kotlin.jvm)
}

description = "Kotlin coroutine and Flow adapters for the NexNet Java client"

kotlin {
    explicitApi()
    compilerOptions {
        // Java 11 bytecode against the Java 11 API, like the Java modules (--release 11).
        jvmTarget = JvmTarget.JVM_11
        freeCompilerArgs.add("-Xjdk-release=11")
        allWarningsAsErrors = true
    }
}

dependencies {
    api(project(":nexnet-client"))
    api(libs.kotlinx.coroutines.core)
    testImplementation(libs.kotlinx.coroutines.test)
}

tasks.jar {
    manifest {
        attributes("Automatic-Module-Name" to "com.dtronix.nexnet.kotlin")
    }
}
