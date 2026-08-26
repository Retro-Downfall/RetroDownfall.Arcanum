# Covenant Exact-Version Curation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator correct a Covenant entry by naming the exact version, branch, revision and compiled hash it replaces; pin, unpin, mask and unmask scoped lane heads through the same prepare-and-apply protocol; and make agent-initiated retirement reachable for the first time.

**Architecture:** Correction is an `OperatorSet` carrying a stricter binding, so no shipped `CHECK` has to be rebuilt. Pin, unpin, mask and unmask are a separate protocol over three append-only tables added in Covenant canonical schema version 2. The agent half wires the already-built retirement contracts — preflight, Ward policy, egress guard, capability — to the turn loop that has never reached them.

**Tech Stack:** .NET 10, Native AOT, xUnit, raw SQL over SQLCipher through the declarative schema tree, System.CommandLine, ASP.NET Core minimal APIs.

**Spec:** `docs/superpowers/specs/2026-08-26-issue-96-covenant-version-operations-design.md`

## Global Constraints

- **No new `CovenantOperation` member and no new `CovenantMutationKind` member.** `covenant_versions.OperationCode`, `covenant_heads.CurrentOperationCode`, and `covenant_mutation_receipts.MutationKindCode` bake their vocabularies into `CHECK` constraints SQLite cannot alter.
- **Covenant canonical version-1 source pin:** `7F906C4C832FDF824EC3B6A56431E9E6098DC9BB83EDA5BAE02EC62CE3B4E105`. Read before any object file was added; nothing can recompute it.
- **The version-2 step adds objects only.** No `ALTER TABLE`. Every transition file carries its head file's statement character for character — generate transitions by extracting from the head files, never by retyping.
- Raw SQL only, one object per file, `CREATE ... IF NOT EXISTS`, under `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/`.
- Native AOT: no reflection-based serialization, no dynamic type loading. Every wire enum carries `StringOnlyJsonStringEnumConverter`.
- Every new public `*Request`/`*Dto` in `RetroDownfall.Arcanum.Core.Covenant` **must** be declared in `CovenantPublicContractInventory` and registered with `ArcanumJsonContext`, or the architecture suite fails in both directions.
- Every new request record owns a `Validate()` returning the typed refusal, built from `CovenantWireValidation` helpers.
- Docs in `docs/` name capabilities, never issues. One logical block is one physical line — no hard wrapping.
- Zero warnings. The zero-warning gate needs `--no-incremental`; an incremental build hides analyzer warnings.
- After any schema step, enum member, or configuration key: run the **full** Arcanum suite **and** the Compendium suite. Targeted `--filter` runs say nothing about closed inventories.

---

### Task 1: Curation vocabulary and digests

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnums.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigestModels.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigests.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDomainTag.cs` (find its actual file with `grep -rn "enum CovenantDomainTag" src/`)
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCurationDigestTests.cs`

**Interfaces:**
- Produces: `CovenantCurationKind` (`Pin = 1, Unpin = 2, Mask = 3, Unmask = 4`); `CovenantCurationSubject(CovenantScope Scope, Guid? CampaignId, string NormalizedKey, CovenantLane Lane, long KeyEpoch)`; `CurationRequestDigestInput`; `CovenantDigests.CurationRequest(CurationRequestDigestInput)`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Curation_request_digest_changes_with_every_field()
{
    CurationRequestDigestInput baseline = new(
        CovenantCurationKind.Pin,
        Guid.Parse("00000000-0000-0000-0000-0000000000AA"),
        CovenantScope.Campaign,
        Guid.Parse("00000000-0000-0000-0000-0000000000BB"),
        new CovenantKey("preference.builds"),
        CovenantLane.Confirmed,
        KeyEpoch: 3,
        ExpectedRevision: 1);

    CovenantDigest reference = CovenantDigests.CurationRequest(baseline);

    Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { Kind = CovenantCurationKind.Unpin }));
    Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { KeyEpoch = 4 }));
    Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { ExpectedRevision = 2 }));
    Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { Lane = CovenantLane.Proposed }));
    Assert.Equal(reference, CovenantDigests.CurationRequest(baseline));
}

[Fact]
public void Curation_request_digest_is_domain_separated_from_a_mutation_request()
{
    // A curation digest and a mutation digest over the same subject must not collide, or a token
    // issued for one could authorize the other.
    CovenantDigest curation = CovenantDigests.CurationRequest(new CurationRequestDigestInput(
        CovenantCurationKind.Pin,
        Guid.Parse("00000000-0000-0000-0000-0000000000AA"),
        CovenantScope.Global,
        null,
        new CovenantKey("preference.builds"),
        CovenantLane.Confirmed,
        0,
        0));

    Assert.NotEqual(CovenantDomainTag.Request, CovenantDomainTag.CurationRequest);
    Assert.Equal(32, curation.Bytes.Length);
}
```

- [ ] **Step 2: Run it and confirm it fails to compile on the missing members**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCurationDigestTests"`
Expected: compile error naming `CovenantCurationKind`, `CurationRequestDigestInput`, `CovenantDomainTag.CurationRequest`.

- [ ] **Step 3: Add the vocabulary**

Add to `CovenantEnums.cs`, with the `StringOnlyJsonStringEnumConverter` every wire enum carries:

```csharp
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantCurationKind>))]
public enum CovenantCurationKind : byte
{
    Pin = 1,
    Unpin = 2,
    Mask = 3,
    Unmask = 4
}
```

Add a new `CovenantDomainTag.CurationRequest` member with a fresh, unused tag value, and `CovenantDigests.CurationRequest` following the exact shape of `MutationRequest`: validate, then `Hash(CovenantDomainTag.CurationRequest, writer => …)` writing kind, mutation id, scope, optional campaign, key, lane, key epoch, expected revision in that fixed order.

- [ ] **Step 4: Run the test and confirm it passes**

- [ ] **Step 5: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Covenant tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCurationDigestTests.cs
git commit -m "feat(covenant): give curation its own domain-separated request digest"
```

---

### Task 2: Covenant canonical schema version 2

**Files:**
- Create: `.../Capabilities/Covenant/Canonical/Tables/covenant_curation_versions.sql`
- Create: `.../Capabilities/Covenant/Canonical/Tables/covenant_curation_heads.sql`
- Create: `.../Capabilities/Covenant/Canonical/Tables/covenant_curation_receipts.sql`
- Create: `.../Capabilities/Covenant/Canonical/Triggers/covenant_curation_versions_guard_delete.sql`
- Create: `.../Capabilities/Covenant/Canonical/Triggers/covenant_curation_versions_guard_update.sql`
- Create: `.../Capabilities/Covenant/Canonical/Triggers/covenant_curation_receipts_guard_delete.sql`
- Create: `.../Capabilities/Covenant/Canonical/Triggers/covenant_curation_receipts_guard_update.sql`
- Create: `.../Capabilities/Covenant/Canonical/Transitions/V2/010_covenant_curation_versions.sql` and one file per object, in install order
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/.../CovenantCanonicalSchemaVersionOneFixture.cs`
- Test: existing `GrimoireSchemaTransitionResourceTests`, `GrimoireSchemaVersionChainTests`

**Interfaces:**
- Produces: three installed tables; `GrimoireSchemaVersionChains.CovenantCanonicalSchemaVersion = 2`; `SourcePins[(CovenantCanonical, 2)]`.

- [ ] **Step 1: Write the failing pin test first, and watch it be vacuously green**

The pin test must go red before it goes green, or it has proved nothing. Write `CovenantCanonicalSchemaVersionOneFixture` to reconstruct version 1 by **removing** the curation objects from `GrimoireSchemaCatalog.CovenantCanonicalObjects`, and a test asserting its fingerprint equals the pin. Run it now, before any `.sql` exists: the filter removes nothing, so it passes vacuously.

- [ ] **Step 2: Write the table SQL**

`covenant_curation_versions.sql`:

```sql
-- The append-only record of what an operator curated, and when. Immutable by trigger rather than by
-- convention: a curation state that could be edited in place would make "this entry was pinned when
-- the agent tried to write it" a claim nobody could check afterwards.
CREATE TABLE IF NOT EXISTS covenant_curation_versions (
    CurationVersionId TEXT NOT NULL PRIMARY KEY,
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    NormalizedKey TEXT NOT NULL CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch >= 0),
    CurationKindCode INTEGER NOT NULL CHECK (CurationKindCode IN (1, 2, 3, 4)),
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    PredecessorVersionId TEXT NULL REFERENCES covenant_curation_versions(CurationVersionId),
    MutationId TEXT NOT NULL,
    RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
    AuthorizationDigest BLOB NOT NULL CHECK (length(AuthorizationDigest) = 32),
    FinalMutationDigest BLOB NOT NULL CHECK (length(FinalMutationDigest) = 32),
    CreatedAtUtc TEXT NOT NULL,
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
    -- A mask names a Campaign and the Confirmed lane and nothing else. A Global mask has no broader
    -- scope to fall back from, and the Proposed lane is review-only beside effective Confirmed
    -- content, so masking it would change nothing an operator could observe.
    CHECK (CurationKindCode NOT IN (3, 4) OR (ScopeCode = 2 AND LaneCode = 1)),
    -- Revision one opens a subject's chain and has no predecessor; every later revision links to one.
    CHECK ((Revision = 1 AND PredecessorVersionId IS NULL) OR (Revision > 1 AND PredecessorVersionId IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_global_subject_revision
    ON covenant_curation_versions(NormalizedKey, LaneCode, KeyEpoch, Revision) WHERE CampaignId IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_campaign_subject_revision
    ON covenant_curation_versions(CampaignId, NormalizedKey, LaneCode, KeyEpoch, Revision) WHERE CampaignId IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_mutation
    ON covenant_curation_versions(MutationId);

CREATE INDEX IF NOT EXISTS idx_covenant_curation_versions_campaign_cleanup
    ON covenant_curation_versions(CampaignId);
```

`covenant_curation_heads.sql`:

```sql
-- The guarded current pointer for one curation subject. The subject deliberately does not require a
-- head in covenant_heads: masking a Global key inside a Campaign is exactly the case where that
-- Campaign holds no entry, no head, and no version for the key.
CREATE TABLE IF NOT EXISTS covenant_curation_heads (
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    NormalizedKey TEXT NOT NULL CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch >= 0),
    IsPinned INTEGER NOT NULL CHECK (IsPinned IN (0, 1)),
    IsMasked INTEGER NOT NULL CHECK (IsMasked IN (0, 1)),
    CurrentVersionId TEXT NOT NULL REFERENCES covenant_curation_versions(CurationVersionId),
    CurrentRevision INTEGER NOT NULL CHECK (CurrentRevision > 0),
    UpdatedAtUtc TEXT NOT NULL,
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
    CHECK (IsMasked = 0 OR (ScopeCode = 2 AND LaneCode = 1))
);

-- A NULL inside a SQLite primary key does not enforce uniqueness, so the subject's identity is two
-- partial unique indexes, exactly as covenant_entries keys its nullable Campaign.
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_heads_global_subject
    ON covenant_curation_heads(NormalizedKey, LaneCode, KeyEpoch) WHERE CampaignId IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_heads_campaign_subject
    ON covenant_curation_heads(CampaignId, NormalizedKey, LaneCode, KeyEpoch) WHERE CampaignId IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_heads_current_version
    ON covenant_curation_heads(CurrentVersionId);

-- The turn snapshot reads a Campaign's live masks by this index, so it must lead with CampaignId.
CREATE INDEX IF NOT EXISTS idx_covenant_curation_heads_campaign_masks
    ON covenant_curation_heads(CampaignId, IsMasked, NormalizedKey);
```

`covenant_curation_receipts.sql` mirrors `covenant_mutation_receipts.sql`: `MutationId` primary key, the three digests, `CurationKindCode`, the subject tuple, `OutcomeCode IN (1, 2)`, `ResultingVersionId`/`ResultingRevision` nullable under the same `Applied ⇒ both present, NoChange ⇒ both absent` `CHECK`, `ResponseReceiptDigest`, and `CommittedAtUtc`, plus `idx_covenant_curation_receipts_campaign_cleanup ON covenant_curation_receipts(CampaignId)`.

- [ ] **Step 3: Write the four guard triggers**

Copy the shape of `covenant_versions_guard_delete.sql` and `covenant_versions_guard_update.sql` verbatim, changing only the table and the message. History and receipts are append-only; nothing edits or removes a row.

- [ ] **Step 4: Generate the transition files from the head files**

Do **not** retype the SQL. Extract statements from each head file with a script that splits on lines ending in `;` (a semicolon mid-comment is common in this tree and a comment line never ends with one), and write one numbered file per object under `Transitions/V2/` in install order: tables before their indexes, tables before the triggers that fire on them.

- [ ] **Step 5: Declare the step**

In `GrimoireSchemaVersionChains.cs` set `CovenantCanonicalSchemaVersion = 2` and add the pin:

```csharp
// Read out of the Covenant canonical head tree immediately before the curation objects were added.
// Nothing can recompute it. CovenantCanonicalSchemaVersionOneFixture reconstructs that tree by
// removing those objects from the shipped list and a test hashes it, so a wrong value here fails
// there rather than against every operator's version-1 installation.
[(GrimoireSchemaTransactionTier.CovenantCanonical, 2)] =
    "7F906C4C832FDF824EC3B6A56431E9E6098DC9BB83EDA5BAE02EC62CE3B4E105",
```

No backfill: every object is new, so the step's DDL is correct with no sweep, and there is no pre-existing curation state to classify.

- [ ] **Step 6: Watch the pin test go red, then green for the right reason**

Run the fixture test again. It must now **fail** (the head moved), then pass once the fixture's removal list names the new objects. A pin test that is green before and after has proved nothing.

- [ ] **Step 7: Extend the two closed inventories**

`GrimoireSchemaTransitionResourceTests` pins every transition statement by name in install order — add the V2 statements. `GrimoireSchemaVersionChainTests` pins each tier's head version as a literal — bump the Covenant canonical literal to 2.

- [ ] **Step 8: Run the full suite, not a filter**

Run: `dotnet test RetroDownfall.Arcanum.slnx` and the Compendium suite separately. Closed inventories do not appear in targeted runs.

- [ ] **Step 9: Commit**

```bash
git commit -am "feat(covenant): install the curation substrate as canonical schema version 2"
```

---

### Task 3: The curation kernel

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCurationKernel.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationContracts.cs` (add `CovenantCurationIntent`, `CovenantCurationReceipt`)
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCurationKernelTests.cs`

**Interfaces:**
- Consumes: `CovenantCurationKind`, `CovenantDigests.CurationRequest` from Task 1; the three tables from Task 2.
- Produces: `CovenantCurationKernel.ApplyAsync(SqliteConnection, SqliteTransaction, CovenantCurationIntent, DateTimeOffset, CancellationToken) → ValueTask<Result<CovenantCurationReceipt>>`; `CovenantCurationReceipt(Guid MutationId, CovenantMutationOutcome Outcome, CovenantCurationKind Kind, CovenantCurationSubject Subject, Guid? ResultingVersionId, long? ResultingRevision, CovenantDigest RequestIdempotencyDigest, CovenantDigest FinalMutationDigest, CovenantDigest ResponseReceiptDigest, bool Replayed)`.

- [ ] **Step 1: Write the failing tests**

Name them for the behaviour, and drive every precondition through `ApplyAsync` rather than by writing rows:

- `Applying_a_pin_to_an_unpinned_subject_opens_revision_one`
- `Applying_the_same_pin_twice_replays_the_first_receipt_and_appends_no_version` — the second call returns `Replayed: true` and `covenant_curation_versions` still holds one row
- `Applying_a_pin_to_an_already_pinned_subject_reports_NoChange_and_still_writes_a_receipt` — a deliberate no-op is recorded, or a replay of it is indistinguishable from a mutation that never arrived
- `Applying_an_unpin_advances_the_revision_and_links_its_predecessor`
- `A_curation_commit_whose_expected_revision_disagrees_with_the_head_is_refused` — expect `ErrorCodes.Covenant.RevisionConflict`
- `A_mask_targeting_the_Global_scope_is_refused_before_it_reaches_the_table` — the contract refuses it, so the `CHECK` is a backstop rather than the first complaint
- `A_curation_row_recorded_under_an_earlier_key_epoch_does_not_bind_the_re-created_key`

- [ ] **Step 2: Run and confirm each fails for the reason expected**, not on a setup error.

- [ ] **Step 3: Implement `CovenantCurationKernel`**

Follow `CovenantMutationKernel` exactly: receipt-first replay resolution keyed on `MutationId` with an `IdempotencyConflict` when the same identity carries a different request digest; read the head under the caller's transaction; compare-and-swap on `CurrentRevision`; insert the version; upsert the head; insert the receipt. Everything runs on the caller's connection and transaction — the kernel opens neither.

- [ ] **Step 4: Run and confirm green.**

- [ ] **Step 5: Mutation-check before committing.** Delete the revision comparison and confirm `A_curation_commit_whose_expected_revision_disagrees…` fails. Make `ApplyAsync` always report `Applied` and confirm the `NoChange` test fails.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(covenant): apply one curation change against the revision it expects"
```

---

### Task 4: The curation read model

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStoreSql.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStore.cs` (find the concrete store with `grep -rn "class CovenantStore" src/`)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantReadContracts.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCurationReadTests.cs`

**Interfaces:**
- Produces: `CovenantCurationState(bool IsPinned, bool IsMasked, long Revision)`; `ICovenantStore.ReadCurationStateAsync(CovenantCurationSubject, ICovenantSnapshotReadLease, CancellationToken)`; `CovenantLaneDetail` gains `CurationState`; `CovenantEntrySummary` gains `IsPinned` and `IsMasked`.

- [ ] **Step 1: Write the failing test** — `Detail_reports_the_pin_an_operator_applied` and `List_reports_a_masked_Global_key_as_masked_in_the_masking_Campaign`, both reaching the store through the same read lease the routes use.

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Extend the projections** with a `LEFT JOIN covenant_curation_heads` on the subject tuple, defaulting a missing row to unpinned and unmasked. A subject with no curation row is the ordinary case and must cost no extra round trip.

- [ ] **Step 4: Run and confirm green.**

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(covenant): report pin and mask state beside every lane head"
```

---

### Task 5: The curation service and its port

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantServicePorts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationWireContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantPublicContractInventory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs` (find with `grep -rn "class ArcanumJsonContext" src/`)
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCurationServiceTests.cs`

**Interfaces:**
- Produces on `ICovenantMutationService`: `PrepareCurationAsync(CovenantCurationPrepareRequest, ICovenantSnapshotReadLease, CancellationToken) → ValueTask<Result<CovenantCurationPreflightDto>>` and `CurateAsync(CovenantCurationRequest, CovenantWriteLease, CancellationToken) → ValueTask<Result<CovenantCurationResultDto>>`.

```csharp
public sealed record CovenantCurationPrepareRequest(
    CovenantCurationKind Kind,
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    CovenantLane Lane,
    long ExpectedRevision,
    Guid MutationId)
{
    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.ValidateOperationScope(Scope, CampaignId),
            CovenantWireValidation.ValidateKey(Key),
            CovenantWireValidation.ValidateLane(Lane),
            CovenantWireValidation.ValidateCurationKind(Kind),
            CovenantWireValidation.ValidateMaskablePlacement(Kind, Scope, Lane),
            CovenantWireValidation.RequireNonNegative(ExpectedRevision, "expected Covenant curation revision"),
            CovenantWireValidation.RequireIdentity(MutationId, "client-generated mutation identity"));
}
```

`CovenantCurationRequest` is the same record plus `string PreflightToken`, validated with `ValidateToken` appended. `CovenantCurationPreflightDto` carries the subject, the kind, the live and expected revisions, the current pin and mask state, the **fallback sentence facts** (`bool GlobalConfirmedResurfaces`, `bool GlobalConfirmedSuppressed`, `long AffectedCampaignCount`), the issued and expiry instants, and the token. `CovenantCurationResultDto` mirrors `CovenantMutationResultDto`.

- [ ] **Step 1: Write the failing tests**

- `Preparing_a_mask_reports_that_the_Global_entry_will_stop_applying_and_nothing_replaces_it`
- `Preparing_a_Campaign_retirement_reports_that_the_Global_entry_resurfaces` (the existing effect, asserted from the curation preflight's sibling so both sentences come from one measurement)
- `A_curation_commit_carrying_a_token_prepared_for_a_different_subject_is_refused`
- `A_curation_commit_after_the_token_expired_still_replays_a_committed_receipt` — receipt-first, before the token is looked at
- `Preparing_a_mask_for_the_Global_scope_is_refused_by_Validate_before_any_read`

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Implement.** Reuse `CovenantOperatorPreflightBody` — a curation preflight carries no compiled artifact, so `CompiledArtifactDigest` is absent, which is already a stated position in that format. Reuse `PreflightLifetime`. Commit is receipt-first exactly as `ApplyAsync` is.

- [ ] **Step 4: Declare every new shape in `CovenantPublicContractInventory`** and register it with `ArcanumJsonContext`. The architecture suite fails in both directions, so an undeclared DTO and a declared-but-deleted type both stop the build.

- [ ] **Step 5: Run and confirm green, then commit**

```bash
git commit -am "feat(covenant): prepare and commit one curation change under the operator's authority"
```

---

### Task 6: The curation routes

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/CovenantMutationEndpoints.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/CovenantCurationEndpointTests.cs`

- [ ] **Step 1: Write the failing tests** — an unauthenticated call to each route is refused before a body byte is bound; an authorized prepare returns a token; the commit applies. Enter through the mapped route with the real app factory, never through the service.

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Map `POST /api/memory/covenant/curate/prepare` and `POST /api/memory/covenant/curate`**, both `.RequireCovenantOperatorAuthority(CovenantAuthorityRequirement.CovenantManage)`, reusing the file's existing `PrepareAsync`/`CommitAsync` helpers so lease handling is not written a second time.

- [ ] **Step 4: Run, confirm green, commit**

```bash
git commit -am "feat(covenant): route the curation prepare and commit pair"
```

---

### Task 7: The curation CLI verbs

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.Covenant.cs`
- Modify: `docs/Arcanum.CommandMap.json` (regenerated, never hand-edited)
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCurationCommandTests.cs`

- [ ] **Step 1: Write the failing tests**

- `Pin_prints_the_server_measurement_before_asking_for_confirmation`
- `Declining_the_confirmation_reaches_no_mutating_route` — assert on the fake API client's recorded calls
- `Mask_names_the_Campaign_and_states_that_nothing_replaces_the_Global_entry`
- `A_confirmation_is_refused_when_either_half_of_the_console_is_redirected`
- `Committed_command_map_matches_the_live_tree` (existing test; it will fail until the map is regenerated)

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Add `pin`, `unpin`, `mask` and `unmask` under `memory covenant`.** Each takes the key argument, `--campaign`, `--lane` where the verb reads one, and `--expected-revision`. `mask` and `unmask` require `--campaign`, because a Global mask is unrepresentable. Print the server's own preflight — never what the client believes — then confirm.

- [ ] **Step 4: Regenerate the command map**

Run: `ARCANUM_UPDATE_COMMAND_MAP=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter Committed_command_map_matches_the_live_tree`

- [ ] **Step 5: Run, confirm green, commit**

```bash
git commit -am "feat(covenant): give the operator pin, unpin, mask and unmask verbs"
```

---

### Task 8: The preflight token learns the exact target

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperatorPreflightBody.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigestModels.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantOperatorPreflightBodyTests.cs`

**Interfaces:**
- Produces: `CovenantOperatorPreflightBody` gains `Guid? TargetVersionId` and `CovenantDigest? TargetRenderedHash`; `FormatVersion = 2`; `EncodedBytes` grows by `1 + 16 + 1 + 32`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_body_carrying_a_target_round_trips_it()
{
    CovenantOperatorPreflightBody body = Sample() with
    {
        TargetVersionId = Guid.Parse("00000000-0000-0000-0000-0000000000CC"),
        TargetRenderedHash = new CovenantDigest(Enumerable.Repeat((byte)7, 32).ToArray()),
    };

    Result<CovenantOperatorPreflightBody> decoded =
        CovenantOperatorPreflightBody.TryDecode(body.Encode());

    Assert.True(decoded.IsSuccess);
    Assert.Equal(body.TargetVersionId, decoded.Value.TargetVersionId);
    Assert.Equal(body.TargetRenderedHash, decoded.Value.TargetRenderedHash);
}

[Fact]
public void A_version_one_body_is_refused_rather_than_read_as_a_targetless_version_two()
{
    byte[] encoded = Sample().Encode();
    encoded[0] = 1;

    // Refusing is the point. A build that read a shorter body as "no target" would silently drop the
    // binding a correction exists to carry.
    Assert.True(CovenantOperatorPreflightBody.TryDecode(encoded).IsFailure);
}

[Fact]
public void A_present_target_flag_over_an_absent_target_is_refused()
{
    // Every failure is the same content-free refusal: these bytes survived authenticated decryption,
    // so distinguishing them would report on material there is no reason to describe.
    byte[] encoded = Sample().Encode();
    encoded[^41] = 2;

    Assert.True(CovenantOperatorPreflightBody.TryDecode(encoded).IsFailure);
}
```

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Add the two fields** as presence-byte optionals at the end of the fixed-width layout, bump `FormatVersion` to 2, and add them to `PreflightBodyDigestInput` and `CovenantDigests.PreflightBody` in the same order. Leave `MutationRequestDigestInput` alone.

- [ ] **Step 4: Run, confirm green, commit**

```bash
git commit -am "feat(covenant): bind the exact target version into the preflight token"
```

---

### Task 9: Correction

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationWireContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantWireValidation.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantServicePorts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantPublicContractInventory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/CovenantMutationEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Covenant.cs`, `.../Commands/Tower/CovenantCommands.cs`, `.../Services/ArcanumApiClient.Covenant.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCorrectionTests.cs`

**Interfaces:**
- Produces: `CovenantCorrectPrepareRequest(CovenantScope, Guid?, string Key, string Content, Guid TargetVersionId, CovenantLane TargetLane, long ExpectedRevision, string TargetRenderedHash, Guid MutationId)`, `CovenantCorrectRequest` (the same plus `PreflightToken`), `ICovenantMutationService.PrepareCorrectAsync` and `CorrectAsync`.

- [ ] **Step 1: Write the failing tests, one per refusal in the spec's table**

Each one writes the entry with `set` first and then corrects it, so nothing asserted is seeded:

- `A_correction_naming_the_current_head_appends_a_version_and_the_next_turn_retrieves_the_corrected_text`
- `A_correction_naming_a_version_that_is_not_the_lane_head_is_refused_as_stale`
- `A_correction_naming_a_Proposed_version_is_refused_as_the_wrong_branch`
- `A_correction_whose_rendered_hash_disagrees_with_the_head_is_refused_as_a_guess`
- `A_correction_of_a_retired_head_is_refused_and_names_reactivation_instead`
- `A_correction_preserves_the_attachment_provenance_the_corrected_version_carried`
- `A_commit_naming_one_target_with_a_token_bound_to_another_is_refused` — the three-way equality
- `A_correction_records_its_predecessor_so_the_prior_text_stays_readable_in_history`

- [ ] **Step 2: Run and confirm each fails for its own reason.**

- [ ] **Step 3: Implement.** `PrepareCorrectAsync` compiles the new content, reads the effect and detail as `PrepareSetAsync` does, resolves the target from the detail, refuses on every condition in the table **before** issuing a token, and puts `TargetVersionId`/`TargetRenderedHash` into the body. `CorrectAsync` recomputes the request digest, decodes the body, and enforces request == body == live before delegating to the existing `Set` commit path with `CovenantMutationKind.OperatorSet`.

- [ ] **Step 4: Map `POST /api/memory/covenant/correct/prepare` and `POST /api/memory/covenant/correct`; add `memory covenant correct <key>`** taking content through `--file` or piped standard input and requiring `--target-version`, `--target-hash`, and `--expected-revision`. Regenerate the command map.

- [ ] **Step 5: Mutation-check.** Delete the rendered-hash comparison and confirm the guess test fails. Delete the lane comparison and confirm the wrong-branch test fails. Both must fail, and each on its own test.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(covenant): correct an entry by naming the exact version it replaces"
```

---

### Task 10: Masks reach the turn

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStoreSql.cs` (`TurnSnapshot()`)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantTurnSnapshot.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantLinker.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnums.cs` (`CovenantPlanDecision.Masked`)
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigestModels.cs` and `CovenantDigests.cs` (`SnapshotDigestInput` gains the mask vector)
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantMaskPlanningTests.cs`

- [ ] **Step 1: Write the failing tests**

- `A_masked_Global_key_reaches_no_turn_in_the_masking_Campaign`
- `A_masked_Global_key_still_reaches_every_other_Campaign`
- `A_Campaign_that_masks_a_key_and_then_sets_its_own_gets_its_own_value` — the ratified decision, and the one a wrong implementation silently breaks
- `A_masked_candidate_is_reported_as_Masked_rather_than_Shadowed`
- `Two_snapshots_with_identical_candidates_and_different_masks_do_not_share_a_digest`

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Implement.** Add a third `UNION ALL` arm to `TurnSnapshot()` projecting the evaluating Campaign's masked keys through `idx_covenant_curation_heads_campaign_masks`; carry them on `CovenantTurnSnapshot`; drop a masked Global Confirmed candidate in `BuildEffectiveConfirmedKeys` and report `Masked` in `Decide`; write the mask vector into `SnapshotDigestInput`.

- [ ] **Step 4: Mutation-check.** Remove the mask filter from the linker and confirm the first test fails. Remove the mask vector from the snapshot digest and confirm the digest test fails.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(covenant): stop a masked Global preference reaching the Campaign that masked it"
```

---

### Task 11: Pins refuse agent authorship

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantTurnHeadProbe.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantLaneHeadProbe.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantPinEnforcementTests.cs`

- [ ] **Step 1: Write the failing tests**

- `An_agent_proposal_against_a_pinned_Campaign_head_is_refused_by_the_write_authority`
- `An_approved_agent_retirement_of_a_pinned_head_is_refused`
- `The_operators_own_correction_of_a_pinned_head_succeeds` — a pin the operator has to fight is a pin they stop using
- `The_staging_head_probe_reports_the_pin_so_the_turn_keeps_its_answer` — the model gets a typed refusal instead of losing the reply
- `A_Global_pin_is_recorded_and_reported_but_binds_no_agent_path_that_exists` — assert the honest limit rather than implying enforcement with no production caller

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Implement.** In `ApplyIntentAsync`, read the curation head for the target subject in the same transaction and refuse an `AgentProposed` or `AgentApproved` origin against `IsPinned = 1`. Extend `CovenantLaneHeadProbe` with `IsPinned` so staging refuses early, and make the write authority the one that decides — the probe advises, the transaction enforces.

- [ ] **Step 4: Mutation-check.** Delete the origin check in the kernel and confirm both agent tests fail while the operator test still passes.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(covenant): refuse agent authorship of a pinned head"
```

---

### Task 12: Curation state joins the lifecycle

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStateInspector.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCleanupWorker.cs:262`
- Modify: the retention dry-run renderer (find with `grep -rn "dry-run\|DryRun" src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService*.cs`)
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantCurationLifecycleTests.cs`

- [ ] **Step 1: Write the failing tests**

- `Campaign_cleanup_removes_the_masks_that_Campaign_owned` — otherwise a mask outlives the Campaign it applied to
- `The_protected_state_inventory_counts_curation_rows`
- `The_retention_dry_run_states_that_a_pinned_entry_is_exempt`

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Implement.** Add the three tables to `CanonicalContentTables` — the retention inventory derives its list from it rather than restating it, so one edit covers both. Delete curation rows in the same transaction that deletes `covenant_heads` by Campaign, ordered so the head goes before the versions it references.

- [ ] **Step 4: Extend `CovenantArchitectureBoundaryTests`** if the cleanup worker or any new component now writes Covenant accelerator state or deletes a retained table. Those tests fail on the new file rather than on a wrong result.

- [ ] **Step 5: Run the full suite, commit**

```bash
git commit -am "feat(covenant): keep curation state inside backup, retention and Campaign cleanup"
```

---

### Task 13: Resolving a retirement target

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantRetirementPreflightResolver.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantServicePorts.cs` (new `ICovenantRetirementPreflightResolver`)
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantRetirementPreflightResolverTests.cs`

**Interfaces:**
- Produces: `ICovenantRetirementPreflightResolver.ResolveAsync(Guid campaignId, CovenantKey key, CovenantLane lane, CancellationToken) → ValueTask<Result<CovenantRetirementPreflight>>`.

- [ ] **Step 1: Write the failing tests**

- `Resolving_a_live_Campaign_Confirmed_head_reports_that_Global_content_starts_applying`
- `Resolving_a_head_with_no_Global_sibling_reports_no_fallback`
- `Resolving_a_key_with_no_head_in_that_lane_is_refused`
- `Resolving_a_pinned_head_is_refused_before_a_Ward_is_ever_raised` — the operator is not asked to approve something that cannot be applied
- `The_resolved_disclosure_is_the_compiled_fragment_and_never_the_raw_authored_text`

- [ ] **Step 2: Run and confirm failure.**

- [ ] **Step 3: Implement.** One bounded read under the caller's lease producing every field `CovenantRetirementPreflight`'s constructor requires, including the target-bound `PreflightBodyDigest` the staged tombstone carries as evidence.

- [ ] **Step 4: Run, confirm green, commit**

```bash
git commit -am "feat(covenant): resolve the exact retirement target outside the inference hot path"
```

---

### Task 14: The Ward, the disclosure, and the capability

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs` (`ExecuteToolCallWithWardAsync`, around line 1326)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/CovenantToolStagingAmbient.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/SessionAttachmentAmbientSend.cs` (`BindCovenantStaging`, around line 210)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpToolMerger.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantAgentRetirementTurnTests.cs`

- [ ] **Step 1: Write the failing tests, entering through a turn that emits `retire_covenant`**

- `A_turn_that_retires_a_Campaign_preference_shows_the_operator_the_content_about_to_disappear`
- `Declining_the_Ward_stages_nothing_and_the_entry_survives_the_turn`
- `Approving_the_Ward_stages_a_tombstone_that_publishes_with_the_turn_answer`
- `A_disclosure_receipt_is_committed_before_the_retirement_has_any_effect`
- `With_Wards_disabled_a_retirement_is_denied_rather_than_executed_unwarded`
- `A_proposal_the_turn_never_admitted_cannot_be_retired_under_configured_auto_approval` — it reaches the interactive Ward instead of self-approving
- `retire_covenant_is_advertised_only_once_a_capability_can_be_minted_for_it`

- [ ] **Step 2: Run and confirm failure.** Expect the refusal `Covenant.IneligibleTurn` today, which is the state this task removes.

- [ ] **Step 3: Implement.** In `ExecuteToolCallWithWardAsync`, when the tool is `retire_covenant`: classify with `CovenantToolClassifier`, resolve the preflight, resolve the authorization with `CovenantEgressWardPolicy.Resolve`, force interactive when the target was never admitted on this turn's plan, raise the Ward with the preflight's `SanitizedAuthoredDisclosure` as the disclosure, build the receipt with `CovenantEgressWardPolicy.Accept`, hand both to the staging ambient, and run the dispatch inside `CovenantToolEgressGuard.DiscloseThenAsync`. Extend `BindCovenantStaging` to mint a retirement capability when both are present. Remove `retire_covenant` from the withheld set only where a capability can now exist.

- [ ] **Step 4: Mutation-check.** Make `CovenantEgressWardPolicy.Resolve` return `SensitivePayloadOnly` for a retirement and confirm the Wards-disabled test fails. Move `DiscloseThenAsync` after the effect and confirm the ordering test fails.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(covenant): let an approved agent retirement actually reach the collector"
```

---

### Task 15: Documentation

**Files:**
- Modify: `docs/Arcanum.DESIGN.md` — a new `### 10.26` section for the curation substrate, with subsections for the exact-version binding, the curation protocol and its storage, the mask's effect on planning, the pin's refusal, and the agent retirement path; renumber §10.26 onward if anything follows it
- Modify: `docs/Arcanum.DESIGN.md` §10.14 — remove the "Deferred" paragraph that says the retirement pipeline is unbuilt, because it now is
- Modify: `docs/Arcanum.DESIGN.md` §10.22.5 and §10.22.6 — `retire_covenant` is no longer withheld; the absent-list shrinks
- Modify: `docs/Arcanum.API.md` — the four new routes and their error-code mapping
- Modify: `docs/Arcanum.Command.Reference.md` — `correct`, `pin`, `unpin`, `mask`, `unmask`
- Modify: `README.md` — a bolded `**Issue #96 …**` sentence in the Covenant status paragraph with updated DESIGN anchors
- Modify: `docs/Arcanum.OATH.md` — §15, the §2.1/§2.2 status tables, the §14 surfaces note, the §22 document map range
- Modify: `docs/ArcanumOATH.Human.md` — §9 prose and the §11 status table
- Modify: `docs/Arcanum.Design.Human.md` and `docs/Arcanum.DEBUGGING.Human.md` where they describe what an operator can do

- [ ] **Step 1: Write the DESIGN section.** Describe what the system is and why, never who asked for it or when it arrived.

- [ ] **Step 2: Verify the no-issue-reference rule**

Run: `grep -nE "#[0-9]{2,4}\b" docs/*.md` — hits are permitted only in `Arcanum.OATH.md`. Then read through for orphan phrases like "the slice that", which are inferred references even with no digits.

- [ ] **Step 3: Verify no hard wrapping was introduced.** Every paragraph, list item and table row is one physical line.

- [ ] **Step 4: Commit**

```bash
git commit -am "docs(covenant): document exact-version curation and the agent retirement path"
```

---

### Task 16: Green gates, adversarial pass, and merge

- [ ] **Step 1: Clean build with the zero-warning gate**

```bash
dotnet build RetroDownfall.Arcanum.slnx --no-incremental -warnaserror
```

- [ ] **Step 2: Clear accumulated test state, then run both suites in full**

```bash
find . -type d -name TestResults -prune -exec rm -rf {} +
```

Then `dotnet test RetroDownfall.Arcanum.slnx` and the Compendium suite. A full run reds roughly one flaky concurrency test, different each time — isolate it before calling it a regression.

- [ ] **Step 3: Audit the whole diff**

```bash
git diff long-term-memory...HEAD
```

Read every deletion line. Treat any removed condition, filter, bounds check, ordering constraint or `await` as guilty until proven innocent. Ask of any test or fixture change whether it made the test realistic or merely quiet.

- [ ] **Step 4: Merge, push, close**

```bash
git checkout long-term-memory
git merge --no-ff codex/issue-96-covenant-version-operations -m "Merge issue #96: target curation at exact versions, and let an approved retirement land"
git push origin long-term-memory
git branch -d codex/issue-96-covenant-version-operations
```

Close #96 with an acceptance-criteria checklist comment, and tick its line in #78's child list with `gh issue edit 78 --body-file`.

## Self-review

**Spec coverage.** §5 correction → Tasks 8, 9. §6.1–6.2 curation subject and storage → Tasks 1, 2, 3. §6.3 pin → Task 11. §6.4 mask → Task 10. §6.5 fallback explanation → Task 5. §7 agent side → Tasks 13, 14. §8 read-back → Tasks 4, 7. §9 lifecycle → Task 12. §10 testing → the mutation-check step inside Tasks 3, 9, 10, 11, 14. §11 inventories → Tasks 2, 5, 12.

**Type consistency.** `CovenantCurationSubject` is defined in Task 1 and consumed by Tasks 3, 4, 5. `CovenantCurationKind` member names are fixed in Task 1 and used unchanged in the SQL `CHECK` of Task 2 (`3, 4` are `Mask`, `Unmask`). `ICovenantMutationService.PrepareCurationAsync`/`CurateAsync` are named in Task 5 and routed in Task 6. `PrepareCorrectAsync`/`CorrectAsync` are named in Task 9 and used nowhere earlier.

**Known ordering hazard.** Task 2's pin must be added while the fingerprint in Global Constraints is still the head tree's — that is why Task 2 comes before every task that adds a `.sql` file, and why no other task adds one.
