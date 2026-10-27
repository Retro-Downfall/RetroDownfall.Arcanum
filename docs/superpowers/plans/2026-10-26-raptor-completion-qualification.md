# RAPTOR bounded offline completion qualification plan

> **For agentic workers:** Execute inline with `superpowers:executing-plans` after root reviews this plan. No children. This document proposes budgets; it does not authorize starting a run. Preserve reviewed78c54bd3/382d3011 branches and packages. Root owns integration, final campaign CI and the live/install pause.

**Goal:** Complete a measured50k neighbor graph and10k global/local numerical composition on the repaired kernels, retaining exact numerical/quality contracts and honest incomplete outcomes.

**Architecture:** Add test-harness-only, explicitly opted-in completion probes around the existing real managed kernels and default `RaptorClusterer`. Run each workload in a fresh process with fixed time/memory/work containment, terminal TRX and content-free progress/receipts. No algorithm, manifest, provider, configuration or product-gate edit is proposed.

**Tech stack:** .NET10/Arm64, existing xUnit harness, deterministic768-dimensional synthetic float corpora, native-free numerical kernels, existing cancellation and source/receipt controls.

**Spec:** `docs/superpowers/plans/2026-10-25-real-raptor.md` Task12 and `docs/superpowers/specs/2026-10-25-real-raptor.md`, Evidence/release acceptance; owning neighbor protocol `scripts/raptor-reference/benchmark.md`.

## Scope and fixed contracts

This slice means **complete50k neighbor graph**, **complete10k standalone global numerical stages**, and **complete10k default global/local clusterer**. It does not claim50k UMAP/GMM/full clusterer/recursive tree throughput, live summaries/re-embeddings, shipping AOT or overallTask12 completion. This interpretation is explicit for root's review; expanding it requires a separately bounded plan.

Resolve the clean controller checkpoint supplied at approval (current reported checkpoint616d04dc/runtime7463a68c) and make a new continuation branch. Before any runtime measurement verify all8 kernel/manifest hashes equal reviewed78c54bd3; report any unexpected delta instead of silently changing the baseline. Preserve `codex/review-raptor-pair-work` at78c54bd3 exactly.

Keep manifest `8a4be77332ff695fbfae7e4955d7e874b37db8707cd8fb8382764486faec3b5e`, all seeds/recipe/constants, scalar miss math, canonical ordering, candidate/new sets, search/returned widths, comparator, maximum12descent rounds, full covariance, BIC K1..49, threshold0.1, dimensions10 and UMAP epochs unchanged. **10k is at the inclusive10,000boundary and runs500UMAP epochs; only counts above10,000use200.** Never infer200epochs for10k or reduce iterations to meet the budget.

Retain the exact corpus generator/ordered IDs; no private data or model/provider access. Existing10k corpus hash `1666be3372195f72acc9e450f84082b34e4b94f4335255d488fe1e6838a03123`;50k hash `e9a8589f0e8a63d3c96daf663a6eddce62d1b97488582bcb7ed2be5a1b3e6ee0`. Global k is floor(sqrt(n-1)),99/223; search widths196/444. Actual default clusterer uses its existing identity-derived per-stage seeds and local k10, not the standalone751/752/753 streams. Standalone stage metrics are not relabeled as the default clusterer's internal metrics.

## Measured basis, uncertainty and budget derivation

Reviewed A+B10k completed47.013s:7,171,119scalar evaluations,18,697,398requests,11,526,279hits,462,712evictions,934,258,686membership visits,2rounds,990,000edges, recall1.0. Its first actual-descent callback occurred around13.918s, after4,402,754scalar evaluations. A pre-final repeat completed41.245s; this observed shared-host variation is retained, not averaged into an SLO.

The older50k profile completed initialization around244.52s/73,264,806scalar evaluations, then spent196.17s on only754old-descent rows. Its old repeated-pair descent extrapolation is obsolete after B. We do not extrapolate the old6b-request round as the repaired workload.

A rough repaired two-round enumeration scaling is `5*(444/196)^2 =25.6581`, or about23.97b membership visits. Applying that conservative factor to the *entire*47.013s10k time gives about1,206s (~20.1min). This is an estimate, not a complexity guarantee:50k neighborhoods, memo residency, round count and shared-host scheduling differ. A fixed2,400s (40min) graph containment is proposed, roughly twice that estimate. It follows a reviewed algorithmic repair and explicit work model; it is not another timeout-only rerun of the failed old algorithm.

Memory remains a hard concern, not an assumed pass. Exact new pair payload at50k is178,400,004B; output CSR is134,000,004B. Adding both conservatively to the old3,786,686,464B process peak gives4,099,086,472B, only195,880,824B below4GiB. This is not a guaranteed peak: array lifetimes, headers, GC/native/test-host variance and sampled overshoot matter. Retain4GiB, sample every10ms, and report a memory-triggered run as INCOMPLETE; do not increase the ceiling to rescue it or assume physical RAM availability.

For10k standalone global stages: measured neighbor47s; the1k500-epoch UMAP took4.612s over39,762fuzzy edges, and49BICcandidates took6.032s. An O(n*k) edge ratio at10k is about31.94, suggesting roughly147sUMAP before graph-density/weight and scheduling differences; a simple n scaling suggests60sBIC before convergence differences. Proposed stage limits below leave substantial measurement headroom without altering any iteration/candidate rules. The1k actual default clusterer completed16.175s with164clusters/all1,000sources. This does not establish10k composition time; full composition gets one fixed1,800s budget, not a success claim from scaling.

## Acceptance versus containment

| Run | Acceptance required for success | Fixed containment (cancellation means INCOMPLETE) |
|---|---|---|
|50k complete graph|Successful Result;50,000points/k223/11,150,000edges; complete valid CSR/self/finite ordered neighbors; exact corpus/manifest; scalar evaluations **strictly below1,249,975,000** exact all-pairs; sampled exact nonself recall>=.95; truthful memo/heap/pair counters|2,400s neighbor timer;4GiB sampled process peak;100,000,000,000membership-visit measurement ceiling; caller cancellation. A separate120s validation/recall deadline; outer process watchdog2,640s.|
|10k standalone global pipeline|Complete neighbor/fuzzy/500-epoch UMAP and full K1..49 BIC; valid finite10d coordinates with exact source mapping; converged finite selected mixture/normalized posteriors; neighbor scalar work<49,995,000and recall>=.95; retain frozen10k neighbor fingerprint|Neighbor240s; fuzzy60s; UMAP600s; BIC600s; validation/receipt120s; one total1,620s linked caller deadline and outer1,740s watchdog;4GiB throughout.|
|10k actual default global/local clusterer|Actual `new RaptorClusterer().Cluster` successful;10,000sources all represented; zero unassigned; valid source indices and finite probabilities; canonically ordered identities and deterministic fingerprint recorded; no dropped source or partial Result promoted to success|One1,800s linked caller deadline and outer1,920s watchdog;4GiB. No substituted injected cluster result or simplified local algorithm.|

The50k scalar bound is the same predeclared minimum rationale for approximation used at10k: strictly less actual work than exact all-pairs. Reaching that bound is a terminal **work-acceptance failure** with exact last phase/counters, not ordinary timeout. Recall below.95 is a quality failure. Time, memory, membership-visit or caller cancellation is **INCOMPLETE**, not proof of an algorithmic work/quality defect and never counted as completed scale qualification. The100b membership ceiling is containment only; it is about4.17times the two-round estimate and does not change the12-round algorithm. Its theoretical maximum per round is39,516,000,000visits, so the counter remains intelligible if convergence differs.

Output remains bounded: only source/corpus/graph/layout/fit/membership hashes, safe counts, phase/pass, stage outcome and runtime/memory. Do not write vectors, source text, individual identities or huge graph dumps. Progress sidecars must be rate-limited and capped, with terminal counters authoritative. Treat `descent` and `descent-pair-enumeration` as one checkpoint family for phase/window dedup so alternation cannot exhaust the1,024-record cap before later rounds. Keep actual phase separately in each record. Provide a live content-free sidecar in the per-run receipt directory so a40min process remains observable; this is a test-only artifact destination, not product configuration.

## Files and review focus

- Modify `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/Raptor/RaptorScaleProbe.cs`: make the existing class partial if needed; retain generator/oracles and extend linked budget/receipt helpers without changing numerical kernels.
- Create `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/Raptor/RaptorScaleProbe.Completion.cs`: opt-in50k/10k completion probes and cheap behavioral helper controls. This is the **same** `RaptorScaleProbe` class; do not count the partial file as another class.
- Modify `scripts/raptor-reference/benchmark.md` and owning DESIGN testing paragraph with exact accepted limits/results and unresolved gates; add the approved tracked plan on the new branch.
- No production kernel, schema, API, provider, service, configuration or source-catalog exception change.

Review focus: cancellation during enumeration and oracle validation; result refusal misreported as completed; progress cap exhausted by alternating phases;10k inclusive500-epoch boundary; silently replacing real default composition with instrumented approximations. Pin each below using cheap real-kernel/helper controls and causal mutants.

## Task1: Harness contracts with genuine RED/GREEN

- [ ] Write opt-in methods `Complete_neighbor_graph_at_fifty_thousand`, `Complete_global_pipeline_at_ten_thousand`, and `Complete_real_clusterer_at_ten_thousand`, using existing real kernels/default clusterer and the exact table above. Default ordinary discovery must skip them with a named reason; no accidental long run.
- [ ] Add cheap controls for natural completion, returned failure, exact work-bound failure, caller/time/memory/enumeration containment and cancellation during validation. Record accepted/completed/incomplete/refused separately; a returned failure must not acquire completion credit merely because a call returned normally. Use bounded small synthetic fixtures and controlled cancellation, never a long calibration run to manufacture RED.
- [ ] Add checkpoint-policy RED that alternates the two descent phases across multiple rounds/windows; the later pass must remain represented within the closed cap. Make the smallest harness-only correction and prove GREEN.
- [ ] Keep `AssertRecall` arithmetic/order unchanged; add token polling to row/pair traversal so validation obeys its linked deadline. Test supplied-token cancellation with actual oracle work. Preserve exact completed small-oracle bits/recall.
- [ ] Run complete RaptorScaleProbe/RaptorNeighborRoundStateTests and relevant neighbor/reference/clusterer classes. Causal mutants: invent success for containment/refusal; drop checkpoint-family dedup; ignore oracle cancellation; bypass a completion acceptance check. Exact source restoration mandatory. Existing A+B production mutants remain reused; no repeat merely for a new harness filename.
- [ ] Update owning docs, normal build, owned styles and complete affected/guard census; freeze harness candidate SHA/tree. Record all kernel/test/owning-document hashes **before/after each isolated process** (not just the8kernels), exact commands/env, terminal TRX and process disposition. No numerical run starts until root approves the fixed plan.

## Task2: Three separate, sequential fresh-process runs

- [ ] Run10k standalone global pipeline first to detect full-stage numerical/refusal/time issues before the longer50k job. Then run actual default10k clusterer in a second fresh process. If either fails/cancels, stop and send exact phase/counters; do not launch50k automatically on a questionable composition candidate.
- [ ] If both complete and source is unchanged, run the single approved50k complete-neighbor process. Watch with asynchronous tool sessions and waits<=60s; inspect the rate-limited sidecar for meaningful phase/work/memory changes. Keep root informed; do not run other heavy tests concurrently in this worker. Record externally known concurrent-host jobs rather than promise an idle machine.
- [ ] A complete50k graph gets full CSR validation and the unchanged32evenly-spaced-row exact oracle (1,599,968nonself distances; threshold.95). Stream hashes; do not allocate another whole graph/corpus or claim exhaustive recall. The120s validation deadline is independent and linked to caller cancellation.
- [ ] For10k default composition, preserve existing scope/identity construction and report fingerprint, cluster count, membership count and complete source coverage. Do not infer per-stage default call counts from the separate standalone run. Pure Core kernels have no provider dependency; direct provider calls are structurally zero, not an estimate of paid summary cost.
- [ ] Every process has one terminal TRX plus exit code, outcome-specific receipt, exact source maps, time/counter/memory and cancellation-latency facts. No TRX or watchdog termination is never green. Any subsequent repeat requires a named defect/variance question and a new explicit ruling; there is no automatic timeout/memory/work increase.

Example approved-after-freeze commands (fresh directories, one process each):

```sh
ARCANUM_RAPTOR_SCALE_PROBE=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build -m:1 -nodeReuse:false --filter 'FullyQualifiedName~RaptorScaleProbe.Complete_global_pipeline_at_ten_thousand' --logger 'trx;LogFileName=probe.trx' --results-directory /private/tmp/raptor-completion-10k-global
ARCANUM_RAPTOR_SCALE_PROBE=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build -m:1 -nodeReuse:false --filter 'FullyQualifiedName~RaptorScaleProbe.Complete_real_clusterer_at_ten_thousand' --logger 'trx;LogFileName=probe.trx' --results-directory /private/tmp/raptor-completion-10k-clusterer
ARCANUM_RAPTOR_SCALE_PROBE=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build -m:1 -nodeReuse:false --filter 'FullyQualifiedName~RaptorScaleProbe.Complete_neighbor_graph_at_fifty_thousand' --logger 'trx;LogFileName=probe.trx' --results-directory /private/tmp/raptor-completion-50k-neighbors
```

The outer runner enforces the corresponding watchdog and supplies the test-only progress/receipt destination; exact executable command arrays and that destination are recorded after implementation. No new CLI/product option or model configuration is added.

## Task3: Exact-source evidence and stop/review

- [ ] Verify all final source maps/Git blobs and deduplicate successful IDs against ordinary skipped/previously executed tests. Preserve partial/failed attempts and all pre-existing382d/78c54bd3evidence unchanged.
- [ ] If a runtime/performance/correctness defect appears, capture its genuine behavioral RED and report measured cause before production changes. Time/memory cancellation alone cannot justify a recipe downgrade, new kernel or new budget. No endless partial-pilot sequence.
- [ ] Seal harness/source/evidence/report with exact SHA/tree and independent review. Report the exact completed envelopes and remaining gaps; even three successes do not close full50k composition, live summaries, shipping/fullCI or root's fullTask12 gates.

**State:** Plan only. No branch/code change, new measurement, provider discovery, installation or live call occurred while preparing it.
