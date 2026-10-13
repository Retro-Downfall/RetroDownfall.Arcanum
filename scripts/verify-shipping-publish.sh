#!/usr/bin/env bash
# Publish the Native AOT Arcanum host and exercise its first persisted provider-contract turn.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"
CONFIGURATION=Release
RID=""
RETAIN_PUBLISH_DIR=""
RETAIN_OWNED=0
VERIFICATION_COMPLETE=0

usage() {
  cat <<'EOF'
Usage: verify-shipping-publish.sh [--rid <shipping-rid>] [--configuration <name>] [--retain-publish-dir <directory>]

Publishes the self-contained Native AOT Arcanum host, fails on any publish warning, then delegates
the exact published apphost to the reusable deterministic in-process provider stub gate.
This CI-safe contract smoke never contacts Ollama or any other real inference model.
With --retain-publish-dir, copies the validated whole publish closure to a fresh leaf under an
existing parent. Reports retained success only after private temporary cleanup succeeds.
Without this option, all publish contents are removed after verification.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      RID="${2:?}"
      shift 2
      ;;
    --configuration)
      CONFIGURATION="${2:?}"
      shift 2
      ;;
    --retain-publish-dir)
      RETAIN_PUBLISH_DIR="${2:?--retain-publish-dir requires a directory}"
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

if [[ -n "$RETAIN_PUBLISH_DIR" ]]; then
  RETAIN_PARENT="$(dirname "$RETAIN_PUBLISH_DIR")"
  RETAIN_LEAF="$(basename "$RETAIN_PUBLISH_DIR")"

  if ! RETAIN_PARENT="$(cd "$RETAIN_PARENT" && pwd -P)"; then
    echo "error: --retain-publish-dir requires an existing parent directory" >&2
    exit 2
  fi

  RETAIN_PUBLISH_DIR="${RETAIN_PARENT%/}/$RETAIN_LEAF"

  if [[ -e "$RETAIN_PUBLISH_DIR" || -L "$RETAIN_PUBLISH_DIR" ]]; then
    echo "error: --retain-publish-dir must name a fresh leaf: $RETAIN_PUBLISH_DIR" >&2
    exit 2
  fi
fi

for command in dotnet rg; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "error: required command not found: $command" >&2
    exit 2
  fi
done

if [[ -z "$RID" ]]; then
  RID="$(dotnet --info | awk '$1 == "RID:" { print $2; exit }')"
fi

case "$RID" in
  osx-arm64 | win-x64 | win-arm64)
    ;;
  *)
    echo "error: '$RID' is not an Arcanum shipping RID" >&2
    exit 2
    ;;
esac

WORK="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-shipping-verify.XXXXXX")"
cleanup() {
  local original_status=$?
  local final_status="$original_status"

  trap - EXIT

  for attempt in 1 2 3; do
    if [[ ! -e "$WORK" ]]; then
      break
    fi

    if rm -rf "$WORK" && [[ ! -e "$WORK" ]]; then
      break
    fi

    if [[ "$attempt" -lt 3 ]]; then
      sleep "0.$attempt"
    fi
  done

  if [[ -e "$WORK" ]]; then
    echo "error: could not remove shipping verification temporary directory after three attempts: $WORK (original status: $original_status)" >&2

    if [[ "$final_status" -eq 0 ]]; then
      final_status=1
    fi
  fi

  if [[ "$VERIFICATION_COMPLETE" -ne 1 && "$final_status" -eq 0 ]]; then
    final_status=1
  fi

  if [[ "$RETAIN_OWNED" -eq 1 && "$final_status" -ne 0 ]]; then
    for attempt in 1 2 3; do
      if [[ ! -e "$RETAIN_PUBLISH_DIR" && ! -L "$RETAIN_PUBLISH_DIR" ]]; then
        break
      fi

      if rm -rf "$RETAIN_PUBLISH_DIR" && [[ ! -e "$RETAIN_PUBLISH_DIR" && ! -L "$RETAIN_PUBLISH_DIR" ]]; then
        break
      fi

      if [[ "$attempt" -lt 3 ]]; then
        sleep "0.$attempt"
      fi
    done

    if [[ -e "$RETAIN_PUBLISH_DIR" || -L "$RETAIN_PUBLISH_DIR" ]]; then
      echo "error: could not remove owned retained publish after three attempts; unusable failed-run artifact: $RETAIN_PUBLISH_DIR (original status: $original_status)" >&2
    fi
  fi

  if [[ "$final_status" -eq 0 ]]; then
    if [[ "$RETAIN_OWNED" -eq 1 ]]; then
      echo "==> Retained qualified publish: $RETAIN_PUBLISH_DIR"
    fi

    echo "==> Shipping publish is Native AOT, warning-free, and its first-session provider contract is healthy"
  fi

  exit "$final_status"
}
trap cleanup EXIT

WORK_ROOT="$(cd "$WORK" && pwd -P)"

case "$RETAIN_PUBLISH_DIR" in
  "$WORK_ROOT" | "$WORK_ROOT"/*)
    echo "error: --retain-publish-dir must be outside the private temporary tree: $RETAIN_PUBLISH_DIR" >&2
    exit 2
    ;;
esac

PUBLISH_DIR="$WORK/publish"
PUBLISH_LOG="$WORK/publish.log"

resolved_property() {
  dotnet msbuild "$PROJECT" \
    -nologo \
    -getProperty:"$1" \
    -p:Configuration="$CONFIGURATION" \
    -p:RuntimeIdentifier="$RID" \
    | tr -d '\r'
}

require_property() {
  local property="$1"
  local expected="$2"
  local actual

  actual="$(resolved_property "$property")"

  if [[ "$actual" != "$expected" ]]; then
    echo "error: $property resolved to '$actual' for $RID; expected '$expected'" >&2
    exit 1
  fi
}

reject_warnings() {
  local log="$1"
  local scan_status

  if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$log"; then
    echo "error: warning-free shipping verification found warning output in $log" >&2
    exit 1
  else
    scan_status=$?
  fi

  if [[ "$scan_status" -ne 1 ]]; then
    echo "error: shipping warning scan failed with exit code $scan_status for $log" >&2
    exit 1
  fi
}

require_property PublishAot true
require_property SelfContained true
require_property PublishSingleFile false

echo "==> Publishing warning-free Native AOT Arcanum ($RID)"
if dotnet publish "$PROJECT" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  --artifacts-path "$WORK/publish-artifacts" \
  --disable-build-servers \
  -m:1 \
  -o "$PUBLISH_DIR" \
  2>&1 | tee "$PUBLISH_LOG"; then
  :
else
  exit "$?"
fi

reject_warnings "$PUBLISH_LOG"

PUBLISHED_NAME="RetroDownfall.Arcanum.Cli"

if [[ "$RID" == win-* ]]; then
  PUBLISHED_NAME="$PUBLISHED_NAME.exe"
fi

EXECUTABLE="$PUBLISH_DIR/$PUBLISHED_NAME"

if [[ ! -x "$EXECUTABLE" && "$RID" != win-* ]]; then
  echo "error: expected executable apphost not found: $EXECUTABLE" >&2
  exit 1
fi

if [[ ! -f "$EXECUTABLE" ]]; then
  echo "error: expected published apphost not found: $EXECUTABLE" >&2
  exit 1
fi

if [[ "$RID" == osx-* ]]; then
  for command in file otool; do
    if ! command -v "$command" >/dev/null 2>&1; then
      echo "error: required command not found: $command" >&2
      exit 2
    fi
  done

  PUBLISH_ROOT="$(cd "$PUBLISH_DIR" && pwd -P)"
  NON_PORTABLE_DEPENDENCIES=""
  MACHO_COUNT=0
  SYMLINK_MATCH=""

  if ! SYMLINK_MATCH="$(find "$PUBLISH_DIR" -type l -print -quit)"; then
    echo "error: could not inspect the Native AOT publish shape for symbolic links" >&2
    exit 1
  fi

  if [[ -n "$SYMLINK_MATCH" ]]; then
    echo "error: Native AOT publish contains a symbolic link; dependency closure must be self-contained" >&2
    exit 1
  fi

  MACHO_INVENTORY="$WORK/macho-files.list"

  if ! find "$PUBLISH_DIR" -type f -print0 > "$MACHO_INVENTORY"; then
    echo "error: could not inspect the Native AOT publish shape for Mach-O files" >&2
    exit 1
  fi

  while IFS= read -r -d '' MACHO_FILE; do
    FILE_DESCRIPTION="$(file -b "$MACHO_FILE")"

    if [[ "$FILE_DESCRIPTION" != *Mach-O* ]]; then
      if [[ "$MACHO_FILE" == *.dylib ]]; then
        NON_PORTABLE_DEPENDENCIES+="${MACHO_FILE#"$PUBLISH_ROOT"/}: not a Mach-O library"$'\n'
      fi

      continue
    fi

    MACHO_COUNT=$((MACHO_COUNT + 1))
    MACHO_RELATIVE="${MACHO_FILE#"$PUBLISH_ROOT"/}"
    INSTALL_NAME="$(otool -D "$MACHO_FILE" | tail -n +2 | head -n 1)"

    if [[ -n "$INSTALL_NAME" ]]; then
      MACHO_BASENAME="${MACHO_FILE##*/}"

      case "$INSTALL_NAME" in
        "$MACHO_BASENAME" | "@rpath/$MACHO_BASENAME" | "@loader_path/$MACHO_BASENAME" | "@executable_path/$MACHO_BASENAME")
          ;;
        *)
          NON_PORTABLE_DEPENDENCIES+="$MACHO_RELATIVE: unsafe install name $INSTALL_NAME"$'\n'
          ;;
      esac
    fi

    LOAD_COMMAND_OUTPUT="$(otool -l "$MACHO_FILE")"

    if [[ "$LOAD_COMMAND_OUTPUT" == *"LC_RPATH"* ]]; then
      NON_PORTABLE_DEPENDENCIES+="$MACHO_RELATIVE: LC_RPATH is forbidden in a shipping binary"$'\n'
    fi

    DEPENDENCY_OUTPUT="$(otool -L "$MACHO_FILE")"

    while read -r dependency _; do
      if [[ -z "$dependency" || "$dependency" == "$INSTALL_NAME" ]]; then
        continue
      fi

      case "$dependency" in
        /usr/lib/* | /System/Library/*)
          ;;
        *)
          NON_PORTABLE_DEPENDENCIES+="$MACHO_RELATIVE -> $dependency"$'\n'
          ;;
      esac
    done < <(printf '%s\n' "$DEPENDENCY_OUTPUT" | tail -n +2)
  done < "$MACHO_INVENTORY"

  if [[ "$MACHO_COUNT" -eq 0 ]]; then
    echo "error: Native AOT publish contains no Mach-O files" >&2
    exit 1
  fi

  if [[ -n "$NON_PORTABLE_DEPENDENCIES" ]]; then
    echo "error: non-portable macOS dependency found in the Native AOT publish closure:" >&2
    printf '%s' "$NON_PORTABLE_DEPENDENCIES" >&2
    echo "error: /opt/homebrew/, /usr/local/, unresolved tokenized paths, and LC_RPATH are never valid shipping inputs" >&2
    exit 1
  fi
fi

if [[ -f "$PUBLISH_DIR/RetroDownfall.Arcanum.Cli.dll" ]]; then
  echo "error: Native AOT must not ship RetroDownfall.Arcanum.Cli.dll" >&2
  exit 1
fi

HOSTFXR_MATCH=""

if ! HOSTFXR_MATCH="$(find "$PUBLISH_DIR" -maxdepth 1 -iname '*hostfxr*' -print -quit)"; then
  echo "error: could not inspect the Native AOT publish shape for hostfxr" >&2
  exit 1
fi

if [[ -n "$HOSTFXR_MATCH" ]]; then
  echo "error: Native AOT must not ship hostfxr" >&2
  exit 1
fi

HOSTPOLICY_MATCH=""

if ! HOSTPOLICY_MATCH="$(find "$PUBLISH_DIR" -maxdepth 1 -iname '*hostpolicy*' -print -quit)"; then
  echo "error: could not inspect the Native AOT publish shape for hostpolicy" >&2
  exit 1
fi

if [[ -n "$HOSTPOLICY_MATCH" ]]; then
  echo "error: Native AOT must not ship hostpolicy" >&2
  exit 1
fi

echo "==> Delegating the exact shipping publish to the reusable apphost gate"
if bash "$SCRIPT_DIR/verify-published-apphost.sh" \
  --executable "$EXECUTABLE" \
  --configuration "$CONFIGURATION"; then
  :
else
  exit "$?"
fi

if [[ -n "$RETAIN_PUBLISH_DIR" ]]; then
  if mkdir "$RETAIN_PUBLISH_DIR"; then
    RETAIN_OWNED=1
  else
    echo "error: could not create fresh retained publish directory: $RETAIN_PUBLISH_DIR" >&2
    exit 1
  fi

  if cp -a "$PUBLISH_DIR/." "$RETAIN_PUBLISH_DIR/"; then
    :
  else
    copy_status=$?
    echo "error: could not retain the validated publish closure: $RETAIN_PUBLISH_DIR" >&2
    exit "$copy_status"
  fi
fi

VERIFICATION_COMPLETE=1
