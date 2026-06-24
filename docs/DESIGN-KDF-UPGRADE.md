# Design Decision: Grimoire Key Derivation Function Upgrade

## Status

**Approved and implemented.** Implementation matches the decisions below.

## Context

`GrimoireKeyDerivation` previously derived the SQLCipher passphrase from the master API key or a dedicated encryption secret using HKDF-SHA256 with a fixed, hard-coded salt. The API key is expected to be a high-entropy random string, so HKDF is a reasonable choice for key expansion. However, the current design had two issues that the security hardening plan flagged as P1-3:

1. **Fixed salt**: HKDF's extraction phase is safe with a fixed salt, but using a unique salt per key would isolate compromised-key attacks and make pre-computation harder.
2. **No iteration cost**: HKDF does not provide intentional compute cost. If the API key is ever low-entropy or reused, an attacker with the database can brute-force it quickly.

Because the database file can be exfiltrated from a host, the KDF must resist offline brute-force even when the input secret is not perfectly random.

## Decision

Adopt **PBKDF2-HMAC-SHA256 with a unique 16-byte salt per Grimoire database and 600,000 iterations**. The HKDF path remains as a **legacy fallback** for databases created before this change. This gives the project measurable offline brute-force resistance without introducing an unvetted third-party dependency or AOT risk in a security-critical path.

Future work (P3/Discretionary): once Argon2id has been packaged or vetted for Native AOT, add a v3 derivation path that uses Argon2id and auto-migrates existing databases on unlock.

## Algorithm details

- **KDF version 1 (legacy):** HKDF-SHA256 with fixed salt `Arcanum.Grimoire.SQLCipher.salt.v1` and info `Arcanum.Grimoire.SQLCipher.hkdf.v1` (API key) or `Arcanum.Grimoire.SQLCipher.hkdf.v2` (dedicated secret).
- **KDF version 2 (new):** PBKDF2-HMAC-SHA256 with 600,000 iterations, a 128-bit random salt, and a 256-bit output used as the SQLCipher passphrase.
- **Iteration count:** 600,000 (OWASP recommendation, ~100–200 ms on target hardware).

## Salt storage

- A **sidecar JSON file** named `{grimoire.db}.kdf` sits next to the Grimoire database.
- Format: `{"v":2,"salt":"<base64-16-byte-salt>"}`.
- The salt is not a secret; it must be unique per database and readable before the database is unlocked so the passphrase can be derived.

## Key rotation and migration

1. **Database version marker:** `KdfVersion` is stored in the sidecar file (`{grimoire.db}.kdf`).
2. **Version 1** (legacy): HKDF with fixed salt.
3. **Version 2** (new): PBKDF2 with unique salt and 600,000 iterations.
4. On unlock:
   - If the sidecar exists, read the version and salt, derive the passphrase with PBKDF2, and open the database.
   - If the sidecar is missing and the database exists, try to open the database with the legacy HKDF-derived passphrase. After a successful open, generate a new random salt, derive a new PBKDF2 passphrase, execute `PRAGMA rekey`, and write the sidecar. This is a transparent upgrade.
   - API-key-encrypted legacy databases are migrated to a dedicated encryption secret during the rekey.
5. New databases always use Version 2 and a dedicated encryption secret stored alongside the master API key.

## Threat model considerations

- An attacker who steals the database but not the API key must brute-force the KDF.
- PBKDF2 at 600,000 iterations raises the cost significantly compared to HKDF.
- Memory-hard resistance is not available until Argon2id is adopted.
- The salt must be stored with the database; it is not a secret, but it must be unique per database.

## AOT and compatibility constraints

- PBKDF2 is available in `System.Security.Cryptography` and is fully AOT-compatible.
- No new native dependencies are required.
- The iteration count was calibrated on the slowest supported runtime; it remains responsive on macOS, Linux, and Windows targets.

## Open questions (resolved)

1. **Minimum supported runtime?** Iteration count is 600,000 across all supported targets; performance is acceptable on modern hardware and low-power VMs.
2. **Salt storage location?** Sidecar JSON file (`{grimoire.db}.kdf`) next to the database.
3. **Automatic re-encryption on unlock?** Yes — legacy databases are transparently re-encrypted to Version 2 on successful unlock.

## Implementation files

- `src/RetroDownfall.Arcanum.Infrastructure/Security/GrimoireKeyDerivation.cs`
- `src/RetroDownfall.Arcanum.Infrastructure/Security/GrimoireKdfSidecar.cs`
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- `tests/RetroDownfall.Arcanum.Tests/Security/GrimoireKeyDerivationTests.cs`
- `tests/RetroDownfall.Arcanum.Tests/Security/GrimoireKdfSidecarTests.cs`
- `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
