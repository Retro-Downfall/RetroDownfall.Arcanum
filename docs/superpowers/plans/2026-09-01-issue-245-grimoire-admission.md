# Issue #245 Grimoire Admission Gate Completion Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete and harden the already-present process-local Grimoire request, work, and physical-open admission gate so every pre-close capability stays generation-bound, effect-frontier winners drain without maintenance cancellation, and native-open closure waits for an explicit terminal outcome.

**Architecture:** Retain the existing singleton `IGrimoireConnectionAdmissionGate` and its lock-linearized two-stage state machine. Tighten only the primitive #245 boundary: external-effect revocation, open-ticket terminality, exact promoted-connection authority, and next-generation invalidation. Do not activate the gate in HTTP requests or background workers and do not extend the #246 maintenance/acquisition surface already overlaid on the same files.

**Tech Stack:** .NET 10, C# 13, xUnit, `TaskCompletionSource`/manual-time deterministic race tests, linked Git worktree, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`

## Global Constraints

- Work on `codex/issue-245-host-admission`, based on `grimoire-fixes` commit `2d65d1211dd68b5f4161c9ce9a409786032943eb`, and merge only into `grimoire-fixes`.
- Preserve the two unrelated untracked issue-221 duplicate documents exactly as found; never stage, edit, delete, or move them.
- Scope is the live #245 child only: generation-bound request/work/open capabilities, two-stage closure, atomic external-effect frontiers, exact initiator promotion, owner-only disposition, and deterministic primitive races.
- Do not absorb #246–#256: no new EF/raw acquisition integration, physical drain, maintenance factory, launch-row codec, transition handler, startup recovery, HTTP middleware/error, stream quiescence, or worker wiring.
- `ICovenantOperationGate` remains separate durable destructive-operation authority. This gate owns only process-local live-Grimoire admission.
- A started external-effect group receives no maintenance revocation; stage 1 waits through the group and its durable disposition. A revocation winner starts no external effect.
- `IGrimoireConnectionOpenTicket` remains unresolved until `MarkOpened`, `MarkFailed`, or `MarkRefusedAfterOpen` supplies the explicit terminal outcome. Disposal cannot manufacture that outcome.
- A promoted request may use only its exact scoped `DbConnection` during stage 1. Any ordinary lifetime minted before a reopen is invalid in every later closing generation.
- Follow strict RED → GREEN → REFACTOR for every production change. Use deterministic barriers/manual time and no sleeps.
- Preserve Native-AOT compatibility, existing internal contracts, C# vertical-whitespace style, and zero build warnings/errors.
- Run the focused gate suite during development. Reserve the parent-wide coverage, AOT, benchmark, native SQLCipher, and full-host qualification matrix for #257 as required by the approved parent spec.

---

### Task 1: Preserve the winning external-effect group

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`

**Interfaces:**
- Consumes: `IGrimoireWorkLease.TryBeginExternalEffectGroup`, `IGrimoireWorkLease.MaintenanceRevocation`, and `IGrimoireConnectionAdmissionGate.BeginOrResumeExclusive`.
- Produces: the invariant that the effect-frontier winner drains to explicit group/scope disposal without receiving maintenance cancellation.

- [ ] **Step 1: Make the existing effect-wins test assert the missing behavior**

In `Effect_start_wins_race_and_closure_waits_through_durable_disposition`, immediately after beginning closure, add these independently observable assertions:

```csharp
Assert.False(work.MaintenanceRevocation.IsCancellationRequested);

Assert.False(work.TryBeginExternalEffectGroup(
    out IGrimoireExternalEffectGroup? secondEffectGroup));

Assert.Null(secondEffectGroup);
```

The mutation this catches is the current unconditional revocation of every work lease after an effect group has already won.

- [ ] **Step 2: Run the single test and observe RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Effect_start_wins_race_and_closure_waits_through_durable_disposition"
```

Expected: FAIL because `work.MaintenanceRevocation.IsCancellationRequested` is `true`.

- [ ] **Step 3: Revoke only work whose external-effect frontier has not won**

In `BeginOrResumeExclusive`, replace unconditional work revocation selection with the lock-protected frontier condition:

```csharp
revocations.AddRange(
    _workLeases
        .Where(static work => work.ActiveEffectGroup is null)
        .Select(static work => work.Revocation));
```

Do not cancel an already-started group after it disposes. The closing state already prevents another group from starting, and the work lease remains in `_workLeases` until both its group and scope dispose.

- [ ] **Step 4: Run the single test GREEN, then the focused gate suite**

Run the Step 2 command, then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAdmissionGateTests"
```

Expected: the single test and the complete gate suite pass with zero failures and no warnings.

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs
git commit -m "fix: preserve active Grimoire effect groups"
```

---

### Task 2: Require an explicit native-open terminal outcome

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`

**Interfaces:**
- Consumes: `IGrimoireConnectionOpenTicket.Dispose`, `RevalidateAfterNativeOpen`, `MarkOpened`, and `MarkRefusedAfterOpen`.
- Produces: disposal is cleanup only after terminality; it cannot remove a native-open attempt from the stage-2 unresolved set.

- [ ] **Step 1: Add the disposal regression test**

Add this test beside the existing physical-open race tests:

```csharp
[Fact]
public async Task Disposal_cannot_terminalize_a_native_open_without_an_explicit_outcome()
{

    GrimoireConnectionAdmissionGate gate = CreateGate();

    using SqliteConnection connection = new();

    IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(connection);

    Result revalidated = ticket.RevalidateAfterNativeOpen();

    Assert.True(
        revalidated.IsSuccess,
        revalidated.IsFailure ? revalidated.Error.Message : null);

    _ = Assert.Throws<InvalidOperationException>(ticket.Dispose);

    await using IGrimoireClosingOwner closing = Begin(gate, Owner(24));

    Task<Result<IGrimoireExclusiveClosedLease>> close = gate
        .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
        .AsTask();

    Assert.False(close.IsCompleted);

    Result opened = ticket.MarkOpened();

    Assert.True(opened.IsFailure);

    Assert.False(close.IsCompleted);

    ticket.MarkRefusedAfterOpen();

    Result<IGrimoireExclusiveClosedLease> closed = await close;

    Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

    await using IGrimoireExclusiveClosedLease lease = closed.Value;

    ticket.Dispose();

    Result keptClosed = await lease.CompleteAsync(
        CovenantExclusiveLeaseDisposition.KeepClosed,
        CancellationToken.None);

    Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

}
```

The mutation this catches is treating `Dispose()` as though it were `MarkFailed()` while the native operation is still opening or has already opened and awaits enrollment/refusal.

- [ ] **Step 2: Run the single test and observe RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Disposal_cannot_terminalize_a_native_open_without_an_explicit_outcome"
```

Expected: FAIL because premature `Dispose()` currently succeeds and removes the ticket from `_unresolvedOpens`.

- [ ] **Step 3: Make disposal validate terminality before becoming disposed**

Change `DisposeTicket` so it never calls `CompleteTicketWhileLocked` and instead rejects a nonterminal ticket:

```csharp
private void DisposeTicket(OpenTicket ticket)
{

    lock (_sync)
    {

        if (ticket.State is not OpenTicketState.Terminal)
        {

            throw new InvalidOperationException(
                "A Grimoire open ticket requires an explicit terminal outcome before disposal.");

        }

    }

}
```

In `OpenTicket.Dispose`, call `gate.DisposeTicket(this)` before setting `_disposed`, so a rejected premature disposal leaves `MarkOpened`, `MarkFailed`, or `MarkRefusedAfterOpen` available to report the real native outcome:

```csharp
public void Dispose()
{

    if (Volatile.Read(ref _disposed) != 0)
    {

        return;

    }

    gate.DisposeTicket(this);

    if (Interlocked.Exchange(ref _disposed, 1) != 0)
    {

        return;

    }

    GC.SuppressFinalize(this);

}
```

- [ ] **Step 4: Run the single test GREEN, then the focused gate suite**

Run the Step 2 command, followed by the complete focused gate-suite command from Task 1 Step 4.

Expected: all focused tests pass, including every existing interceptor-style `using` path that explicitly reports terminality before disposal.

- [ ] **Step 5: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs
git commit -m "fix: require explicit Grimoire open outcomes"
```

---

### Task 3: Bind promotion to one connection and invalidate prior generations

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`

**Interfaces:**
- Consumes: `BeginOrResumeExclusive(owner, initiatingRequest, scopedConnection)`, `AcquireOrdinaryOpen`, and the flow-bound `OrdinaryLifetime` chain.
- Produces: exact connection authority for the promoted request and exact-generation authority for every ordinary finisher lifetime.

- [ ] **Step 1: Add RED coverage for exact promoted connection authority**

Add:

```csharp
[Fact]
public async Task Promotion_allows_only_the_exact_scoped_connection_during_stage_one()
{

    GrimoireConnectionAdmissionGate gate = CreateGate();

    Assert.True(gate.TryAcquireRequestLease(
        GrimoireRequestKind.Finite,
        out IGrimoireRequestLease? initiating));

    using SqliteConnection exact = new();

    using SqliteConnection foreign = new();

    await using IGrimoireClosingOwner closing = Begin(
        gate,
        Owner(25),
        initiating,
        exact);

    using IGrimoireConnectionOpenTicket admitted = gate.AcquireOrdinaryOpen(exact);

    admitted.MarkFailed();

    _ = Assert.Throws<GrimoireMaintenanceUnavailableException>(
        () => gate.AcquireOrdinaryOpen(foreign));

    await initiating!.DisposeAsync();

}
```

The mutation this catches is treating a promoted request's flow marker as general stage-one open authority instead of authority for the bound `DbConnection`.

- [ ] **Step 2: Add RED coverage for a stale lifetime crossing reopen**

Add a deterministic manual-time test named `Pre_reopen_lifetime_cannot_authorize_a_later_closing_generation`:

1. Acquire one finite initiating request lease and one work lease in the same async flow.
2. Promote the request into owner 26 and start stage-1 drain.
3. Advance `ManualTimeProvider` by `OpeningTimeout` and prove the drain times out on the work lease.
4. Dispose the work lease and call `AbortClosingAsync` with exact pre-effect safety proof.
5. Keep the promoted request undisposed, begin a second closure under owner 27, and assert that `AcquireOrdinaryOpen` throws `GrimoireMaintenanceUnavailableException`.
6. Dispose the stale request, finish stage 2, and select `KeepClosed` so the test leaves no live gate authority.

The mutation this catches is `lifetime.Generation <= _generation`, which lets generation N flow authority become valid again during closing generation N+1.

- [ ] **Step 3: Run both tests and observe RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Promotion_allows_only_the_exact_scoped_connection_during_stage_one|FullyQualifiedName~Pre_reopen_lifetime_cannot_authorize_a_later_closing_generation"
```

Expected: both tests FAIL because current finisher checks accept any connection and every earlier lifetime generation.

- [ ] **Step 4: Bind the promoted lifetime and require exact generation**

Pass the connection into the finisher check:

```csharp
if (_state == GateState.Closed
    || (_state == GateState.Closing
        && !HasLiveFinisherLifetimeWhileLocked(connection)))
```

When promotion linearizes, bind the exact connection to the request's flow lifetime:

```csharp
promoted.IsPromoted = true;

promoted.Lifetime.PromotedConnection = scopedConnection;
```

Add `DbConnection? PromotedConnection` to `OrdinaryLifetime`, then require both the current generation and either ordinary finisher authority or the exact promoted connection:

```csharp
private bool HasLiveFinisherLifetimeWhileLocked(DbConnection connection)
{

    for (OrdinaryLifetime? lifetime = CurrentOrdinaryLifetime.Value;
        lifetime is not null;
        lifetime = lifetime.Previous)
    {

        if (ReferenceEquals(lifetime.Gate, this)
            && !lifetime.IsReleased
            && lifetime.Generation == _generation
            && (lifetime.PromotedConnection is null
                || ReferenceEquals(lifetime.PromotedConnection, connection)))
        {

            return true;

        }

    }

    return false;

}
```

- [ ] **Step 5: Run both tests GREEN, then the focused gate suite**

Run the Step 3 command, followed by the complete focused gate-suite command from Task 1 Step 4.

Expected: both regression tests and the full gate suite pass with zero failures.

- [ ] **Step 6: Commit the slice**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs
git commit -m "fix: bind Grimoire finisher authority"
```

---

### Task 4: Publish the delivered child contract and qualify the branch

**Files:**
- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`
- Include: `docs/superpowers/plans/2026-09-01-issue-245-grimoire-admission.md`

**Interfaces:**
- Consumes: the reviewed #245 implementation and the approved parent child-boundary table.
- Produces: truthful repository documentation that distinguishes the delivered process-local primitive from all unintegrated #246–#256 behavior.

- [ ] **Step 1: Update the owning documents without claiming activation**

In `README.md`, add an issue #245 paragraph after #244 stating:

- the singleton gate and its generation-bound request/work/open/owner capabilities are implemented;
- stage 1 drains request/work and stage 2 resolves native-open attempts;
- an external-effect frontier either refuses before provider I/O or drains through durable disposition;
- promoted access is bound to one request, connection, and generation;
- request middleware, worker adoption, complete EF/raw acquisition inventory, transition handlers, startup, API responses, and stream behavior remain later children; and
- the existing V3 runtime path remains active.

In `docs/Arcanum.DESIGN.md` §10.20.3, add the same architecture without naming tracker issue numbers, and update the section overview so the delivered gate is no longer described as absent.

In the parent spec header, replace the stale “pending user approval and replacement implementation plan” status with an approved-umbrella status that records #243/#244 as integrated and #245 as the current delivered child, without marking later children complete.

Do not edit `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`, or configuration documentation.

- [ ] **Step 2: Run focused implementation and documentation tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAdmissionGateTests|FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~DocumentationStructureTests|FullyQualifiedName~RetiredServerNamespaceTests"
```

Expected: all selected tests pass with zero failures and no warnings.

- [ ] **Step 3: Check changed C# formatting and repository diff hygiene**

Run:

```bash
./scripts/align-csharp-blanklines.sh --check src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs
git diff --check
```

Expected: both commands exit 0.

- [ ] **Step 4: Run the warning-free Release solution build once**

Run:

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --disable-build-servers -m:1
```

Expected: exit 0 with 0 warnings and 0 errors.

The parent-wide `coverage.sh --threshold`, Native AOT/IL, Covenant benchmark, native SQLCipher provenance, packaging, full-host, and cross-platform matrices are intentionally not duplicated for this isolated child. The approved parent spec assigns that unchanged-SHA qualification to #257 after every child is integrated.

- [ ] **Step 5: Commit the documentation and plan**

```bash
git add README.md docs/Arcanum.DESIGN.md docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md docs/superpowers/plans/2026-09-01-issue-245-grimoire-admission.md
git commit -m "docs: publish issue 245 admission contract"
```

- [ ] **Step 6: Review and delivery sequence**

1. Run one task review after each task and one whole-branch review after Task 4.
2. Resolve every Critical and Important finding with a focused RED/GREEN fix and scoped re-review.
3. Merge `codex/issue-245-host-admission` into `grimoire-fixes` without touching unrelated untracked files.
4. Verify the merged tree matches the reviewed feature tree; do not rerun identical gates on a byte-identical merge.
5. Delete the local feature branch and any remote feature branch created by this plan.
6. Push `grimoire-fixes`, verify `origin/grimoire-fixes` resolves to the delivered SHA, post the exact verification evidence to issue #245, close it as completed, and verify its Feature Tracker status is `Done`.

