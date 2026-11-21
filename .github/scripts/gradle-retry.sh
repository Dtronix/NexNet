#!/usr/bin/env bash
# Runs a Gradle command and, when it fails on dependency resolution, runs it once more (H35).
#
#   .github/scripts/gradle-retry.sh clients/java/gradlew -p clients/java check --continue
#
# Why: Maven Central answers some AWS-hosted GitHub runners (the runner log's "Cloud: AWS") with 404 for artifacts it
# has, or with 429, and keeps doing so for minutes. Gradle reports the 404 as "Could not find" (the plugin portal
# redirects Kotlin and other non-plugin artifacts to Central, so both repositories fail together), and Gradle's own
# retries (clients/java/gradle.properties, H29) never retry a 404. A job with no Gradle cache (any pull request into
# master) downloads the whole build-logic classpath, so it is the one that fails.
#
# The retry waits NEXNET_GRADLE_RETRY_DELAY seconds (default 15) and adds:
#   --init-script gradle-central-mirror.init.gradle  download Central's artifacts from Google's mirror instead of Central
#   --refresh-dependencies    Gradle remembers a module it could not find (also in a restored Gradle User Home)
#   --no-configuration-cache  reuse nothing the failed configuration stored
# It retries only when the output shows a resolution failure and no failing test: a real failure is never retried.
set -uo pipefail

if [ "$#" -eq 0 ]; then
  echo "usage: $0 <gradlew> [gradle arguments...]" >&2
  exit 2
fi

log=$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/gradle-retry.XXXXXX")
trap 'rm -f "$log"' EXIT

"$@" 2>&1 | tee "$log"
status=${PIPESTATUS[0]}
if [ "$status" -eq 0 ]; then exit 0; fi

# Dependency resolution failures (Gradle's own wording), including a plugin that could not be resolved.
resolution='Could not resolve all (artifacts|files|dependencies|task dependencies) for configuration|Could not find [A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+(:[A-Za-z0-9_.+-]+)?\.|Could not (GET|HEAD) .https?://|Received status code [0-9]+ from server|was not found in any of the following sources|Error resolving plugin \[id:|Could not download [^ ]+ \(|Skipped due to earlier error'
# A failing test (JUnit, Android connected tests): never retried, even when a resolution failure came with it.
real='tests completed, [0-9]+ failed|There were failing tests|Tests on .+ failed|There w(as|ere) [0-9]+ failure'

if ! grep -Eq "$resolution" "$log"; then exit "$status"; fi
if grep -Eq "$real" "$log"; then
  echo "gradle-retry: not retrying; tests failed too (above)."
  exit "$status"
fi

# Evidence for the next investigation: what the repositories answer now, from this runner.
# (The plugin portal answers 303 for Central-hosted artifacts, so redirects are followed.)
grep -Eo "https?://(repo\.maven\.apache\.org|repo1\.maven\.org|plugins\.gradle\.org|dl\.google\.com/dl/android/maven2)/[^' ]+" "$log" \
  | sort -u | head -n 4 | while read -r url; do
    printf 'gradle-retry: %s -> ' "$url"
    curl -sSL -o /dev/null -m 20 -w '%{http_code} (%{url_effective})\n' "$url" || echo 'curl failed'
  done

# Absolute: Gradle resolves a relative --init-script against the -p directory.
mirror_init="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/gradle-central-mirror.init.gradle"
delay=${NEXNET_GRADLE_RETRY_DELAY:-15}
echo "::warning title=Gradle dependency resolution failed::Retrying once in ${delay}s from Google's Maven Central mirror (H35): $*"
sleep "$delay"
"$@" --init-script "$mirror_init" --refresh-dependencies --no-configuration-cache
