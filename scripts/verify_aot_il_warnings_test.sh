#!/usr/bin/env bash
# Unit tests for verify-aot-il-warnings.sh.
#
# The gate is the only thing standing between a first-party AOT/trim warning and a published
# build, and its historic failure modes were silent: warning ownership confused by an allow-list
# token in a file path, ILC evidence borrowed from another publish leg, scans affected by caller
# ripgrep configuration, and incremental reuse that supplied no fresh analysis. All are pinned here.
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

# The ordering bug this pins: il_warning_is_allowed used to run before il_warning_is_first_party,
# so a genuine first-party warning whose *message* happens to name an ALLOWED third-party
# component (here, a first-party call into an EF Core API annotated RequiresUnreferencedCode) was
# silently dropped -- the same bug class FIRST_PARTY_SERILOG_PATH above pins for the file *path*,
# moved to the message instead.
FIRST_PARTY_MESSAGE_NAMES_ALLOWED_COMPONENT='/repo/src/RetroDownfall.Arcanum.Infrastructure/Grimoire/X.cs(10,5): warning IL2026: Using member '"'"'Microsoft.EntityFrameworkCore.Query.QueryCompiler.Execute<TResult>(Expression)'"'"' which has '"'"'RequiresUnreferencedCodeAttribute'"'"' [/repo/src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj]'

expect_eq \
  "a first-party IL warning is counted even when its message names an EF Core member" \
  "1" \
  "$(violations_for "$FIRST_PARTY_MESSAGE_NAMES_ALLOWED_COMPONENT")"

# ILC diagnostics often have no source location. Their trailing project path says only which
# publish produced the diagnostic, not which symbol owns it. A first-party symbol in the message
# must therefore win over a later allow-listed dependency name.
FIRST_PARTY_SOURCELESS_NAMES_ALLOWED_COMPONENT='ILC : warning IL2026: RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Run(): calls Microsoft.EntityFrameworkCore.Query.QueryCompiler.Execute<TResult>(Expression) [/repo/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj]'

expect_eq \
  "a sourceless first-party ILC warning is counted even when its message names an allow-listed dependency" \
  "1" \
  "$(violations_for "$FIRST_PARTY_SOURCELESS_NAMES_ALLOWED_COMPONENT")"

UNKNOWN_SOURCELESS='ILC : warning IL2026: Future.Dependency.DynamicEntryPoint(): requires unreferenced code [/repo/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj]'

expect_eq \
  "an unattributed sourceless ILC warning fails closed until its owner is reviewed" \
  "1" \
  "$(violations_for "$UNKNOWN_SOURCELESS")"

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

# A .cs source location outside RetroDownfall. -- a generated file, or a source-included package
# compiled into one of these projects -- used to be dropped without ever being counted or checked
# against ALLOWED: the .cs branch counted first-party origins and `continue`d on everything else.
# The gate has to fail closed there, or a whole class of origin passes silently.
FOREIGN_CS_ORIGIN='/repo/obj/Generated/Vendor.Serialization.Generated.cs(88,13): warning IL2026: Using member which has '"'"'RequiresUnreferencedCodeAttribute'"'"' [/repo/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj]'

expect_eq \
  "a .cs warning whose path is not first-party and whose owner is not allow-listed is counted" \
  "1" \
  "$(violations_for "$FOREIGN_CS_ORIGIN")"

# The other half of failing closed: the allow list still decides, so a non-first-party .cs origin
# whose message names a component ALLOWED owns is suppressed exactly as its ILC counterpart is.
ALLOWED_FOREIGN_CS_ORIGIN='/repo/obj/Generated/Vendor.Generated.cs(88,13): warning IL2026: Using member '"'"'Microsoft.EntityFrameworkCore.Query.QueryCompiler.Execute<TResult>(Expression)'"'"' which has '"'"'RequiresUnreferencedCodeAttribute'"'"' [/repo/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj]'

expect_eq \
  "a .cs warning outside RetroDownfall. whose message names an allow-listed owner stays suppressed" \
  "0" \
  "$(violations_for "$ALLOWED_FOREIGN_CS_ORIGIN")"

expect_eq \
  "a clean log reports no violations" \
  "0" \
  "$(violations_for 'Build succeeded.')"

# A caller-controlled ripgrep config must not be able to break the gate's scans. In particular,
# ripgrep can report a missing config with the same exit code it uses for "no match", which makes a
# banned-pattern scan look clean unless the gate explicitly disables external configuration.
BROKEN_RG_CONFIG="$WORK/broken-ripgrep-config"
printf '%s\n' '--definitely-not-a-real-ripgrep-option' >"$BROKEN_RG_CONFIG"
CONFIG_PROBE_LOG="$WORK/config-probe.log"
printf 'Build succeeded.\n' >"$CONFIG_PROBE_LOG"

CONFIG_ISOLATED_OUTPUT="$(
  export RIPGREP_CONFIG_PATH="$BROKEN_RG_CONFIG"

  # shellcheck disable=SC1090
  source "$GATE" >/dev/null 2>&1

  if rg_capture 'Build succeeded' "$CONFIG_PROBE_LOG" 2>/dev/null; then
    capture_exit=0
  else
    capture_exit=$?
  fi
  printf '\nexit=%s' "$capture_exit"
)"

expect_eq \
  "gate scans ignore caller ripgrep configuration" \
  $'Build succeeded.\nexit=0' \
  "$CONFIG_ISOLATED_OUTPUT"

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
    if [[ "${STUB_REQUIRE_ISOLATED_ARTIFACTS:-0}" == 1 ]] \
      && ! printf '%s\n' "$@" | grep -qx -- '--artifacts-path'; then
      echo "Publish reused the project's incremental outputs."
      exit 0
    fi

    if printf '%s\n' "$@" | grep -q "RegexAotSmoke"; then
      /bin/cat "$STUB_SMOKE_LOG"
    else
      /bin/cat "$STUB_CLI_LOG"
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

install_failing_cat() {
  local bin="$WORK/failing-cat-bin"

  mkdir -p "$bin"

  cat >"$bin/cat" <<'STUB'
#!/usr/bin/env bash
echo "injected append failure" >&2
exit 86
STUB

  chmod +x "$bin/cat"

  printf '%s' "$bin"
}

FAILING_CAT_BIN="$(install_failing_cat)"

install_failing_cleanup_rm() {
  local bin="$WORK/failing-rm-bin"

  mkdir -p "$bin"

  cat >"$bin/rm" <<'STUB'
#!/usr/bin/env bash
if [[ "${1:-}" == -rf ]]; then
  echo "injected cleanup failure" >&2
  exit 87
fi

exec /bin/rm "$@"
STUB

  chmod +x "$bin/rm"

  printf '%s' "$bin"
}

FAILING_RM_BIN="$(install_failing_cleanup_rm)"

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

# A shipping RID that resolves the CLI away from AOT must fail closed. There is no managed or
# analyzer-only shipping shape.
OUTPUT="$(run_gate_for_rid osx-arm64 "" "$CLI_LOG_WITHOUT_ILC" "$SMOKE_LOG_WITH_ILC")"

if [[ "$OUTPUT" == *"exit=0"* ]]; then
  fail "a shipping RID whose CLI is not Native AOT must fail: $OUTPUT"
elif [[ "$OUTPUT" != *"PublishAot resolved off"* ]]; then
  fail "a non-AOT shipping RID must name the failed invariant, got: $OUTPUT"
else
  pass "a non-AOT shipping RID fails closed"
fi

# The ordinary healthy case still passes.
OUTPUT="$(run_gate_for_rid linux-x64 true "$CLI_LOG_WITH_ILC" "$SMOKE_LOG_WITH_ILC")"

if [[ "$OUTPUT" == *"exit=0"* && "$OUTPUT" == *"AOT IL gate passed"* ]]; then
  pass "a publish with ILC output on both legs passes"
else
  fail "a healthy AOT publish should pass: $OUTPUT"
fi

# The outer RID loop intentionally disables errexit so it can report every matrix leg. A failed
# append must therefore be checked explicitly: otherwise the complete per-leg logs pass their ILC
# checks while the combined warning log stays empty and the warning scan reports a false green.
OUTPUT="$(
  export PATH="$FAILING_CAT_BIN:$STUB_BIN:$PATH"
  export STUB_PUBLISH_AOT=true
  export STUB_CLI_LOG="$CLI_LOG_WITH_ILC"
  export STUB_SMOKE_LOG="$SMOKE_LOG_WITH_ILC"

  # shellcheck disable=SC1090
  source "$GATE" >/dev/null 2>&1
  set +e
  run_single_rid linux-x64 1 2>&1
  echo "exit=$?"
)"

if [[ "$OUTPUT" == *"exit=0"* ]]; then
  fail "a failed combined-log append must fail the RID instead of scanning an incomplete log: $OUTPUT"
elif [[ "$OUTPUT" != *"could not append CLI publish output"* ]]; then
  fail "a failed combined-log append must name the incomplete evidence, got: $OUTPUT"
else
  pass "a failed combined-log append fails closed"
fi

OUTPUT="$(
  export PATH="$FAILING_RM_BIN:$STUB_BIN:$PATH"
  export STUB_PUBLISH_AOT=true
  export STUB_CLI_LOG="$CLI_LOG_WITH_ILC"
  export STUB_SMOKE_LOG="$SMOKE_LOG_WITH_ILC"

  # shellcheck disable=SC1090
  source "$GATE" >/dev/null 2>&1
  set +e
  run_single_rid linux-x64 1 2>&1
  echo "exit=$?"
)"

if [[ "$OUTPUT" == *"exit=0"* ]]; then
  fail "a failed private-artifact cleanup must fail the RID instead of leaking its temporary tree: $OUTPUT"
elif [[ "$OUTPUT" != *"could not remove CLI publish artifacts"* ]]; then
  fail "a failed private-artifact cleanup must name the leaked evidence, got: $OUTPUT"
else
  pass "a failed private-artifact cleanup fails closed"
fi

# A real second publish can otherwise reuse both native outputs and emit no ILC marker, making the
# gate fail closed without actually performing the analysis it was invoked to verify. Fresh artifact
# roots force both legs through their build and native-analysis pipelines without deleting repo output.
export STUB_REQUIRE_ISOLATED_ARTIFACTS=1

OUTPUT="$(run_gate_for_rid linux-x64 true "$CLI_LOG_WITH_ILC" "$SMOKE_LOG_WITH_ILC")"

unset STUB_REQUIRE_ISOLATED_ARTIFACTS

if [[ "$OUTPUT" == *"exit=0"* && "$OUTPUT" == *"AOT IL gate passed"* ]]; then
  pass "both publish legs use isolated artifact roots"
else
  fail "the AOT audit must isolate both publish legs from incremental outputs: $OUTPUT"
fi

echo
if [[ "$FAILED" -gt 0 ]]; then
  echo "$FAILED test(s) failed" >&2
  exit 1
fi

echo "all verify-aot-il-warnings.sh tests passed"
