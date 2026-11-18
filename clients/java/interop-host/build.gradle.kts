// JVM language host for the cross-language test harness. Implemented in K12; empty until then.

plugins {
    id("nexnet.java-library")
}

description = "NexNet JVM interop test host (not published)"

dependencies {
    implementation(project(":nexnet-client"))
}
