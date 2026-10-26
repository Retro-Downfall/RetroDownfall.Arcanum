# Semantic spell routing diagnostics implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans inline. Controller prohibits children and requires independent review of the final immutable candidate.

**Goal:** Complete RV105 with actionable, privacy-safe, configuration-epoch-throttled semantic spell routing fallback Warnings without changing routing or cache policy.

**Architecture:** Resolve the scoped router through the existing DI registration with IOptionsMonitor, capturing CurrentValue once at resolution entry. A static ConditionalWeakTable keyed by that exact settings object holds only an atomic six-bit reason set shared across router scopes. The singleton cache retains its existing key/locking/invalidation behavior; sanitize its existing Debug logs only.

**Tech Stack:** .NET10, C#, Microsoft options/DI/logging, xUnit.

**Spec:** RV105 in `docs/reviews/2026-10-24-repository-code-review.md` lines1596 onward, U02 usability finding6, and remediation register; owning architecture DESIGN21.10. Controller accepted monitor-backed epoch identity; weak snapshot keys would not deduplicate across requests.

## Constraints and review focus

- Exact base `1e71847ec5450ad0bddf764619e8c6dea5ee3eda`; assigned maintenance-outcomes worktree; preserve guardrail branch at `45b6279e64c82240b0e3f53bcc08bef8530fe079` and all earlier candidate packages.
- No children, unrelated Wizard behavior, cache/performance policy changes, RV127 credit, schema/config changes, installed/live providers, operator commands, main or push.
- Closed reasons only: catalog unavailable, provider unavailable, prompt embedding failure, dimension mismatch, no matching cached vectors, unexpected error. Fixed Warning includes possible billable LLM routing and existing `integrations.embeddings.provider`, `.model`, `.dimensions` keys plus `arcanum doctor` guidance; never emit private values/raw Error.Code, raw exception/message, prompt, spell names/path, endpoint or credentials.
- Real scoped DI must prove one Warning per reason across scopes/parallel calls, replacement options epoch warns again even for equal config values, and an in-flight call keeps its captured epoch after awaits.
- Disabled, empty catalog and genuine no-match are normal controls. Requested cancellation propagates without a failure Warning; unrequested exceptions retain existing fallback behavior.
- Preserve pure/hybrid/default decisions, embedding observation/counts, cache reuse/invalidation and actual LLM/billable invocation counts. Current pure/hybrid policy is code-owned, not a new operator setting.

### Task1: Test scoped fallback diagnostics before behavior edits

**Files:** `tests/RetroDownfall.Arcanum.Tests/Intelligence/SemanticSpellRouterTests.cs`, `tests/RetroDownfall.Arcanum.Tests/Weave/SpellWeaveCacheTests.cs`, relevant existing Wizard consumer tests.

- [x] Add actual AddOptions/monitor-cache/scoped-router/singleton-cache tests for all six failures, cross-scope suppression, epoch replacement, concurrency and mid-await replacement attribution; include private canaries and invocation counts.
- [x] Add normal/cancellation and genuine Wizard fallback billing controls.
- [x] Add cache typed-failure/throwing-provider log privacy tests and update the old arbitrary-code assertion to a closed local reason.
- [x] Run and retain real behavioral RED with exact command, source-state hashes, log and terminal TRX before production edits.

### Task2: Minimal diagnostic implementation

**Files:** `src/RetroDownfall.Arcanum.Api/Intelligence/SemanticSpellRouter.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Weave/SpellWeaveCache.cs`; constructor adaptations in router/Wizard/Fallback tests only; `docs/Arcanum.DESIGN.md`21.10.

- [x] Capture IOptionsMonitor.CurrentValue once before awaits, use it for policy and epoch identity, and emit one fixed safe Warning per closed reason through a bounded atomic weak-key state.
- [x] Sanitize only the cache's existing Debug failure/exception diagnostics; preserve count-contract distinction with closed catalog failure vs refresh failure reasons.
- [x] Adapt the three original constructor-test sites to TestOptionsMonitor and document warning semantics, normal no-match controls, safe remedy and unchanged fallback behavior.
- [ ] Run complete router/cache classes and relevant Wizard/DI/boundary consumers with normal analyzers.

### Task3: Qualification and independent handoff

- [ ] Check owned house styles and Roslyn formatting.
- [x] Kill distinct meaningful behavior mutants with exact restoration; compile-only probes receive no behavioral credit.
- [ ] Create tracked implementation report, freeze clean commit/tree/source hashes, then normal zero-warning build and complete affected-class/guard qualification with terminal TRX and full census.
- [ ] Seal full-context diff, committed source archive, terminal evidence, exact commands and hashes; independent review required before any integrated credit.

Final-stage boxes remain pending at source freeze; the immutable terminal report records actual final outcomes without changing qualified bytes.

Tracked implementation report: `docs/superpowers/reports/2026-10-26-semantic-routing-diagnostics.md`. Final qualification remains recorded in the immutable exact-candidate terminal package.
