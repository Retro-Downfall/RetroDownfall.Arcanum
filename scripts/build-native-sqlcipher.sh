#!/usr/bin/env bash
#
# Builds one hermetic SQLCipher shared library from the sources pinned in
# src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json.
#
# Every input is fetched by hash, OpenSSL's release signature is verified against the fingerprint
# recorded in the manifest, and OpenSSL is linked statically with hidden symbols so the shipped
# library has no ambient crypto dependency. The build is reproducible: two runs from clean work
# areas produce byte-identical output.
#
# Usage:
#   scripts/build-native-sqlcipher.sh --rid <RID> --output <directory> [--keep-work]
#
# Windows RIDs are built by scripts/build-native-sqlcipher.ps1 on a Windows runner; this script
# refuses them rather than producing a cross-built approximation.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

ASSET_PROJECT="${REPO_ROOT}/src/RetroDownfall.Arcanum.NativeSqlCipher"

MANIFEST="${ASSET_PROJECT}/native-source-manifest.json"

RID=""

OUTPUT_DIR=""

KEEP_WORK="0"

usage() {

  cat <<'EOF'
Usage: build-native-sqlcipher.sh --rid <RID> --output <directory> [--keep-work]

  --rid       One of: osx-arm64, win-x64, win-arm64
  --output    Directory the built library and attestation are written to
  --keep-work Leave the temporary work area in place for inspection

Windows RIDs must be built with scripts/build-native-sqlcipher.ps1 on a Windows runner.
EOF

}

require_cmd() {

  local missing=0

  for cmd in "$@"; do

    if ! command -v "${cmd}" >/dev/null 2>&1; then

      echo "Required command not found: ${cmd}" >&2

      missing=1

    fi

  done

  if [ "${missing}" -ne 0 ]; then

    exit 1

  fi

}

read_manifest_value() {

  jq -er "$1" "${MANIFEST}"

}

# Hashes a file with the platform's SHA-256 tool and prints the bare lowercase digest.
sha256_of() {

  if command -v shasum >/dev/null 2>&1; then

    shasum -a 256 "$1" | awk '{print $1}'

  else

    sha256sum "$1" | awk '{print $1}'

  fi

}

verify_sha256() {

  local file="$1"

  local expected="$2"

  local label="$3"

  local actual

  actual="$(sha256_of "${file}")"

  if [ "${actual}" != "${expected}" ]; then

    echo "Hash mismatch for ${label}." >&2

    echo "  expected: ${expected}" >&2

    echo "  actual:   ${actual}" >&2

    exit 1

  fi

  echo "  verified ${label} (${actual})"

}

rid_output_name() {

  case "$1" in

    osx-arm64) echo "libe_sqlcipher.dylib" ;;

    win-x64 | win-arm64) echo "e_sqlcipher.dll" ;;

    *) echo "Unsupported RID: $1" >&2 ; exit 1 ;;

  esac

}

while [ "$#" -gt 0 ]; do

  case "$1" in

    --rid) RID="${2:-}" ; shift 2 ;;

    --output) OUTPUT_DIR="${2:-}" ; shift 2 ;;

    --keep-work) KEEP_WORK="1" ; shift ;;

    -h | --help) usage ; exit 0 ;;

    *) echo "Unknown argument: $1" >&2 ; usage >&2 ; exit 2 ;;

  esac

done

if [ -z "${RID}" ] || [ -z "${OUTPUT_DIR}" ]; then

  usage >&2

  exit 2

fi

case "${RID}" in

  osx-arm64) ;;

  win-x64 | win-arm64)

    echo "RID ${RID} is built by scripts/build-native-sqlcipher.ps1 on a Windows runner." >&2

    echo "This script does not cross-build Windows binaries." >&2

    exit 2

    ;;

  *)

    echo "Unsupported RID: ${RID}" >&2

    usage >&2

    exit 2

    ;;

esac

if [ "$(uname -s)" != "Darwin" ]; then

  echo "RID ${RID} must be built on macOS; this host is $(uname -s)." >&2

  exit 2

fi

require_cmd curl jq gpg tar make cc clang install_name_tool strip git awk

if [ ! -f "${MANIFEST}" ]; then

  echo "Missing native source manifest: ${MANIFEST}" >&2

  exit 1

fi

OUTPUT_NAME="$(rid_output_name "${RID}")"

mkdir -p "${OUTPUT_DIR}"

OUTPUT_DIR="$(cd -- "${OUTPUT_DIR}" && pwd)"

SQLCIPHER_TAG="$(read_manifest_value '.sqlcipher.tag')"

SQLCIPHER_TAG_OBJECT="$(read_manifest_value '.sqlcipher.tagObject')"

SQLCIPHER_COMMIT="$(read_manifest_value '.sqlcipher.commit')"

SQLCIPHER_URL="$(read_manifest_value '.sqlcipher.archiveUrl')"

SQLCIPHER_SHA="$(read_manifest_value '.sqlcipher.archiveSha256')"

SQLCIPHER_REPO="$(read_manifest_value '.sqlcipher.repository')"

SOURCE_DATE_EPOCH="$(read_manifest_value '.sqlcipher.sourceDateEpoch')"

export SOURCE_DATE_EPOCH

OPENSSL_VERSION="$(read_manifest_value '.openssl.version')"

OPENSSL_URL="$(read_manifest_value '.openssl.archiveUrl')"

OPENSSL_SHA="$(read_manifest_value '.openssl.archiveSha256')"

OPENSSL_SIG_URL="$(read_manifest_value '.openssl.signatureUrl')"

OPENSSL_KEYS_URL="$(read_manifest_value '.openssl.publicKeysUrl')"

OPENSSL_KEYS_SHA="$(read_manifest_value '.openssl.publicKeysSha256')"

OPENSSL_FINGERPRINT="$(read_manifest_value '.openssl.signerFingerprint')"

WORK_DIR="$(mktemp -d)"

cleanup() {

  if [ "${KEEP_WORK}" = "1" ]; then

    echo "Work area retained: ${WORK_DIR}"

    return

  fi

  rm -rf "${WORK_DIR}"

}

trap cleanup EXIT

echo "==> Hermetic SQLCipher build"

echo "    RID:        ${RID}"

echo "    SQLCipher:  ${SQLCIPHER_TAG} (${SQLCIPHER_COMMIT})"

echo "    OpenSSL:    ${OPENSSL_VERSION}"

echo "    Work area:  ${WORK_DIR}"

echo "==> Proving the pinned tag object resolves to the pinned commit"

REMOTE_REFS="$(git ls-remote "${SQLCIPHER_REPO}" "refs/tags/${SQLCIPHER_TAG}" "refs/tags/${SQLCIPHER_TAG}^{}")"

REMOTE_TAG_OBJECT="$(echo "${REMOTE_REFS}" | awk -v ref="refs/tags/${SQLCIPHER_TAG}" '$2 == ref {print $1}')"

REMOTE_COMMIT="$(echo "${REMOTE_REFS}" | awk -v ref="refs/tags/${SQLCIPHER_TAG}^{}" '$2 == ref {print $1}')"

if [ "${REMOTE_TAG_OBJECT}" != "${SQLCIPHER_TAG_OBJECT}" ]; then

  echo "Upstream tag object ${REMOTE_TAG_OBJECT} does not match pinned ${SQLCIPHER_TAG_OBJECT}." >&2

  exit 1

fi

if [ "${REMOTE_COMMIT}" != "${SQLCIPHER_COMMIT}" ]; then

  echo "Upstream tag peels to ${REMOTE_COMMIT}, not pinned ${SQLCIPHER_COMMIT}." >&2

  exit 1

fi

echo "  verified ${SQLCIPHER_TAG} -> ${SQLCIPHER_TAG_OBJECT} -> ${SQLCIPHER_COMMIT}"

echo "==> Fetching and verifying pinned sources"

curl -sSL --fail --proto '=https' --tlsv1.2 -o "${WORK_DIR}/sqlcipher.tar.gz" "${SQLCIPHER_URL}"

verify_sha256 "${WORK_DIR}/sqlcipher.tar.gz" "${SQLCIPHER_SHA}" "SQLCipher archive"

curl -sSL --fail --proto '=https' --tlsv1.2 -o "${WORK_DIR}/openssl.tar.gz" "${OPENSSL_URL}"

verify_sha256 "${WORK_DIR}/openssl.tar.gz" "${OPENSSL_SHA}" "OpenSSL archive"

curl -sSL --fail --proto '=https' --tlsv1.2 -o "${WORK_DIR}/openssl.tar.gz.asc" "${OPENSSL_SIG_URL}"

curl -sSL --fail --proto '=https' --tlsv1.2 -o "${WORK_DIR}/pubkeys.asc" "${OPENSSL_KEYS_URL}"

verify_sha256 "${WORK_DIR}/pubkeys.asc" "${OPENSSL_KEYS_SHA}" "OpenSSL public keys"

export GNUPGHOME="${WORK_DIR}/gnupg"

mkdir -p "${GNUPGHOME}"

chmod 700 "${GNUPGHOME}"

gpg --batch --quiet --import "${WORK_DIR}/pubkeys.asc"

GPG_STATUS="${WORK_DIR}/gpg-status.txt"

if ! gpg --batch --status-file "${GPG_STATUS}" --verify \
  "${WORK_DIR}/openssl.tar.gz.asc" "${WORK_DIR}/openssl.tar.gz" >/dev/null 2>&1; then

  echo "OpenSSL release signature did not verify." >&2

  exit 1

fi

if ! grep -q "VALIDSIG ${OPENSSL_FINGERPRINT}" "${GPG_STATUS}"; then

  echo "OpenSSL signature is not from the pinned signer ${OPENSSL_FINGERPRINT}." >&2

  exit 1

fi

echo "  verified OpenSSL signature from ${OPENSSL_FINGERPRINT}"

echo "==> Extracting sources"

mkdir -p "${WORK_DIR}/src"

tar xzf "${WORK_DIR}/sqlcipher.tar.gz" -C "${WORK_DIR}/src"

tar xzf "${WORK_DIR}/openssl.tar.gz" -C "${WORK_DIR}/src"

SQLCIPHER_SRC="${WORK_DIR}/src/sqlcipher-${SQLCIPHER_COMMIT}"

OPENSSL_SRC="${WORK_DIR}/src/openssl-${OPENSSL_VERSION}"

OPENSSL_PREFIX="${WORK_DIR}/openssl-install"

if [ ! -d "${SQLCIPHER_SRC}" ] || [ ! -d "${OPENSSL_SRC}" ]; then

  echo "Extracted source layout did not match the pinned identities." >&2

  exit 1

fi

MACOS_DEPLOYMENT_TARGET="$(read_manifest_value ".assets[] | select(.rid == \"${RID}\") | .deploymentTarget")"

export MACOSX_DEPLOYMENT_TARGET="${MACOS_DEPLOYMENT_TARGET}"

OPENSSL_TARGET="darwin64-arm64-cc"

ARCH_FLAG="arm64"

echo "==> Building OpenSSL ${OPENSSL_VERSION} (static, hidden symbols)"

# OpenSSL compiles its configured directories and its own CFLAGS string into libcrypto, so a work
# area under mktemp would embed a different random path on every run. Configure against a fixed
# virtual prefix and redirect only the installation with DESTDIR: the recorded strings become
# constant while the files still land inside the disposable work area.
OPENSSL_VIRTUAL_PREFIX="/openssl"

OPENSSL_STAGE="${OPENSSL_PREFIX}${OPENSSL_VIRTUAL_PREFIX}"

(

  cd "${OPENSSL_SRC}"

  ./Configure "${OPENSSL_TARGET}" \
    --prefix="${OPENSSL_VIRTUAL_PREFIX}" \
    --openssldir="${OPENSSL_VIRTUAL_PREFIX}/ssl" \
    --libdir=lib \
    no-shared \
    no-module \
    no-tests \
    no-docs \
    no-legacy \
    no-engine \
    no-apps \
    -fPIC \
    -fvisibility=hidden \
    >/dev/null

  make -j"$(sysctl -n hw.ncpu)" build_libs >/dev/null

  make install_dev DESTDIR="${OPENSSL_PREFIX}" >/dev/null

)

if [ ! -f "${OPENSSL_STAGE}/lib/libcrypto.a" ]; then

  echo "OpenSSL did not install libcrypto.a into the staged prefix." >&2

  exit 1

fi

echo "  built libcrypto.a ($(sha256_of "${OPENSSL_STAGE}/lib/libcrypto.a"))"

echo "==> Generating the SQLCipher amalgamation"

(

  cd "${SQLCIPHER_SRC}"

  ./configure --disable-tcl --with-tempstore=yes >/dev/null

  make sqlite3.c >/dev/null 2>&1

)

if [ ! -f "${SQLCIPHER_SRC}/sqlite3.c" ]; then

  echo "SQLCipher amalgamation was not generated." >&2

  exit 1

fi

echo "  generated sqlite3.c ($(sha256_of "${SQLCIPHER_SRC}/sqlite3.c"))"

# Compile definitions. Kept as an array so no flag is ever re-split by the shell. Read with a
# while loop rather than mapfile, which macOS's bash 3.2 does not have.
DEFINES=()

while IFS= read -r option; do

  DEFINES+=("-D${option}")

done < <(read_manifest_value '.compileOptions[]')

if [ "${#DEFINES[@]}" -eq 0 ]; then

  echo "Manifest declared no compile options." >&2

  exit 1

fi

HARDENING=(
  -fstack-protector-strong
  -D_FORTIFY_SOURCE=2
  -fPIC
  -fvisibility=hidden
  -O2
)

# The work area is a fresh mktemp directory on every run, so its path must never reach the
# binary's debug or diagnostic strings; SOURCE_DATE_EPOCH covers anything that embeds a timestamp.
DETERMINISM=(
  -ffile-prefix-map="${SQLCIPHER_SRC}=/sqlcipher"
)

# Exports only the documented SQLite C API; every SQLCipher internal and every statically linked
# OpenSSL symbol stays hidden.
API_VISIBILITY=(
  '-DSQLITE_API=__attribute__((visibility("default")))'
)

echo "==> Linking ${OUTPUT_NAME}"

BUILD_DIR="${WORK_DIR}/build"

mkdir -p "${BUILD_DIR}"

# OpenSSL's perlasm-generated assembly declares its symbols global directly, so -fvisibility=hidden
# never sees them and AES/SHA3/ChaCha20 helpers would be exported from the shipped library. An
# explicit export list is the only thing that closes the surface to the SQLite C API alone.
EXPORT_LIST="${BUILD_DIR}/exported.symbols"

printf '_sqlite3_*\n' > "${EXPORT_LIST}"

clang \
  -arch "${ARCH_FLAG}" \
  -dynamiclib \
  -install_name "@rpath/${OUTPUT_NAME}" \
  -o "${BUILD_DIR}/${OUTPUT_NAME}" \
  "${SQLCIPHER_SRC}/sqlite3.c" \
  "${DEFINES[@]}" \
  "${API_VISIBILITY[@]}" \
  "${HARDENING[@]}" \
  "${DETERMINISM[@]}" \
  -I"${SQLCIPHER_SRC}" \
  -I"${OPENSSL_STAGE}/include" \
  "${OPENSSL_STAGE}/lib/libcrypto.a" \
  -Wl,-exported_symbols_list,"${EXPORT_LIST}" \
  -Wl,-dead_strip \
  -Wl,-no_adhoc_codesign \
  -Wl,-x

strip -x -S "${BUILD_DIR}/${OUTPUT_NAME}"

# arm64 macOS refuses to dlopen an unsigned Mach-O, and strip invalidates whatever signature the
# linker produced, so the ad-hoc signature has to be applied last. It stays reproducible: an ad-hoc
# signature is derived from the file's own contents and the explicit identifier, with no timestamp
# and no path dependence. Packaging re-signs with the real identity for notarization.
codesign --force --sign - --identifier "arcanum.${OUTPUT_NAME}" --timestamp=none \
  "${BUILD_DIR}/${OUTPUT_NAME}"

OUTPUT_SHA="$(sha256_of "${BUILD_DIR}/${OUTPUT_NAME}")"

cp "${BUILD_DIR}/${OUTPUT_NAME}" "${OUTPUT_DIR}/${OUTPUT_NAME}"

echo "  linked ${OUTPUT_NAME} (${OUTPUT_SHA})"

echo "==> Writing build attestation"

COMPILER_ID="$(clang --version | head -1)"

LINKER_ID="$(ld -v 2>&1 | head -1)"

jq -n \
  --arg rid "${RID}" \
  --arg outputFileName "${OUTPUT_NAME}" \
  --arg sha256 "${OUTPUT_SHA}" \
  --arg sqlcipherCommit "${SQLCIPHER_COMMIT}" \
  --arg sqlcipherArchiveSha256 "${SQLCIPHER_SHA}" \
  --arg amalgamationSha256 "$(sha256_of "${SQLCIPHER_SRC}/sqlite3.c")" \
  --arg opensslVersion "${OPENSSL_VERSION}" \
  --arg opensslArchiveSha256 "${OPENSSL_SHA}" \
  --arg libcryptoSha256 "$(sha256_of "${OPENSSL_STAGE}/lib/libcrypto.a")" \
  --arg compiler "${COMPILER_ID}" \
  --arg linker "${LINKER_ID}" \
  --arg image "$(uname -srm)" \
  --arg sourceDateEpoch "${SOURCE_DATE_EPOCH}" \
  --argjson compileOptions "$(read_manifest_value '.compileOptions')" \
  '{
     rid: $rid,
     outputFileName: $outputFileName,
     sha256: $sha256,
     sqlcipher: { commit: $sqlcipherCommit, archiveSha256: $sqlcipherArchiveSha256, amalgamationSha256: $amalgamationSha256 },
     openssl: { version: $opensslVersion, archiveSha256: $opensslArchiveSha256, libcryptoSha256: $libcryptoSha256 },
     toolchain: { compiler: $compiler, linker: $linker, image: $image },
     sourceDateEpoch: $sourceDateEpoch,
     compileOptions: $compileOptions
   }' > "${OUTPUT_DIR}/${RID}-attestation.json"

echo "==> Done"

echo "    ${OUTPUT_DIR}/${OUTPUT_NAME}"

echo "    ${OUTPUT_DIR}/${RID}-attestation.json"
