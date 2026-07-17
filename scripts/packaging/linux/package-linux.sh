#!/usr/bin/env bash
# Package Arcanum (Native AOT) + The Forge + Compendium for Linux private beta.
#
# Defaults to the current host RID (linux-x64 or linux-arm64). Cross-OS packaging
# is handled by GitHub Actions — do not require foreign toolchains locally.
#
# Usage:
#   package-linux.sh --version 0.1.0-beta.1 --output-dir ./dist
#   package-linux.sh --version 0.1.0-beta.1 --output-dir ./dist --rid linux-arm64
#
# Outputs under --output-dir:
#   arcanum-linux-<arch>.tar.gz
#   the-forge-linux-<arch>.tar.gz
#   compendium-linux-<arch>.tar.gz
#   SHA256SUMS

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

VERSION=""
OUTPUT_DIR=""
RID=""
CONFIGURATION=Release

usage() {
  cat <<'EOF'
Usage: package-linux.sh --version <semver> --output-dir <dir> [--rid linux-x64|linux-arm64]

  --version       SemVer (e.g. 0.1.0-beta.1); no +build metadata
  --output-dir    Directory for .tar.gz artifacts and SHA256SUMS
  --rid           Override RID (default: host linux-x64 or linux-arm64)
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
    --rid)
      RID="${2:?}"
      shift 2
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

if [[ "$VERSION" == *+* ]]; then
  echo "error: SemVer build metadata (+...) is not allowed: '$VERSION'" >&2
  exit 1
fi

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "error: invalid SemVer '$VERSION'" >&2
  exit 1
fi

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "error: package-linux.sh must run on Linux (current host: $(uname -s)). Use GitHub Actions for cross-OS artifacts." >&2
  exit 1
fi

if [[ -z "$RID" ]]; then
  arch="$(uname -m)"
  case "$arch" in
    x86_64) RID=linux-x64 ;;
    aarch64 | arm64) RID=linux-arm64 ;;
    *)
      echo "error: unsupported host arch '$arch'; pass --rid explicitly" >&2
      exit 1
      ;;
  esac
fi

case "$RID" in
  linux-x64 | linux-arm64) ;;
  *)
    echo "error: unsupported RID '$RID' (expected linux-x64 or linux-arm64)" >&2
    exit 1
    ;;
esac

ARCH_SUFFIX="${RID#linux-}"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/arcanum-linux-pack.XXXXXX")"
cleanup() {
  rm -rf "$WORK"
}
trap cleanup EXIT

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "error: required command not found: $1" >&2
    exit 1
  }
}

require_cmd dotnet
require_cmd tar
require_cmd sha256sum

publish_cli() {
  local publish_dir="$WORK/cli-publish"
  local stage_dir="$WORK/stage/arcanum-linux-${ARCH_SUFFIX}"
  local archive="$OUTPUT_DIR/arcanum-linux-${ARCH_SUFFIX}.tar.gz"
  local project="$REPO_ROOT/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"

  echo "==> Publishing Arcanum Native AOT ($RID, Version=$VERSION)"
  dotnet publish "$project" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -p:Version="$VERSION" \
    -o "$publish_dir"

  local published=""
  if [[ -f "$publish_dir/RetroDownfall.Arcanum.Cli" ]]; then
    published="$publish_dir/RetroDownfall.Arcanum.Cli"
  elif [[ -f "$publish_dir/arcanum" ]]; then
    published="$publish_dir/arcanum"
  else
    echo "error: expected published Cli executable not found under $publish_dir" >&2
    ls -la "$publish_dir" >&2 || true
    exit 1
  fi

  mkdir -p "$stage_dir"
  cp "$published" "$stage_dir/arcanum"
  chmod +x "$stage_dir/arcanum"
  cp "$REPO_ROOT/docs/PRIVATE-BETA-NOTES.md" "$stage_dir/PRIVATE-BETA-NOTES.md"

  echo "==> Creating $archive"
  tar -C "$WORK/stage" -czf "$archive" "arcanum-linux-${ARCH_SUFFIX}"
}

publish_gui() {
  local product="$1"
  local project="$2"
  local folder_name="$3"
  local publish_dir="$WORK/${product}-publish"
  local stage_dir="$WORK/stage/${folder_name}"
  local archive="$OUTPUT_DIR/${folder_name}.tar.gz"

  echo "==> Publishing $product self-contained Avalonia folder ($RID, Version=$VERSION)"
  # Multi-file self-contained — not Native AOT, not single-file.
  dotnet publish "$project" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -p:Version="$VERSION" \
    -p:UseAppHost=true \
    -p:PublishSingleFile=false \
    -o "$publish_dir"

  mkdir -p "$stage_dir"
  cp -a "$publish_dir/." "$stage_dir/"
  cp "$REPO_ROOT/docs/PRIVATE-BETA-NOTES.md" "$stage_dir/PRIVATE-BETA-NOTES.md"

  echo "==> Creating $archive"
  tar -C "$WORK/stage" -czf "$archive" "$folder_name"
}

publish_cli
publish_gui "the-forge" \
  "$REPO_ROOT/src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj" \
  "the-forge-linux-${ARCH_SUFFIX}"
publish_gui "compendium" \
  "$REPO_ROOT/src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj" \
  "compendium-linux-${ARCH_SUFFIX}"

echo "==> Writing SHA256SUMS"
(
  cd "$OUTPUT_DIR"
  sha256sum \
    "arcanum-linux-${ARCH_SUFFIX}.tar.gz" \
    "the-forge-linux-${ARCH_SUFFIX}.tar.gz" \
    "compendium-linux-${ARCH_SUFFIX}.tar.gz" \
    >SHA256SUMS
)

echo "==> Linux private-beta artifacts in $OUTPUT_DIR"
ls -la "$OUTPUT_DIR"
