#!/usr/bin/env bash
# Reports which Java/JavaScript toolchains are installed for NexNet multi-language work.
#
# Usage:
#   bash tools/setup/check-toolchain.sh [--require java,node,...]
#
# Prints found/missing and the version for: dotnet, java/javac (21+), node (22+), npm,
# ANDROID_HOME and Playwright. Exits 0 unless --require names a tool that is missing or too old.
# Recognized --require names: dotnet, java, node, npm, android, playwright.
# See docs/articles/contributing-multilang.md.

set -u

KNOWN="dotnet java node npm android playwright"
REQUIRED=""

add_required() {
  local list="$1" name
  IFS=',' read -r -a names <<< "$list"
  for name in "${names[@]}"; do
    name="$(echo "$name" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')"
    [ -z "$name" ] && continue
    case " $KNOWN " in
      *" $name "*) ;;
      *) echo "check-toolchain: unknown tool '$name' in --require (known: ${KNOWN// /, })" >&2; exit 2 ;;
    esac
    case " $REQUIRED " in
      *" $name "*) ;;
      *) REQUIRED="${REQUIRED:+$REQUIRED }$name" ;;
    esac
  done
}

while [ $# -gt 0 ]; do
  case "$1" in
    --require=*) add_required "${1#--require=}" ;;
    --require|-require)
      if [ $# -lt 2 ]; then
        echo "check-toolchain: --require needs a value, e.g. --require java,node" >&2
        exit 2
      fi
      shift
      add_required "$1"
      ;;
    -h|--help) echo "Usage: check-toolchain.sh [--require dotnet,java,node,npm,android,playwright]"; exit 0 ;;
    *) echo "check-toolchain: unknown argument '$1'" >&2; exit 2 ;;
  esac
  shift
done

# Per-tool state: OK_<name>=1|0 and PROBLEM_<name>=message
set_state() { eval "OK_$1=$2"; [ $# -ge 3 ] && eval "PROBLEM_$1=\"\$3\""; return 0; }

row() {
  # row <ok 1|0> <name> <detail>
  if [ "$1" = 1 ]; then mark="found  "; else mark="MISSING"; fi
  printf '  %s  %-12s %s\n' "$mark" "$2" "$3"
}

major_of() {
  # Prints the major version of "21.0.13", "1.8.0_402", "v24.1.0"; -1 if unparseable.
  local v="${1#v}"
  if [[ "$v" =~ ^1\.([0-9]+) ]]; then echo "${BASH_REMATCH[1]}"
  elif [[ "$v" =~ ^([0-9]+) ]]; then echo "${BASH_REMATCH[1]}"
  else echo -1; fi
}

echo "NexNet toolchain check"
echo

# dotnet
if exe="$(command -v dotnet 2>/dev/null)"; then
  v="$(dotnet --version 2>&1 | head -n 1 | tr -d '\r')"
  row 1 dotnet "$v ($exe)"; set_state dotnet 1
else
  row 0 dotnet "not on PATH; install the .NET SDK"
  set_state dotnet 0 "dotnet not found on PATH; install the .NET SDK (https://dot.net)"
fi

# java + javac (major 21+)
java_ok=1
java_problems=""
for tool in java javac; do
  exe=""
  if [ -n "${JAVA_HOME:-}" ]; then
    for cand in "$JAVA_HOME/bin/$tool" "$JAVA_HOME/bin/$tool.exe"; do
      if [ -f "$cand" ]; then exe="$cand"; break; fi
    done
  fi
  [ -z "$exe" ] && exe="$(command -v "$tool" 2>/dev/null || true)"
  if [ -z "$exe" ]; then
    row 0 "$tool" "not found (set JAVA_HOME or put it on PATH); need JDK 21+"
    java_ok=0; java_problems="${java_problems:+$java_problems; }$tool not found"
    continue
  fi
  out="$("$exe" -version 2>&1 | tr -d '\r')"
  v=""
  if [[ "$out" =~ version\ \"([^\"]+)\" ]]; then v="${BASH_REMATCH[1]}"
  elif [[ "$out" =~ javac\ ([^[:space:]]+) ]]; then v="${BASH_REMATCH[1]}"; fi
  if [ -z "$v" ]; then
    row 0 "$tool" "could not read version ($exe)"
    java_ok=0; java_problems="${java_problems:+$java_problems; }$tool version unreadable"
    continue
  fi
  m="$(major_of "$v")"
  if [ "$m" -ge 21 ]; then
    row 1 "$tool" "$v ($exe)"
  else
    row 0 "$tool" "$v is too old; need 21+ ($exe)"
    java_ok=0; java_problems="${java_problems:+$java_problems; }$tool $v is older than 21"
  fi
done
if [ "$java_ok" = 1 ]; then set_state java 1
else set_state java 0 "$java_problems. Install JDK 21+, e.g. sdk install java 21-tem or apt install temurin-21-jdk"; fi

# node (22+)
if exe="$(command -v node 2>/dev/null)"; then
  v="$(node --version 2>&1 | head -n 1 | tr -d '\r')"; v="${v#v}"
  m="$(major_of "$v")"
  if [ "$m" -ge 22 ]; then
    row 1 node "$v ($exe)"; set_state node 1
  else
    row 0 node "$v is too old; need 22+ ($exe)"
    set_state node 0 "node $v is older than 22; install Node 22+ (24 LTS recommended)"
  fi
else
  row 0 node "not on PATH; need Node 22+ (24 LTS recommended)"
  set_state node 0 "node not found on PATH; install Node 22+ (24 LTS recommended)"
fi

# npm
if exe="$(command -v npm 2>/dev/null)"; then
  v="$(npm --version 2>&1 | head -n 1 | tr -d '\r')"
  row 1 npm "$v ($exe)"; set_state npm 1
else
  row 0 npm "not on PATH; npm ships with Node"
  set_state npm 0 "npm not found on PATH; it ships with Node"
fi

# ANDROID_HOME (only needed for nexnet-client-android work)
if [ -n "${ANDROID_HOME:-}" ] && [ -d "$ANDROID_HOME" ]; then
  row 1 ANDROID_HOME "$ANDROID_HOME"; set_state android 1
elif [ -n "${ANDROID_HOME:-}" ]; then
  row 0 ANDROID_HOME "set to '$ANDROID_HOME' but the directory does not exist"
  set_state android 0 "ANDROID_HOME points to a missing directory: $ANDROID_HOME"
else
  row 0 ANDROID_HOME "not set (only needed for nexnet-client-android work)"
  set_state android 0 "ANDROID_HOME is not set; install the Android command-line tools"
fi

# Playwright (via npx, without installing anything)
if command -v npx >/dev/null 2>&1; then
  out="$(npx --no-install playwright --version 2>&1 | tr -d '\r')"
  line="$(printf '%s\n' "$out" | grep -E '^Version[[:space:]]+[^[:space:]]+' | head -n 1)"
  if [ -n "$line" ]; then
    row 1 playwright "$(printf '%s' "$line" | sed -E 's/^Version[[:space:]]+//')"; set_state playwright 1
  else
    row 0 playwright "not installed (installs with clients/js; then npx playwright install chromium)"
    set_state playwright 0 "playwright not found via npx; run npm ci --prefix clients/js, then npx playwright install chromium"
  fi
else
  row 0 playwright "npx not on PATH"
  set_state playwright 0 "npx not found on PATH; install Node 22+"
fi

echo
failed=0
for name in $REQUIRED; do
  eval "ok=\${OK_$name:-0}"
  if [ "$ok" != 1 ]; then
    eval "msg=\${PROBLEM_$name:-}"
    echo "check-toolchain: required tool '$name' is not usable: $msg" >&2
    failed=1
  fi
done
if [ "$failed" = 1 ]; then
  echo "See docs/articles/contributing-multilang.md" >&2
  exit 1
fi
if [ -n "$REQUIRED" ]; then
  echo "All required tools present: ${REQUIRED// /, }"
else
  echo "Only the .NET SDK is needed for C# work. Use --require to enforce others."
fi
exit 0
