# Counterfactual memory evaluation in The Proving Grounds (#106)

Status: design direction and this detailed specification approved by the operator on 2026-10-18; source-backed review corrections below preserve that scope. The implementation plan is also approved; execution uses its recommended subagent-driven method. Delivery target: `memory-enhancement`. Baseline: `c1b74e95e2190a1e412ff5b6c5a8277dc8fcdc63`.

## 1. Outcome and boundaries

Implement [#106](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/106), the counterfactual-evaluation prerequisite for #95 under [#73](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/73). #84 and #105 are complete. OATH §20.7 and the approved Covenant authority, disclosure, exact-version and erasure contracts govern this work.

An authenticated operator submits a bounded, stateless workload, explicit exact memory targets, an exact provider/model selection, provider options, deterministic Inquisitors, and a fixed paired-sample count. Arcanum measures outcomes with and without that admitted memory plan and publishes immutable, content-free evidence. A report distinguishes Benefit, Harm, NoMeasurableEffect and InsufficientEvidence.

This is an Arcanum Core/API/Infrastructure enhancement. Extend the existing Proving Grounds and its Trial/Inquisitor vocabulary; introduce no new thematic domain name. The Forge UI, new CLI/MCP tools, production transcript replay, semantic model judges, automatic evaluation schedules, per-item causal attribution, and #95 ranking/decay/reinforcement are outside this change. Existing Trial APIs and ordinary inference retain their behavior.

Evaluation grants no confirmation, promotion, correction, retirement, pinning, review, extraction, consolidation or other production-memory mutation authority. The lab may write only its own operation, disclosure/accounting and evaluation-evidence records. A report measures the entire selected memory plan; it does not assign a group's benefit to each member.

## 2. Decisions and alternatives

Use a dedicated paired-Trial runner, rather than ordinary Wizard turn replay. Wizard replay can retrieve fresh memory, route Spells, invoke tools, run maintenance and publish production artifacts. Ordinary Saga materialization also currently drops durable subject and Annals-version identities. A narrow exact-version reader avoids manufacturing that identity from a content hash and avoids changing ordinary prompt behavior solely to support this lab.

Use deterministic Regex and JsonSchema Inquisitors first. The current semantic judge starts an additional inference with mutable model defaults and free-text verdicts; making it a lawful protected judge is a separate extension. Persist a new closed report shape, never `TrialResult` or `InquisitorVerdict`, which contain output, labels and details.

Use request-bound synchronous execution with the existing long-running-operation coordinator. A detached worker cannot retain a human request's protected read authority. The operation ledger supplies cancellation, ownership, heartbeats, idempotency and recovery without serializing that authority.

Three properties make the evidence stronger than a score difference: both prompt arms are derived mechanically from one attributed plan; evaluator and sampling choices are committed before dispatch; and per-check regressions cannot be hidden by improvements on different checks.

## 3. Input contract and bounds

Add positional, source-generated input DTOs under `Core/ProvingGrounds/MemoryEvaluation`. No wire DTO contains an invocation, lease, execution-surface selector or mutation capability. Unknown input members, undefined enums and unsupported check kinds are rejected.

`MemoryEvaluationRequest` contains:

| Field | Meaning |
| --- | --- |
| `evaluationId` | Required nonempty caller-generated GUID; durable idempotency name. |
| `scope` | Explicit `Global` or `Campaign`; only Campaign carries a nonempty `campaignId`. |
| `providerName`, `model` | Explicit configured provider and exact model identifiers; no default-model fallback or routing. |
| `options` | Closed typed temperature, top-P, optional seed, maximum output tokens, and optional supported reasoning-effort controls. Resolve all effective values or explicit omissions once. |
| `workload` | Exactly one operator-authored system-prompt string and one user-prompt string. No history, attachment, media, tool messages, Session or working-directory inference. |
| `memory` | Separate closed Covenant, Saga and Lexicon target lists. Each target names its owning subject, exact version and expected content/fragment digest in that store's existing identity/hash grammar. Annals targets also bind their hash-format code. |
| `checks` | Ordered deterministic Regex or JsonSchema checks; their specification is transient input. Labels, semantic judges and arbitrary diagnostic templates are absent. |
| `pairCount` | Integer 3 through 16 inclusive; default 5. |

The options DTO has no arbitrary additional-properties bag. `maxOutputTokens` is 1 through 4,096 inclusive, default 512. Temperature/top-P and reasoning values must satisfy the existing provider/model capability validation; omission is a frozen, explicit absence, not a later lookup of mutable defaults. A fixed seed, when supplied and supported, is identical in both arms and across repetitions; it is part of the plan, not a promise of independent samples.

Hard code-owned ceilings for policy version 1:

- Request body: 128 KiB of strict UTF-8 JSON; system prompt: 16 KiB; user prompt: 32 KiB.
- Distinct intervention targets: 1 through 16 across all three stores. Empty plans and duplicate subject/version selections are rejected.
- Exact source/dependency manifest: at most 128 coordinates. Oversized dependency closure is refused rather than truncated.
- Rendered admitted memory: at most 32 KiB, also subject to stricter existing store limits. Complete rendered provider prompt: at most 96 KiB.
- Checks: 1 through 16; total check specifications: at most 16 KiB; one regex pattern: at most 1 KiB. Identical canonical checks cannot be repeated to give one assertion extra weight.
- One provider HTTP response: at most 1 MiB before SDK deserialization; aggregate answer/reasoning text retained for one arm: at most 64 KiB. Overflow is invalid evidence, never silent truncation.
- Retained evaluation registrations: at most 1,024 installation-wide, counting prepared/incomplete registrations. One registration and its complete terminal report/manifest metadata: at most 64 KiB.
- One active evaluation per installation; additional distinct runs receive a typed busy refusal. Replays resolve their existing operation. Up to 32 physical provider sends per run; zero automatic retries.

These are experiment bounds, not a restriction on productive Wizard/tool loops. Existing stricter installation budget, context, transport and shutdown policies still apply. No new configuration key is needed for this explicit API capability.

## 4. Authority and exact source snapshot

All evaluation routes require the ordinary master-key authentication and clean protected-read authority, using `.RequireCovenantReadAuthority()`. Report IDs and scope appear in request bodies, not URL paths or query strings. Responses use `Cache-Control: no-store` and existing protected-response handling. An unavailable/disabled required capability refuses evaluation explicitly; ordinary inference continues under its existing contracts.

Resolve explicit scope through `ICanonicalCampaignContextResolver`. Bind the canonical resolver's actual scope, Campaign availability and owner-deletion facts; no path text or ambient Session supplies authority. This stateless capability accesses no workspace files and makes no physical-root identity claim: an explicit Campaign-only resolution has no root digest/revision. Do not borrow the feature-gated Long Rest root enricher. Global-only is an explicit resolved scope.

Add a thin authenticated `ArcanumInvocationContexts.ForMemoryEvaluation` factory selecting the existing stateless operator surface with `ToolPolicy.NoTools`, resolved scope and the current request-issued read epoch. Both Covenant mutation predicates must remain false. It is neither an inspection invocation, which promises no dispatch, nor a Long Rest maintenance capability, which requires a Session claim and a different operation.

Acquire and hold a genuine `CovenantTurnLease` through source hydration, both arms, report publication and response serialization. Validate its request epoch, borrowed ownership and revocation with `ICovenantOperationGate`. Link request abort, operation cancellation, host shutdown, authority/owner revocation and lease loss into the active provider cancellation path.

Read targets through narrow owning-store adapters. Saga and Lexicon join the canonical subject to the exact current Annals head/version and prove its recorded content hash/format, origin, scope, valid-time facts, transaction-time facts, lifecycle, sensitivity, canonical subject-owner Session (when one exists), truthful nullable version-authoring SourceSessionId, and exact dependency closure. Preserve both Session coordinates independently for selected and dependency subjects: an operator correction can have null version authorship while its canonical Saga row remains Session-owned. Covenant proves exact eligible immutable version, fragment digest, lane, scope, lifecycle, generations and normal linker decision. Check Global/Campaign shadowing and other normal eligibility rules before restricting the plan to the explicit target set; an omitted Campaign shadow must not make an otherwise ineligible Global target eligible.

Reject stale, erased, unresolved, retired, not-current, wrong-scope, invalid-time, suppressed or otherwise ineligible targets. Pinning does not widen scope or override another eligibility rule. A row lacking exact retained version evidence cannot participate. Intervention targets must be current; dependency coordinates must remain retained and satisfy their declared edge semantics, rather than being forced to current heads indiscriminately. Mark manifest coordinates as Intervention or Dependency. Dependencies and their sensitivity/generations remain bound; they do not become extra injected memory. No adapter appends Annals claims, backfills rows, refreshes embeddings or advances review state.

All selected memory is explicit. Unselected Covenant/Saga/Lexicon content, workspace retrieval, summaries, Lore, Codex, automatic DCI and caches cannot enter either arm. Render the selected memory with the existing store-appropriate placement and data fencing: operator-confirmed Covenant CONTEXT remains distinct from proposed/model-writable DATA. User memory text cannot become a new system instruction by being evaluated.

Do not call the ordinary `CovenantDispatchGate.BeginTurnAsync` degradation path: it can turn an acquisition failure into absent memory so ordinary inference can proceed. Here, failure to obtain the requested snapshot is a refusal or InsufficientEvidence, never a valid memory-free treatment arm.

## 5. One plan and a provable intervention

Construct one immutable evaluation plan in Core. Freeze the workload, exact source manifest, eligibility/as-of instant, rendering/placement and tokenization policy, all selected provider destination/configuration facts, model, context window, effective options, checks, pair count and sample order. Deep-copy mutable settings/options before hashing or SDK mapping.

Produce a common attributed prompt representation with explicit selected-memory occurrences. Derive treatment by retaining all selected occurrences and control by removing exactly those occurrences and their code-owned memory-section delimiters. Preserve every non-memory text span byte-for-byte and in order. Bind the exact removal ranges, selected source version/occurrence coordinates, base/treatment/control prompt digests and the unchanged-span digest into the plan. Independently verify the derivation before dispatch; accepting two separately assembled strings is insufficient.

Both arms use the same user workload, exact model, provider, options and zero-tool policy. They must each fit the frozen context window including the full answer reservation. The control keeps the treatment's conservative input reservation; removing memory must not make room for different retrieved material, larger answers, history or eviction. If the treatment needs omission, truncation or different non-memory admission to fit, refuse before dispatch.

Pair order is fixed: even zero-based pairs send control then treatment; odd pairs send treatment then control. Calls are sequential. This order is committed with the plan and cannot be chosen after an outcome. No response is fed into a later arm or pair. No early success stopping, selective reruns, best-of selection or sample replacement is allowed.

Use versioned, domain-separated canonical digests: strict UTF-8, framed lengths, explicit nulls, fixed numeric codes/endianness, canonical UTC and deterministic source/dependency ordering. Bind check order explicitly. Do not hash serializer property order, runtime object identity or current mutable configuration. Use existing Covenant/Annals digest and identity primitives where their grammars apply; prove new canonical encodings with literal golden vectors and permutation tests.

Maintain separate submitted-request and executed-plan digests. Before the first physical send, commit the immutable registration, full bounded source manifest, plan/evaluator/order digests, planned counts and ownership under the operation identity. This durable commitment prevents post-response changes to tests, sources, options or sample count from becoming evidence for the original run.

## 6. Provider execution and disclosure

Add a dedicated evaluation provider adapter and freezer using existing exact `ProviderCallEnvelope`/`FrozenProviderOptions`, materialization, outbound-address, disclosure and accounting primitives. Reuse transport/freeze utilities where they are truly generic; do not give evaluation a consolidation capability or route it through the ordinary inference pipeline.

The adapter has no tool definitions, mutation services, collector, head probe, Spell routing, retrieval or production turn-finalization port. Disable SDK/transport/schema retries and fallback providers/models. One adapter send is one physical request. Bound the HTTP body before the SDK parser allocates its result.

Each arm send gets a distinct ordinal and exact effect digest binding operation/evaluation identity, pair, arm, frozen provider payload, destination, options, source manifest and sensitivity. Use the existing Operation disclosure subject and ProviderDispatch effect category. The sequence is current authority/source/configuration validation, durable external-disclosure acknowledgement, revalidation, then one send. A failed disclosure commit means zero sends.

Revalidate current source heads/eligibility, canonical owner/dataset/restore generations, provider/configuration epoch and lease before every send and before scored publication. Configuration A-to-B-to-A must invalidate an active experiment; equal text alone is insufficient. A source/configuration change between the two arms invalidates the pair and the experiment rather than comparing different interventions.

Charge/reserve/settle calls through normal accounting. Record known token, latency and exact-decimal cost totals with explicit unknown flags; never turn missing usage into zero. Independent accounting/disclosure history survives report cleanup under its existing lifecycle. Provider exceptions and errors map to closed public codes without text payloads.

Require a complete, bounded textual answer and the exact declared model identity, with provider-reported model identity checked against it. Missing or mismatched identity, refusal, truncation, unexpected tool/media output, transport failure, response overflow or unsupported result shape makes the arm invalid. A model's complete textual answer that fails an assertion is a valid negative task score. The report binds the exact configured/requested/reported model contract; it does not claim to hash remote weights or guarantee identical future provider output.

## 7. Deterministic evaluator and outcome policy v1

Policy v1 accepts only Regex and JsonSchema checks. Evaluate full bounded answer text; do not reuse the arbiter's silent truncation or classify its free-text `Detail` messages.

Regex preserves CultureInvariant matching, optional IgnoreCase and ShouldMatch. Parse all patterns before spending provider tokens and retain the existing one-second match timeout. A timeout or engine failure is InvalidEvaluation, not a failed assertion.

JsonSchema uses only the documented supported grammar: type (including supported type unions), properties, required, items, enum and boolean additionalProperties. Reject unrecognized keywords, external references, unsupported types and malformed schemas before dispatch; do not silently ignore an author's unsupported constraint. An answer that is not valid JSON or does not satisfy a valid schema is a failed assertion, not an evaluator failure. Limit schema nesting to 16 and validate its depth/size before evaluating it; stricter existing JSON limits still apply.

An arm has one closed Pass/Fail result per valid check. For each complete pair:

| Pair relation | Rule across the ordered checks |
| --- | --- |
| Improved | Treatment has at least one Fail-to-Pass change and no Pass-to-Fail change. |
| Regressed | Treatment has at least one Pass-to-Fail change and no Fail-to-Pass change. |
| Equivalent | Every check has the same result in both arms. |
| Mixed | Both a gain and a regression occur. |

The terminal experiment is scored only when every requested pair completed validly and the committed pair count was at least three. All pairs Improved yields Benefit; all Regressed yields Harm; all Equivalent yields NoMeasurableEffect. Any Mixed pair or mixture of pair relations yields InsufficientEvidence with MixedOutcomes. Any missing/invalid/cancelled pair yields InsufficientEvidence with its closed completeness/failure reason, regardless of favorable completed samples. A missing answer is never scored as a task failure.

This is conservative descriptive evidence for the declared checks and finite workload. NoMeasurableEffect means the checks observed equivalent outcomes on every completed pair; it does not assert distributional/statistical equivalence, independence, absence of unmeasured harm or universal usefulness. Benefit/Harm make no significance claim. Fixed seeds and repeated requests can correlate samples. Reports expose these policy/version/coverage limits rather than using unsupported confidence percentages.

Retain per-check 2-by-2 pass/fail counts, counts of the four pair relations, completed/valid/invalid counts and a domain-separated bounded response-evidence chain digest. Check-level regression counts remain visible even when total pass counts improve. Intermediate answer text and verdict details are released after evaluating each pair; no growing conversation/history or answer list is retained.

## 8. API and replay behavior

Add named endpoints under the existing authenticated `/api` group. Every request, response, report, enum and envelope is registered in `ArcanumJsonContext`; every failable JSON result uses explicit generated type information and `ApiResponse<T>.FromResult`.

| Route | Contract |
| --- | --- |
| `POST /api/proving-grounds/memory-evaluations/run` | Validate/freeze/commit, execute synchronously while the request is alive, and return content-free `MemoryEvaluationResponse`. |
| `POST /api/proving-grounds/memory-evaluations/get` | Body contains `evaluationId`; return authorized existing status/report without plaintext source hydration or provider work. |
| `POST /api/proving-grounds/memory-evaluations/delete` | Exact report-only cleanup: `evaluationId`, expected terminal report digest and explicit `confirm: true`. No running-registration deletion, source mutation or provider work. |

The response has evaluation ID, coordinator operation ID, closed state, closed reason, an optional terminal report, and its exact nullable retained `reportDigest` (null until terminal). Returning the digest makes the required exact report-only deletion usable without reimplementing the private canonical hash grammar. A completed run or terminal replay returns 200; an in-flight replay/status read returns 202. Invalid request/policy/bounds uses 400; missing authentication/clean read authority uses existing 401/403 mappings; missing report uses 404; same ID/different request uses 409 `Security.IdempotencyConflict`; busy/stale-source/terminal-digest conflicts use typed 409 codes. A retained idempotency identity whose report has been erased returns 410. Unavailable schema/provider capability uses a typed existing-convention 503 before execution. Quota exhaustion uses a typed 409 refusal naming report-only cleanup, with zero provider calls.

After a registration exists, execution or evaluator failures normally return a terminal 200 report whose Outcome is InsufficientEvidence and whose closed reason explains the evidence limit. Invalid policy discovered during preflight never starts an experiment. Request abort may prevent delivery; the report is retrieved separately afterward.

Use `ILongRunningOperationCoordinator.StartWithRequestIdentityAsync` with the caller evaluation ID, submitted-request digest and frozen-plan effect digest. Its coordinator operation ID may differ from the requested evaluation ID. A replay never acquires a second execution owner. Resolve existing request identities before plaintext hydration; same-ID/same-request returns existing authorized status/report with zero source refresh, provider sends or repeat charges. Same ID with different checks, scope, options, sample count, workload or targets conflicts. Returning an existing report does not claim its sources are still current or confer ranking eligibility.

Report deletion is exact and all-or-nothing, retains no source/check subset, releases registration quota, and is idempotent under the retained operation/request-identity lifecycle. It cannot permit another provider execution under a still-retained identity. Deleting report evidence never deletes or rewrites source memory. No list/export surface, Forge UI or CLI addition is needed in this slice.

## 9. Durable evidence and privacy boundary

Add Core schema version 19; Covenant Canonical remains version 6. Freeze the existing Core v18 fingerprint/fixture, add declarative object files and the V19 transition, and extend only the current UTC-column inventory. No numbered EF migration, source-version rewrite or compiled-model regeneration is needed.

Use three source-owned table groups: `memory_evaluations` for immutable prepared registrations/ownership/digests; `memory_evaluation_sources` for bounded exact intervention/dependency coordinates; and `memory_evaluation_reports` for the once-appended terminal aggregate. The report may encode its bounded typed per-check aggregates with a source-generated metadata shape; it never accepts arbitrary JSON from a caller. Schema constraints, store validation and unique keys enforce bounds, ownership and terminal immutability. Use parameterized raw SQLite on admitted connections/transactions.

The report store accepts only validated metadata DTOs. Legal fields are format/policy versions, evaluation/operation IDs, canonical creation/completion UTC, explicit scope/Campaign and dataset/restore ownership, frozen request/plan/workload/provider/model/options/prompt/evaluator/manifest digests, exact typed source coordinates and provenance metadata, counts, closed states/reasons, response-evidence digest and known/unknown aggregate usage/cost/latency. A terminal report digest binds all its canonical metadata.

Forbidden fields include workload text, answers/reasoning, memory content, expected literals, regex patterns, schemas, check labels, free-text verdicts, endpoint URLs, arbitrary provider identifiers copied from responses, credentials, request/response payloads and raw exception text. Public summaries, traces, progress, operation checkpoints, audit and Chronicle paths carry only code-owned status/IDs/counts, never input or output text. The dedicated transport cannot log payloads.

Declare the report sink in `CovenantDerivedOutputInventory` as `ContentFreeMetadataOnly`, with an explicit rationale permitting the bounded opaque source/scope coordinates and digests needed for lifecycle/replay. This does not make it an unprotected census or authorize plaintext storage. Its governed contract must be enforced in both directions by the inventory tests. Stored metadata and report reads remain authenticated and no-store.

Quota reservation is atomic when the prepared registration/source manifest commits and occurs before any provider call. Concurrent prepares cannot exceed 1,024 registrations. Refuse at the limit with an actionable report-only cleanup path; no silent eviction and no need to erase production memory to free quota. A transaction failure leaves no partial registration/source set. Terminal publication is unique and atomic; it cannot recreate an erased registration.

## 10. Cancellation, crash and recovery

Register exactly one `MemoryEvaluation` operation kind, one recovery descriptor and one idempotent handler using `AbandonSafely`. There is no provider replay/retry/resume action for this kind, including through `operation retry`. A new experiment requires a fresh evaluation ID and authenticated request.

Checkpoints contain only evaluation/operation IDs, frozen digests, closed phase, counts and bounded numeric aggregates. They contain no workload, answers, source content, live Task/token/client/DI object, invocation, lease, credential or executable authority. Use the existing encrypted checkpoint lane where a checkpoint is needed.

Heartbeat/lease failure, request/operation cancellation, shutdown or authority/configuration/source revocation stops further sends and joins the in-flight adapter. The coordinator does not supply a cancellation token and may renew a Cancelling operation. Use an independent, TimeProvider-driven operation-state observer at one-second intervals on an independently admitted connection; Cancelling, terminal state, missing ownership or observer failure cancels active I/O. Also check operation state immediately before each send. A successful heartbeat is never interpreted as proof that cancellation was not requested. The active slot is released only after the adapter has drained, not when cancellation is requested. Independently committed disclosure/accounting remains truthful about possibly completed remote calls.

Scored publication requires current authority and source/configuration fences. If those checks fail, settle only InsufficientEvidence through a narrow metadata-only path. If no registration was committed, abandon the operation with NotPrepared and publish no fabricated report. If a registration exists, recovery appends Interrupted/InsufficientEvidence from its metadata and checkpoint aggregates without hydrating plaintext or acquiring protected read authority. If registration/source cleanup already removed it, recovery must not recreate it. A crash after report commit but before operation completion preserves that report and reconciles only the operation ledger.

Use fault injection at every registration, disclosure, send, aggregate/checkpoint and terminal-commit boundary. Cancellation or a crash cannot convert a partial favorable run into Benefit, double-charge by replay, leave an active execution slot released early, or resurrect erased evidence.

## 11. Lifecycle and future consumption

Evaluation evidence has the existing Annals source-owned lifetime. `RetentionDataClass.Annals` has no independent age rule; do not add one implicitly or invent a retention setting in this slice. Report-only deletion provides ordinary quota management.

The complete coordinate manifest is necessary, not replaceable by a manifest digest. Any participating source/dependency erasure removes the entire registration, report and source/check metadata. No partial report survives with a changed manifest. Integrate with the shared `AnnalsErasurePlan` for Saga/Lexicon and the separate `CovenantEntryErasurePlan` for Covenant. Capture matching report IDs before source versions disappear. Append any new `MemoryErasureTableCodes`; never reorder existing persisted codes. Cleanup must work even when SQLite foreign-key cascades are disabled.

Campaign deletion purges reports owned by that evaluation Campaign, including reports whose selected sources were Global. Source-Session deletion purges dependent reports through retained canonical subject-owner Session coordinates, independently of the version-authoring SourceSessionId, even though evaluation itself has no Session. Apply the same rule to every dependency subject. Store/Campaign memory reset, selective erasure, protected artifact purge and source retention use these same closures. Ordinary protected-state reset refusals and independent nonrevocable disclosure evidence remain unchanged.

Include all three table groups in Annals status/counting, prune dry-run and apply accounting, factory-reset inventories and deterministic deletion order. Extend derived-evidence count-once logic currently specialized to `long_rest_` tables so a multi-source report is counted/deleted once. Full backup includes valid terminal evidence; prepared/incomplete registrations are sanitized to interrupted metadata or removed on restore and can never be resumed. Staged restore uses the shared erasure closures and destination-authoritative generation rules; imported source coordinates that cannot be reconciled cause dependent reports to be dropped, never rebound to look-alike content or a new owner. Restored reports are historical evidence and cannot resume an experiment or mint authority.

A later #95 consumer must prove exact current source versions/scope/generations and compatible workload/model/evaluator policies before interpreting a report. This slice provides evidence and exact joins only. It does not change ranking, decay, review or lifecycle based on the report.

## 12. TDD acceptance and delivery

Write and observe behavioral RED tests before implementation. Prove the following with real policy/SQLite/endpoint code and bounded fake provider transport where needed:

1. Literal canonical digest vectors; target permutation stability; changed model/options/workload/checks/source versions produce different bindings; submitted-request identity is distinct from execution-plan identity.
2. Derived prompt arms preserve exact non-memory bytes and occurrence attribution; treatment/control differ only by selected memory spans; no hidden retrieval/tools/cache, admission change, truncation or larger control answer budget; same model/options for each pair.
3. Exact Covenant/Saga/Lexicon version/hash/scope/validity/dependency proof; claimless/stale/erased/shadowed/suppressed inputs fail closed; mutable caller collections cannot change a prepared plan.
4. Complete outcome truth tables, including a check regression hidden by higher total pass counts; same check vectors versus same totals; mixed pairs; invalid policy versus a valid failed assertion; regex timeout/unsupported schema/output overflow never become Harm or Benefit.
5. Fixed pair/order/count commitment before first send; no early stop/cherry-pick; correct alternating arm order; no cross-pair feedback; insufficient results for every incomplete or invalid requested pair.
6. Real protected-read boundary and NoTools invocation truth table; no collector/mutation ports; source/configuration A-to-B-to-A, dataset/Campaign deletion, reset, restore and authority revocation before/between sends and before publication; disclosure commits precede every physical send and no send follows failed acknowledgement.
7. Exact physical-send count, guarded destination, bounded response read, model mismatch, complete versus truncated answer, unknown usage, normal accounting; no SDK/schema retries, fallback or duplicate concurrent execution.
8. Same-ID replay has zero sends/charges/hydration; different-body conflict; concurrent replay; cancellation/disconnect/shutdown/heartbeat failure; all crash windows; recovery only settles/abandons and never sends; report deletion cannot reopen a retained operation identity.
9. Immutable prepared metadata/final report, atomic quota/source reservation and terminal insertion, rollback, all input/output privacy sentinels absent from database/checkpoints/logs/wire reports, and typed sink/inventory enforcement.
10. Fresh/evolved schema equivalence, frozen v18 preservation, UTC inventory, report-only deletion, complete-report closures for every source/store/scope/Session/dependency (including operator-corrected Saga with null version-authoring Session and a retained canonical owner Session), quota release, count-once dry-run/apply agreement, cascades-disabled cleanup, backup/restore sanitization and no resurrection.
11. API envelopes/names/source-generated JSON/no-store/status/errors/body bounds; get/delete never hydrate plaintext or call providers; disabled/absent capability preserves all existing ordinary DCI prompt bytes and token/retrieval behavior; no new Forge/CLI/MCP or production-memory mutation surface.

The implementation plan must name the concrete owning files, task interfaces and each task's RED/GREEN commands after this spec is approved. Operator amendment after spec approval: enable latest-stable SDK roll-forward and always use the latest version; the installed SDK is 10.0.402. Preserve the minimum serviced floor and Native AOT provenance gates, resolve/install the latest stable SDK in CI, and record the exact toolchain used for qualification. Do not provision the older 10.0.401 SDK merely to preserve the former exact pin. Baseline CI is green in run [39069929236](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/actions/runs/39069929236); that evidence qualifies only the unchanged baseline.

Update owning architecture/persistence/testing documentation in DESIGN §4.3/§20 and affected lifecycle sections; exact routes/shapes in API; OATH delivery-status wording only when the feature is delivered. No public README, Forge or CLI/config documentation change is needed for an unchanged surface. Keep this design and its later implementation plan with the implementation.

Final qualification uses appropriate focused tests, build/format/schema checks, complete runtime coverage, Compendium/Forge regression suites, source-analysis authority, Native AOT/SQLCipher/provider-contract gates and all configured native CI jobs for the exact final candidate. CI has no push trigger; explicitly dispatch the candidate or use a PR. Do not duplicate full Arcanum runtime suites already exercised by the coverage authority. Integrate only the qualified candidate into existing `memory-enhancement`, preserve its tree, verify local/tracking/remote equality and issue state, and then mark #106 completed. #73 and #95 remain open. Honor the operator's delivery condition: keep drafts uncommitted, create the candidate commit only after local qualification is green, submit that exact commit to hosted CI, and merge only after terminal green CI. This review draft stays uncommitted until the local candidate is qualified.
