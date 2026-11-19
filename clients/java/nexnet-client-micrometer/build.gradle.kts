// com.dtronix:nexnet-client-micrometer: NexNet client metrics as Micrometer meters (K13). Built and tested, not published
// (D30).

plugins {
    id("nexnet.java-library")
}

description = "Micrometer meters for the NexNet Java client metrics"

dependencies {
    api(project(":nexnet-client"))
    api(libs.micrometer.core)
    testCompileOnly(libs.jspecify)
}

tasks.jar {
    manifest {
        attributes("Automatic-Module-Name" to "com.dtronix.nexnet.micrometer")
    }
}

// Broken Javadoc fails check, as in the core module.
tasks.check {
    dependsOn(tasks.javadoc)
}
