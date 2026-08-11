#!/usr/bin/env bash
# Unit tests for verify-aot-il-warnings.sh.
#
# The gate is the only thing standing between a first-party AOT/trim warning and a published
# build, and both of its historic failure modes were silent: a warning classified as third-party
# because an allow-list token appeared in the *file path*, and an "ILC ran" assertion satisfied by
# a different project's publish output. Both are pinned here.
#
# The publish legs are driven through a stub `dotnet` on PATH, so nothing here compiles anything.
# Requires ripgrep, the same as the gate itself.
#
# Usage: verify_aot_il_warnings_test.sh [path-to-verify-aot-il-warnings.sh]

set -uo pipefail

TEST_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="${1:-$TEST_DIR/verify-aot-il-warnings.sh}"

if [[ ! -r "$GATE" ]]; then
  echo "error: gate script not found: $GATE" >&2
  exit 1
fi

if ! command -v rg >/dev/null 2>&1; then
  echo "error: ripgrep is required (brew install ripgrep / apt-get install -y ripgrep)" >&2
  exit 1
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/aot-gate-test.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

FAILED=0

fail() {
  echo "FAIL: $1" >&2
  FAILED=$((FAILED + 1))
}

pass() {
  echo "ok   $1"
}

expect_eq() {
  local name="$1"
  local expected="$2"
  local actual="$3"

  if [[ "$expected" == "$actual" ]]; then
    pass "$name"
  else
    fail "$name — expected [$expected], got [$actual]"
  fi
}

# ---------------------------------------------------------------------------
# Warning classification
# ---------------------------------------------------------------------------

# Counts violations in a log holding exactly the given warning line.
violations_for() {
  local line="$1"
  local log="$WORK/warning.log"

  printf '%s\n' "$line" >"$log"

  (
    # shellcheck disable=SC1090
    source "$GATE" >/dev/null 2>&1
    set +e
    count_il_violations "$log" 2>/dev/null
  )
}

FIRST_PARTY_SERILOG_PATH='/repo/src/RetroDownfall.Arcanum.Infrastructure/Logging/SerilogLogRingBufferSink.cs(42,9): warning IL2026: Using member '"'"'System.Text.Json.JsonSerializer.Serialize(Object, Type)'"'"' which has '"'"'RequiresUnreferencedCodeAttribute'"'"' [/repo/src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj]'

expect_eq \
  "a first-party IL warning is counted even when its file path contains an allow-list token" \
  "1" \
  "$(violations_for "$FIRST_PARTY_SERILOG_PATH")"

FIRST_PARTY_PLAIN='/repo/src/RetroDownfall.Arcanum.Api/Endpoints/ArcanumEndpoints.cs(11,5): warning IL3050: Using member which requires dynamic code [/repo/src/RetroDownfall.Arcanum.Api/RetroDownfall.Arcanum.Api.csproj]'

expect_eq \
  "a plain first-party IL warning is counted" \
  "1" \
  "$(violations_for "$FIRST_PARTY_PLAIN")"

THIRD_PARTY_ILC='ILC : warning IL2026: Microsoft.EntityFrameworkCore.Query.QueryCompiler.Execute<TResult>(Expression): Using member which has '"'"'RequiresUnreferencedCodeAttribute'"'"' [/repo/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj]'

expect_eq \
  "an allow-listed third-party ILC warning is still suppressed" \
  "0" \
  "$(violations_for "$THIRD_PARTY_ILC")"

THIRD_PARTY_SERILOG='ILC : warning IL2026: Serilog.Settings.Configuration.ConfigurationReader.Bind(): Using member which has '"'"'RequiresUnreferencedCodeAttribute'"'"' [/repo/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj]'

expect_eq \
  "an allow-listed Serilog-owned ILC warning is still suppressed" \
  "0" \
  "$(violations_for "$THIRD_PARTY_SERILOG")"

expect_eq \
  "a clean log reports no violations" \
  "0" \
  "$(violations_for 'Build succeeded.')"

# ---------------------------------------------------------------------------
# ILC evidence, per publish leg
# ---------------------------------------------------------------------------

ILC_MARKER='Generating native code'

install_stub_dotnet() {
  local bin="$WORK/bin"

  mkdir -p "$bin"

  cat >"$bin/dotnet" <<'STUB'
#!/usr/bin/env bash
# Stub dotnet: no compilation, just canned publish output.
case "${1:-}" in
  --info)
    echo "  RID:         stub-rid"
    exit 0
    ;;
  msbuild)
    # No colon: an explicitly empty STUB_PUBLISH_AOT is the osx case and must stay empty.
    printf '%s\n' "${STUB_PUBLISH_AOT-true}"
    exit 0
    ;;
  publish)
    if printf '%s\n' "$@" | grep -q "RegexAotSmoke"; then
      cat "$STUB_SMOKE_LOG"
    else
      cat "$STUB_CLI_LOG"
    fi
    exit 0
    ;;
esac
exit 0
STUB

  chmod +x "$bin/dotnet"

  printf '%s' "$bin"
}

STUB_BIN="$(install_stub_dotnet)"

# Runs the gate for one RID under the stub toolchain. Echoes the combined output, then a final
# line "exit=<status>". Never publishes: `dotnet` resolves to the stub.
run_gate_for_rid() {
  local rid="$1"
  local publish_aot="$2"
  local cli_log="$3"
  local smoke_log="$4"

  (
    export PATH="$STUB_BIN:$PATH"
    export STUB_PUBLISH_AOT="$publish_aot"
    export STUB_CLI_LOG="$cli_log"
    export STUB_SMOKE_LOG="$smoke_log"

    # shellcheck disable=SC1090
    source "$GATE" >/dev/null 2>&1
    set +e
    run_single_rid "$rid" 1 2>&1
    echo "exit=$?"
  )
}

CLI_LOG_WITHOUT_ILC="$WORK/cli-no-ilc.log"
printf 'Determining projects to restore...\nBuild succeeded.\n' >"$CLI_LOG_WITHOUT_ILC"

CLI_LOG_WITH_ILC="$WORK/cli-ilc.log"
printf '%s\nBuild succeeded.\n' "$ILC_MARKER" >"$CLI_LOG_WITH_ILC"

SMOKE_LOG_WITH_ILC="$WORK/smoke-ilc.log"
printf '%s\nregex smoke ok\n' "$ILC_MARKER" >"$SMOKE_LOG_WITH_ILC"

# A CLI publish that produced no ILC output must fail on a RID that AOT-compiles, however much
# ILC output the regex smoke project contributed.
OUTPUT="$(run_gate_for_rid linux-x64 true "$CLI_LOG_WITHOUT_ILC" "$SMOKE_LOG_WITH_ILC")"

if [[ "$OUTPUT" == *"exit=0"* ]]; then
  fail "a CLI publish with no ILC output must not pass on an AOT RID (the regex smoke's ILC lines satisfied the check)"
else
  pass "a CLI publish with no ILC output fails on an AOT RID"
fi

# Where the CLI is deliberately not AOT-compiled (osx), the run may pass — but it must not claim
# a closure it never analysed.
OUTPUT="$(run_gate_for_rid osx-arm64 "" "$CLI_LOG_WITHOUT_ILC" "$SMOKE_LOG_WITH_ILC")"

if [[ "$OUTPUT" != *"exit=0"* ]]; then
  fail "a non-AOT RID should still pass the gate: $OUTPUT"
elif [[ "$OUTPUT" != *"no ILC closure analysis"* ]]; then
  fail "a non-AOT RID must report that the CLI closure was not analysed, got: $OUTPUT"
else
  pass "a non-AOT RID passes but reports that no ILC closure analysis ran"
fi

# The ordinary healthy case still passes.
OUTPUT="$(run_gate_for_rid linux-x64 true "$CLI_LOG_WITH_ILC" "$SMOKE_LOG_WITH_ILC")"

if [[ "$OUTPUT" == *"exit=0"* && "$OUTPUT" == *"AOT IL gate passed"* ]]; then
  pass "a publish with ILC output on both legs passes"
else
  fail "a healthy AOT publish should pass: $OUTPUT"
fi

echo
if [[ "$FAILED" -gt 0 ]]; then
  echo "$FAILED test(s) failed" >&2
  exit 1
fi

echo "all verify-aot-il-warnings.sh tests passed"
