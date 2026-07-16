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

# Sign Mach-O files inside a .app: nested libs/helpers first, then main executable, then bundle.
sign_app_bundle() {
  local app_path="$1"
  local entitlements="${2:-}"
  local macos_dir="$app_path/Contents/MacOS"
  local executable_name
  executable_name="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app_path/Contents/Info.plist")"

  # Sign nested Mach-O files except the main apphost (depth-first via find).
  while IFS= read -r -d '' file; do
    local base
    base="$(basename "$file")"
    if [[ "$base" == "$executable_name" ]]; then
      continue
    fi
    if file -b "$file" | grep -q 'Mach-O'; then
      codesign_item "$file" "$entitlements"
    fi
  done < <(find "$macos_dir" -type f -print0)

  codesign_item "$macos_dir/$executable_name" "$entitlements"
  codesign_item "$app_path" "$entitlements"

  codesign --verify --strict --deep --verbose=4 "$app_path"
}

notarize_submit() {
  local archive="$1"
  xcrun notarytool submit "$archive" \
    --apple-id "$APPLE_ID" \
    --team-id "$APPLE_TEAM_ID" \
    --password "$APPLE_APP_SPECIFIC_PASSWORD" \
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
