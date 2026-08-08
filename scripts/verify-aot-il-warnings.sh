#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

PROJECT="$ROOT/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"

REGEX_SMOKE_PROJECT="$ROOT/tests/RetroDownfall.Arcanum.RegexAotSmoke/RetroDownfall.Arcanum.RegexAotSmoke.csproj"

DEFAULT_RIDS=(osx-arm64 osx-x64 linux-x64 win-x64)

ALLOWED=(
  'Microsoft.EntityFrameworkCore'
  'Serilog'
  'Microsoft.AspNetCore.Mvc'
  'SpatialiteLoader'
  'DependencyContext'
  'ld: warning'
)

usage() {
  cat <<'EOF'
Verify Native AOT publish IL warnings for RetroDownfall.Arcanum.Cli and publish/run
the runtime-regex smoke executable.

Usage:
  verify-aot-il-warnings.sh [RID|all] [options]

Arguments:
  RID                 Runtime identifier (default: current host RID)
  all                 Run the primary RID matrix: osx-arm64 osx-x64 linux-x64 win-x64

Options:
  --force             Attempt publish even when the host OS cannot link this RID
  --strict            Exit non-zero when any RID is skipped (CI matrix mode)
  -h, --help          Show this help

Exit codes:
  0  All attempted RIDs passed (skips are OK unless --strict)
  1  At least one attempted RID failed (publish or IL gate)

Skips are chosen from the host OS:
  darwin  → builds osx-* only
  linux   → builds linux-* only
  win     → builds win-* only

Cross-OS targets are skipped with a platform note instead of a link failure.
EOF
}

require_cmd() {
  local cmd="$1"
  local hint="${2:-}"

  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "error: required command not found: $cmd" >&2

    if [[ -n "$hint" ]]; then
      echo "  $hint" >&2
    fi

    exit 1
  fi
}

host_os() {
  case "$(uname -s)" in
    Darwin) echo darwin ;;
    Linux) echo linux ;;
    MINGW* | MSYS* | CYGWIN*) echo win ;;
    *) echo unknown ;;
  esac
}

host_rid() {
  dotnet --info | awk '/RID:/{print $2; exit}'
}

rid_os_family() {
  local rid="$1"

  case "$rid" in
    osx-* | maccatalyst-* | ios-* | tvos-*)
      echo darwin
      ;;
    linux-* | alpine-* | android-*)
      echo linux
      ;;
    win-*)
      echo win
      ;;
    *)
      echo unknown
      ;;
  esac
}

skip_reason_for_rid() {
  local rid="$1"
  local host
  host="$(host_os)"
  local family
  family="$(rid_os_family "$rid")"

  if [[ "$family" == unknown ]]; then
    echo "unknown RID '$rid' — expected osx-*, linux-*, or win-*"
    return 0
  fi

  if [[ "$host" == unknown ]]; then
    echo "unrecognized host OS '$(uname -s)' — cannot determine cross-compile support"
    return 0
  fi

  if [[ "$host" != "$family" ]]; then
    case "$family" in
      darwin)
        echo "requires a macOS host (Cross-OS native compilation is not supported from $(uname -s))"
        ;;
      linux)
        echo "requires a Linux host (Cross-OS native compilation is not supported from $(uname -s))"
        ;;
      win)
        echo "requires a Windows host (Cross-OS native compilation is not supported from $(uname -s))"
        ;;
    esac
    return 0
  fi

  return 1
}

explain_publish_failure() {
  local rid="$1"
  local log="$2"

  if rg -q "Cross-OS native compilation is not supported" "$log"; then
    echo "  Publish failed: Cross-OS native compilation is not supported." >&2
    echo "  Build $rid on a $(rid_os_family "$rid") host or in CI (e.g. GitHub Actions)." >&2
    return
  fi

  if rg -q "llvm-objcopy|objcopy.*not found|Symbol stripping tool" "$log"; then
    echo "  Publish failed: symbol stripping tool (llvm-objcopy or objcopy) not found in PATH." >&2
    echo "  Install llvm/binutils (e.g. apt install llvm) or publish with -p:StripSymbols=false." >&2
    return
  fi

  if rg -q "invalid linker name.*-fuse-ld=bfd|fuse-ld=bfd" "$log"; then
    echo "  Publish failed: Linux linker (-fuse-ld=bfd) is unavailable on this host." >&2
    echo "  Build $rid on Linux (native or CI) — macOS hosts cannot complete the Linux AOT link step." >&2
    return
  fi

  echo "  Publish failed. Last lines from the log:" >&2
  tail -n 8 "$log" | sed 's/^/    /' >&2
}

publish_cli_rid() {
  local rid="$1"
  local log="$2"
  local -a publish_args=(
    publish "$PROJECT"
    -c Release
    -r "$rid"
  )

  echo "  Publishing $rid via Native AOT — this performs native compilation and can take several minutes..." >&2

  # Stream publish output to the screen while capturing it for IL-warning analysis.
  # pipefail (set at top) makes the pipeline surface dotnet's exit status, not tee's.
  if dotnet "${publish_args[@]}" 2>&1 | tee "$log"; then
    return 0
  fi

  if rg -q "llvm-objcopy|objcopy.*not found|Symbol stripping tool" "$log"; then
    echo "  Symbol stripper missing; retrying $rid with StripSymbols=false..." >&2

    if dotnet "${publish_args[@]}" -p:StripSymbols=false 2>&1 | tee "$log"; then
      echo "  Publish succeeded after disabling symbol stripping." >&2
      return 0
    fi
  fi

  explain_publish_failure "$rid" "$log"
  return 1
}

publish_regex_smoke_rid() {
  local rid="$1"
  local log="$2"
  local output
  output="$(mktemp -d)"
  local -a publish_args=(
    publish "$REGEX_SMOKE_PROJECT"
    -c Release
    -r "$rid"
    -o "$output"
  )

  echo "  Publishing runtime-regex Native-AOT smoke for $rid..." >&2

  if ! dotnet "${publish_args[@]}" 2>&1 | tee -a "$log"; then
    if rg -q "llvm-objcopy|objcopy.*not found|Symbol stripping tool" "$log"; then
      echo "  Symbol stripper missing; retrying regex smoke with StripSymbols=false..." >&2

      if ! dotnet "${publish_args[@]}" -p:StripSymbols=false 2>&1 | tee -a "$log"; then
        rm -rf "$output"
        explain_publish_failure "$rid" "$log"
        return 1
      fi
    else
      rm -rf "$output"
      explain_publish_failure "$rid" "$log"
      return 1
    fi
  fi

  if [[ "$rid" == "$(host_rid)" ]]; then
    local executable="$output/RetroDownfall.Arcanum.RegexAotSmoke"

    if [[ "$rid" == win-* ]]; then
      executable="$executable.exe"
    fi

    echo "  Running runtime-regex Native-AOT smoke for $rid..." >&2

    if ! "$executable" 2>&1 | tee -a "$log"; then
      rm -rf "$output"
      echo "  Runtime-regex Native-AOT smoke failed for $rid." >&2
      return 1
    fi
  else
    echo "  Regex smoke published but not run because $rid is not the host RID." >&2
  fi

  rm -rf "$output"
  return 0
}

publish_rid() {
  local rid="$1"
  local log="$2"

  publish_cli_rid "$rid" "$log" \
    && publish_regex_smoke_rid "$rid" "$log"
}

# ripgrep exits 1 for "no match" and >1 for a real failure (missing file, bad pattern, and
# 127 when rg itself is absent). A process substitution's exit status is never propagated to
# the reading loop, so capture the output here and inspect the status explicitly — otherwise
# a failed scan is indistinguishable from a clean publish and the gate reports PASS.
rg_capture() {
  local pattern="$1"
  shift

  local out
  local status

  set +e
  out="$(rg "$pattern" "$@")"
  status=$?
  set -e

  if [[ "$status" -gt 1 ]]; then
    echo "  ripgrep failed (exit $status) while scanning: $*" >&2
    return 1
  fi

  printf '%s' "$out"
  return 0
}

# An empty or truncated publish log looks exactly like a clean AOT publish to the warning
# scanner, so require positive evidence that ILC actually ran before trusting a zero count.
assert_log_has_ilc_output() {
  local log="$1"
  local rid="$2"

  if [[ ! -r "$log" || ! -s "$log" ]]; then
    echo "  Publish log for RID $rid is empty or unreadable: $log" >&2
    return 1
  fi

  local markers

  if ! markers="$(rg_capture "Generating native code|ILC :|ilc\.rsp" "$log")"; then
    return 1
  fi

  if [[ -z "$markers" ]]; then
    echo "  Publish log for RID $rid contains no ILC output; refusing to report a pass." >&2
    return 1
  fi

  return 0
}

count_il_violations() {
  local log="$1"
  local violations=0
  local line
  local skip
  local matches

  if ! matches="$(rg_capture "warning IL[0-9]{4}|ILC :" "$log")"; then
    return 1
  fi

  while IFS= read -r line; do
    if [[ "$line" =~ warning\ IL[0-9]{4}|ILC\ :\ (warning\ )?IL[0-9]{4} ]]; then
      skip=0

      for allowed in "${ALLOWED[@]}"; do
        if [[ "$line" == *"$allowed"* ]]; then
          skip=1
          break
        fi
      done

      if [[ "$skip" -eq 0 && "$line" == *"RetroDownfall.Arcanum"* ]]; then
        echo "$line" >&2
        violations=$((violations + 1))
      fi
    fi
  done <<<"$matches"

  echo "$violations"
  return 0
}

check_nowarn_banned() {
  local matches

  if ! matches="$(rg_capture "IlcArg.*--nowarn" "$PROJECT" "$REGEX_SMOKE_PROJECT")"; then
    echo "AOT IL gate failed: could not scan the project files for banned IlcArg --nowarn" >&2
    return 1
  fi

  if [[ -n "$matches" ]]; then
    echo "AOT IL gate failed: blanket IlcArg --nowarn is not permitted" >&2
    return 1
  fi

  return 0
}

run_single_rid() {
  local rid="$1"
  local force="${2:-0}"
  local reason=""

  echo "========== RID: $rid (host: $(host_rid), OS: $(host_os)) =========="

  if reason="$(skip_reason_for_rid "$rid")"; then
    if [[ "$force" -eq 1 ]]; then
      echo "SKIP expected ($reason) — attempting anyway due to --force" >&2
    else
      echo "SKIP: $reason"
      echo
      return 2
    fi
  fi

  local log
  log="$(mktemp)"

  if ! publish_rid "$rid" "$log"; then
    rm -f "$log"
    echo
    return 1
  fi

  if ! assert_log_has_ilc_output "$log" "$rid"; then
    rm -f "$log"
    echo "AOT IL gate failed: cannot verify RID $rid" >&2
    echo
    return 1
  fi

  local violations

  if ! violations="$(count_il_violations "$log")"; then
    rm -f "$log"
    echo "AOT IL gate failed: cannot scan the publish log for RID $rid" >&2
    echo
    return 1
  fi

  rm -f "$log"

  if [[ "$violations" -gt 0 ]]; then
    echo "AOT IL gate failed: $violations unapproved first-party IL warning(s) on RID $rid" >&2
    echo
    return 1
  fi

  echo "AOT IL gate passed for RID $rid"
  echo
  return 0
}

main() {
  local target=""
  local force=0
  local strict=0

  while [[ $# -gt 0 ]]; do
    case "$1" in
      -h | --help)
        usage
        exit 0
        ;;
      --force)
        force=1
        shift
        ;;
      --strict)
        strict=1
        shift
        ;;
      all)
        target=all
        shift
        ;;
      -*)
        echo "Unknown option: $1" >&2
        usage >&2
        exit 1
        ;;
      *)
        if [[ -n "$target" && "$target" != all ]]; then
          echo "Unexpected extra argument: $1" >&2
          usage >&2
          exit 1
        fi

        target="$1"
        shift
        ;;
    esac
  done

  # Fail closed on a missing toolchain: every scan in this script goes through ripgrep, and
  # a missing rg would otherwise make the gate report PASS while verifying nothing.
  require_cmd dotnet "Install the .NET SDK (https://dotnet.microsoft.com/download)."
  require_cmd rg "Install ripgrep (brew install ripgrep / apt-get install -y ripgrep)."

  if [[ -z "$target" ]]; then
    target="$(host_rid)"
  fi

  if ! check_nowarn_banned; then
    exit 1
  fi

  local -a rids=()

  if [[ "$target" == all ]]; then
    rids=("${DEFAULT_RIDS[@]}")
  else
    rids=("$target")
  fi

  local passed=0
  local skipped=0
  local failed=0
  local rid
  local status
  local -a pass_rids=()
  local -a skip_rids=()
  local -a fail_rids=()

  for rid in "${rids[@]}"; do
    set +e
    run_single_rid "$rid" "$force"
    status=$?
    set -e

    case "$status" in
      0)
        passed=$((passed + 1))
        pass_rids+=("$rid")
        ;;
      2)
        skipped=$((skipped + 1))
        skip_rids+=("$rid")
        ;;
      *)
        failed=$((failed + 1))
        fail_rids+=("$rid")
        ;;
    esac
  done

  if [[ "${#rids[@]}" -gt 1 ]]; then
    echo "========== AOT IL gate summary (host: $(host_rid), OS: $(host_os)) =========="

    for rid in "${pass_rids[@]+"${pass_rids[@]}"}"; do
      echo "PASS   $rid"
    done

    for rid in "${skip_rids[@]+"${skip_rids[@]}"}"; do
      reason="$(skip_reason_for_rid "$rid")"
      echo "SKIP   $rid — $reason"
    done

    for rid in "${fail_rids[@]+"${fail_rids[@]}"}"; do
      echo "FAIL   $rid"
    done

    echo
    echo "$passed passed, $skipped skipped, $failed failed"
  fi

  if [[ "$failed" -gt 0 ]]; then
    exit 1
  fi

  if [[ "$strict" -eq 1 && "$skipped" -gt 0 ]]; then
    echo "Strict mode: $skipped RID(s) were skipped on this host." >&2
    exit 1
  fi

  exit 0
}

main "$@"
