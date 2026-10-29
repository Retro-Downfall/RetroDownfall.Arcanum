#!/usr/bin/env bash
# Exercise real bundle assembly and ad-hoc signing without a certificate, keychain, or install.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/app-bundle-test.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/bin"
cat >"$WORK/bin/dotnet" <<'STUB'
#!/bin/bash
set -euo pipefail
while [[ $# -gt 0 ]]; do
  if [[ "$1" == -o ]]; then
    mkdir -p "$2"
    cp -a "$PUBLISH_FIXTURE"/. "$2/"
    exit 0
  fi
  shift
done
exit 1
STUB
cat >"$WORK/bin/hdiutil" <<'STUB'
#!/bin/bash
set -euo pipefail
while [[ $# -gt 0 ]]; do
  if [[ "$1" == -srcfolder ]]; then
    cp -R "$2"/. "$CAPTURE_DIRECTORY/"
    exit 0
  fi
  shift
done
exit 1
STUB
chmod +x "$WORK/bin/dotnet" "$WORK/bin/hdiutil"

products=(compendium the-forge)
if [[ $# -gt 0 ]]; then
  products=("$@")
fi

for product in "${products[@]}"; do
  if [[ "$product" == compendium ]]; then
    app_name=Compendium
    executable=RetroDownfall.Compendium.Ux
  else
    app_name='The Forge'
    executable=RetroDownfall.TheForge.Ux
  fi
  export PUBLISH_FIXTURE="$WORK/$product/publish"
  export CAPTURE_DIRECTORY="$WORK/$product/captured"
  mkdir -p "$PUBLISH_FIXTURE/licenses/Microsoft.Data.Sqlite.Core" "$CAPTURE_DIRECTORY"
  for name in LICENSE.txt source-manifest.json README.md; do
    cp "$REPO_ROOT/vendor/Microsoft.Data.Sqlite.Core/$name" "$PUBLISH_FIXTURE/licenses/Microsoft.Data.Sqlite.Core/$name"
  done
  cp "$REPO_ROOT/src/RetroDownfall.Arcanum.Core/Weave/Tapestry/Raptor/UMAP-LICENSE.txt" "$PUBLISH_FIXTURE/licenses/umap-learn-LICENSE.txt"
  cp /bin/echo "$PUBLISH_FIXTURE/$executable"
  cp /bin/echo "$PUBLISH_FIXTURE/libfixture.dylib"
  printf 'managed assembly fixture\n' >"$PUBLISH_FIXTURE/managed.dll"
  printf 'debug symbols\n' >"$PUBLISH_FIXTURE/fixture.pdb"

  PATH="$WORK/bin:$PATH" /bin/bash "$SCRIPT_DIR/build-app-dmg.sh" \
    --product "$product" --version 0.1.0-beta.6 --marketing-version 0.1.0 \
    --bundle-version 1 --output-dir "$WORK/$product/output" --skip-sign

  app="$CAPTURE_DIRECTORY/$app_name.app"
  # Only publish and disk-image creation are stubbed. The native signer and verification are real.
  (
    # shellcheck source=common.sh
    source "$SCRIPT_DIR/common.sh"
    APPLE_SIGNING_IDENTITY=-
    CODESIGN_TIMESTAMP_ARG=--timestamp=none
    sign_app_bundle "$app"
  )
  [[ ! -e "$app/Contents/MacOS/licenses" ]]
  [[ ! -e "$app/Contents/MacOS/fixture.pdb" ]]
  diff -r "$PUBLISH_FIXTURE/licenses" "$app/Contents/Resources/licenses"
  cmp "$PUBLISH_FIXTURE/managed.dll" "$app/Contents/MacOS/managed.dll"
  /usr/bin/codesign --verify --strict --deep "$app"
  echo "PASS: $product preserves license bytes and passes native strict/deep bundle verification"

  printf 'tampered\n' >>"$app/Contents/Resources/licenses/Microsoft.Data.Sqlite.Core/LICENSE.txt"
  if /usr/bin/codesign --verify --strict --deep --verbose=4 "$app" >"$WORK/tamper.log" 2>&1; then
    echo 'FAIL: resource tampering was accepted' >&2
    exit 1
  fi
  cat "$WORK/tamper.log"
  grep -q 'modified' "$WORK/tamper.log"
  echo "PASS: $product rejects a changed sealed license"

  cp "$PUBLISH_FIXTURE/licenses/Microsoft.Data.Sqlite.Core/LICENSE.txt" "$app/Contents/Resources/licenses/Microsoft.Data.Sqlite.Core/LICENSE.txt"
  mkdir -p "$app/Contents/MacOS/Malformed.Code"
  cp /bin/echo "$app/Contents/MacOS/Malformed.Code/helper"
  if (
    # shellcheck source=common.sh
    source "$SCRIPT_DIR/common.sh"
    APPLE_SIGNING_IDENTITY=-
    CODESIGN_TIMESTAMP_ARG=--timestamp=none
    sign_app_bundle "$app"
  ) >"$WORK/malformed.log" 2>&1; then
    echo 'FAIL: a malformed nested code bundle was accepted' >&2
    exit 1
  fi
  grep -q 'Malformed.Code' "$WORK/malformed.log"
  echo "PASS: $product still refuses malformed nested code"
done
