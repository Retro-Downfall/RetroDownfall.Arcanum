# Inference Refusal and Failure Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Execution is inline; no child agents. Root owns independent review and integration.

**Goal:** Preserve Long Rest's actual pre-send refusal and durable not-sent evidence, and make existing executor failure logs identify the configured provider/model and structured HTTP status safely.

**Architecture:** Separate a code-owned refusal's error from the executor's observation that it occurred inside the awaited physical-send fence. Carry that observation only through the active provider invocation, never infer it from an error code/type received from the SDK or later stages. Reuse existing native Prepared release/Sent not-sent settlement without changing schema, counters, fault policy, accounting authority or admission.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, controlled SDK/HTTP-handler fakes, native SQLCipher fixtures, xUnit, structured ILogger metadata.

**Spec:** `docs/reviews/2026-10-24-repository-code-review.md` RV-097/RV-102; U02 reliability/usability reports; `docs/Arcanum.DESIGN.md` Unknown-Usage Settlement and Cap and Long Rest dispatch; approved root preflight ruling.

## Global Constraints

- Exact base `aa995fb2bfdab49ea2869614f66d2bbe3d672baa`; branch `codex/review-inference-refusal-diagnostics`. Preserve sealed0214/6ff/166 branches and packets.
- No live model/provider, network, embedding, installation, configuration, main, push, schema, numeric RAPTOR, model/scanner or TSV changes.
- Only an exception observed during the code-owned `beforeSend` await proves no physical SDK dispatch. A matching SDK/later-stage exception cannot grant that classification.
- Preserve fail-closed source/claim/lease checks, current ownership, one physical dispatch, actual paid usage, ambiguous Sent cancellation/transport, and existing best-effort cleanup retention.
- Never parse exception messages for codes. Never log exception objects/messages, request/response bodies, prompts, credentials, endpoints or secret-bearing caller text.
- Log bounded/sanitized configured provider/model identifiers, call/purpose/type and nullable structured numeric HTTP status; absent context is explicitly unknown.

## Review Focus

- Post-MarkSent refusal: native settlement must charge no unknown-usage counter and preserve the original restart-generation code, with no SDK send or billable row.
- Replayed/same-type refusal from the SDK or a later accounting/disposal stage: no not-sent classification; real paid/unknown effects survive.
- Cancellation and lost worker ownership at the fence: no retry or extra provider effect; preserve current durable cancellation/recovery rules.
- Repeated known no-send attempts: cannot spend the unknown-usage cap, including after a retained durable disclosure.
- Provider labels, null context, nested structured status and control/Unicode line separators: useful structured metadata without private content or multiline injection.

## Task 1: Preserve the observed Long Rest pre-send refusal

**Files:** `ModelCallExecutor.LongRest.cs`, `LongRestProvider.cs`, `LongRestMaintenance.cs`, `LongRestMaintenance.Dispatch.cs`; narrow LongRestProviderCall/Dispatch and LongRestMaintenance test partials; owning DESIGN dispatch/settlement text.

**Interfaces:** Existing `IModelCallExecutor`/`ILongRestProvider` signatures stay unchanged. Add only host-internal error/refusal observation types in existing API owners; no wire or persisted fields. The executor attests the active beforeSend await; the provider preserves only that active invocation's observed exception identity through its accounting lifetime; maintenance consumes that observation and rethrows its original Error to the coordinator.

- [ ] Add native production-maintenance tests for before/after MarkSent source/configuration/claim gates; assert literal original codes and RestartGeneration, zero SDK sends/BillableOperations/unknown FailureCount, exact attempt state and retained no-completion shape. Add repeated no-send cap and ownership/cancellation controls.
- [ ] Add direct real executor controls for both chat/embedding: a coded beforeSend exception preserves Error; identical type/code from SDK or paid callbacks is not not-sent evidence. Keep arbitrary local faults sanitized; do not infer a code from message text.
- [ ] Run bounded tests against unchanged production and archive exact source, actual RED failures and passing controls.
- [ ] Implement phase-origin observation, active-invocation propagation, existing Prepared release/NotSent settlement and original code preservation. Map actual epoch changes to ConfigurationChanged, one-use fence refusal to StaleFence; retain malformed admission InvalidRequest.
- [ ] Run focused GREEN plus complete owning provider/maintenance/fault/accounting suites. Causally revert origin/code/settlement branches individually, preserve failures and exact restoration.

## Task 2: Correlate existing executor failure logs safely

**Files:** `ModelCallExecutor.cs`, `ModelCallExecutor.CampaignMaintenance.cs`, existing safe label helper only if its new Unicode-line tests demonstrate a defect; narrow executor diagnostic tests and owning DESIGN diagnostics text.

**Interfaces:** Private logging helper only for the two existing executor error log paths. It consumes their already-captured `ModelCallContext`, purpose/call ID and exception's structured status/type; no new provider resolution or caller payload inspection. Public error, failure cause, fallback/retry and streaming behavior stay unchanged.

- [ ] Add actual buffered/Campaign calls through controlled failing clients for HTTP404/429/503 and transport/unknown status; assert Provider/Model/HttpStatus/call/purpose/type in structured log state and sanitized public error, without exception object/body/prompt/endpoint canaries.
- [ ] Cover context absent, hostile/long configured labels, Unicode line separators and owned cancellation (no error log); preserve existing paid/reconciliation behavior via full owning suites.
- [ ] Run authoritative RED on unchanged logging, then implement the minimal private metadata logger. Supplement structured HttpRequestException status privately if needed; do not change public failure classification/fallback.
- [ ] Run GREEN, exact old-template/privacy causal controls and restored qualification.

## Final qualification and seal

- [ ] Run all changed owning classes plus affected fault/accounting/admission/privacy/constructor/environment guards; normal solution build zero warnings/errors and all owned house/Roslyn style gates.
- [ ] Preserve exact RED/GREEN/TRX/source archives, causal mutation/restoration maps and any failed intermediate gates. Count actual distinct cases/classes and skips; do not claim full CI or global Hosted closure.
- [ ] Update owning docs, seal a clean local commit and immutable Git/archive/evidence packet; root independently reviews/integrates and owns final coherent global gates.
