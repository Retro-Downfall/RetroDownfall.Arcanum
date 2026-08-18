#!/usr/bin/env bash
# Shared helpers for macOS packaging (signing, notarization, version helpers).
# shellcheck shell=bash

set -euo pipefail

PACKAGING_MACOS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$PACKAGING_MACOS_DIR/../../.." && pwd)"

require_cmd() {
  local cmd="$1"
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "error: required command not found: $cmd" >&2
    exit 1
  fi
}

# Allowed: 0.1.0 | 0.1.0-beta | 0.1.0-beta.1
# Rejected: 0.1-beta | 0.1.0+build.42 | empty
validate_semver() {
  local version="$1"
  if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    echo "error: invalid SemVer '$version' (expected MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-prerelease; no +build metadata)" >&2
    exit 1
  fi
  if [[ "$version" == *+* ]]; then
    echo "error: SemVer build metadata (+...) is not allowed: '$version'" >&2
    exit 1
  fi
}

# 0.1.0-beta.1 -> 0.1.0
marketing_version_from_semver() {
  local version="$1"
  echo "${version%%-*}"
}

require_signing_env() {
  local missing=0
  local key
  for key in APPLE_SIGNING_IDENTITY APPLE_ID APPLE_TEAM_ID APPLE_APP_SPECIFIC_PASSWORD; do
    if [[ -z "${!key:-}" ]]; then
      echo "error: missing required env var: $key" >&2
      missing=1
    fi
  done
  if [[ "$missing" -ne 0 ]]; then
    exit 1
  fi
  if [[ "${APPLE_SIGNING_IDENTITY}" != Developer\ ID\ Application:* ]]; then
    echo "error: APPLE_SIGNING_IDENTITY must be a Developer ID Application identity" >&2
    echo "  got: ${APPLE_SIGNING_IDENTITY}" >&2
    exit 1
  fi
}

codesign_item() {
  local target="$1"
  local entitlements="${2:-}"
  local -a args=(
    --force
    --options runtime
    --timestamp
    --sign "$APPLE_SIGNING_IDENTITY"
  )
  if [[ -n "$entitlements" ]]; then
    args+=(--entitlements "$entitlements")
  fi
  codesign "${args[@]}" "$target"
}

# Reorders MACHO_PATHS deepest-first into MACHO_PATHS_ORDERED. Globals rather than parameters because
# macOS ships bash 3.2, which has no namerefs and no way to return an array; $1 names the caller for
# the error message. `find` alone will not do: its walk is pre-order, not depth-first, so it emits a
# directory's own files before descending into that directory's subdirectories, and the shallow
# dependents come back before the libraries they load.
#
# The ordering is done in-shell rather than through a NUL-delimited pipeline: BSD `cut` has no `-z`
# and one-true-awk stores RS="\0" as the empty string, so on the only OS that runs this script the
# pipeline emitted nothing at all — which then either aborted the release on bash 3.2 (`sorted:
# unbound variable`) or, on a newer bash, silently signed nothing but the main executable. Parameter
# expansion never word-splits, so paths with spaces are safe without a delimiter.
order_macho_paths_deepest_first() {
  local context="$1"
  MACHO_PATHS_ORDERED=()
  if ((${#MACHO_PATHS[@]} == 0)); then
    return 0
  fi

  local -a depths=()
  local entry
  local separators
  local max_depth=0
  for entry in "${MACHO_PATHS[@]}"; do
    separators="${entry//[!\/]/}"
    depths+=("${#separators}")
    if ((${#separators} > max_depth)); then
      max_depth=${#separators}
    fi
  done

  local level
  local index
  for ((level = max_depth; level >= 0; level--)); do
    for ((index = 0; index < ${#MACHO_PATHS[@]}; index++)); do
      if ((depths[index] == level)); then
        MACHO_PATHS_ORDERED+=("${MACHO_PATHS[index]}")
      fi
    done
  done

  # A reordering that drops entries would sign a partial tree and leave the rest unsigned inside an
  # archive that `codesign --verify` still calls valid, because it only inspects the main binary.
  # Fail loudly instead.
  if ((${#MACHO_PATHS_ORDERED[@]} != ${#MACHO_PATHS[@]})); then
    echo "error: $context: depth ordering kept ${#MACHO_PATHS_ORDERED[@]} of ${#MACHO_PATHS[@]} Mach-O files; refusing to sign a partial tree" >&2
    exit 1
  fi
}

# Sign Mach-O files inside a .app: nested libs/helpers first, then main executable, then bundle.
# build-app-dmg.sh copies the publish directory verbatim into Contents/MacOS, so this walks the same
# tree shape as sign_publish_dir and needs the same deepest-first ordering and partial-tree guard.
sign_app_bundle() {
  local app_path="$1"
  local entitlements="${2:-}"
  local macos_dir="$app_path/Contents/MacOS"
  local executable_name
  executable_name="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app_path/Contents/Info.plist")"

  require_cmd file
  require_cmd codesign

  # Collect nested Mach-O files except the main apphost, which is signed after everything it loads.
  MACHO_PATHS=()
  local file
  local base
  while IFS= read -r -d '' file; do
    base="$(basename "$file")"
    if [[ "$base" == "$executable_name" ]]; then
      continue
    fi
    if file -b "$file" | grep -q 'Mach-O'; then
      MACHO_PATHS+=("$file")
    fi
  done < <(find "$macos_dir" -type f -print0)

  order_macho_paths_deepest_first sign_app_bundle

  local path
  for path in "${MACHO_PATHS_ORDERED[@]+"${MACHO_PATHS_ORDERED[@]}"}"; do
    echo "==> Signing nested Mach-O: $path"
    codesign_item "$path" "$entitlements"
  done

  codesign_item "$macos_dir/$executable_name" "$entitlements"
  codesign_item "$app_path" "$entitlements"

  codesign --verify --strict --deep --verbose=4 "$app_path"
}

# Sign a flat publish/stage directory (CLI zip): nested Mach-Os first, main executable last.
# Discovers regular files via `file`; verifies each with codesign --verify --strict --verbose=2.
sign_publish_dir() {
  local dir="$1"
  local entitlements="${2:-}"
  local main_name="${3:-arcanum}"
  local main_path="$dir/$main_name"

  if [[ ! -d "$dir" ]]; then
    echo "error: sign_publish_dir: not a directory: $dir" >&2
    exit 1
  fi
  if [[ ! -f "$main_path" ]]; then
    echo "error: sign_publish_dir: main executable not found: $main_path" >&2
    exit 1
  fi

  require_cmd file
  require_cmd codesign

  MACHO_PATHS=()
  local file
  while IFS= read -r -d '' file; do
    if [[ "$file" == "$main_path" ]]; then
      continue
    fi
    if file -b "$file" | grep -q 'Mach-O'; then
      MACHO_PATHS+=("$file")
    fi
  done < <(find "$dir" -type f -print0)

  # Deepest paths first so nested dylibs are signed before dependents / main.
  order_macho_paths_deepest_first sign_publish_dir

  local path
  for path in "${MACHO_PATHS_ORDERED[@]+"${MACHO_PATHS_ORDERED[@]}"}"; do
    echo "==> Signing nested Mach-O: $path"
    codesign_item "$path" "$entitlements"
    codesign --verify --strict --verbose=2 "$path"
  done

  echo "==> Signing main executable: $main_path"
  codesign_item "$main_path" "$entitlements"
  codesign --verify --strict --verbose=2 "$main_path"
}

# Notarization credentials are handed over through a keychain item, never as an argv element:
# a command line is readable from the whole process table for as long as the process lives, and
# `notarytool submit --wait` lives for the entire notarization. Same rule the Windows packager
# follows for signtool. Both `notarytool` and `security` prompt for the value when the option is
# omitted, and both read the answer from stdin, so every secret below arrives on a pipe.
NOTARY_KEYCHAIN_PROFILE="arcanum-notarytool"
NOTARY_KEYCHAIN=""
NOTARY_KEYCHAIN_OWNED=0

notarize_prepare_credentials() {
  if [[ -n "$NOTARY_KEYCHAIN" ]]; then
    return 0
  fi

  require_cmd security
  require_cmd xcrun

  if [[ -n "${KEYCHAIN_PATH:-}" && -f "${KEYCHAIN_PATH:-}" ]]; then
    # CI already built an ephemeral signing keychain for this run and deletes it afterwards.
    NOTARY_KEYCHAIN="$KEYCHAIN_PATH"
  else
    require_cmd openssl
    local keychain_password
    keychain_password="$(openssl rand -hex 24)"
    NOTARY_KEYCHAIN="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-notary.XXXXXX")/notary.keychain-db"
    printf '%s\n%s\n' "$keychain_password" "$keychain_password" |
      security create-keychain "$NOTARY_KEYCHAIN"
    NOTARY_KEYCHAIN_OWNED=1
    security set-keychain-settings -lut 21600 "$NOTARY_KEYCHAIN"
    printf '%s\n' "$keychain_password" | security unlock-keychain "$NOTARY_KEYCHAIN"
  fi

  echo "==> Storing notarization credentials in keychain profile '$NOTARY_KEYCHAIN_PROFILE'"
  printf '%s\n' "$APPLE_APP_SPECIFIC_PASSWORD" |
    xcrun notarytool store-credentials "$NOTARY_KEYCHAIN_PROFILE" \
      --apple-id "$APPLE_ID" \
      --team-id "$APPLE_TEAM_ID" \
      --keychain "$NOTARY_KEYCHAIN"
}

# Callers own an EXIT trap; they must call this from it. Deletes only a keychain this script
# created — a CI-provided KEYCHAIN_PATH is the workflow's to clean up.
notarize_cleanup() {
  if [[ "$NOTARY_KEYCHAIN_OWNED" -eq 1 && -n "$NOTARY_KEYCHAIN" && -f "$NOTARY_KEYCHAIN" ]]; then
    security delete-keychain "$NOTARY_KEYCHAIN" >/dev/null 2>&1 || true
    rm -rf "$(dirname "$NOTARY_KEYCHAIN")"
  fi
  NOTARY_KEYCHAIN=""
  NOTARY_KEYCHAIN_OWNED=0
}

notarize_submit() {
  local archive="$1"
  notarize_prepare_credentials
  xcrun notarytool submit "$archive" \
    --keychain-profile "$NOTARY_KEYCHAIN_PROFILE" \
    --keychain "$NOTARY_KEYCHAIN" \
    --wait
}

staple_item() {
  local target="$1"
  xcrun stapler staple "$target"
  xcrun stapler validate "$target"
}

render_plist_template() {
  local template="$1"
  local dest="$2"
  local marketing_version="$3"
  local bundle_version="$4"
  sed \
    -e "s|__MARKETING_VERSION__|${marketing_version}|g" \
    -e "s|__BUNDLE_VERSION__|${bundle_version}|g" \
    "$template" >"$dest"
}
