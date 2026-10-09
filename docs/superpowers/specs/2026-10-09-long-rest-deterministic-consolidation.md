# The Long Rest: deterministic consolidation (#93)

Implements the delivery contract of #93 under #75 and #73, using their approved Covenant forward contracts. Discovery/sweeps belong to #91; usefulness, ranking and decay belong to #95.

## Intent and canonical authority

One bounded operator-declared transformation makes equivalent Saga observations or explicitly superseded claims converge to one effective canonical claim eligible for recall. Original memories, embeddings, exact Annals versions and dependencies, origin, Session and attachment provenance, scope, sensitivity, pin and retirement remain intact. Subject heads remain truthful per-subject history; an immutable exact-version suppression projection states which heads consolidation has removed from recall.

The domain is The Long Rest, using existing Saga and Annals terminology. There is no cross-store mutation or model-callable tool. The authenticated API exposes a bounded Saga transformation and content-free receipt inspection. An equivalent-observation or supersession request is the operator's explicit declaration over exact inspected versions and content hashes. Exact duplicates require identical format and digest. Future discovery consumes this policy without turning similarity into canonical authority.

## Policy version 1

At most sixteen unique exact targets. Sort by ordinal stable identities; select the survivor by origin authority then ordinal memory identity, or the explicitly named exact version for supersession. Use strict UTF-8 length-prefixed binary fields, fixed big-endian numeric codes, canonical UTC instants and explicit null framing. Never use floating-point similarity, storage allocation Sequence, platform Unicode normalization or the current clock in canonical identity.

Bind each input's memory/claim/version identity, revision, content hash format and digest, origin, source Session, scope/Campaign, sensitivity/label status, valid and recorded instants, pin/retirement, embedding availability, decision-relevant consolidation and prior-output context, and ordered exact outgoing and incoming dependencies. Include incoming dependent-head status; exclude the input's own current-head marker from canonical identity. Preserve every source interval. Open ordinary assertions at different dates may converge without widening the survivor's interval; incompatible finite intervals do not.

Pinned, retired, unresolved or mixed scopes, protected material, incompatible dependencies or validity, current DerivedFrom dependents, a survivor without an embedding, already consolidated sources, and suppression of a prior transformation output produce typed no-change decisions. Protected material is left untouched; request-bound protected discovery and provider disclosure are #91's work. A claimless row or stale exact target cannot authorize a transformation and is refused without manufacturing Annals history.

## Transaction, receipts and replay

Read and prove targets and sensitivity inside one admitted SQLite write transaction. Validate exact persisted versions, row content binding, live heads and dependencies. A retirement tombstone binds its retained asserted predecessor digest, proved without reading protected plaintext. Refuse an unavailable binding rather than borrowing the caller's digest. Write an immutable receipt for each valid applied or no-change decision. The content-addressed receipt identity binds policy, transformation kind, ordered canonical inputs and explicit survivor declaration; the immutable receipt binds the input and output hashes. Its ordered input manifest binds exact retained versions; applied suppression rows bind exact source and survivor versions. Locate and verify an existing applied receipt for the exact declared group before evaluating it as new work. If any input head has advanced, return that historical applied receipt with zero writes. When all inputs remain current, replay requires matching bound metadata after restoring only the historical consolidation and prior-output flags. Other bound changes produce a distinct identity. No-change replay uses the current bound context, so removal of a blocking projection permits a fresh decision; historical no-change receipts remain inspectable by receipt identity.

All statements are parameterized raw SQLite. Core schema v17 declares receipt, manifest and suppression objects and update guards, with a frozen v16 predecessor and declarative evolution. Failure or cancellation rolls back all receipt/projection writes.

## Recall and lifecycle

Use the same exact-current-source suppression rule in semantic candidate selection before top-K, hydration, eligibility reporting and inspection. A later source correction advances its own head and releases its old-version suppression; a later source pin makes it eligible while pinned. Survivor correction or retirement does not silently revive an old superseded assertion. Erasing any participating subject erases the entire receipt and companions in the shared Annals erasure plan; surviving sources then follow their own eligibility again. Factory reset, retention, Campaign/store reset and restore use that common plan and inventory.

## Acceptance and verification

Behavioral RED tests precede implementation. Prove permutation/RID-independent canonical bytes with literal golden digests; exact/equivalent convergence; explicit supersession; source/dependency preservation; no-change and applied zero-write replay; lifecycle/scope/sensitivity protection; rollback; pre-top-K filtering; correction/pin and erasure effects; fresh/evolved schema equivalence; API envelope/serialization. Run build, all repository test projects, coverage gates, formatting, Native AOT warning/shipping smoke and SQLCipher provenance, plus configured CI for the exact candidate. Integrate into existing memory-enhancement only after terminal qualification. Verify tree/ref equality and issue state without redundant unchanged post-merge suites.
