#!/usr/bin/env bash
# Build, sign, and notarize Arcanum Native AOT for osx-arm64 as a zip.
#
# Usage:
#   build-arcanum.sh --version 0.1.0-beta.1 --output-dir ./dist [--skip-sign]
#
# CI must never pass --skip-sign. Local structure smoke tests may.
#
# Outputs:
#   <output-dir>/arcanum-osx-arm64.zip
#     contains folder arcanum-osx-arm64/ with arcanum (signed Mach-O) and README.md
#
# The zip is submitted to notarytool. The zip is NOT stapled (Apple staples
# .app/.dmg/.pkg, not arbitrary zip containers). After notarization, the
# signed binary is extracted and validated with codesign/spctl.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

VERSION=""
OUTPUT_DIR=""
SKIP_SIGN=0
CONFIGURATION=Release
RID=osx-arm64

usage() {
  cat <<'EOF'
Usage: build-arcanum.sh --version <semver> --output-dir <dir> [--skip-sign]

  --version       SemVer (e.g. 0.1.0-beta.1); no +build metadata
  --output-dir    Directory for arcanum-osx-arm64.zip
  --skip-sign     Local structure smoke only — never use in CI release
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      VERSION="${2:?}"
      shift 2
      ;;
    --output-dir)
      OUTPUT_DIR="${2:?}"
      shift 2
      ;;
    --skip-sign)
      SKIP_SIGN=1
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

if [[ -z "$VERSION" || -z "$OUTPUT_DIR" ]]; then
  usage >&2
  exit 1
fi

validate_semver "$VERSION"
require_cmd dotnet
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-pack.XXXXXX")"
cleanup() {
  notarize_cleanup
  rm -rf "$WORK"
}
trap cleanup EXIT

PUBLISH_DIR="$WORK/publish"
STAGE_DIR="$WORK/stage/arcanum-osx-arm64"
ZIP_PATH="$OUTPUT_DIR/arcanum-osx-arm64.zip"
PROJECT="$REPO_ROOT/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"
PUBLISHED_NAME="RetroDownfall.Arcanum.Cli"

echo "==> Publishing Arcanum Native AOT ($RID, Version=$VERSION)"
dotnet publish "$PROJECT" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -o "$PUBLISH_DIR"

if [[ ! -f "$PUBLISH_DIR/$PUBLISHED_NAME" ]]; then
  echo "error: expected published executable not found: $PUBLISH_DIR/$PUBLISHED_NAME" >&2
  ls -la "$PUBLISH_DIR" >&2 || true
  exit 1
fi

rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR"
cp -R "$PUBLISH_DIR"/. "$STAGE_DIR"/
if [[ -f "$STAGE_DIR/$PUBLISHED_NAME" ]]; then
  mv "$STAGE_DIR/$PUBLISHED_NAME" "$STAGE_DIR/arcanum"
fi
chmod +x "$STAGE_DIR/arcanum"
cp "$REPO_ROOT/README.md" "$STAGE_DIR/README.md"

# The csproj enables Native AOT on macOS only when ld64.lld is present and otherwise falls back to a
# self-contained CoreCLR folder publish. That fallback signs, notarizes, launches, and passes every
# check below, so without an explicit assertion a release host that simply lacks the linker ships a
# non-AOT binary and says nothing. A Native AOT publish carries no managed assemblies at all, which
# is the difference the two shapes cannot fake.
MANAGED_DLL_COUNT="$(find "$STAGE_DIR" -maxdepth 1 -name '*.dll' | wc -l | tr -d ' ')"

if [[ "${ARCANUM_REQUIRE_MACOS_AOT:-}" == "true" ]]; then
  if [[ "$MANAGED_DLL_COUNT" != "0" ]]; then
    echo "error: ARCANUM_REQUIRE_MACOS_AOT=true but the publish contains $MANAGED_DLL_COUNT managed assemblies," >&2
    echo "       so this is the CoreCLR fallback, not Native AOT. Install ld64.lld (brew install lld)." >&2
    exit 1
  fi

  echo "==> Verified Native AOT publish (no managed assemblies in the staged tree)"
else
  if [[ "$MANAGED_DLL_COUNT" == "0" ]]; then
    echo "==> Native AOT publish (no managed assemblies staged)"
  else
    echo "==> CoreCLR fallback publish ($MANAGED_DLL_COUNT managed assemblies); ld64.lld not found on this host"
  fi
fi

if [[ "$SKIP_SIGN" -eq 0 ]]; then
  require_cmd codesign
  require_cmd ditto
  require_cmd xcrun
  require_signing_env

  # These are the .NET JIT entitlements, and they are required whenever the macOS CLI falls back to
  # the self-contained CoreCLR folder publish (no ld64.lld on the build host): the hardened runtime
  # must be told to permit the RWX/MAP_JIT mappings the runtime creates, and signing without them
  # still passes `codesign --verify` and `spctl --assess` while the binary aborts before Main.
  #
  # A Native AOT publish does not JIT and does not need them. They are left applied because an
  # unused entitlement is permissive rather than breaking, and because dropping them cannot be
  # verified without a Developer ID certificate and a notarization round-trip. Narrowing this to the
  # AOT path is a real hardening improvement and should be done the next time a signed build can
  # actually be exercised end to end -- not blind.
  ENTITLEMENTS="$SCRIPT_DIR/entitlements.cli.plist"
  if [[ ! -f "$ENTITLEMENTS" ]]; then
    echo "error: entitlements plist not found: $ENTITLEMENTS" >&2
    exit 1
  fi

  echo "==> Signing publish tree Mach-Os (Developer ID Application, hardened runtime + JIT entitlements)"
  sign_publish_dir "$STAGE_DIR" "$ENTITLEMENTS"
else
  echo "==> --skip-sign: skipping codesign/notarization (local smoke only)"
fi

echo "==> Creating notarization zip with ditto"
rm -f "$ZIP_PATH"
(
  cd "$WORK/stage"
  ditto -c -k --keepParent "arcanum-osx-arm64" "$ZIP_PATH"
)

if [[ "$SKIP_SIGN" -eq 0 ]]; then
  echo "==> Submitting zip to notarytool (no staple on zip)"
  notarize_submit "$ZIP_PATH"

  echo "==> Validating extracted signed binary"
  VALIDATE_DIR="$WORK/validate"
  mkdir -p "$VALIDATE_DIR"
  ditto -x -k "$ZIP_PATH" "$VALIDATE_DIR"
  BINARY="$VALIDATE_DIR/arcanum-osx-arm64/arcanum"
  codesign --verify --strict --verbose=4 "$BINARY"
  # Gatekeeper assessment for a Developer ID signed executable.
  spctl --assess --type execute --verbose=4 "$BINARY" || {
    echo "warning: spctl --assess returned non-zero; notarization was accepted — check local Gatekeeper policy" >&2
  }

  # Both checks above pass on a hardened-runtime binary that cannot start at all (a missing
  # JIT entitlement aborts CoreCLR before Main), so actually launch the signed artifact.
  echo "==> Launching signed binary (--version) as a release smoke"
  "$BINARY" --version
fi

echo "==> Wrote $ZIP_PATH"
