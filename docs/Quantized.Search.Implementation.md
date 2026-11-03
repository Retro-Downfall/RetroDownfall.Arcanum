# Quantized search implementation

**Goal:** Complete issues #173 and #170 with exact managed retrieval and measured compact-scan savings, then deliver the qualified candidate to `main` under the user's explicit delivery amendment.

**Architecture:** The full-precision embedding remains authoritative. Each of the five Weave corpora receives a compact, disposable int8 companion with a validated error envelope. The scanner rejects a row only when its upper bound is strictly below the current exact heap boundary or similarity threshold; all other rows use the existing cosine implementation. This streaming rescue keeps O(K) candidate memory and permits an exhaustive worst case.

**Contracts:** .NET 10 Native AOT, existing hermetic SQLCipher, no new dependency or public configuration, same scoped predicates and tie ordering. Core37 installs standalone companion tables and invalidation triggers. Existing writes maintain companions in the same transaction; bounded background batches fill inherited rows without gating retrieval. Missing, partial, unsafe, or damaged representations use authoritative scoring.

## Work and evidence

- [x] Numeric codec: red tests for an outward bound on the existing SIMD cosine, checksum/shape corruption, unsafe magnitudes, randomized and adversarial vectors, and allocation-free per-row validation. Implement `QuantizedEmbeddingCodec.Encode`, `PrepareQuery`, and `TryGetUpperBound` in Core.
- [x] Storage lifecycle: red integration tests for Core36 evolution, fresh/reopen install, rollback, update invalidation, deletes/reset, and interrupted backfill. Implement five companion tables, same-transaction upserts, and admitted bounded background batches.
- [x] Search: red integration tests for automatic engagement, frozen small-corpus query, exhaustive identity across substantial randomized queries, filtered scopes, threshold/tie rescue, Saga ranking, incomplete/corrupt companions, and allocations. Integrate compact scans plus exact point reads in Divination, Session Divination, and Tapestry graph ranking.
- [x] Measurement: compare full BLOB bytes, compact bytes, exact rows scored, and cold SQLCipher page-cache misses on the same seeded 768-dimensional corpus. Report physical reads separately from logical payload savings; record poor-selectivity worst cases honestly.
**Delivery gate:** independently review and remediate the complete diff; run relevant regression and full suite/coverage gates, native provenance, warning-free AOT, and complete CI including Windows SIMD differential coverage. Commit and merge only the qualified candidate, verify remote/local main and close both issues with evidence. The pull request and issue delivery records own the final candidate SHA, qualification results, and merge/closure state.

## Review focus

- Float overflow/subnormal arithmetic and float SIMD reduction differ from real-number cosine: unsupported values fall back and the envelope includes numerical error.
- Equal upper bounds must be rescued so id/time tie-breaking remains exact; a fixed shortlist must demonstrably fail the adversarial fixture.
- Saga uses additive ranking modifiers: apply the same monotone policy to upper bounds before pruning.
- The compact cursor and authoritative reads share a SQLite snapshot; concurrent embedding replacement cannot combine two versions.
- SQLite pages, B-tree occupancy, and exact survivor reads determine physical I/O. A fourfold payload reduction is not itself evidence of fourfold decryption savings.

The existing #171 managed-mode and frozen-result tests already cover the two unchecked non-accelerator criteria in #170. Requalify them on the delivered candidate before updating the tracker.

## Recorded implementation evidence

The randomized differential fixture checks 128 queries over 2,048 vectors at 768 dimensions, including ties, partial coverage, and corrupt companions. A deliberate mutation removing the residual allowance returned the wrong winner (`00000000` instead of `00002048`); restoring the bound returned the focused regression suite to green.

On the encrypted 8,192-row selective measurement fixture, full-vector payload fell from 25,165,824 to 7,018,496 bytes including compact reads and exact rescoring. Only two rows required authoritative scoring. Cold SQLCipher pager misses fell from 8,213 to 2,097, or 33,640,448 to 8,589,312 encrypted page bytes. These are SQLite page reads, including OS-cache hits, rather than physical disk-operation counts; ambiguous bounds can still require an exhaustive rescore.

Production fault-trigger tests prove atomic insertion and correction. Frozen Core36 tests cover pre-companion writes, pruning, workspace reset planning, Tapestry inventories, and embeddings reset during pending upgrades. The final review found no remaining production or owning-document defects; delivery qualification remains a separate gate.

The final design uses a SIMD int8 dot with a query-prepared arithmetic envelope, retaining the reference-scorer and quantization residual allowances. It keeps compact rows clustered by key, bounds stored representation size in the schema, guards inherited source payloads before projection, and reuses the existing fresh retention schema census. DESIGN §21.5 records randomized-key locality and both cold- and warm-pager latency; warm exhaustive scanning remains faster.
