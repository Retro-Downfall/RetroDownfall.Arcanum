# Issue #98 Lexicon Version Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver exact, inspectable Lexicon correction, retirement, reinstatement, pinning, and unpinning without retaining superseded plaintext, while preserving protected-memory authority, append-only evidence, active-only model retrieval, pin-aware retention, and the existing hard-delete contract.

**Architecture:** Keep `lexicon_entries` as the sole plaintext authority and add lifecycle plus a monotonic curation generation. Bind every operator write to an exact Global-or-Campaign target containing the canonical structured snapshot digest, lifecycle, Annals head, and sensitivity label. Perform canonical row, current provenance, content-free historical provenance, Annals, sensitivity-label replacement, FTS, and retention-relevant changes in one admitted SQLite transaction; inspection and API layers retain the narrowest Covenant lease through protected response serialization.

**Tech Stack:** .NET 10, C# 13, ASP.NET Core Minimal APIs, source-generated `System.Text.Json`, Microsoft.Data.Sqlite with SQLCipher, FTS5 external-content triggers, xUnit, System.CommandLine, Native AOT, Git, and GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-27-issue-98-lexicon-version-operations-design.md`

## Global Constraints

- Work only in `/Users/mat/.codex/worktrees/issue-98-lexicon-curation/RetroDownfall.Arcanum` on `codex/issue-98-lexicon-version-operations` until the delivery task deliberately moves immutable refs.
- The approved base is `959729244cb611e1fd45a094987d9da6b33e6b58`. Preserve the unrelated dirty primary checkout exactly; never stash, reset, clean, overwrite, stage, or expose its files.
- The already committed archived-publish repair (`e3b41bf9`) and approved design (`4aac874b`) are part of the feature branch. Do not rewrite either commit.
- Follow RED -> GREEN -> REFACTOR for every production behavior. A production edit is allowed only after a focused behavioral test has failed for the expected reason. After GREEN, temporarily reverse each load-bearing fix listed in the task, prove the intended test turns RED, then restore and rerun GREEN.
- Use production schema installers, SQLCipher connections, mapped endpoints, and the registered CLI tree in acceptance tests. Mocks may isolate authority timing or injected publication failure, but must not replace the durable boundary the test claims to prove.
- Keep `lexicon_entries` as the only plaintext authority. Never store superseded `Type`, `FactsJson`, `FactsText`, individual facts, or per-fact digests in historical tables, logs, errors, or request receipts.
- Correction changes only `Type` and the complete ordered facts. It never renames an entry, changes its normalized name, moves it between scopes, or replaces its stable id.
- Keep legacy `AnnalContentDigest.ForLexiconEntry` bytes unchanged as hash format 1. Every newly appended content-bearing Lexicon version and every curation target uses format 2. Annals equality compares `(ContentHashFormatCode, ContentHash)`.
- Use one canonical Lexicon normalizer for scribe, correction, snapshot digesting, `FactsJson`, `FactsText`, FTS, and provenance ordinals. Trim name/type/facts, discard blank facts, exact-ordinal deduplicate with first occurrence winning, require at least one fact, and do not independently strip or collapse stored control characters.
- Use strict UTF-8 for format-2 snapshots and reject unpaired UTF-16 surrogates and unsigned-length/count overflow. Do not use `BinaryWriter` platform endianness or reflection-based JSON.
- Every mutation is exact-scope and exact-target. Campaign operations never fall back to Global. A predecessor target submitted after success returns a stale-target conflict; it is not treated as an idempotent replay.
- Before any mutation, independently validate the row, Annals head, and sensitivity label against one SQLite snapshot. Contradictory evidence fails closed and is never repaired implicitly by a curation write.
- Materialize the mutation response detail inside that same write transaction after every publication succeeds and before commit; never commit and then re-query a potentially newer row for the response.
- A claimless or legacy-format Lexicon head receives a format-2 baseline in the same transaction before correction, retirement, or reinstatement. Pin and unpin never append content history.
- Curation evidence is mandatory even when ordinary Annals capture is disabled; do not gate baseline, correction, retirement, or reinstatement writes on that setting.
- Retired rows remain inspectable and explicitly deletable, but are excluded from every model-facing exact, Campaign-fallback, FTS, LIKE, prompt-assembly, tool, daemon, and bulk operational read.
- Protected exact inspection uses a scoped read lease. Effective Campaign-to-Global lookup and arbitrary-scope list/search/sources/explain use an installation read lease. A protected mutation uses an exact-scope write lease. Retain and revalidate the selected lease through response serialization.
- Every API payload is registered in `ArcanumJsonContext`; every failable `Results.Json` call passes explicit `JsonTypeInfo`; no anonymous wire DTOs or `[JsonPropertyName]` attributes are introduced.
- Keep the existing effective GET and hard DELETE API routes compatible. New curation routes are static POST routes so the name catch-all cannot capture them.
- Keep the CLI thin: exact show, render/confirm, then submit the returned target unchanged. Under `--json`, stdout contains exactly one JSON document and diagnostics go to stderr.
- Add no Ward or second approval gate. These authenticated operator actions use the existing CLI confirmation contract; the closed #197 amendment remains record-only.
- Update `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`, `docs/Arcanum.Engineering.md`, and generated `docs/Arcanum.CommandMap.json`. Do not change the root `README.md` or `docs/Compendium.README.md` because this feature adds no public-front-page statement or configuration key.
- Commit each task separately after its focused matrix is green and reviewed. Do not merge, push, or close #98 before the final immutable-SHA qualification and two independent final reviews are green.

## Stable Contract Names

Create these types under `src/RetroDownfall.Arcanum.Core/Lexicon/LexiconCurationContracts.cs` unless an existing type already expresses the exact contract:

```csharp
public enum LexiconScopeKind { Global = 1, Campaign = 2 }

public enum LexiconRetrievalEligibility { Eligible = 1, Retired = 2 }

public enum LexiconCurationOutcomeKind
{
    Applied = 1,
    Unchanged = 2,
    AlreadyRetired = 3,
    NotRetired = 4,
    AlreadyPinned = 5,
    NotPinned = 6,
}

public sealed record LexiconCurationScope(LexiconScopeKind Kind, Guid? CampaignId);

public sealed record LexiconEntryLifecycle(
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? PinnedAtUtc);

public sealed record LexiconReplacementContent(string Type, string[] Facts);

public sealed record LexiconCurationAnnalHead(
    [property: JsonRequired] bool IsPresent,
    string? ClaimId,
    string? VersionId,
    int? Revision,
    AnnalOperation? Operation,
    AnnalContentHashFormat? ContentHashFormat,
    string? ContentHash);

public sealed record LexiconCurationSensitivityLabel(
    [property: JsonRequired] bool IsPresent,
    Guid? LabelId,
    ulong? ArtifactRevision,
    string? ArtifactContentDigest,
    GenerationProvenance? GenerationProvenance);

public sealed record LexiconCurationTarget(
    LexiconCurationScope Scope,
    string NormalizedName,
    Guid EntryId,
    long CurationGeneration,
    string SnapshotDigest,
    LexiconEntryLifecycle Lifecycle,
    LexiconCurationAnnalHead AnnalHead,
    LexiconCurationSensitivityLabel SensitivityLabel);

public sealed record LexiconCurationResult(
    LexiconCurationOutcomeKind Outcome,
    LexiconEntryDetail Entry);

public sealed record LexiconAnnalFactProvenance(
    string AnnalVersionId,
    int FactOrdinal,
    Guid SessionId,
    Guid AttachmentId,
    string LogicalKey,
    int AttachmentVersion,
    string AttachmentContentHash,
    DateTimeOffset MaterializedAt,
    string SourceType);

public sealed record LexiconEntryDetail(
    LexiconEntryDto Entry,
    LexiconCurationScope Scope,
    AnnalOrigin? CurrentOrigin,
    LexiconEntryLifecycle Lifecycle,
    LexiconRetrievalEligibility Eligibility,
    long CurationGeneration,
    string SnapshotDigest,
    LexiconCurationTarget Target,
    AnnalClaimVersion[] AnnalHistory,
    LexiconAnnalFactProvenance[] HistoricalFactProvenance);

public sealed record LexiconInspectionResult<T>(
    T Value,
    bool ContainsProtectedContent);
```

`LexiconEntryDetail` additionally carries the canonical entry, typed scope/origin, lifecycle, eligibility, target, current fact provenance, ordered Annals history, and content-free historical source coordinates. Explicit `[JsonRequired] IsPresent` discriminators are mandatory: omitted JSON must fail deserialization and explicit `false` must remain distinguishable. An absent Annals arm has every other field null. A present `Assert`/`Correct` arm requires identity, revision, operation, persisted format, and digest. A present `Retire` arm requires identity, revision, operation, and persisted format but requires a null content digest. A sensitivity arm follows the same all-null versus all-required rule.

Add `AnnalContentHashFormat` under `Core/Annals/AnnalEnums.cs` with `LegacyStoreDigest = 1` and `LexiconStructuredSnapshot = 2`. If implementation discovers an existing repository term is more precise, change the symbol consistently in code, tests, and docs without changing these semantics.

Add only the missing Lexicon error inventory under `ErrorCodes.Lexicon`: `InvalidScope`, `InvalidCurationTarget`, `InvalidReplacement`, `StaleCurationTarget`, `RetiredMutationRefused`, `ProtectedMutationRefused`, `CurationIntegrityFailed`, `CurationGenerationExhausted`, and `ArtifactRevisionExhausted`. Add `ErrorCodes.Data.PinnedAfterPlanning` for the public retention conflict. Reuse the existing Covenant authority errors for gate refusal/revocation rather than duplicating them.

## Review Focus

- No superseded plaintext or per-fact guessing oracle survives a correction.
- Exact-target comparison includes scope, normalized name, stable id, generation, snapshot digest, lifecycle, Annals presence/head/format/digest, and label presence/id/revision/content digest/generation provenance.
- Target comparison happens before desired-state no-op evaluation; an old target never becomes a false success.
- Active-row filtering is explicit at every operational SQL boundary, including non-FTS fallbacks and Campaign shadowing.
- Claimless and legacy-head baselines, provenance snapshots, label replacement, FTS publication, and canonical row updates share one transaction and fully roll back on failure.
- Label replacement preserves ownership, producing evidence, sensitivity, and monotonic provenance; `session_sensitivity_state.TaintedArtifactCount` does not grow for same-artifact replacement.
- A post-plan pin wins the retention apply race and is reported distinctly from a missing/already-pruned candidate.
- Covenant leases are acquired before protected bytes are read, revalidated before commit/serialization, and disposed exactly once.
- API and CLI use source-generated JSON, static routes, exact scopes, correct status/exit mapping, and one-document stdout.
- Reset, erase, backup, UTC, schema-fingerprint, AOT, EF-boundary, command-map, and endpoint inventories are complete.

---

### Task 1: Freeze Canonical Lexicon Identity and Format-2 Digests

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Lexicon/LexiconCurationContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Lexicon/LexiconValueNormalizer.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Lexicon/LexiconSnapshotDigest.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Annals/AnnalEnums.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconValueNormalizerTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconSnapshotDigestTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCurationContractTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalContentDigestTests.cs`

**Result:** One shared canonical value path and one durable, collision-safe format-2 snapshot identity exist before any persistence work. Existing format-1 bytes remain pinned.

- [ ] **Step 1: Write contract and normalizer RED tests**

In `LexiconCurationContractTests`, use a test-local source-generated JSON context to cover valid Global/Campaign scopes, zero/missing Campaign ids, omitted `IsPresent` versus explicit `false`, malformed partial evidence arms, `Assert`/`Correct` digest requirements, `Retire` null-digest requirements, digest hex shape, and enum zero/unknown refusal without introducing reflection serialization. In `LexiconValueNormalizerTests`, cover name/type trimming, correction rejecting blank type, scribe preserving current type or defaulting a new row to `General`, fact trimming, blank removal, exact ordinal deduplication with first occurrence retained, at-least-one-fact validation, existing length bounds, embedded NUL/U+001F/newline preservation, non-ASCII, and unpaired surrogate refusal. Require one result object to supply `Name`, `NameNormalized`, `Type`, `Facts`, serialized `FactsJson`, and derived `FactsText` so no caller can normalize independently.

- [ ] **Step 2: Run the normalizer tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconCurationContractTests|FullyQualifiedName~LexiconValueNormalizerTests"
```

Expected RED: the curation contracts, required-presence validation, shared normalizer, and correction-specific validation do not exist.

- [ ] **Step 3: Implement the minimum canonicalizer and curation contracts**

Keep the domain types wire-safe and AOT-safe. Validate `LexiconCurationScope`: Global requires null Campaign id; Campaign requires a non-empty id. Validate `IsPresent` evidence arms so every absent arm has all nullable fields null and every present arm has all required fields.

- [ ] **Step 4: Write digest RED tests with pinned vectors**

Require the exact preimage `UTF8("Arcanum.Lexicon.Snapshot.v2\0") || 0x02 || u32be(typeByteLength) || typeBytes || u32be(factCount) || each(u32be(factByteLength) || factBytes)` and pin these SHA-256 results:

```text
general + [alpha]                                  1D1CA295A1538D2AEE3794E497EF7766CAA7038BD6D953324A16F0D3B4F422EB
A\0B + [line1\nline2, unit\u001Fseparator]         4BAD0C07880E6A9A8EE2BDA5F0FAA7C7DF5974F95E0AE4112BF4B8EAAFD8D3E1
魔法 + [café, 🧙]                                  5EB4D021B536093A672F565E4ED6FD598BF89DFEB2724E7B88A81FBD832AF1A2
T + [b, a]                                         588222A37CE2E0202F36893FD71C56CE258D425DCA1F4988097A1C9CBB93D9B2
T + [a, b]                                         4969AEF8A80E6CF3C81C48759341DD3BAE779CDEB3B8D0F2A8DB7EFC74B51396
```

Also prove one-fact-with-newline differs from two facts, type/fact boundaries cannot collide, repeated facts disappear before digesting, unpaired surrogates fail, and format/count/byte-length limits fail closed.

- [ ] **Step 5: Run the digest tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconSnapshotDigestTests|FullyQualifiedName~AnnalContentDigestTests"
```

Expected RED: no format-2 codec exists. Existing legacy-vector tests must remain green.

- [ ] **Step 6: Implement the strict encoder and digest**

Expose exact canonical bytes for both SHA-256 snapshot identity and `DerivedArtifactContentDigest.ForBytes`; do not duplicate serialization in the label path. Use checked conversions for every unsigned length/count and a strict `UTF8Encoding` instance.

- [ ] **Step 7: Prove GREEN and mutation sensitivity**

Run all three new test classes plus `AnnalContentDigestTests`. Temporarily write one length little-endian and require a pinned vector to fail; restore big-endian and rerun green. Temporarily bypass fact deduplication and require the repeated-fact normalizer test to fail; restore and rerun green.

- [ ] **Step 8: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Core/Lexicon/LexiconCurationContracts.cs src/RetroDownfall.Arcanum.Core/Lexicon/LexiconValueNormalizer.cs src/RetroDownfall.Arcanum.Core/Lexicon/LexiconSnapshotDigest.cs src/RetroDownfall.Arcanum.Core/Annals/AnnalEnums.cs src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconValueNormalizerTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconSnapshotDigestTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCurationContractTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalContentDigestTests.cs
git diff --cached --name-only
git commit -m "feat: define canonical Lexicon curation identity"
```

---

### Task 2: Evolve Core Schema Version 11 Without Losing Catalog Identity

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/lexicon_entries.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_versions.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/lexicon_annal_fact_provenance.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/FullTextSearch/lexicon_fts.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/lexicon_entries_ai.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/lexicon_entries_au.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/lexicon_entries_ad.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Views/grimoire_utc_instant_columns.sql`
- Create in this exact order:
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/010_lexicon_entries_retired_at_utc.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/020_lexicon_entries_pinned_at_utc.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/030_lexicon_entries_curation_generation.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/040_annal_versions_content_hash_format_code.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/050_lexicon_annal_fact_provenance.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/060_lexicon_entries_ai_drop.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/061_lexicon_entries_ai.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/070_lexicon_entries_ad_drop.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/071_lexicon_entries_ad.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/080_lexicon_entries_au_drop.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/081_lexicon_entries_au.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/090_lexicon_fts_delete_all.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/100_lexicon_fts_repopulate_active.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/110_grimoire_utc_instant_columns_drop.sql`
  - `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/120_grimoire_utc_instant_columns.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/UtcInstantColumnInventory.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionTenFixture.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/LexiconCurationEvolutionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaSourceFingerprintTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaCatalogTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/UtcInstantColumnInventoryTests.cs`

**Result:** Fresh and evolved databases converge on one v11 catalog: lifecycle and generation columns, hash-format-aware Annals, content-free historical provenance, active-only FTS, and UTC diagnostics.

- [ ] **Step 1: Write the v10 fingerprint RED**

Create `CoreSchemaVersionTenFixture` by reconstructing the present v10 tree from the future head and pin `B484778B9288D99C4337FA3C95BEB56B95FDAE6B1D149C6A9A2A822591BBE951`. Freeze the old Lexicon table, Annals table, three FTS trigger bodies, and UTC view while excluding the new provenance table; merely subtracting the new object is insufficient because all six existing objects change. Add `Version_ten_reconstruction_matches_the_pinned_fingerprint` and change the current-version assertion from 10 to 11. Run only those tests; they must fail because the fixture/chain step is absent.

- [ ] **Step 2: Write fresh/evolved catalog RED tests**

Model `LexiconCurationEvolutionTests` on `SagaCurationEvolutionTests`. Assert:

- inherited rows receive `RetiredAtUtc = NULL`, `PinnedAtUtc = NULL`, `CurationGeneration = 1`, and inherited Annals rows receive format 1;
- fresh and v10->v11 evolved `sqlite_schema` definitions match after normalization;
- the new provenance table has PK `(AnnalVersionId, FactOrdinal)`, non-negative ordinal, Annals-owned cascade, attachment identity/source coordinates, and no fact/fact-hash/content columns;
- all three FTS triggers are structurally replaced and enforce active-only insert/delete/update behavior;
- the UTC view and managed inventory contain all three new instant columns: `lexicon_entries.RetiredAtUtc`, `lexicon_entries.PinnedAtUtc`, and `lexicon_annal_fact_provenance.MaterializedAt`; the Core instant-column count grows from 115 to 118;
- transition resource order is deterministic and every head-changing statement is represented.

- [ ] **Step 3: Run the schema cluster and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconCurationEvolutionTests|FullyQualifiedName~GrimoireSchemaSourceFingerprintTests|FullyQualifiedName~GrimoireSchemaVersionChainTests|FullyQualifiedName~UtcInstantColumnInventoryTests"
```

Expected RED: v11 and its objects do not exist; current version remains 10.

- [ ] **Step 4: Implement the v11 head and transition**

Add columns with SQLite declaration shapes that fresh creation reproduces exactly. Define historical provenance columns exactly as `AnnalVersionId`, `FactOrdinal`, `SessionId`, `AttachmentId`, `LogicalKey`, `AttachmentVersion`, `AttachmentContentHash`, `MaterializedAt`, and `SourceType`; never add content text or a per-fact digest. Drop/recreate the three FTS triggers in the transition; `CREATE IF NOT EXISTS` alone is forbidden. Rebuild the external-content FTS projection using active rows only. Add the v10 source pin before changing the head files. Do not add an EF migration or compiled-model property.

- [ ] **Step 5: Prove catalog GREEN and mutation sensitivity**

Run the focused schema cluster plus `GrimoireSchemaInstallerTests`, `GrimoireSchemaTransitionResourceTests`, `GrimoireSchemaCatalogTests`, and `UtcInstantPersistenceBoundaryTests`. Temporarily remove the `RetiredAtUtc IS NULL` arm from the update trigger and require the structural/behavioral trigger test to fail; restore it. Temporarily alter the v10 pin and require the reconstruction test to fail; restore it.

- [ ] **Step 6: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/lexicon_entries.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_versions.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/lexicon_annal_fact_provenance.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/FullTextSearch/lexicon_fts.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/lexicon_entries_ai.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/lexicon_entries_au.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/lexicon_entries_ad.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Views/grimoire_utc_instant_columns.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/010_lexicon_entries_retired_at_utc.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/020_lexicon_entries_pinned_at_utc.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/030_lexicon_entries_curation_generation.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/040_annal_versions_content_hash_format_code.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/050_lexicon_annal_fact_provenance.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/060_lexicon_entries_ai_drop.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/061_lexicon_entries_ai.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/070_lexicon_entries_ad_drop.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/071_lexicon_entries_ad.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/080_lexicon_entries_au_drop.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/081_lexicon_entries_au.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/090_lexicon_fts_delete_all.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/100_lexicon_fts_repopulate_active.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/110_grimoire_utc_instant_columns_drop.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V11/120_grimoire_utc_instant_columns.sql src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/UtcInstantColumnInventory.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionTenFixture.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/LexiconCurationEvolutionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaSourceFingerprintTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaCatalogTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/UtcInstantColumnInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/UtcInstantPersistenceBoundaryTests.cs
git diff --cached --name-only
git commit -m "feat: evolve the Lexicon curation schema"
```

---

### Task 3: Make Annals Hash-Format-Aware and Own Historical Provenance

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Annals/AnnalClaimVersion.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Annals/IAnnalsStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsErasurePlan.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/MemoryAnnalsBackfill.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantProtectedArtifactErasureKernel.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalFormatTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalProvenanceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsErasureTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionAnnalsErasureTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureKernelTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs`

**Result:** Readers expose version hash format and digest, writers compare format plus bytes, structured Lexicon versions can snapshot content-free ordinal provenance, and every erase/reset owner removes it.

- [ ] **Step 1: Write format-aware Annals RED tests**

Extend `AnnalClaimVersion` to require `ContentHashFormat` and `ContentHash`. Test that old rows materialize as format 1; a format-2 Lexicon version round-trips; equal hash bytes with different format codes are not equal content; tombstones carry no content hash and ignore format for equality; invalid persisted format codes fail closed.

- [ ] **Step 2: Run the Annals format cluster and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconAnnalFormatTests|FullyQualifiedName~AnnalsStoreTests|FullyQualifiedName~LexiconAnnalsWriteThroughTests|FullyQualifiedName~SagaAnnalsWriteThroughTests"
```

Expected RED: the store neither persists nor returns `ContentHashFormatCode`.

- [ ] **Step 3: Implement format-aware reads and writes**

Make `AnnalsClaimWriter` accept an explicit format for content-bearing appends and return the exact inserted `AnnalVersionId` to transaction-bound callers; keep simple format-1 wrappers only for existing non-Lexicon callers. Extend every SELECT/INSERT ordinal and compare `(format, hash)`. Preserve existing Saga behavior byte-for-byte and make `MemoryAnnalsBackfill` explicitly write format 1; v11 performs no eager Lexicon content backfill.

- [ ] **Step 4: Write provenance and erasure RED tests**

Append one structured Lexicon version with two current provenance rows and assert the historical table stores ordinal plus coordinates but no fact or fact hash. Correct/remove the source facts and prove coordinates remain. Hard-delete, Campaign reset, memory reset, factory reset, and protected-artifact erase must leave no orphan. A failed Annals/provenance insert must roll back the owning mutation in later service tests.

- [ ] **Step 5: Implement the provenance writer/reader and ownership closures**

Add an internal transaction-bound append helper; never expose a standalone writer capable of publishing evidence for an uncommitted row. Extend `IAnnalsStore` only with read projections needed by exact detail. Delete provenance before `annal_versions` wherever explicit order is required; rely on the FK cascade only as defense in depth.

- [ ] **Step 6: Prove GREEN and mutation sensitivity**

Run the Annals/provenance/erasure cluster. Temporarily compare only digest bytes and require the format-mismatch test to fail; restore. Temporarily omit the factory-reset inventory row and require its exact cleanup/inventory test to fail; restore.

- [ ] **Step 7: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Core/Annals/AnnalClaimVersion.cs src/RetroDownfall.Arcanum.Core/Annals/IAnnalsStore.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsStore.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsErasurePlan.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/MemoryAnnalsBackfill.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantProtectedArtifactErasureKernel.cs tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalFormatTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalProvenanceTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionAnnalsErasureTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureKernelTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs
git diff --cached --name-only
git commit -m "feat: version Lexicon Annals evidence"
```

---

### Task 4: Split Exact Inspection From Active Operational Reads

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Core/Lexicon/ILexiconService.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Lexicon/ILexiconCurationService.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Lexicon/LexiconEntryDto.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Memory/MemoryDtos.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.CurationInspection.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Support/FakeLexiconService.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCurationInspectionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCampaignScopeTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/SystemPromptBuilderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Daemons/UnseenServantDaemonJobTests.cs`

**Result:** Exact inspection can see lifecycle state and produce a complete target from one deferred read transaction, while every model-facing method returns eligible rows only and retired Campaign rows fall through to active Global rows.

**Service shape:**

```csharp
public interface ILexiconCurationService
{
    Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowExactAsync(
        LexiconCurationScope scope,
        string name,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowEffectiveAsync(
        LexiconCurationScope requestedScope,
        string name,
        ICovenantSnapshotReadLease? installationReadLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconCurationResult>> CorrectAsync(
        LexiconCurationTarget target,
        LexiconReplacementContent replacement,
        CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconCurationResult>> RetireAsync(
        LexiconCurationTarget target,
        CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconCurationResult>> ReinstateAsync(
        LexiconCurationTarget target,
        CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconCurationResult>> PinAsync(
        LexiconCurationTarget target,
        CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconCurationResult>> UnpinAsync(
        LexiconCurationTarget target,
        CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default);

    Task<Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>> ListInspectionAsync(
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default);
}
```

The endpoint owns optional lease acquisition and transfer. `LexiconInspectionResult<T>` is an internal service-boundary result, not a wire DTO; it tells the endpoint whether the verified projection contains protected content. Add lifecycle timestamps, curation generation, and typed retrieval eligibility as trailing fields on `LexiconEntryDto` so existing list/effective-read JSON remains structurally compatible while retired operator rows are explicit. Add optional typed Lexicon lifecycle/eligibility fields to `MemorySearchResultDto` for Lexicon results only. The service requires a non-null matching lease whenever live label evidence is present and rejects an unexpected/mismatched lease. `ShowEffectiveAsync` performs Campaign then Global resolution and label verification inside one deferred read transaction under an installation lease. If an implementation-owned non-wire ownership wrapper makes transfer safer, preserve these semantic inputs/outputs.

Remove the ambiguous inspection `ListAsync` from `ILexiconService`; its model/tool/daemon methods remain active-only. Route explicit operator list/search through the lightweight `ILexiconCurationService.ListInspectionAsync` summary rather than materializing every entry's full Annals history. Keep exact active operational lookup and exact all-lifecycle detail inspection as separately named methods so no caller can select behavior with a boolean flag.

- [ ] **Step 1: Write active-only retrieval RED tests**

Seed active and retired Global/Campaign twins through production SQL fixtures. Assert inherited and newly scribed rows expose generation 1; exact operational get ignores retired; Campaign effective get and entity match fall through from retired Campaign to active Global; retired Global never returns; FTS and LIKE fallback exclude retired; list/inspection may include retired with lifecycle and `Retired` eligibility. Add source-boundary tests for prompt/tool/daemon bulk reads that would fail if an unfiltered general-purpose query were introduced.

- [ ] **Step 2: Run retrieval tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconServiceTests|FullyQualifiedName~LexiconCampaignScopeTests|FullyQualifiedName~LexiconCurationInspectionTests|FullyQualifiedName~SystemPromptBuilderTests|FullyQualifiedName~UnseenServantDaemonJobTests"
```

Expected RED: existing queries have no lifecycle columns or active predicates.

- [ ] **Step 3: Implement the read split and expand every row mapper**

Make `LexiconService` partial. Keep `ILexiconService` operational and make method names/summaries explicit about eligibility; put exact and list lifecycle-bearing inspection on `ILexiconCurationService`. Replace the shared six-column projection and every raw-SQL ordinal consistently. Require `RetiredAtUtc IS NULL` in SQL even when FTS triggers already exclude retired rows.

- [ ] **Step 4: Write exact-detail consistency RED tests**

From one deferred SQLite transaction, require a complete `LexiconEntryDetail` and exact target for: claimless/unlabeled; format-1 head; format-2 head; labeled active; retired. Require effective Campaign inspection to resolve Campaign then Global and verify the selected row plus label inside that same transaction. Require list inspection to verify every label in its one snapshot and return an explicit `ContainsProtectedContent` aggregate without materializing full history. Corrupt each independent invariant—head operation, hash format/digest, scope, Campaign, sensitivity, label owner kind/id/scope/revision/digest/generation provenance, malformed presence arm—and assert a typed integrity failure rather than a target. Prove normalized name and scope are server-derived, not echoed from the request.

- [ ] **Step 5: Implement exact detail materialization and validation**

Read canonical row, current fact provenance, Annals head/history, historical coordinates, and sensitivity label under the same transaction/connection. Validate labels with `DerivedArtifactContentDigest.ForBytes` over the exact format-2 snapshot bytes. Return explicit no-head/no-label arms for legitimate absence.

- [ ] **Step 6: Prove GREEN and mutation sensitivity**

Run the focused cluster. Temporarily remove the active predicate from LIKE fallback and require its retired-content test to fail; restore. Temporarily accept a mismatched label digest and require the corruption-negative test to fail; restore.

- [ ] **Step 7: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Core/Lexicon/ILexiconService.cs src/RetroDownfall.Arcanum.Core/Lexicon/ILexiconCurationService.cs src/RetroDownfall.Arcanum.Core/Lexicon/LexiconEntryDto.cs src/RetroDownfall.Arcanum.Core/Memory/MemoryDtos.cs src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.CurationInspection.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Support/FakeLexiconService.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCurationInspectionTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCampaignScopeTests.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/SystemPromptBuilderTests.cs tests/RetroDownfall.Arcanum.Tests/Daemons/UnseenServantDaemonJobTests.cs
git diff --cached --name-only
git commit -m "feat: separate Lexicon inspection from retrieval"
```

---

### Task 5: Implement Atomic Exact-Target Correction and Label Replacement

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Correction.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs`
- Optionally create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.LexiconReplacement.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/SessionDerivedArtifactStore.cs` only if sharing an existing authorization helper requires it
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCorrectionTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCorrectionConcurrencyTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/ArtifactSensitivityLedgerTests.cs`

**Result:** Correction replaces the complete canonical type/fact set and every dependent projection atomically, using the inspected target as a strict compare-and-swap token and preserving protected-artifact evidence.

- [ ] **Step 1: Write exact-target and no-op RED tests**

Prove a valid target can replace type plus complete ordered facts, increments generation exactly once, updates `UpdatedAt`, republishes FTS, preserves unchanged-fact current provenance by exact canonical value, removes current provenance for removed facts, and gives new operator facts no attachment source. Prove canonical-equal replacement returns `Unchanged` with no timestamp/generation/Annals/label/provenance write. Mutate each target field independently and require stale conflict before desired-state logic. Require correction of retired content to return conflict.

- [ ] **Step 2: Run correction tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconCorrectionTests|FullyQualifiedName~LexiconCorrectionConcurrencyTests"
```

Expected RED: only merge-style upsert exists; there is no exact-target replacement.

- [ ] **Step 3: Implement transaction skeleton and strict target comparison**

Normalize replacement before opening `BEGIN IMMEDIATE`. Inside the transaction, re-read exact scope/name and all evidence, validate internal consistency, compare the complete target, refuse generation `long.MaxValue`, then evaluate retired/no-op state. Do not call public upsert from correction.

- [ ] **Step 4: Write baseline/history/provenance RED tests**

For claimless rows, require format-2 `Assert` baseline with `AgentAsserted`, `ValidFrom = row.UpdatedAt`, then operator `Correct`. For legacy-head rows, require a `SystemBackfilled` format-2 `Correct` restatement, then operator `Correct`. Repeat with ordinary Annals capture disabled and require the same curation evidence. Snapshot pre- and post-mutation historical coordinates by canonical ordinal without retaining facts. Assert exact revision/order/timestamps and no duplicate baseline for an existing format-2 head.

- [ ] **Step 5: Write protected-label replacement RED tests**

Require full old-label CAS, same stable artifact id, new label id, checked artifact revision `n + 1`, preserved/strengthened sensitivity and generation provenance, preserved owner/Session/Campaign/turn and producing evidence, new derived digest, unchanged `TaintedArtifactCount`, advanced Session projection revision, and monotonic maxima. Require typed artifact-revision overflow. Inject failure after canonical update, label delete, new label insert, provenance append, FTS publication, and Annals append; every durable surface must roll back.

- [ ] **Step 6: Implement the Lexicon-specific label replacement primitive and full publication**

Use `ArtifactReplacement` authorization. CAS-delete the complete old label and insert the successor in the same transaction/connection. Do not weaken the ordinary append-only ledger write contract. Revalidate a provided exact-scope write lease immediately before commit.

- [ ] **Step 7: Prove concurrency GREEN and mutation sensitivity**

Use `TaskCompletionSource` barriers and bounded waits, never sleeps, to race scribe/correction and two corrections. Exactly one predecessor target may commit; the loser is stale and no partial evidence remains. Temporarily omit generation comparison and require the race/stale test to fail; restore. Temporarily increment the Session taint count during replacement and require the accounting test to fail; restore.

- [ ] **Step 8: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Correction.cs src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.LexiconReplacement.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCorrectionTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCorrectionConcurrencyTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/ArtifactSensitivityLedgerTests.cs
git diff --cached --name-only
git commit -m "feat: correct Lexicon entries atomically"
```

---

### Task 6: Add Retirement, Reinstatement, and Safe Scribe Behavior

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Lifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconLifecycleTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconLifecycleConcurrencyTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/ArcanumInternalToolServerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs`

**Result:** Retirement and reinstatement are exact-target lifecycle writes with immutable Annals evidence; scribe cannot revive retired rows or alter labeled content; hard deletion can still find a retired protected row.

- [ ] **Step 1: Write lifecycle RED tests**

Require retire to stamp canonical UTC, increment generation, preserve `UpdatedAt`, remove FTS visibility, preserve facts/current provenance/pin/label, and append operator `Retire` tombstone after any required baseline. The tombstone has a persisted hash-format code but no content digest and no fact-provenance rows. Require reinstate to clear retirement, increment generation, preserve `UpdatedAt`, restore FTS, append operator format-2 `Correct` restatement, and snapshot current provenance by canonical ordinal. Repeat retirement and reinstatement with ordinary Annals capture disabled and require identical curation evidence. Cover `AlreadyRetired` and `NotRetired` only with an exact current target; predecessor targets are stale.

- [ ] **Step 2: Run lifecycle tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconLifecycleTests|FullyQualifiedName~LexiconLifecycleConcurrencyTests"
```

- [ ] **Step 3: Implement retire/reinstate inside the shared CAS transaction boundary**

Reuse exact validation and baseline helpers from correction. Do not add a new Annals operation for reinstatement. Require write authority for protected targets and revalidate before commit.

- [ ] **Step 4: Write scribe and hard-delete RED tests**

At exact scope, `scribe_lexicon` must refuse a retired row without inserting a duplicate or falling through to Global. A labeled active no-op may succeed; a content-changing labeled scribe fails closed. A real scribe create/merge uses format-2 Annals, snapshots the resulting current provenance by canonical ordinal after the current projection is replaced, increments generation and `UpdatedAt`, and rolls back the row if evidence publication fails. A semantic no-op changes neither and appends no version. Existing hard DELETE and `delete_lexicon` must locate retired labeled rows for conditional purge and erase them. Non-Lexicon Annals writers remain format 1.

- [ ] **Step 5: Implement scribe guards and inspection-only hard-delete lookup**

Perform the exact retired/labeled check in the same immediate transaction as upsert. Replace the existing legacy-digest scribe append with the shared format-2 snapshot and transaction-bound version/provenance publication; current provenance must be updated before snapshotting the resulting version. Keep ordinary model retrieval active-only. Give deletion an explicitly named exact inspection path rather than weakening operational lookup.

- [ ] **Step 6: Prove GREEN and mutation sensitivity**

Run lifecycle, Lexicon service, Campaign scope, MCP, Memory endpoint, and Annals write-through tests. Temporarily let scribe update retired content and require the dedicated test to fail; restore. Temporarily omit retirement from the FTS update trigger or query filter and require the lifecycle visibility test to fail; restore.

- [ ] **Step 7: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Lifecycle.cs src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconLifecycleTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconLifecycleConcurrencyTests.cs tests/RetroDownfall.Arcanum.Tests/Mcp/ArcanumInternalToolServerTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs
git diff --cached --name-only
git commit -m "feat: govern Lexicon retirement lifecycle"
```

---

### Task 7: Add Pinning and Make Retention Respect the Apply-Time Race

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Lifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconPinTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionPlanningAcceptanceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionApplyBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionInventoryAccuracyTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/DataRetentionCommandTests.cs`

**Result:** Pin/unpin are generation-bearing exact-target state changes, dry-run explains pin exclusions, and a pin acquired after planning wins during apply with a distinct result.

- [ ] **Step 1: Write pin/unpin RED tests**

Require pin to stamp canonical UTC and increment generation; unpin clears it and increments generation. Both preserve `UpdatedAt`, content, FTS, Annals revision, and sensitivity evidence. Require `AlreadyPinned`/`NotPinned` only for exact current targets and generation overflow refusal.

- [ ] **Step 2: Run pin tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconPinTests"
```

- [ ] **Step 3: Implement pin/unpin through the shared target validator**

Do not append an Annals version or update `UpdatedAt`. Pinned rows remain correctable, retireable, reinstateable, explicitly deletable, and scribe-updatable when active/unprotected.

- [ ] **Step 4: Write retention inventory and race RED tests**

Add `DataRetentionLexiconCurationInventory(PinnedRows, PinnedRowsExemptFromPlan)` to plan/result contracts, mirroring the existing Saga curation inventory. Seed old pinned and unpinned entries; assert candidate count, total pinned inventory, and number the plan would otherwise prune. With a barrier between plan and delete, pin a candidate and require apply to preserve it and append `DataRetentionConflict(ErrorCodes.Data.PinnedAfterPlanning, candidateId, ...)` to the public `DataRetentionApplyResult.Conflicts`, distinct from missing/already-pruned/`Data.PlanChanged`. Prove the API JSON and CLI plain/JSON renderings preserve that exact code.

- [ ] **Step 5: Implement planning exclusion and apply-time guard**

Count pins independently, calculate would-prune exemption under the same planning bounds, add `PinnedAtUtc IS NULL` to the exact delete predicate, and make the private candidate-delete result distinguish `PinnedAfterPlanning` from a generic preservation. Translate it into the public `DataRetentionConflict` code without exposing mutable row content. Keep explicit operator hard deletion unchanged.

- [ ] **Step 6: Prove GREEN and mutation sensitivity**

Run pin plus retention planning/apply/inventory/endpoint/CLI serialization tests. Temporarily remove the apply-time `PinnedAtUtc IS NULL` predicate and require the race test to fail; restore. Temporarily omit the exempted count and require the dry-run inventory assertion to fail; restore.

- [ ] **Step 7: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Lifecycle.cs src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconPinTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionPlanningAcceptanceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionApplyBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionInventoryAccuracyTests.cs
git add tests/RetroDownfall.Arcanum.Tests/Api/DataRetentionEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/DataRetentionCommandTests.cs
git diff --cached --name-only
git commit -m "feat: protect pinned Lexicon memory from pruning"
```

---

### Task 8: Enforce Covenant Authority on Every Lexicon Inspection and Mutation

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.CurationInspection.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Correction.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Lifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/LexiconProtectedInspectionTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCurationAuthorityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantProtectedResultTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantSensitivePurgeRouteInventoryTests.cs`

**Result:** Every Lexicon content surface selects, validates, retains, and disposes the correct generation-bound authority, and protected content never crosses an ordinary response boundary.

- [ ] **Step 1: Write lease-selection and same-snapshot RED tests**

At the service boundary, require exact Global/Campaign show to receive the matching scoped read lease before content read and effective Campaign inspection to receive an installation lease. Through existing mapped endpoints, require effective Campaign GET, list, generic memory search, sources, explain, and arbitrary-scope projections to acquire an installation lease. Under the same deferred transaction, verify every returned label against the selected row and propagate `ContainsProtectedContent`. Feature-absent and unlabeled paths retain current successful behavior.

- [ ] **Step 2: Write serialization-lifetime RED tests**

Use a blocking response body and gate observer on the existing effective/list/search/sources/explain endpoints to prove protected read leases remain live until the final JSON byte, are revalidated immediately before serialization, and dispose once on success, mapped error, cancellation, and serialization failure. Assert `Cache-Control: no-store, private`, `Pragma: no-cache`, `Expires: 0`, no ETag/Last-Modified, and conditional-read metadata. Unlabeled responses must not inherit protected cache headers merely because the feature exists. The six new curation routes receive their equivalent read/write serialization tests in Task 9 after those routes exist.

- [ ] **Step 3: Run the protection cluster and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconProtectedInspectionTests|FullyQualifiedName~LexiconCurationAuthorityTests|FullyQualifiedName~CovenantProtectedResultTests"
```

- [ ] **Step 4: Implement conditional admission and ownership transfer**

For existing inspection endpoints, acquire through `ICovenantExportPolicy.AcquireConditionalReadAsync` before any content read. Transfer a lease to `CovenantProtectedJsonResult<T>` only when the verified service result reports protected content; otherwise dispose before the ordinary result. At the service boundary, require and revalidate a provided exact-scope `CovenantWriteLease` for labeled mutation and reject mismatched authority. If a submitted absent arm races with a new live label, return stale/integrity failure without mutation. Task 9 makes the new route handlers acquire and retain those write leases.

- [ ] **Step 5: Audit and protect legacy read surfaces**

Update existing list/effective show/search/sources/explain handlers, not just the six new routes. Preserve hard-delete conditional sensitivity-purge metadata and ensure its retired-row guard remains label-aware. Add an exhaustive route/source inventory so a later unprotected Lexicon projection is a test failure.

- [ ] **Step 6: Prove GREEN and mutation sensitivity**

Run all protection tests plus existing export policy, gate, protected-result, purge, and memory endpoint suites. Temporarily release an existing protected-read lease before serialization and require the blocked-serialization test to fail; restore. Temporarily skip label-content digest verification and require a corruption-negative test to fail; restore.

- [ ] **Step 7: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.CurationInspection.cs src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Correction.cs src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Lifecycle.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs tests/RetroDownfall.Arcanum.Tests/Api/LexiconProtectedInspectionTests.cs tests/RetroDownfall.Arcanum.Tests/Lexicon/LexiconCurationAuthorityTests.cs tests/RetroDownfall.Arcanum.Tests/Api/CovenantProtectedResultTests.cs tests/RetroDownfall.Arcanum.Tests/Api/CovenantSensitivePurgeRouteInventoryTests.cs
git diff --cached --name-only
git commit -m "feat: protect Lexicon curation authority"
```

---

### Task 9: Publish the Exact HTTP Curation Contract

**Files:**

- Create: `src/RetroDownfall.Arcanum.Api/Tower/LexiconCurationEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/Tower/LexiconCurationEndpointTests.cs`
- Create or modify: `tests/RetroDownfall.Arcanum.Tests/Api/LexiconCurationRouteInventoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/ApiSurfaceContractTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs`

**Result:** Six authenticated static POST routes expose exact show and mutation envelopes with stable endpoint names, source-generated JSON, explicit scope/evidence arms, and correct status semantics.

**Routes and types:**

```text
POST /api/memory/lexicon/show       LexiconShowRequest(Name, Scope)
POST /api/memory/lexicon/correct    LexiconCorrectRequest(Target, Content)
POST /api/memory/lexicon/retire     LexiconRetireRequest(Target)
POST /api/memory/lexicon/reinstate  LexiconReinstateRequest(Target)
POST /api/memory/lexicon/pin        LexiconPinRequest(Target)
POST /api/memory/lexicon/unpin      LexiconUnpinRequest(Target)
```

Endpoint names are `ShowLexiconEntry`, `CorrectLexiconEntry`, `RetireLexiconEntry`, `ReinstateLexiconEntry`, `PinLexiconEntry`, and `UnpinLexiconEntry`.

- [ ] **Step 1: Write mapped-route RED tests**

Enter through `ArcanumWebApplicationFactory` with authentication enabled. Test exact Global/Campaign show with no fallback; full target/detail round-trip; all six outcomes; missing subject 404; invalid scope/name/content and malformed/wrong-media JSON 400 envelopes; omitted versus explicit-false evidence discriminators; stale/retired correction 409; mapped authority/integrity failures; and feature gates both present/absent. Prove the static route wins over the existing catch-all and existing GET effective show/DELETE behavior stays compatible. With blocking response bodies, prove exact show acquires the matching conditional scoped read and every labeled mutation acquires the exact scoped write before service invocation; both transfer sole lease ownership to `CovenantProtectedJsonResult<T>`, revalidate immediately before final serialization, retain through the last byte, and dispose once on success/error/cancellation/serialization failure. Unlabeled responses use ordinary results without protected cache headers.

- [ ] **Step 2: Run endpoint tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconCurationEndpointTests|FullyQualifiedName~LexiconCurationRouteInventoryTests|FullyQualifiedName~ApiSurfaceContractTests|FullyQualifiedName~ArcanumJsonContextCompletenessTests"
```

- [ ] **Step 3: Implement routes with explicit body parsing and results**

Use `ApiRequestJson.ReadAsync`, never implicit Minimal API body binding. Validate nested presence arms before service invocation. For exact show, call `ICovenantExportPolicy.AcquireConditionalReadAsync` with the exact scope before service read. For a mutation target with a present label arm, call `ICovenantOperationGate.AcquireWriteAsync` with that exact owner scope before service mutation; an absent target arm gets no speculative write authority and a raced-in label is a stale/integrity refusal. Convert only at the endpoint via `ApiResponse<T>.FromResult`, explicit `ArcanumJsonContext` type info, and `ArcanumErrorMapper`. Transfer protected response ownership through `CovenantProtectedJsonResult<T>`; never manually reproduce its headers or release the lease early. Attach `.RequireConditionalCovenantReadAuthority()` and exact write metadata required by the existing security inventory; metadata does not replace actual lease acquisition.

- [ ] **Step 4: Register every wire type and close error mapping**

Register all request types, scope/lifecycle/outcome enums, target/evidence/detail/history/provenance DTOs, retention inventory, and `ApiResponse<LexiconEntryDetail>` / `ApiResponse<LexiconCurationResult>`. Map invalid target/scope/content to 400, missing exact subject to 404, stale target and retired correction to 409, and existing authority/integrity codes to their established fail-closed statuses.

- [ ] **Step 5: Prove GREEN and route mutation sensitivity**

Run endpoint, JSON completeness, API surface, memory endpoint, auth, and protected inspection tests. Temporarily remove one source-generation registration and require completeness/AOT contract tests to fail; restore. Temporarily make show use effective lookup and require the no-fallback test to fail; restore.

- [ ] **Step 6: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Api/Tower/LexiconCurationEndpoints.cs src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/LexiconCurationEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/LexiconCurationRouteInventoryTests.cs tests/RetroDownfall.Arcanum.Tests/Api/ApiSurfaceContractTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Serialization/ArcanumJsonContextCompletenessTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs
git diff --cached --name-only
git commit -m "feat: expose exact Lexicon curation endpoints"
```

---

### Task 10: Add the Thin CLI Workflow and Generated Command Contract

**Files:**

- Create: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.LexiconCuration.cs`
- Create: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.LexiconCuration.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/AuthoredContentReader.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs`
- Modify: `docs/Arcanum.CommandMap.json`
- Create: `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientLexiconCurationTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Cli/MemoryLexiconCurationCommandTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/MemoryCommandTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/CliJsonContextCoverageTests.cs`

**Result:** The registered `memory lexicon` tree performs exact show and show-confirm-submit mutations over HTTP, supports correction JSON from path or stdin, preserves direct-command options, and emits one machine-readable result.

- [ ] **Step 1: Write HTTP client RED tests**

Require each method to POST the exact static path, serialize the full source-generated request, decode the correct envelope, propagate cancellation/network errors, and never call the legacy effective GET for curation.

- [ ] **Step 2: Write registered-command RED tests**

Invoke the real tree for:

```text
memory lexicon show <name> [--campaign <id>]
memory lexicon correct <name> --file <path|-> [--campaign <id>]
memory lexicon retire <name> [--campaign <id>]
memory lexicon reinstate <name> [--campaign <id>]
memory lexicon pin <name> [--campaign <id>]
memory lexicon unpin <name> [--campaign <id>]
```

Assert Global default and exact Campaign scope, show-before-confirm-before-write ordering, cancellation sends no mutation, `--yes` skips only prompting, the target is forwarded byte-for-byte, malformed/missing correction JSON fails before confirmation, and `--file -` consumes stdin. Because reading redirected stdin to EOF leaves no confirmation channel, require `--yes` with `--file -`; without it return exit code 2 before exact show and send no mutation. Assert `--json` writes exactly one stdout document, diagnostics use stderr, and exit codes remain 0/1/2/3/130.

- [ ] **Step 3: Run client/command tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~ArcanumApiClientLexiconCurationTests|FullyQualifiedName~MemoryLexiconCurationCommandTests|FullyQualifiedName~MemoryCommandTests"
```

- [ ] **Step 4: Implement the client, handlers, and stdin-safe authored-content reader**

Extend the existing `lexicon` subgroup; do not register a duplicate group. Deserialize correction through `ArcanumJsonContext.Default.LexiconReplacementContent`. Teach `AuthoredContentReader` that the literal path `-` selects stdin, with focused regression coverage for its other consumers, and enforce the `--file -` plus `--yes` noninteractive rule before consuming input. Render measured target/effect before prompting without putting a preflight JSON document on stdout.

- [ ] **Step 5: Regenerate and verify the command map**

```bash
ARCANUM_UPDATE_COMMAND_MAP=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CliSurfaceTests.Committed_command_map_matches_the_live_tree"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CliSurfaceTests"
```

Review the generated diff; do not hand-edit it.

- [ ] **Step 6: Prove GREEN and mutation sensitivity**

Run all CLI tests plus API client serialization tests. Temporarily omit the preflight show and require the ordering test to fail; restore. Temporarily write the preflight detail to stdout under `--json` and require the one-document test to fail; restore.

- [ ] **Step 7: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.LexiconCuration.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.LexiconCuration.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs src/RetroDownfall.Arcanum.Cli/Commands/Tower/AuthoredContentReader.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs docs/Arcanum.CommandMap.json tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumApiClientLexiconCurationTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/MemoryLexiconCurationCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/MemoryCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/CliJsonContextCoverageTests.cs
git diff --cached --name-only
git commit -m "feat: add Lexicon curation commands"
```

---

### Task 11: Close Cross-Cutting Ownership, Documentation, and Architecture Inventories

**Files:**

- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `docs/Arcanum.Command.Reference.md`
- Modify: `docs/Arcanum.Engineering.md`
- Modify as identified: reset/backup/erasure/source-analysis tests that enumerate Lexicon or Annals objects
- Create or modify: `tests/RetroDownfall.Arcanum.Tests/Build/LexiconCurationArchitectureTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Build/DocumentationStructureTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Build/EfNativeAotBoundaryTests.cs` if its closed SQL inventory requires the new files
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCampaignScopedResetTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionWorkspaceResetTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/AnnalsSchemaInvariantTests.cs`

**Result:** Owning documents and closed inventories describe and enforce the shipped behavior; reset/erase/backup/AOT/source boundaries have no forgotten curation object or read path.

- [ ] **Step 1: Write/extend closed-inventory RED tests**

Cover every Lexicon SQL projection column/ordinal, operational-versus-inspection method boundary, schema object/transition/UTC inventory, reset/erasure ownership, endpoint name/security metadata, error-code mapping, JSON registration, retention plan nested inventory, and Native AOT/EF raw-SQL split. Add a source test that enumerates every model-facing Lexicon query and rejects one lacking active eligibility.

- [ ] **Step 2: Run architecture and ownership tests and inspect RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~LexiconCurationArchitectureTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~EfNativeAotBoundaryTests|FullyQualifiedName~AnnalsSchemaInvariantTests|FullyQualifiedName~DataRetentionCampaignScopedResetTests|FullyQualifiedName~DataRetentionWorkspaceResetTests"
```

- [ ] **Step 3: Repair every discovered inventory omission**

Fix close neighbors required for correctness, safety, reset ownership, or build/AOT health. Do not expand the slice into #100 resurrection suppression, #99 review workflow, or a generic memory-version framework.

- [ ] **Step 4: Update canonical documentation**

Document:

- DESIGN: schema v11, canonical format-2 grammar, exact target/CAS, operational versus inspection reads, transaction/authority/label replacement, pin-aware retention, reset ownership, and testing boundaries;
- API: all six POST request/response/status contracts, explicit evidence arms, exact versus effective lookup, protected headers;
- Command Reference: exact Global/Campaign behavior, correction file/stdin shape, confirmation, JSON/stdout and exit codes;
- Engineering: Lexicon lifecycle, Annals hash formats/provenance, retention pin accounting, and operator limitations.

Do not mention issue numbers as product behavior and do not modify root `README.md`.

- [ ] **Step 5: Run documentation and cross-cutting GREEN**

Run the focused architecture/ownership cluster, `git diff --check`, and any doc spelling/link checks exposed by the repository. Review all changed docs against the approved design and actual wire/CLI tests.

- [ ] **Step 6: Review and commit**

```bash
git diff --check
git status --short
git diff --name-only
git add docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Arcanum.Command.Reference.md docs/Arcanum.Engineering.md tests/RetroDownfall.Arcanum.Tests/Build/LexiconCurationArchitectureTests.cs tests/RetroDownfall.Arcanum.Tests/Build/DocumentationStructureTests.cs tests/RetroDownfall.Arcanum.Tests/Build/EfNativeAotBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCampaignScopedResetTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionWorkspaceResetTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/AnnalsSchemaInvariantTests.cs
git diff --cached --name-only
git commit -m "docs: specify immutable Lexicon curation"
```

---

### Task 12: Independent Review, Mutation Audit, and Feature-Branch Qualification

**Result:** The feature SHA is independently reviewed, all load-bearing tests are proven sensitive, the complete local matrix is green, and the branch is ready for integration without relying on historical results.

- [ ] **Step 1: Run an independent requirements review**

Give a fresh reviewer the approved spec, this plan, issue #98 body/comments, and `git diff 959729244cb611e1fd45a094987d9da6b33e6b58...HEAD`. Require a table mapping every requirement to code plus test evidence and explicit confirmation that #73/#78 remain out of closure scope. Resolve every blocker/high finding with a new RED/GREEN slice and commit.

- [ ] **Step 2: Run an independent code-quality/security review**

Focus on SQL parameterization, transaction and rollback boundaries, lease ownership/disposal, constant-time digest comparisons where applicable, overflows, strict UTF-8, source generation, raw-SQL ordinals, FTS trigger behavior, protected logging/cache headers, CLI secret/history exposure, and absence of superseded plaintext. Resolve every blocker/high and justified medium finding with tests and commit.

- [ ] **Step 3: Perform the required mutation audit**

One at a time, temporarily break exact-target generation comparison, lifecycle generation increment, format-2 length framing, Annals format comparison, label digest verification, active-row filtering, FTS lifecycle publication, and retention apply-time pin guard. Run the named focused test for each and record that it fails for the intended assertion. Restore every change and prove the focused test green. End with a clean `git status`.

- [ ] **Step 4: Run targeted aggregate tests**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Lexicon|FullyQualifiedName~Annal|FullyQualifiedName~DataRetention|FullyQualifiedName~CovenantProtected|FullyQualifiedName~CovenantExport|FullyQualifiedName~CliSurface|FullyQualifiedName~MemoryCommand|FullyQualifiedName~MemoryEndpoint"
```

- [ ] **Step 5: Run the complete feature-branch qualification**

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --disable-build-servers -m:1
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
./scripts/verify-shipping-publish.sh --rid osx-arm64
./scripts/verify-native-sqlcipher.sh --rid osx-arm64
git diff --check
git status --short
```

Every command must be fresh against the same `HEAD`; preserve logs and exact counts. If a command mutates tracked generated output, inspect and either commit an intentional change then restart qualification from the new SHA, or restore only the tool-generated unintended delta with a narrow patch.

- [ ] **Step 6: Freeze the reviewed feature SHA**

```bash
git rev-parse HEAD
git status --short
git log --oneline --decorate 959729244cb611e1fd45a094987d9da6b33e6b58..HEAD
```

Do not add a ceremonial empty commit. Record the exact reviewed/qualified SHA for integration.

---

### Task 13: Merge to `memory-enhancement`, Requalify the Merge SHA, and Close #98

**Result:** The qualified feature is merged with preserved topology into `memory-enhancement`, the exact merge SHA is green and pushed, issue #98 is closed with evidence, and parent epics remain open.

- [ ] **Step 1: Refresh remote and verify integration ancestry**

```bash
git fetch origin --prune
git rev-parse origin/main
git rev-parse memory-enhancement
git config --get branch.memory-enhancement.merge
git show-ref --verify --quiet refs/remotes/origin/memory-enhancement
git status --short
```

The expected pre-delivery state is local `memory-enhancement == origin/main`, upstream `refs/heads/main`, and no remote `origin/memory-enhancement` yet. If a remote integration branch now exists, inspect it and stop on divergence rather than overwriting it. Stop if remote movement changes the integration assumptions. Never transplant unrelated primary-checkout work.

- [ ] **Step 2: Merge the feature with explicit topology**

Switch this isolated worktree to `memory-enhancement`, fast-forward it to `origin/main`, then:

```bash
git switch memory-enhancement
git merge --ff-only origin/main
git merge --no-ff codex/issue-98-lexicon-version-operations -m "merge: complete Lexicon version operations"
```

Resolve conflicts by preserving both current integration behavior and the tested feature contract; any semantic conflict requires focused tests and a fresh review.

- [ ] **Step 3: Requalify the exact merge SHA**

Repeat the complete Task 12 Step 5 matrix against the merge commit. Because the merge SHA differs from the feature SHA, historical green output is supporting evidence only. Record the exact merge SHA and all counts.

- [ ] **Step 4: Push only the qualified integration branch**

```bash
git push --set-upstream origin memory-enhancement:memory-enhancement
git ls-remote --heads origin memory-enhancement
```

Verify the remote object equals the locally qualified merge SHA. Do not push a failed or superseded SHA.

- [ ] **Step 5: Close issue #98 with delivered evidence**

Post a concise comment naming the delivered merge SHA, TDD slices, independent reviews, and final qualification commands/counts, then close #98. Re-read #98 and verify `state=CLOSED`. Re-read #73 and #78 and verify both remain open.

- [ ] **Step 6: Final repository and artifact check**

Confirm the isolated worktree is clean, `memory-enhancement` tracks the pushed SHA, the primary dirty checkout remains unchanged, and no temporary mutation/review file is tracked. Keep the attached worktree available unless the user asks to archive it or it is clearly no longer needed for follow-up.
