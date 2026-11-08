# Guild memory enforcement implementation plan

> DESIGN §23.14 is the binding specification. Implementation uses TDD and independent review; Guild execution remains dormant.

**Goal:** Intersect every Guild memory operation with live Signet authority and existing canonical ownership, lifecycle and sensitivity policy.

**Architecture:** A private invocation context carries the exact host execution handle. Typed, root-fenced memory admission lends an expiring access scope to existing store readers. Queries intersect that scope before rank, limit and allocation; mutations revalidate on their existing transaction and commit metadata-only attribution atomically. Operator-only paths refuse Guild invocation. No new memory engine, ambient installation grant, public endpoint or setting is introduced.

**Integration:** This slice targets the `guild-implementation` prerequisite tree. Delivery requires focused local qualification and the targeted CI pass before integration and issue closure. It does not activate Guild execution or move the accumulated work to `main`.

## Implemented boundaries

- Typed admission uses expiring access scopes, canonical query predicates, same-transaction verification and transactional attribution. Tests cover absent, mismatched, revoked and expired authority; explicit Global classification; canonical Campaign ownership; and escaped or cross-invocation scope reuse.
- Saga/transcript candidate and hydration tests enforce predicates before managed/quantized ranking and archive FTS limits. Direct reads retain lifecycle, provenance and sensitivity checks; unsupported preview adapters refuse Guild access.
- Lexicon tier, exact/FTS/LIKE/fallback/eligibility tests enforce reads and mutations without changing canonical scope or shadowing. Tests retain pinned/retired/erased rules and prove attribution rollback, including writes while ordinary Annals capture is disabled.
- Context/attachment/Tapestry/preview/delegation/retained-tool tests constrain materialization and final disclosure. Operator recall/Covenant remain ineligible for Guild, and physical workspace access requires its own adapter.
- Guild V6 attribution follows canonical immutable versions through cleanup, sensitivity erasure, restore and reset, including explicit deletion and absence reconciliation without foreign-key cascades. The ordinary extraction queue refuses Guild work until a sealed background adapter exists.

## Qualification and delivery

Run `scripts/qualify-guild-memory.sh` from the repository root. It builds Release, runs the affected memory, lifecycle, documentation, SQL and reflection/AOT contracts, records SDK/commit/worktree status and TRX, and refuses missing or skipped new Guild memory suites and factory-reset cases. Existing opt-in probes can remain skipped; the script does not claim full coverage or hosted source-analysis authority.

Run the local Guild Native AOT smoke and static workflow/formatting checks. Resolve review findings through failing regression tests before pushing a candidate. Dispatch `CI` with `qualification=guild-memory` for that candidate; the default remains `full`. Integrate only the qualified candidate and retain its CI run as delivery evidence. No pull request is required for this delivery.

## Review focus

- A retained or asynchronous callback must not retain authority after its admission ends.
- Null Session, legacy-unresolved rows and missing Guild schema must never become Global access.
- Unauthorized rows must not consume top-K or shadow an authorized same-name entry.
- Revocation, owner deletion and maintenance closure must serialize against effect admission and source disclosure.
- Attribution is immutable version provenance, never scope or inferred authorship of old facts.
