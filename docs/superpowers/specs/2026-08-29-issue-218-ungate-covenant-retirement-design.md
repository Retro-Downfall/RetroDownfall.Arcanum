# Issue #218: Ungated Covenant retirement

**Status:** Approved design, pending implementation.

**Branch:** `codex/issue-218-ungate-covenant-retirement`, cut from the tracked `remove-wards` aggregation branch after merging `origin/main` at `decdf011f69ab91c1e48a0d50c2bbf97cd928162`.

**Issues:** Delivery slice #218 under epic #197. Blocker #217 is closed and present on `remove-wards` as commit `2ff6a775e34e4b95ab3f18c01dd04284bd47a23e`. Completing this slice does not close #197.

## 1. Objective

Make `retire_covenant` execute without an operator prompt in attended and unattended turns, including when `Arcanum:Security:Ward:Enabled=false`.

Retirement remains an exact, Campaign-bound, one-call mutation. The pipeline still resolves the canonical target preflight, binds the capability to the provider call and its admission, commits the disclosure record before the effect, enforces Sanctum and every path/capability boundary, and publishes the tombstone only with the turn's own committed reply. What disappears is the second decision: no Ward, timeout, configured auto-approval, or synthetic consent receipt stands between an eligible invocation and that existing flow.

## 2. Approved receipt decision

New agent retirements carry **no Ward or operator-consent receipt**. The implementation must not manufacture a digest merely to satisfy the current database `CHECK`; that would claim consent nobody gave.

The historical receipt vocabulary remains readable and digest-compatible:

- `CovenantAuthorizationMode` retains its numeric codes and its operator `ApiMasterKey` use.
- `WardEvidenceDigestInput`, `CovenantToolWardReceipt`, the Ward-evidence digest domain tag, and their pinned vectors remain as legacy evidence vocabulary.
- `covenant_versions.WardReceiptDigest` and `AuthorizationModeCode` remain nullable columns so already-written Ward-backed retirement history is not discarded or rewritten.
- New `AgentRetire` intents use `CovenantAuthorizationMode.None`, a null Ward receipt digest, and the existing `CovenantOrigin.AgentApproved` numeric value. The origin name is a frozen compatibility label; documentation states that new rows in this category mean an agent-requested retirement admitted by the ungated host, not a new operator approval.

This is deliberately narrower than removing the complete receipt chain. Removing `CovenantAuthorizationMode` or the authorization preimage would change operator mutation and curation digests, stored idempotency identities, pinned corpus vectors, and historical readers outside #218.

## 3. Scope

### 3.1 In scope

- Add `UngatedRetirement` to `CovenantEgressAuthorization`.
- Remove `DeniedWardsDisabled`, attended-Ward, and configured-auto-approval authorization outcomes from the live Covenant egress policy. `DeniedIneligibleTurn` remains the only denial.
- Rewrite `CovenantEgressWardPolicy` as an explicit record of the reversed rule: eligible retirement is ungated regardless of Ward settings or attendance. Its `Resolve` path no longer accepts `WardSettings` or calls `ToolRiskClassifier.IsAutoApproved`.
- Remove the retirement prompt, timeout, automatic-approval carve-out, `IWard.WardAsync`, and `CovenantEgressWardPolicy.Accept` path from `ToolExecutionPipeline`.
- Emit the same record-only `Warded` / `WardResolved` pair with `WardResolutionOrigin.Ungated` for `retire_covenant` that #216/#217 established for ordinary provider-issued calls.
- Keep target parsing and `ResolveRetirementPreflightAsync` before the effect.
- Keep `CovenantToolEgressGuard.DiscloseThenAsync`; its `McpToolUse` disclosure is audit/accounting evidence, not operator-consent evidence. The attempt carries a null Ward evidence digest.
- Permit a session-backed, Campaign-bound, tool-enabled Covenant turn to stage mutations regardless of `InvocationAttendance`.
- Advertise `retire_covenant` whenever the Covenant feature/canonical tier makes the tool available; Ward enabled state no longer affects advertisement.
- Remove `CovenantToolWardReceipt` from the live staging ambient and `CovenantToolInvocationContext`. A retirement capability requires its exact preflight and nonce, not a receipt.
- Permit and persist a new `AgentRetire` with authorization mode `None` and no Ward receipt.
- Evolve existing Covenant canonical databases to accept that row shape without changing historical rows.
- Update the owning design and Compendium documentation in the same change set.

### 3.2 Out of scope

- Removing Ward API routes, Command Center modal/coordinator surfaces, or auto-approve/Forbidden-Arts configuration keys; those belong to #219.
- Hoisting or deleting per-call Ward settings resolution beyond what this retirement path no longer needs; the complete performance slice is #220.
- The repository-wide Ward wording and AOT qualification sweep; that is #221.
- Changing `propose_covenant` wire shape, mutation semantics, destination policy, or Proposed-lane rules. Its shared attendance restriction disappears only because that restriction existed solely to support the retirement prompt.
- Changing Covenant identity binding, Campaign scoping, preflight compare-and-swap, pinned-head refusal, key epochs, admission binding, Sanctum, or disclosure-before-effect ordering.
- Closing #197 or any sibling delivery slice.

## 4. Considered approaches

### 4.1 Preserve live receipt-shaped retirement with synthetic ungated evidence — rejected

Mint a Ward-shaped receipt with an `Ungated` decision so the current schema and factory continue to accept `AgentApproved` rows.

Rejected because the receipt type and digest preimage explicitly assert an operator-consent decision. Re-labelling an automatic host action as that evidence would satisfy a database constraint by making durable history false, directly contradicting #218.

### 4.2 Remove every Ward receipt and authorization type — rejected

Delete `CovenantToolWardReceipt`, `CovenantAuthorizationMode`, `WardEvidenceDigestInput`, their digest tags/vectors, and the two storage columns.

Rejected as a separate compatibility migration. `CovenantAuthorizationMode.ApiMasterKey` is used by operator mutations and curation; the mode and optional receipt digest are part of `AuthorizationDigestInput`; those digests are persisted and participate in idempotency and final mutation identities. Deleting them would rewrite a protocol substantially larger than the retirement gate.

### 4.3 Stop producing consent evidence while preserving historical vocabulary — selected

Remove receipt production and live capability plumbing, write null legacy fields for new retirements, keep historical encodings readable, and migrate the table constraint atomically. This satisfies the issue without falsifying evidence or invalidating unrelated durable identities.

## 5. Runtime design

### 5.1 Eligibility and advertisement

`ArcanumInvocationContext.CanStageCovenantMutation` keeps all of its authority conditions except attendance:

- Covenant content is currently readable under a live authority epoch;
- the surface is a session-backed operator turn with a durable assistant entry;
- tools are enabled;
- a canonical Campaign is bound.

Removing `Attendance == Attended` is necessary for the issue's unattended acceptance case. It also lets `propose_covenant` use the same already-defined `SensitivePayloadOnly` behavior in an unattended eligible turn; no proposal semantics change.

`ArcanumInternalToolServer.CovenantRetirementAvailable` no longer depends on `_wardsEnabled`. Feature and canonical-tier health remain the advertisement authority, and the handler continues to re-check its one-shot capability so a stale/direct invocation cannot widen scope.

### 5.2 Egress policy

The Covenant classifier still freezes and canonicalizes the complete tool call and produces the exact name, argument, sensitivity, and tool-input digests.

`CovenantEgressWardPolicy.Resolve` becomes a four-outcome policy:

| Classification | Authorization |
|---|---|
| not a Covenant mutation | `NotSensitive` |
| invocation cannot stage a Covenant mutation | `DeniedIneligibleTurn` |
| `propose_covenant` | `SensitivePayloadOnly` |
| `retire_covenant` | `UngatedRetirement` |

There is no Ward-settings input, prompt-required flag, configured-auto-approval mode, disabled-Ward denial, or acceptance method. The class-level remarks state the reversal: installing, enabling, and admitting the Covenant tool catalog is the authorization to execute the advertised tool; the host records rather than asks again.

### 5.3 Pipeline ordering

For every provider-issued tool call that reaches `ExecuteToolCallWithWardAsync`, the pipeline first records one `Warded` / `WardResolved` pair through `RecordUngatedWardResolutionAsync`. The record has origin `Ungated`, never creates an active Ward, never raises `ToolApprovalRequestedEvent`, and increments `WardDecisionsTotal` once.

Ordinary tools then continue to Sanctum and invocation as they do after #217. `retire_covenant` takes the following additional preparation path, without a decision point:

1. Require the live staging ambient, invocation context, and egress guard.
2. Classify the frozen tool call.
3. Resolve `CovenantEgressWardPolicy`; refuse only `DeniedIneligibleTurn`.
4. Parse the key and lane.
5. Resolve the canonical `CovenantRetirementPreflight`, including pinned-head, lifecycle, revision, and key-epoch checks.
6. Mint the call nonce.
7. Build `CovenantToolEgressAttempt` with the existing Process destination, preflight destination identity, admission/sensitivity evidence, and a null Ward evidence digest.
8. Commit the disclosure through `CovenantToolEgressGuard.DiscloseThenAsync`.
9. Push a staging context carrying the exact preflight and nonce, then invoke through Sanctum.

A malformed, ineligible, missing, stale, tombstoned, pinned, or otherwise invalid target still fails before the effect. The absence of a Ward is not permission to bypass preflight or write authority.

### 5.4 One-call capability

`CovenantToolStagingContext` and `CovenantToolInvocationContext` remove their Ward-receipt member. Their retirement invariant becomes:

- `retire_covenant` requires one resolved `CovenantRetirementPreflight`;
- `propose_covenant` carries no retirement preflight;
- both remain bound to one connection/request id, one random nonce, one producing admission, one Campaign, one tool call id, and one collector branch.

`SessionAttachmentAmbientSend.BindCovenantStaging` checks the preflight/tool pairing and reuses the nonce already bound into the disclosure attempt. The handler still validates that the key/lane it receives names the exact preflight target before staging.

## 6. Durable mutation and compatibility

`CovenantAgentMutationFactory.Retire` requires a live retirement preflight but no Ward receipt. It keeps `CovenantMutationKind.AgentRetire` and the persisted origin code `3`, binds the preflight digest, and creates authorization with:

- `WardReceiptDigest = null`;
- `Mode = CovenantAuthorizationMode.None`;
- every existing request, plan, admission, target, key-epoch, and tool-input binding unchanged.

`CovenantMutationIntent` accepts the no-receipt/`None` tuple for `AgentRetire`. It continues to reject Ward evidence on other mutation kinds and continues to enforce the exact kind/origin/lane relationships.

`CovenantMutationKernel` writes `AuthorizationModeCode = NULL` when the mode is `None`; it must not bind numeric zero into a nullable column whose persisted vocabulary is `1..3`.

Historical rows with a non-null Ward digest and authorization mode `2` or `3` remain valid and unchanged. The live factory produces neither shape after #218.

## 7. Covenant canonical schema version 3

SQLite cannot alter a table `CHECK`, so editing only the head `covenant_versions.sql` would make fresh installations work while every upgraded installation failed at finalization. Version 3 therefore rebuilds `covenant_versions` atomically and declares no backfill.

Before changing the head object, implementation records the currently published Covenant canonical version-2 source-definition fingerprint in `GrimoireSchemaVersionChains.SourcePins[(CovenantCanonical, 3)]` and adds a version-2 reconstruction fixture that proves the pin.

The version-3 transition executes in one Covenant-canonical transaction:

1. enable deferred foreign-key checking for the transaction;
2. create a replacement `covenant_versions` table with the head column order, foreign keys, and constraints;
3. copy every existing row and every column byte-for-byte;
4. drop the original table;
5. rename the replacement to `covenant_versions`;
6. recreate the table's indexes and append-only guard triggers character-for-character from the head catalog.

The revised authorization constraint accepts exactly these durable shapes:

- origin code `3` with both Ward fields null, for new ungated retirements;
- origin code `3` with a non-null 32-byte Ward digest and authorization mode `2` or `3`, for historical Ward-backed retirements;
- every other origin with both Ward fields null.

The transaction commit performs deferred foreign-key validation. Head-manifest inspection then proves the evolved catalog equals a fresh version-3 catalog before schema metadata advances. Tests seed a real version-2 database through production installers, preserve historical Ward-backed and non-Ward rows across the upgrade, verify all foreign keys and indexes, and then publish a receipt-free retirement through the production mutation kernel.

## 8. Error and observability contract

- `DeniedWardsDisabled` and its message disappear.
- No reachable failure mentions missing operator consent or a Ward receipt.
- `DeniedIneligibleTurn` remains content-free and is the sole Covenant egress authorization denial.
- Existing target, pinned-head, capability, disclosure-journal, Sanctum, and mutation-publication errors retain their codes and behavior.
- `GetActiveWards` stays empty during retirement because the record transition never enters `_pending`.
- A retirement contributes one `WardDecisionsTotal` sample with origin `ungated` and one record pair with unchanged field shapes.

## 9. Documentation

This slice updates direct Covenant-retirement claims in:

- `docs/Arcanum.DESIGN.md` §§10.2, 10.7, 10.14, 10.22/10.26, and 11.14 so no section retains the reversed fail-closed rule;
- `docs/Compendium.README.md` Covenant/Features overview and Ward-setting descriptions so Ward toggles and auto-approval no longer claim to govern retirement.

The broad README, API, CLI, Command Center, and repository-wide Ward terminology sweep remains #221 unless a touched contract would otherwise be factually false. #218 adds no endpoint, CLI option, configuration key, or JSON response shape.

## 10. TDD and verification

Implementation proceeds in independent RED-GREEN-REFACTOR slices:

1. policy and invocation eligibility;
2. retirement advertisement and capability shape;
3. pipeline unattended/disabled-Ward execution and ungated record pair;
4. receipt-free mutation intent and persistence;
5. version-2 to version-3 schema evolution;
6. documentation and contract cleanup.

Each production change begins with a focused test that fails for the expected pre-change reason. Existing tests for canonical name/argument identity, different-target preflight digests, key/lane mismatch, Campaign-only capability scope, pinned-head refusal, admission binding, disclosure-before-effect, egress destination codes, Sanctum, and #217 ordinary ungated calls remain unmodified as invariant canaries.

The slice is not complete until all of the following pass on the merged `remove-wards` tree with zero warnings and zero errors:

- focused #218 and schema-evolution tests;
- no-incremental solution build with warnings as errors;
- full Arcanum, Compendium, and The Forge test suites;
- coverage threshold gate;
- Native AOT/first-party IL warning gate and runtime-regex smoke;
- native SQLCipher verification for `osx-arm64`;
- documentation review, schema manifest/version-chain tests, and `git diff --check`.

Only then may the implementation branch be merged and deleted, `remove-wards` pushed, and #218 marked Done. Epic #197 remains open.
