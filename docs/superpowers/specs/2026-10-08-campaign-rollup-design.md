# Campaign rollup design for issue #77

Date: 2026-10-08
Status: Approved by the operator; implementation and TDD qualification are in progress.
Baseline: fc09a07eeca961ccb86e13d87e9d217d8933623b

## Goal and authority

Implement [issue #77](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/77) under the managed contract in [issue #73](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/73) and `Arcanum.OATH.md` §20.3. Session B in a Campaign receives decisions from Session A without A's transcript or operator restatement. The new capability stays default off.

The live prerequisite issues #83, #84, #86, and #102 are closed. Inspection nevertheless confirms that Loremaster still uses the legacy mutable Session-summary writer and `ArcanumInvocationContext.None`; the registered Session artifact writer and durable request-maintenance claims are not used by that path. Wiring the relevant summary maintenance is part of this vertical issue. Title, Saga, and Lexicon maintenance are outside this change except shared contract compatibility.

## Public behavior

Add `Arcanum:Features:CampaignRollups`, a default-false `{ get; set; }` property, descriptor and documentation. Bounds are code-owned, not new public tuning controls.

With the gate off, inference performs no Campaign-rollup lookup, adds no block, preserves the legacy compression heading and existing token attribution, and leaves retrieval unchanged. Data previously produced while enabled still participates in inspection and authorized lifecycle cleanup.

With the gate on, CONTEXT renders the Campaign block after Master Codex and before the optional Session block:

- `### Campaign Summary (cross-session context)`: a Campaign artifact loaded even when the active Session is short.
- `### Session Summary (compressed context)`: the existing compression-time Session summary.

The previous gate-off text remains exactly `### Campaign Summary (compressed context)`. The builder's old Session-summary parameter becomes `sessionSummary`, with explicit feature/rendering input for compatibility. Both new blocks use adaptive fences, carry volatile cache descriptors, and never acquire instruction or operator-authored authority.

Each turn binds one immutable Campaign artifact identity, revision, content digest and sensitivity/provenance at preparation. All prompt rebuilds, provider fallbacks, retries and tool continuations use that same snapshot. A later publication may update the pointer without changing an already-bound turn. Deletion, remapping, reset, restore, disablement or authority invalidation stop a later dispatch rather than substituting a different revision.

Live and preview paths share preparation. Preview never performs maintenance or starts a billable turn. Separate typed attribution spans and `campaignRollup` / `sessionRollup` token sources count the actual single rendered prompt; summary text that imitates a heading cannot move its cost to another source.

## Revisioned persistence

Use declarative raw-SQL objects in the existing Core schema tier. Pin the current version-15 fingerprint before editing objects, add Core version 16 with resumable transitions and a v15 reconstruction fixture, and prove fresh/evolved parity. Do not add an EF entity, compiled-model change, numbered migration or parallel schema installer.

A dedicated store owns:

- Immutable Campaign artifacts with Campaign owner, monotonic revision, bounded text, digest, sensitivity/provenance and producing maintenance evidence.
- A guarded current pointer and Campaign source-generation/refold state.
- Immutable per-Session contribution artifacts and their native-entry sequence frontiers.
- Exact consumed source revisions and committed maintenance checkpoints.
- Durable fork inherited-through frontier, retained even if its source Session disappears.

Use composite owner/revision foreign keys and immutable-write guards. Prior Campaign revisions remain available while an admitted unfinished turn requires their exact identity; lifecycle cleanup and retention use the same guarded kernel.

The proposed code-owned bounds are 8,192 UTF-8 bytes per Campaign/contribution summary, 32,768 UTF-8 bytes per fold input page, and 2,048 requested output tokens. These bound pages and artifacts, never total sessions or maintenance progress. Invalid or oversized output publishes nothing; refolding/structured correction creates a newly receipted physical attempt rather than appending or silently truncating.

Publication runs in one immediate transaction: recheck current Campaign revision, source generation, canonical binding/path evidence, exact source identities and label revisions; insert artifact and sensitivity label; publish pointer, consumed revisions, cursor and checkpoint. A stale writer publishes nothing. Identical committed input/checkpoint replay returns its receipt without another provider call.

## Contribution and fork semantics

Campaign rollups fold completed, revisioned Session contribution summaries, never raw transcripts. A completed contribution means a persisted summary checkpoint; shipped Session statuses are active and archived, not completed. Short rested/idle Sessions are eligible without reaching the old compression threshold, and unresolved contribution debt remains discoverable after ordinary Session-summary debt is cleared.

Ordinary Session compression continues to summarize the complete history. Campaign contribution summarizes only a Session's own new sequence tail plus its prior committed contribution. A root Session may reuse the same proven clean summary result when its two input sets are identical; a fork's contribution call must never receive the copied prefix.

The fork transaction records the exact copied-through sequence frontier. A pure fork contributes nothing; newly authored tail contributes once; a nested fork excludes its copied prefix in the same way. This rule also applies to an explicitly cross-Campaign fork: copied transcript remains available to that Session, but is not automatically promoted to the destination Campaign rollup.

Backfill derives a legacy fork frontier only from provable persisted lineage and deterministic copied-entry identity. If lineage cannot be established, fail closed for historical contribution and report the unavailable source; never invent an origin or let an LLM decide deduplication. Future tail can become eligible under a recorded conservative frontier.

Replacing a consumed contribution revision, deleting a contributor, or purging a source invalidates the current Campaign publication and creates refold debt in the same transaction. Refold starts from the remaining current eligible contributions, not the old Campaign text. Newly eligible previously-unconsumed contributions can fold incrementally. Timestamp-only watermarks and whole-fork-summary folds are prohibited.

## Maintenance and sensitivity

Retain Loremaster's timer/queue and Grimoire admission shell. A shared summary-maintenance service supplies contribution and Campaign folds. Clean background work must prove all inputs clean; ambient work cannot read or dispatch protected history.

An authenticated top-level request claim may derive a nonserializable, single-use maintenance capability for one exact Campaign source vector. Bind requesting Session/claim, canonical Campaign/physical-root revision, authority generations, every source owner/artifact/revision/content-and-label digest, and the pre-request checkpoint. A Session B lease alone never grants generic Session A access. No caller can manufacture this capability through a PingRequest field.

Protected maintenance injects no fresh Covenant, advertises zero tools at the executor boundary, pins structured output and records a durable disclosure before every physical provider attempt. Use existing model resolution, pricing, reservation and audit lanes. Revalidate authority and feature state before each dispatch and before guarded publication.

Persist a paid successful result through the existing uncancellable commit suffix, with source/authority CAS intact. Cancellation or interruption before success leaves cursors unchanged. A committed deterministic checkpoint is replayable; an uncertain provider attempt receives a new ordinal and cannot be mistaken for a committed result.

Sensitivity joins actual inputs, including previous rollup on an incremental fold. A clean refold may remove taint only when all surviving inputs and their provenance prove clean. Keep bounded generation provenance and exact dependency evidence. Append distinct sensitive-artifact codes for CampaignRollup and CampaignContribution and distinct maintenance-step codes for their outputs, preserving every existing code and digest domain. Existing V1 evidence hashes only the selected code, not the whole manifest, so append-only expansion remains compatible. Keep the 249-case existing digest corpus unchanged and add separate literal vectors. Extend artifact_sensitivity checks through schema evolution. Use a dedicated claim-bound Campaign-maintenance checkpoint table so the existing four-step table and CompletedStepMask retain their meaning.

## Lifecycle and inspection

Add the real Campaign rollup as its own content-free memory status/source/explain row. Label the old Session source honestly when enabled; retain compatibility when disabled. Explain reports eligibility independently of persisted count. Generic search does not become a protected summary-content read.

Append stable Campaign-summary retention/reset scope codes; never reorder existing persisted codes. Retention is disabled by default, with a 365-day configured default when enabled, consistent with other memory rules. Targeted reset supports exactly one selected Campaign, or the documented installation selection, and removes its derived artifacts, pointers, contribution state, labels and checkpoints together. Retained transcript may generate future summaries after an authorized maintenance event; reset is not a claim that provider copies were revoked.

Campaign deletion purges all Campaign-owned derived state and labels before parent deletion in the same transaction. Session archive preserves continuity. Actual Session hard deletion retires dependent publication and schedules a fresh refold. Entry deletion and Entry age pruning invalidate Session summary/contribution evidence so rebuilding cannot re-import deleted words through a stale summary.

Physical backups carry the new database objects and protected evidence. Restore validation, staged protected purges and Campaign remapping preserve or invalidate them atomically. Selective import never copies an aggregate of unselected Sessions or invents maintenance authority; only selected valid Session contribution/fork evidence can be transferred, and destination rollup is rebuilt. Integrate shared artifact purge policy, exact owner resolution, source dependency invalidation and closed derived-output inventory.

Attachment inputs remain consulted logical key/version metadata and useful decisions. New payloads and stored summaries reject raw attachment content and host absolute paths; apply code-owned sanitization/validation in addition to provider instructions. No new content enters audit logs, Chronicle, /v1 tools, prompt-cache prefixes or inherited subagent context.

## TDD and delivery

Start with behavioral RED tests for new schema ownership, atomic publication and fork/source debt. Test happy paths and interruption, concurrent source changes, deletion, reset, restore, stale physical roots and protected disclosure ordering. Golden prompt tests cover gate-off byte identity, both new blocks, empty CONTEXT and heading/fence attacks. Live/preview tests prove warm short-session continuity, frozen revision and separate cost.

Update DESIGN §5.4.2, §5.4.7–8, §10.2.3, §10.5, §10.6.1–2 and relevant protected maintenance/lifecycle sections; API, Command Reference, Compendium and OATH status travel with code. Root README changes only if public-front-page behavior intentionally changes.

Qualify the final candidate with the solution build, all required test projects, coverage threshold, hosted-producer analysis and applicable Native AOT, shipping-provider smoke and SQLCipher provenance gates. Fix validated related defects with regression tests. Commit only after qualification, integrate into the already-forwarded memory-enhancement branch, push, verify exact tree/ref equivalence, then close #77 as completed. Keep #73 open unless its whole umbrella is complete. Do not repeat expensive suites after an unchanged integration.
