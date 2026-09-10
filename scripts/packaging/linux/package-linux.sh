#!/usr/bin/env bash
# Package Native AOT Arcanum + The Forge + Compendium for a future Linux private beta.
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
require_cmd rg
require_cmd tar
require_cmd sha256sum

# Arcanum builds its own SQLCipher and delivers exactly one verified library per shipping runtime
# identifier. Linux is not currently one of them: the delivery targets do not map a linux-* RID to
# a library filename (ARCSQLC001), and no verified asset exists. Both must land before this dormant
# packager can run; Arcanum never falls back to ambient SQLite.
require_native_sqlcipher_asset() {
  local rid="$1"

  local asset="$REPO_ROOT/src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/$rid/native/libe_sqlcipher.so"
  local targets="$REPO_ROOT/src/RetroDownfall.Arcanum.NativeSqlCipher/buildTransitive/RetroDownfall.Arcanum.NativeSqlCipher.targets"

  if ! rg --no-config -q "StartsWith\\('linux-'\\)" "$targets"; then
    echo "No linux-* SQLCipher filename mapping exists, so Linux packaging cannot produce a" >&2
    echo "working archive. Add the mapping beside the osx-* and win-* mappings only with a" >&2
    echo "verified native asset and manifest record; see docs/Arcanum.DESIGN.md 5.4.4a." >&2
    exit 2
  fi

  if [ -f "$asset" ]; then
    return 0
  fi

  echo "No hermetic SQLCipher asset exists for '$rid', so Linux packaging cannot produce a" >&2
  echo "working archive. Arcanum ships osx-arm64, win-x64, and win-arm64; see" >&2
  echo "docs/Arcanum.DESIGN.md 5.4.4a. To restore Linux support: add the RID to" >&2
  echo "src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json, build and verify" >&2
  echo "its asset on a native runner, check it in, and re-add the RID to the CI matrix." >&2

  exit 2
}

# The Native AOT image does not absorb P/Invoke shared libraries. SQLitePCLRaw only static-links
# e_sqlcipher for browser-wasm, so a future linux-* publish must emit libe_sqlcipher.so and
# libonigwrap.so beside the host. Shipping an archive without them produces a CLI that dies with
# DllNotFoundException the moment it opens the Grimoire, so assert here and fail loudly.
require_staged_natives() {
  local stage_dir="$1"
  shift

  local missing=()
  local name

  for name in "$@"; do
    if [[ ! -f "$stage_dir/$name" ]]; then
      missing+=("$name")
    fi
  done

  if [[ ${#missing[@]} -gt 0 ]]; then
    echo "error: staged package is missing required native libraries: ${missing[*]}" >&2
    echo "error: expected them beside the host in $stage_dir; the archive would be unusable." >&2
    ls -la "$stage_dir" >&2 || true
    exit 1
  fi

  echo "==> Verified native sidecars in $stage_dir: $*"
}

require_native_aot_publish() {
  local stage_dir="$1"
  local forbidden=(
    RetroDownfall.Arcanum.Cli.dll
    libhostfxr.so
    libhostpolicy.so
  )
  local present=()
  local name

  for name in "${forbidden[@]}"; do
    if [[ -f "$stage_dir/$name" ]]; then
      present+=("$name")
    fi
  done

  if [[ ${#present[@]} -gt 0 ]]; then
    echo "error: Native AOT package contains managed runtime files: ${present[*]}" >&2
    ls -la "$stage_dir" >&2 || true
    exit 1
  fi

  echo "==> Verified Native AOT publish"
}

publish_cli() {
  local publish_dir="$WORK/cli-publish"
  local stage_dir="$WORK/stage/arcanum-linux-${ARCH_SUFFIX}"
  local archive="$OUTPUT_DIR/arcanum-linux-${ARCH_SUFFIX}.tar.gz"
  local project="$REPO_ROOT/src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"
  local publish_log="$WORK/cli-publish.log"
  local publish_aot
  local warning_scan_status

  publish_aot="$(dotnet msbuild "$project" \
    -nologo \
    -getProperty:PublishAot \
    -p:Configuration="$CONFIGURATION" \
    -p:RuntimeIdentifier="$RID" \
    | tr -d '\r')"

  if [[ "$publish_aot" != "true" ]]; then
    echo "error: PublishAot resolved to '$publish_aot' for $RID; expected true" >&2
    exit 1
  fi

  echo "==> Publishing Arcanum Native AOT ($RID, Version=$VERSION)"
  if ! dotnet publish "$project" \
      -c "$CONFIGURATION" \
      -r "$RID" \
      --self-contained true \
      -p:Version="$VERSION" \
      -o "$publish_dir" \
      2>&1 | tee "$publish_log"; then
    echo "error: dotnet publish Cli failed" >&2
    exit 1
  fi

  if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$publish_log"; then
    echo "error: Arcanum publish emitted warning output" >&2
    exit 1
  else
    warning_scan_status=$?
  fi

  if [[ "$warning_scan_status" -ne 1 ]]; then
    echo "error: could not scan Arcanum publish output (ripgrep exit $warning_scan_status)" >&2
    exit 1
  fi

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

  # Stage the whole publish directory (as publish_gui and the macOS packager do) and rename
  # only the apphost; the flattened native sidecars must travel with it.
  mkdir -p "$stage_dir"
  cp -a "$publish_dir/." "$stage_dir/"

  local staged_host
  staged_host="$stage_dir/$(basename "$published")"
  if [[ "$staged_host" != "$stage_dir/arcanum" ]]; then
    mv "$staged_host" "$stage_dir/arcanum"
  fi
  chmod +x "$stage_dir/arcanum"
  cp "$REPO_ROOT/README.md" "$stage_dir/README.md"

  require_staged_natives "$stage_dir" libe_sqlcipher.so libonigwrap.so
  require_native_aot_publish "$stage_dir"

  echo "==> Creating $archive"
  tar -C "$WORK/stage" -czf "$archive" "arcanum-linux-${ARCH_SUFFIX}"
}

publish_gui() {
  local product="$1"
  local project="$2"
  local folder_name="$3"
  local publish_dir="$WORK/${product}-publish"
  local publish_log="$WORK/${product}-publish.log"
  local stage_dir="$WORK/stage/${folder_name}"
  local archive="$OUTPUT_DIR/${folder_name}.tar.gz"
  local warning_scan_status

  echo "==> Publishing $product self-contained Avalonia folder ($RID, Version=$VERSION)"
  # Multi-file self-contained, not single-file.
  if ! dotnet publish "$project" \
      -c "$CONFIGURATION" \
      -r "$RID" \
      --self-contained true \
      -p:Version="$VERSION" \
      -p:UseAppHost=true \
      -p:PublishSingleFile=false \
      -o "$publish_dir" \
      2>&1 | tee "$publish_log"; then
    echo "error: dotnet publish $product failed" >&2
    exit 1
  fi

  if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$publish_log"; then
    echo "error: GUI publish emitted warning output" >&2
    exit 1
  else
    warning_scan_status=$?
  fi

  if [[ "$warning_scan_status" -ne 1 ]]; then
    echo "error: could not scan GUI publish output (ripgrep exit $warning_scan_status)" >&2
    exit 1
  fi

  mkdir -p "$stage_dir"
  cp -a "$publish_dir/." "$stage_dir/"
  cp "$REPO_ROOT/README.md" "$stage_dir/README.md"

  echo "==> Creating $archive"
  tar -C "$WORK/stage" -czf "$archive" "$folder_name"
}

require_native_sqlcipher_asset "$RID"

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
