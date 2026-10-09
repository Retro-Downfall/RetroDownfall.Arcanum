# Campaign Rollup Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for integrated TDD implementation, with independent parallel read-only review where useful. Steps use checkbox syntax.

**Goal:** Complete issue #77 so a short new Session receives bounded Campaign continuity, with exact revision, sensitivity and lifecycle evidence.

**Architecture:** Immutable Campaign artifacts are published behind guarded pointers from immutable per-Session contribution summaries. Logger scheduling, authenticated request maintenance, live prompt preparation and lifecycle cleanup share exact source and owner contracts.

**Tech Stack:** .NET 10, Native AOT, source-generated JSON, parameterized raw SQL on admitted SQLCipher connections.

**Spec:** [Campaign rollup design](../specs/2026-10-08-campaign-rollup-design.md), [issue #77](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/77), Arcanum.OATH.md §20.3.

**Baseline:** fc09a07eeca961ccb86e13d87e9d217d8933623b; latest main and pushed memory-enhancement match.

## Global Constraints

- Raw SQL and declarative schema evolution; no EF model or numbered migration changes.
- Default-off Arcanum:Features:CampaignRollups; gate-off DCI bytes and token accounting stay identical.
- Separate contribution artifacts exclude inherited fork prefix structurally.
- Artifact, guarded pointer, sensitivity, source receipts, cursor and checkpoint publish atomically.
- A turn uses one exact Campaign revision across rebuilds and provider attempts.
- Protected maintenance is authenticated, claim-bound, vector-scoped, zero-tool, structured and disclosed before dispatch.
- Preserve existing persisted enum codes and V1 policy/digest compatibility.
- 8,192-byte summary bound, 32,768-byte fold-page bound, 2,048 requested output tokens are code-owned mechanics.
- No attachment bytes, absolute host paths, prompt-cache content, audit bodies or implicit subagent inheritance.
- Preserve unrelated checkout changes. Commit and integrate only after required gates pass.

## Review Focus

1. Source replacement during provider inference: stale output cannot publish or advance a cursor.
2. Fork-of-fork and missing parent: copied evidence never contributes twice or receives an invented origin.
3. Disabled or revoked maintenance authority: no additional provider dispatch or protected read.
4. Entry/Session deletion and restored purge: stale summary text cannot survive in a prompt-visible Campaign artifact.
5. Structured-output retry and paid-answer interruption: physical attempts are disclosed/billed, and only committed checkpoints replay.

## Task 1: Revisioned sources, fork frontier and atomic Campaign publication

**Files:** Core/Storage new CampaignRollupContracts.cs; Infrastructure/Data new CampaignRollupStore.cs and contribution store; schema Tables/Triggers/Transitions/V16; GrimoireSchemaVersionChains.cs; SessionRepository.cs; appropriate UTC/schema inventories; new Data/CampaignRollupStoreTests.cs and Data/Schema/CampaignRollupSchemaEvolutionTests.cs; SessionRepositoryTests.cs.

**Interfaces:** Define ICampaignRollupStore operations for snapshot preparation, exact-revision read, guarded publication and transactional invalidation, using typed CampaignRollupInputSnapshot, CampaignRollupArtifactWrite and CampaignRollupWriteReceipt. Define a Session contribution snapshot and guarded write receipt with native sequence frontier. All writes return Result and require explicit expected revisions/source generation; no public ambient authority.

- [x] Pin v15 source fingerprint and prepare stable v15 reconstruction fixture before changing schema objects.
- [x] RED: after fresh schema installation, assert required Campaign objects/owner constraints; then prove fresh/evolved schemas match and resumed transition preserves existing rows.
- [x] Implement v16 object installation/evolution with consistent timestamp inventories.
- [x] RED: rollback a failed label/checkpoint write and assert artifact, pointer and cursor all remain at the prior revision; reject stale publication and replay identical committed manifest once.
- [x] Implement the exact publication transaction with label writes within that transaction.
- [x] RED: changed consumed source invalidates publication/refolds; unrelated Session UpdatedAt change does not create fold debt.
- [x] Implement source-revision/debt tracking with bounded snapshot paging.
- [x] RED: pure fork, fork with novel tail, nested fork, cutoff fork and missing-parent legacy fork exhibit the spec's exact contribution behavior.
- [x] Persist and backfill only provable copied-through frontiers; preserve surviving fork evidence when a source disappears.
- [x] Verify targeted store/schema/repository tests pass and review the persistence boundary.

## Task 2: Clean and protected summary maintenance

**Files:** Hosting/Loremaster.cs; Core/Storage/SessionDerivedArtifactContracts.cs; Data/Covenant/SessionDerivedArtifactStore.cs; SessionTurnClaimStore/coordinator; new summary maintenance service and protected provider adapter; DI; closed policy/derived-output inventories; Hosting/LoremasterTests.cs; new protected Campaign maintenance tests.

**Interfaces:** Consume Task 1 snapshots/receipts. Add a claim-derived nonserializable maintenance capability and companion Campaign checkpoint bound to the explicit source vector. A service shared by the clean Logger path and authenticated top-level orchestration returns committed receipts or typed deferred/failure states.

- [x] RED: a two-entry short idle/rested Session creates contribution debt even after ordinary summary debt clears; success produces immutable source evidence.
- [x] Route Session summary writes through guarded artifact replacement, coordinating the ordinary watermark/count within its transaction.
- [x] RED: a fork contribution provider payload contains novel tail and prior native contribution only; copied prefix and attachment bytes are absent.
- [x] Implement bounded contribution preparation and reuse root summary output only when actual inputs are identical.
- [x] RED: ambient protected input dispatches nothing; authenticated source-vector authority permits only the exact Campaign/source set and writes disclosure before dispatch.
- [x] Wire summary/Campaign maintenance into durable authenticated claims. Append distinct CampaignRollup/CampaignContribution artifact and maintenance codes, preserve all old selected-code digest bytes and the 249-case corpus, add separate new literal vectors, and use a dedicated companion checkpoint table without changing the old four-step mask semantics.
- [x] RED: same-boot expired claim, reset/remap/disable/deletion/authority changes prevent another dispatch; fix confirmed directly related claim-resume defect.
- [x] RED: provider error, malformed/oversized output, storage failure and pre-success cancellation advance no frontier; paid-result shutdown suffix commits once; committed resume does not dispatch again.
- [x] Implement guarded retries/checkpoint reuse using normal model resolution, pricing/reservation/audit and zero-tool executor enforcement.
- [x] Verify targeted maintenance/runtime tests and inspect every provider effect boundary.

## Task 3: Warm-session injection and precise token attribution

**Files:** Core/Configuration/PublicConfigurationSettings.cs and runtime projection; SystemPromptBuilder.cs; SystemPromptDocument.cs; SystemPromptAttributionMap.cs; ModelTokenizationContracts.cs; WizardIntelligenceProvider.cs and ContextPreview.cs; shared preparation/claim envelope; ModelTokenEstimator.cs; Compendium SettingDescriptors.cs; prompt/token/live/preview/config tests.

**Interfaces:** Consume exact Campaign snapshot from Task 1, obtained after authorized Task 2 maintenance on a live turn. Builder accepts sessionSummary and separate Campaign snapshot/gate input; typed attribution categories map to campaignRollup/sessionRollup sources.

- [x] RED golden: gate off preserves every existing prompt byte, legacy heading and source total; absent blocks preserve [None].
- [x] RED golden: gate on renders Campaign Summary (cross-session context) and Session Summary (compressed context) distinctly with adaptive fences and volatile cache descriptors.
- [x] Implement builder API/typed spans without reparsing untrusted summary headings for attribution.
- [x] RED: short Session B contains decisions from A without A transcript and below compression threshold; per-Session compression behavior remains intact.
- [x] RED: current-pointer advance during a tool continuation does not change the turn's bound revision; source invalidation refuses the next dispatch.
- [x] Wire shared live/preview snapshot preparation and every rebuild/fallback/retry.
- [x] RED: separate token rows sum to the rendered prompt total; heading-lookalike/fence attacks cannot shift attribution; preview performs no maintenance/provider summary call.
- [x] Add default-off config/property/descriptor/serialization parity and verify targeted tests.

## Task 4: Lifecycle, backup/transfer and memory surfaces

**Files:** MemoryEndpoints.cs; DataRetentionContracts.cs; RetentionSettings.cs and policy catalogs; DataRetentionService.cs/Pruning.cs/FactoryReset.cs; CampaignRepository.cs; GrimoireRepository.DeleteEntryAsync; shared protected erasure kernel/purge plans; BackupRestoreProtectedStatePurger.cs; ProtectedArtifactTransferStore.cs; CLI data scope mapping; API JSON context and descriptor inventories; relevant retention/memory/repository/backup tests.

**Interfaces:** Use Task 1's same transactional invalidation/purge kernel. Append dedicated stable reset/retention codes; authorize exact owners before protected metadata or mutation. New API payloads are typed and source-generated.

- [x] RED: status/sources/explain distinguish actual Campaign continuity from Session compression; off reports retained inventory but ineligible continuity.
- [x] RED: one Campaign reset/retention cannot affect another store/owner; stale plan and interrupted recovery preserve existing lifecycle authority semantics.
- [x] Implement targeted Campaign-summary scope, policy and table/label/checkpoint accounting.
- [x] RED: archive preserves continuity; hard Session delete, Entry delete, age pruning and source replacement immediately invalidate obsolete publication; Campaign deletion leaves no orphan artifacts/labels/pointers.
- [x] Integrate all deletion/erasure seams into the shared transaction kernel and add regression coverage for stale Session-summary source removal.
- [x] RED: physical restore/staged purge/remap cannot revive invalid source text; selective import excludes aggregate content for unselected Sessions and preserves valid sensitivity/finalization/fork evidence.
- [x] Implement compatible backup/transfer ownership and closed purge/inventory coverage.
- [x] Verify targeted API/CLI/lifecycle/backup/config tests.

## Task 5: Documentation, independent review and final qualification

**Files:** docs/Arcanum.DESIGN.md; Arcanum.API.md; Arcanum.Command.Reference.md; Compendium.README.md; Arcanum.OATH.md; required hosted-producer analysis catalogs; this plan's checked steps.

- [x] Record old/new DCI heading text, gate defaults, bounded mechanics, source/vector authority, fork policy and lifecycle rebuild semantics in owning docs.
- [x] Review complete base-to-candidate diff independently for spec compliance, correctness and test quality; fix validated related defects with RED regressions.
- [x] Run solution build and required Arcanum, Compendium and Forge tests; run coverage threshold, hosted-producer analysis, AOT/IL, osx-arm64 shipping/provider smoke and native SQLCipher provenance gates.
- [x] Confirm every required gate terminated successfully on the candidate tree; inspect warnings/skips rather than treating partial logs as completion.

## Qualification record, 2026-10-09

All required gates terminated successfully before committing the implementation.

| Gate | Result |
|------|--------|
| Solution build | Zero warnings and errors. |
| Arcanum runtime and coverage | 21,708 passed, 113 platform/environment skips, zero failures; 88.04% line and 76.93% branch coverage, all tiered thresholds met. |
| Full hosted-producer analysis | 2,019 passed, zero failures/skips; all 726 declared methods covered across four shards, with the existing rules and timeout. |
| Compendium | 186 passed, zero failures/skips. |
| Forge | 728 passed, two Windows-only skips, zero failures. |
| Native AOT/IL | osx-arm64 gate passed with zero first-party IL/AOT warnings; existing third-party audit warnings reviewed. |
| Shipping publish and provider contract | Exact Native AOT apphost and in-process provider smoke passed. |
| Native SQLCipher | All 17 osx-arm64 provenance checks passed. |
| Final v15 fixture compatibility | 19 schema evolution tests passed after blank-line whitespace normalization; the original published v15 fingerprint is unchanged. |

Production bytes remained unchanged after the final runtime/native qualification. Subsequent repairs affected test discovery ownership and the reviewed schema-backfill dispatch inventory. The final full analysis qualified those repairs and the deliberately reviewed 4,393-row capsule manifest, covering the same 40 operations. Its SHA-256 is `c8d927c977b5f89126f22c45037132428128eaf6386e112580342d44a102e78f`.

## Delivery procedure after qualification

1. Commit exact task files, preserving unrelated main-checkout edits.
2. Recheck remote main/memory-enhancement lineage; integrate the qualified candidate into memory-enhancement and push.
3. Prove delivered tree equivalence/ref alignment and cleanliness without redundant full suites after unchanged integration.
4. Close issue #77 as completed and verify live state; leave #73 open while its remaining capabilities are incomplete.
