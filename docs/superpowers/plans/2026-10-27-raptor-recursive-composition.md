# Offline Recursive RAPTOR Composition Qualification Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline, with no children. Await the controller's approval of this plan before branching, editing or running further scale work.

**Goal:** Qualify the real default numerical clusterer inside the real recursive Tapestry generation builder at 10k, then conditionally 50k synthetic 768-dimensional leaves, with durable SQLCipher publication and honest resource/effect receipts.

**Architecture:** Exercise `TapestryWeaver` → `TapestryBuildPlanner` → a counting forwarder around an unchanged default `RaptorClusterer`, backed by real `TapestryStore` and a fresh encrypted fixture database. Only summary text and embedding transport are deterministic offline substitutes. Each run uses one fresh process and the same source identity recipe; no alternate reducer, fitter, clustering recipe or product configuration is introduced.

**Tech Stack:** .NET10, xUnit, repository SQLCipher fixture, real numerical Core kernels, real Infrastructure storage/orchestration; Python outer process containment and immutable evidence receipts.

**Spec:** `docs/superpowers/plans/2026-10-25-real-raptor.md` Task12; DESIGN §21.11; measured candidate `c3ffb36faf0e6bfcb6185633b690dba526d130fd`.

## Global constraints

- Preserve the sealed c3ffb36f branch/package. Start a new branch only from the controller's next clean committed checkpoint after approval; recheck exact RAPTOR/weaver source equality and disclose any changed contract before execution.
- Synthetic/offline only. No live embeddings, Ollama, providers, discovery, installation, operator configuration, schema, service or production-kernel changes. Never describe fake summary/embedding effects as paid/live proof.
- Keep manifest `8a4be77332ff695fbfae7e4955d7e874b37db8707cd8fb8382764486faec3b5e`, all numerical constants, scalar arithmetic, memberships, iteration policies, tie ordering and identity-derived default seeds unchanged.
- Production defaults: five summary layers, maximum24 children per summary, maximum512 summary tokens; do not use obsolete target/cluster knobs or lower tree bounds to make the run pass.
- Time/memory limits below are containment, not product SLOs or estimates of exclusive-host performance. No automatic increase, hidden retry, reduced corpus, successful substitution or resume of an incomplete run.

## Measured basis and remaining uncertainty

The independently approved c3ffb36f packet contains terminal acceptance for10k global212.611s, actual default10k clusterer375.629s, and50k complete neighbor graph1764.448s including validation. The50k graph used384,553,630 scalar distances,3 descent rounds,11,150,000 edges, sampled recall1.0 and4,084,793,344B peak working set. All prior limits held.

The standalone50k graph is not a50k global reduction/mixture or default-clusterer run. Its legacy751 seed differs from the actual clusterer's identity-derived seeds. UMAP uses200 epochs above10k versus500 at10k; a simple n×epoch projection suggests about141s for50k UMAP from the measured70.314s at10k, but graph size/local groups/derived seeds and GMM convergence make that only a planning estimate. Scaling the10k96.610s global GMM by5 gives483s, also not a guarantee. Recursive planning can additionally split oversized clusters and call the full clusterer many times.

Four GiB leaves only210,173,952B above the measured50k graph peak. The real consumer necessarily retains leaf content/working nodes, and `BuildLeafLayerAsync` copies minted vectors; each additional50k×768 float set is153,600,000B before headers. A50k consumer under the old4GiB limit is unproved. The proposed new6GiB sampled containment below explicitly accounts for extra fixture/consumer state; it does not certify a6GiB operating envelope or assume physical RAM availability. If the controller requires4GiB instead, keep it and record a possible honest incomplete result; do not silently change it.

## Proposed fixed process limits (require controller approval)

| Run | Seed/setup | Build | Validation + unchanged rebuild | Overall cooperative | Outer wall | Sampled process working set |
|---|---:|---:|---:|---:|---:|---:|
|10k real recursive weaver|120s|1800s|180s|2100s|2220s|4GiB (4,294,967,296B)|
|50k real recursive weaver, only after accepted10k|300s|7200s|300s|7800s|7920s|6GiB (6,442,450,944B)|

Independent phase and overall timers link to the same caller token. The outer process watchdog kills only after cooperative bounds plus the declared headroom; no dump collection. Ten-millisecond resource polling is sampled and can overshoot; preserve trigger sample, last successful sample and terminal peak. Report total allocation separately from live working set. Run sequentially on the shared host; disclose concurrent work. A failed10k predicate prohibits50k launch. A failed50k run stops this plan: report exact last call/layer/resources and do not expand budgets.

The10k build allowance is4.8× the measured actual10k clusterer time, reserving room for real SQL writes, additional split calls and higher layers. The50k allowance is4.1× the measured50k graph time, reserving space for unmeasured global/local stages, splits and recursion. These are bounded opportunities to complete, not guarantees. Planner's existing closed member-visit/call bounds remain the algorithmic limits; no unobserved internal distance count is reported as zero.

## Review focus

1. Incomplete/canceled/failed build must never count as publication or erase the old complete generation: small real-consumer behavioral controls before scale.
2. Summary embedding must carry the exact synthetic provider/model identity and full768 width: actual consumer refusal and no-publication controls.
3. Duplicate/carry/soft-membership paths must preserve source coverage and DAG identity: validate every persisted edge, root and reachable leaf, not text matching.
4. A current generation must avoid new summary/embedding/cluster effects: record exact counters before/after unchanged rebuild.
5. Harness retention can inflate memory: keep only bounded call metadata and hashes; do not retain every input/output vector a second time in a recording dictionary.

## Task1: Offline consumer harness and causal oracles

**Files:** Add `tests/RetroDownfall.Arcanum.Tests/Weave/Tapestry/TapestryRecursiveScaleProbe.cs` (new opt-in class, real Grimoire fixture), and if necessary one partial support file for bounded observers/validation. Reuse existing numerical corpus generation/hash logic through a narrow test-only helper without changing generated bytes. Add small harness-oracle tests in the same class. Update `scripts/raptor-reference/benchmark.md` and a tracked copy of this plan after approval. No production file changes.

**Interfaces:** consume `TapestryWeaver.WeaveAsync`, `TapestryStore`, `ITapestryClusterer.Cluster`, `ITapestrySummarizer`, `IWeaveService`; count calls through forwarders that preserve cancellation and exact default output. No production observer API or alternate numerical seam.

- [ ] Write real behavioral REDs for an oracle accepting a refused generation, an oracle losing one reachable source, an unchanged-generation check accepting another synthetic effect, and caller/phase cancellation not reaching the actual weaver. Include healthy publication and truthful refusal controls.
- [ ] Retain terminal RED TRX; implement the narrow offline harness/oracles and prove GREEN. Use small real numerical/SQLCipher consumers for these controls, not a fake publication result alone.
- [ ] Seed canonical workspace chunk rows with the existing state177 eight-center corpus, IDs `scale-000000` etc.,768 dimensions. Preserve10k/50k input hashes from c3ffb36f. Fake batch embedding answers exactly one typed vector per leaf. It does not store additional copies merely for inspection.
- [ ] Synthetic summary text is a bounded deterministic tag containing the ordered child-content digest. Summary embeddings use a separately named, fixed hash-derived768-dimensional finite nonzero recipe keyed by that exact tag. Record this new synthetic-summary recipe/hash; it is not the original leaf corpus or a claim about semantic quality. Keep the same recipe for10k and50k. `FitsOneRequest` checks the actual default24-child bound; do not declare all requests fitting or force specific clusters.
- [ ] Counting clusterer always forwards to `new RaptorClusterer()` and records only layer, stage/path/member digest, input count, elapsed, output counts/hash, outcome and cancellation. Hash ordered vectors without retaining copies. Default inner phase/scalar counters remain explicitly UNOBSERVED. All fake transport methods honor the supplied token.
- [ ] Add opt-in methods `Complete_recursive_weaver_at_ten_thousand` and `Complete_recursive_weaver_at_fifty_thousand`; default full-class runs skip only these named expensive cases. Progress sidecar at most every5s plus call/phase transitions; no raw source text, prompts or endpoint data.

## Task2: Freeze, qualify and execute sequentially

- [ ] Run complete new harness class, complete `TapestryWeaverTests` (including partials), `TapestryBuildPlannerTests`, affected default-clusterer/identity/reference classes and relevant existing build/consumer-boundary guards. Capture actual terminal TRXs, unique IDs and complete-class census. Run normal zero-warning solution build and both owned C# style lanes.
- [ ] Causal mutations: accept refusal as publication; bypass exact source reachability; allow unchanged rebuild to add an effect; sever linked caller cancellation. Kill each with a meaningful small behavioral case and restore exact bytes. Do not rerun approved c3ffb36f numerical mutants absent a substantive numerical edit.
- [ ] Commit/freeze exact SHA/tree and hash all numerical kernels, fixture/harness/docs and first-party assemblies. Launch10k in a fresh process under the fixed limits. Require all acceptance predicates below before launching one50k process. Bind full before/after source+binary maps to each command/TRX/sidecar.
- [ ] Seal final source/evidence/report and send for independent review; no controller integration or completion credit by this worker.

## Acceptance predicates, separate from containment

For each completed scale case require real `Woven`, a current Complete generation matching the exact source snapshot and manifest/recipe, zero quarantined/missing leaves, exactly n typed leaf embedding outputs, at least two actual summary layers, and at least one real default numerical call at a higher layer. A leaf-only/single-layer/noncontracting outcome is described honestly and does not satisfy this recursive acceptance. MaxDepth with valid explicit multi-roots is allowed at the default five layers; do not claim one root.

Read persisted generation/nodes/edges/roots on the admitted lane. Require all n original source IDs reachable from roots, every edge child exists, no cycles, increasing summary layers, ordered distinct children, child count≤24, exact content/recipe/embedding identities, finite768-dimensional vectors and count agreement with returned outcome. Validate memberships/lineage as persisted, allowing legitimate overlap and carries. Do not infer source identity from prose. Require summary fake-call count equals `SummaryCallsMade`, summary single-embedding count equals successful newly minted summary count, batch inputs=n, and zero paid/network calls. Count calls once, not repeated test IDs.

Immediately repeat the unchanged build in the same process/database: require UpToDate, same generation/root/source fingerprints and no additional cluster, summary or embedding effects. Existing cancellation/failure controls remain part of ordinary qualification; do not force a long expensive cancellation of a previously successful scale run merely to duplicate them.

## Explicit remaining gates

This plan can establish offline50k default global/local composition and recursive durable generation construction if both runs succeed. It does not establish live model quality/cost, both retrieval-mode end-to-end proof, destructive lifecycle behavior at scale, shipping AOT/fullCI or full Task12 completion. Existing smaller controls remain controls rather than new original-finding credit. Any confirmed production defect gets its own behavioral RED and controller report before broadening ownership.


## Approved observation continuation — supersedes only the original no-production-edit restriction

Controller ruling2026-10-27 retains the original specification's50k BIC candidate-count acceptance. The qualified no-production-change checkpoint084a0107 and its567passes/23classes remain immutable earlier-source evidence; no10k/50k consumer run occurred. The following narrow Core observation edits are authorized before the superseding source-review packet, with no numerical/default-configuration or scale-limit change.

- `RaptorClusterer.cs` keeps its public parameterless constructor and original internal reducer/fitter constructor. A non-null internal observer overload uses the same actual default reduction and fitter. Per-fit events carry only fixed lifecycle phase, layer, global/local mixture stage,64hex hashed fit identity, sample count and existing candidate diagnostics. Instance-local observed call ordinals distinguish repeated/concurrent identical semantic calls; ordinals never affect seeds or arithmetic. No static subscription or retained vectors/fits.
- `RaptorNumericContracts.cs` owns the internal closed observation record/phase. `RaptorGaussianMixture.cs` preserves RaptorGmmCandidate's positional constructor/deconstruction and adds `SearchBypassed` as an init property. Its actual identical-row branch reports its real one-component fit with that flag; this is zero BIC-search attempts, not49 inferred from a formula. Ordinary candidate events still come from the existing actual selection loop.
- Lifecycle reports begin before actual fitting, then actual completion/refusal; cancellation/faults never manufacture success in finally. Observer faults propagate unchanged; a terminal observation fault during a prior cancellation/fault preserves both originals in order. Callbacks are synchronous and no work continues after the owned call completes.
- The recursive harness uses this observed default path, not an alternate fitter/reducer. One active-fit accumulator validates actual candidate order/counts; completed/refused/canceled/faulted selections and bypasses have distinct counters. Hashes cover every fit, and only64 bounded metadata examples are retained. Global/local/layer counts and candidate attempts/refusals are observed; neighbor/UMAP internal phases/scalars remain explicitly UNOBSERVED.
- TDD must prove off/on exact membership bits on small real corpora, actual candidate ranges and fast-path diagnostics, global/local/layer identity, repeated/parallel call isolation, cancellation/refusal/fault outcomes, bounded metadata retention and the real consumer's measured receipt. Add causal mutations and exact restoration, then normal build/full affected classes/styles and an immutable source packet. No expensive consumer process before independent source approval.

The two Core implementation files and contracts file therefore have new source hashes. Earlier numerical receipts remain evidence for their recorded versions; do not claim all kernels are byte-identical to the earlier packet. Numerical/default equivalence is established by unchanged manifest/recipes, reviewed arithmetic paths, off/on bitwise comparisons and complete reference/quality regressions.


### Terminal-callback cancellation correction

Independent review of8fa12c6b found that Completed/Refused callbacks could cancel the supplied token after the fitter's last check. Two real default-fitter regressions reproduced missing cancellation (2RED/10controls). Check the token after those synchronous callbacks too, retaining the truthful completed/refused fit fact and never emitting another terminal fit outcome. Original observer/dual-fault behavior, seeds, numerical options, fixed scale ceilings and source-review-before-scale remain unchanged. Preserve8fa's independent578-pass/24-class qualification as earlier-source evidence; the correction requires fresh exact-source qualification.
