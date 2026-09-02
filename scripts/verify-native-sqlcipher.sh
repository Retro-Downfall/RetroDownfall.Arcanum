#!/usr/bin/env bash
#
# Verifies the hermetic SQLCipher delivery against
# src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json.
#
#   --manifest-only   Provenance and shape only. No network, no binaries required.
#   --rid <RID>       Everything above plus the checked-in binary for one RID.
#   --all             Every shipping RID, including a twice-from-clean rebuild comparison.
#
# --all never skips: a RID this host cannot build or inspect is a failure, because a skipped RID
# reported as a pass is exactly the evidence gap this script exists to close.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

ASSET_PROJECT="${REPO_ROOT}/src/RetroDownfall.Arcanum.NativeSqlCipher"

MANIFEST="${ASSET_PROJECT}/native-source-manifest.json"

MODE=""

SELECTED_RID=""

FAILURES=0

UNVERIFIED=0

LF="
"

usage() {

  cat <<'EOF'
Usage: verify-native-sqlcipher.sh (--manifest-only | --rid <RID> | --all)

  --manifest-only  Validate provenance and manifest shape. Runs offline.
  --rid <RID>      Also validate the checked-in binary for one RID.
  --all            Validate every shipping RID and prove the build reproduces byte for byte.
EOF

}

fail() {

  echo "  FAIL: $*" >&2

  FAILURES=$((FAILURES + 1))

}

pass() {

  echo "  ok: $*"

}

# A property this script has no way to check on this host or in this job -- distinct from both a
# proven "ok" and a proven "FAIL". Printed rather than silently passed, because a claim nobody
# checked is not the same evidence as a claim that was checked and held.
unverified() {

  echo "  UNVERIFIED: $*"

  UNVERIFIED=$((UNVERIFIED + 1))

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

    fail "${label}: expected ${expected}, got ${actual}"

    return 1

  fi

  pass "${label} hash matches"

}

# Rejects anything that could read outside the asset project: absolute paths, parent traversal, and
# symlinks that point elsewhere.
verify_relative_path() {

  local relative="$1"

  local label="$2"

  case "${relative}" in

    /* | *..*)

      fail "${label}: path must stay inside the asset project: ${relative}"

      return 1

      ;;

  esac

  local resolved="${ASSET_PROJECT}/${relative}"

  if [ -L "${resolved}" ]; then

    fail "${label}: symlinks are not accepted: ${relative}"

    return 1

  fi

  return 0

}

# Whole-line membership test done in the shell rather than through a pipe.
#
# `producer | grep -q pattern` is unusable here: grep exits at the first match, the producer dies of
# SIGPIPE, and `set -o pipefail` reports the whole pipeline as failed even though the pattern was
# found. Every membership check in this script goes through this function instead.
contains_line() {

  local haystack="$1"

  local needle="$2"

  case "${LF}${haystack}${LF}" in

    *"${LF}${needle}${LF}"*) return 0 ;;

    *) return 1 ;;

  esac

}

rid_output_name() {

  case "$1" in

    osx-*) echo "libe_sqlcipher.dylib" ;;

    win-*) echo "e_sqlcipher.dll" ;;

    *) echo "" ;;

  esac

}

host_rid() {

  case "$(uname -s)" in

    Darwin)

      case "$(uname -m)" in

        arm64) echo "osx-arm64" ;;

        x86_64) echo "osx-x64" ;;

        *) echo "" ;;

      esac

      ;;

    *) echo "" ;;

  esac

}

verify_manifest() {

  echo "==> Manifest provenance"

  if [ ! -f "${MANIFEST}" ]; then

    fail "missing manifest: ${MANIFEST}"

    return

  fi

  if ! jq -e . "${MANIFEST}" >/dev/null 2>&1; then

    fail "manifest is not valid JSON"

    return

  fi

  local schema

  schema="$(read_manifest_value '.schemaVersion')"

  if [ "${schema}" != "1" ]; then

    fail "unsupported manifest schema version: ${schema}"

    return

  fi

  pass "schema version 1"

  local expected_rids="osx-arm64 win-arm64 win-x64"

  local actual_rids

  actual_rids="$(jq -r '[.assets[].rid] | sort | join(" ")' "${MANIFEST}")"

  if [ "${actual_rids}" != "${expected_rids}" ]; then

    fail "shipping RIDs are '${actual_rids}', expected '${expected_rids}'"

  else

    pass "shipping RIDs: ${actual_rids}"

  fi

  local unique_paths total_paths

  unique_paths="$(jq -r '[.assets[].path] | unique | length' "${MANIFEST}")"

  total_paths="$(jq -r '[.assets[].path] | length' "${MANIFEST}")"

  if [ "${unique_paths}" != "${total_paths}" ]; then

    fail "asset paths are not unique"

  else

    pass "asset paths are unique"

  fi

  # Every declared digest must be a lowercase 64-character SHA-256.
  local bad_digests

  bad_digests="$(jq -r '
    [
      .sqlcipher.archiveSha256,
      .openssl.archiveSha256,
      .openssl.publicKeysSha256
    ]
    + [.licenses[].sha256]
    + [.sboms[].sha256]
    + [.assets[] | select(.status == "verified") | .sha256]
    | map(select(test("^[0-9a-f]{64}$") | not))
    | length' "${MANIFEST}")"

  if [ "${bad_digests}" != "0" ]; then

    fail "${bad_digests} declared digest(s) are not lowercase 64-character SHA-256"

  else

    pass "all declared digests are well formed"

  fi

  local required_options="SQLCIPHER_CRYPTO_OPENSSL SQLITE_ENABLE_FTS5 SQLITE_HAS_CODEC SQLITE_OMIT_LOAD_EXTENSION SQLITE_TEMP_STORE=2 SQLITE_THREADSAFE=1"

  local option

  for option in ${required_options}; do

    if ! jq -e --arg option "${option}" '.compileOptions | index($option)' "${MANIFEST}" >/dev/null; then

      fail "manifest does not pin compile option ${option}"

    fi

  done

  pass "required compile options pinned"

  if [ "$(read_manifest_value '.runtimePragmas.sqliteVersion')" != "$(read_manifest_value '.sqliteVersion')" ]; then

    fail "runtimePragmas.sqliteVersion disagrees with sqliteVersion"

  else

    pass "runtime SQLite version agrees with the pinned source version"

  fi

  local relative

  while IFS= read -r relative; do

    verify_relative_path "${relative}" "license" || continue

    local expected

    expected="$(jq -er --arg path "${relative}" '.licenses[] | select(.path == $path) | .sha256' "${MANIFEST}")"

    if [ ! -f "${ASSET_PROJECT}/${relative}" ]; then

      fail "missing license file: ${relative}"

      continue

    fi

    verify_sha256 "${ASSET_PROJECT}/${relative}" "${expected}" "license ${relative}" || true

  done < <(jq -r '.licenses[].path' "${MANIFEST}")

  while IFS= read -r relative; do

    verify_relative_path "${relative}" "sbom" || continue

    local expected

    expected="$(jq -er --arg path "${relative}" '.sboms[] | select(.path == $path) | .sha256' "${MANIFEST}")"

    if [ ! -f "${ASSET_PROJECT}/${relative}" ]; then

      fail "missing SBOM file: ${relative}"

      continue

    fi

    verify_sha256 "${ASSET_PROJECT}/${relative}" "${expected}" "sbom ${relative}" || true

  done < <(jq -r '.sboms[].path' "${MANIFEST}")

}

verify_rid_binary() {

  local rid="$1"

  echo "==> Asset ${rid}"

  local record

  record="$(jq -c --arg rid "${rid}" '.assets[] | select(.rid == $rid)' "${MANIFEST}")"

  if [ -z "${record}" ]; then

    fail "${rid} is not a shipping RID"

    return

  fi

  local status

  status="$(echo "${record}" | jq -r '.status')"

  local relative

  relative="$(echo "${record}" | jq -r '.path')"

  local expected_name

  expected_name="$(rid_output_name "${rid}")"

  if [ "$(echo "${record}" | jq -r '.outputFileName')" != "${expected_name}" ]; then

    fail "${rid}: manifest output filename does not match the platform convention"

  fi

  verify_relative_path "${relative}" "${rid} asset" || return

  local file="${ASSET_PROJECT}/${relative}"

  if [ "${status}" = "pending" ]; then

    if [ -f "${file}" ]; then

      fail "${rid}: a binary is checked in but the manifest records it as pending"

    else

      fail "${rid}: no verified binary is checked in yet (status: pending). Build it on a ${rid} runner and record its hash."

    fi

    return

  fi

  if [ ! -f "${file}" ]; then

    fail "${rid}: manifest records a verified asset but ${relative} is absent"

    return

  fi

  verify_sha256 "${file}" "$(echo "${record}" | jq -r '.sha256')" "${rid} binary" || true

  case "${rid}" in

    osx-*)

      local description

      description="$(file "${file}")"

      case "${description}" in

        *"Mach-O 64-bit dynamically linked shared library"*) pass "${rid} Mach-O format" ;;

        *) fail "${rid}: not a Mach-O 64-bit dynamic library" ;;

      esac

      ;;

    win-*)

      local magic

      magic="$(head -c 2 "${file}")"

      if [ "${magic}" != "MZ" ]; then

        fail "${rid}: not a PE image"

      else

        pass "${rid} PE format"

      fi

      ;;

  esac

  verify_rid_symbols "${rid}" "${file}"

  verify_rid_compile_options "${rid}" "${file}"

  verify_rid_dependencies "${rid}" "${file}" "${record}"

}

# The shipped library must export the SQLite C API and nothing else. A statically linked OpenSSL
# that exports its own symbols can be interposed at load time, which would defeat the point of
# linking it statically.
verify_rid_symbols() {

  local rid="$1"

  local file="$2"

  case "${rid}" in

    osx-*)

      if ! command -v nm >/dev/null 2>&1; then

        fail "${rid}: nm is required to inspect exported symbols"

        return

      fi

      local exported

      exported="$(nm -gU "${file}" 2>/dev/null | awk '{print $3}')"

      if contains_line "${exported}" "_sqlite3_open"; then

        pass "${rid} exports the SQLite C API"

      else

        fail "${rid}: the SQLite C API is not exported"

      fi

      local leaked

      leaked="$(printf '%s\n' "${exported}" | grep -vc "^_sqlite3_" || true)"

      if [ "${leaked}" != "0" ]; then

        fail "${rid}: ${leaked} non-SQLite symbol(s) are exported, including statically linked crypto"

      else

        pass "${rid} exports no symbol outside the SQLite C API"

      fi

      if contains_line "${exported}" "_sqlite3_load_extension"; then

        fail "${rid}: sqlite3_load_extension is exported despite SQLITE_OMIT_LOAD_EXTENSION"

      else

        pass "${rid} omits the extension loading entry points"

      fi

      ;;

    win-*)

      # No job in verify-native-sqlcipher.yml runs dumpbin /EXPORTS (or any equivalent) against
      # the checked-in win-* binary, so this script cannot claim the symbol table was inspected.
      # The construction-time guarantee is real -- build-native-sqlcipher.ps1 builds the .def
      # export list from dumpbin /symbols filtered to sqlite3_* and links with /DEF, so a leaked
      # export is not possible from this build path -- but that is a property of the build script,
      # not something this verification step checked, so it is reported as such rather than as a
      # pass.
      unverified "${rid} symbol table is not inspected by any job (see build-native-sqlcipher.ps1's /DEF export list for the construction-time guarantee)"

      ;;

  esac

}

verify_rid_compile_options() {

  local rid="$1"

  local file="$2"

  # SQLite stores its compile options as literal strings so sqlite3_compileoption_get can return
  # them; that makes the security-relevant set checkable without executing the library.
  #
  # The strings output is captured once rather than piped into each grep: under `set -o pipefail`,
  # `grep -q` exits at the first match, strings dies of SIGPIPE, and the pipeline reports failure
  # even though the option was found.
  local literals

  literals="$(strings -a "${file}")"

  local option missing=0

  for option in ENABLE_FTS5 OMIT_LOAD_EXTENSION THREADSAFE=1 TEMP_STORE=2; do

    if ! contains_line "${literals}" "${option}"; then

      fail "${rid}: compile option ${option} is not present in the built library"

      missing=1

    fi

  done

  if [ "${missing}" -eq 0 ]; then

    pass "${rid} compile options match the manifest"

  fi

}

verify_rid_dependencies() {

  local rid="$1"

  local file="$2"

  local record="$3"

  case "${rid}" in

    osx-*)

      local allowed self_id actual dependency otool_status=0

      allowed="$(echo "${record}" | jq -r '.dynamicDependencies[]')"

      # `|| true` used to swallow a missing/failing otool along with a genuinely empty result,
      # and both read as "no dependencies to complain about" -- the exact shape of a build that
      # accidentally linked OpenSSL dynamically. Capture the pipeline's own exit status instead
      # (set -o pipefail makes it otool's, not tail's or awk's) and treat an empty result as a
      # failure: a Mach-O dylib always imports at least libSystem, so empty means the tool did
      # not run rather than that there is nothing to report.
      actual="$(otool -L "${file}" | tail -n +2 | awk '{print $1}')" || otool_status=$?

      if [ "${otool_status}" -ne 0 ]; then

        fail "${rid}: otool -L exited ${otool_status} against ${file}; cannot verify dynamic dependencies"

        return

      fi

      if [ -z "${actual}" ]; then

        fail "${rid}: otool -L reported no dynamic dependencies for ${file}; a Mach-O dylib always imports at least libSystem"

        return

      fi

      # otool -L's first entry after the header is the dylib's own LC_ID_DYLIB self-reference
      # (this asset's is @rpath/libe_sqlcipher.dylib), not a dependency; otool -D returns exactly
      # that one line. Excluding only this entry -- rather than every @rpath/ entry, as before --
      # keeps a genuine @rpath/ dependency (a dynamically linked libcrypto, say) visible to the
      # comparison below instead of discarding it unseen. Same status-capture reasoning as the
      # otool -L call above: without it, a failing otool -D aborts the whole script under set -e,
      # with no FAIL line and no summary, instead of reporting the failure honestly.
      self_id="$(otool -D "${file}" | tail -n +2)" || otool_status=$?

      if [ "${otool_status}" -ne 0 ]; then

        fail "${rid}: otool -D exited ${otool_status} against ${file}; cannot verify dynamic dependencies"

        return

      fi

      local unexpected=0

      while IFS= read -r dependency; do

        [ -z "${dependency}" ] && continue

        [ "${dependency}" = "${self_id}" ] && continue

        if ! contains_line "${allowed}" "${dependency}"; then

          fail "${rid}: undeclared dynamic dependency ${dependency}"

          unexpected=1

        fi

      done <<< "${actual}"

      if [ "${unexpected}" -eq 0 ]; then

        pass "${rid} links only its declared dynamic dependencies"

      fi

      ;;

    win-*)

      # No job in verify-native-sqlcipher.yml runs dumpbin /DEPENDENTS (or any equivalent) against
      # the checked-in win-* binary, so nothing compares its import table to the manifest's
      # declared dynamicDependencies (ADVAPI32.dll, KERNEL32.dll, bcrypt.dll) even though
      # build-native-sqlcipher.ps1 also links ws2_32.lib, crypt32.lib and user32.lib. Unlike the
      # symbol table, there is no construction-time guarantee standing in for this check, so an
      # unlisted import would ship undetected until an import-table step exists in the Windows job.
      unverified "${rid} import table is not checked by any job; the manifest's dynamicDependencies are unverified against the built binary"

      ;;

  esac

}

# Rebuilds from a clean work area and requires the result to equal the checked-in binary byte for
# byte. Only a host that can natively build the RID may attempt this.
verify_rid_reproducibility() {

  local rid="$1"

  echo "==> Reproducibility ${rid}"

  if [ "${rid}" != "$(host_rid)" ]; then

    fail "${rid}: this host cannot rebuild the RID, so reproducibility is unproven. Run the ${rid} native job."

    return

  fi

  local status

  status="$(jq -r --arg rid "${rid}" '.assets[] | select(.rid == $rid) | .status' "${MANIFEST}")"

  if [ "${status}" != "verified" ]; then

    fail "${rid}: cannot compare a rebuild against a pending asset"

    return

  fi

  local work

  work="$(mktemp -d)"

  # shellcheck disable=SC2064
  trap "rm -rf '${work}'" RETURN

  if ! "${SCRIPT_DIR}/build-native-sqlcipher.sh" --rid "${rid}" --output "${work}" >"${work}/build.log" 2>&1; then

    fail "${rid}: rebuild failed; see ${work}/build.log"

    return

  fi

  local rebuilt expected

  rebuilt="$(sha256_of "${work}/$(rid_output_name "${rid}")")"

  expected="$(jq -r --arg rid "${rid}" '.assets[] | select(.rid == $rid) | .sha256' "${MANIFEST}")"

  if [ "${rebuilt}" != "${expected}" ]; then

    fail "${rid}: rebuild produced ${rebuilt}, checked-in asset is ${expected}"

  else

    pass "${rid} rebuild is byte-identical to the checked-in asset"

  fi

}

while [ "$#" -gt 0 ]; do

  case "$1" in

    --manifest-only) MODE="manifest" ; shift ;;

    --rid) MODE="rid" ; SELECTED_RID="${2:-}" ; shift 2 ;;

    --all) MODE="all" ; shift ;;

    -h | --help) usage ; exit 0 ;;

    *) echo "Unknown argument: $1" >&2 ; usage >&2 ; exit 2 ;;

  esac

done

if [ -z "${MODE}" ]; then

  usage >&2

  exit 2

fi

require_cmd jq awk

verify_manifest

case "${MODE}" in

  manifest) ;;

  rid)

    if [ -z "${SELECTED_RID}" ]; then

      usage >&2

      exit 2

    fi

    verify_rid_binary "${SELECTED_RID}"

    ;;

  all)

    while IFS= read -r rid; do

      verify_rid_binary "${rid}"

      verify_rid_reproducibility "${rid}"

    done < <(jq -r '.assets[].rid' "${MANIFEST}")

    # --all's own contract, in the usage banner above, is "every shipping RID", never skipped. An
    # UNVERIFIED report from any RID's checks is exactly the skip that contract rules out, so it
    # fails the run here -- even though --rid and --manifest-only, which is what CI actually
    # invokes, leave the same report non-failing.
    if [ "${UNVERIFIED}" -ne 0 ]; then

      fail "--all found ${UNVERIFIED} unverified check(s) above; --all never skips, and an unverified check is not a pass"

    fi

    ;;

esac

echo

if [ "${FAILURES}" -ne 0 ]; then

  echo "verify-native-sqlcipher: ${FAILURES} check(s) failed." >&2

  exit 1

fi

if [ "${UNVERIFIED}" -ne 0 ]; then

  echo "verify-native-sqlcipher: all checks passed (${UNVERIFIED} unverified)."

else

  echo "verify-native-sqlcipher: all checks passed."

fi
