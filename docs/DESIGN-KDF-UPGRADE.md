# Design Decision: Grimoire Key Derivation Function Upgrade

## Status

Pending approval. Implementation is gated on this design decision.

## Context

`GrimoireKeyDerivation` currently derives the SQLCipher passphrase from an API key or encryption secret using HKDF-SHA256 with a fixed, hard-coded salt. The API key is expected to be a high-entropy random string, so HKDF is a reasonable choice for key expansion. However, the current design has two issues that the security hardening plan flagged as P1-3:

1. **Fixed salt**: HKDF's extraction phase is safe with a fixed salt, but using a unique salt per key would isolate compromised-key attacks and make pre-computation harder.
2. **No iteration cost**: HKDF does not provide intentional compute cost. If the API key is ever low-entropy or reused, an attacker with the database can brute-force it quickly.

Because the database file can be exfiltrated from a host, the KDF must resist offline brute-force even when the input secret is not perfectly random.

## Options

### Option A: Salted HKDF + per-Grimoire salt (minimal change)

- Keep HKDF-SHA256 as the algorithm.
- Generate a random 16-byte salt per Grimoire database.
- Store the salt as metadata inside the database header or alongside `arcanum.json`.
- Keep the existing API key format unchanged.
- Pros: simple, fast, AOT-friendly, deterministic upgrade path.
- Cons: still no memory-hard or compute-cost protection; relies on API key entropy.

### Option B: PBKDF2-HMAC-SHA256 with ~600,000 iterations

- Replace HKDF with PBKDF2.
- Use a unique 16-byte salt per Grimoire database.
- Iteration count tuned to take ~100-200 ms on target hardware.
- Pros: widely supported, NIST-commended, easy to explain.
- Cons: not memory-hard, vulnerable to GPU/ASIC brute-force, still weaker than modern alternatives.

### Option C: Argon2id

- Use Argon2id with parameters tuned to the deployment hardware (e.g., `m=64 MB`, `t=3`, `p=4`).
- Requires a third-party library because .NET does not ship Argon2id in-box.
- Pros: memory-hard, modern recommendation, strongest offline brute-force resistance.
- Cons: extra dependency (e.g., `Isopoh.Cryptography.Argon2` or `Konscious.Security.Cryptography.Argon2`), must be evaluated for AOT/trimming compatibility, and must be available on all target runtimes (win-x64, linux-x64, osx-arm64, osx-x64).

### Option D: scrypt

- Similar trade-off to Argon2id but older.
- Pros: available in `System.Security.Cryptography` via `Scrypt`? No, .NET does not ship scrypt in-box.
- Cons: requires third-party dependency; generally considered second choice to Argon2id.

## Recommended approach

Adopt a **hybrid Option A + B**: migrate to PBKDF2-HMAC-SHA256 with a unique salt and a high iteration count, while keeping the HKDF path available as a legacy fallback for existing databases. This gives the project a measurable offline brute-force resistance without introducing an unvetted third-party dependency or AOT risk in a security-critical path.

Future work (P3/Discretionary): once Argon2id has been packaged or vetted for Native AOT, add a v3 derivation path that uses Argon2id and auto-migrates existing databases on unlock.

## Key rotation and migration

1. **Database version marker**: store a `KdfVersion` integer alongside the database (e.g., in `arcanum.json` or a database header table).
2. **Version 1** (legacy): HKDF with fixed salt.
3. **Version 2** (new): PBKDF2 with unique salt and iteration count.
4. On unlock:
   - If `KdfVersion == 1`, derive with HKDF and open the database.
   - After opening, re-encrypt with the Version 2 passphrase and bump `KdfVersion` to 2. This is a transparent upgrade.
5. New databases always use Version 2.

## Threat model considerations

- An attacker who steals the database but not the API key must brute-force the KDF.
- PBKDF2 at 600k iterations raises the cost significantly compared to HKDF.
- Memory-hard resistance is not available until Argon2id is adopted.
- The salt must be stored with the database; it is not a secret, but it must be unique per database.

## AOT and compatibility constraints

- PBKDF2 is available in `System.Security.Cryptography` and is fully AOT-compatible.
- No new native dependencies are required.
- The iteration count must be calibrated on the slowest supported runtime (e.g., Raspberry Pi or low-power VM) so the application remains responsive.

## Open questions

1. What is the minimum supported runtime? This determines the iteration count.
2. Should the salt be stored inside `arcanum.json` or inside the database header?
3. Is automatic re-encryption on unlock acceptable, or should key rotation require an explicit `arcanum rekey` command?

## Decision gate

Do not proceed with implementation until the design is approved and the open questions above are answered.
