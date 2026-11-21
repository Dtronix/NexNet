// com.dtronix:nexnet-client-okhttp: a WebSocket transport over OkHttp, for apps that already configure OkHttp (proxies,
// interceptors, certificate pinning) (K13). Built and tested, not published (D30).

plugins {
    id("nexnet.java-library")
}

description = "OkHttp WebSocket transport for the NexNet Java client"

dependencies {
    api(project(":nexnet-client"))
    api(libs.okhttp)
    testImplementation(libs.okhttp.mockwebserver)
    testCompileOnly(libs.jspecify)
}

tasks.jar {
    manifest {
        attributes("Automatic-Module-Name" to "com.dtronix.nexnet.okhttp")
    }
}

// Broken Javadoc fails check, as in the core module.
tasks.check {
    dependsOn(tasks.javadoc)
}
