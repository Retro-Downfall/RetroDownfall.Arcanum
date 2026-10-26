# Embedding provider diagnostics implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans inline. No children; controller independently reviews the immutable candidate.

**Goal:** Complete remaining RV118 provider diagnostics and confirmed adjacent privacy/attribution defects, preserving the integrated RV113 completion contract and existing paid-work/cancellation/retry behavior.

**Architecture:** Use private per-attempt diagnostic state to distinguish generator resolution, generation and local accounting, retaining actual first-party lease provenance separately from the initial configured snapshot. Classify actual ClientResultException/HttpRequestException status only; keep Embeddings.ProviderUnavailable and bounded fixed actionable messages. Logs use closed reason/stage, checked bounded identifier labels, safe status/type/counts and no raw exception/private payload. Mixed-model monetary attribution is a separately required adjacent investigation before final qualification; propose its narrow contract with actual RED evidence before behavior edits.

**Tech Stack:** .NET10, C#, Microsoft.Extensions.AI, System.ClientModel, options/logging, xUnit and existing SQLCipher accounting consumers.

**Spec:** `/private/tmp/arcanum-rv118-embedding-diagnostics-preflight.md`; RV118 register and original detailed review around1754; owning DESIGN21.1/API error contract. RV113 is integrated at4743b7445cef4c6d8ed6e9cdb6d4467ca35b3dee and is not a second fix here.

## Constraints and review focus

- Exact baseline `4adafb838673fcaf0135590ec79ad08bae570f96`; branch `codex/review-embedding-provider-diagnostics` in maintenance-outcomes. Preserve RV105 at681701e38f3f1f5a992fa9716f94b24785b4bc0f and prior branches/packages.
- No RV111 retry-policy or RV127 cache-policy credit, schema, capsule, live provider/model, install, persistent config, operator command, children, main or push.
- Preserve Result/code/HTTP mapping, sequential batches, admitted reservation and earlier incurred spend, vector-count contract, caller cancellation and RV113 safe IDs/type completion Warning and scope disposal.
- Actual resolution vs generation provenance must remain honest under hot reload. Missing lease provenance stays unknown, never copied from current settings or provider-returned metadata; reset per batch so a new resolution failure cannot inherit an earlier lease.
- Status401 credential refusal,403 permission refusal,404 missing model/endpoint and413 size rejection get fixed supported next actions; ordinary transport/status failure remains actionable unavailable. No exception-message parsing, endpoint/input/body/credential text or arbitrary error code pass-through.
- Provider/model labels use a bounded conservative ASCII identifier grammar; reject unusual, control/URI/path/credential-shaped or overlong values as a whole rather than truncating secret prefixes. Public messages contain only fixed guidance/status, not identifier values.
- Local accounting-write failure must not be classified as a provider HTTP rejection merely because its exception type/status resembles one.
- Investigate real mid-operation lease/model changes and billing records. Preferred contract to assess: pin expected operation embedding identity and refuse before paid Generate if a resolved identity differs, preserving reservation/pricing instead of silently repricing or mixing vectors. Assess same-name credential/endpoint rotation separately; do not overclaim identity coverage.

### Task1: Provider diagnostic TDD

**Files:** existing `WeaveServiceTests.cs` and `.Provenance.cs`; production `WeaveService.cs` only unless evidence requires a narrow additional owner.

- [x] Add real SDK ClientResultException and raw HTTP status-shaped cases with private message/inner/body canaries, both resolution and generation faults, actual/missing provenance, hot reload and per-batch reset.
- [x] Add mismatch/success/disabled/empty/cancellation/earlier-spend/RV113 controls and local accounting-stage attribution negative control.
- [x] Retain exact test-only source state and terminal behavioral RED before editing behavior.
- [x] Add minimal private stage/provenance/classification/identifier formatting; keep code/status mapping and existing billing/control flow intact.
- [x] Run complete Weave class and relevant provider/cache/consumer controls.

### Task2: Required adjacent mixed-identity investigation and fix

- [x] Prove whether consecutive batches can return differently stamped provider/model vectors while original pricing/reservation/ledger identity remains unchanged; retain separate genuine RED and observations.
- [x] Assess credential/endpoint rotation with identical first-party provider/model using actual factory behavior.
- [x] Present the narrow pre-dispatch identity contract with evidence to the controller before fixing; continue independent diagnostics work meanwhile.
- [x] Implement the ruled contract and positive/cancellation/paid-work/authority controls, without changing SDK retry/cache/capsule policy.

### Task3: Documentation, qualification and handoff

- [x] Update owning DESIGN21.1 and API-visible fixed message behavior; write tracked implementation report separating RV118 from the adjacent correction and prior RV113.
- [x] Check both owned house-style modes and Roslyn formatting; kill meaningful mutants with exact source restoration.
- [ ] Freeze clean commit/tree/source hashes, normal zero-warning build and complete affected classes/guards with terminal TRX/census.
- [ ] Seal source/full-context diff/evidence/commands/hashes for independent review. Final-stage boxes remain pending at source freeze; immutable terminal report records final results without changing qualified bytes.

## Confirmed adjacent ruling

Real factory + OpenAI SDK/scripted transport RED proved3 failures: model/provider change between batches returned mixed stamps while both rows retained original identity/rates; first-lease change generated paid work before mismatch could be refused. Controller approved one initial configuration snapshot for identity/pricing/reservation, known canonical name mismatch refusal after existing Tapestry validation and before every paid Generate, with cancellation priority and earlier incurred spend preserved. Same-name endpoint/key rotations remain supported and explicitly outside this name-only assurance; null-provenance legacy ordinary leases remain unknown. This is a separate confirmed adjacent repair, without RV111/RV127/capsule credit. A further2RED/4controls exposed cancellation priority at the new guard and were corrected with an explicit pre-send token observation.

## Qualification-discovered adjacent contract correction

The first committed candidate4016795b's19-class suite found a pre-existing duplicate cardinality predicate in TapestryWeaver, contrary to IWeaveService/DESIGN and the unchanged inventory. Controller granted narrow removal of that count operand only, preserving the identity/quarantine predicate and all revision/provider fences. Complete Weave/Tapestry/inventory classes passed161/161; reinstating the operand caused1inventory failure with13controls passing and exact12-file restoration. Original failed run1102/1/1platform-skip and its build/commands/TRX are preserved. Final qualification includes the same19classes, already including the complete TapestryWeaver class, and discloses the exact Windows-only test skipped on macOS.
