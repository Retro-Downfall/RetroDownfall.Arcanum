# RV056 shared Campaign provenance

Core25 preserves existing v1 source rows and digests. New publications use immutable segments containing bounded additions. A nullable ManifestId on each retained rollup is its root. Segments carry a random lineage identity, monotonic ordinal, prior digest and cumulative count; source membership is the lineage prefix through that root. The segment stores provenance only, never a predecessor summary body. Deleting an intermediate summary therefore neither breaks a successor nor retains erased summary content.

New v2 digests bind Campaign, lineage, ordinal, prior digest, cumulative count and exact Session-sorted additions. A legacy current revision is imported once as a v1 base segment with the unchanged v1 digest. All source verification reconstructs exact contribution metadata, canonical ownership, state, labels, sensitivity and chain evidence. No detached authority cache is introduced.

Publication validates in one deferred SQLite snapshot and promotes that same transaction for bounded writes. A concurrent writer makes promotion fail closed and rolls back; the store never replays a provider call. O(N) source verification before promotion remains intentional. The root technical ruling accepts this cost; the requirement is linear stored provenance and no full-history verification under the writer lock. Legacy import is a one-time O(N) write exception.

Retention and protected erasure enumerate reverse dependencies from Session/contribution to every consuming root. Counts and closures describe physical rows once. Segment cleanup removes only the suffix no retained root references. Existing v1 framing, closed catalogs, admission, live-turn binding and historical summaries remain authoritative.
