# Issue #230: Annals Default-On Policy

**Status:** Approved design, pending implementation.

**Branch:** `codex/issue-230-annals-default`, cut from the tracked `remove-wards` aggregation branch at `178e43dc78a28a22dc627ecc3ae3070f356156dd` after completion of issue #221.

**Issues:** Delivery slice #230 under epic #197. This is the remaining Annals policy follow-on identified during the Ward-removal documentation qualification. It changes memory-history defaults, not Ward behavior.

## 1. Objective

Record ordinary Saga and Lexicon memory history by default while preserving an explicit operator opt-out, the mandatory audit trail for state-changing manual curation, and the existing transactional guarantee that a memory mutation and its required Annals claim either commit together or do not commit.

The policy is intentionally prospective. There are no current Arcanum users and no migration is necessary. Existing unclaimed memories remain valid, readable records; this slice does not add a schema version, replay prior writes, or create synthetic claims.

## 2. Approved decisions

### 2.1 Default and opt-out

`Arcanum:Features:Annals` defaults to `true` in the code-owned `FeatureSettings` configuration object. An omitted setting therefore enables ordinary Annals write-through. An explicit `false` value remains authoritative and disables automatic history for ordinary Saga extraction and Lexicon upsert writes.

The implementation uses a property initializer:

```csharp
public bool Annals { get; set; } = true;
```

It does not add an environment-aware resolver, preset override, compatibility shim, or second configuration key.

### 2.2 Claim-write failure policy

Annals history is part of the transaction whenever a write requires a claim. If claim persistence fails, the associated memory mutation fails or rolls back as it does today:

- Saga extraction leaves its watermark unadvanced so the operation can retry;
- Lexicon reports `Lexicon.WriteFailed`;
- neither path commits the subject without its required claim.

This slice does not introduce best-effort history, a background repair queue, or a partially successful response.

### 2.3 No migration or backfill

No current Arcanum users require migration. The prior schema-v3 Annals backfill remains historical upgrade behavior only. Issue #230 adds no schema version and does not run that backfill again.

Memories that already lack claims remain unchanged and continue to be treated as valid. Enabling Annals later affects future ordinary writes only; it does not infer or manufacture missing history.

### 2.4 Explicit-off boundary

When `Annals` is explicitly `false`:

- ordinary Saga insertion does not append `AgentExtracted` history;
- ordinary Lexicon upsert does not append `AgentAsserted` history;
- existing history remains readable;
- state-changing Saga correction, retirement, and reinstatement still append their evidence;
- subject erasure still removes associated claims atomically.

Existing idempotent curation outcomes remain no-ops: retiring an already retired memory, reinstating a memory that is not retired, or correcting to identical content appends no duplicate evidence. The opt-out governs automatic history, not evidence for an operator-authored state change or retention integrity. Documentation and Compendium copy must use that distinction consistently.

## 3. Current architecture and intended change

### 3.1 Configuration authority

`FeatureSettings.Annals` is the single policy input. Configuration binding preserves the initializer when the key is absent and replaces it when an explicit value is supplied. The property remains mutable with `{ get; set; }` so the Native AOT configuration-binding generator can bind it.

The setting description in Compendium and the owning configuration documentation will state:

- default: on;
- on: future ordinary Saga and Lexicon writes receive history claims;
- off: future automatic claims stop;
- evidence for state-changing manual curation remains mandatory;
- no retroactive claims are created when the setting changes.

### 3.2 Saga ordinary-write flow

The existing `SagaMemoryStore` flow remains structurally unchanged:

1. begin the subject transaction;
2. persist the Saga memory candidate;
3. when `Features.Annals` is true, append its `AgentExtracted` claim in the same transaction;
4. complete dependent work and commit;
5. on failure, do not advance the extraction watermark and do not commit a partial subject.

The code change is the default value feeding the existing conditional. Explicit false continues to exercise the current no-claim branch.

### 3.3 Lexicon ordinary-write flow

The existing `LexiconService` flow also remains structurally unchanged:

1. begin the immediate transaction;
2. insert or update the Lexicon entry;
3. when `Features.Annals` is true, append its `AgentAsserted` claim in the same transaction;
4. commit only after both writes succeed;
5. on failure, roll back and return `Lexicon.WriteFailed`.

No new public Lexicon-history read surface is introduced.

### 3.4 Curation, reads, retention, and erasure

State-changing manual Saga correction, retirement, and reinstatement remain unconditional Annals writers because those claims are the durable evidence for an operator-authored state change. The `Annals` opt-out does not suppress them. Their current idempotent no-op outcomes continue to write neither the subject nor a duplicate claim.

Annals reads remain available regardless of the current setting. Disabling future automatic history cannot hide or delete existing history. Retention continues to follow the subject, and erasure continues to remove the subject's claims in the same transaction.

## 4. Scope

### 4.1 In scope

- Make `FeatureSettings.Annals` default to `true` through a code-owned property initializer.
- Prove that missing configuration retains the default and explicit `false` overrides it.
- Prove default ordinary Saga and Lexicon writes append their expected claims.
- Preserve and prove the explicit-off behavior for ordinary writes.
- Preserve and prove required state-changing curation evidence, plus idempotent curation no-ops, while the automatic-history setting is off.
- Preserve the existing fail-closed transaction behavior.
- Update XML documentation, Compendium setting metadata, `README.md`, `docs/Arcanum.DESIGN.md`, and `docs/Compendium.README.md`.
- Remove the now-completed issue #230 follow-on wording from current product documentation where applicable while preserving dated historical plans and specifications.

### 4.2 Out of scope

- Any Ward, Forbidden Arts, Covenant, tool-advertisement, or tool-execution change.
- A database schema or version-chain change.
- Replaying the schema-v3 backfill or creating a new migration/backfill.
- Synthesizing claims for existing unclaimed memories.
- Removing the `Annals` feature setting or its explicit opt-out.
- Weakening claim-write atomicity or adding best-effort persistence.
- Suppressing curation claims when automatic history is disabled.
- Adding a public Lexicon-history endpoint or CLI command.
- Changing API wire contracts, CLI syntax, source-generated JSON registrations, or Native AOT reachability.

## 5. Considered approaches

### 5.1 Default-on with explicit opt-out — selected

Initialize `FeatureSettings.Annals` to `true` and retain the two existing automatic-write checks.

This is the smallest policy change. It makes the safer provenance behavior the normal path, preserves operator control, and leaves the established transaction and read models intact.

### 5.2 Remove the feature gate — rejected

Delete the checks and make every future automatic claim unconditional.

This would eliminate the requested opt-out and make `Arcanum:Features:Annals` misleading or obsolete. It is broader than the approved policy.

### 5.3 Best-effort history — rejected

Commit the memory subject even when claim persistence fails, then repair history later.

This creates deliberately unprovenanced writes under a default-on policy, changes response semantics, and requires a durable repair design that is outside this issue.

### 5.4 New backfill — rejected

Add a schema transition or startup sweep for unclaimed memories.

There are no current users to migrate. A new sweep would add upgrade complexity and synthetic historical assertions without a product need.

## 6. TDD design

### 6.1 Configuration RED/GREEN cycle

Add focused tests that initially fail because the current implicit Boolean default is false:

- a freshly constructed `FeatureSettings` has `Annals == true`;
- configuration with no `Arcanum:Features:Annals` key binds to true;
- configuration with `Arcanum:Features:Annals=false` binds to false.

Make those tests green with the property initializer only. Do not introduce a second default in test helpers, service registration, or Compendium.

### 6.2 Ordinary Saga write RED/GREEN cycle

Exercise the Saga store with the default `FeatureSettings` rather than explicitly forcing `Annals = true`. The pre-change test must fail because no claim is written. The green result proves that a normal inserted Saga memory receives exactly the expected `AgentExtracted` claim.

Keep the explicit-off test and prove that the subject is stored unchanged without an automatic claim.

### 6.3 Ordinary Lexicon write RED/GREEN cycle

Exercise Lexicon upsert with the default `FeatureSettings`. The pre-change test must fail because no claim is written. The green result proves that a normal upsert receives the expected `AgentAsserted` claim.

Keep the explicit-off test and prove that no automatic claim is appended.

### 6.4 Mandatory curation and atomicity characterization

Retain or add focused characterization tests proving that, with automatic Annals history explicitly off:

- state-changing Saga correction, retirement, and reinstatement continue to append their required claims;
- idempotent curation outcomes append no duplicate claim;
- a claim-write failure prevents the associated ordinary subject mutation from committing;
- a claim-write failure in at least one representative state-changing curation path prevents its subject mutation from committing.

These are preservation tests. If existing coverage already proves the exact invariant, reuse it rather than duplicating it. Any new observable behavior begins with an intentional failing test.

### 6.5 Documentation contracts

Update test-owned documentation expectations where an existing contract still requires default-off or inaccurately says that disabling Annals stops every writer. The revised assertions must distinguish automatic claims from mandatory curation evidence and must reject a reintroduction of the old default.

## 7. Compatibility, safety, and Native AOT

- Existing explicit `false` configurations keep their chosen behavior.
- An omitted key changes from off to on by design.
- Configuration binding stays source-generated and uses no reflection.
- No payload type, endpoint, CLI verb, JSON context, or serialization shape changes.
- No schema asset, SQL statement, encryption boundary, retention rule, or erasure rule changes.
- Claim persistence remains inside the subject transaction, so a default-on deployment cannot silently lose required history.
- Existing unclaimed subjects remain readable and are not treated as corruption.

## 8. Documentation architecture

The implementation updates each owning surface rather than relying on tracker context:

- `PublicConfigurationSettings.cs`: exact default, automatic-write scope, state-changing curation exception, prospective-only behavior, and failure semantics;
- `SettingDescriptors.cs`: concise operator-facing wording for default on and explicit opt-out;
- `docs/Compendium.README.md`: complete configuration contract;
- `docs/Arcanum.DESIGN.md`: normative write, failure, read, curation, retention, and no-backfill policy;
- `README.md`: high-level default and opt-out orientation plus current #197 child accounting.

Dated review, plan, and specification artifacts remain historical and are not rewritten.

## 9. Verification and review

### 9.1 Focused TDD evidence

Record RED and GREEN evidence for each behavior-changing test cluster. Run existing focused Annals, configuration, Saga curation, and transaction tests needed to establish the preserved boundaries.

### 9.2 Independent review

Request one bounded, read-only sub-agent review over the complete feature-branch diff before qualification. The reviewer checks default ownership, explicit-off behavior, state-changing curation evidence and idempotent no-ops, atomicity, absence of migration work, documentation consistency, and test honesty.

Critical or important findings are resolved before qualification. Fixes to observable behavior begin with a failing test.

### 9.3 Final qualification

After review, run the complete locally applicable verification matrix once on the finished feature tree, following the repository wrappers and recording exact results. The qualification must include a clean Release build with zero errors and warnings, the applicable full test/coverage gates, Native AOT/IL verification, native SQLCipher provenance, documentation/static checks, `git diff --check`, and tracked-tree status.

Do not repeat a green full suite. Focused tests may be rerun only as required by their RED/GREEN cycles or to resolve a concrete failure.

## 10. Acceptance criteria

- A default `FeatureSettings` instance has Annals enabled.
- Missing configuration enables automatic Saga and Lexicon history.
- Explicit `false` disables only future automatic Saga and Lexicon claims.
- Default Saga insertion appends the expected `AgentExtracted` claim.
- Default Lexicon upsert appends the expected `AgentAsserted` claim.
- State-changing manual correction, retirement, and reinstatement still append evidence while automatic history is off, while their idempotent outcomes remain no-ops.
- A required claim-write failure cannot leave its subject committed.
- Existing claims remain readable after disable, and existing unclaimed subjects remain valid.
- No schema transition, migration, or backfill is added.
- Product documentation consistently says default on, explicit automatic-history opt-out, mandatory state-changing curation evidence, prospective-only changes, and fail-closed writes.
- The reviewed feature tree completes all locally applicable gates with zero errors and warnings.

## 11. Delivery boundary

Implementation work remains on `codex/issue-230-annals-default` until TDD, review, and qualification are complete. Integration, push, tracker closeout, and parent-issue handling are separate external delivery actions and must follow the authorization in effect at closeout.
