# Issue #98: Immutable Lexicon correction, retirement, pinning, and reindexing

**Status:** Designed on 2026-09-27. Approved by the operator before any implementation code was written.

**Branches:** `codex/issue-98-lexicon-version-operations`, cut from `memory-enhancement`, to be merged back with `--no-ff` after qualification.

**Issue:** #98, a delivery slice under #78 and #73. Its blockers #76, #86, #102, and #105 are closed. It can close independently while its parent epics and the later review/bulk-erasure slices remain open.

**Names settled in design:** a **correction** replaces one Lexicon entry's type and complete fact set while naming the exact content version the operator inspected. A **retirement** removes one entry from matching and retrieval without erasing it. A **reinstatement** makes that retained entry eligible again. A **pin** protects it from automatic pruning. Together these are the Lexicon **curation** verbs.

## 1. Objective

Give the operator exact, inspectable control over Lexicon memory without weakening the store's local-first, append-only evidence model. An operator can inspect one exact Global or Campaign entry, correct its complete content, retire and reinstate it, and pin or unpin it. The model never receives retired content, a retired Campaign entry no longer hides an eligible Global entry, and automatic retention cannot remove a pin between planning and execution.

The canonical `lexicon_entries` row remains the only plaintext authority. A correction replaces that row in place, atomically republishes its full-text projection and current fact provenance, and appends an immutable Annals revision. No content-bearing history table preserves facts the operator corrected away. Annals records the claim transition by a versioned, collision-safe content digest plus origin, scope, and sensitivity evidence. A content-free historical provenance ledger retains attachment coordinates by committed fact ordinal so editing content does not edit its provenance, corrected-away short facts do not leave a guessing oracle, and the existing forgetting boundary still removes superseded plaintext.

## 2. Scope and settled decisions

- **One delivery slice.** Schema evolution, exact-target inspection, correction, retirement/reinstatement, pin-aware retention, HTTP, CLI, documentation, and qualification ship together because they share one identity and transaction protocol.
- **The current row remains authoritative.** Superseded type/fact plaintext is not copied into a version table. The immutable version is an Annals claim revision binding the published content digest; its associated provenance rows contain attachment identity and source coordinates, never the superseded fact text or a per-fact digest.
- **Correction does not rename or move.** It replaces `Type` and the complete ordered facts collection. Name, normalized name, stable entry id, and typed scope remain fixed.
- **Reinstatement is explicit.** A retired entry cannot be corrected or revived through `scribe_lexicon`; the operator must use `reinstate`.
- **Campaign fallthrough is intentional.** Retiring Campaign `X`'s exact entry for a name stops that row shadowing Global, so the active Global entry resurfaces for `X`. Reinstatement restores Campaign shadowing.
- **Pins govern automatic work.** They exempt Lexicon entries from retention pruning and from future consolidation or decay. They do not block operator correction, retirement, reinstatement, explicit deletion, or agent-authored updates to an active entry.
- **Existing hard deletion keeps its meaning.** `DELETE /api/memory/lexicon/{name}` and `delete_lexicon` continue to erase the row. Keyed resurrection suppression after hard erase belongs to #100; this slice guarantees only that a retained retired row cannot be silently revived.
- **Curation evidence is ungated.** State-changing correction, retirement, and reinstatement append the required Annals evidence even when ordinary Annals capture is disabled. Pre-existing claimless rows remain valid. Their first Annals-bearing curation verb first materializes the exact pre-mutation baseline and then records the requested transition in the same transaction; pin and unpin do not open content history.
- **Protected memory stays protected.** Any operator response containing Lexicon content obeys the existing Covenant generation-bound read-authority, same-snapshot label verification, and no-store response rules. Protected inspection retains the narrowest lease covering its complete result—scoped `CovenantReadLease` or all-scopes `CovenantInstallationReadLease`—through response serialization. Every protected mutation retains a `CovenantWriteLease` over the exact installation-global or Campaign owner scope through commit and response serialization. A correction of labeled content atomically advances equivalent sensitivity evidence; it never silently drops or downgrades the label.
- **No Ward is introduced.** These are authenticated operator actions with the existing CLI confirmation contract. The closed #197 amendment records the action; it does not add an approval prompt.

## 3. Governing constraints

- Core schema version 10 currently publishes normalized source fingerprint `B484778B9288D99C4337FA3C95BEB56B95FDAE6B1D149C6A9A2A822591BBE951`. Version 11 must pin that value before any head object changes, reconstruct the v10 tree in a fixture, and prove the fixture hashes to the pin.
- `ALTER TABLE ... ADD COLUMN` persists SQLite's own declaration shape. The version-11 head file and transition tests must prove fresh and evolved databases converge exactly; a fresh-only success is insufficient.
- Raw SQL remains one object per file under `Infrastructure/Data/Schema/**`. There is no EF migration and no compiled-model change for these application-authored reads and set-based writes.
- Native AOT rules apply to every request/response type. New `/api` payloads are source-generated through `ArcanumJsonContext`; there are no anonymous wire objects or reflection serializers.
- Every curation operation returns `Result`/`Result<T>` from the domain/service layer. Only the endpoint converts it to `ApiResponse<T>` and an HTTP status.
- Stored lifecycle instants use the repository's canonical fixed-width UTC `Z` form and join the Core UTC-instant inventory and diagnostics.
- Every model-facing Lexicon path must make eligibility explicit. Exact lookup, Campaign fallback, FTS lookup, LIKE fallback, bulk reads used for prompt construction, and direct daemon/tool reads are all in scope; fixing only the public endpoint is not sufficient.
- Documentation under `docs/` describes the capability rather than the issue number. The public root `README.md` does not change.

## 4. Considered approaches

### 4.1 A content-bearing `lexicon_entry_versions` table — rejected

This would make rollback and old-content display easy, but it would preserve every rejected fact in plaintext and create a second content authority every erase/reset path must reach. It conflicts with the established Annals rule that history proves which content a claim described without retaining the content itself.

### 4.2 Annals identity as the only compare-and-swap token — rejected

Rows written while ordinary Annals capture was disabled may have no claim. More importantly, the existing Lexicon Annals digest is not a collision-free serialization: it combines type, a unit separator, and newline-joined facts even though fact text is not forbidden from containing those delimiters. Historical digest bytes cannot be redefined without breaking durable claim identity, but that digest is unsafe as the sole proof of which fact array an operator inspected.

### 4.3 Current row plus one curation generation and versioned structured digests — selected

The current row gains lifecycle timestamps and a monotonically increasing `CurationGeneration`. Exact inspection returns that generation plus a new collision-free snapshot digest. New Lexicon Annals versions use the same structured encoding under an explicit hash-format code, while existing Annals bytes retain their legacy interpretation. This gives both compare-and-swap and future history exact content binding without rewriting historical identities.

## 5. Exact item identity and snapshot binding

An exact target is the complete tuple:

- typed scope: `Global`, or `Campaign` plus its exact Campaign id;
- server-normalized name;
- stable `lexicon_entries.Id`;
- current `CurationGeneration`;
- the structured snapshot digest of the stored type and ordered facts;
- the exact current lifecycle state, including `RetiredAtUtc` and `PinnedAtUtc`;
- the current Annals claim/version identity, revision, hash-format code, and digest when a head exists, or an explicit no-head state;
- the current sensitivity-label identity, artifact revision, artifact-content digest, and authority generation when protected, or an explicit no-label state.

The format-2 snapshot grammar is durable and exact. Its preimage is the UTF-8 byte sequence `Arcanum.Lexicon.Snapshot.v2\0`, one unsigned format byte `0x02`, the type's unsigned 32-bit big-endian UTF-8 byte length and bytes, the unsigned 32-bit big-endian fact count, then each fact's unsigned 32-bit big-endian UTF-8 byte length and bytes in stored order. Encoding uses strict UTF-8 and rejects unpaired UTF-16 surrogates or any count/byte length that cannot fit the unsigned field. SHA-256 of that preimage is the format-2 snapshot digest. It is computed from post-normalization values actually stored, never from the client's proposed replacement. Thus `['a\nb']` and `['a', 'b']`, repeated/reordered facts, empty values, non-ASCII text, embedded NUL/U+001F/newline, and maximum-length values all have pinned golden vectors. The same canonical snapshot bytes feed `DerivedArtifactContentDigest.ForBytes` when verifying or replacing a sensitivity label; the label's artifact-content digest remains its own domain-separated digest and is not conflated with the Annals/snapshot digest merely because both are SHA-256 values.

One canonical Lexicon value normalizer is shared by scribe, correction, digesting, `FactsJson`, `FactsText`, provenance ordinals, FTS, and label verification. Names and types are trimmed and retain the existing length bounds; scribe's blank type retains the existing type or uses `General` for a new row, while correction requires a non-empty replacement type. Facts are individually trimmed, empty results are discarded, exact ordinal duplicates are removed with the first occurrence retaining its order, and at least one fact is required. Persisted values are not whitespace-collapsed or control-stripped because those bytes are part of memory identity. Every model-facing render continues through the existing DATA framing and `SanitizeLexiconText` control/newline/heading hardening. No projection is allowed to normalize independently.

`CurationGeneration` starts at 1 for inherited and newly inserted rows. Every accepted write that changes canonical type, facts, retirement, or pin state—including `scribe_lexicon` merges and every operator mutation verb—increments it in the same transaction, with overflow refused rather than wrapped. No-op writes do not increment it. `UpdatedAt` changes only when type or facts change; retire, reinstate, pin, and unpin must not make old content appear newly learned to retention.

Every state-changing request carries the exact target returned by the detail view. The service acquires a `CovenantWriteLease` when the target is protected, opens `BEGIN IMMEDIATE`, locates the exact scope/name row without Campaign fallback, reads the Annals head and any sensitivity label in that same SQLite snapshot, and compares every target field against live state before writing. A mismatch is a typed stale-target conflict. The write lease is not reused as a snapshot-read capability; it authorizes the mutation only, is revalidated before commit and before protected response serialization, and remains held until serialization completes. This makes the CLI's read/confirm/write sequence a real compare-and-swap rather than a courtesy preview.

Matching target fields is necessary but not sufficient. Before any write, the service independently proves the live store is internally consistent. An active row's Annals head, when present, must be `Assert` or `Correct` and its `(hash format, digest, scope, Campaign, sensitivity)` must describe the canonical row under that format. A retired row must have a `Retire` head with matching scope and sensitivity. A live label must name `SensitiveArtifactKind.Lexicon`, the stable entry id, the exact owner scope, its current artifact revision, and the derived-artifact digest of the canonical format-2 snapshot bytes; unlabeled content must agree with `ContentSensitivity.None`. Claimless rows are the only permitted absence. Malformed, missing, or contradictory evidence returns a typed integrity failure and is never repaired implicitly by correction, retirement, or backfill.

A desired-state result (`AlreadyRetired`, `NotRetired`, `AlreadyPinned`, or `NotPinned`) writes nothing and appends no duplicate Annals revision, but only after the submitted target matches the current row exactly. A network retry carrying its predecessor target is therefore a stale-target conflict, not a successful replay. Durable request receipts are outside this slice, and the API does not accept an arbitrary later Annals head as proof that a caller's earlier write succeeded.

## 6. Storage — Core schema version 11

`lexicon_entries` gains three columns:

- `RetiredAtUtc TEXT NULL` — null means eligible for operational reads;
- `PinnedAtUtc TEXT NULL` — null means automatic retention may consider the row;
- `CurationGeneration INTEGER NOT NULL DEFAULT 1` — the exact current content-and-lifecycle generation.

The writers own the positive/monotonic generation invariant because SQLite cannot add the needed cross-update constraint with `ALTER TABLE`. Existing rows evolve to generation 1 with both lifecycle timestamps null. There is no eager content backfill and no initial Annals write.

`annal_versions` gains `ContentHashFormatCode INTEGER NOT NULL DEFAULT 1`. Format 1 names the existing store-specific legacy digest and preserves every historical byte. Format 2 names the domain-separated, length-prefixed Lexicon encoding from section 5. A closed `AnnalContentHashFormat` inventory makes the interpretation explicit; Saga and existing rows remain format 1, while every newly appended content-bearing Lexicon version uses format 2. The format is ignored for a null tombstone hash. Annals equality compares the pair `(ContentHashFormatCode, ContentHash)`, never bytes alone.

A new content-free `lexicon_annal_fact_provenance` object associates `(AnnalVersionId, FactOrdinal)` with the current projection's attachment/source coordinates: Session id, attachment id, logical key, attachment version and content hash, materialized instant, and source type. It never stores a fact, per-fact digest, `FactsJson`, or `FactsText`; the referenced Annals format-2 digest already commits the ordered fact array, and avoiding a retained unkeyed fact hash prevents short corrected-away facts from becoming an offline guessing oracle. Canonical normalization removes repeated facts, so one ordinal names exactly one occurrence. Whenever a structured Lexicon version is appended, the transaction snapshots the applicable current provenance by canonical ordinal before the mutable current projection changes. The primary key is `(AnnalVersionId, FactOrdinal)`, the ordinal is non-negative, and foreign-key ownership follows the Annals version so reset, Campaign erase, hard erase, and factory reset cannot orphan evidence.

Labeled correction uses a Lexicon-specific same-artifact replacement primitive because the stable `lexicon_entries.Id` is also purge ownership. Under `ArtifactReplacement` SQLite authorization, it CAS-checks the complete old label, deletes that exact row, and inserts a new label id for the same artifact id at checked revision `n + 1`. It preserves or strengthens sensitivity and generation provenance, preserves owner and producing evidence, and updates `session_sensitivity_state` without incrementing the tainted-artifact count for a replacement: maximum sensitivity/provenance remain monotonic and the projection revision advances. Artifact-revision overflow is a typed refusal. The canonical row, old-label removal, new label, Session projection, Annals, and other projections share one transaction and roll back together.

The version transition explicitly drops and recreates all three external-content `lexicon_fts` triggers; `CREATE ... IF NOT EXISTS` is insufficient because it would retain the v10 bodies. Insert indexes only when `new.RetiredAtUtc IS NULL`. Delete removes the old index row only when `old.RetiredAtUtc IS NULL`. Update deletes the old index row only when the old row was active and inserts the new row only when the new row is active. Therefore active correction, retirement, reinstatement, pin/unpin, and hard deletion of either active or retired rows leave FTS consistent in the same transaction as the canonical row. Query predicates still require `RetiredAtUtc IS NULL` as defense in depth and for non-FTS fallback paths.

The Core UTC-instant inventory/view is evolved in the same version step to include both new timestamp columns. The v11 transition resource inventory pins every statement in install order, and fresh/evolved catalog-equivalence tests cover both the table declaration and rebuilt triggers/views.

## 7. Correction and provenance

`correct` accepts a structured document containing the replacement `Type` and complete `Facts` array. It does not use the ordinary upsert/merge behavior. After validating and normalizing the complete replacement outside the transaction, one write transaction:

1. re-reads and validates the exact target, the independent row/Annals/label consistency invariants, and the exact-scope `CovenantWriteLease` when protected;
2. refuses a retired row;
3. if the row is claimless, appends an `AgentAsserted` format-2 baseline; if its current Annals head is legacy format 1, appends a `SystemBackfilled` format-2 restatement of that exact pre-mutation snapshot;
4. snapshots the pre-mutation fact/source coordinates to the content-free historical provenance ledger;
5. replaces `Type`, `FactsJson`, and derived `FactsText`, updates `UpdatedAt`, and increments `CurationGeneration`;
6. replaces the current fact-provenance projection: exact unchanged facts keep their existing attachment provenance, removed facts leave only their content-free historical evidence, and new operator-authored facts do not inherit a removed fact's source;
7. if content is labeled, invokes the Lexicon-specific CAS replacement from section 6 with a new label id on the same artifact id, the checked next artifact revision, and `DerivedArtifactContentDigest.ForBytes` of the new canonical snapshot bytes;
8. lets the lifecycle-aware update trigger republish the FTS row;
9. appends an Annals format-2 `Correct` revision with origin `OperatorStated`, binding the new canonical content, and snapshots the resulting version provenance;
10. revalidates write authority and commits only after every canonical, label, projection, provenance, FTS, and Annals write succeeds, then retains and revalidates the lease through protected response serialization.

If normalized replacement content is byte-for-byte the current canonical snapshot, the operation returns `Unchanged` and writes nothing. If provenance publication, FTS publication, or Annals publication fails, the transaction rolls back the canonical row and every projection together.

The existing `AnnalContentDigest.ForLexiconEntry` format remains unchanged as format 1 for historical compatibility. A new structured format-2 implementation is used for exact targets and new Lexicon Annals content. The two formats are named and tested; the implementation must never compare their bytes without also comparing the format code.

Annals timestamps preserve the bitemporal distinction. A claimless baseline or legacy-format restatement uses the canonical row's historical `UpdatedAt` as `ValidFromUtc`, but uses the curation transaction's current instant as `RecordedAtUtc`. The immediately following operator correction or retirement has its own current `ValidFromUtc` and `RecordedAtUtc`. No backfill backdates transaction time.

## 8. Retirement and reinstatement

Retirement first creates the same claimless or legacy-format structured baseline described for correction, then stamps `RetiredAtUtc`, increments `CurationGeneration`, and appends an `OperatorStated` Annals `Retire` tombstone in one transaction. It does not alter `UpdatedAt`. The lifecycle-aware FTS trigger removes the indexed row. The canonical row, facts, provenance, pin, and sensitivity label remain inspectable. Retiring an already-retired exact subject returns `AlreadyRetired` without another write only when the submitted target already describes that retired state.

A retired row is excluded from every operational matching and retrieval path. In particular:

- exact Campaign lookup ignores the retired Campaign row and then evaluates the active Global row;
- retired Global content is never the fallback;
- FTS and LIKE fallback require active rows;
- prompt assembly, internal tools, and daemon reads receive active content only;
- operator inspection/list/search surfaces may include retired rows, but must mark their lifecycle and retrieval ineligibility.

`scribe_lexicon` performs an exact-scope existence check before upsert. If the exact row is retired, it returns a typed refusal and neither mutates nor inserts. It does not follow Campaign fallback and update Global, and the retained unique row prevents a second same-scope identity from being manufactured around the refusal. A no-op against labeled active content remains allowed, but a content-changing scribe call against a labeled row fails closed because the tool has no authority to replace protected artifact evidence; operator `correct` is the supported protected-content mutation path.

Reinstatement clears `RetiredAtUtc`, increments `CurationGeneration`, leaves `UpdatedAt` unchanged, lets the same transaction restore the FTS projection, and appends an `OperatorStated` format-2 Annals `Correct` restatement binding the still-current content. It does not synthesize a new operation code. Reinstating an active exact subject returns `NotRetired` without writing only when the target exactly matches that active state. A later correction is a separate explicit action.

The existing hard-delete route remains an erase operation, but its guard lookup must use exact inspection semantics that can see a retired row. Retirement must never make a sensitivity-labeled entry invisible to conditional purge authority or the labeled-artifact guard.

## 9. Pinning and retention

Pin and unpin set and clear `PinnedAtUtc`, increment `CurationGeneration`, and leave `UpdatedAt` unchanged on the exact subject. Both have no-op desired-state outcomes only for an exactly matching target. They do not create a content claim revision because they do not change what the entry claims.

Retention planning excludes pinned Lexicon rows from candidates and reports both the total pinned inventory and how many rows the current plan would otherwise prune. A dry-run that merely omits them is incomplete because it hides why the configured rule reaches fewer rows.

Retention execution re-checks `PinnedAtUtc IS NULL` in the deleting statement or immediately under the same write transaction. A pin taken after planning but before apply must win. The affected-row accounting must distinguish a newly pinned exemption from a disappeared/already-pruned row rather than treating both as a successful planned delete.

No consolidation or decay sweep exists today. The durable flag and contract are intentionally phrased so those future automatic paths inherit the same exemption. Explicit operator correction, retirement, reinstatement, unpinning, and hard deletion remain allowed for a pinned row, as does `scribe_lexicon` on an active row.

## 10. HTTP contract

Existing effective-read and hard-delete routes remain compatible. Exact curation uses authenticated static POST routes so it cannot be confused with the existing name catch-all:

- `POST /api/memory/lexicon/show`
- `POST /api/memory/lexicon/correct`
- `POST /api/memory/lexicon/retire`
- `POST /api/memory/lexicon/reinstate`
- `POST /api/memory/lexicon/pin`
- `POST /api/memory/lexicon/unpin`

Exact `show` never performs Campaign-to-Global fallback. One deferred SQLite read transaction produces the entire detail and target: canonical row, lifecycle, current provenance, Annals head/history, historical source coordinates, and sensitivity label. It returns canonical content, typed scope and origin, stable identity, lifecycle timestamps, typed retrieval eligibility, curation generation, structured snapshot digest, exact sensitivity evidence, and the optional Annals history needed to bind a subsequent mutation. A claimless row is a successful response with an explicit absent head; any other torn or contradictory state fails closed rather than presenting an impossible target.

Mutation responses use the closed outcomes `Applied`, `Unchanged`, `AlreadyRetired`, `NotRetired`, `AlreadyPinned`, and `NotPinned`, plus the resulting detail projection where authority permits it. Missing exact subjects return 404, malformed names/scopes/content return 400, and stale targets or correction of a retired subject return 409. Integrity or authority failures fail closed with the existing mapped error semantics. Publication failure is an error with no partial durable effect.

Every new request, response, outcome, and nested DTO is registered on `ArcanumJsonContext`, every endpoint has a stable `.WithName(...)`, and every failable JSON response passes explicit source-generated type information.

Every Lexicon operator surface that returns protected content reads the complete projection and label in one SQLite snapshot, verifies the label owner, artifact revision, and derived-artifact digest of the canonical structured snapshot, and fails closed on a mismatch. Exact Global or exact Campaign inspection acquires the matching scoped `CovenantReadLease`. Effective Campaign lookup can fall through to Global and therefore acquires `CovenantInstallationReadLease` unless a future compound capability explicitly covers both. List, generic memory search, sources, explain, and any other result that can span arbitrary scopes also acquire `CovenantInstallationReadLease`. The selected generation-bound read lease is revalidated and retained through response serialization. Protected mutations instead use the exact-scope `CovenantWriteLease` contract from section 5 and likewise retain it through mutation-response serialization. Protected responses set `Cache-Control: no-store, private`, `Pragma: no-cache`, and `Expires: 0`, expose no validators, and carry the established conditional-read-authority endpoint metadata. Unlabeled rows retain their current read behavior.

## 11. CLI contract

The thin HTTP client exposes:

```text
arcanum memory lexicon show <name> [--campaign <id>]
arcanum memory lexicon correct <name> --file <path|-> [--campaign <id>]
arcanum memory lexicon retire <name> [--campaign <id>]
arcanum memory lexicon reinstate <name> [--campaign <id>]
arcanum memory lexicon pin <name> [--campaign <id>]
arcanum memory lexicon unpin <name> [--campaign <id>]
```

Omitting `--campaign` means exact Global scope; it never means effective lookup. Correction input is JSON containing `type` and the complete `facts` array, read from a file or standard input so replacement facts need not appear in shell history.

Before a mutation, the CLI calls exact `show`, renders the measured subject and effect, obtains confirmation unless `--yes` is present, and submits the returned target bundle without asking the operator to transcribe version fields. `--json` emits exactly one JSON document on stdout, with diagnostics on stderr, and all direct-command global options and exit-code conventions continue to apply.

## 12. Read boundaries and lifecycle

The implementation separates **inspection** reads from **operational** reads in names and interfaces. Inspection can include retired entries and always returns lifecycle markers. Operational methods return only eligible entries and implement Campaign fallthrough. Protected inspection additionally owns same-snapshot label verification. A general method whose callers can accidentally choose either behavior is not acceptable.

The lifecycle columns live on the row already owned by backup, factory reset, memory reset, and Campaign-scoped reset. The content-free version-provenance object joins the Annals-owned reset and erasure inventories and is tested for referential cleanup. The new instants join UTC diagnostics, and every existing projection, raw SQL column ordinal, reset fixture, and schema-equivalence inventory that depends on `lexicon_entries`, `annal_versions`, or the new provenance object is audited. Page-level database backup carries the columns, provenance evidence, and trigger definitions automatically.

Generic operator memory list/search may show retired Lexicon rows, clearly marked. Model-facing search and context construction may not. This distinction is tested at each production entry point, not inferred from one shared helper.

## 13. TDD and qualification

Implementation proceeds in observable red/green slices:

1. **Schema and identity:** v10 fingerprint fixture, v11 evolution, lifecycle columns, curation generation and overflow, Annals hash-format inventory, exact format-2 golden vectors and collision cases, canonical normalization, trigger replacement, instant inventory, and fresh/evolved equivalence.
2. **Correction:** exact-target stale detection across identity/content/lifecycle/Annals/label fields, complete replacement rather than merge, generation increments/no-ops, bitemporal claimless and legacy-head baselines, ordinal historical provenance survival without fact hashes, current-projection replacement, same-id labeled-artifact CAS replacement including artifact-revision overflow and Session-projection accounting, FTS replacement, and full rollback on publication failure.
3. **Retirement/reinstatement:** strict desired-state outcomes, opposite lifecycle races, structural FTS removal/restoration, all FTS/LIKE/exact/bulk operational filters, Campaign-to-Global fallthrough, scribe refusal including labeled content, baseline-plus-tombstone ordering, and hard-delete visibility of retired protected rows.
4. **Pin/retention:** generation-bearing lifecycle writes, pin/unpin races, dry-run inventory/exemption reporting, candidate exclusion, and apply-time race protection.
5. **Protected reads and integrity:** clean versus stale read/write authority epochs, leases retained through serialization, one-snapshot detail projection, no-store headers, mutation-time authority revocation, unlabeled behavior, wrong scope/id/label target rejection, and corruption-negative cases for head operation, hash format/digest, scope, Campaign, sensitivity, label owner/revision/digest, and missing evidence.
6. **API and CLI:** authenticated route contracts, source-generated JSON registration, exact Global/Campaign behavior, confirmation, stdin/file input, JSON stdout, exit codes, and generated command map.
7. **Cross-cutting regression:** concurrent scribe/correction serialization, claimless inherited rows, lifecycle/reset/backup/erasure ownership, hard-delete compatibility, retired FTS hard deletion, and Native AOT boundaries.

Acceptance tests enter through the mapped route or registered CLI verb and establish preconditions through production write paths. Before completion, the load-bearing tests are mutation-checked by temporarily breaking, one at a time, exact-target validation, lifecycle generation, structured Annals hashing, protected-label verification, retirement filtering, FTS publication, and the apply-time pin guard; each break must make the intended test fail.

Documentation travels in the same change: `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`, generated `docs/Arcanum.CommandMap.json`, and the relevant engineering reference. `docs/Compendium.README.md` changes only if a configuration key is introduced; none is planned. The root `README.md` remains untouched.

Final qualification runs the repository's full build, both named test suites, coverage threshold, AOT/IL warning gate, shipping `osx-arm64` publish, and native SQLCipher provenance gate from the worktree root. An independent requirements and code-quality review precedes integration.

## 14. Delivery

All implementation commits remain on `codex/issue-98-lexicon-version-operations`. After fresh qualification is green, that feature branch is merged into `memory-enhancement` with `--no-ff`; the integration branch is requalified at its exact merge SHA and pushed. Issue #98 is then closed with the delivered commit and verification evidence. Issues #78 and #73 remain open because this slice does not complete their remaining children.

The original dirty `main` checkout is not staged, reset, cleaned, or otherwise modified.

## 15. Closed inventories this change grows

- Core schema version constant, v10 source pin, v10 reconstruction fixture, and v11 transition list.
- Core UTC-instant inventory and its validation view.
- Annals content-hash format enum, SQL column, read/write projections, and legacy/new digest tests.
- Immutable Lexicon version-provenance schema, reset/erasure ownership, and source-detail projections.
- Lexicon read-projection column lists and every raw-SQL ordinal mapper.
- `ArcanumJsonContext` request/response registrations and endpoint names.
- Lexicon error-code inventory for invalid scope, stale target, retired mutation, protected-label mismatch, unavailable authority, and generation exhaustion.
- Data-retention plan/result inventory and dry-run rendering.
- CLI command tree and generated `docs/Arcanum.CommandMap.json`.
- Conditional Covenant read/purge endpoint inventories and protected response-header checks.
- Architecture-boundary/source tests that enumerate model-facing and operator-facing Lexicon reads.
- Setting descriptor/count inventories only if configuration is added; none is planned.
