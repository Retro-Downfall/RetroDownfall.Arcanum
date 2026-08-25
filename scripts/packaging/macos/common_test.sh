#!/usr/bin/env bash
# Unit tests for the local (development) signing helpers in common.sh.
#
# Local signing resolves the identity from the operator's own keychain rather than from repository
# secrets, so the failure modes it has to refuse are all silent ones: picking an arbitrary identity
# when several are installed, accepting a certificate Apple did not issue, or leaving the release
# path's secure timestamp switched off. Each of those produces an artifact that signs and verifies
# and is still wrong, so they are pinned here.
#
# `security` and `codesign` are driven through stubs on PATH; nothing here touches a real keychain
# or signs anything.
#
# Usage: common_test.sh [path-to-common.sh]

set -uo pipefail

TEST_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMMON="${1:-$TEST_DIR/common.sh}"

if [[ ! -r "$COMMON" ]]; then
  echo "error: common.sh not found: $COMMON" >&2
  exit 1
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/macos-signing-test.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

BIN="$WORK/bin"
mkdir -p "$BIN"
PATH="$BIN:$PATH"
export PATH

FAILED=0

fail() {
  echo "FAIL: $1" >&2
  FAILED=$((FAILED + 1))
}

pass() {
  echo "ok   $1"
}

expect_eq() {
  local name="$1"
  local expected="$2"
  local actual="$3"

  if [[ "$expected" == "$actual" ]]; then
    pass "$name"
  else
    fail "$name — expected [$expected], got [$actual]"
  fi
}

expect_contains() {
  local name="$1"
  local needle="$2"
  local haystack="$3"

  if [[ "$haystack" == *"$needle"* ]]; then
    pass "$name"
  else
    fail "$name — [$needle] not found in [$haystack]"
  fi
}

# Installs a `security` stub whose `find-identity` prints the identities passed here, formatted
# exactly as the real tool does. Called with no arguments it reports an empty keychain.
stub_identities() {
  {
    echo '#!/usr/bin/env bash'
    # shellcheck disable=SC2016  # the stub's own source text, not an expansion for this shell
    echo 'if [[ "${1:-}" != "find-identity" ]]; then exit 0; fi'
    echo 'cat <<'"'"'IDENTITIES'"'"''

    local index=0
    local entry
    for entry in "$@"; do
      index=$((index + 1))
      echo "  $index) $entry"
    done

    echo "     $index valid identities found"
    echo 'IDENTITIES'
  } >"$BIN/security"

  chmod +x "$BIN/security"
}

# `codesign` stub: records its own argument vector, one argument per line, in $WORK/codesign.args.
cat >"$BIN/codesign" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" >"$CODESIGN_ARGS"
STUB
chmod +x "$BIN/codesign"

export CODESIGN_ARGS="$WORK/codesign.args"

DEVELOPMENT='A1B2C3D4E5F60718293A4B5C6D7E8F901A2B3C4D "Apple Development: Ada Lovelace (TEAM123456)"'
DEVELOPER_ID='0F1E2D3C4B5A69788796A5B4C3D2E1F009182736 "Developer ID Application: Ada Lovelace (TEAM123456)"'
SELF_SIGNED='1122334455667788990011223344556677889900 "localhost"'

# ---------------------------------------------------------------------------
# Identity resolution
# ---------------------------------------------------------------------------

# An empty keychain must be a hard error naming the command that lists identities; the alternative
# is a build that quietly produces the unsigned artifact the operator asked to have signed.
stub_identities
output="$( (
  unset APPLE_SIGNING_IDENTITY
  # shellcheck source=common.sh
  source "$COMMON"
  require_local_signing_identity
) 2>&1 )"
expect_eq "empty keychain fails" "1" "$?"
expect_contains "empty keychain explains how to check" "security find-identity -v -p codesigning" "$output"

# The ordinary case: one development certificate, selected without being named.
stub_identities "$DEVELOPMENT"
output="$( (
  unset APPLE_SIGNING_IDENTITY
  # shellcheck source=common.sh
  source "$COMMON"
  require_local_signing_identity >/dev/null
  echo "$APPLE_SIGNING_IDENTITY|$LOCAL_SIGNING_IDENTITY_NAME|$CODESIGN_TIMESTAMP_ARG"
) 2>&1 )"
expect_eq "single development identity resolves" \
  "A1B2C3D4E5F60718293A4B5C6D7E8F901A2B3C4D|Apple Development: Ada Lovelace (TEAM123456)|--timestamp=none" \
  "$output"

# A certificate Apple did not issue can sit in the same keychain and is perfectly signable, so the
# prefix allow-list is the only thing keeping it out of a build the operator believes is Apple-signed.
stub_identities "$SELF_SIGNED"
output="$( (
  unset APPLE_SIGNING_IDENTITY
  # shellcheck source=common.sh
  source "$COMMON"
  require_local_signing_identity
) 2>&1 )"
expect_eq "non-Apple identity is refused" "1" "$?"
expect_contains "non-Apple identity lists what was installed" "localhost" "$output"

# Two usable identities must stop the build rather than have one silently win: signing a release
# rehearsal with the development certificate and a local build with Developer ID are both wrong.
stub_identities "$DEVELOPMENT" "$DEVELOPER_ID"
output="$( (
  unset APPLE_SIGNING_IDENTITY
  # shellcheck source=common.sh
  source "$COMMON"
  require_local_signing_identity
) 2>&1 )"
expect_eq "ambiguous keychain fails" "1" "$?"
expect_contains "ambiguous keychain names the knob" "APPLE_SIGNING_IDENTITY" "$output"

# ...and APPLE_SIGNING_IDENTITY is that knob, by common name.
stub_identities "$DEVELOPMENT" "$DEVELOPER_ID"
output="$( (
  APPLE_SIGNING_IDENTITY="Apple Development: Ada Lovelace (TEAM123456)"
  # shellcheck source=common.sh
  source "$COMMON"
  require_local_signing_identity >/dev/null
  echo "$APPLE_SIGNING_IDENTITY"
) 2>&1 )"
expect_eq "common name selects an identity" "A1B2C3D4E5F60718293A4B5C6D7E8F901A2B3C4D" "$output"

# ...and by SHA-1, which is what disambiguates two certificates sharing a common name.
stub_identities "$DEVELOPMENT" "$DEVELOPER_ID"
output="$( (
  APPLE_SIGNING_IDENTITY="0F1E2D3C4B5A69788796A5B4C3D2E1F009182736"
  # shellcheck source=common.sh
  source "$COMMON"
  require_local_signing_identity >/dev/null
  echo "$LOCAL_SIGNING_IDENTITY_NAME"
) 2>&1 )"
expect_eq "SHA-1 selects an identity" "Developer ID Application: Ada Lovelace (TEAM123456)" "$output"

# A selector that matches nothing must fail rather than fall back to auto-discovery.
stub_identities "$DEVELOPMENT"
output="$( (
  APPLE_SIGNING_IDENTITY="Developer ID Application: Someone Else (OTHER00000)"
  # shellcheck source=common.sh
  source "$COMMON"
  require_local_signing_identity
) 2>&1 )"
expect_eq "unknown selector fails" "1" "$?"
expect_contains "unknown selector is reported" "matches no installed identity" "$output"

# ---------------------------------------------------------------------------
# Timestamping
# ---------------------------------------------------------------------------

# The release path must keep the secure timestamp: without it the signature dies with the
# certificate and notarization rejects the submission outright.
(
  # The subshell is the point: this case needs a release identity without leaking it into the
  # ad-hoc cases below, and the assignment being subshell-local is what keeps them independent.
  # shellcheck disable=SC2030
  APPLE_SIGNING_IDENTITY="Developer ID Application: Ada Lovelace (TEAM123456)"
  # shellcheck source=common.sh
  source "$COMMON"
  codesign_item "$WORK/artifact"
) >/dev/null 2>&1
expect_contains "release signature is timestamped" "--timestamp" "$(cat "$CODESIGN_ARGS")"
if grep -qx -- "--timestamp=none" "$CODESIGN_ARGS"; then
  fail "release signature is timestamped — got --timestamp=none"
else
  pass "release signature does not disable the timestamp"
fi

# A local signature is never distributed and is often produced offline, where contacting Apple's
# timestamp authority fails the build instead of warning.
stub_identities "$DEVELOPMENT"
(
  unset APPLE_SIGNING_IDENTITY
  # shellcheck source=common.sh
  source "$COMMON"
  require_local_signing_identity
  codesign_item "$WORK/artifact"
) >/dev/null 2>&1
if grep -qx -- "--timestamp=none" "$CODESIGN_ARGS"; then
  pass "local signature disables the timestamp"
else
  fail "local signature disables the timestamp — got $(tr '\n' ' ' <"$CODESIGN_ARGS")"
fi

# ---------------------------------------------------------------------------

if [[ "$FAILED" -ne 0 ]]; then
  echo "$FAILED test(s) failed" >&2
  exit 1
fi

echo "all macOS signing helper tests passed"
