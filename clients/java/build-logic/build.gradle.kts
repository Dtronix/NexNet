plugins {
    `kotlin-dsl`
}

dependencies {
    // The Animal Sniffer checker library (not a Gradle plugin): AnimalSnifferCheck calls it directly.
    implementation(libs.animalsniffer)
}
