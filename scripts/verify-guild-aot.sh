#!/usr/bin/env bash
# Publish and execute the dormant Guild contract graph with JSON reflection disabled.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/tests/RetroDownfall.Arcanum.GuildAotSmoke/RetroDownfall.Arcanum.GuildAotSmoke.csproj"
RID=""

usage() {
  echo "Usage: verify-guild-aot.sh --rid <osx-arm64|win-x64|win-arm64>"
  echo "Requires the matching native host SDK and AOT linker; no skip or managed fallback."
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      if [[ $# -lt 2 || -n "$RID" ]]; then
        usage >&2
        exit 2
      fi
      RID="$2"
      shift 2
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      usage >&2
      exit 2
      ;;
  esac
done

case "$RID" in
  osx-arm64 | win-x64 | win-arm64) ;;
  *)
    echo "error: --rid must explicitly select an Arcanum shipping RID" >&2
    exit 2
    ;;
esac

for command in dotnet rg; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "error: required command not found: $command" >&2
    exit 2
  fi
done

case "$(uname -s)" in
  Darwin) HOST_FAMILY=osx ;;
  MINGW* | MSYS* | CYGWIN*) HOST_FAMILY=win ;;
  *) HOST_FAMILY=unsupported ;;
esac

if [[ "$RID" != "$HOST_FAMILY"-* ]]; then
  echo "error: $RID requires a matching native host OS; this host is $HOST_FAMILY" >&2
  exit 1
fi

if ! SDK_INFO="$(dotnet --info)"; then
  echo "error: could not inspect the SDK host RID" >&2
  exit 1
fi

HOST_RID="$(printf '%s\n' "$SDK_INFO" | awk '$1 == "RID:" { print $2; exit }' | tr -d '\r')"

if [[ "$HOST_RID" != "$RID" ]]; then
  echo "error: SDK host RID '$HOST_RID' does not match requested native RID '$RID'" >&2
  exit 1
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-guild-aot.XXXXXX")"
VERIFIED=0
cleanup() {
  local status=$?
  trap - EXIT

  if ! rm -rf -- "$WORK"; then
    echo "error: could not clean Guild AOT verification directory: $WORK" >&2
    status=1
  fi

  if [[ "$status" -eq 0 && "$VERIFIED" -eq 1 ]]; then
    echo "Guild Native AOT verification passed ($RID)."
  elif [[ "$status" -eq 0 ]]; then
    status=1
  fi

  exit "$status"
}
trap cleanup EXIT

# Both paths are unique and absent before publishing. An incremental build or an old
# apphost cannot satisfy the native compilation or executable checks for this run.
OUTPUT="$WORK/publish"
PUBLISH_LOG="$WORK/publish.log"
SMOKE_LOG="$WORK/smoke.log"

if ! dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  -o "$OUTPUT" \
  --artifacts-path "$WORK/artifacts" \
  --disable-build-servers \
  -m:1 \
  -p:PublishAot=true \
  -p:SelfContained=true \
  -p:UseAppHost=true \
  -p:TreatWarningsAsErrors=true \
  -p:IlcTreatWarningsAsErrors=true \
  -p:ILLinkTreatWarningsAsErrors=true \
  -p:TrimmerSingleWarn=false \
  -p:SuppressTrimAnalysisWarnings=false \
  -p:JsonSerializerIsReflectionEnabledByDefault=false \
  2>&1 | tee "$PUBLISH_LOG"; then
  echo "error: Guild Native AOT publish failed" >&2
  exit 1
fi

if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$PUBLISH_LOG"; then
  echo "error: Guild Native AOT publish emitted a warning" >&2
  exit 1
else
  SCAN_STATUS=$?
  if [[ "$SCAN_STATUS" -ne 1 ]]; then
    echo "error: Guild Native AOT warning scan failed ($SCAN_STATUS)" >&2
    exit 1
  fi
fi

if ! rg --no-config -q 'Generating native code' "$PUBLISH_LOG"; then
  echo "error: Guild native compilation evidence missing or scan unavailable; refusing managed fallback" >&2
  exit 1
fi

EXECUTABLE="$OUTPUT/RetroDownfall.Arcanum.GuildAotSmoke"
if [[ "$RID" == win-* ]]; then
  EXECUTABLE="$EXECUTABLE.exe"
fi

if [[ ! -f "$EXECUTABLE" || -L "$EXECUTABLE" ]] || [[ "$RID" != win-* && ! -x "$EXECUTABLE" ]]; then
  echo "error: expected published Guild apphost not found: $EXECUTABLE" >&2
  exit 1
fi

if ! "$EXECUTABLE" 2>&1 | tr -d '\r' | tee "$SMOKE_LOG"; then
  echo "error: published Guild Native AOT smoke failed" >&2
  exit 1
fi

if ! rg --no-config -F -x -q 'Guild Native AOT graph smoke passed.' "$SMOKE_LOG"; then
  echo "error: Guild Native AOT smoke success marker missing or scan unavailable" >&2
  exit 1
fi

VERIFIED=1
