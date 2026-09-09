#!/usr/bin/env bash
# Opt-in, local-only real-model qualification for an already-published Native AOT apphost.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_PROJECT="$REPO_ROOT/tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj"
CONFIGURATION=Release
EXECUTABLE=""
IMAGE=""
MODEL="gemma4:e4b"
ENDPOINT="http://127.0.0.1:11434/v1/"
EXPECTED_IMAGE_SHA256="88d2994d07000a2c3bb31f307c536c6a9c45731d9d002c33bd8407817f269cb0"

usage() {
  cat <<'EOF'
Usage: verify-local-ollama-aot.sh --executable <published-apphost> --image <jpeg> [options]

Local-only options:
  --executable <path>    Existing Native AOT Arcanum apphost. This wrapper never publishes it.
  --image <path>         The qualified stop-sign JPEG (exact SHA-256 is checked).
  --model <tag>          Installed Ollama vision model (default: gemma4:e4b).
  --endpoint <uri>       Literal-loopback Ollama /v1 endpoint (default: http://127.0.0.1:11434/v1/).
  --configuration <name> Managed test-harness configuration (default: Release).

The test creates and removes an isolated temporary Arcanum home. It never reads or writes the
operator's Arcanum configuration, never starts or stops Ollama, and never publishes a product.
It refuses GitHub Actions, proves three persisted turns, attachment v1/v2 replacement across an
Arcanum host restart, latest-version selection, and the fixed JPEG vision fixture. It does not claim
in-place transcript or memory editing.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --executable)
      EXECUTABLE="${2:?--executable requires a path}"
      shift 2
      ;;
    --image)
      IMAGE="${2:?--image requires a path}"
      shift 2
      ;;
    --model)
      MODEL="${2:?--model requires a tag}"
      shift 2
      ;;
    --endpoint)
      ENDPOINT="${2:?--endpoint requires a URI}"
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

if [[ "${GITHUB_ACTIONS:-}" =~ ^([Tt][Rr][Uu][Ee]|1)$ ]]; then
  echo "error: real-model qualification is local-only and cannot run in GitHub Actions" >&2
  exit 2
fi

for command in dotnet rg shasum; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "error: required command not found: $command" >&2
    exit 2
  fi
done

if [[ -z "$EXECUTABLE" ]]; then
  echo "error: --executable is required" >&2
  exit 2
fi

if [[ -z "$IMAGE" ]]; then
  echo "error: --image is required" >&2
  exit 2
fi

WINDOWS_HOST=0
EXECUTABLE_PATH="$EXECUTABLE"
IMAGE_PATH="$IMAGE"

case "$(uname -s)" in
  CYGWIN* | MINGW* | MSYS*)
    WINDOWS_HOST=1

    if ! command -v cygpath >/dev/null 2>&1; then
      echo "error: cygpath is required to run local qualification on Windows" >&2
      exit 2
    fi

    EXECUTABLE_PATH="$(cygpath -u "$EXECUTABLE")"
    IMAGE_PATH="$(cygpath -u "$IMAGE")"
    ;;
esac

if [[ ! -f "$EXECUTABLE_PATH" || ( "$WINDOWS_HOST" -eq 0 && ! -x "$EXECUTABLE_PATH" ) ]]; then
  echo "error: --executable must name an existing executable apphost: $EXECUTABLE" >&2
  exit 2
fi

if [[ ! -f "$IMAGE_PATH" || ! -r "$IMAGE_PATH" ]]; then
  echo "error: --image must name a readable JPEG: $IMAGE" >&2
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
IMAGE_PATH="$(canonical_existing_file "$IMAGE_PATH")"

IMAGE_SHA256="$(shasum -a 256 "$IMAGE_PATH" | awk '{ print $1 }')"
if [[ "$IMAGE_SHA256" != "$EXPECTED_IMAGE_SHA256" ]]; then
  echo "error: --image is not the qualified stop-sign fixture (expected SHA-256 $EXPECTED_IMAGE_SHA256)" >&2
  exit 2
fi

if [[ -z "$MODEL" ]]; then
  echo "error: --model must not be empty" >&2
  exit 2
fi

if [[ ! "$ENDPOINT" =~ ^http://(127\.0\.0\.1|\[::1\]):[0-9]+/v1/?$ ]]; then
  echo "error: --endpoint must be an HTTP /v1 URI on literal loopback" >&2
  exit 2
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-local-ollama-aot.XXXXXX")"
cleanup() {
  local original_status=$?
  local final_status="$original_status"

  trap - EXIT

  for attempt in 1 2 3; do
    if [[ ! -e "$WORK" ]] || { rm -rf "$WORK" && [[ ! -e "$WORK" ]]; }; then
      exit "$final_status"
    fi

    if [[ "$attempt" -lt 3 ]]; then
      sleep "0.$attempt"
    fi
  done

  echo "error: could not remove local qualification temporary directory after three attempts: $WORK (original status: $original_status)" >&2

  if [[ "$original_status" -eq 0 ]]; then
    final_status=1
  fi

  exit "$final_status"
}
trap cleanup EXIT

BUILD_LOG="$WORK/test-build.log"
TEST_LOG="$WORK/test.log"
QUALIFICATION_RECEIPT="$WORK/local-ollama-aot-qualification.receipt"
EXPECTED_QUALIFICATION_RECEIPT="local-ollama-aot-qualification:v1"
QUALIFICATION_RECEIPT_FOR_TEST="$QUALIFICATION_RECEIPT"
EXECUTABLE_FOR_TEST="$EXECUTABLE_PATH"
IMAGE_FOR_TEST="$IMAGE_PATH"

if [[ "$WINDOWS_HOST" -eq 1 ]]; then
  QUALIFICATION_RECEIPT_FOR_TEST="$(cygpath -w "$QUALIFICATION_RECEIPT")"
  EXECUTABLE_FOR_TEST="$(cygpath -w "$EXECUTABLE_PATH")"
  IMAGE_FOR_TEST="$(cygpath -w "$IMAGE_PATH")"
fi

reject_warnings() {
  local log="$1"
  local scan_status

  if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$log"; then
    echo "error: local qualification found warning output in $log" >&2
    exit 1
  else
    scan_status=$?
  fi

  if [[ "$scan_status" -ne 1 ]]; then
    echo "error: local qualification warning scan failed with exit code $scan_status for $log" >&2
    exit 1
  fi
}

echo "==> Building the managed local-qualification harness without warnings"
dotnet build "$TEST_PROJECT" \
  -c "$CONFIGURATION" \
  --artifacts-path "$WORK/test-artifacts" \
  --disable-build-servers \
  -m:1 \
  2>&1 | tee "$BUILD_LOG"

reject_warnings "$BUILD_LOG"

rm -f "$QUALIFICATION_RECEIPT"

echo "==> Running isolated Native AOT + Ollama qualification (cold turns may take up to 3 minutes)"
ARCANUM_RUN_LOCAL_OLLAMA_AOT_QUALIFICATION=1 \
ARCANUM_PUBLISHED_EXECUTABLE="$EXECUTABLE_FOR_TEST" \
ARCANUM_OLLAMA_QUALIFICATION_IMAGE="$IMAGE_FOR_TEST" \
ARCANUM_OLLAMA_QUALIFICATION_MODEL="$MODEL" \
ARCANUM_OLLAMA_QUALIFICATION_ENDPOINT="$ENDPOINT" \
ARCANUM_OLLAMA_QUALIFICATION_RECEIPT="$QUALIFICATION_RECEIPT_FOR_TEST" \
dotnet test "$TEST_PROJECT" \
  -c "$CONFIGURATION" \
  --filter 'FullyQualifiedName=RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumOllamaQualificationTests.Published_native_aot_preserves_corrected_file_context_and_runs_vision_across_restart' \
  --artifacts-path "$WORK/test-artifacts" \
  --disable-build-servers \
  -m:1 \
  --no-build \
  --no-restore \
  2>&1 | tee "$TEST_LOG"

reject_warnings "$TEST_LOG"

if [[ ! -f "$QUALIFICATION_RECEIPT" ]]; then
  echo "error: selected local qualification test did not execute successfully (success receipt was not created)" >&2
  exit 1
fi

IFS= read -r actual_qualification_receipt <"$QUALIFICATION_RECEIPT" || true
actual_qualification_receipt="${actual_qualification_receipt%$'\r'}"
qualification_receipt_byte_count="$(wc -c <"$QUALIFICATION_RECEIPT" | tr -d '[:space:]')"
expected_qualification_receipt_byte_count=$((${#EXPECTED_QUALIFICATION_RECEIPT} + 1))

if [[ "$actual_qualification_receipt" != "$EXPECTED_QUALIFICATION_RECEIPT" \
  || "$qualification_receipt_byte_count" != "$expected_qualification_receipt_byte_count" ]]; then
  echo "error: selected local qualification test did not execute successfully (invalid success receipt)" >&2
  exit 1
fi

echo "==> Native AOT first-session, durable correction, attachment history, restart, and vision are healthy"
