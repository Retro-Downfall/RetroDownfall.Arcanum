# Issue #230 Annals Default-On Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ordinary Saga and Lexicon history default on while preserving explicit opt-out, mandatory evidence for state-changing Saga curation, idempotent curation no-ops, fail-closed transactions, existing reads and erasure, and the deliberate absence of migration/backfill work.

**Architecture:** Keep `FeatureSettings.Annals` as the only policy input and retain the two existing ordinary-write conditionals in `SagaMemoryStore` and `LexiconService`. Change the code-owned default with one initializer, prove that default through both source-generated configuration paths and real store behavior, add real-SQLite failure injection to characterize atomicity, then align every live owning document and description. Do not change schema, API, CLI, Annals writers, or transaction structure.

**Tech Stack:** .NET 10, C# 13, xUnit, Microsoft.Extensions.Configuration source generation, source-generated `System.Text.Json`, EF Core, Microsoft.Data.Sqlite/SQLCipher, Avalonia Compendium metadata, Markdown, Bash, Git, Native AOT.

**Spec:** `docs/superpowers/specs/2026-08-30-issue-230-annals-default-on-design.md`

## Global constraints

- Work only on `codex/issue-230-annals-default`, based on tracked `remove-wards` commit `178e43dc78a28a22dc627ecc3ae3070f356156dd`. Do not modify `main`.
- Preserve these unrelated pre-existing untracked files exactly and never stage them:
  - `docs/superpowers/plans/2026-08-30-issue-221-ward-documentation-qualification 2.md`
  - `docs/superpowers/specs/2026-08-30-issue-221-ward-documentation-qualification-design 2.md`
- `FeatureSettings.Annals` remains a mutable `{ get; set; }` property. The configuration binding generator silently skips `init`-only members.
- An omitted Annals key means `true`; an explicit `false` remains authoritative.
- Retain the `if (options.CurrentValue.Features.Annals)` checks in ordinary Saga insertion and Lexicon upsert. Do not make those writers unconditional.
- State-changing correction, retirement, and reinstatement remain ungated Annals writers even when automatic history is explicitly off. Existing idempotent outcomes still append nothing.
- A required claim and its subject mutation commit together or not at all. Do not introduce best-effort history, exception swallowing in Saga, a repair queue, or partial success.
- There are no current Arcanum users and no migration is necessary. Do not change SQL schema assets, schema versions, `MemoryAnnalsBackfill`, or version-chain registration.
- Existing unclaimed subjects remain valid; existing history remains readable after explicit disable; erasure remains ungated and atomic.
- Do not add or change API routes, CLI commands, payloads, JSON wire types, or Lexicon history reads.
- Dated reviews and `docs/superpowers/**` plans/specifications are historical artifacts. Do not rewrite them.
- Follow strict TDD for the one behavior change: write every default-dependent consumer test first, run the focused cluster and record the intended RED result, then add the initializer and record GREEN.
- Characterization tests for already-correct atomicity/no-op behavior need not be forced RED. Each must exercise the real SQLCipher store and name the realistic mutation it catches.
- Use `rg --no-config` or `RIPGREP_CONFIG_PATH=/dev/null`, and `--disable-build-servers -m:1` for .NET commands.
- Run focused RED/GREEN and finding-specific commands as needed. Run the complete locally applicable qualification matrix only once after review; do not rerun a green full suite.
- Integration into `remove-wards`, push, issue/project completion, and parent-issue handling are not part of this plan unless the user separately authorizes those external delivery actions after seeing the verified branch.

## File and responsibility map

- `src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs` — the sole code-owned Annals default and exact XML policy contract.
- `tests/RetroDownfall.Arcanum.Tests/Configuration/ArcanumSettingsBindingTests.cs` — generated `Microsoft.Extensions.Configuration` binder default/override contract.
- `tests/RetroDownfall.Arcanum.Tests/Configuration/ConfigurationBootstrapperTests.cs` — actual persisted source-generated JSON loading path.
- `tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs` — real Saga default write-through, explicit-off behavior, and subject/claim rollback.
- `tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs` — real Lexicon default write-through, explicit-off behavior, and typed rollback failure.
- `tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs` — state-changing curation evidence, idempotent outcomes, and curation rollback.
- `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs` — operator-facing setting description.
- `src/RetroDownfall.Arcanum.Core/Weave/ISagaCurationService.cs` and `SagaCurationContracts.cs` — read semantics based on claim existence, not the current feature value.
- `README.md` — high-level default-on and explicit automatic-history opt-out orientation.
- `docs/Arcanum.DESIGN.md` — authoritative default, failure, read, curation, retention, historical upgrade, and no-new-backfill policy.
- `docs/Compendium.README.md` — complete configuration contract.
- `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs` and `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs` — comment-only clarification of intentionally unclaimed fixtures.

---

### Task 1: Drive and document the default-on policy through every real seam

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Configuration/ArcanumSettingsBindingTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Configuration/ConfigurationBootstrapperTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs`
- Modify after RED: `src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs`
- Modify after GREEN: `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs`
- Modify after GREEN: `src/RetroDownfall.Arcanum.Core/Weave/ISagaCurationService.cs`
- Modify after GREEN: `src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs`
- Modify after GREEN: `README.md`
- Modify after GREEN: `docs/Arcanum.DESIGN.md`
- Modify after GREEN: `docs/Compendium.README.md`
- Modify comments after GREEN if stale: `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs`
- Modify comments after GREEN if stale: `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs`

**Interfaces:**
- Consumes: `FeatureSettings`, `ArcanumSettingsBindingTests.BindArcanum`, `ConfigurationBootstrapper.LoadArcanumSettingsFile`, `ISagaMemoryStore.InsertAsync`, and `ILexiconService.UpsertAsync`.
- Produces: default `Annals == true`, missing-key `true`, explicit-false `false`, a default `AgentExtracted` Saga claim, and a default `AgentAsserted` Lexicon claim.

- [ ] **Prerequisite: Restore once before the RED run**

```bash
dotnet restore RetroDownfall.Arcanum.slnx --disable-build-servers -m:1
```

Expected: PASS. Subsequent focused commands use `--no-restore` so dependency resolution cannot obscure their intended result.

- [ ] **Step 1: Add the generated-binder contract**

Add a sibling of the Covenant default test in `ArcanumSettingsBindingTests`:

```csharp
[Fact]
public void Annals_feature_defaults_true_and_binds_explicit_false_through_generated_configuration()
{

    Assert.True(new FeatureSettings().Annals);

    Assert.True(BindArcanum("""{"Arcanum":{"features":{}}}""").Features.Annals);

    Assert.False(
        BindArcanum("""{"Arcanum":{"features":{"annals":false}}}""").Features.Annals);

}
```

This test exercises the test project's enabled configuration binding generator, not reflection binding. The three literal assertions independently cover construction, omission, and explicit override.

- [ ] **Step 2: Add the persisted-file contract**

Add a two-row theory near `LoadArcanumSettingsFile_UsesSourceGeneratedModelAndPricingShapes` in `ConfigurationBootstrapperTests`. Each row writes its JSON into `_workspace.Root`, calls `ConfigurationBootstrapper.LoadArcanumSettingsFile(path)`, and compares `settings.Features.Annals` to a literal expected Boolean:

```csharp
[Theory]
[InlineData("""{"Arcanum":{"features":{}}}""", true)]
[InlineData("""{"Arcanum":{"features":{"annals":false}}}""", false)]
public void LoadArcanumSettingsFile_preserves_the_Annals_default_and_explicit_override(
    string json,
    bool expected)
```

Use a deterministic file name under the fixture workspace and the existing source-generated loader. Do not use `JsonSerializer` without `ConfigurationJsonContext` and do not add an environment variable to the test.

- [ ] **Step 3: Make one Saga write prove the default rather than an explicit test override**

In `SagaAnnalsWriteThroughTests`, change `An_inserted_memory_receives_a_claim_asserting_the_content_that_was_stored` to call `CreateStore()` with no Boolean. Keep every existing explicit-on and explicit-off call.

Refactor the test helper without hiding the decision:

```csharp
private ISagaMemoryStore CreateStore() => CreateStore(new FeatureSettings());

private ISagaMemoryStore CreateStore(bool annals) =>
    CreateStore(new FeatureSettings { Annals = annals });

private ISagaMemoryStore CreateStore(FeatureSettings features) =>
    new SagaMemoryStore(
        _db!,
        new WeaveIndexAvailability(),
        new TestOptionsMonitor<ArcanumSettings>(
            new ArcanumSettings
            {
                Features = features,
                Integrations = new IntegrationSettings
                {
                    Embeddings = new EmbeddingIntegrationSettings { Dimensions = TestDimensions },
                },
            }));
```

The existing claim assertions remain unchanged and therefore test the real store side effect, not the helper.

- [ ] **Step 4: Make one Lexicon write prove the default**

In `LexiconAnnalsWriteThroughTests`, change `A_first_upsert_asserts_revision_one` to call `CreateService()` with no Boolean. Retain all explicit-on and explicit-off calls.

Use the analogous transparent overloads:

```csharp
private ILexiconService CreateService() => CreateService(new FeatureSettings());

private ILexiconService CreateService(bool annals) =>
    CreateService(new FeatureSettings { Annals = annals });

private ILexiconService CreateService(FeatureSettings features) =>
    new LexiconService(
        _db!,
        NullLogger<LexiconService>.Instance,
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = features }));
```

- [ ] **Step 5: Run the complete default-dependent cluster and record RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~ArcanumSettingsBindingTests|FullyQualifiedName~ConfigurationBootstrapperTests|FullyQualifiedName~SagaAnnalsWriteThroughTests|FullyQualifiedName~LexiconAnnalsWriteThroughTests'
```

Expected: FAIL only in the new/converted default-dependent cases because the current implicit Boolean default is false. The explicit-false Saga/Lexicon tests and unrelated configuration tests must remain green. Record failing test names and assertion evidence in the task report.

- [ ] **Step 6: Apply the minimal production change**

Change only the property declaration in `FeatureSettings`:

```csharp
public bool Annals { get; set; } = true;
```

Do not touch the two ordinary-store conditionals.

- [ ] **Step 7: Re-run the Step 5 command and record GREEN**

Expected: PASS. Confirm the mutation check:

- removing the initializer fails configuration plus real-store default tests;
- ignoring explicit false fails the binder override and the existing off-path store tests;
- removing either ordinary-write conditional fails that store's explicit-off test.

- [ ] **Step 8: Update every owning description before committing the behavior**

In `PublicConfigurationSettings.cs`, replace the default-off remarks with all of these facts:

- default true and omitted configuration records ordinary Saga/Lexicon history;
- explicit false stops only future automatic `AgentExtracted`/`AgentAsserted` claims;
- state-changing curation still records evidence, while idempotent outcomes write nothing;
- prior unclaimed rows remain valid and later enablement is prospective only;
- schema-v3 was a one-time historical sweep and this policy change adds no rerun;
- required claim failure rolls back the subject write;
- reads and erasure are independent of the current setting;
- `{ get; set; }` remains required for generated binding.

Update the `features.annals` Compendium descriptor to say default on, explicit automatic-history opt-out, existing-history readability, and mandatory state-changing curation evidence. Change `ISagaCurationService.ShowAsync` and `SagaMemoryDetail` XML from “when the Annals is enabled” to “when a claim exists.”

- [ ] **Step 9: Update the canonical product documentation**

Update:

- `README.md` Annals overview: default on, ordinary write-through, explicit automatic-history opt-out, fail-closed transaction, existing-history reads, and no retroactive claim;
- `README.md` Ward-removal heading: stop presenting #230 as pending while keeping Annals independent of Ward execution;
- `docs/Compendium.README.md` Features entry: replace “default off” and “no claim by any writer” with the automatic/curation split and historical-only backfill;
- `docs/Arcanum.DESIGN.md` §21.4 degradation row: `false` means explicit opt-out, not default;
- DESIGN §21.5 and §21.12: unclaimed rows remain first-class, rows written during explicit opt-out receive no later claim, schema-v3 backfill remains historical, and no new migration/backfill exists;
- DESIGN write-through/curation: ordinary gates remain, state-changing curation and erasure remain ungated, no-op curation writes nothing, and required claim failure rolls back the subject;
- DESIGN reads: claim existence, not the current toggle, controls whether history is present.

Do not rewrite `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`, or dated `docs/superpowers` artifacts. Clarify live test comments that describe intentionally explicit-false fixtures only when they are stale.

- [ ] **Step 10: Validate code-owned descriptions without repeating the runtime cluster**

```bash
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~SettingDescriptorCoverageTests|FullyQualifiedName~SettingDescriptorParityTests'
git diff --check
```

Expected: PASS. This compiles the changed Core/Compendium description sources and verifies descriptor coverage; the Task 1 GREEN run already proved runtime behavior.

- [ ] **Step 11: Commit one coherent behavior-and-documentation change**

```bash
git add src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs src/RetroDownfall.Arcanum.Core/Weave/ISagaCurationService.cs src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs README.md docs/Arcanum.DESIGN.md docs/Compendium.README.md tests/RetroDownfall.Arcanum.Tests/Configuration/ArcanumSettingsBindingTests.cs tests/RetroDownfall.Arcanum.Tests/Configuration/ConfigurationBootstrapperTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs
git commit -m "feat: default Annals history on"
```

Stage only files that actually changed. Never stage the two unrelated untracked duplicates.

---

### Task 2: Characterize fail-closed ordinary memory writes with real SQLite failures

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs`

**Interfaces:**
- Consumes: the real SQLCipher-backed `SagaMemoryStore` and `LexiconService`, with a connection-local SQLite trigger that aborts `annal_claims` insertion.
- Produces: proof that Saga propagates the claim failure and commits no subject rows, while Lexicon returns `Lexicon.WriteFailed` and commits no subject rows.

- [ ] **Step 1: Add Saga transaction-failure characterization**

Add `A_claim_failure_rolls_back_the_Saga_memory` using the default-on `CreateStore()`:

1. open the fixture's shared connection with the existing `OpenAsync`;
2. on that connection create `CREATE TEMP TRIGGER fail_saga_annals BEFORE INSERT ON main.annal_claims BEGIN SELECT RAISE(ABORT, 'forced Saga Annals failure'); END;`;
3. call `InsertAsync`, assert `SqliteException` escapes with base `SqliteErrorCode == 19`, and assert its message contains the unique trigger text;
4. query literal outcomes: zero matching `saga_memories`, zero `saga_memory_embeddings`, and zero `annal_claims`.

Keep the trigger connection-local and the database fixture real. Do not mock `AnnalsClaimWriter` or add a production injection seam.

- [ ] **Step 2: Add Lexicon transaction-failure characterization**

Add `A_claim_failure_rolls_back_the_Lexicon_entry_and_reports_write_failed`:

1. open the shared connection and create `CREATE TEMP TRIGGER fail_lexicon_annals BEFORE INSERT ON main.annal_claims BEGIN SELECT RAISE(ABORT, 'forced Lexicon Annals failure'); END;`;
2. call default-on `CreateService().UpsertAsync`;
3. assert `IsFailure` and `ErrorCodes.Lexicon.WriteFailed`;
4. assert zero matching `lexicon_entries` and zero `annal_claims`.

This catches moving `COMMIT` before the claim, removing rollback, or treating history as best-effort.

- [ ] **Step 3: Run the two write-through classes**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~RetroDownfall.Arcanum.Tests.Annals.SagaAnnalsWriteThroughTests|FullyQualifiedName~RetroDownfall.Arcanum.Tests.Annals.LexiconAnnalsWriteThroughTests'
```

Expected: PASS. These are characterization tests for an approved existing guarantee, so a new failure means investigate the real transaction behavior with `superpowers:systematic-debugging`; do not alter the policy to make the test pass.

- [ ] **Step 4: Commit the ordinary-write atomicity contract**

```bash
git add tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs
git commit -m "test: prove Annals write atomicity"
```

---

### Task 3: Characterize state-changing curation evidence, idempotent no-ops, and rollback

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs`

**Interfaces:**
- Consumes: the real `SagaStoreHarness` with `Annals = false`, `RetireAsync`, `ReinstateAsync`, `CorrectAsync`, `IAnnalsStore`, and a connection-local abort trigger.
- Produces: exact proof that state changes write evidence despite explicit false, repeated no-ops add no revision, and a required curation-claim failure rolls back the subject mutation.

- [ ] **Step 1: Strengthen retirement and reinstatement no-op assertions**

Keep `Retirement_and_reinstatement_write_annals_history_even_when_the_feature_is_off` and `Correction_writes_annals_history_even_when_the_feature_is_off` unchanged as the state-changing opt-out evidence.

Extend `Retiring_twice_is_refused_rather_than_recorded_twice` to read the subject's claim/version count after the first retirement, perform the second retirement, and prove both the current revision and version count are unchanged.

Add or extend a reinstatement test so that a second reinstatement returns `NotRetired` and leaves the post-reinstatement claim revision/version count unchanged.

Keep `Correcting_to_the_text_already_stored_writes_nothing` as the claim-bearing correction no-op case, and add one complementary explicit-false case over a claimless row. It must assert `Unchanged`, identical content and embedding bytes, and no `annal_claims` row. This catches a regression that opens an `AgentExtracted` claim before recognizing an identical correction while automatic history is opted out.

- [ ] **Step 2: Add a representative curation claim-failure rollback test**

Add `A_claim_failure_rolls_back_a_correction_even_when_automatic_history_is_off`:

1. create a harness with `annalsEnabled: false` and insert a claimless memory;
2. record its content and embedding bytes;
3. on the shared connection create `CREATE TEMP TRIGGER fail_curation_annals BEFORE INSERT ON main.annal_claims BEGIN SELECT RAISE(ABORT, 'forced curation Annals failure'); END;`;
4. call state-changing `CorrectAsync`, assert `SqliteException` with base `SqliteErrorCode == 19`, and assert its message contains the unique trigger text;
5. prove the original content and embedding remain, and no claim/version row was committed.

This path intentionally fails while the curation method tries to reconstruct the initial `AgentExtracted` assertion before appending its operator correction. It therefore proves both that curation ignores the automatic-history opt-out and that its subject/claim transaction fails closed.

- [ ] **Step 3: Run the focused curation class**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~RetroDownfall.Arcanum.Tests.Data.SagaCurationStoreTests'
```

Expected: PASS. Mutation check: gating curation on Annals false, appending duplicate no-op versions, or committing before the forced claim failure must each break at least one test.

- [ ] **Step 4: Commit the curation characterization**

```bash
git add tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs
git commit -m "test: preserve mandatory Annals curation evidence"
```

---

### Task 4: Complete a bounded semantic sweep without repeating tests

**Files:**
- Inspect: `README.md`, `docs/Arcanum.DESIGN.md`, `docs/Compendium.README.md`, `src/**`, and live `tests/**` comments.
- Modify only a Task 1 owning file when the sweep proves a residual stale claim.

**Interfaces:**
- Produces: one classified search result showing no live default-off/all-writers/read-gated claim remains outside historical `docs/superpowers` artifacts.

- [ ] **Step 1: Run the semantic stale-claim sweep**

```bash
rg --no-config -n -i 'Annals.{0,100}(default off|off.{0,20}default|when enabled|gate off)|default off.{0,100}Annals|no claim is appended by any writer|turning it off stops new records|when the Annals is enabled|every store behaves exactly as it does without the feature' README.md docs src tests --glob '!docs/superpowers/**'
```

Expected: no stale live claim. Classify every surviving match as an explicitly-false fixture, a historical schema-v3 statement, or a correct claim-existence/read statement. Do not edit dated `docs/superpowers` artifacts.

- [ ] **Step 2: Correct only proven residual wording**

If the sweep finds a real stale claim, correct it in its owning Task 1 file and run `git diff --check`. Human prose is reviewed semantically rather than pinned by an exact-string change-detector test.

- [ ] **Step 3: Check only the resulting diff**

Run `git diff --check`. Do not rerun Task 1's configuration/write cluster, Task 1's Compendium metadata tests, or Task 2–3 characterization classes after documentation/comment-only work; final qualification supplies the one later complete suite. If a residual correction changes C# rather than prose, run only the narrowest affected compile/test before committing it.

- [ ] **Step 4: Commit only if the sweep found a residual correction**

```bash
git add README.md docs/Arcanum.DESIGN.md docs/Compendium.README.md src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs src/RetroDownfall.Arcanum.Core/Weave/ISagaCurationService.cs src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs
git diff --cached --quiet || git commit -m "docs: complete Annals policy sweep"
```

Stage only a file with a real residual correction. Never stage the unrelated untracked duplicates.

---

### Task 5: Perform one bounded independent whole-branch review

**Files:**
- Review: `git diff 178e43dc78a28a22dc627ecc3ae3070f356156dd..HEAD`
- Modify only files already named in Tasks 1–4 when a verified finding requires it.

**Interfaces:**
- Produces: one read-only review report covering issue alignment, TDD honesty, configuration ownership, explicit-off behavior, curation/no-op boundaries, transaction failure, documentation consistency, absence of migration, and scope containment.

- [ ] **Step 1: Invoke `superpowers:requesting-code-review`**

Give one fresh reviewer this exact scope:

```text
Review issue #230 only, comparing base 178e43dc78a28a22dc627ecc3ae3070f356156dd to HEAD. Check the approved spec and plan; strict RED/GREEN evidence for the default change; generated binder and persisted JSON behavior; default and explicit-false Saga/Lexicon writes; real transaction rollback tests; state-changing curation evidence and idempotent no-ops; existing-history reads and erasure; no schema/version/backfill/API/CLI changes; live documentation consistency; Native AOT/config-binding compatibility; and preservation of unrelated untracked files. Report only Critical, Important, or Minor findings with file/line evidence. Do not edit files and do not spawn another reviewer.
```

- [ ] **Step 2: Resolve findings with evidence**

For each Critical or Important finding:

1. reproduce it with the narrowest existing or new behavior test;
2. if observable behavior changes, start with a failing test;
3. apply the smallest in-scope correction;
4. rerun only that focused gate;
5. commit the disposition.

Accept Minor wording changes only when they remove a real ambiguity. Do not start an open-ended review loop.

- [ ] **Step 3: Record review disposition**

Record reviewer identity, base, HEAD, findings, and resolutions. An unresolved Critical or Important finding blocks qualification.

---

### Task 6: Run the complete locally applicable qualification matrix once

**Files:**
- Inspect: `scripts/coverage.sh`
- Inspect: `scripts/verify-aot-il-warnings.sh`
- Inspect: `scripts/verify-native-sqlcipher.sh`
- Inspect: `scripts/packaging/macos/common_test.sh`
- Verify: complete reviewed feature tree

**Interfaces:**
- Produces: one fresh recorded set of Release, full-suite, coverage, static, Native AOT/IL, packaging, and native-provenance evidence for the reviewed HEAD.

- [ ] **Step 1: Reconfirm wrapper behavior**

```bash
sed -n '1,220p' scripts/coverage.sh
sed -n '1,260p' scripts/verify-aot-il-warnings.sh
sed -n '1,220p' scripts/verify-native-sqlcipher.sh
sed -n '1,220p' scripts/packaging/macos/common_test.sh
```

Confirm `coverage.sh --threshold` supplies the one complete non-Perf Arcanum suite, and the AOT wrapper publishes the current `osx-arm64` closure and runs regex smoke. Do not run a second unfiltered Arcanum suite.

- [ ] **Step 2: Validate and clear only generated Native AOT object directories**

Validate the repository and exact generated targets. If present, request destructive-action approval and remove only:

```text
/Users/mat/Documents/Source/apps/RetroDownfall.Arcanum/src/RetroDownfall.Arcanum.Cli/obj/Release/net10.0/osx-arm64/native
/Users/mat/Documents/Source/apps/RetroDownfall.Arcanum/tests/RetroDownfall.Arcanum.RegexAotSmoke/obj/Release/net10.0/osx-arm64/native
```

Never broaden the target or remove source-controlled files.

- [ ] **Step 3: Run the clean zero-warning Release build**

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --no-incremental --no-restore --disable-build-servers -m:1 -warnaserror
```

Expected: exit 0, zero errors, zero warnings.

- [ ] **Step 4: Run coverage and the one full non-Perf Arcanum suite**

```bash
python3 -m unittest scripts/coverage_threshold_test.py
./scripts/coverage.sh --threshold
```

Expected: PASS. Record exact pass/fail/skip counts and tiered line/branch thresholds from the wrapper.

- [ ] **Step 5: Run the deliberately excluded Perf category and remaining first-party suites once**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --disable-build-servers -m:1 --filter 'Category=Perf'
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
```

Expected: PASS. Record exact test counts.

- [ ] **Step 6: Run packaging, gate-unit, fresh AOT/IL, and native provenance checks**

```bash
./scripts/packaging/macos/common_test.sh
./scripts/verify_aot_il_warnings_test.sh
RIPGREP_CONFIG_PATH=/dev/null ./scripts/verify-aot-il-warnings.sh osx-arm64
RIPGREP_CONFIG_PATH=/dev/null ./scripts/verify-native-sqlcipher.sh --rid osx-arm64
```

Expected: PASS. Record whether macOS used full Native AOT or the documented folder-publish fallback, regex-AOT smoke, and SQLCipher provenance.

- [ ] **Step 7: Run repository static gates and prove tracked cleanliness**

```bash
python3 scripts/align_csharp_blanklines.py --repo . --check
find scripts -name '*.sh' -print0 | xargs -0 shellcheck -x -P SCRIPTDIR
actionlint
git diff --check 178e43dc78a28a22dc627ecc3ae3070f356156dd..HEAD
git diff --exit-code
git diff --cached --exit-code
git status --short --branch
```

Expected: all commands exit 0. The two known unrelated untracked duplicates may remain listed; no tracked edit may remain. If a formatter changes tracked files, inspect, commit, rerun only the affected focused/static gate, and perform a bounded diff review of that change before delivery.

- [ ] **Step 8: Record intentionally inapplicable gates**

State that production signing/notarization, Windows/Linux packaging lanes, and operator keychain/jail integration were not locally applicable unless a current wrapper actually ran them. Do not claim unrun gates passed.

- [ ] **Step 9: Present the verified feature branch without external delivery claims**

Report:

- feature branch and verified HEAD/tree id;
- RED/GREEN evidence;
- review disposition;
- exact qualification results;
- no schema/migration/API/CLI changes;
- preservation of the two unrelated untracked files;
- integration, push, issue #230 completion, and #197 handling still awaiting explicit authorization.

Do not switch branches, merge, push, delete the feature branch, or change GitHub issue/project state in this task.
