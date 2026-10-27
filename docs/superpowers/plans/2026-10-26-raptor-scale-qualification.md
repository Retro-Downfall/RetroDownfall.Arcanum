# RAPTOR offline scale qualification implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline; no children. Preserve the approved numerical manifest and exact source/evidence. Parent owns final Task12 integration/full CI/live gates.

**Goal:** Measure real RAPTOR numerical kernels on deterministic synthetic 1k/10k/50k corpora with 768 dimensions and global neighborhoods, reporting completed and partial work honestly.

**Architecture:** Existing public Core kernel APIs and content-free progress seams feed an opt-in test-only probe. No new product/provider/schema/configuration behavior. Fresh test processes separate larger pilots and their process memory peaks.

**Tech stack:** .NET 10, existing xUnit/TRX, managed kernels, synthetic in-memory vectors.

**Spec:** `../specs/2026-10-25-real-raptor.md`, Task12 in `2026-10-25-real-raptor.md`; parent-approved preflight `/private/tmp/arcanum-raptor-task12-scale-preflight.md`.

## Constraints and review focus

Keep d768, k=floor(sqrt(n-1)), full covariance, real 500/200 UMAP epochs, exclusive BIC candidate upper bound and >=.95 nonself neighbor recall. No reduced numerical recipe masquerading as production throughput. No live providers, installs, configuration, schema, paid work or source-content diagnostics. A global stage is not a full recursive tree. A canceled pilot is not completed scale qualification.

Before execution, containment ceilings approved by parent: 1k neighbor 30s (exact499,500 evaluation result is observed, not canceled at its final boundary), fuzzy 30s, UMAP 60s, global BIC 120s; larger neighbor pilots max2,000,000 reported evaluations or15s. Per-stage process working set4GiB, sampled10ms; cancellation must propagate. Whole-clusterer pilot at1k only after global observation,120s containment. These are safe pilot ceilings, not acceptance thresholds adjusted from results.

- [x] Add opt-in probe with exact input/output fingerprints, per-stage time/allocation/working-set/work/candidate counts and independent cancellation controls.
- [x] Run bounded1k global pipeline; preserve terminal logs/TRX, including any incomplete stage. Run each larger neighbor pilot in its own process.
- [x] Inspect observed time/memory/cancellation and exact oracle recall. Establish justified completed-run envelopes before larger complete runs or any production change.
- [x] A confirmed performance/correctness defect requires a meaningful failing acceptance test, narrow repair, unchanged reference/determinism/recall contracts and root review before any kernel edit.
- [ ] Complete appropriate scale/composition measurements when safe; record omitted/incomplete lanes explicitly in `scripts/raptor-reference/benchmark.md`.
- [ ] Qualify full affected numerical classes, normal build and owned styles; preserve terminal TRXs and semantic mutations/exact restoration when retained harness behavior warrants them.
- [ ] Commit exact scoped candidate and immutable package/report for independent review. No integrated/fullTask12 completion claim.

## Recorded checkpoint

The initial10k work refusal is genuine behavioral RED (49,995,776 actual scalar evaluations before completion). A bounded exact-index memo preserves the original scalar miss block and all numerical manifest/options bytes. The completed10k graph uses11,115,526 scalar distances, with sampled recall1.0; its older metrics/source versions are archived separately. All100 existing numerical cases plus two selected new controls passed; the complete default probe class later passed5 with8 explicit opt-in skips. Four causal mutants were killed and all12 source/document hashes restored exactly. These are intermediate checkpoints; the immutable report owns the final exact-candidate gate census.

The50k180s/50m pilot remained in initialization and is explicitly incomplete. Parent approved one600s/150m/4GiB first-descent measurement on the frozen candidate after implementation review packaging. Its per-phase/pass/4096-row checkpoints are bounded to1024 records. No additional partial-pilot sequence or full50k completion is implied. FullTask12, all required50k completion/composition/operational envelopes, finalCI/AOT/live lanes and parent integration remain open.
