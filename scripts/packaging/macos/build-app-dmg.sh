#!/usr/bin/env bash
# Build, sign, notarize, and staple an Avalonia .app into a DMG (osx-arm64).
#
# Usage:
#   build-app-dmg.sh \
#     --product compendium|the-forge \
#     --version 0.1.0-beta.1 \
#     --marketing-version 0.1.0 \
#     --bundle-version 42 \
#     --output-dir ./dist \
#     [--single-file] \
#     [--skip-sign | --local-sign]
#
# Default publish is multi-file self-contained (preferred for first signed release
# so native libraries can be codesigned individually). Pass --single-file to use
# PublishSingleFile=true instead.
#
# CI must never pass --skip-sign or --local-sign. --local-sign signs with the certificate the
# operator installed in Keychain Access and stops short of notarization and stapling, which Apple
# does not offer for a development certificate.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

PRODUCT=""
VERSION=""
MARKETING_VERSION=""
BUNDLE_VERSION=""
OUTPUT_DIR=""
SKIP_SIGN=0
LOCAL_SIGN=0
SINGLE_FILE=0
CONFIGURATION=Release
RID=osx-arm64

usage() {
  cat <<'EOF'
Usage: build-app-dmg.sh --product <compendium|the-forge> --version <semver>
                        --marketing-version <x.y.z> --bundle-version <n>
                        --output-dir <dir> [--single-file] [--skip-sign|--local-sign]

  --product             compendium | the-forge
  --version             SemVer stamped into .NET Version (e.g. 0.1.0-beta.1)
  --marketing-version   Numeric CFBundleShortVersionString (e.g. 0.1.0)
  --bundle-version      Numeric CFBundleVersion (e.g. github.run_number)
  --output-dir          Directory for the .dmg
  --single-file         Publish with PublishSingleFile=true (default: multi-file)
  --skip-sign           Local structure smoke only — never use in CI release
  --local-sign          Sign with the identity installed in Keychain Access and skip
                        notarization/stapling; never use in CI release
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --product)
      PRODUCT="${2:?}"
      shift 2
      ;;
    --version)
      VERSION="${2:?}"
      shift 2
      ;;
    --marketing-version)
      MARKETING_VERSION="${2:?}"
      shift 2
      ;;
    --bundle-version)
      BUNDLE_VERSION="${2:?}"
      shift 2
      ;;
    --output-dir)
      OUTPUT_DIR="${2:?}"
      shift 2
      ;;
    --single-file)
      SINGLE_FILE=1
      shift
      ;;
    --skip-sign)
      SKIP_SIGN=1
      shift
      ;;
    --local-sign)
      LOCAL_SIGN=1
      shift
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ -z "$PRODUCT" || -z "$VERSION" || -z "$MARKETING_VERSION" || -z "$BUNDLE_VERSION" || -z "$OUTPUT_DIR" ]]; then
  usage >&2
  exit 1
fi

if [[ "$SKIP_SIGN" -eq 1 && "$LOCAL_SIGN" -eq 1 ]]; then
  echo "error: --skip-sign and --local-sign are mutually exclusive" >&2
  exit 1
fi

validate_semver "$VERSION"

if [[ ! "$MARKETING_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "error: marketing version must be numeric x.y.z (got '$MARKETING_VERSION')" >&2
  exit 1
fi
if [[ "$MARKETING_VERSION" == *-* ]]; then
  echo "error: marketing version must not contain a prerelease suffix" >&2
  exit 1
fi
if [[ ! "$BUNDLE_VERSION" =~ ^[0-9]+([.][0-9]+)*$ ]]; then
  echo "error: bundle version must be numeric (got '$BUNDLE_VERSION')" >&2
  exit 1
fi

case "$PRODUCT" in
  compendium)
    PROJECT="$REPO_ROOT/src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj"
    APP_NAME="Compendium"
    PLIST_TEMPLATE="$SCRIPT_DIR/Info.plist.compendium"
    ICNS="$SCRIPT_DIR/Compendium.icns"
    DMG_NAME="compendium-osx-arm64.dmg"
    EXECUTABLE_NAME="RetroDownfall.Compendium.Ux"
    ;;
  the-forge)
    PROJECT="$REPO_ROOT/src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj"
    APP_NAME="The Forge"
    PLIST_TEMPLATE="$SCRIPT_DIR/Info.plist.theforge"
    ICNS="$SCRIPT_DIR/TheForge.icns"
    DMG_NAME="the-forge-osx-arm64.dmg"
    EXECUTABLE_NAME="RetroDownfall.TheForge.Ux"
    ;;
  *)
    echo "error: unknown product '$PRODUCT' (expected compendium|the-forge)" >&2
    exit 1
    ;;
esac

require_cmd dotnet
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/app-pack.XXXXXX")"
cleanup() {
  notarize_cleanup
  rm -rf "$WORK"
}
trap cleanup EXIT

PUBLISH_DIR="$WORK/publish"
APP_PATH="$WORK/${APP_NAME}.app"
DMG_PATH="$OUTPUT_DIR/$DMG_NAME"
ENTITLEMENTS="$SCRIPT_DIR/entitlements.desktop.plist"

PUBLISH_ARGS=(
  publish "$PROJECT"
  -c "$CONFIGURATION"
  -r "$RID"
  --self-contained true
  -p:Version="$VERSION"
  -p:UseAppHost=true
  -o "$PUBLISH_DIR"
)

if [[ "$SINGLE_FILE" -eq 1 ]]; then
  echo "==> Publishing $APP_NAME (single-file self-contained, $RID, Version=$VERSION)"
  PUBLISH_ARGS+=(-p:PublishSingleFile=true)
else
  echo "==> Publishing $APP_NAME (multi-file self-contained, $RID, Version=$VERSION)"
  PUBLISH_ARGS+=(-p:PublishSingleFile=false)
fi

dotnet "${PUBLISH_ARGS[@]}"

if [[ ! -f "$PUBLISH_DIR/$EXECUTABLE_NAME" ]]; then
  echo "error: expected apphost not found: $PUBLISH_DIR/$EXECUTABLE_NAME" >&2
  ls -la "$PUBLISH_DIR" >&2 || true
  exit 1
fi

echo "==> Assembling ${APP_NAME}.app"
rm -rf "$APP_PATH"
mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources"
render_plist_template "$PLIST_TEMPLATE" "$APP_PATH/Contents/Info.plist" "$MARKETING_VERSION" "$BUNDLE_VERSION"

if [[ ! -f "$ICNS" ]]; then
  echo "error: icon not found: $ICNS" >&2
  exit 1
fi
cp "$ICNS" "$APP_PATH/Contents/Resources/$(basename "$ICNS")"
cp -a "$PUBLISH_DIR"/. "$APP_PATH/Contents/MacOS/"

# Debug symbols are not part of a shipped app. They are several megabytes of internals nobody
# installing the DMG can use, and every one of them is another file the bundle seal has to cover --
# codesign treats everything under Contents/MacOS as nested code, whatever its type, so a .pdb that
# does not belong here still has to be signed before the bundle will seal. Removing them is cheaper
# than signing them and is what a release should carry either way.
find "$APP_PATH/Contents/MacOS" -type f -name '*.pdb' -delete

chmod +x "$APP_PATH/Contents/MacOS/$EXECUTABLE_NAME"

# Guard: marketing version in plist must stay numeric-only.
SHORT_VER="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP_PATH/Contents/Info.plist")"
BUNDLE_VER="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$APP_PATH/Contents/Info.plist")"
if [[ "$SHORT_VER" == *-* ]]; then
  echo "error: CFBundleShortVersionString must not contain prerelease: '$SHORT_VER'" >&2
  exit 1
fi
if [[ "$SHORT_VER" != "$MARKETING_VERSION" ]]; then
  echo "error: CFBundleShortVersionString mismatch ('$SHORT_VER' vs '$MARKETING_VERSION')" >&2
  exit 1
fi
if [[ "$BUNDLE_VER" != "$BUNDLE_VERSION" ]]; then
  echo "error: CFBundleVersion mismatch ('$BUNDLE_VER' vs '$BUNDLE_VERSION')" >&2
  exit 1
fi

if [[ "$SKIP_SIGN" -eq 0 ]]; then
  require_cmd codesign
  require_cmd hdiutil

  if [[ "$LOCAL_SIGN" -eq 1 ]]; then
    require_local_signing_identity
  else
    require_cmd xcrun
    require_signing_env
  fi

  echo "==> Signing ${APP_NAME}.app (nested then bundle; no --deep for signing)"
  sign_app_bundle "$APP_PATH" "$ENTITLEMENTS"
else
  echo "==> --skip-sign: skipping codesign/notarization/staple (local smoke only)"
fi

echo "==> Creating DMG"
rm -f "$DMG_PATH"
DMG_STAGE="$WORK/dmg-root"
mkdir -p "$DMG_STAGE"
cp -R "$APP_PATH" "$DMG_STAGE/"
ln -s /Applications "$DMG_STAGE/Applications"
hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$DMG_STAGE" \
  -ov \
  -format UDZO \
  "$DMG_PATH"

if [[ "$LOCAL_SIGN" -eq 1 ]]; then
  echo "==> Signing DMG"
  codesign_item "$DMG_PATH"
  codesign --verify --strict --verbose=4 "$DMG_PATH"

  # No notarization and therefore no staple: Apple notarizes Developer ID submissions only, and
  # stapler has nothing to attach without a notarization ticket. spctl is skipped for the same
  # reason -- it would fail on this DMG by design, not because the bundle is malformed.
  echo "==> Signed with '$LOCAL_SIGNING_IDENTITY_NAME'; NOT notarized, NOT stapled, NOT releasable."
  echo "    On any Mac that does not trust this certificate, Gatekeeper will refuse ${APP_NAME}.app."
elif [[ "$SKIP_SIGN" -eq 0 ]]; then
  echo "==> Signing DMG"
  codesign_item "$DMG_PATH"

  echo "==> Notarizing DMG"
  notarize_submit "$DMG_PATH"

  echo "==> Stapling DMG"
  staple_item "$DMG_PATH"

  echo "==> Assessing DMG with spctl"
  spctl --assess --type open --context context:primary-signature --verbose=4 "$DMG_PATH" || {
    echo "warning: spctl --assess on DMG returned non-zero; notarization/staple succeeded — check local policy" >&2
  }
fi

echo "==> Wrote $DMG_PATH"
