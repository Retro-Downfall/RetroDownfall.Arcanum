#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

PROJECT="$ROOT/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"

DEFAULT_RIDS=(osx-arm64 osx-x64 linux-x64 win-x64)

ALLOWED=(
  'Spectre.Console.Cli'
  'Microsoft.EntityFrameworkCore'
  'Serilog'
  'Microsoft.AspNetCore.Mvc'
  'SpatialiteLoader'
  'DependencyContext'
  'ld: warning'
)

usage() {
  cat <<'EOF'
Verify Native AOT publish IL warnings for RetroDownfall.Arcanum.Cli.

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

publish_rid() {
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

count_il_violations() {
  local log="$1"
  local violations=0
  local line
  local skip

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
  done < <(rg "warning IL[0-9]{4}|ILC :" "$log" || true)

  echo "$violations"
}

check_nowarn_banned() {
  if rg -q "IlcArg.*--nowarn" "$PROJECT"; then
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

  local violations
  violations="$(count_il_violations "$log")"
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
