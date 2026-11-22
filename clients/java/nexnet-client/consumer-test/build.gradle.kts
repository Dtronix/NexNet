// Compiles the Java generator's output for the interop zoo (the K03 snapshot) against nexnet-client exactly as a
// user gets it: the only declared dependency is com.dtronix:nexnet-client. Generated code imports JSpecify, so this
// fails if the runtime stops exposing it. Two variants: one resolves the Gradle module metadata, the other the POM
// alone (Maven users). Each then round-trips a value through the generated formatters on its runtime classpath.
//
// Run by :nexnet-client:consumerTest, which passes the repository, the version and the snapshot path.

plugins {
    java
}

val nexnetRepo = providers.gradleProperty("nexnet.consumer.repo").get()
val nexnetVersion = providers.gradleProperty("nexnet.consumer.version").get()
val zooSnapshot = file(providers.gradleProperty("nexnet.consumer.snapshot").get())

val pomSourceSet = "pom"

repositories {
    maven {
        name = "nexnetGradleMetadata"
        url = uri(nexnetRepo)
        metadataSources { gradleMetadata() }
        content {
            includeGroup("com.dtronix")
            onlyForConfigurations("compileClasspath", "runtimeClasspath")
        }
    }
    maven {
        name = "nexnetPomOnly"
        url = uri(nexnetRepo)
        metadataSources {
            mavenPom()
            ignoreGradleMetadataRedirection()
        }
        content {
            includeGroup("com.dtronix")
            onlyForConfigurations("${pomSourceSet}CompileClasspath", "${pomSourceSet}RuntimeClasspath")
        }
    }
    mavenCentral {
        content { excludeGroup("com.dtronix") }
    }
}

// The snapshot holds every generated file, each after a "// ==== <path>" line.
val generatedDir = layout.buildDirectory.dir("generated/sources/zoo/java")
val splitZoo = tasks.register("splitZoo") {
    val snapshot = zooSnapshot
    val outputDir = generatedDir
    inputs.file(snapshot)
    outputs.dir(outputDir)
    doLast {
        val root = outputDir.get().asFile
        root.deleteRecursively()
        val marker = "// ==== "
        var path: String? = null
        val body = StringBuilder()
        fun flush() {
            val current = path ?: return
            val target = root.resolve(current)
            target.parentFile.mkdirs()
            target.writeText(body.toString())
            body.setLength(0)
        }
        snapshot.readLines().forEach { line ->
            if (line.startsWith(marker)) {
                flush()
                path = line.removePrefix(marker).trim()
            } else if (path != null) {
                body.append(line).append('\n')
            }
        }
        flush()
        check(root.walk().any { it.isFile && it.name.endsWith(".java") }) { "No generated files in $snapshot" }
    }
}

sourceSets {
    main {
        java.srcDir(splitZoo)
    }
    create(pomSourceSet) {
        java.srcDir(splitZoo)
        java.srcDir("src/main/java")
    }
}

dependencies {
    implementation("com.dtronix:nexnet-client:$nexnetVersion")
    "${pomSourceSet}Implementation"("com.dtronix:nexnet-client:$nexnetVersion")
}

tasks.withType<JavaCompile>().configureEach {
    options.release = 11
    options.encoding = "UTF-8"
    options.compilerArgs.addAll(listOf("-Xlint:all", "-Werror"))
}

val runGradleMetadata = tasks.register<JavaExec>("runGradleMetadata") {
    classpath = sourceSets.main.get().runtimeClasspath
    mainClass = "com.dtronix.nexnet.consumer.ConsumerMain"
}

val runPomOnly = tasks.register<JavaExec>("runPomOnly") {
    classpath = sourceSets[pomSourceSet].runtimeClasspath
    mainClass = "com.dtronix.nexnet.consumer.ConsumerMain"
}

tasks.register("consumerCheck") {
    dependsOn(runGradleMetadata, runPomOnly)
}
