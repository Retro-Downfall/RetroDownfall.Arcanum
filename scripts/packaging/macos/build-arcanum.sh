#!/usr/bin/env bash
# Build, sign, and notarize Arcanum Native AOT for osx-arm64 as a zip.
#
# Usage:
#   build-arcanum.sh --version 0.1.0-beta.1 --output-dir ./dist [--skip-sign]
#   build-arcanum.sh --version 0.1.0-beta.1 --output-dir ./dist --local-sign
#
# CI must never pass --skip-sign or --local-sign. Local structure smoke tests may pass --skip-sign;
# --local-sign signs with the certificate the operator installed in Keychain Access and stops short
# of notarization, which Apple does not offer for a development certificate.
#
# Outputs:
#   <output-dir>/arcanum-osx-arm64.zip
#     contains folder arcanum-osx-arm64/ with arcanum (signed Mach-O) and README.md
#
# The zip is submitted to notarytool. The zip is NOT stapled (Apple staples
# .app/.dmg/.pkg, not arbitrary zip containers). After notarization, the signed binary is
# extracted, validated with strict codesign integrity and notarization-ticket checks, and launched.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

VERSION=""
OUTPUT_DIR=""
SKIP_SIGN=0
LOCAL_SIGN=0
CONFIGURATION=Release
RID=osx-arm64

usage() {
  cat <<'EOF'
Usage: build-arcanum.sh --version <semver> --output-dir <dir> [--skip-sign|--local-sign]

  --version       SemVer (e.g. 0.1.0-beta.1); no +build metadata
  --output-dir    Directory for arcanum-osx-arm64.zip
  --skip-sign     Local structure smoke only — never use in CI release
  --local-sign    Sign with the identity installed in Keychain Access and skip notarization;
                  for verifying the signed binary on this machine, never for release
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

if [[ -z "$VERSION" || -z "$OUTPUT_DIR" ]]; then
  usage >&2
  exit 1
fi

if [[ "$SKIP_SIGN" -eq 1 && "$LOCAL_SIGN" -eq 1 ]]; then
  echo "error: --skip-sign and --local-sign are mutually exclusive" >&2
  exit 1
fi

validate_semver "$VERSION"
require_cmd dotnet
require_cmd rg
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-pack.XXXXXX")"
cleanup() {
  local original_status=$?

  packaging_cleanup_exit "$original_status" "$WORK"
}
trap cleanup EXIT

PUBLISH_DIR="$WORK/publish"
STAGE_DIR="$WORK/stage/arcanum-osx-arm64"
ZIP_PATH="$OUTPUT_DIR/arcanum-osx-arm64.zip"
PROJECT="$REPO_ROOT/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"
PUBLISHED_NAME="RetroDownfall.Arcanum.Cli"
PUBLISH_LOG="$WORK/publish.log"

PUBLISH_AOT="$(dotnet msbuild "$PROJECT" \
  -nologo \
  -getProperty:PublishAot \
  -p:Configuration="$CONFIGURATION" \
  -p:RuntimeIdentifier="$RID" \
  | tr -d '\r')"

if [[ "$PUBLISH_AOT" != "true" ]]; then
  echo "error: PublishAot resolved to '$PUBLISH_AOT' for $RID; expected true" >&2
  exit 1
fi

echo "==> Publishing Arcanum Native AOT ($RID, Version=$VERSION)"
dotnet publish "$PROJECT" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -o "$PUBLISH_DIR" \
  2>&1 | tee "$PUBLISH_LOG"

if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$PUBLISH_LOG"; then
  echo "error: Arcanum publish emitted warning output" >&2
  exit 1
else
  warning_scan_status=$?
fi

if [[ "$warning_scan_status" -ne 1 ]]; then
  echo "error: could not scan Arcanum publish output (ripgrep exit $warning_scan_status)" >&2
  exit 1
fi

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

# Native AOT ships no managed application assembly or CoreCLR host. Check the staged package, not
# only an MSBuild property, so a packaging regression cannot silently ship another runtime shape.
DLL_MATCH=""

if ! DLL_MATCH="$(find "$STAGE_DIR" -maxdepth 1 -name '*.dll' -print -quit)"; then
  echo "error: could not inspect Native AOT package for managed assemblies" >&2
  exit 1
fi

if [[ -n "$DLL_MATCH" ]]; then
  echo "error: Native AOT package contains managed assemblies" >&2
  exit 1
fi

for forbidden in '*hostfxr*' '*hostpolicy*'; do
  FORBIDDEN_MATCH=""

  if ! FORBIDDEN_MATCH="$(find "$STAGE_DIR" -maxdepth 1 -iname "$forbidden" -print -quit)"; then
    echo "error: could not inspect Native AOT package for $forbidden" >&2
    exit 1
  fi

  if [[ -n "$FORBIDDEN_MATCH" ]]; then
    echo "error: Native AOT package contains forbidden CoreCLR component: $forbidden" >&2
    exit 1
  fi
done

echo "==> Verified Native AOT publish (no managed assemblies or CoreCLR host)"

if [[ "$SKIP_SIGN" -eq 0 ]]; then
  require_cmd codesign
  require_cmd ditto

  if [[ "$LOCAL_SIGN" -eq 1 ]]; then
    require_local_signing_identity
  else
    require_cmd xcrun
    require_signing_env
  fi

  # Native AOT needs no JIT or library-validation exception. Keep the entitlement file explicit and
  # privilege-free so a future managed fallback cannot acquire executable-memory authority merely
  # by reusing this signing path.
  ENTITLEMENTS="$SCRIPT_DIR/entitlements.cli.plist"
  if [[ ! -f "$ENTITLEMENTS" ]]; then
    echo "error: entitlements plist not found: $ENTITLEMENTS" >&2
    exit 1
  fi

  if [[ "$LOCAL_SIGN" -eq 1 ]]; then
    echo "==> Signing publish tree Mach-Os (keychain identity, hardened runtime, no JIT entitlements)"
  else
    echo "==> Signing publish tree Mach-Os (Developer ID Application, hardened runtime, no JIT entitlements)"
  fi

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

if [[ "$LOCAL_SIGN" -eq 1 ]]; then
  # No notarization: Apple notarizes Developer ID submissions only, and a development certificate
  # is rejected outright. The launch smoke below is the part that actually matters locally — it is
  # the only check that catches a hardened-runtime binary which aborts before Main because the JIT
  # entitlements are wrong, while static codesign verification still passes on that binary.
  echo "==> Verifying the locally signed binary"
  codesign --verify --strict --verbose=4 "$STAGE_DIR/arcanum"

  echo "==> Launching signed binary (--version) as a local smoke"
  "$STAGE_DIR/arcanum" --version

  echo "==> Signed with '$LOCAL_SIGNING_IDENTITY_NAME'; NOT notarized, NOT a release artifact."
  echo "    On any Mac that does not trust this certificate, Gatekeeper will refuse the binary."
elif [[ "$SKIP_SIGN" -eq 0 ]]; then
  echo "==> Submitting zip to notarytool (no staple on zip)"
  notarize_submit "$ZIP_PATH"

  echo "==> Validating extracted signed binary"
  VALIDATE_DIR="$WORK/validate"
  mkdir -p "$VALIDATE_DIR"
  ditto -x -k "$ZIP_PATH" "$VALIDATE_DIR"
  BINARY="$VALIDATE_DIR/arcanum-osx-arm64/arcanum"
  verify_notarized_cli "$BINARY"

  # Signature integrity and notarization-ticket checks do not prove that a hardened-runtime binary
  # can reach Main, so actually launch the signed artifact.
  echo "==> Launching signed binary (--version) as a release smoke"
  "$BINARY" --version
fi

echo "==> Wrote $ZIP_PATH"
