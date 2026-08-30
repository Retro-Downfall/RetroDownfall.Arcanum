# Issue #220 Stop Paying the Ward Gate's Per-Call Cost Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove the obsolete Ward engine's settings projection, empty argument-document construction, and live-path event-buffer allocation from every ordinary tool call while preserving the complete Ward record, tool-event, Grimoire persistence, and session-recall contracts.

**Architecture:** The singleton `WardGate` resolves its retained compatibility `WardSettings` once at construction. `ToolPolicy.NoForbiddenArts` reads its public policy list directly. `ToolExecutionPipeline` materializes Ward arguments only when arguments or a host disclosure exist and allocates a two-event buffer only for buffered execution; streaming emits the unchanged pair directly and returns the shared empty array.

**Tech Stack:** .NET 10, C#, xUnit, Microsoft.Extensions.AI, EF Core with SQLCipher, source-generated `System.Text.Json`, Git, and GitHub CLI.

**Spec:** [`docs/superpowers/specs/2026-08-30-issue-220-stop-per-call-cost-design.md`](../specs/2026-08-30-issue-220-stop-per-call-cost-design.md)

**Global Constraints:**

- Work only on `codex/issue-220-stop-per-call-cost` until the final integration step. The branch is based on tracked `remove-wards` at `b2561a3a91dcb866552cc203291b7a52d755dc38`; `remove-wards` contains `main` at `decdf011f69ab91c1e48a0d50c2bbf97cd928162`.
- Apply strict RED-GREEN-REFACTOR for every behavior or cost change: write the named test, run it and retain the expected failure evidence, make the smallest production change, then rerun the focused set green before committing.
- Green-before-change characterization tests are permitted only for the existing tool-event, durable-entry, and recall invariants in Task 1 and the required retained-payload canaries in Task 3.
- Preserve exactly one ordered `Warded` / `WardResolved` pair, one fresh shared Ward id, one allowed `Ungated` tombstone, one `origin=ungated` metric sample, and the existing pair on every success/refusal/failure path.
- Preserve live `ToolCall` / `ToolResult` events, `ToolError` ordering, generic best-effort Grimoire persistence, `apply_patch`'s mandatory receipt-backed persistence, and reconstruction into Command Center Incantations.
- Preserve `workspace_check`'s host-owned disclosure even when the model supplies no arguments. Preserve malformed-argument wrapping and host replacement of `_arcanumRiskDisclosure` when a payload exists.
- Preserve the active-Ward compatibility engine, its clamps, tombstones, timeout/capacity behavior, and HTTP 409 `AlreadyResolved` behavior. `WardGate` remains a singleton and retains the same constructor/DI surface.
- Preserve `ForbiddenArts`, `UnattendedMode`, and `ToolPolicy.NoForbiddenArts`. The list remains an advertisement filter and never becomes an execution gate.
- Do not alter canonical product documentation unless implementation reveals a public semantic change. The approved spec and this plan are the expected documentation for this internal optimization; #221 owns the broad wording sweep.
- Treat every proof as release-grade: no timing-dependent allocation assertion, warning suppression, skipped applicable gate, weakened containment, or test seam that adds work back to the production hot path.
- Do not expand into #221 or #230, close epic #197, modify `main`, create a PR, push the temporary feature branch, or dispatch GitHub CI. Push only the completed `remove-wards` branch; GitHub CI is deferred until the epic's eventual merge to `main`.
- Use `rg --no-config`. Run .NET build/test processes with `--disable-build-servers -m:1` when invoked directly. The VSTest host requires permission to open its local socket in this environment.
- Run the complete verification suite exactly once after bounded review. Do not rerun it after the history-only merge; prove the merged tree equals the verified feature tree.

Before Task 1, verify the already-created implementation branch and clean starting point:

```bash
git branch --show-current
git merge-base --is-ancestor b2561a3a HEAD
git status --short
```

Expected: `codex/issue-220-stop-per-call-cost`, successful ancestry, and no changes except this committed plan.

## Task 1: Pin live, durable, and recalled tool records before optimizing Ward work

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Repositories/GrimoireRepositoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/CommandCenter/SessionWorkspaceServiceTests.cs`
- Verify unchanged: `tests/RetroDownfall.Arcanum.Tests/Cli/CommandCenter/IncantationPaneTests.cs`
- Verify unchanged: `tests/RetroDownfall.Arcanum.Tests/Intelligence/GrimoireTurnWriterTests.cs`

### Step 1: Add a multi-tool streaming and append characterization

In `WizardIntelligenceProviderTests`, add a test named:

```csharp
[Fact]
public async Task Issue220_Multi_tool_stream_keeps_live_and_durable_tool_records()
```

Use the existing `CreateProgressMcpTool`, `ScriptingChatClient`, `CollectStreamAsync`, and `CreateWizard` helpers. Enqueue two streaming `record_progress` calls with provider ids `progress-1` and `progress-2`, arguments `evidence = 1` and `evidence = 2`, then enqueue the final token response. Bind a fixed session id in both `FakeGrimoireRepository.FixedSessionId` and the `PingRequest`.

Extend `FakeGrimoireRepository` with an append-only capture that mirrors the real port's arguments:

```csharp
private sealed record RecordedToolInteraction(
    Guid SessionId,
    string ToolName,
    string Arguments,
    string Result,
    string ModelUsed);

public List<RecordedToolInteraction> ToolInteractions { get; } = [];
```

At the start of its existing `AppendToolInteractionAsync`, append:

```csharp
ToolInteractions.Add(new RecordedToolInteraction(
    sessionId,
    toolName,
    arguments,
    result,
    modelUsed));
```

The new test must assert:

- two `IntelligenceEventType.ToolCall` frames and two `ToolResult` frames reach the caller;
- each result follows its corresponding call and retains the same provider call id and tool name;
- the call's `ArgumentsJson` byte-for-byte equals the matching captured Grimoire append arguments;
- the result frame's `Data` byte-for-byte equals the matching captured append result;
- both captured interactions use the bound session id and expected model;
- the two changed evidence values produce `evidence-1` and `evidence-2`, so no-progress detection is not involved.

This is a characterization test and must pass before any production edit.

### Step 2: Add a real-SQLCipher persistence characterization

In `GrimoireRepositoryTests`, add a `[SkippableFact]` named:

```csharp
public async Task AppendToolInteractionAsync_persists_an_exact_recallable_pair()
```

Use the class's real `GrimoireFixture`, `CreateRepository`, and `_db` helpers. Begin a session, finalize its assistant placeholder, append this interaction, and query the session Entries ordered by `Sequence`:

```csharp
const string toolName = "execute_command";
const string arguments = """{"command":"dotnet --version"}""";
const string result = "10.0.0";
const string model = "test-model";
```

Assert the final two persisted Entries are consecutive and exact:

- assistant call Entry: content `[ToolCall: execute_command({"command":"dotnet --version"})]`, `ToolName == toolName`, `ToolArguments == arguments`, and `ModelUsed == model`;
- system result Entry: content `[ToolResult: 10.0.0]`, null tool columns, and `ModelUsed == model`.

This proves the database representation itself, not only a fake append call.

### Step 3: Expand the recall characterization to two interactions

Change `Resume_routes_grimoire_tool_call_result_pairs_to_incantations` in `SessionWorkspaceServiceTests` to supply two chronological call/result pairs in the existing newest-first API fixture. Keep `write_file` and add:

```text
[ToolCall: execute_command({"command":"dotnet --version"})]
[ToolResult: 10.0.0]
```

Update `EntryCount` and timestamps. Assert the transcript excludes all four persisted tool markers and `state.Incantations.Snapshot()` contains two succeeded records in chronological order with their exact tool names, arguments, and results.

The unchanged `IncantationStoreTests.ToolCall_creates_pending_by_CallId_Result_updates_same` remains the live TUI reducer canary. The unchanged `GrimoireTurnWriterTests.TryAppendToolInteractionAsync_PersistsAndPublishesSavedEntries` remains the persisted-entry publication canary.

### Step 4: Run the characterization gate GREEN

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~Issue220_Multi_tool_stream_keeps_live_and_durable_tool_records|FullyQualifiedName~AppendToolInteractionAsync_persists_an_exact_recallable_pair|FullyQualifiedName~Resume_routes_grimoire_tool_call_result_pairs_to_incantations|FullyQualifiedName~ToolCall_creates_pending_by_CallId_Result_updates_same|FullyQualifiedName~TryAppendToolInteractionAsync_PersistsAndPublishesSavedEntries"
```

Expected: every selected characterization passes on the unoptimized tree. If it does not, diagnose the existing contract before touching Ward performance code.

### Step 5: Commit the characterization slice

```bash
git add tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs tests/RetroDownfall.Arcanum.Tests/Repositories/GrimoireRepositoryTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/CommandCenter/SessionWorkspaceServiceTests.cs
git commit -m "test: pin issue 220 tool record invariants"
```

## Task 2: Resolve Ward compatibility settings once per singleton lifetime

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Security/WardGateTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WardAutoApprovalPipelineTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Security/WardGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Verify unchanged behavior: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`

### Step 1: Add the counting monitor and host-lifetime test

Add `CountingOptionsMonitor` beside the existing `FakeOptionsMonitor` in `WardGateTests`:

```csharp
private sealed class CountingOptionsMonitor(ArcanumSettings value)
    : IOptionsMonitor<ArcanumSettings>
{

    public int CurrentValueReadCount { get; private set; }

    public ArcanumSettings CurrentValue
    {

        get
        {

            CurrentValueReadCount++;

            return value;

        }

    }

    public ArcanumSettings Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;

}
```

Add `Runtime_settings_are_resolved_once_and_reused_by_active_and_automatic_paths`. Construct one gate with the counting monitor, then:

1. create and human-resolve one active Ward;
2. await its resolution;
3. record one automatic `Ungated` resolution;
4. call `GetActiveWards`;
5. make a late manual resolution attempt against the automatic tombstone;
6. assert the active resolution succeeded, the automatic Ward never became active, the late attempt is `AlreadyResolved`, and `CurrentValueReadCount == 1`.

The current implementation reads `CurrentValue` during every prune, so this test is RED before caching.

### Step 2: Add the deterministic allocation-counting test

Add `using Xunit.Abstractions;`, inject `ITestOutputHelper output` into the existing `WardRecordPipelineTests` class, and add:

```csharp
[Fact]
public void N_tool_record_path_allocation_does_not_scale_with_ForbiddenArts_count()
```

Use these fixed parameters:

```csharp
const int WarmupCount = 8;
const int SampleCount = 128;
const int ForbiddenArtCount = 512;
const long MaximumSettingsDependentDelta = 4096;
```

Before measurement, pre-create:

- empty and 512-name `ForbiddenArts` settings objects;
- one real `WardGate` and `ToolExecutionPipeline` for each settings object;
- one `WarmupCount + SampleCount` `FunctionCallContent` array with unique provider call ids, empty arguments, and one registered synchronous `allocation_probe` function;
- shared `PingRequest`, `ChatOptions`, and `TurnContext` values.

Drive the real `ProcessSingleToolCallAsync` path with `argumentsSnapshot: ""`, no live emitter, and `suppressInvocationFailures: false`. Warm both pipelines with the same first eight call objects in the same order. Measure both pipelines with the same remaining 128 call objects in the same order, so provider ids, string hashes, and dictionary growth inputs cannot consume the tolerance. Use `GC.GetAllocatedBytesForCurrentThread`.

The measurement helper must call `ProcessSingleToolCallAsync` directly, require each returned task to be `IsCompletedSuccessfully`, then consume it with `GetAwaiter().GetResult()`. Capture the current managed-thread id before each sample set and fail if it changes, so a scheduler hop cannot make current-thread allocation evidence silently incomplete. Do not interpolate strings, enumerate LINQ, construct settings/functions/options, or create call objects inside the measured loop.

Write one stable evidence line through `ITestOutputHelper`:

```csharp
output.WriteLine(
    $"Issue #220 allocation sample: N={SampleCount}; empty={emptyBytes}; "
        + $"configured={configuredBytes}; delta={deltaBytes}");
```

Assert `Math.Abs(configuredBytes - emptyBytes) <= MaximumSettingsDependentDelta`, with N and all three byte values in the failure message. The pre-change N-tool path recopies 512 names when every call records its automatic Ward resolution and must fail materially above the tolerance. After construction-time caching, the one list projection sits outside the measured loops and the per-tool allocation totals must be independent of list size. Total allocation remains non-zero because the test intentionally retains Ward ids, two frames, tombstones, metrics, and buffered event collections.

### Step 3: Run the settings/allocation tests RED and capture the before measurement

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --logger "console;verbosity=detailed" --filter "FullyQualifiedName~Runtime_settings_are_resolved_once_and_reused_by_active_and_automatic_paths|FullyQualifiedName~N_tool_record_path_allocation_does_not_scale_with_ForbiddenArts_count"
```

Expected failures:

- the monitor read count is greater than one;
- the configured allocation total is materially larger than the empty-list total.

Record the exact RED output, machine/runtime, build configuration, N, and source commit (`git rev-parse HEAD`) for the issue #220 before measurement. Do not weaken the tolerance to make the old implementation pass.

### Step 4: Cache the compatibility projection in `WardGate`

In `WardGate.cs`, replace the retained monitor field with:

```csharp
private readonly WardSettings _runtimeSettings;
```

Keep the public constructor and assign exactly once:

```csharp
_runtimeSettings = settings.CurrentValue.ResolveWard();
```

Keep the existing clamp calls, but read `_runtimeSettings.MaxActiveWards` in `WardAsync` and `_runtimeSettings.TimeoutSeconds` in `PruneResolvedTombstones`. Delete `ResolveRuntimeSettings`. Do not change DI: `IWard` is already singleton, so the constructor projection is the host-lifetime projection.

### Step 5: Remove the per-turn advertisement projection

In `WizardIntelligenceProvider.ApplyToolPolicyFilters`, retain the `NoForbiddenArts` arm but replace:

```csharp
settings.Value.ResolveWard().ForbiddenArts
```

with:

```csharp
settings.Value.Security?.Ward?.ForbiddenArts ?? []
```

The runtime default list is empty, so this preserves the exact exclusion behavior while removing the final turn-path `ResolveWard()` call.

### Step 6: Run the settings/allocation gate GREEN

Run the Step 3 command again, then run the retained active-Ward and advertisement behavior:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~WardGateTests|FullyQualifiedName~Scenario53_NoForbiddenArtsPolicy_ExcludesOperatorConfiguredTools"
rg --no-config -n "\.ResolveWard\(" src
```

Expected: all selected tests pass; the counting monitor proves one projection across active and automatic compatibility paths; the real fixed-N tool path reports after-allocation totals within tolerance; the retained advertisement test proves direct `ForbiddenArts` behavior; and the source search confirms there is no second production `ResolveWard()` consumer outside the singleton constructor.

### Step 7: Commit the settings slice

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Security/WardGate.cs src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs tests/RetroDownfall.Arcanum.Tests/Security/WardGateTests.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/WardAutoApprovalPipelineTests.cs
git commit -m "perf: cache Ward runtime settings"
```

## Task 3: Skip empty Ward payloads and the live-only event buffer

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WardAutoApprovalPipelineTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolExecutionObserverTimingTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs`

### Step 1: Add the retained-payload canaries

In `ToolExecutionObserverTimingTests`, add `Workspace_check_without_tool_arguments_still_emits_host_owned_execution_risk_disclosure`. Reuse the existing pipeline and `CapturingWard`, but construct the call with an empty argument dictionary and pass `argumentsSnapshot: ""` to `ProcessSingleToolCallAsync`.

Assert the returned pair is still `Warded`, then `WardResolved`, both are `Ungated`, the automatic-resolution count is one, and `Warded.WardArguments` contains all existing disclosure terms: `workspace-authored code`, `read-only`, `writable build`, and `network`.

Also add `Workspace_check_replaces_a_caller_supplied_risk_disclosure`. Pass a non-empty object containing `_arcanumRiskDisclosure: "model supplied"`; assert the returned Ward argument object contains exactly one property with that name, its value is not the caller text, and it contains the host's workspace-execution disclosure.

In `WardRecordPipelineTests`, add `Malformed_non_empty_arguments_keep_the_raw_Ward_payload`. Process an ordinary tool with `argumentsSnapshot: "{not-json"`; assert the call still executes and the `Warded` argument object contains exactly `raw: "{not-json"`.

Run all three canaries before any production edit:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~Workspace_check_without_tool_arguments_still_emits_host_owned_execution_risk_disclosure|FullyQualifiedName~Workspace_check_replaces_a_caller_supplied_risk_disclosure|FullyQualifiedName~Malformed_non_empty_arguments_keep_the_raw_Ward_payload"
```

Expected: GREEN on the old implementation. A later failure in disclosure, replacement, or malformed-input preservation blocks the optimization.

### Step 2: Write the live-buffer RED test

Extend the local `ProcessAsync` helper in `WardRecordPipelineTests` with an optional `liveWardEmit` argument and forward it to `ProcessSingleToolCallAsync`.

Add `Live_ward_emit_forwards_the_pair_without_buffering_it`. Capture the callback's events, process one ordinary call with a non-empty fixture payload, and assert:

```csharp
Assert.Same(Array.Empty<IntelligenceEvent>(), processed.WardEvents);
Assert.Equal(
    [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
    emitted.Select(static evt => evt.Type));
Assert.Equal(emitted[0].WardId, emitted[1].WardId);
Assert.All(
    emitted,
    static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));
```

Add two boundary variants with the same shared-empty and emitted-pair assertions:

- `Live_apply_patch_session_refusal_emits_the_pair_without_a_buffer`: call `apply_patch` without the mandatory persisted-turn context and assert the unchanged `session_required` result plus the live Ward pair.
- `Live_tolerated_failure_emits_the_pair_without_a_buffer`: register a throwing ordinary tool, set `suppressInvocationFailures: true`, and assert `processed.Failed`, the public failure result, the live Ward pair, and the shared empty returned collection.

Run only these tests:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~Live_ward_emit_forwards_the_pair_without_buffering_it|FullyQualifiedName~Live_apply_patch_session_refusal_emits_the_pair_without_a_buffer|FullyQualifiedName~Live_tolerated_failure_emits_the_pair_without_a_buffer"
```

Expected RED: every callback already receives its pair, but each `processed.WardEvents` is a new empty `List<IntelligenceEvent>` rather than the shared empty array.

### Step 3: Implement lazy buffered-event storage

In `ProcessSingleToolCallAsync`, replace the unconditional list with a nullable buffer:

```csharp
List<IntelligenceEvent>? wardEvents = liveWardEmit is null
    ? new List<IntelligenceEvent>(2)
    : null;
```

Add a non-allocating helper:

```csharp
private static IReadOnlyList<IntelligenceEvent> BufferedWardEventsOrEmpty(
    List<IntelligenceEvent>? buffered) =>
    buffered ?? Array.Empty<IntelligenceEvent>();
```

Change only the private audit plumbing to accept `List<IntelligenceEvent>?`. Every result boundary must call `BufferedWardEventsOrEmpty`; the Covenant retirement helpers should consume `IReadOnlyList<IntelligenceEvent>` because they never mutate the collection. `EmitWardEventAsync` must retain this order:

```csharp
if (liveWardEmit is not null)
{

    await liveWardEmit(wardEvent, cancellationToken).ConfigureAwait(false);

    return;

}

(buffered ?? throw new InvalidOperationException(
    "Buffered Ward emission requires a buffer."))
    .Add(wardEvent);
```

Do not remove either event object or buffering on the non-live path. Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~Live_ward_emit_forwards_the_pair_without_buffering_it|FullyQualifiedName~Live_apply_patch_session_refusal_emits_the_pair_without_a_buffer|FullyQualifiedName~Live_tolerated_failure_emits_the_pair_without_a_buffer|FullyQualifiedName~A_tolerated_invocation_failure_still_reports_ungated_record_frames"
```

Expected: both the live no-buffer test and buffered tolerated-failure test pass.

### Step 4: Write the builder-contract and ordinary zero-argument tests RED

Add these tests to `WardRecordPipelineTests`:

```csharp
[Fact]
public void Ward_arguments_builder_rejects_an_empty_payload()
```

The test calls the target internal method with empty arguments and disclosure and expects `ArgumentException`.

```csharp
[Fact]
public async Task Ordinary_call_without_arguments_or_disclosure_skips_Ward_payload_materialization()
```

The pipeline test passes `argumentsSnapshot: ""`, asserts the tool still executes, asserts the unchanged ordered/shared-id `Ungated` pair, and asserts `Warded.WardArguments` is null.

First run both tests before production changes:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~Ward_arguments_builder_rejects_an_empty_payload|FullyQualifiedName~Ordinary_call_without_arguments_or_disclosure_skips_Ward_payload_materialization"
```

Expected RED is a compile error because `BuildWardArgumentsDocument` is private and does not expose the required `(argsSnapshot, disclosure)` contract.

### Step 5: Make the builder contract visible, then observe the branch RED

Change the builder to:

```csharp
internal static JsonDocument BuildWardArgumentsDocument(
    string argsSnapshot,
    string disclosure)
```

Its first action must throw `ArgumentException` when both values are empty/whitespace. Remove the internal `ToolRiskClassifier.GetWardDisclosure` lookup; the caller owns that classification. Keep parsing, malformed `{ "raw": ... }` wrapping, disclosure merge, caller-disclosure replacement, disposal, and cloning behavior unchanged.

Run the Step 4 command again before adding the caller guard. Expected intermediate RED: the direct contract test passes, but the ordinary zero-argument pipeline test throws the new precondition exception. This proves the pipeline must branch before the builder.

### Step 6: Guard payload materialization before the builder

In `RecordUngatedWardResolutionAsync`, resolve disclosure once:

```csharp
string disclosure = ToolRiskClassifier.GetWardDisclosure(toolName);

JsonElement? recordWardArguments = null;

if (!string.IsNullOrWhiteSpace(argsSnapshot)
    || !string.IsNullOrEmpty(disclosure))
{

    using JsonDocument recordArgsDocument = BuildWardArgumentsDocument(
        argsSnapshot,
        disclosure);

    recordWardArguments = recordArgsDocument.RootElement.Clone();

}
```

An empty ordinary call now bypasses the builder. An empty `workspace_check` still enters it because disclosure is non-empty.

### Step 7: Run the complete pipeline cluster GREEN

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers -m:1 --filter "FullyQualifiedName~WardRecordPipelineTests|FullyQualifiedName~ToolExecutionObserverTimingTests|FullyQualifiedName~Scenario56_AttunementExecuteCommand_IsAdvertisedExecutesAndRecordsUngated|FullyQualifiedName~CovenantAgentRetirementTests"
git diff --check
```

Expected: all selected tests pass, including buffered tolerant failures, live shared-empty identity, ordinary null arguments, `workspace_check` disclosure, Ward ordering/identity/origin, metrics, Sanctum denial, and Covenant retirement.

### Step 8: Commit the pipeline slice

```bash
git add src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/WardAutoApprovalPipelineTests.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolExecutionObserverTimingTests.cs
git commit -m "perf: elide empty Ward record work"
```

## Task 4: Review the complete implementation and freeze measurement evidence

### Step 1: Inspect the branch diff and required source invariants

```bash
git log --oneline b2561a3a..HEAD
git diff --stat b2561a3a..HEAD
git diff b2561a3a..HEAD
rg --no-config -n "\.ResolveWard\(" src
rg --no-config -n "BuildWardArgumentsDocument|new List<IntelligenceEvent>\(2\)|Array\.Empty<IntelligenceEvent>" src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs
git diff --check b2561a3a..HEAD
git status --short
```

Expected: one production `ResolveWard()` call in the singleton constructor; argument building sits behind the arguments-or-disclosure branch; the two-item list is created only when no live emitter exists; no uncommitted files remain.

### Step 2: Request a bounded code review

Use `superpowers:requesting-code-review` over `b2561a3a..HEAD`. Review specifically for:

- settings projection or `ForbiddenArts` copying on a turn/tool-call path;
- a lost clamp, tombstone prune, active-Ward capacity, timeout, race, or `AlreadyResolved` invariant;
- dropped/reordered Ward events, ids, origins, metrics, or failure-path buffers;
- a zero-argument `workspace_check` disclosure regression;
- malformed JSON/redaction/disclosure replacement drift;
- loss or reordering of live tool events, ordinary Grimoire entries, mandatory `apply_patch` receipts, or recalled Incantations;
- accidental #221/#230 work or a public semantic change that would require canonical docs.

Fix each correctness issue with a new failing focused test when observable, rerun only that focused test, and commit the fix. Do not start an open-ended review loop.

### Step 3: Re-run only the fixed-N measurement for final evidence

Run the exact Task 2 Step 3 measurement command against the reviewed implementation commit. Record the commit id, runtime, configuration, N, warmup, empty/configured totals, per-call values, delta, and intentionally retained costs. This is not a full-suite rerun; it is the required before/after measurement using the identical harness.

Prepare the issue comment from the actual captured values. Do not invent or round away an unfavorable result.

## Task 5: Run the complete locally applicable verification suite once

Use `superpowers:verification-before-completion`. Run from the repository root in this order, stop at the first failure, and apply `superpowers:systematic-debugging` before changing code:

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --disable-build-servers -m:1
python3 -m unittest scripts/coverage_threshold_test.py
./scripts/coverage.sh --threshold
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --disable-build-servers -m:1
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj --disable-build-servers -m:1
./scripts/packaging/macos/common_test.sh
./scripts/verify_aot_il_warnings_test.sh
RIPGREP_CONFIG_PATH=/dev/null ./scripts/verify-aot-il-warnings.sh
RIPGREP_CONFIG_PATH=/dev/null ./scripts/verify-native-sqlcipher.sh --rid osx-arm64
dotnet build tests/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj -c Debug --no-incremental --disable-build-servers -m:1
./scripts/benchmark-covenant.sh --gate --record .superpowers/sdd/2026-08-30-issue-220-stop-per-call-cost/covenant-benchmark-run.json
python3 scripts/align_csharp_blanklines.py --repo . --check
find scripts -name '*.sh' -print0 | xargs -0 shellcheck -x -P SCRIPTDIR
actionlint
git diff --check b2561a3a..HEAD
git status --short --branch
```

`scripts/coverage.sh --threshold` is the one complete non-Perf Arcanum suite; do not run another unfiltered Arcanum suite. Compendium and The Forge are separate shipped-client suites. The Covenant benchmark is the repository's Native-AOT absolute gate, not the manual xUnit `Category=Perf` suite. Do not run that machine-load-sensitive non-gate; Task 2's fixed-N allocation test is the deterministic #220 evidence.

The GitHub-hosted production macOS workspace-check jail, disposable-Keychain integration, and Windows lanes are explicitly deferred by the operator until the eventual merge to `main`; do not simulate them by mutating the local root-owned SDK or operator Keychain. No temporary branch is pushed and no GitHub workflow is dispatched.

Acceptance is zero failed applicable tests, zero build/AOT/IL/native/benchmark errors, zero warnings, coverage thresholds met, alignment/shell/workflow/diff checks clean, and a clean tracked worktree. Record exact counts and outputs once. Never rerun a green complete suite.

## Task 6: Merge into `remove-wards`, clean branches, push, and mark #220 done

Use `superpowers:finishing-a-development-branch` after Task 5 is green.

### Step 1: Refresh remote identities and capture the verified tree

```bash
git fetch origin main remove-wards
git worktree list --porcelain
git rev-parse HEAD
git rev-parse HEAD^{tree}
git rev-parse origin/main
git rev-parse origin/remove-wards
git merge-base --is-ancestor origin/main origin/remove-wards
git merge-base --is-ancestor origin/remove-wards HEAD
git status --short --branch
```

Confirm `origin/main` and `origin/remove-wards` have not advanced unexpectedly and both ancestry checks succeed. If `origin/main` has advanced beyond `origin/remove-wards`, or `origin/remove-wards` is no longer contained in the verified feature branch, stop for direction; #220 does not authorize silently merging new `main` or unrelated aggregation work into the verified tree. Do not overwrite or force-push.

### Step 2: Merge every #220 commit into the tracked aggregation branch

```bash
git switch remove-wards
git merge --no-ff codex/issue-220-stop-per-call-cost -m "Merge issue #220 stop per-call Ward cost"
```

Use `git worktree list --porcelain` to resolve the actual checkout that owns `remove-wards`. If another worktree owns it, run the merge in that exact clean checkout instead of forcing the branch into this one or removing the worktree.

Do not rerun the complete suite for this history-only merge. Prove the merge did not change the verified tree:

```bash
git diff --exit-code codex/issue-220-stop-per-call-cost..remove-wards
test "$(git rev-parse codex/issue-220-stop-per-call-cost^{tree})" = "$(git rev-parse remove-wards^{tree})"
git diff --check
git status --short --branch
```

### Step 3: Delete the local feature branch and push only `remove-wards`

Delete every local feature branch created for #220 after confirming it is merged. First inspect `git worktree list --porcelain`. If the feature branch is still checked out in an auxiliary worktree because the merge ran in a different `remove-wards` checkout, verify that auxiliary worktree is clean and detach it at the verified `remove-wards` merge commit; never remove a user worktree merely to free the branch. Then run in the aggregation checkout:

```bash
git branch -d codex/issue-220-stop-per-call-cost
```

Publish only the completed aggregation branch:

```bash
git push origin remove-wards
git rev-parse remove-wards
git rev-parse origin/remove-wards
git ls-remote --heads origin codex/issue-220-stop-per-call-cost
```

The two final `remove-wards` ids must match and the feature-ref query must be empty because the feature branch was never pushed. Do not push or merge `main`.

### Step 4: Post evidence and close issue #220

Post the actual before/after allocation evidence and delivery result in the close comment. Include:

- before and after source commit ids;
- OS/architecture, .NET runtime, and build configuration;
- fixed tool-path N and warmup count;
- empty/configured total and per-call allocated bytes plus settings-dependent delta;
- retained Ward id/event/tombstone/metric/payload-on-demand costs;
- full verification results and pushed `remove-wards` merge id.

After posting that exact evidence with `gh issue comment`, close as completed without a PR:

```bash
gh issue close 220 --repo Retro-Downfall/RetroDownfall.Arcanum --reason completed
```

Re-resolve the linked project metadata before mutation:

```bash
gh issue view 220 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,projectItems,url
gh project field-list 1 --owner Retro-Downfall --format json
gh project item-list 1 --owner Retro-Downfall --format json --limit 300 --jq '.items[] | select(.content.number == 220 and .content.repository == "Retro-Downfall/RetroDownfall.Arcanum") | {id: .id, status: .status, title: .title, url: .content.url}'
```

The planning-time identities are project `PVT_kwDOElfBBM4BeEbA`, item `PVTI_lADOElfBBM4BeEbAzg4Va7g`, Status field `PVTSSF_lADOElfBBM4BeEbAzhYhXCo`, and Done option `98236657`. Use them only if the live readback still matches issue #220. If closing did not set that exact item to `Done`, update only it:

```bash
gh project item-edit --id PVTI_lADOElfBBM4BeEbAzg4Va7g --project-id PVT_kwDOElfBBM4BeEbA --field-id PVTSSF_lADOElfBBM4BeEbAzhYhXCo --single-select-option-id 98236657
```

### Step 5: Verify final issue and branch state

```bash
gh issue view 220 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,closedAt,url
gh issue view 197 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,url
gh issue view 221 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,url
gh issue view 230 --repo Retro-Downfall/RetroDownfall.Arcanum --json number,state,url
gh project item-list 1 --owner Retro-Downfall --format json --limit 300 --jq '.items[] | select(.content.number == 220 and .content.repository == "Retro-Downfall/RetroDownfall.Arcanum") | {id: .id, status: .status, title: .title, url: .content.url}'
git branch --list
git status --short --branch
```

Expected final state: #220 is closed/completed and its project item is `Done`; #197, #221, and #230 remain open; local and remote `remove-wards` point to the delivered merge; `main` is unchanged; no #220 feature branch remains; the worktree is clean.
