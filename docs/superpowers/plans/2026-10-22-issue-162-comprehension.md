# Comprehension foundation implementation plan

> **For agentic workers:** Use the TDD workflow for each owned change and independent review before integration.

**Goal:** Deliver issue #162 to `memory-enhancement`, with the default-off document extraction seam proven through retrieval, explicit attachment tools, per-turn references, and durable pins.

**Architecture:** Retain durable `Binary` classification. A shared Core eligibility policy admits only the closed Comprehension MIME registry when both AttachmentRetrieval and Comprehension are enabled. A bounded extraction host behind the existing text facade reads authenticated bytes from the attachment store and reuses the text normalization/chunking invariants.

**Tech stack:** Existing .NET 10, Native AOT, SQLCipher, and Microsoft.Extensions.AI; no new dependencies or schema changes.

**Spec:** [#162](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/162), under [#161](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/161). The operator's current delivery request names `memory-enhancement`, superseding the issues' older integration-branch wording.

## Decisions and constraints

- Keep `Binary`, avoiding a durable enum change and allowing old rows to become eligible without rewriting metadata.
- Add `Features.Comprehension`, default false, requiring `Features.AttachmentRetrieval`. All other text/image behavior remains unchanged.
- The initial container is `application/vnd.arcanum.comprehension-text`: ASCII `ARCCOMP1\n` followed by strict UTF-8. It is a seam fixture, not a PDF/Office/OCR parser.
- Per-format extractors accept bytes and a bounded sink only. Input is read exclusively through `ReadBytesAsync`, with the text byte ceiling checked before allocation and after the read. Output is capped at 262144 UTF-16 characters and truncated without splitting surrogate pairs. One code-owned five-second attempt clock includes extraction-slot queueing, authenticated read, and extraction. Directly await/drain the store read under caller admission, cancelling at the shared deadline and joining cleanup; elapsed-over-budget reads fail before parser dispatch. The trusted store must honor cancellation, but an arbitrary uncooperative read cannot be abandoned safely: retain admission until it releases resources. Only already-read bytes plus the pure extractor/sink may run detached with bounded caller wait under the remaining budget. Reviewed extractors must cooperate with cancellation and the sink; an outstanding timed-out parser is quarantined to prevent unbounded abandoned work. No hard wall-clock preemption of arbitrary I/O is promised.
- Unsupported/empty output is terminal NotEligible; malformed input/deadline failures are terminal Failed. Caller cancellation propagates. No new status or public mechanical-limit setting.
- Reconciliation includes previously ineligible supported Binary rows whose extraction timestamp is null. Attempted empty extraction has a timestamp and is not repeatedly queued.
- Document context carries literal untrusted DATA framing, source format, and a layout-loss notice. Turning the feature off also suppresses previously indexed document retrieval.
- Preserve admission leases, effect groups, source authority, byte budgets, adaptive fences, and Native AOT closure.
- Keep document chunks out of Tapestry in this foundation slice. Document support for Tapestry is owned by [#295](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/295). Refuse a captured attachment generation containing known document leaves so persisted summaries cannot bypass the Comprehension gate, while retaining existing text-only stale-source behavior.

## Work and verification

- [x] Retrieve parent/target/source contracts, verify no blockers, refresh refs, and create an isolated branch from current `memory-enhancement` (baseline `ad28419c32643bab274465caaf8aa3db5dee2128`, identical to main).
- [x] Core extraction contract, shared eligibility, bounded host, and facade: write failing authenticated-read/output/deadline tests, implement, run focused GREEN.
- [x] Indexing/retrieval/reconciliation: prove real encrypted document index and model-context projection, prior-row backfill, gate-off suppression, and terminal poison behavior; implement without changing text streaming.
- [x] API materialization: independent attach, refresh, per-turn reference, and durable-pin RED/GREEN tests, literal framing, gate-off/unsupported no-read tests.
- [x] Add the Comprehension public configuration opt-in and update the five owning documents; keep #161 open and the feature off by default.
- [x] Run independent review, fix validated findings with regression tests, and restore each original refusal separately to prove the four path tests are independently sensitive.
- [x] Complete focused local boundary, source-authority, packaging and runtime checks before candidate freeze.
- [ ] Freeze the final candidate SHA/tree and complete focused qualification, including a fresh macOS Native AOT diagnostic and shipping/provider check. The operator explicitly waived the whole CI gate for this first foundation change on 2026-10-22; full coverage, the complete source-fixture matrix and Windows lanes are outside this delivery boundary.
- [ ] After exact-candidate terminal green, integrate the qualified commit with expected-head checks, push `memory-enhancement`, verify ref/tree equality, and mark only #162 completed.

## Review focus

An uncooperative pure parser must not hold the single-reader queue indefinitely; authenticated store reads must remain joined under caller admission through cancellation and cleanup, and an over-budget read must not dispatch a parser; malformed data after the output ceiling must not silently bypass validation; off-gate retrieval must not leak previously indexed documents; reconciliation must filter before its page limit; all document paths must read only the authenticated stored snapshot.

## Local verification before candidate freeze

- Behavioral RED/GREEN covers all four model-context paths, plus refresh and real HTTP intake.
- Restoring index, attach, refresh, per-turn reference, or pin refusal independently produced exactly one matching failure and four passing path tests in each run.
- Restoring the earlier detached-read host fails the borrowed-read lifetime regression; the final host awaits read completion under admission.
- Final integrated attachment/Tapestry suites plus the production extraction-source regression: 724 passed, no failures or skips.
- Restored path and repository boundary suite: 504 passed, no failures or skips.
- Full Compendium suite: 197 passed, no failures or skips.
- macOS arm64 native SQLCipher provenance and Native AOT IL/regex executable gates passed. The diagnostic publish emitted no first-party trim/AOT warnings.
- Full production-source discovery passed with zero diagnostics. The reviewed capsule manifest has 6,863 rows; five additions and one replacement are explained entirely by edited sources, with zero unexplained rows and all classifier invariants passing. The source checker is unchanged.
- Source archive packaging: 37 passed against the staged final implementation. Both standard whitespace and repository blank-line checks passed.
- macOS shipping Native AOT publish and its exact-apphost provider-contract smoke passed during development; final platform evidence must bind to the frozen candidate.
- Qualification amendment, 2026-10-22: the operator explicitly stated that the whole CI gate is not required for this first change. Full CI run 39778749160 was dispatched for implementation commit `7b1ccf1df01b058c9f05132510c3b3230320c544` and cancellation was requested after that instruction; it is not qualification evidence. Delivery uses the focused checks above plus fresh final-candidate macOS Native AOT diagnostics and shipping/provider proof. Windows, full runtime coverage and the complete source-analysis fixture matrix are intentionally not claimed. Exact final SHA, delivery ref/tree equality and completion evidence will be recorded in #162 without changing the qualified source tree.
