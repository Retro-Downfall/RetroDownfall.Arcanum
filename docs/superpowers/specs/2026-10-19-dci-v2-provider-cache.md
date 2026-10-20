# Dynamic Context Injection v2: secure provider-cacheable prefix (#103)

Status: design approved by the operator on 2026-10-19; implementation uses local Ollama measurements as clarified below.

## Intent and delivery contract

Implement #103 under #73 on the existing `memory-enhancement` lineage. Preserve the approved Covenant/OATH authority, disclosure, sensitivity, and attribution rules. The current baseline is `425a2db3f5f1394cf2602f5bc32944cbc64114d0`; #84 and #86 are completed prerequisites.

The deliverable is a closed eligibility policy, an immutable prefix decision bound to the actual provider attempt and current authority, deterministic lifecycle invalidation, and reproducible cache-economics evidence. It is not a response cache. A generation change invalidates Arcanum's prior cache decision and emitted reuse identity. Remote separation is claimed only for a selected dialect that guarantees it; no identity change deletes a remote provider cache.

Layout changes remain disabled until a matched live-provider experiment establishes useful economics and the semantic/security tests pass. Fixture observations cannot substitute for provider-reported usage or measured provider latency. #103 remains open until its acceptance evidence exists; #73 remains open for its other delivery obligations.

## Existing behavior and verified gaps

`SystemPromptBuilder` renders Preamble, DATA, CONTEXT, and INSTRUCTIONS. Covenant sections and rollups are volatile; Covenant sections are sensitive. Nonempty DATA normally ends the leading stable prefix before Codex and Spells.

`PromptCachePlanner` stops boundary selection at the first volatile segment, but hashes all Stable segments, including later suffixes. It does not apply a sensitivity or source-kind policy. Its key includes provider name, model, namespace, dialect, retention, stable-segment digest, and finalized tool digest, but omits the required tokenizer and lifecycle authority identities.

The baseline also lacks the explicit-cache suppression required by OATH section 12.4 on Covenant-bearing calls. Repair that contract with whole-document and protected-history wire regressions.

`ModelCallExecutor` checks the selected provider/model and system-message indexes, but does not prove that the final prefix bytes and tools still match the planned payload. Existing usage telemetry distinguishes a sent key from a reported hit, but has no cache-specific per-call latency or complete invalidation benchmark.

The baseline focused planner, DCI regression, and buffered/streaming adapter tests pass: 18 passed, zero failed, zero skipped. This establishes the starting point, not final qualification.

## Approaches considered

1. Repair the current boundary/digest and retain the current layout. This is a useful correctness fix but does not by itself deliver #103.
2. Add a gated v2 policy and identity to the existing render/freeze/dispatch pipeline, with measured enablement. Recommended: reuse the existing tokenizer, attribution, authority, and provider accounting owners.
3. Add a generic cache subsystem and several provider dialects. Rejected for this issue: it expands retention and pricing contracts without providing stronger evidence for the selected provider.

## Closed source and sensitivity policy

Eligibility is a code-owned policy version and a closed switch over source classes. Stable and boundary flags are necessary metadata, not authority. Unknown enum values, missing provenance, uncertain scope, and sensitive material fail closed.

| Source | v2 policy |
| --- | --- |
| Code-owned persona, structural framing, literal empty placeholders, terminal formatting | Eligible when their rendered bytes are invariant for the bound request settings. |
| Master Codex and primary/resonant Spell instructions | Eligible only through a typed source proof of their canonical scope, exact content version, and unprotected sensitivity. Preserve existing instruction precedence and adaptive framing. Existing Stable flags alone are insufficient; lack of proof leaves the source in its original suffix. |
| Covenant Confirmed/Proposed or Covenant-derived material | Ineligible. Enforce the existing OATH suppression contract for explicit cache directives on Covenant-bearing calls; baseline review found that the implementation does not yet enforce it. Protected caching is unconditionally excluded from this implementation. Introduce no dormant protected branch or configurable flag that can lift this exclusion. |
| Workspace paths/indexes, retrieved DATA, Lexicon, Saga, Tapestry, attachments, streams, Campaign/session rollups and summaries | Ineligible. |
| Per-request additional instructions, client-supplied content, auxiliary/background/subagent purpose | Ineligible. No new inherited memory authority. |

The policy also covers every tool description and schema placed inside the provider's actual prefix: a digest alone does not make a tool definition eligible. Admit only tool definitions with a closed trusted provenance/sensitivity classification; unknown MCP/client definitions make explicit optimization ineligible when the provider includes them in its cached prefix.

Source-owner review found that `CodexReader` currently returns merged text and `SpellScanner` returns parsed text/path without a canonical sensitivity/version proof. These sources are therefore not eligible in the current implementation. Their promotion requires a concrete typed read result from those owners and same-read sensitivity/version evidence before the corresponding layout task can be accepted. `McpConnectionManager`/`McpToolMerger` already own internal/global/local origin metadata; preserve that origin with the finalized tool surface to classify code-owned native schemas without name/type heuristics. The immediately provable production inventory is code-owned framing/formatting plus proven internal native schemas. Whether that inventory alone is economically useful is a measurement question, not an assumed outcome. Missing reader proof remains an acceptance blocker for Codex/Spell promotion and must not be bypassed to close #103.

Do not classify text with a model, keyword scan, or an assumption that a local file is unprotected. Source proof must come from its existing canonical reader and sensitivity owner on the same read, and remain valid through the dispatch gate. If a source owner cannot supply that proof safely within this issue, exclude that source rather than add a parallel authority system.

For the enabled layout, emit an eligible contiguous prefix followed by the ordinary dynamic suffix. Preserve each source's semantic role, heading, relative precedence, fencing, and token attribution. Do not move a CONTEXT or DATA span under an INSTRUCTIONS heading. Render once; derive the prefix decision and attribution from spans over that exact string. The disabled path uses the existing builder unchanged.

## Immutable decision and identity

One decision identifies the policy version, eligibility/reason, exact prefix range and UTF-8 digest, effective tokenizer/framing identity, finalized ordered tool-definition digest, and opaque reuse identity. All fields are content-free. No raw content, paths, API-key digests, or protected identifiers become telemetry labels or diagnostics.

The reuse identity uses a versioned, domain-separated, length-prefixed cryptographic construction over:

- Actual client-bound provider type, canonical endpoint identity, configured provider identity, and resolved model.
- Effective tokenizer profile/version and framing settings after fallback resolution.
- Exact rendered prefix bytes, source classes/versions, policy version, semantic namespace, and exact finalized ordered tool definitions.
- Process boot identity and monotonic observed provider/configuration/credential epochs, captured with the settings and credential used to construct the client lease.
- Canonical Campaign binding kind, Campaign identity and availability generation, physical-root identity policy/revision/digest. Explicit GlobalOnly is distinct from unresolved context.
- Published installation/runtime authority, authority epoch, master-key version, recovery-envelope epoch, host-tools state/transition, and protected capability/dataset/canonical/deletion generations where relevant.

Published content-free authority snapshots may be read without opening protected canonical stores. A disabled Covenant path must not gain protected reads merely for cache identity. Missing or unavailable authority cannot be represented as a valid established generation. An unprovable optimization identity or source proof makes caching ineligible and preserves the ordinary authorized inference path; it does not make absent Covenant authority a new inference prerequisite.

Configuration epochs must resist A-to-B-to-A reuse. Credential resolution must capture an opaque generation together with the actual resolved secret; provider name or credential variable name cannot stand in for credential identity. Observe environment/OS credential changes at resolution as well as local credential mutations. Keep secret comparison state private and never export a secret-derived fingerprint.

Campaign/profile/credential changes, feature toggles, reset, restore, protected mutations, host-tools transitions, and restart invalidate reuse through their owning generation publishers. Existing authority leases remain the authority to dispatch; the cache identity grants no read or egress permission.

## Dispatch, retry, and provider wire

Freeze the decision with the actual materialized provider payload. Immediately before every physical attempt, prove that prefix bytes, finalized tools, provider/model/tokenizer, source proofs, and lifecycle generations still match. A changed payload requires replanning or an ordinary nonoptimized payload. Unavailable cache-only proofs select the ordinary authorized payload; stale protected authority follows its existing explicit refusal. Cache optimization must not hide canonical corruption.

Use the same rule for streaming, buffered, tool continuation, structured retry, compression, no-tools restart, and fallback. Preserve clone-before-wire adaptation so reusable turn state is not mutated. Auxiliary, background, subagent, and unsupported-provider paths remain conservative.

Select one documented and golden-tested wire contract for the benchmark provider. Existing root-only key/retention support does not implement selective prefix retention. Do not infer support for a new model generation from its family prefix. Constrain verified rows or implement a new dialect and its accounting deliberately. Unknown rows receive no new directive.

The current OpenAI guide distinguishes older key/retention models from GPT-5.6+ explicit breakpoints and cache-write pricing. If the selected model uses the newer contract, support its actual Chat Completions request fields and cache-write usage/cost before measuring it. A sent key is never counted as a hit, and absent usage or pricing remains unknown.

## Measurement and enablement

Provide two explicitly distinct evidence modes:

- Credential-free fixture mode runs the production document/policy/planner/freeze/executor/SDK serialization path. It proves byte parity, exact-prefix identity, closed sensitivity/source decisions, mutation rejection, and lifecycle invalidation. Report actual local planning/serialization time separately from synthetic usage and modeled cost. Never report fake network delay as provider latency.
- Live mode uses a public nonsensitive corpus and an explicitly selected provider/model. Interleave baseline/candidate requests with matched settings/tools, separate warmup, record provider usage and monotonic total latency/streaming time to first token, and retain an explicit pricing snapshot/source/date. Observe cold, warm, changing-suffix, changed-prefix, and changed-identity cases.

Reports include evidence mode, revision/tree and dirty state, corpus/tool hashes, SDK/runtime/RID, exact model/contract/settings, trial order, sample counts, usage-observed denominator, request hit rate, cached-token ratio, total and first-token latency distributions, cost, and invalidation outcomes. Account for cache writes when reported. Zero configured price is not evidence of free inference. No raw prompts, credentials, protected context, or cache keys enter report artifacts. Changed-identity trials prove local decision invalidation and report remote observations; they do not require a remote miss unless that dialect guarantees it.

Useful enablement requires observed reusable-prefix hits and positive saved prompt processing (or net input-charge savings after writes for a billed provider), no unexplained latency regression in the matched workload, all declared invalidation checks, and passing semantic/security compatibility tests. Publish the observed results and uncertainty; do not turn a noisy latency sample into a universal claim. Without this evidence, the new layout stays disabled and the issue is not represented as fully delivered.

The selected live target is local Ollama 0.40.6, initially `gemma4:e4b` (installed model digest `a3d2b95350da03ff9b1943a753bb6617c49a3ee462b632a758518ec817743986`). Use the production `/v1/chat/completions` contract and SDK usage mapping. The daemon exposes `usage.prompt_tokens_details.cached_tokens`; native `prompt_eval_cached_count` and streaming `timings.prompt_ms` can corroborate it. No external credential is required. Report local provider usage charges as zero for this run and energy/hardware cost as unmeasured. Absence of an explicit control profile is not an absence of automatic caching.

Ollama does not accept the OpenAI explicit cache-key/retention controls. Do not send those fields or broaden the verified OpenAI dialect. The enabled v2 prefix includes a code-owned opaque scope marker derived from the current content-free reuse scope before eligible scoped text. Bind the final rendered prefix, including that marker, in the decision. This makes scope changes visible to token-prefix matching without asserting remote cache deletion; providers may still reuse preceding public framing/tool tokens. The disabled path contains no marker. The marker grants no authority and is attributed as code-owned preamble.

A three-request public-corpus instrumentation probe on the selected daemon observed prompt totals of 1,546 tokens and cached subsets of 0, 1,535, and 1,535; streamed prompt-evaluation durations were 612, 32, and 32 ms. Model loading and this small sample preclude treating those numbers as a layout comparison or final benchmark evidence.

## TDD and qualification

Start each behavior change with a failing behavioral test and record its intended failure, then implement the smallest correction and verify GREEN. Cover at least:

- Disabled/default DCI byte and attribution parity, including absent Covenant, Unicode, fences, empty sections, all existing retrieval sources, and request instruction precedence.
- Unknown and forged-stable protected/sensitive kinds, missing source proof with ordinary-turn fallback, mixed source roles, protected-call directive suppression, subagent noninheritance, and safe system text accompanied by an ineligible tool description/schema.
- Exact prefix digest; suffix edits leave it unchanged, prefix/tool/order/tokenizer/provider changes do not. Final payload changes cannot reuse a stale decision.
- Campaign/root/profile/credential/authority transitions, A-to-B-to-A, reset, restore, host-tools taint, feature toggles, and restart; include stale in-flight attempts.
- Buffered/streaming actual SDK wire goldens, absent usage/pricing, cache-write accounting when applicable, and content-free reports.
- Fixture manifest/report integrity and matched live benchmark evidence.

Update the owning DESIGN, CHAT-LOOP, configuration/API/CLI documents only where their contracts change. Keep public README content unchanged unless intentionally affected. Add proportionate credential-free CI coverage and artifacts; keep paid live inference out of ordinary PR CI.

After independent review and required local checks, commit the reviewed candidate, run all canonical exact-SHA CI lanes (including source analysis, coverage, macOS and both Windows shipping/AOT lanes), and repair confirmed failures with regression coverage. Fast-forward `memory-enhancement` only after terminal green qualification. Prove remote SHA/tree equality, then close #103 completed. Preserve unrelated checkout work.

## Source contracts

- GitHub issues #73, #103, and completed blockers #84/#86.
- `docs/Arcanum.OATH.md` sections 12.4 and 20.9.
- `docs/Arcanum.DESIGN.md` sections 10.5, 10.13, and 22.3.
- OpenAI prompt-caching guide: https://developers.openai.com/api/docs/guides/prompt-caching (reviewed 2026-10-19).
- OpenAI Chat Completions reference: https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create (reviewed 2026-10-19).

- Ollama usage documentation: https://docs.ollama.com/api/usage (reviewed 2026-10-19).
- Ollama installed-version compatibility source: https://github.com/ollama/ollama/blob/v0.40.6/openai/openai.go (reviewed 2026-10-19).
