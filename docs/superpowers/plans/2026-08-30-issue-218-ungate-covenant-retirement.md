# Issue #218 Ungated Covenant Retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `retire_covenant` execute without a blocking Ward in attended, unattended, and Wards-disabled eligible turns while preserving its exact target, Campaign, egress, disclosure, Sanctum, and durable-history boundaries.

**Architecture:** Retirement joins the record-only `Ungated` Ward audit path, then keeps a Covenant-specific preparation path for classification, canonical preflight, one-call capability minting, disclosure-before-effect, and Sanctum dispatch. New retirement intents retain the frozen `AgentApproved` origin code but carry authorization mode `None` and no Ward receipt. Covenant canonical schema v3 atomically rebuilds `covenant_versions` so new receipt-free rows and historical Ward-backed rows are both valid.

**Tech Stack:** .NET 10, C#, xUnit, Microsoft.Extensions.AI, MCP in-process transport, Microsoft.Data.Sqlite/SQLCipher, embedded SQL schema resources, source-generated `System.Text.Json`, Git, GitHub CLI.

**Spec:** [`docs/superpowers/specs/2026-08-29-issue-218-ungate-covenant-retirement-design.md`](../specs/2026-08-29-issue-218-ungate-covenant-retirement-design.md)

**Global Constraints:**

- Work only on `codex/issue-218-ungate-covenant-retirement` until the final integration step.
- Apply strict RED-GREEN-REFACTOR: add or change the named tests, run them and observe the expected failure, make the smallest production change, then rerun them green before committing.
- Preserve `CovenantOrigin.AgentApproved = 3`, every `CovenantAuthorizationMode` numeric code, `WardEvidenceDigestInput`, `CovenantToolWardReceipt`, Ward digest tags/encoders, and pinned digest vectors as historical compatibility vocabulary.
- Never synthesize a Ward receipt or consent digest for an ungated call.
- Keep proposal staging attended-only and add retirement preparation eligibility that shares every other condition. Session backing, read authority, tool policy, canonical Campaign binding, exact preflight, nonce, disclosure, destination, and Sanctum remain mandatory.
- Keep `propose_covenant` classified as `SensitivePayloadOnly`; do not expand issue #218 into Ward configuration removal (#219), performance work (#220), or the broader prose sweep (#221).
- Use `rg --no-config`. Run .NET test/build processes with `--disable-build-servers -m:1`; the VSTest host needs permission to create its local socket in this environment.
- Do not repeat the complete verification suite after the history-only merge into `remove-wards`; prove the merged tree matches the verified implementation commit instead.

## Task 1: Evolve Covenant canonical storage to schema v3

**Files:**

- Create: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantCanonicalSchemaVersionTwoFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantCanonicalSchemaVersionOneFixture.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantUngatedRetirementEvolutionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantCanonicalSchemaTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantCurationEvolutionTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_versions.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/010_covenant_versions_defer_foreign_keys.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/020_covenant_versions_replacement.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/030_covenant_versions_copy.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/040_covenant_versions_drop.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/050_covenant_versions_rename.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/060_covenant_versions_entry_lane_revision_index.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/070_covenant_versions_head_candidate_index.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/080_covenant_versions_mutation_index.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/090_covenant_versions_entry_created_index.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/100_covenant_versions_source_turn_index.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/110_covenant_versions_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Transitions/V3/120_covenant_versions_guard_update.sql`

### Step 1: Freeze the real v2 catalog in a test fixture

Create `CovenantCanonicalSchemaVersionTwoFixture` before changing the head SQL. Follow `CovenantCanonicalSchemaVersionOneFixture`, but replace the `covenant_versions` object with a frozen copy of its current v2 SQL and retain all v2 curation objects. Its `ChainSet()` must declare canonical version 2 and include only the shipped canonical step whose `ToVersion <= 2`.

Expose that frozen v2 `GrimoireSchemaObject` to `CovenantCanonicalSchemaVersionOneFixture` and substitute it there too. Version 1 and version 2 used the same `covenant_versions` definition; without this substitution, editing the head for v3 would silently mutate the reconstructed v1 tree and invalidate its already-published pin.

The fixture must compute its own fingerprint from the reconstructed objects. The literal captured from the unedited v2 head is:

```csharp
internal const string PublishedFingerprint =
    "BC0914DABEF7A54B0637E66697EE47CC7F2077E67B40BCE6D824EDE2913EDC61";
```

Assert the reconstructed fingerprint equals this literal. Do not calculate the pin from the edited v3 head.

### Step 2: Write the failing v3 version-chain and schema-shape tests

In `GrimoireSchemaVersionChainTests`, change the canonical expected head version from 2 to 3.

In `CovenantUngatedRetirementEvolutionTests`, add tests equivalent to:

```csharp
[Fact]
public void The_shipped_chain_pins_the_fingerprint_the_version_two_tree_published()
{
    GrimoireSchemaVersionChain canonical = GrimoireSchemaVersionChains.Default
        .ForTier(GrimoireSchemaTransactionTier.CovenantCanonical);

    Assert.Equal(
        CovenantCanonicalSchemaVersionTwoFixture.Fingerprint,
        canonical.SourceDefinitionFingerprintFor(2));
}
```

Add RED tests that:

- install the real reconstructed v2 chain and seed an OriginCode 3 row with a 32-byte Ward digest and mode 2 plus an ordinary non-Ward row;
- make the seed exercise every rebuild-sensitive relationship: a `covenant_heads` row whose composite foreign key names the historical version, a `covenant_version_attachment_provenance` child, and a second version whose `PredecessorVersionId` names the first;
- evolve through the production installer and expect canonical health `Healthy` at version 3;
- compare normalized `sqlite_master` definitions for evolved v2→v3 and fresh v3 databases;
- compare the complete Covenant canonical tier, including `covenant_heads`, `covenant_version_attachment_provenance`, and the head-validation triggers that depend on the rebuilt table, rather than narrowing the comparison to objects named `covenant_versions`;
- verify the seeded rows and byte values still join through the head, provenance child, and predecessor chain; assert `PRAGMA foreign_key_check` returns no rows; and prove all five `covenant_versions` indexes plus both append-only triggers survive;
- exercise `covenant_heads_validate_insert` and `covenant_heads_validate_update` after evolution so dependent trigger behavior, not only its `sqlite_master` text, is preserved;
- insert OriginCode 3 with both Ward fields null successfully on fresh and evolved databases;
- retain acceptance of historical OriginCode 3 with a digest and mode 2 or 3;
- reject mixed legacy tuples (digest/null-mode or null-digest/Ward-mode) and any non-3 origin carrying either Ward field.

Update `CovenantCanonicalSchemaTests` with the same raw CHECK-matrix assertions for a fresh database. In `CovenantCurationEvolutionTests`, keep the v1 pin/reconstruction assertions, install `CovenantCanonicalSchemaVersionTwoFixture.ChainSet()` after v1 and assert health/version 2, then install the shipped default and assert health/version 3. Change its former “head fingerprint answers for version two” assertion to compare `SourceDefinitionFingerprintFor(2)` with the v2 fixture; the new evolution suite owns the v3 head assertion.

In `GrimoireSchemaTransitionResourceTests`, add `(CovenantCanonical, 3)` to the allowed tier/version pairs and append these exact statement names to the ordered catalog assertion:

```text
covenant_versions_defer_foreign_keys
covenant_versions_replacement
covenant_versions_copy
covenant_versions_drop
covenant_versions_rename
covenant_versions_entry_lane_revision_index
covenant_versions_head_candidate_index
covenant_versions_mutation_index
covenant_versions_entry_created_index
covenant_versions_source_turn_index
covenant_versions_guard_delete
covenant_versions_guard_update
```

### Step 3: Run the focused tests and observe RED

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantUngatedRetirementEvolutionTests|FullyQualifiedName~CovenantCanonicalSchemaTests|FullyQualifiedName~CovenantCurationEvolutionTests|FullyQualifiedName~GrimoireSchemaVersionChainTests|FullyQualifiedName~GrimoireSchemaTransitionResourceTests"
```

Expected failure: the shipped canonical head is still 2, there is no v2→v3 step/pin, and the existing CHECK rejects OriginCode 3 with null Ward fields.

### Step 4: Implement the v3 head and version chain

In `GrimoireSchemaVersionChains.cs`:

```csharp
internal const int CovenantCanonicalSchemaVersion = 3;
```

Add the captured source pin:

```csharp
[(GrimoireSchemaTransactionTier.CovenantCanonical, 3)] =
    "BC0914DABEF7A54B0637E66697EE47CC7F2077E67B40BCE6D824EDE2913EDC61",
```

Update only the Ward-field CHECK in the head `covenant_versions.sql`. Its logic must be:

```sql
CHECK (
    (OriginCode = 3 AND (
        (WardReceiptDigest IS NULL AND AuthorizationModeCode IS NULL)
        OR (WardReceiptDigest IS NOT NULL AND AuthorizationModeCode IN (2, 3))
    ))
    OR (OriginCode <> 3 AND WardReceiptDigest IS NULL AND AuthorizationModeCode IS NULL)
)
```

### Step 5: Implement the ordered atomic table rebuild

Each transition resource contains exactly one statement. In order:

1. `PRAGMA defer_foreign_keys = ON;`
2. Create `covenant_versions_replacement` with the head column order, foreign keys, and v3 constraints. Its self-reference names `covenant_versions_replacement` until rename.
3. Copy every column in head order with this exact statement:

```sql
INSERT INTO covenant_versions_replacement (
    VersionId, EntryId, LaneCode, LaneRevision, OperationCode,
    AuthoredContent, CompiledContent, AuthoredHash, RenderedHash,
    CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion, RendererPolicyVersion,
    OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest, AdmissionReceiptDigest,
    WardReceiptDigest, AuthorizationModeCode, MutationId, RequestIdempotencyDigest,
    AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
    AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc
)
SELECT
    VersionId, EntryId, LaneCode, LaneRevision, OperationCode,
    AuthoredContent, CompiledContent, AuthoredHash, RenderedHash,
    CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion, RendererPolicyVersion,
    OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest, AdmissionReceiptDigest,
    WardReceiptDigest, AuthorizationModeCode, MutationId, RequestIdempotencyDigest,
    AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
    AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc
FROM covenant_versions;
```
4. `DROP TABLE covenant_versions;`
5. `ALTER TABLE covenant_versions_replacement RENAME TO covenant_versions;`
6. Recreate `ux_covenant_versions_entry_lane_revision`.
7. Recreate `ux_covenant_versions_head_candidate`.
8. Recreate `ux_covenant_versions_mutation`.
9. Recreate `idx_covenant_versions_entry_created`.
10. Recreate `idx_covenant_versions_source_turn`.
11. Recreate `covenant_versions_guard_delete` from the head trigger file character-for-character.
12. Recreate `covenant_versions_guard_update` from the head trigger file character-for-character.

Do not add a backfill. The copy must not rewrite historical Ward fields, digests, identities, revisions, timestamps, or content.

### Step 6: Run focused schema tests GREEN

Run the Step 3 command. Expected: all selected tests pass with no warnings.

### Step 7: Commit the schema slice

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Schema tests/RetroDownfall.Arcanum.Tests/Data/Schema tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantCanonicalSchemaVersionOneFixture.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantCanonicalSchemaVersionTwoFixture.cs
git commit -m "feat(schema): allow receipt-free Covenant retirement"
```

## Task 2: Reverse the live egress policy and attendance rule

**Atomic runtime sequencing:** Tasks 2 through 6 are one compile-atomic TDD slice and produce one commit. Before editing production code, complete the test edits described in Task 2 Step 1, Task 3 Step 1, Task 4 Step 1, and Task 5 Step 1, then run the unified RED command below. After that failure is recorded, apply the production phases in Tasks 2 through 5, update every owning document/comment in Task 6, run the unified GREEN command in Task 6, and commit once. Do not commit or claim a green build between these phases: the old pipeline consumes the policy and receipt members removed by the new capability, so an intermediate source tree is intentionally not a supported boundary.

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ArcanumInvocationContextTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantSensitiveEgressTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantMutationToolTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Intelligence/ArcanumInvocationContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantEgressWardPolicy.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs`

### Step 1: Write the failing eligibility, policy, and advertisement tests

In `ArcanumInvocationContextTests`, keep `CanStageCovenantMutation` false for the unattended, session-backed, authority-bearing, Campaign-bound, all-tools case and expect the new retirement-preparation predicate to be true. Retain false assertions for both predicates in Global-only, no-tools, missing-authority, non-session, and context-disabled cases.

Replace the Ward-specific policy cases in `CovenantSensitiveEgressTests` with assertions that:

- eligible retirement resolves `UngatedRetirement` for attended and unattended eligible invocations;
- Ward enabled/disabled and auto-approval settings cannot change that result;
- attended proposal remains `SensitivePayloadOnly`, while unattended proposal is `DeniedIneligibleTurn`;
- ordinary calls remain `NotSensitive`;
- only a turn that fails the classified tool's proposal or retirement predicate resolves `DeniedIneligibleTurn`;
- no live policy method produces `CovenantToolWardReceipt`.

The target production signature is:

```csharp
public static CovenantEgressWardDecision Resolve(
    ProviderToolCallClassification classification,
    ArcanumInvocationContext invocation)
```

In `CovenantMutationToolTests`, rename the advertisement theory to `Retirement_is_advertised_independently_of_Ward_configuration` and assert both Covenant tools are listed for both `wardsEnabled` inputs. Keep the registered-handler assertion.

### Step 2: Run the complete runtime test set and observe RED

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~ArcanumInvocationContextTests|FullyQualifiedName~CovenantSensitiveEgressTests|FullyQualifiedName~CovenantAgentRetirementTests|FullyQualifiedName~CovenantToolStagingMintTests|FullyQualifiedName~CovenantMutationToolTests|FullyQualifiedName~CovenantToolInvocationContextTests|FullyQualifiedName~CovenantToolCapabilityRegistryTests|FullyQualifiedName~CovenantAgentMutationFactoryTests|FullyQualifiedName~CovenantMutationIntentTests|FullyQualifiedName~CovenantMutationKernelTests"
```

Expected failure: unattended staging is false; policy returns attended/auto-approved/disabled-Ward outcomes; retirement advertisement follows `_wardsEnabled`; live capabilities/intents require receipts; and the pipeline prompts or denies rather than recording `Ungated`.

### Step 3: Implement the four-outcome policy

Keep proposal staging attended-only and add retirement preparation with the shared non-attendance conditions:

```csharp
public bool CanStageCovenantMutation =>
    Attendance is InvocationAttendance.Attended
    && CanPrepareCovenantRetirement;

public bool CanPrepareCovenantRetirement =>
    CanReadCovenant
    && Surface is ArcanumExecutionSurface.SessionBackedOperatorTurn
    && ToolPolicy is not ToolPolicy.NoTools
    && Campaign is { IsCampaignBound: true };
```

Keep `CovenantEgressWardPolicy.cs`, but reduce the enum to live outcomes `NotSensitive`, `SensitivePayloadOnly`, `UngatedRetirement`, and `DeniedIneligibleTurn`. Preserve the existing numeric values of retained members where possible; assign `UngatedRetirement` the retired retirement slot so no unrelated value moves.

Reduce `CovenantEgressWardDecision` to `Authorization` and `RiskIdentity`. `IsDenied` is true only for `DeniedIneligibleTurn`. Remove its authorization mode, `RequiresOperatorPrompt`, `CarriesWardEvidence`, Ward-settings input, auto-approval resolution, `DeniedWardsDisabled`, and `Accept`.

The resolution body is:

```csharp
if (!classification.IsCovenantMutation)
{
    return Decision(CovenantEgressAuthorization.NotSensitive, classification.RiskIdentity);
}

bool retirement = string.Equals(
    classification.ToolName,
    CovenantToolNames.RetireCovenant,
    StringComparison.Ordinal);

bool eligible = retirement
    ? invocation.CanPrepareCovenantRetirement
    : invocation.CanStageCovenantMutation;

if (!eligible)
{
    return Decision(CovenantEgressAuthorization.DeniedIneligibleTurn);
}

return retirement
    ? Decision(CovenantEgressAuthorization.UngatedRetirement)
    : Decision(CovenantEgressAuthorization.SensitivePayloadOnly);
```

Rewrite the policy remarks to state the reversal: feature/canonical health, invocation authority, and the one-call capability authorize the advertised retirement; the host records the call and does not ask again.

Delete `_wardsEnabled`, its options-monitor resolution, and `CovenantRetirementAvailable`. Inside the existing `if (CovenantToolsAvailable())` block, advertise both proposal and retirement unconditionally. Rewrite the retirement description so it promises exact preflight-bound staging rather than operator approval. Keep the handler registered so direct or stale requests still fail on capability.

### Step 4: Continue the atomic runtime implementation

Do not run a green gate or commit here. The old pipeline still consumes the policy members removed in this phase. Continue directly through Tasks 3–6 and use Task 6's unified GREEN gate and single runtime/documentation commit.

## Task 3: Remove Ward receipts from the live capability and new intents

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantToolInvocationContextTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantToolStagingMintTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantMutationToolTests.cs`
- Modify mechanically for the constructor signature: `tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantToolCapabilityRegistryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantAgentMutationFactoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantMutationIntentTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMutationFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMutationKernelTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantToolInvocationContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantAgentMutationFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/CovenantToolStagingAmbient.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/SessionAttachmentAmbientSend.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs`

### Step 1: Write the failing capability tests

Replace `A_retirement_capability_requires_its_preflight_and_its_ward_receipt` with tests proving:

- retirement with an exact preflight and no receipt constructs successfully;
- retirement without a preflight throws;
- proposal with a retirement preflight throws;
- proposal without a preflight constructs successfully.

In `CovenantToolStagingMintTests`, change the retirement test to publish a staging context carrying `RetirementPreflight` and assert that `BindCovenantStaging` registers a retirement capability. Add the inverse assertion that a retirement without preflight mints nothing, while a proposal still mints normally.

In `CovenantMutationToolTests`, rename `A_retirement_stages_the_exact_target_its_ward_was_shown` to `A_retirement_stages_the_exact_target_its_preflight_resolved`, remove the declined-Ward test, and make `RegisterRetirementCapability` accept only the preflight. Preserve malformed key/lane, different-target, pinned/tombstone, one-shot, and cross-tool refusal tests.

In `CovenantAgentMutationFactoryTests`, keep the frozen origin and request-preimage assertions, but expect `Mode == None`, a null Ward digest, and the same non-null preflight digest. In `CovenantMutationIntentTests`, make AgentRetire + `None` + null Ward digest valid. In `CovenantMutationKernelTests`, expect the factory-built retirement row to retain `OriginCode = 3` while both Ward columns are SQL NULL.

### Step 2: Include these tests in the unified RED observation

Write these tests before Task 2 Step 2. Their expected RED is that the constructor/binder/factory still require a Ward receipt, the intent rejects the new tuple, and the kernel would bind numeric mode zero. Do not run a second RED command here.

### Step 3: Implement the receipt-free capability shape

Remove `CovenantToolWardReceipt? wardReceipt` and the `WardReceipt` property from `CovenantToolInvocationContext`. Keep this invariant:

```csharp
bool isRetirement = string.Equals(
    toolName,
    CovenantToolNames.RetireCovenant,
    StringComparison.Ordinal);

if (isRetirement != (retirementPreflight is not null))
{
    throw new ArgumentException(
        isRetirement
            ? "A retirement capability carries its resolved target preflight."
            : "A proposal capability carries no retirement target.",
        nameof(retirementPreflight));
}
```

Remove `WardReceipt` from `CovenantToolStagingContext`. Keep `RetirementPreflight` and `Nonce` as call-scoped optional fields.

In `SessionAttachmentAmbientSend.BindCovenantStaging`, pair the tool only with the preflight:

```csharp
bool retirement = string.Equals(
    name,
    CovenantToolNames.RetireCovenant,
    StringComparison.Ordinal);

if (retirement != (staging.RetirementPreflight is not null))
{
    return;
}
```

Pass only `staging.RetirementPreflight` to the revised capability constructor and reuse `staging.Nonce` when the disclosure already bound one.

Update every constructor call found by:

```bash
rg --no-config -n "new CovenantToolInvocationContext\(|CovenantToolInvocationContext capability = new" src tests
```

Do not delete `CovenantToolWardReceipt` itself; it remains historical digest vocabulary.

Update `CovenantAgentMutationFactory.Retire` to require only the preflight. Keep the frozen `AgentApproved` origin, target, revision, key epoch, request preimage, plan, admission, and tool-input bindings. Pass the preflight digest, `WardReceiptDigest: null`, and `CovenantAuthorizationMode.None` into both `AuthorizationDigestInput` and `CovenantMutationAuthorization`.

Make `CovenantMutationIntent` accept both the new AgentRetire null/None tuple and the complete paired historical Ward tuple described in Task 4. Continue rejecting partial pairs and Ward evidence/modes on every non-AgentApproved origin. The live factory produces only the new tuple.

Change only the AgentApproved mode write in `CovenantMutationKernel` so `None` becomes SQL NULL rather than zero:

```csharp
object authorizationMode =
    intent.Origin == CovenantOrigin.AgentApproved
    && intent.Authorization.Mode != CovenantAuthorizationMode.None
        ? (int)intent.Authorization.Mode
        : DBNull.Value;

Bind(command, "$authorizationMode", authorizationMode);
```

Change `CovenantMutationFixture.AgentRetire` to build the new null/None shape. Add the visibly named historical helper required by the Task 4 tests, but do not use it from any live call path.

### Step 4: Continue the atomic runtime implementation

Do not commit this capability/factory phase separately. Continue through the pipeline and owning-document updates; Task 6 is the first supported compile-complete runtime boundary.

## Task 4: Preserve historical Ward-backed retirement compatibility

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantMutationIntentTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMutationFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMutationKernelTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationContracts.cs`

### Step 1: Write the failing historical-shape tests

In `CovenantMutationIntentTests`, assert the following closed matrix:

- AgentRetire + WardInteractive/WardConfiguredAutoApproval + non-null digest remains representable for historical compatibility fixtures;
- AgentRetire with only one half of a legacy pair is rejected;
- AgentRetire + ApiMasterKey is rejected;
- every other mutation kind rejects a Ward digest or either Ward mode;
- operator mutations retain ApiMasterKey and agent proposals retain None.

Add `CovenantMutationFixture.HistoricalWardBackedAgentRetire` without changing the new default helper. Use it only in compatibility tests.

Add a kernel test that the historical helper still writes OriginCode 3, its exact digest, and mode 2 or 3 unchanged. Keep the receipt-free factory test from Task 3 green.

### Step 2: Include these tests in the unified RED observation

Write these tests before Task 2 Step 2. Some historical-pair assertions already pass against the old contract; the unified test set is RED because the newly required live null/None, policy, capability, and pipeline behavior does not. Do not run a second RED command here.

### Step 3: Admit only complete historical pairs

In `CovenantMutationIntent`, accept exactly either the new null/None tuple or the paired historical Ward tuple for `AgentApproved`; reject partial/mismatched pairs and reject any Ward evidence/mode on other origins. The live factory must only produce null/None.

Keep the historical helper visibly named and isolated. Do not add any live call site that supplies it, and do not restore a Ward receipt to the staging ambient, capability, factory, or pipeline.

### Step 4: Continue the atomic runtime implementation

Do not commit a separate compatibility checkpoint. Continue to the pipeline phase; Task 6's unified GREEN run proves new and historical shapes together.

## Task 5: Route retirement through record-only audit and retained containment

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantAgentRetirementTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantRetirementHarness.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantToolInvocationContracts.cs`

### Step 1: Write the failing end-to-end pipeline tests

Revise `CovenantRetirementHarness` so `RetireAsync` can select `InvocationAttendance` and collect observer events. Make `RecordingWard` retain automatic-resolution origins, not just a count.

Rewrite `CovenantAgentRetirementTests` to prove:

- disabled Wards execute the retirement;
- an unattended but otherwise eligible turn executes;
- `IWard.WardAsync` is never called;
- exactly one `RecordAutomaticResolution` occurs, with origin `Ungated` rather than auto-approval;
- `ProcessedToolCall.WardEvents` contains exactly `Warded`, then `WardResolved`, both with `WardResolutionOrigin.Ungated` and one shared Ward id;
- no `ToolApprovalRequestedEvent` is observed;
- disclosure journal acknowledgement still precedes tool invocation;
- journal failure still prevents the effect;
- no staging ambient, malformed target, ineligible invocation, missing/tombstoned/pinned/stale preflight, or mismatched target reaches the effect;
- the disclosed `CovenantToolEgressAttempt.WardEvidenceDigest` is null;
- the Ward metric records one `origin="ungated"` measurement for the retirement.

Delete tests about prompt content, operator decline, and configured auto-approval. Keep `A_target_the_probe_refuses_never_reaches_a_Ward`, but rename it to say it never reaches the effect and assert `WaitCount == 0`.

### Step 2: Include these tests in the unified RED observation

Write the pipeline tests before Task 2 Step 2. Their expected RED is that retirement still branches before ordinary recording, denies disabled/unattended calls, prompts or auto-approves, and manufactures Ward evidence. Do not run a second RED command here.

### Step 3: Move record-only audit ahead of retirement preparation

In `ExecuteToolCallWithWardAsync`, call `RecordUngatedWardResolutionAsync` immediately after resolving `toolName`, before the `retire_covenant` branch. Pass `metricToolName` so arbitrary unregistered names remain bounded. The branch then calls the simplified retirement preparation; ordinary calls proceed directly to ask-human observation/Sanctum as today.

Remove now-unused `PingRequest request` and `sessionId` parameters from `ExecuteToolCallWithWardAsync` and its two call sites. Remove `WardSettings`, `sessionId`, `liveWardEmit`, and `observer` from `ExecuteCovenantRetirementAsync`.

### Step 4: Simplify retirement to preparation, disclosure, and dispatch

The method order must be:

1. require staging ambient, live invocation, and egress guard;
2. classify the exact frozen name and canonical arguments;
3. resolve the four-outcome egress policy and refuse only `DeniedIneligibleTurn`;
4. parse key/lane;
5. resolve canonical preflight;
6. mint nonce;
7. create `CovenantToolEgressAttempt` with Process destination, current admission/sensitivity/preflight identity, and `WardEvidenceDigest: null`;
8. commit disclosure;
9. push `staging with { RetirementPreflight = preflight, Nonce = nonce }`;
10. invoke through `InvokeToolCallWithSanctumAsync`.

Delete the retirement prompt JSON, timeout, `WardAsync`, configured-auto-approval carve-out, `ToolApprovalRequestedEvent`, `CovenantEgressWardPolicy.Accept`, and `WasAdmittedOnThisTurn`.

Because the prompt wire shape becomes unreachable, remove `CovenantRetirementDisclosureWire` and its `ArcanumJsonContext` registration. Keep `CovenantToolWardReceipt`, `CovenantWardDecision`, and the digest types/vectors as historical compatibility vocabulary.

### Step 5: Continue to owning documentation

Do not commit the pipeline without its owning docs and configuration/UI descriptions. Continue to Task 6, then run the unified GREEN gate over the compile-complete runtime tree.

## Task 6: Update owning descriptions and close the atomic runtime slice

**Files:**

- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `docs/Compendium.README.md`
- Modify: `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Configuration/WardSettings.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Configuration/ConfigurationValidator.cs`

### Step 1: Replace direct false claims

Update the direct retirement contract in DESIGN §§10.2, 10.7, 10.14, 10.22/10.26, and 11.14:

- `retire_covenant` emits the ordinary record-only `Ungated` pair and never creates an active Ward;
- attendance, Ward enabled/disabled, timeout, and auto-approval no longer decide retirement;
- capability eligibility, exact canonical preflight, target/key-epoch binding, Campaign scope, Process destination, disclosure-before-effect, egress accounting, and Sanctum remain;
- new origin-code-3 rows use null legacy Ward fields and schema v3 preserves historical mode-2/3 rows without rewriting them;
- `AgentApproved` is now a frozen compatibility label for agent-requested retirement admitted by the ungated host, not proof of fresh operator consent.

In `docs/Compendium.README.md`, update the Covenant feature overview and the rows for `security.ward.enabled`, `security.ward.autoApprove.enabled`, and `security.ward.autoApprove.tools` so they say no live ordinary or Covenant-retirement path consults them. Retain the settings themselves for #219.

Update the active Ward-frame contract in `docs/Arcanum.API.md` §8: retirement now also emits `ungated`, creates no active Ward, and never produces an approval prompt. This is the one API-doc exception to the broader historical prose sweep assigned to #221 because the current streaming-contract sentence becomes directly false in #218.

Update the Ward compatibility descriptions in `SettingDescriptors.cs`, `WardSettings.cs`, `PublicConfigurationSettings.cs`, and `ConfigurationValidator.cs`. Retain the keys, validation, public shapes, and defaults for #219, but remove every claim that they grant or control Covenant-retirement consent. The Compendium labels/descriptions and placeholder must not encourage adding `retire_covenant` to a no-longer-consumed allowlist.

Do not broaden this task into the remaining README/CLI/Command Center prose sweep assigned to #221.

### Step 2: Run documentation and stale-claim checks

```bash
rg --no-config -n "retire_covenant|Covenant retirement|retirement.*Ward|Ward.*retirement|DeniedWardsDisabled|AttendedWardRequired|ConfiguredAutoApproval" docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Compendium.README.md src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs src/RetroDownfall.Arcanum.Core/Configuration/WardSettings.cs src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs src/RetroDownfall.Arcanum.Core/Configuration/ConfigurationValidator.cs
git diff --check
```

Manually inspect every hit. Expected: no current-contract claim says retirement prompts, times out, auto-approves, refuses disabled Wards, or requires a consent receipt; historical descriptions are clearly labeled.

### Step 3: Run the unified runtime GREEN gate

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~ArcanumInvocationContextTests|FullyQualifiedName~CovenantSensitiveEgressTests|FullyQualifiedName~CovenantAgentRetirementTests|FullyQualifiedName~CovenantToolStagingMintTests|FullyQualifiedName~CovenantMutationToolTests|FullyQualifiedName~CovenantToolInvocationContextTests|FullyQualifiedName~CovenantToolCapabilityRegistryTests|FullyQualifiedName~CovenantAgentMutationFactoryTests|FullyQualifiedName~CovenantMutationIntentTests|FullyQualifiedName~CovenantMutationKernelTests|FullyQualifiedName~CovenantRetirementPreflightTests|FullyQualifiedName~CovenantDispatchGateTests|FullyQualifiedName~WardAutoApprovalPipelineTests|FullyQualifiedName~ToolRiskClassifierTests|FullyQualifiedName~CampaignSettingsTests"
git diff --check
```

Expected: all selected runtime and invariant tests pass with no warnings, and documentation/source descriptions have no malformed diff.

### Step 4: Prove the live retirement Ward path is gone

These searches must return no matches:

```bash
rg --no-config -n "CovenantEgressWardPolicy\.Accept|CovenantRetirementAvailable|_wardsEnabled|WasAdmittedOnThisTurn|WardReceipt =" src
rg --no-config -n "WardAsync\(" src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs
rg --no-config -n "CovenantToolWardReceipt" src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/CovenantToolStagingAmbient.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/SessionAttachmentAmbientSend.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantToolInvocationContext.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantAgentMutationFactory.cs
```

The historical type, digest input, enum values, encoders, and vectors outside those live files remain intentionally.

### Step 5: Commit the compile-complete runtime and owning descriptions once

Inspect `git status --short`, stage only the files enumerated in Tasks 2–6, and commit them together:

```bash
git add docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Compendium.README.md src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs src/RetroDownfall.Arcanum.Core/Configuration/WardSettings.cs src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs src/RetroDownfall.Arcanum.Core/Configuration/ConfigurationValidator.cs src/RetroDownfall.Arcanum.Core/Intelligence/ArcanumInvocationContext.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantEgressWardPolicy.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantToolInvocationContext.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantAgentMutationFactory.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantMutationContracts.cs src/RetroDownfall.Arcanum.Core/Covenant/CovenantToolInvocationContracts.cs src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/CovenantToolStagingAmbient.cs src/RetroDownfall.Arcanum.Infrastructure/Mcp/SessionAttachmentAmbientSend.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/ArcanumInvocationContextTests.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantAgentRetirementTests.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/CovenantRetirementHarness.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantSensitiveEgressTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantToolInvocationContextTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantAgentMutationFactoryTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantMutationIntentTests.cs tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantToolStagingMintTests.cs tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantMutationToolTests.cs tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantToolCapabilityRegistryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMutationFixture.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMutationKernelTests.cs
git commit -m "feat(covenant): ungate Covenant retirement"
```

## Task 7: Review the completed implementation before full verification

### Step 1: Inspect the combined committed diff without repeating focused tests

```bash
git log --oneline 3e03e0b5..HEAD
git diff --stat 3e03e0b5..HEAD
git diff 3e03e0b5..HEAD
```

Task 1 already ran the schema cluster GREEN and Task 6 already ran the runtime/invariant cluster GREEN. Do not repeat them here; the next test execution is the one full coverage-backed suite in Task 8.

### Step 2: Perform a bounded code review

Use `superpowers:requesting-code-review` against the full diff from `3e03e0b5` through HEAD. Review specifically for:

- an alternate reachable Ward prompt/auto-approval/disabled-Ward denial;
- missing preflight, Campaign, nonce, disclosure, destination, or Sanctum checks;
- fake or numeric-zero Ward evidence;
- v2 data rewrite, missing indexes/triggers, or fresh/evolved catalog drift;
- accidental changes to proposal semantics, enum numbers, digest encoders/vectors, or #219/#220/#221 scope.

Fix every correctness issue with a new RED test where observable, rerun only its focused test, and commit the fix. Do not enter an open-ended review loop.

### Step 3: Prove the worktree is review-clean

```bash
git diff --check 3e03e0b5..HEAD
git status --short
```

Expected: no whitespace errors and no uncommitted changes.

## Task 8: Run the complete required verification suite once

Use `superpowers:verification-before-completion`. Run from the repository root in this order, stopping at the first failure and applying `superpowers:systematic-debugging` before changing code:

`scripts/coverage.sh` runs the complete non-Perf Arcanum test suite itself. Use that as the one full non-Perf Arcanum run, then run only the deliberately excluded Perf category separately. This meets both the coverage gate and the no-duplicate-full-suite rule.

```bash
dotnet build RetroDownfall.Arcanum.slnx --disable-build-servers -m:1
./scripts/coverage.sh --threshold
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build --disable-build-servers -m:1 --filter "Category=Perf"
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --disable-build-servers -m:1
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj --disable-build-servers -m:1
./scripts/verify-aot-il-warnings.sh
./scripts/verify-native-sqlcipher.sh --rid osx-arm64
git diff --check 3e03e0b5..HEAD
git status --short
```

Acceptance is zero failed tests, zero build/AOT/IL/native-verification errors, zero warnings, coverage gates met, clean diff check, and a clean worktree. Record the exact counts/output once; do not rerun already-green full gates.

## Task 9: Merge, clean branches, push, and mark #218 done

Use `superpowers:finishing-a-development-branch` after Task 8 is green.

### Step 1: Capture the verified implementation identity

```bash
git rev-parse HEAD
git status --short --branch
```

### Step 2: Merge every #218 commit into the tracked aggregation branch

```bash
git switch remove-wards
git merge --no-ff codex/issue-218-ungate-covenant-retirement -m "Merge issue #218 ungated Covenant retirement"
```

Do not rerun the full suite for this history-only merge. Instead prove the merged tree is byte-identical to the verified implementation tree:

```bash
git diff --exit-code codex/issue-218-ungate-covenant-retirement..remove-wards
git diff --check
git status --short --branch
```

### Step 3: Push only the aggregation branch

```bash
git push origin remove-wards
```

Confirm `origin/remove-wards` resolves to the local merge commit before closing the issue.

### Step 4: Delete implementation branches created for #218

```bash
git branch -d codex/issue-218-ungate-covenant-retirement
```

No remote implementation branch should exist because only `remove-wards` is pushed. If inspection finds one created during implementation, delete that exact remote branch after confirming it is merged.

### Step 5: Mark GitHub issue #218 done without closing parent #197

Post a concise issue comment naming the receipt-free compatibility choice, schema v3 upgrade, test/gate results, and pushed `remove-wards` merge commit. Then close #218:

```bash
gh issue close 218 --comment "Delivered on remove-wards: Covenant retirement is record-only ungated, retains exact preflight/disclosure/Sanctum containment, writes no operator-consent receipt for new rows, and upgrades canonical storage to v3 while preserving historical Ward-backed rows. All required verification gates passed."
```

Re-read #218 and its project item. If closing did not move the linked project status to `Done`, update that exact item to `Done`. Re-read #197 and confirm it remains open.

### Step 6: Report final evidence

Report:

- `remove-wards` merge commit and pushed remote identity;
- deleted implementation branches;
- focused and full verification results, including zero-warning evidence;
- #218 closed/project `Done` and #197 still open;
- any intentionally unrun gate, which should be none.
