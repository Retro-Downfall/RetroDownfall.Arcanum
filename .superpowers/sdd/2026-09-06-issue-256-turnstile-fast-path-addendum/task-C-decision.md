# Task C six-pair decision

## Decision

The reviewed request/work epoch candidate is rejected by the frozen acceptance contract.

- Immutable harness H: `f51ac3f84c3b408510e448311d7a5e15bdbc041e`
- Reviewed monitor baseline B: `b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`
- Measured candidate C: `09da72dad77dc5b829f688f33ed6b8562621aa8d`
- Caller tip before measurement: `5bc748df6ec8cd6b766f79a03b5c9386b5d1777f`
- Session: `1BFF1AAD-81CF-4F4B-90C3-D4A0C04ED68E`
- UTC interval: `2026-09-07T12:42:07Z` through `2026-09-07T13:13:29Z`
- Result: valid comparison, rejected, exit `1`

Every one of the twelve Native AOT measurement processes exited zero. Every run finished with zero
live requests, work scopes, physical opens, effect groups, and generation waiters; drain and reopen
both succeeded. The runner accepted all ancestry, clean-tree, catalog, immutable-input, manifest,
toolchain, native-binary, operation-count, checksum, allocation, and final-state evidence before it
made the performance decision.

The candidate's paired mixed-throughput point ratio was `1.0421905987487488`, and the deterministic
bootstrap lower bound was `1.0023955084512488`. Both were below the frozen required ratio of `1.2`.
The separately bootstrapped `ef.pooled@one` p99 upper bound was acceptable at
`1.0090215420615836`, but ten individual p99 cells—including `ef.pooled@eight`—exceeded the frozen
maximum ratio of `1.1`. The comparator therefore rejected the candidate for both inadequate material
improvement and tail-latency regressions. No threshold is being waived or reinterpreted.

The rejected candidate's structural no-eager-waiter and O(live) proofs remain useful historical
engineering evidence, but its production implementation and candidate-only tests do not ship. The
reviewed monitor implementation B remains the production design.

## Frozen instrument and environment

The qualification launcher was byte-identical at H, B, C, and the caller:

- Git blob: `974dcf84f37f677311694dc4c515eb444c368d80`
- SHA-256: `aa36efe1ecdc0d1e5a7272a3213534bd5d13aadc504bde07fe5ebde68e69ecef`

All runs recorded macOS arm64, Apple M5 Max, 18 logical processors, .NET SDK `10.0.400`, runtime
`10.0.11`, Native AOT with dynamic code disabled, and the same toolchain/input/native digests.

The first qualification attempt was invalid and supplied no performance evidence. macOS exposed the
same temporary directory as both `/var/folders/...` and `/private/var/folders/...`; NuGet then failed
to match the benchmark ProjectReference identity to the dependency project identity and stopped in
the baseline publish before a session UUID or measurement process existed. Its log SHA-256 is
`0638d752807b4b28958fa2eaf0faa8cc357f04e9a31179f3e8547c86a6744481`.

The rerun changed only the spelling of `TMPDIR` to the physical path
`/private/var/folders/c_/ljwpl72n2sb6z2kvv2_hvr5h0000gn/T/`. The lexical and physical spellings were
verified to be the same directory on device `16777234`, inode `314556`. A bounded archived-baseline
Native AOT publish and 36-cell smoke passed before the rerun. The unchanged runner then created new
archives, binaries, session identity, and all twelve measurements in a fresh output directory.

The launcher portability defect is corrected only after this decision is committed. The correction
is not H, was not measured here, and must continue to refuse the historical H/B/C triple because the
caller bytes will differ.

## Artifact binding

The complete bundle is retained at
`/private/tmp/grimoire-admission-qualification-b0be2b4df885-09da72dad77d-canonical-rerun` for the
delivery session. These digests bind the report to the exact raw evidence:

| Artifact | SHA-256 |
|---|---|
| `runs/pair-0-B.json` | `3443fed8a90d7df47c93f18614723ff01016af9f40d69eb618b673ba4d5e1181` |
| `runs/pair-0-C.json` | `e0ea3472c790fc17e369052677a2d16222d3fe02a00a95507aa17304f393aae3` |
| `runs/pair-1-B.json` | `15cddfcd119ae67b80d6ff39cc75aeba6e2d7cc536e121a4f98d51ffb65214ea` |
| `runs/pair-1-C.json` | `c2997a2f1181f687e9467fd9d49684688d9a9f9c076be69ab24016c25b2556e0` |
| `runs/pair-2-B.json` | `79a57cc4814eea33710a337055c5615eebbc07396e1af224800de094cf0c8102` |
| `runs/pair-2-C.json` | `afddab2298d922d2fc017add0ce1bc27395310a705229e2141bcfc604784b935` |
| `runs/pair-3-B.json` | `bba52853cba047d83f2e8fa815992aaf58af322c752d54cb5c736b7ec1e8199f` |
| `runs/pair-3-C.json` | `6fa1ebdbc31e536e2a6dab0c04162f719d5d86b527d03493aad18b8f770cbca1` |
| `runs/pair-4-B.json` | `982169420aa40c426e24186910e9c8670e381ee26995d9b451c3f9b6c96d037f` |
| `runs/pair-4-C.json` | `023d0ae2a476206ab4beb17131fc8b31e7c4ccdc9bd3cfbbcfc069668db6b907` |
| `runs/pair-5-B.json` | `29f3c7eb6e0bf46e7e5a15aec5d40358c55a3437114be998d1f0e81a96d5468f` |
| `runs/pair-5-C.json` | `701aa891c4515680b6fae8397543e38920207e55de0af16b935db9a527d0f2e9` |
| `evidence.json` | `429f475b8e4d473ba6009bfac820294b7c38bead52bfdd3f2736dbfc77a9a5e9` |
| `comparison.json` | `07d02cc9951987c97212ce9239369cac9daf3ad19a82e6e90892dac37f6e6eaa` |

## Required restoration

The next implementation commit restores the allowlisted production gate path byte-for-byte from B,
removes the six candidate-only test partials, and restores the monitor description in DESIGN. Task B
characterization and this decision remain. A fresh independent review must verify that production is
identical to B before caller work begins.
