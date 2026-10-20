# Dynamic Context Injection v2 Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development with TDD, scoped task reviews, and independent whole-branch review. Preserve the operator-authorized commit/merge/closure workflow; do not make intermediate commits before the relevant checks are green.

**Goal:** Deliver #103 on `memory-enhancement` with safe generation-bound stable-prefix planning, deterministic invalidation, default-off byte parity, and actual local Ollama cache measurements.

**Architecture:** Extend the existing render/freeze/dispatch pipeline. A closed Core policy admits code-owned sources; source owners retain reference-bound tool provenance. Per-client credential/configuration identity and one atomic content-free authority snapshot bind each attempt. A code-owned scope marker in the enabled prefix makes changes visible to automatic token-prefix caches; existing protected authority still controls dispatch.

**Tech Stack:** .NET 10 / Native AOT, existing OpenAI-compatible SDK transport, immutable typed records/classes, xUnit, local Ollama 0.40.6. No new database schema or response cache.

**Spec:** ../specs/2026-10-19-dci-v2-provider-cache.md

## Global Constraints

- Default-off DCI prompt bytes, attribution, retrieval, and normal inference remain unchanged. No protected canonical read merely for cache metadata.
- Covenant/derived/sensitivity-unknown sources never enter the eligible prefix. Protected calls retain explicit-cache suppression.
- Initial eligible prompt source inventory is code-owned framing/placeholders/terminal formatting. Codex/Spell file content stays ineligible until its owner supplies actual scope/sensitivity proof; this implementation does not invent one.
- Every provider-prefix tool needs reference-bound trusted provenance and audited code-owned schema bytes. Unknown/client/external MCP tools and configuration-derived `workspace_check` schemas make v2 optimization unavailable; do not remove tools for cache benefit.
- A scope marker contains no raw identity or content, grants no authority, and is part of the exact rendered prefix digest and preamble attribution. No marker on the disabled path.
- New optional proof failures fall back to ordinary authorized payloads. Existing security failures remain explicit. No remote-erasure promises.
- Use current delivery baseline `425a2db3`; preserve main and unrelated work. User authorized implementation, appropriate subagents, final commit, merge into memory-enhancement only after green, then #103 closure. #73 remains open.

## Review Focus

- Forged Stable flags, unknown kinds, sensitive tools and code-owned-looking names must not manufacture eligibility.
- Mutable settings/client mismatches, ABA changes, and torn authority snapshots must invalidate before a physical send.
- Fallback/retry/compression/tool changes cannot reuse stale bytes, tools, or authority; unavailable cache proofs cannot break ordinary inference.
- Automatic Ollama caches ignore local keys: scope changes must alter the enabled rendered marker, and preceding public tool/framing reuse is not a security failure.
- Missing cached usage/pricing is unknown; local model load and short samples cannot masquerade as comparative savings.

### Task 1: Closed policy and exact prefix planning

**Files:** Core/Intelligence/PromptCacheContracts.cs; new Core/Intelligence/PromptCachePolicyV2.cs; Infrastructure/Intelligence/SystemPromptDocument.cs; Api/Intelligence/PromptCachePlanner.cs; tests/Intelligence/PromptCachePlannerTests.cs and new PromptCachePolicyV2Tests.cs.

**Interfaces:** Define `PromptCacheSourceClass` (Unclassified, CodeOwnedFraming, CodeOwnedFormatting, Protected, Dynamic); `PromptCachePolicyV2.IsEligible(sourceClass, stable, sensitive)` with version `arcanum-dci-cache-policy-v2`. Add an immutable `PromptCachePrefixDecision` carrying policy, range, exact UTF-8 digest, scope marker digest, ordered tool digest, and eligibility/reason. Keep source text out of metadata. Legacy entry points remain compatible.

- [x] Write behavioral RED for sensitive/unknown/forged-stable sources, exact contiguous prefix hashing, stable suffix exclusion, mutated prefix/tool order, unsupported provider and existing legacy output. Watch the intended failures.
- [x] Implement the closed Core policy, source-class projection, exact rendered prefix-range hashing, and immutable v2 decision using existing length-prefixed hashing. Correct the existing suffix-digest defect with regression coverage; no product layout enablement yet.
- [x] Run focused policy/planner/DCI/adapter tests; independently review the task and fix confirmed defects with RED/GREEN coverage.

### Task 2: Freeze client and lifecycle identity

**Files:** Core/Configuration/PublicConfigurationSettings.cs (default-false DynamicContextInjectionV2 gate); Core/Security/IProviderApiKeyResolver.cs; new ProviderCredentialStamp.cs; Infrastructure/Security/ProviderApiKeyResolver.cs and ProviderCredentialStore.cs; Api/Intelligence/ChatClientFactory.cs; new PromptCacheConfigurationEpoch.cs; Core/Intelligence/IPromptCacheAuthoritySnapshotProvider.cs; Infrastructure/Security/CovenantRuntimeGenerationProvider.cs and dependency registration; API bootstrap; security/factory/epoch tests.

**Interfaces:** `ResolvedProviderCredential` is a nonrecord secret-bearing class with redacted formatting and optional `ProviderCredentialStamp`. Add opt-in `ResolveWithIdentityAsync` / `IsCurrentAsync` to the resolver with conservative compatibility defaults. `PromptCacheConfigurationEpoch.Capture()` returns an immutable snapshot and `IsCurrent(stamp)` proves currentness. `IPromptCacheAuthoritySnapshotProvider.Capture()` projects one `CovenantRuntimeGenerationProvider.Current` read. `ChatClientLease.PromptCacheIdentity` binds frozen provider/model/configuration/credential facts to the actual client.

- [x] RED tests for secret/stamp same-read binding, keyless vs failed read, environment precedence, redacted formatting, observed credential/configuration A-B-A, failed mutation, mutable provider settings, and atomic authority tuple. Explicitly test disabled v2 uses old resolution and no protected reads.
- [x] Implement process-private monotonic observed epochs, change notifications for successful local credential mutations, immutable settings/client binding, and atomic content-free authority projection. External changes wholly between observations cannot be detected; document the observation limit.
- [x] Verify focused tests and task review. Do not treat the stamp or cache key as authentication/egress authority.

### Task 3: Source provenance, gated layout, and every dispatch

**Files:** FeatureSettings and owning configuration metadata/docs; Infrastructure/Mcp/ConnectionManager merge/lifecycle owner and tool contracts; API native/client tool-construction seams; Infrastructure/Intelligence/SystemPromptBuilder.cs; Api/Intelligence/WizardIntelligenceProvider.cs; new PromptCacheDispatchValidator.cs; ModelCallExecutor.cs; relevant serialization/attribution/configuration/Wizard/executor tests.

**Interfaces:** Consume `Features.DynamicContextInjectionV2` default false from Task 2. Maintain an owner-issued reference-bound tool provenance map; never read proof from tool name/AdditionalProperties. Add an enabled build path over the existing document that emits a code-owned scope marker and moves only eligible segments with intact semantic framing. Keep the ordinary document/payload available for optimization fallback. `PromptCacheDispatchValidator.ValidateAsync(actualClient,messages,options,context,ct)` returns closed Current/OptimizationUnavailable/ReplanRequired results. Use `invocationContext.Campaign` and one authority capture per attempt; no path-based re-resolution.

- [x] RED for disabled golden bytes/accounting; unknown/client/external/config-derived tool schemas; static source role/precedence/fencing; ineligible sensitive/Covenant calls; marker changes on each relevant generation; same scope stable marker; unknown proofs ordinary fallback.
- [x] RED for changed final bytes/tools/client/provider/tokenizer/credential/configuration/authority before buffered and streaming sends; fallback, retry, compression, no-tools restarts; cancellation; subagent/background exclusion. No provider I/O on stale protected authority.
- [x] Implement reference provenance at actual owner construction, code-owned static prefix/marker, once-rendered span attribution, attempt binding and pre-send revalidation. Missing cache proofs select ordinary payload; changed materialized source requires replan before send. Preserve normal tools and established Covenant publication.
- [x] Keep OpenAI explicit wire fields limited to verified profiles. Ollama uses automatic caching and reported standard usage; no unsupported cache fields or new provider kind. Add actual SDK wire goldens and owning docs/config binding tests.
- [x] Run focused and affected architecture/boundary tests; independent task review and regression fixes.

### Task 4: Reproducible fixture and real Ollama evidence

**Files:** new tests/RetroDownfall.Arcanum.PromptCache.Benchmarks/ (project, Program, manifest, reports, source-generated JSON); new scripts/benchmark-prompt-cache.sh; tests/Benchmarks/PromptCacheBenchmarkTests.cs; CI compiled-project/fixture coverage and artifacts; DESIGN measurement documentation.

**Interfaces:** Credential-free fixture mode and opt-in `--ollama-endpoint` live mode use the production builder/planner/factory/executor/SDK path. JSON report modes are explicit; samples retain nullable cached usage, prompt/output tokens, monotonic first generated token/first answer/total latency, optional provider prompt timing, modeled or local pricing basis, and invalidation observations. No text/key/secret artifacts.

- [x] RED tests for missing usage, short prefixes, cost provenance, warmup exclusion, mixed trial order, manifest revision/digests, corruption, local invalidation vs remote observations, and source-generated serialization.
- [x] Implement a pinned public corpus and alternating matched baseline/candidate sequences (cold, warm, suffix, prefix, tools, scope/profile/credential/reset/restore/protected-state identity changes). Use bounded output and explicit sample counts; record load separately and keep energy/hardware costs unknown. Fixture latency is local planning/serialization only.
- [x] Verify fixture through real SDK serialization and run live `gemma4:e4b` on Ollama 0.40.6 after a cached-usage support probe. Record actual immutable model digest. Require useful saved prompt processing, observed hits, explained latency behavior, and passing invalidation checks before enabling eligible layout execution.
- [x] Capture a content-free report with exact revision/corpus/tool/settings/runtime/RID. Keep default off; do not invent provider billing savings. Add credential-free CI fixture artifact, without paid inference on PRs. Independently review task and repair confirmed regressions.

### Task 5: Exact-candidate qualification and delivery

**Files:** owning DESIGN/CHAT-LOOP/Compendium/API/CLI docs only where affected; spec/plan evidence; final implementation files; existing CI scripts.

- [x] Whole-branch review on the strongest available model against #73/#103, approved spec, TDD evidence, disabled parity, authority and measurement limits. Fix confirmed issues with behavioral regressions.
- [ ] Run required local solution build/tests, DCI suite, coverage/format/source-analysis closure, native SQLCipher/AOT/shipping/provider smoke as required by canonical workflow. Preserve and report each failure; do not weaken gates.
- [ ] Commit reviewed locally green candidate, push isolated feature branch for canonical exact-SHA CI, and wait for every required lane including source-analysis reconciliation and both Windows RIDs. Changed candidate requires appropriate requalification.
- [ ] Fetch current delivery ref and prove fast-forward eligibility. Integrate only the exact qualified candidate, verify local/remote SHA/tree equality and terminal checks, close #103 completed with concise evidence, and keep #73 open. Attach any created PR. Clean up only this task's managed worktree when safe and delivered.
