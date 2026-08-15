#!/usr/bin/env bash
#
# Produces the old-runtime compatibility fixture: an encrypted database written by the SQLCipher
# runtime Arcanum shipped before the hermetic delivery (SQLitePCLRaw.lib.e_sqlcipher), so the tests
# can prove an operator's existing Grimoire still opens under the new library.
#
# The fixture is generated against the real previous runtime rather than described in a comment,
# because "old databases still open" is exactly the claim that is worthless without evidence.
#
# Usage:
#   scripts/build-sqlcipher-compatibility-fixture.sh [--force]
#
# Refuses to overwrite a fixture whose recorded source identity differs, so a regenerated fixture
# cannot silently change what the compatibility test is testing.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

FIXTURE_DIR="${REPO_ROOT}/tests/RetroDownfall.Arcanum.Tests/TestData/SqlCipher"

LEGACY_PACKAGE_VERSION="2.1.11"

FORCE="0"

while [ "$#" -gt 0 ]; do

  case "$1" in

    --force) FORCE="1" ; shift ;;

    -h | --help)

      echo "Usage: build-sqlcipher-compatibility-fixture.sh [--force]"

      exit 0

      ;;

    *) echo "Unknown argument: $1" >&2 ; exit 2 ;;

  esac

done

if [ "$(uname -s)" != "Darwin" ]; then

  echo "This fixture generator runs on macOS; the legacy runtime is taken from the local NuGet cache." >&2

  exit 2

fi

for cmd in clang jq shasum; do

  command -v "${cmd}" >/dev/null 2>&1 || { echo "Required command not found: ${cmd}" >&2 ; exit 1 ; }

done

LEGACY_LIB="${HOME}/.nuget/packages/sqlitepclraw.lib.e_sqlcipher/${LEGACY_PACKAGE_VERSION}/runtimes/osx-arm64/native/libe_sqlcipher.dylib"

if [ ! -f "${LEGACY_LIB}" ]; then

  echo "The previous shipping runtime was not found at:" >&2

  echo "  ${LEGACY_LIB}" >&2

  echo "Restore SQLitePCLRaw.lib.e_sqlcipher ${LEGACY_PACKAGE_VERSION} into the NuGet cache first." >&2

  exit 1

fi

WORK_DIR="$(mktemp -d)"

cleanup() {

  rm -rf "${WORK_DIR}"

}

trap cleanup EXIT

# A minimal harness against the documented SQLite C API. Declaring the handful of entry points here
# avoids needing the legacy runtime's headers, which the package does not ship.
cat > "${WORK_DIR}/make-fixture.c" <<'EOF'
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct sqlite3 sqlite3;

extern int sqlite3_open_v2(const char *filename, sqlite3 **ppDb, int flags, const char *zVfs);
extern int sqlite3_key(sqlite3 *db, const void *pKey, int nKey);
extern int sqlite3_exec(sqlite3 *db, const char *sql, void *callback, void *arg, char **errmsg);
extern int sqlite3_close(sqlite3 *db);
extern const char *sqlite3_libversion(void);
extern const char *sqlite3_errmsg(sqlite3 *db);

#define SQLITE_OK 0
#define SQLITE_OPEN_READWRITE 0x00000002
#define SQLITE_OPEN_CREATE    0x00000004

static int run(sqlite3 *db, const char *sql)
{
    char *error = NULL;

    int status = sqlite3_exec(db, sql, NULL, NULL, &error);

    if (status != SQLITE_OK)
    {
        fprintf(stderr, "SQL failed (%d): %s\n  %s\n", status, error ? error : "", sql);
        return status;
    }

    return SQLITE_OK;
}

int main(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: make-fixture <database-path> <key>\n");
        return 2;
    }

    sqlite3 *db = NULL;

    if (sqlite3_open_v2(argv[1], &db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, NULL) != SQLITE_OK)
    {
        fprintf(stderr, "open failed\n");
        return 1;
    }

    if (sqlite3_key(db, argv[2], (int)strlen(argv[2])) != SQLITE_OK)
    {
        fprintf(stderr, "key failed: %s\n", sqlite3_errmsg(db));
        return 1;
    }

    /* Exercise the same shape a real Grimoire has: WAL, a table, an index, and FTS5 content. */
    if (run(db, "PRAGMA journal_mode=WAL;") != SQLITE_OK) return 1;
    if (run(db, "CREATE TABLE legacy_sentinel (id INTEGER PRIMARY KEY, value TEXT NOT NULL);") != SQLITE_OK) return 1;
    if (run(db, "INSERT INTO legacy_sentinel (id, value) VALUES (1, 'written-by-the-previous-runtime');") != SQLITE_OK) return 1;
    if (run(db, "CREATE INDEX legacy_sentinel_value ON legacy_sentinel (value);") != SQLITE_OK) return 1;
    if (run(db, "CREATE VIRTUAL TABLE legacy_fts USING fts5(body);") != SQLITE_OK) return 1;
    if (run(db, "INSERT INTO legacy_fts (body) VALUES ('inherited corpus');") != SQLITE_OK) return 1;
    if (run(db, "PRAGMA wal_checkpoint(TRUNCATE);") != SQLITE_OK) return 1;

    printf("%s\n", sqlite3_libversion());

    if (sqlite3_close(db) != SQLITE_OK)
    {
        fprintf(stderr, "close failed\n");
        return 1;
    }

    return 0;
}
EOF

echo "==> Building the legacy-runtime harness"

# The previous runtime was linked with a relative install name
# ("./bin/e_sqlcipher/mac/arm64/libe_sqlcipher.dylib"), so it can only be loaded from a matching
# working directory. Copy it and give it an @rpath identity to load it here. The hermetic build does
# not have this problem: it sets @rpath at link time.
cp "${LEGACY_LIB}" "${WORK_DIR}/libe_sqlcipher_legacy.dylib"

install_name_tool -id "@rpath/libe_sqlcipher_legacy.dylib" "${WORK_DIR}/libe_sqlcipher_legacy.dylib"

codesign --force --sign - "${WORK_DIR}/libe_sqlcipher_legacy.dylib"

clang -O0 -o "${WORK_DIR}/make-fixture" "${WORK_DIR}/make-fixture.c" \
  "${WORK_DIR}/libe_sqlcipher_legacy.dylib" \
  -Wl,-rpath,"${WORK_DIR}"

FIXTURE_KEY="arcanum-compatibility-fixture-key"

DB_PATH="${WORK_DIR}/sqlcipher-legacy.db"

echo "==> Writing the fixture with the previous runtime"

LEGACY_SQLITE_VERSION="$("${WORK_DIR}/make-fixture" "${DB_PATH}" "${FIXTURE_KEY}")"

if [ ! -f "${DB_PATH}" ]; then

  echo "The legacy runtime did not produce a database." >&2

  exit 1

fi

FIXTURE_NAME="sqlcipher-legacy-sqlite-${LEGACY_SQLITE_VERSION}"

DB_TARGET="${FIXTURE_DIR}/${FIXTURE_NAME}.db"

JSON_TARGET="${FIXTURE_DIR}/${FIXTURE_NAME}.json"

LEGACY_LIB_SHA="$(shasum -a 256 "${LEGACY_LIB}" | awk '{print $1}')"

if [ -f "${JSON_TARGET}" ] && [ "${FORCE}" != "1" ]; then

  RECORDED="$(jq -r '.legacyRuntime.librarySha256' "${JSON_TARGET}")"

  if [ "${RECORDED}" != "${LEGACY_LIB_SHA}" ]; then

    echo "Refusing to overwrite ${FIXTURE_NAME}: it was produced by a different legacy runtime." >&2

    echo "  recorded: ${RECORDED}" >&2

    echo "  current:  ${LEGACY_LIB_SHA}" >&2

    echo "Pass --force only if the compatibility target has genuinely changed." >&2

    exit 1

  fi

fi

mkdir -p "${FIXTURE_DIR}"

cp "${DB_PATH}" "${DB_TARGET}"

DB_SHA="$(shasum -a 256 "${DB_TARGET}" | awk '{print $1}')"

jq -n \
  --arg sqliteVersion "${LEGACY_SQLITE_VERSION}" \
  --arg librarySha256 "${LEGACY_LIB_SHA}" \
  --arg packageVersion "${LEGACY_PACKAGE_VERSION}" \
  --arg databaseSha256 "${DB_SHA}" \
  --arg key "${FIXTURE_KEY}" \
  --arg sentinel "written-by-the-previous-runtime" \
  --arg fileName "${FIXTURE_NAME}.db" \
  '{
     description: "Encrypted database written by the SQLCipher runtime Arcanum shipped before the hermetic delivery. Proves an existing Grimoire opens unchanged under the new library.",
     fileName: $fileName,
     legacyRuntime: {
       package: "SQLitePCLRaw.lib.e_sqlcipher",
       packageVersion: $packageVersion,
       sqliteVersion: $sqliteVersion,
       librarySha256: $librarySha256
     },
     cipherDefaults: {
       note: "SQLCipher 4 defaults; no cipher pragma was set when writing the fixture."
     },
     key: $key,
     sentinel: $sentinel,
     databaseSha256: $databaseSha256
   }' > "${JSON_TARGET}"

echo "==> Done"

echo "    ${DB_TARGET}"

echo "    ${JSON_TARGET}"

echo "    legacy SQLite ${LEGACY_SQLITE_VERSION}, library ${LEGACY_LIB_SHA}"
