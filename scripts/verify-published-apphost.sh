#!/usr/bin/env bash
# Exercise one existing published Arcanum apphost without publishing or substituting another binary.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_PROJECT="$REPO_ROOT/tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj"
CONFIGURATION=Release
EXECUTABLE=""
TEST_FILTER='FullyQualifiedName=RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumSessionSmokeTests.Published_executable_creates_initial_session_and_completes_provider_contract_exchange'
EXPECTED_SMOKE_RECEIPT='published-session-smoke:v1'

usage() {
  cat <<'EOF'
Usage: verify-published-apphost.sh --executable <published-apphost> [--configuration <name>]

Builds the managed smoke harness without warnings, then exercises exactly the supplied published
apphost as both host and CLI through the deterministic first-session provider-contract test. The
test must write the expected success receipt after all assertions and cleanup. This gate never
publishes or substitutes a product binary.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --executable)
      EXECUTABLE="${2:?--executable requires a path}"
      shift 2
      ;;
    --configuration)
      CONFIGURATION="${2:?--configuration requires a name}"
      shift 2
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

for command in dotnet rg; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "error: required command not found: $command" >&2
    exit 2
  fi
done

if [[ -z "$EXECUTABLE" ]]; then
  echo "error: --executable is required" >&2
  exit 2
fi

EXECUTABLE_PATH="$EXECUTABLE"
WINDOWS_HOST=0

case "$(uname -s)" in
  CYGWIN* | MINGW* | MSYS*)
    WINDOWS_HOST=1
    ;;
esac

if [[ "$WINDOWS_HOST" -eq 1 ]]; then
  if ! command -v cygpath >/dev/null 2>&1; then
    echo "error: cygpath is required to validate the published Windows apphost" >&2
    exit 2
  fi

  EXECUTABLE_PATH="$(cygpath -u "$EXECUTABLE")"
fi

if [[ ! -f "$EXECUTABLE_PATH" ]]; then
  echo "error: --executable must name an existing published apphost: $EXECUTABLE" >&2
  exit 2
fi

if [[ "$WINDOWS_HOST" -eq 0 && ! -x "$EXECUTABLE_PATH" ]]; then
  echo "error: --executable must name an executable published apphost: $EXECUTABLE" >&2
  exit 2
fi

canonical_existing_file() {
  local path="$1"
  local directory
  local leaf
  local physical_directory

  if [[ "$path" == */* ]]; then
    directory="${path%/*}"
    leaf="${path##*/}"

    if [[ -z "$directory" ]]; then
      directory="/"
    fi
  else
    directory="."
    leaf="$path"
  fi

  physical_directory="$(cd "$directory" && pwd -P)"

  if [[ "$physical_directory" == "/" ]]; then
    printf '/%s\n' "$leaf"
  else
    printf '%s/%s\n' "$physical_directory" "$leaf"
  fi
}

EXECUTABLE_PATH="$(canonical_existing_file "$EXECUTABLE_PATH")"
SMOKE_EXECUTABLE="$EXECUTABLE_PATH"

if [[ "$WINDOWS_HOST" -eq 1 ]]; then
  SMOKE_EXECUTABLE="$(cygpath -w "$EXECUTABLE_PATH")"
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-apphost-verify.XXXXXX")"
cleanup() {
  local original_status=$?
  local final_status="$original_status"

  trap - EXIT

  for attempt in 1 2 3; do
    if [[ ! -e "$WORK" ]]; then
      exit "$final_status"
    fi

    if rm -rf "$WORK" && [[ ! -e "$WORK" ]]; then
      exit "$final_status"
    fi

    if [[ "$attempt" -lt 3 ]]; then
      sleep "0.$attempt"
    fi
  done

  echo "error: could not remove apphost verification temporary directory after three attempts: $WORK (original status: $original_status)" >&2

  if [[ "$original_status" -eq 0 ]]; then
    final_status=1
  fi

  exit "$final_status"
}
trap cleanup EXIT

TEST_BUILD_LOG="$WORK/test-build.log"
TEST_LOG="$WORK/test.log"
SMOKE_RECEIPT="$WORK/published-session-smoke.receipt"
SMOKE_RECEIPT_FOR_TEST="$SMOKE_RECEIPT"

if [[ "$WINDOWS_HOST" -eq 1 ]]; then
  SMOKE_RECEIPT_FOR_TEST="$(cygpath -w "$SMOKE_RECEIPT")"
fi

reject_warnings() {
  local log="$1"
  local scan_status

  if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$log"; then
    echo "error: warning-free published-apphost verification found warning output in $log" >&2
    exit 1
  else
    scan_status=$?
  fi

  if [[ "$scan_status" -ne 1 ]]; then
    echo "error: published-apphost warning scan failed with exit code $scan_status for $log" >&2
    exit 1
  fi
}

require_exact_receipt() {
  local receipt="$1"
  local expected="$2"
  local description="$3"
  local actual
  local actual_byte_count
  local expected_byte_count

  if [[ ! -f "$receipt" ]]; then
    echo "error: $description did not execute successfully (success receipt was not created)" >&2
    exit 1
  fi

  IFS= read -r actual <"$receipt" || true
  actual_byte_count="$(wc -c <"$receipt" | tr -d '[:space:]')"
  expected_byte_count=$((${#expected} + 1))

  if [[ "$actual" == *$'\r' ]]; then
    actual="${actual%$'\r'}"
    expected_byte_count=$((${#expected} + 2))
  fi

  if [[ "$actual" != "$expected" || "$actual_byte_count" != "$expected_byte_count" ]]; then
    echo "error: $description did not execute successfully (invalid success receipt)" >&2
    exit 1
  fi
}

echo "==> Building the published-apphost smoke harness without warnings"
dotnet build "$TEST_PROJECT" \
  -c "$CONFIGURATION" \
  --artifacts-path "$WORK/test-artifacts" \
  --disable-build-servers \
  -m:1 \
  2>&1 | tee "$TEST_BUILD_LOG"

reject_warnings "$TEST_BUILD_LOG"

rm -f "$SMOKE_RECEIPT"

echo "==> Exercising the exact published apphost through the first-session provider-contract smoke"
ARCANUM_PUBLISHED_EXECUTABLE="$SMOKE_EXECUTABLE" \
ARCANUM_PUBLISHED_SMOKE_RECEIPT="$SMOKE_RECEIPT_FOR_TEST" \
dotnet test "$TEST_PROJECT" \
  -c "$CONFIGURATION" \
  --filter "$TEST_FILTER" \
  --artifacts-path "$WORK/test-artifacts" \
  --disable-build-servers \
  -m:1 \
  --no-build \
  --no-restore \
  2>&1 | tee "$TEST_LOG"

reject_warnings "$TEST_LOG"
require_exact_receipt \
  "$SMOKE_RECEIPT" \
  "$EXPECTED_SMOKE_RECEIPT" \
  "selected smoke test"

echo "==> Exact published apphost is warning-free and its first-session provider contract is healthy"
