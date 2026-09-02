# Whole-Tree Hardening Remediation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remediate every finding the 2026-09-01 whole-tree review confirmed, with a failing test written first for each, without delivering any of the pending admission and transition integration children, and leave `grimoire-fixes` green under every local gate before it is pushed.

**Architecture:** Findings are grouped into work packets with provably disjoint file ownership. Each packet is executed by one agent in its own git worktree on a branch cut from `grimoire-fixes`, fixes its findings test-first, and is merged back with `--no-ff` after a read-only audit of its whole diff. Two guard-rail inventory tests land first so the repository's two recurring root causes cannot recur silently. Governed documentation is corrected by one packet at the end so the doc tests run once over the assembled tree.

**Tech Stack:** .NET 10, C# 13, xUnit with `SkippableFact`, Microsoft.Data.Sqlite over hermetic SQLCipher, git worktrees, GitHub CLI.

**Spec:** `docs/Arcanum.Review.20260901.md` (the review), with the per-finding detail in `docs/superpowers/plans/2026-09-01-whole-tree-hardening.findings.json` (id, file, line, verdict, failure scenario, suggested fix, and the RED test to write). Every task below names its findings by id; the executor reads the finding's `test_to_write` and `suggested_fix` from that file as the task's concrete content.

## Global Constraints

- Work only on branches cut from `grimoire-fixes`; merge only into `grimoire-fixes`; never touch `main`. Every packet rebases onto the current tip before it is merged and re-runs its focused suite after the rebase; if the tip moves after a packet is merged, the next packet's rebase absorbs it.
- At most five packets run at a time. The temp-directory sweep runs only between waves, never while any packet is testing, because the suite stalls once a few hundred stale working directories accumulate and a sweep during a run corrupts that run.
- Preserve the two untracked duplicate documents in the sibling worktree exactly as found; never stage, edit, delete, or move them.
- Do not activate the admission gate's closing side, the transition journal, or the lifecycle store in any production path, and do not deliver any pending integration child; a fix that requires activation is out of scope and is reported back instead.
- Strict RED → GREEN → REFACTOR for every production change: write the test named in the finding, run it alone and observe it fail for the finding's reason (not a setup error), fix, run it alone and observe it pass, run the packet's focused suite.
- Every test enters through the outermost production entry point the finding names, seeds nothing it asserts, and passes only values a production caller under `src/` supplies; a test that hand-builds the input to the code under test is rejected at review.
- Compensating work — rollback, cleanup, lease surrender, post-commit proof — runs on `CancellationToken.None`.
- Native AOT: no reflection serialization, no anonymous DTOs, every wire type registered on its source-generated context; config POCOs use `{ get; set; }`.
- C# house style: one blank line after each line of code, file-scoped namespaces, positional records for DTOs. Run `./scripts/align-csharp-blanklines.sh --check <changed files>` before every commit.
- No packet edits a file owned by another packet. If a fix needs a foreign file, the packet stops, reports the exact edit, and the integrator sequences it.
- No packet edits `README.md`, `AGENTS.md`, or anything under `docs/` except the Documentation packet; code packets record the doc sentences their change invalidates in their final report.
- Never run `dotnet build` or `dotnet test` while a full-suite or coverage run is in flight anywhere on this machine; before any full-suite run, sweep `tests/RetroDownfall.Arcanum.Tests/TestResults` and the `*arcanum*` and `grimoire-*` entries under `$(getconf DARWIN_USER_TEMP_DIR)`.
- A single red test in a full run is re-run alone three times before it is called a regression; it is a regression only if it fails in isolation and `git log <base>..HEAD -- <testfile> <srcfile>` shows a commit in range touched it.
- Zero build warnings under `dotnet build RetroDownfall.Arcanum.slnx -c Release --no-incremental`.

## Decisions recorded before execution

- The integration branch is frozen for the duration of this plan; the sibling worker's delivery is complete.
- Every self-contained defect this review re-confirmed is fixed here, including the ones that overlap an open tracker item; the architectural ones — the Windows sandbox end-to-end qualification, the attachment orphan-sweep snapshot window, the Windows exclusive-transaction refusal beyond the busy-retry fix, the remaining acquisition-drain children, and the maintenance-unavailable HTTP response — stay with their own work.
- The fictional remediation command is corrected to the real remedy; the transition verb is not registered by this plan.
- Warnings-as-errors is turned on in the shared build properties.
- The findings file stays beside this plan for the plan's duration.
- Packets run in parallel only in separate worktrees with disjoint file ownership, five at a time; no packet edits the Build inventory tests or the allow-list, which the platform-gated task empties after every other packet has merged.

---

## How to read the tasks

Each task is one work packet. A packet lists the finding ids it closes, the files it owns exclusively, and the steps every packet follows. The concrete content of each finding's steps — the exact failing test to write and the exact fix — is the `test_to_write` and `suggested_fix` field of that id in the findings file named in the header, refined by the verifier's `reason` and `worse` fields, which often name the decisive line and a wider blast radius than the finder saw. An executor reads its packet's findings from that file before starting and treats each finding as one RED → GREEN cycle with its own commit.

Packet execution, identical for every task unless the task says otherwise:

- [ ] **Step A: Cut the worktree**

```bash
git -C /Users/mat/Source/apps/RetroDownfall.Arcanum worktree add /private/tmp/arcanum-hardening/<packet> -b hardening/<packet> grimoire-fixes
```

- [ ] **Step B: For each finding id in the packet, in the order listed: write the RED test named by its `test_to_write`, run it alone with `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --filter "FullyQualifiedName~<TestClass>.<TestMethod>"` and confirm it fails for the finding's reason, apply the `suggested_fix` (as narrowed by the verifier), run the same filter and confirm it passes, then commit with a subject of the form `fix: <what changed>` and a body naming the finding id.** For test-honesty findings the "fix" is the honest test, and the mutation named in the finding must be performed once by hand to confirm the new test goes red under it before the mutation is reverted from a copy, never from the index.

- [ ] **Step C: Run the packet's focused suite** — every test class the packet touched plus the classes named in the packet — and the blank-line formatter check over every changed C# file. Both must be clean.

- [ ] **Step D: Report** — the list of finding ids closed, any finding that could not be closed without a foreign file (with the exact edit needed), and every documentation sentence the change invalidates, quoted with file and line, for the Documentation packet.

Before reporting, the packet rebases onto the current `grimoire-fixes` tip (`git rebase grimoire-fixes`) and re-runs Step C, because the branch has moved under this review once already and may again. The integrator (this session) then audits the packet's whole diff read-only — every deleted condition, filter, bounds check, ordering constraint or `await` is guilty until proven innocent, and any test or fixture change that makes a failure disappear is asked whether it made the test realistic or merely quiet — and merges with `git merge --no-ff hardening/<packet>`.

## Phase 0 — Restore a green tip, then smoke detectors

### Task 0: Make the tip green again

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:72-174`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:1115-1137`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:127-128` (that file's other findings belong to Task 9, which runs after this task merges)
- Create: `tests/RetroDownfall.Arcanum.Tests/Support/FixtureOrdinaryConnectionFactory.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Storage/GrimoireTurnCommitterTests.cs:433-438`, `Repositories/MandatoryGrimoireRepositoryTests.cs:1786-1810` and `:1929-1937`, `Hosting/SagaExtractionServiceTests.cs:799`, `Intelligence/CovenantOperatorJourneyTests.cs:1139-1146`, `Intelligence/CovenantBootstrapProposalTests.cs:357-364`, `Api/MemoryEndpointTests.cs:79-81`, `Api/WorkspaceDivinationEndpointTests.cs:326-328`, `Api/Tower/SessionDivinationEndpointTests.cs:309-311`, `Repositories/GrimoireRepositoryTests.cs:1575-1592`, `Intelligence/WizardIntelligenceProviderTests.cs:8306-8312`
- Modify: `docs/Arcanum.DESIGN.md:2426`

**Findings:** R1-1, R1-2 (important, both introduced by the range that landed during the review); the tracker-reference regression at `docs/Arcanum.DESIGN.md:2426`.

**Interfaces:**
- Consumes: `IGrimoireOrdinaryConnectionFactory`, registered as a singleton in both production compositions.
- Produces: a `GrimoireRepository` public constructor whose ordinary-connection factory is a required parameter, so omitting it is a compile error rather than a runtime refusal.

The suite at `c3705d4d` has 42 failures that `a1160e88` did not: 20 in the mandatory-interaction repository tests, 9 in the turn committer tests, 11 in the Covenant journey and bootstrap-proposal tests, 1 thirty-second timeout in the Command Center threading test, and the documentation tracker-reference test. `git bisect` over the 37 landed commits names `32269c2e` (routing scoped Grimoire opens through admission) as the first bad commit. The cause is the repository's own recurring root cause: the range added `IServiceProvider? serviceProvider = null` to the repository's public constructor and, when it is omitted, substitutes a factory that refuses every acquisition with "Ordinary Grimoire connection admission is not configured.", while the same range made that factory load-bearing on the turn-commit path. Production binds the internal constructor with the real factory and is unaffected.

- [ ] **Step 1: Run the three failing classes alone and record the failure text** — `--filter "FullyQualifiedName~GrimoireTurnCommitterTests|FullyQualifiedName~MandatoryGrimoireRepositoryTests|FullyQualifiedName~CovenantOperatorJourneyTests"`. Expected: red with the message above. Run the Command Center threading test alone three times as well; it is not in the Grimoire collection and nothing it composes changed in the range, so if it passes alone it is a flake, not this task's.

- [ ] **Step 2: Remove the silent fallback at its source.** Delete the `IServiceProvider? serviceProvider = null` parameter and the `?? UnavailableOrdinaryConnectionFactory.Instance` fallback from the public constructor (`GrimoireRepository.cs:72-91`); give the internal six-argument constructor (`:98-116`), which hard-codes the unavailable factory and offers no way to pass a real one, an `IGrimoireOrdinaryConnectionFactory` parameter or delete it in favour of the eight-argument serving constructor; delete `UnavailableOrdinaryConnectionFactory` (`:152-174`) and its dormant twin in `SessionRepository.cs:1115-1137`, which even disagrees on the error code; and in `WizardIntelligenceProvider.cs:127-128` replace the optional `GetService` with a required dependency so the missing factory fails at composition rather than six thousand lines later during a live turn. Every hand-construction site then fails to compile instead of receiving a factory that refuses every call.

- [ ] **Step 3: Write one honest fixture double and use it everywhere.** Do not register the production `GrimoireOrdinaryConnectionFactory` in any fixture-based test: its fresh-open path hard-codes `ArcanumPaths.GrimoireDatabaseFile`, the developer's live database, not the fixture copy. Neither existing fake implements both members (the scoped recorder throws from `OpenFreshAsync`, the fresh recorder throws from `AcquireScopedAsync`). Create `tests/RetroDownfall.Arcanum.Tests/Support/FixtureOrdinaryConnectionFactory.cs` implementing both from the fixture database's own connection string: the scoped member opens the passed connection if it is closed and returns a non-owning lease (the behaviour the range deleted from the turn commit), and the fresh member opens an unpooled connection over the fixture path and runs the real `CovenantSqliteConnectionInitializer` (the behaviour the range deleted from the session-entry persistence). Wire it into `MandatoryGrimoireRepositoryTests.cs:1929-1937`, `GrimoireTurnCommitterTests.cs:433-438`, `CovenantOperatorJourneyTests.cs:1139-1146`, `CovenantBootstrapProposalTests.cs:357-364` and the compile-only site at `SagaExtractionServiceTests.cs:799`; then replace the `RemoveAll` substitutions in `MemoryEndpointTests.cs:79-81`, `WorkspaceDivinationEndpointTests.cs:326-328`, `SessionDivinationEndpointTests.cs:309-311` and the fakes at `GrimoireRepositoryTests.cs:1575-1592` and `WizardIntelligenceProviderTests.cs:8306-8312` with the same double, so no test removes a production service from a composed host. Also wrap the body of `MandatoryGrimoireRepositoryTests.cs:1786-1810` in a `try/finally` that cancels and awaits the pending subscription before the `await using` scope exits, so a real failure is reported instead of the async iterator's `NotSupportedException`.

- [ ] **Step 4: Rewrite `docs/Arcanum.DESIGN.md:2426`, which carries two tracker tokens, not one.** Replace the clause with: "The existing V3 same-database path is a temporary, exact-call-site boundary: its operation-specific adapter requires the Covenant exclusive closed lease the ten-phase erasure coordinator already holds, records every use as `LegacyV3Maintenance`, and is retired once Covenant reset and factory erasure become journal-driven." Run `DocumentationIssueReferenceTests` alone; expected green.

- [ ] **Step 5: Run the three classes alone; expected green, including the two turn-committer assertions that expect `Covenant.Unavailable` and `Covenant.IntegrityFailure`, which the pre-flight had been masking as `Grimoire.WriteFailed`.**

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs tests docs/Arcanum.DESIGN.md
git commit -m "fix: require the ordinary connection factory on repository construction"
```

### Task 1: Guard-rail tests for the two recurring root causes

**Files:**
- Create: `tests/RetroDownfall.Arcanum.Tests/Build/NullableInterfaceConstructorDefaultTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Build/CompensationCancellationTokenTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:331-337` and `:1223-1229`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:86-92`
- Test: `tests/RetroDownfall.Arcanum.Tests/Repositories/GrimoireRepositoryTests.cs`

**Findings:** V-1 (blocking).

**Interfaces:**
- Consumes: `ProductionSourceInventory.Sources()` under `tests/RetroDownfall.Arcanum.Tests/Support/`, which yields every `.cs` under `src/` with comments stripped.
- Produces: `CompensationCancellationTokenTests.AllowedSites`, a static `string[]` of `"<relative path>:<method name>"` entries that later packets shrink as they fix each site; the integrator empties it in Task 23.

- [ ] **Step 1: Write the failing inventory of nullable-interface constructor parameters that default to null**

```csharp
using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A constructor parameter of interface type that defaults to <c>null</c> is where production
/// reachability quietly dies: a factory registration that omits it, or a test that omits it, gets a
/// null (or a substitute that refuses every call) and every guard behind it is dead. This inventory
/// names every such parameter under <c>src/</c> and requires each one to be either removed or listed
/// here with the reason it is legitimately optional.
/// </summary>
public sealed class NullableInterfaceConstructorDefaultTests
{

    /// <summary>Parameters that are optional on purpose, each with the reason. Task 0 removes all three.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:serviceProvider"] = "removed by Task 0",

        ["src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:serviceProvider"] = "removed by Task 0",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:serviceProvider"] = "removed by Task 0",
    };

    private static readonly Regex NullableInterfaceDefault = new(
        @"\b(I[A-Z]\w+)\?\s+(\w+)\s*=\s*null\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_nullable_interface_constructor_default_is_removed_or_allowed_with_a_reason()
    {

        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {

            foreach (Match match in NullableInterfaceDefault.Matches(source.Text))
            {

                string key = $"{source.RelativePath}:{match.Groups[2].Value}";

                if (!Allowed.ContainsKey(key))
                {

                    offenders.Add($"{key} is a {match.Groups[1].Value} defaulting to null");

                }

            }

        }

        Assert.Empty(offenders);

    }

}
```

The first run lists every optional interface parameter in the tree; each is either made required in the packet that owns its file, or added to `Allowed` with a one-line reason a reviewer can check. The three seeded entries are the shape that produced the 42-failure regression and its sibling in the intelligence provider.

- [ ] **Step 2: Run it and observe RED** — expected failure names at least `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:labeledArtifactGuard`; every other name it lists is triaged into `Allowed` with a reason or into the owning packet.

- [ ] **Step 3: Write the failing behavioural test through the real container**

```csharp
[SkippableFact]
public async Task Deleting_a_labelled_entry_through_the_composed_repository_is_refused()
{

    Skip.IfNot(GrimoireFixture.SqlCipherAvailable);

    await using GrimoireFixture fixture = await GrimoireFixture.CreateComposedAsync();

    Guid entryId = await fixture.SeedAssistantEntryAsync();

    await fixture.LabelAsync(SensitiveArtifactKind.AssistantEntry, entryId);

    IGrimoireRepository repository = fixture.Services.GetRequiredService<IGrimoireRepository>();

    Result deleted = await repository.DeleteEntryAsync(fixture.SessionId, entryId, CancellationToken.None);

    Assert.True(deleted.IsFailure);

    Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, deleted.Error.Code);

}
```

`GrimoireFixture.CreateComposedAsync` must build its provider through `AddArcanumInfrastructure` so the repository comes from the same factory registration production uses; if the fixture has no such helper, add one that calls the real composition rather than `new GrimoireRepository(...)`.

- [ ] **Step 4: Run it and observe RED** — the delete succeeds today because the guard is null.

- [ ] **Step 5: Make the guard required and supply it in both registrations** — change the constructor parameter to `ICovenantLabeledArtifactGuard labeledArtifactGuard` (no default), and in both factory registrations resolve `sp.GetRequiredService<ICovenantLabeledArtifactGuard>()` as the seventh argument. The guard is registered in `AddCovenantPersistence`, which both compositions call; confirm with `grep -n "ICovenantLabeledArtifactGuard" src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`.

- [ ] **Step 6: Run both tests and observe GREEN; run `GrimoireRepositoryTests` and `CovenantLabeledArtifactGuardTests`.**

- [ ] **Step 7: Write the compensation-token inventory test**

```csharp
using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Compensating work — a rollback, a detach, a lease surrender, a post-commit proof — must run on
/// <c>CancellationToken.None</c>, because the caller's token is often the reason the compensation is
/// running. This inventory finds every <c>RollbackAsync</c>, <c>ExecuteNonQueryAsync</c> and
/// <c>TryTransitionAsync</c> call inside a <c>catch</c> or <c>finally</c> block that forwards a caller
/// token, and pins the sites that are still allowed while their owning packets remove them.
/// </summary>
public sealed class CompensationCancellationTokenTests
{

    /// <summary>Sites still on the caller's token. Every packet that fixes one deletes its line; Task 23 asserts the array is empty.</summary>
    internal static readonly string[] AllowedSites =
    [
        "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.SessionTurnBegin.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignRepository.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStore.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantManagedFileErasureKernel.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ManagedFileWriteIntentRecoveryService.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreCovenantCoordinator.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Backup/RestoreStagingManagedAuthoritySanitizationCapability.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs",
    ];

    private static readonly Regex CompensationOnCallerToken = new(
        @"(?:RollbackAsync|ExecuteNonQueryAsync|TryTransitionAsync)\((?:[^()]*,\s*)?(?:cancellationToken|ct|token)\)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Compensation_runs_on_no_token_outside_the_allowed_sites()
    {

        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {

            foreach (string block in CompensationBlocks.Of(source.Text))
            {

                if (CompensationOnCallerToken.IsMatch(block) && !AllowedSites.Contains(source.RelativePath))
                {

                    offenders.Add($"{source.RelativePath} compensates on the caller's token");

                }

            }

        }

        Assert.Empty(offenders);

    }

}
```

`CompensationBlocks.Of` is a small brace-matching helper in the same file that yields the text of every `catch` and `finally` block; write it with a depth counter over the comment-stripped source, exactly as `CovenantArchitectureBoundaryTests` walks braces.

- [ ] **Step 8: Run it and observe GREEN with the allow-list in place, then delete one allow-list entry and observe RED, then restore it.**

- [ ] **Step 9: Commit**

```bash
git add tests/RetroDownfall.Arcanum.Tests/Build/NullableInterfaceConstructorDefaultTests.cs tests/RetroDownfall.Arcanum.Tests/Build/CompensationCancellationTokenTests.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs tests/RetroDownfall.Arcanum.Tests/Repositories/GrimoireRepositoryTests.cs
git commit -m "fix: require the labelled-artifact guard on the Grimoire repository"
```

## Phase 1 — Stop the bleeding

### Task 2: Retention deletes honour Covenant labels

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/` expression-index files named by the finding
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantLabeledArtifactGuardTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`

**Findings:** W3a-1, W3a-2 (blocking); W3a-6, W3a-7 (important); W3a-3, W3a-8, W3a-9, W3a-10, W3a-11 (minor).

Order: W3a-3's route-level tests first (they are the RED tests for W3a-1 and W3a-2 and must enter through `POST /api/data/prune`, `POST /api/data/memory/reset` and `DELETE /api/data/sessions/{id}`), then inject `ICovenantLabeledArtifactGuard` into the service and call `EnsureUnlabeledAsync` inside each candidate's transaction and `EnsureNoneLabeledAsync` before each untargeted reset scope, then the two performance items (expression indexes as new schema files under `Data/Schema` — never a migration — and deletion of the manual `Entries_fts` statements that the `Entries_ad` trigger already performs), then the three minor items.

### Task 3: Selective restore reports what it committed

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs`, `BackupRestoreService.cs`, `BackupRestoreDatabaseWorker.cs`, `BackupArchiveCodec.cs`, `BackupRestoreCovenantCoordinator.cs`, `RestoreStagingManagedAuthoritySanitizationCapability.cs`, `BackupRestoreStagingIndex.cs`, `BackupRestoreJournal.cs`; `src/RetroDownfall.Arcanum.Core/Backup/BackupRestoreContracts.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupSessionImporterTests.cs` and the restore test classes

**Findings:** W4-1 (blocking); W4-2, W4-3 (important); W4-4, W4-5, W4-6, W4-7, W4-8, W4-9 (minor).

W4-1's RED test imports three Sessions where the third is source-tainted and asserts the result is not `Rejected` and names the two committed Sessions by querying the destination. The fix returns a partial result carrying committed counts and maps it to `ReconciliationRequired`; the retry-duplication the verifier found means the planner's fresh identities per run must also be addressed by keying the replay guard to the selection, or the partial result must instruct the operator not to re-run. W4-4 removes two allow-list lines from Task 1's inventory.

### Task 4: Host-process-tools decision is the one the gate published

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Security/HostProcessToolPolicy.cs`, `src/RetroDownfall.Arcanum.Core/Security/HostProcessToolsRuntimePolicy.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsStartupGate.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/HostProcessToolsStartupGateTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`

**Findings:** W7-1 (blocking); W13-3 (important); W3b-6 for the bootstrapper's shutdown-checkpoint opener only (minor).

The RED test boots a Development host with the escape-hatch variable against a clean authority row and asserts the advertised tool list contains neither `execute_command` nor `run_spell_script`. The fix routes `HostProcessToolPolicy.AreAllowed` through the published runtime decision so no call site changes; if the static predicate cannot reach the published decision, stop and report, because the five call sites include a file owned by Task 9. W13-3 replaces the fictional remediation command with the real remedy (clear the variable and restart) until the transition verb ships, and the degraded branch must record durable evidence so the next start does not republish the Covenant as permitted.

### Task 5: Covenant CLI verbs keep the one-document contract

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs`, `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliContracts.cs`, `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliApplicationFactory.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/CovenantCommandTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Cli/ConfirmationPromptTests.cs`

**Findings:** W10-1 (blocking); W10-5 (important); W10-9 (minor).

W10-1's RED test invokes `memory covenant set` with `--json --yes` through `CliApplicationFactory.RunAsync` against a stubbed client and asserts stdout parses as exactly one JSON document; the fix guards both confirmation renderers on the JSON option and routes them to the diagnostic stream, as the retention commands already do. W10-5 adds the JSON and print options to the confirmation prompt's refusal condition. W10-4's documented payload shapes are decided here — project the DTOs into the documented records — and the Documentation packet updates the reference to match whichever the executor chooses; report the choice.

### Task 6: Destructive CLI verbs confirm, and errors stay safe

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/PromptCommands.cs`, `SpellCommands.cs`, `CampaignCommands.cs`, `SessionCommands.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/Lore/LoreCommands.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/Conclave/ApprenticeCommands.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/KeyCommands.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/AskCommand.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterApp.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Cli/PromptCommandTests.cs`, `SessionCommandTests.cs`, `AskCommandTests.cs`, `KeyCommandTests.cs`

**Findings:** W10-2, W10-6 (important); W10-3, W10-7, W10-8, W15-7 (minor).

W10-2's RED test runs `prompt delete <id>` with stdout redirected and no `--yes` and asserts exit 2 with no delete call on the fake client; the fix injects the confirmation prompt into each of the seven delete handlers and names the resolved resource, as the file and retention deletes already do. W10-6 routes the turn command's catch-all through the failure mapper so only its safe message and exit code reach the operator, keeping the raw message behind verbose output. W10-3 adds one shared helper that maps a `Connection.*` failure to exit 3 and names the base address that was tried, and routes every command's failure exit through it. W15-7 starts both pipe reads asynchronously before the one-second wait so the deadline governs the read.

### Task 7: Attachment logical keys survive Windows normalisation

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Storage/SessionAttachmentPathSanitizer.cs`, `SessionAttachmentToolAmbient.cs`, `ArcanumPaths.cs`; `src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigests.cs`; `src/RetroDownfall.Arcanum.Core/Intelligence/PingRequestBoundsValidator.cs` and its five callers' argument lists
- Test: `tests/RetroDownfall.Arcanum.Tests/Storage/SessionAttachmentPathSanitizerTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Intelligence/PingRequestBoundsValidatorTests.cs`

**Findings:** W9-1 (blocking); W9-4, W9-6, W9-8, W9-9 (minor).

W9-1's RED test sanitizes `notes.` and `notes` and asserts the two results are equal or both rejected. The fix trims trailing dots and spaces after the `..` collapse and re-runs the empty and reserved checks. W9-9 removes the dead settings parameter and deletes or repoints the three tests the verifier found cannot fail.

### Task 8: Session-scoped memory surfaces count what exists

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs`

**Findings:** W14-1 (blocking); W14-3 (minor).

The RED test seeds one Session with one pinned Entry and a summary through EF and asserts `GET /api/memory/status/{id}` reports one Session entry, one pin and one summary, and that `GET /api/memory/explain/{id}` reports them eligible. The fix points the five predicates at the existing canonical parameter and leaves the three deliberately lowercase predicates untouched.

### Task 9: A disconnected client does not orphan a tool call

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnEventEmitter.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnEngine/TurnExecutionCoordinatorTests.cs`

**Findings:** W1-1 (blocking); W1-3 (important); W1-5, W1-6, W1-7, W1-9 (minor).

W1-1's RED test drives a streaming turn through the coordinator with a tool whose invocation blocks on a barrier, abandons the enumerator at the first ward frame, and asserts the tool task has completed or been awaited by the time disposal returns and that the pump did not spin (count iterations with a manual time provider). The fix observes the tool task in the per-call `finally` with a bounded grace and makes the emitter tolerate cancellation, and the pump must check cancellation on each iteration.

## Phase 2 — What a maintainer must not rely on

### Task 10: Admission gate cancellation and handle leaks

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`, `GrimoireConnectionAdmissionContracts.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs`, `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`, `GrimoireConnectionAdmissionInterceptorTests.cs`, `GrimoireDbContextCompositionTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantConnectionDrainTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs`

**Findings:** D1-1, D1-2, D1-6, D2-1, D6-1 (important); D1-3, D1-4, D1-5, D1-7, D2-3, D2-4, D2-5, D2-6, D2-7, D2-8, D6-2, D6-7 (minor).

The status of D2-1, D6-1, D2-3 and D2-8 against the tip that landed the acquisition-drain work is recorded in the findings file; execute only those still marked open there. D1-1's RED test closes admission with a pre-cancelled token while an open ticket is unresolved and asserts the generation is unchanged and the ticket still revalidates. D1-6 gives the lane drain a bounded wait and makes the tracked maintenance handle disposable so a caller can guard the leak. Three gate lines and the interceptor's refusal path leave Task 1's allow-list here.

### Task 11: Journal file primitives — cancellation, exceptions, and the Windows arm

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFilePrimitives.cs`, `GrimoireOfflineTransitionJournalFileStore.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStoreTests.cs`

**Findings:** D3-2, D3-3, D3-4, D3-7 (important); D3-1, D3-5, D3-6, D6-6 (important, test honesty); D3-8, D3-10 (minor).

D3-3's fix passes `CancellationToken.None` to every step after `published = true` on both publication branches and removes the file store from Task 1's allow-list. D3-7 replaces the fixed-arity `openat` P/Invoke with a creation path whose mode is not variadic (`File.OpenHandle` with `UnixCreateMode`, or a fixed-arity shim) and keeps `fchmod` as a post-condition check; the RED test creates a child under a zero umask and asserts the pre-`fchmod` mode is 0600. D3-4 anchors the Windows exchange to the retained parent handle with two handle-relative renames and adds the trailing parent validation. The four source-text tests are replaced with behavioural tests gated on the platform they exercise, and the durability assertions count barrier calls through an injected primitives seam rather than reading emitted step names.

### Task 12: Journal store — anchor, key outages, and diagnostics

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs`, `GrimoireOfflineTransitionJournalAuthenticator.cs`, `GrimoireOfflineTransitionJournalKeyProvider.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionJournalStoreTests.cs`, `GrimoireOfflineTransitionJournalAuthenticationTests.cs`

**Findings:** D4-1, D4-3 (important); D4-2 (important, test honesty); D4-4, D4-5, D4-6, D4-7, D4-9 (minor).

D4-1's RED test begins a journal to epoch one, retires it, deletes only the anchor credential, and asserts both `BeginAsync` and `RecoverAsync` refuse; the fix requires the key to be absent before minting a closed genesis, using the existing unused `IsPresent`, and applies the same guard to recovery's null-anchor arm. D4-3 propagates `Covenant.Unavailable` from the key provider instead of collapsing it; D4-4 propagates an anchor read failure instead of reporting a revision conflict.

### Task 13: Lifecycle can park twice

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionLifecycleValidator.cs`, `GrimoireOfflineTransitionLifecycleStore.cs`, `GrimoireOfflineTransitionLifecycleJsonContext.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/GrimoireTransitions/GrimoireOfflineTransitionLifecycleTests.cs`, `GrimoireOfflineTransitionLifecycleStoreTests.cs`, `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetCredentialCatalogTests.cs`

**Findings:** D5-1, D5-2 (important); D5-5, D5-6, D5-7, D5-10, D6-9 (minor).

D5-1's RED test runs Applying → KeepClosed → resume → KeepClosed through the handler's `ValidateAdvance` for both kinds and asserts the second park succeeds. D5-2 is plausible rather than confirmed: give the blocker an expected-state digest distinct from its binding digest and compare the resolution's state digest to that, so a genuine recomputation is what resumes; record the design decision in the packet report for the Documentation packet.

### Task 14: API idempotency and dead writer

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Api/Security/IdempotencyEndpointFilters.cs`, `src/RetroDownfall.Arcanum.Api/Tower/InferenceExecuteWriter.cs`, `src/RetroDownfall.Arcanum.Api/Security/CovenantEndpointConventionBuilderExtensions.cs`, `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Api/IdempotencyEndpointFilterTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Api/InferenceExecuteWriterTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Api/PromptExecuteFlowTests.cs`

**Findings:** W2-1, W2-4 (important); W2-2, W2-3, W2-9, W2-10, W2-11 (minor).

W2-1's RED test posts twice with one key where the first provider call fails with an unreachable error and the second succeeds, and asserts the second returns 200 from a fresh execution; the fix marks 5xx and retryable responses abandoned rather than completed, on both the buffered and the streaming arm. W2-4 deletes the dead buffered writer and re-points its three tests at the real handlers through the test host.

### Task 15: MCP tools — timeouts, chunk reads, cancellation, capability release

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Sanctum/SanctumConfig.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.ExecuteCommand.cs`, `ArcanumInternalToolServer.FileTools.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs`, `SandboxedFileIo.cs`, `CovenantToolCapabilityRegistry.cs`, `SessionAttachmentAmbientSend.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Mcp/ArcanumInternalToolServerTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Mcp/CovenantToolCapabilityRegistryTests.cs`

**Findings:** W6-1, W6-2, W8-2 (important); W6-4, W6-6, W6-8, W15-6 (minor).

W6-1's RED test configures a one-second process timeout and asserts a child that sleeps ten seconds is reported timed out within two; the fix passes the configured seconds into both runners, infinite only when zero. W6-2 streams the requested range through the validated handle and budgets the range, not the file. W8-2 adds the registry release to the existing failed-send unbind block. W6-6 records an early-cancel tombstone and consults it after registration.

### Task 16: Sanctum and workspace paths

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/SanctumGuard.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceCheckExecutionPolicy.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/PhysicalFileSystemBrowser.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Security/SanctumGuardTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Workspaces/PhysicalFileSystemBrowserTests.cs`

**Findings:** W7-2 (important); W6-3, W6-5, W6-9 (minor).

W7-2's RED test calls `ValidateToolAsync` with a well-formed Campaign id the repository no longer resolves and a tool in that Campaign's disabled list, and asserts denial; the fix returns a tri-state from the loader so "supplied but unresolvable" denies, at both entries the verifier named.

### Task 17: Data layer — fingerprint, busy retry, probe casing

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaCatalog.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteBusyRetry.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs`, `CovenantSqliteConnectionInitializer.cs`, `CovenantStore.cs`, `CovenantLocalErasureStorageHealth.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCampaignScopeProbe.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs`, `GrimoireRepository.cs`, `GrimoireRepository.SessionTurnBegin.cs`, `CampaignRepository.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantManagedFileErasureKernel.cs`, `ManagedFileWriteIntentRecoveryService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Storage/AtomicFile.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaCatalogTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/SqliteBusyRetryTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantOperationGateFixture.cs`, `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`

**Findings:** W3b-2, W3b-4, W3a-4, W15-1, W15-2, W8-4 (important); W3a-5, W3b-1, W3b-5, W3b-8, W8-1, W14-5, W14-6 (minor).

W3b-2's fix normalises each statement through the existing SQL normaliser before hashing; because the head fingerprint is computed rather than pinned, confirm with `GrimoireSchemaTransitionResourceTests` that no pinned prior version changes. W15-1 and W15-2 convert the twelve compensation sites to `CancellationToken.None` and remove eight files from Task 1's allow-list. W8-1 binds the Campaign id uppercase and makes the gate branch on the probe's enum so the two diagnoses differ; W8-4 adds one test over the real probe against rows the deletion trigger wrote.

### Task 18: Operations and hosting

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs`, `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs`, `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationStartupProbe.cs`, `InstallationResetOfflineCleanup.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/RunCommand.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Logging/HostLockSerilogFileSink.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Resilience/ProviderHealthProbeService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairStartupRecovery.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Telemetry/PrometheusMetricsExporter.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Operations/LongRunningOperationReconcilerTests.cs`, `RecoveryHandlerCoverageTests.cs`

**Findings:** W5-1 (important); W5-3, W5-4, W5-6, W5-7, W5-8, W8-3, W8-6, W8-7, W8-9, W8-10, W8-12 (minor).

W5-1's RED test seeds one expired operation whose handler cancels the pass token before returning `Completed` and asserts the row is `Completed` in the store; the fix issues the read and the transition after the handler on `CancellationToken.None` and removes the reconciler from Task 1's allow-list.

### Task 19: Security minor sweep

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Secrets/Security/LinuxOsCredentialStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Security/HttpsCertificateLoader.cs`, `WardGate.cs`, `HostProcessToolsMarkerStore.cs`, `FileEncryptionKeyProvider.cs`, `ProviderCredentialStore.cs`, `ApiKeyDigestCache.cs`, `src/RetroDownfall.Arcanum.Api/Security/ApiKeyAuthenticator.cs`
- Test: the matching classes under `tests/RetroDownfall.Arcanum.Tests/Security/`

**Findings:** W7-3, W7-5, W7-6, W7-7, W7-8, W7-9, W7-10 (minor).

### Task 20: Build, CI and scripts

**Files:**
- Modify: `.github/workflows/ci.yml`, `.github/dependabot.yml`, `Directory.Build.props`, `scripts/verify-native-sqlcipher.sh`, `scripts/verify-aot-il-warnings.sh`, `scripts/verify_aot_il_warnings_test.sh`, `scripts/benchmark-covenant.sh`, `scripts/packaging/macos/entitlements.cli.plist`, `scripts/packaging/macos/build-arcanum.sh`, `scripts/packaging/windows/package-windows.ps1`
- Test: `tests/RetroDownfall.Arcanum.Tests/Packaging/ContinuousIntegrationWorkflowTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Packaging/MacOsSigningScriptTests.cs`

**Findings:** W12-1, W12-5, W12-6, W12-8, W12-9 (important); W12-2, W12-3, W12-4, W12-7, W12-11 (minor).

W12-1 deletes the two dead jobs (the doc sentences go to Task 22). W12-5 captures the tool's own exit status, treats an empty dependency list as failure, and compares `@rpath/` entries instead of discarding them. W12-6 turns the two false passes into explicit unverified reports until an import-table step exists in the Windows job, and adds that step. W12-3 turns on warnings-as-errors: the build is already at zero warnings, so this is a one-line property. `MacOsSigningScriptTests` is known to fail on this host for an environmental reason; confirm it fails identically on an untouched worktree before treating it as this packet's.

### Task 21: Desktop apps

**Files:**
- Modify: `src/RetroDownfall.TheForge.Ux/ViewModels/Docking/DockLayoutViewModel.cs`, `App.axaml.cs`, `Program.cs`, `Services/ArcanumApiClient.cs`, `Services/AvaloniaApiKeyPrompt.cs`, `ViewModels/FoundryFloor/FoundryFloorViewModel.cs`, `ViewModels/Workbench/ChatMessageViewModel.cs`, `Views/Controls/IlluminationView.axaml.cs`, `Views/Workbench/TomeView.axaml`; `src/RetroDownfall.TheForge.Core/Services/TheForgeSettingsStore.cs`, `IO/TheForgeAtomicJsonFile.cs`; `src/RetroDownfall.Compendium.Ux/Services/FamiliarProbeClient.cs`, `ServiceCollectionConfigurator.cs`, `App.axaml.cs`
- Test: `tests/RetroDownfall.TheForge.Tests/DockLayoutViewModelTests.cs`, `tests/RetroDownfall.Compendium.Tests/FamiliarProbeClientTests.cs`, `ServiceCollectionConfiguratorTests.cs`

**Findings:** W11-1, W11-2, W11-3, W11-4, W15-3 (important); W11-5, W11-8, W11-9, W11-10, W11-11, W11-12, W15-9 (minor).

W11-2's fix throws from both streaming readers on a non-2xx response; every consumer already has a catch arm. W11-3 is a decision: either persist the pasted key through the credential store, or relabel the button and delete the two false comments — the verifier found the design's desktop-settings section describes a migration that does not exist, so choose, implement, and report the choice for Task 22.

## Phase 3 — Sweep

### Task 22: Documentation

**Files:**
- Modify: `README.md`, `AGENTS.md`, `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`, `docs/Arcanum.DEBUGGING.Human.md`, `docs/Arcanum.ConstraintInventory.json`
- Test: `tests/RetroDownfall.Arcanum.Tests/Build/DocumentationIssueReferenceTests.cs`, `DocumentationStructureTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs`

**Findings:** W13-2, W13-4, W13-5, W10-4 (important); W13-1, W13-6, W13-7, W11-6, W2-5, W12-10, W12-12 (minor), plus every sentence the code packets reported.

Runs after every code packet has merged. Every edit describes what the system is, never who asked for it or when; no tracker reference outside `README.md` and `docs/Arcanum.OATH.md`; one logical block per physical line. Add a route-table inventory test that fails when a registered endpoint has no row in the API reference, and a command-table test that fails when the reference names a verb the command map lacks.

### Task 23: Platform-gated tests skip instead of passing

**Files:**
- Create: `tests/RetroDownfall.Arcanum.Tests/Build/PlatformGatedTestSkipTests.cs`
- Modify: the twenty-five test files the finding enumerates, starting with `ChildProcessFilesystemJailTests.cs`, `SecureFilePermissionsTests.cs`, `PhysicalFileSystemWriterTests.cs`, `PermissionPostureTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Build/CompensationCancellationTokenTests.cs` — empty `AllowedSites`

**Findings:** W14-2 (important); W14-4 (minor).

Runs after Task 17 so no file is contested. The source-scan test fails on any `[Fact]` or `[Theory]` whose body returns early on an `OperatingSystem.Is*` condition without a `Skip.` call; convert each site to `[SkippableFact]` with `Skip.IfNot`. Then empty Task 1's allow-list and confirm the compensation inventory is green, which proves every packet removed its sites.

## Phase 4 — Prove it

### Task 24: Gate ladder, merge, push

Run from the assembled `grimoire-fixes` tree after the last merge, in this order, each one green before the next:

- [ ] `dotnet build RetroDownfall.Arcanum.slnx -c Release --no-incremental` — zero warnings, zero errors.
- [ ] Sweep, then `dotnet test` for all three test projects — compare the total against the baseline of 12,944 + 179 + 671 plus the tests this plan added, and apply the isolation protocol to any single red.
- [ ] `./scripts/align-csharp-blanklines.sh --check` over every C# file the plan touched, and `git diff --check`.
- [ ] Sweep, then `./scripts/coverage.sh --threshold`; if the known environmental signing test aborts the script, recover the answer with `python3 scripts/coverage_threshold.py "$(find .tmp/coverage -name coverage.cobertura.xml | head -1)"`.
- [ ] Clear both publish trees, then `./scripts/verify-aot-il-warnings.sh`.
- [ ] `./scripts/verify-native-sqlcipher.sh --rid osx-arm64`.
- [ ] A final read-only adversarial audit of `git diff c3705d4d..HEAD` by agents briefed with this repository's history of reverted guards and papered-over fixtures; any blocker is fixed and the ladder re-run from the build step.
- [ ] `git push origin grimoire-fixes`, then confirm `origin/grimoire-fixes` resolves to the pushed SHA.
