#!/usr/bin/env bash
# Opt-in real-model Long Rest qualification for an already-published Native AOT apphost.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_PROJECT="$REPO_ROOT/tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj"
CONFIGURATION=Release
EXECUTABLE=""
CHAT_MODEL="gemma4:e4b"
DEFAULT_MODEL="ornith1.5:35b"
EMBEDDING_MODEL="embeddinggemma"
EMBEDDING_WINDOW="2048"
ENDPOINT="http://127.0.0.1:11434/v1/"
REPORT_DIR=""

usage() {
  cat <<'EOF'
Usage: verify-local-ollama-long-rest.sh --executable <published-apphost> [options]

Local qualification options:
  --executable <path>        Existing Native AOT Arcanum apphost. This wrapper never publishes it.
  --chat-model <tag>         Installed Ollama chat model used as the FastModel, so it serves every Long Rest
                             chat call (default: gemma4:e4b).
  --default-model <tag>      Installed Ollama chat model used as the DefaultModel, so it serves the ordinary
                             Session turn (default: ornith1.5:35b).
  --embedding-model <tag>    Installed Ollama embedding model (default: embeddinggemma).
  --embedding-window <n>     Context window, in tokens, configured on the separate embedding provider entry
                             (default: 2048, the context length embeddinggemma reports).
  --endpoint <uri>           Literal-loopback Ollama /v1 endpoint (default: http://127.0.0.1:11434/v1/).
  --report-dir <directory>   Existing directory that receives the progress log, the summary and the calibration
                             report (text and JSON). Without it they live in the wrapper's temporary directory,
                             which is removed when the wrapper exits (the calibration is still printed).
  --configuration <name>     Managed test-harness configuration (default: Release).

The test creates and removes an isolated temporary Arcanum home. It never reads or writes the
operator's Arcanum configuration, never touches the operating-system keychain, never starts or
stops Ollama, and never publishes a product. It refuses GitHub Actions. It seeds a labelled Saga
corpus, then qualifies a clean Long Rest sweep, a sweep interrupted by a kill -9 of the host and
recovered after a restart, and one ordinary Session turn on the default model, and always prints a
calibration report of the lexical and vector discovery thresholds against that corpus.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --executable)
      EXECUTABLE="${2:?--executable requires a path}"
      shift 2
      ;;
    --chat-model)
      CHAT_MODEL="${2:?--chat-model requires a tag}"
      shift 2
      ;;
    --default-model)
      DEFAULT_MODEL="${2:?--default-model requires a tag}"
      shift 2
      ;;
    --embedding-model)
      EMBEDDING_MODEL="${2:?--embedding-model requires a tag}"
      shift 2
      ;;
    --embedding-window)
      EMBEDDING_WINDOW="${2:?--embedding-window requires a token count}"
      shift 2
      ;;
    --endpoint)
      ENDPOINT="${2:?--endpoint requires a URI}"
      shift 2
      ;;
    --report-dir)
      REPORT_DIR="${2:?--report-dir requires a directory}"
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

WINDOWS_HOST=0
EXECUTABLE_PATH="$EXECUTABLE"

case "$(uname -s)" in
  CYGWIN* | MINGW* | MSYS*)
    WINDOWS_HOST=1

    if ! command -v cygpath >/dev/null 2>&1; then
      echo "error: cygpath is required to run local qualification on Windows" >&2
      exit 2
    fi

    EXECUTABLE_PATH="$(cygpath -u "$EXECUTABLE")"
    ;;
esac

if [[ ! -f "$EXECUTABLE_PATH" || ( "$WINDOWS_HOST" -eq 0 && ! -x "$EXECUTABLE_PATH" ) ]]; then
  echo "error: --executable must name an existing executable apphost: $EXECUTABLE" >&2
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

for model in "$CHAT_MODEL" "$DEFAULT_MODEL" "$EMBEDDING_MODEL"; do
  if [[ ! "$model" =~ ^[A-Za-z0-9][A-Za-z0-9._:/-]*$ ]]; then
    echo "error: model tags must be nonblank Ollama model names without whitespace or control characters" >&2
    exit 2
  fi
done

if [[ ! "$EMBEDDING_WINDOW" =~ ^[1-9][0-9]{0,6}$ ]]; then
  echo "error: --embedding-window must be a positive token count" >&2
  exit 2
fi

if [[ ! "$ENDPOINT" =~ ^http://(127\.0\.0\.1|\[::1\]):[0-9]+/v1/?$ ]]; then
  echo "error: --endpoint must be an HTTP /v1 URI on literal loopback" >&2
  exit 2
fi

REPORT_DIR_PATH=""

if [[ -n "$REPORT_DIR" ]]; then
  REPORT_DIR_PATH="$REPORT_DIR"

  if [[ "$WINDOWS_HOST" -eq 1 ]]; then
    REPORT_DIR_PATH="$(cygpath -u "$REPORT_DIR")"
  fi

  if [[ ! -d "$REPORT_DIR_PATH" || ! -w "$REPORT_DIR_PATH" ]]; then
    echo "error: --report-dir must name an existing writable directory: $REPORT_DIR" >&2
    exit 2
  fi

  REPORT_DIR_PATH="$(cd "$REPORT_DIR_PATH" && pwd -P)"
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-local-ollama-long-rest.XXXXXX")"
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
RESULTS_DIR="$WORK/results"

if [[ -n "$REPORT_DIR_PATH" ]]; then
  RESULTS_DIR="$REPORT_DIR_PATH"
fi

QUALIFICATION_RECEIPT="$WORK/local-ollama-long-rest-qualification.receipt"
EXPECTED_QUALIFICATION_RECEIPT="local-ollama-long-rest-qualification:v1"
QUALIFICATION_RECEIPT_FOR_TEST="$QUALIFICATION_RECEIPT"
RESULTS_DIR_FOR_TEST="$RESULTS_DIR"
EXECUTABLE_FOR_TEST="$EXECUTABLE_PATH"

mkdir -p "$RESULTS_DIR"

if [[ "$WINDOWS_HOST" -eq 1 ]]; then
  QUALIFICATION_RECEIPT_FOR_TEST="$(cygpath -w "$QUALIFICATION_RECEIPT")"
  RESULTS_DIR_FOR_TEST="$(cygpath -w "$RESULTS_DIR")"
  EXECUTABLE_FOR_TEST="$(cygpath -w "$EXECUTABLE_PATH")"
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

echo "==> Running isolated Native AOT + Ollama Long Rest qualification (real models: expect tens of minutes)"
echo "==> Progress is appended to $RESULTS_DIR/progress.log while it runs"
# The one selected test seeds a Saga corpus through the host's own extraction, then spends real-model
# time on a complete sweep, an interrupted sweep that is recovered after a restart, and one turn on
# the default model, so the suite-wide 15 minute hang bound would kill a slow but live local model.
# Ninety minutes still names and kills a test host that has stopped making progress.
ARCANUM_RUN_LOCAL_OLLAMA_LONG_REST_QUALIFICATION=1 \
ARCANUM_PUBLISHED_EXECUTABLE="$EXECUTABLE_FOR_TEST" \
ARCANUM_OLLAMA_LONG_REST_CHAT_MODEL="$CHAT_MODEL" \
ARCANUM_OLLAMA_LONG_REST_DEFAULT_MODEL="$DEFAULT_MODEL" \
ARCANUM_OLLAMA_LONG_REST_EMBEDDING_MODEL="$EMBEDDING_MODEL" \
ARCANUM_OLLAMA_LONG_REST_EMBEDDING_WINDOW="$EMBEDDING_WINDOW" \
ARCANUM_OLLAMA_LONG_REST_ENDPOINT="$ENDPOINT" \
ARCANUM_OLLAMA_LONG_REST_RESULTS="$RESULTS_DIR_FOR_TEST" \
ARCANUM_OLLAMA_LONG_REST_RECEIPT="$QUALIFICATION_RECEIPT_FOR_TEST" \
dotnet test "$TEST_PROJECT" \
  -c "$CONFIGURATION" \
  --blame-hang-timeout 90m \
  --blame-hang-dump-type none \
  --logger "console;verbosity=detailed" \
  --filter 'FullyQualifiedName=RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumOllamaLongRestQualificationTests.Published_native_aot_long_rest_sweeps_recovers_a_killed_host_and_leaves_turns_unharmed' \
  --artifacts-path "$WORK/test-artifacts" \
  --disable-build-servers \
  -m:1 \
  --no-build \
  --no-restore \
  2>&1 | tee "$TEST_LOG"

reject_warnings "$TEST_LOG"

if [[ -f "$RESULTS_DIR/long-rest-calibration.txt" ]]; then
  echo "==> Calibration report"
  cat "$RESULTS_DIR/long-rest-calibration.txt"
fi

if [[ ! -f "$QUALIFICATION_RECEIPT" ]]; then
  echo "error: selected local qualification test did not execute successfully (success receipt was not created)" >&2
  exit 1
fi

IFS= read -r actual_qualification_receipt <"$QUALIFICATION_RECEIPT" || true
qualification_receipt_byte_count="$(wc -c <"$QUALIFICATION_RECEIPT" | tr -d '[:space:]')"
expected_qualification_receipt_byte_count=$((${#EXPECTED_QUALIFICATION_RECEIPT} + 1))

if [[ "$actual_qualification_receipt" == *$'\r' ]]; then
  actual_qualification_receipt="${actual_qualification_receipt%$'\r'}"
  expected_qualification_receipt_byte_count=$((${#EXPECTED_QUALIFICATION_RECEIPT} + 2))
fi

if [[ "$actual_qualification_receipt" != "$EXPECTED_QUALIFICATION_RECEIPT" \
  || "$qualification_receipt_byte_count" != "$expected_qualification_receipt_byte_count" ]]; then
  echo "error: selected local qualification test did not execute successfully (invalid success receipt)" >&2
  exit 1
fi

echo "==> Native AOT Long Rest sweep, kill and recovery, and an unharmed ordinary turn are healthy"
