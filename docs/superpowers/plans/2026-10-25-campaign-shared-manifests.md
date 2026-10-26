# RV056 implementation and evidence

1. Reproduce retained N(N+1)/2 sources using real SQLite and retained historical revisions. RED: 12 folds stored 78 source rows.
2. Add Core25 immutable shared provenance and preserve the exact Core24 catalog/fingerprint; prove mixed legacy/new schema evolution.
3. Publish bounded deltas in a deferred snapshot; prove concurrent mutation cannot publish and failed/cancelled writes leave no progress.
4. Update every source dependency, invalidation, retention, erasure and restore consumer. Prove independent old-root deletion and all-root source erasure with physical closure counts.
5. Run affected closed-catalog and behavioral tests, meaningful mutation RED and restored GREEN, zero-warning affected builds and both house-style modes. Root owns independent review and final qualification.

Technical ruling: O(N) authoritative read-snapshot verification is accepted. No separate authenticated-generation cache subsystem is required; no O(N) source work may move under the writer lock on ordinary v2 appends. A one-time v1 import is explicitly bounded by retained legacy input and measured separately.
