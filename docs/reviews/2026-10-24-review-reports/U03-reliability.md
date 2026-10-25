# U03 (Arcanum.Api, Intelligence 3) - Reliability lens

Tree: `/Users/mat/Source/apps/RetroDownfall.Arcanum/.claude/worktrees/codebase-review` at `ac0d2d8e` (read-only).
Probes ran in a scratch worktree (`scratchpad/review/wt/U03-reliability`, now removed). Probe sources are archived at
`scratchpad/review/results/A/U03ReliabilityProbeTests.cs` and `U03ReliabilityProbeTests2.cs`; full output at
`scratchpad/review/results/A/U03-probes.log`.

## Unit membership

`units.json` was absent when this reviewer ran. The unit was reconstructed from the label and size: the Api
`Intelligence/` root files (subdirectories excluded) sorted by name and greedily chunked give
`(11735 | 11588 | 7134 | 8904)`, and the only contiguous run of exactly 7,134 lines is the twelve files below
(the U03 usability reviewer reconstructed the identical list independently).

## Coverage

All 12 files read in full, every line:

| File | Lines |
|---|---|
| `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs` | 2490 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/ToolLoopProgressDetector.cs` | 138 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/ToolResultMaterializer.cs` | 368 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingAmbient.cs` | 101 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs` | 632 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAmbientSet.cs` | 132 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs` | 244 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/TurnIdempotencyAmbient.cs` | 63 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs` | 375 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs` | 1349 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/WebWorkflowEndpoints.cs` | 233 |
| `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs` | 1009 |

Owning docs read: DESIGN §10.7.3-10.7.6, §11.27, §21.1, §22 (billable boundary, missing usage, durable ordering,
run status, reservation scope), the Ward tool-inventory table, `Arcanum.CHAT-LOOP.md` (termination, no-progress,
crash table), API §8.10.3, Command Reference research section, Compendium web-workflow paragraph.

Callers and collaborators traced (relevant parts only): `WizardIntelligenceProvider.cs` (tool loop 3080-4275, turn
context 4880-5012, accounting completion 4800-4850, tool-set builder 7073-7290, usage recorders 8430-8510, RAG
embedding 5726-5815), `CovenantDispatchGate.cs` (Protection), `MemoryDelegationRootBinding.cs`,
`SessionAttachmentStore` (`PersistRefreshedAsync`, `ValidateResolvedSource`, `PersistNewCoreAsync`),
`LocalHttpWebProvider` (redirect ward), `PerplexityWebProvider` (query bound, citation indices),
`SanctumEgressWardAmbient`, `CovenantToolEgressGuard` + `external_disclosure_receipts.sql`,
`DataRetentionService.Pruning.cs` (Running-run blockers), `TurnRunWriter`, `EntryWeavingService`,
`OpenAiV1EmbeddingsEndpoints`, `ClientDisconnect`, `ApiBootstrapper` named-client registration,
`EmbeddingGeneratorFactory`, existing `TurnAccountingHandleTests`, `WebWorkflowEndpointTests`.

## Probes

| Probe | What it drives | Observed |
|---|---|---|
| P1 | `WebResearchWorkflowService.SearchAsync` with `AttachToSessionId`, store `PersistNewAsync` throws `AttachmentLimitExceededException` | 1 billed provider call, then the exception escapes `SearchAsync` (FAILED the contract assertion) |
| P2 | `ResearchAsync` with the same store | synthesis ran (billed), frames `Limits, searching x2, source_exhausted, fetching, rendering, synthesizing`, then the exception; no `result` frame |
| P3 | `ResearchAsync`, pass 1 cites a.test/1, a.test/2; pass 2's summary "Gamma is true [1]." cites c.test/3 as its [1] | prompt contains `Gamma is true [1].` while `Source [1]` = a.test/1; result citation [3] = c.test/3 |
| P4 | `ResearchAsync`, each pass a 3,505-char answer and one new URL for 10 passes | 11 passes, `source_budget_reached: Stopped fetching at source 1 of 10`, pages read 0, citations returned 0, prompt 32,768 chars |
| P5 | `ToolLoopProgressDetector` fed what the Wizard loop feeds it once `Protection` is set (blank args/results) | two `read_file_chunk` rounds with different arguments: second round reported repeated (true); unblanked: false |
| P6 | `TurnAccountingHandle.BeginAsync` + `CompleteAsync(Completed)` with `ReconcileAsync` throwing | exception, `CompleteRunAsync` never called, `IsSettled=True` |
| P7 | real `OpenAI.Embeddings.EmbeddingClient` over a handler slower than `NetworkTimeout`, then `WeaveService.EmbedBatchAsync` | SDK throws `AggregateException: Retry failed after 4 tries` (4 HTTP attempts); `WeaveService` returns `ProviderUnavailable` (passed) |
| P8 | `ToolExecutionPipeline.RefreshSessionAttachmentAsync`, store throws its own provenance `InvalidOperationException` | `Attachment.LimitExceeded`, message "The attachment source hash does not match the verified bytes." |

## Findings

### 1. Important - Research/search/browse lose the billed answer when the attachment store throws
`WebResearchWorkflowService.cs:915` (`AttachAsync`). `AttachAsync` only converts "attachments disabled" and "session
not found" into a `Result`; `ISessionAttachmentStore.PersistNewAsync` throws for its ordinary refusals
(`AttachmentLimitExceededException` at the code-owned 256 MiB `MaxBytesPerSession`, encryption/blob faults, disk
full). The exception escapes after the provider work is billed: search/browse become a 500, research ends with the
generic `Hub.Error` "research stream failed unexpectedly" frame and never emits `result`. DESIGN §11.27 and API
§8.10.3 promise a non-terminal `attachmentError` / `attachment_failed`. Existing tests cover only the vanished-session
`Result` path. Probes P1, P2. Fix: wrap `PersistNewAsync` in `try/catch (Exception ex) when (ex is not
OperationCanceledException || !cancellationToken.IsCancellationRequested)` returning a typed failure
(`Attachment.LimitExceeded` for `AttachmentLimitExceededException`, otherwise an attachment error), and add a test
with a throwing store. Effort S.

### 2. Important - Research citations point at the wrong URLs
`WebResearchWorkflowService.cs:1141-1198` with numbering at `:548-553`. Search summaries are copied into the synthesis
prompt with the provider's own per-pass `[n]` markers (Perplexity indices; `PerplexityWebProvider` preserves them),
while sources are renumbered contiguously across passes and after denials (`sources.Count + 1`). The instruction
says "Cite claims using only the supplied [n] numbers", so a claim the model takes from summary 2 or later, or from
any pass where a citation was denied, skipped by `MaxCitations`, or deduplicated, is cited to a different URL in the
final answer. Probe P3 shows `Gamma is true [1].` beside `Source [1]` = a.test/1 while Gamma's URL is `[3]`. Fix:
rewrite each summary's markers to the research index of the same URL (per pass: provider index -> URL -> source
index), dropping markers whose URL was not read; or strip the markers from summaries. Effort M.

### 3. Important - Accumulated search summaries starve every source, so long runs return no citations
`WebResearchWorkflowService.cs:783-801` and `:1159-1166`. Every pass's answer is retained and placed before any
source, and `ResolveRetainedContentBudget` subtracts all of them from the 32,768-char synthesis budget
(`MaxPingPromptChars` default). With ordinary 3.5k-char answers, about nine passes exhaust it: P4 ran 11 passes, read
0 pages, and returned 0 citations from a 32,768-char prompt made only of summaries. The loop has no hop counter by
owner policy, so run length, not the operator, decides whether any source is read. Fix: give summaries a bounded
share (for example newest-first within a third of the budget) and reserve the rest for sources. Effort S-M.

### 4. Important - No-progress false positive in memory-delegation turns
`WizardIntelligenceProvider.cs:4069-4073` (and `:3890-3894`), relatedFiles `ToolLoopProgressDetector.cs`. Once
`covenantScope.Protection` is non-null (`BindProtection(memoryDelegation.CaptureOutputProtection)` at `:701`; non-null
after the delegation root is consumed), each round's progress entry is recorded with empty arguments and result.
Two rounds calling the same tool on different inputs, e.g. `read_file_chunk(a)` then `read_file_chunk(b)`, then hash
identically and the turn stops with `Hub.NoProgressDetected` (`:4252-4269`). DESIGN §10.7.3 and CHAT-LOOP say
different arguments or a new result is progress. P5 shows the collision. Fix: give the detector a keyed digest
(HMAC-SHA256 with a per-turn random key) of the protected arguments and result, so equality is kept without a stable
digest, and keep `RepeatedSignature` out of the log for protected turns. Effort S.

### 5. Minor - A failed reservation settlement leaves the run Running forever
`TurnAccountingHandle.cs:346-382`; callers `WeaveService.cs:245-257` (logs at Debug) and
`WizardIntelligenceProvider.cs:4832-4846` (Warning). If `ReconcileAsync`/`ReleaseAsync` throws, the run disposition
is never tried, and only the tests retry `CompleteAsync`; no production caller does. The `InferenceRuns` row stays
`Running` (`Data.InferenceRunActive` retention conflict for the whole run chain, and no sweep exists, as BeginAsync's
own comment notes), and the reservation stays `Reserved`, counted in that day's outstanding spend. P6. Fix: in
`CompleteAsync` try the run disposition in a `finally` even when the reservation step throws, then rethrow; log the
WeaveService failure at Warning. Effort S.

### 6. Minor - Refresh reports provenance failures as "attachment budget exhausted"
`ToolExecutionPipeline.cs:961`. `catch (InvalidOperationException)` catches `AttachmentLimitExceededException` (a
subclass) and also `SessionAttachmentStore.ValidateResolvedSource`'s provenance and hash refusals and "Only a bound
session attachment can be refreshed". All of them become `attachment_budget_exhausted`, which is
`Attachment.LimitExceeded` on the operator route. P8. Fix: catch `AttachmentLimitExceededException` for the budget
code, and map other refusals to `source_unavailable`. Effort S.

### 7. Minor - The research question bound assumes fewer than 1,000 passes, but nothing enforces that
`WebResearchWorkflowService.cs:940-941` and `:931-932`. The reservation is sized for a 3-digit pass number ("longer
than any run reaches"), but the loop has no ceiling. At pass 1000, a question at the maximum length produces a
4,001-char query. `PerplexityWebProvider` rejects it (`RequestRejected`, :70-75), and the run ends in an error frame
after 999 billed passes. Traced, not executed. Fix: drop the pass number from the suffix (or cap its width), or clamp
the question per pass. Effort S.

### 8. Minor (outside the unit's files, surfaced through WeaveService) - SDK 100 s per-try timeout and 4 tries on every embedding batch
`EmbeddingGeneratorFactory.cs:134-140`, relatedFiles `WeaveService.cs`, `ChatClientFactory.cs`. `OpenAIClientOptions`
is built with neither `NetworkTimeout` nor `RetryPolicy` (no `NetworkTimeout` anywhere under `src`), so the
System.ClientModel defaults apply: 100 s per attempt, 3 retries. P7 measured 4 HTTP attempts and `AggregateException`
with a short timeout. A batch that legitimately needs more than 100 s (CPU-only or cold local model) is cut and
re-sent three times, about 7 minutes of provider work, then reported `ProviderUnavailable`, and the background ticks
repeat it. That contradicts DESIGN §21.1 ("no Arcanum whole-operation deadline"). Paid providers may bill the
timed-out attempts that Arcanum never records. Fix: set `NetworkTimeout = Timeout.InfiniteTimeSpan` (caller
cancellation already bounds the wait) and a retry policy with no retries for the billable POST. Hand this to the
unit owning `EmbeddingGeneratorFactory`/`ChatClientFactory`. Effort S.

## Checked and found sound

- Cancellation in `ToolExecutionPipeline.ProcessSingleToolCallAsync`: only the caller's OCE propagates; a tool's own TaskCanceledException is tolerated; classified `apply_patch` failures rethrow, matching §10.7.4. `suppressInvocationFailures` is always true in production (`WizardIntelligenceProvider.cs:3085`).
- Sanctum preflight: an unreadable policy refuses every call; audit-only collects every breach; escaping paths are anchored to the workspace root; the redirect ward is published only for `read_url`/`browse_web` and `LocalHttpWebProvider` calls it only on redirect hops, which matches AuditOnly letting the initial target go on.
- `retire_covenant`: disclosure is written before the effect. The constant `PhysicalAttemptOrdinal: 1` cannot collide, because the journal assigns `CategoryPhysicalAttemptOrdinal` and the effect identity includes the call id and a fresh nonce (unique indexes checked).
- `ToolLoopProgressDetector` ring logic (8-slot window, length-prefixed SHA-256) is correct for unprotected input.
- `ToolResultMaterializer`: output never exceeds the char or byte budget; slicing is surrogate-safe; the structured binary search is monotone; the minimal envelope is the fallback.
- `TurnAccountingAmbient` Push/Suspend/Clear restore semantics, with a settled handle hidden. `TurnAmbientSet` publishes the Session id only together with the ledger and budget, and clears Covenant staging per round. `TurnIdempotencyAmbient` identity is domain-separated and keeps the ownership token.
- `TurnContextGuards.TryTrimOldestToolExchanges` removes whole parallel-call runs and re-measures before its verdict.
- `TurnAccountingHandle`: the reservation gate is released in `finally`; a failed renewal is tolerated; a durable-write failure holds the reservation (documented). Every production `RecordUsageAsync` caller passes `CancellationToken.None` (checked), so caller cancellation cannot mark accounting failed.
- `WeaveService`: one vector per input is enforced after billing; the scope is disposed when `BeginAsync` throws; an SDK timeout becomes `ProviderUnavailable` (P7). `ChunkAsync` cannot leave a surrogate gap, because overlap is code-owned at 100.
- `WebWorkflowEndpoints`: every exit lands on a frame except a client disconnect. Providers map their own deadlines to `Result`, so no non-caller OCE reaches the swallow-OCE catch.
- ContextPreview: the lease, Covenant scope and Campaign snapshot are disposed on every path; the AsyncLocal observer is restored in `finally`; retrievals are sequential, so the `List.Add` observer is not raced.
- Not reported as documented owner policy: the unbounded research discovery loop (no hop counter), `web_search` egress outside the Sanctum network allowlist (Ward inventory table), and embedding token counts estimated as length/4.
