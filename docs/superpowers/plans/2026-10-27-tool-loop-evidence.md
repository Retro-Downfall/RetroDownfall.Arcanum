# Tool-loop evidence implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans inline; no child agents.

**Goal:** Resolve RV116/RV135 with privacy-safe actionable loop diagnostics and RV117 with bounded evidence reacquisition after real context trimming.

**Architecture:** Preserve the8-round comparison ring. Exact ephemeral message witnesses report evidence loss; a retained fingerprint permits one reacquisition and retains its consumed state across repeated eviction. A coalesced content-free notice participates in actual token admission.

**Tech Stack:** .NET10, Microsoft.Extensions.AI, xUnit, real ModelTokenEstimator with controlled fake SDK/tool implementations.

**Spec:** docs/superpowers/specs/2026-10-27-tool-loop-evidence-design.md

## Global constraints

No total model-call/tool-round/retry/time ceiling; no ring reset; no renewed allowance on repeated identical eviction. Preserve protected no-name/no-signature semantics and turn-local HMAC. Never log raw tool arguments/results/private payloads. No production change before behavioral RED. No live/network/install calls, model/TSV/schema changes or authority grant.

## Review focus

- Model-controlled unknown tool names cannot become diagnostic labels.
- Reused external CallIds cannot create a false evidence witness.
- Partial parallel exchange removal must preserve protocol and exact round linkage.
- Same eviction/reacquisition cycle must terminate without a timeout or retry counter.
- Notice bytes, fixed estimator overhead and final ceiling rounding remain fully admitted.

### Task1: Establish actual diagnostics/trim regressions

Files: new WizardIntelligenceProviderTests.ToolLoopEvidence.cs; existing detector/context-guard/protected tests as passing controls.

- [x] Add shared buffered/streamed diagnostic assertions: typed stop identifies actual tool and next step, no private arguments/results.
- [x] Add actual real-estimator A/B/A sequence: one outbound payload containsA, later payload provesA was removed, unchanged code stops at3calls; repaired code must reacquire and finish at4.
- [x] Archive unchanged production/source-map RED with original immediate-repeat/oscillation/protected controls. Fixture setup/size mistakes are not RED credit.

### Task2: Implement exact bounded evidence state

Existing files: ToolLoopProgressDetector.cs, TurnContextGuards.cs, WizardIntelligenceProvider.cs (append owner only if necessary).

- [x] Add exact detector/witness controls and causal REDs for retained evidence, single loss allowance, repeated loss cycle, unrelated/history trim, reused CallIds, multi-call rounds and8-slot expiry.
- [x] Implement host-owned weak message association and data-only removed-message report. Keep dropped text unretained.
- [x] Implement the specified flags/inheritance and budgeted coalesced notice, then pass actualA/B/A and all controls.
- [ ] Prove real estimator notice/context refusal and existing slice-estimate/ceiling-rounding scaling; preserve cancellation/accounting/reservation/cache/retry boundaries.

### Task3: Safe actionable diagnostics

Existing detector/shared Wizard terminal; new hostile/protected tests.

- [x] RED ordinary names/count/nextstep, unknown-name injection, bounded configured labels, multi-tool ordering/omission, protected no-name/no-signature.
- [x] Implement only actual advertised identity diagnostics and fixed protected guidance, then pass controls.

### Task4: Qualification and seal

- [x] Causals: omit eviction notification; renew consumed allowance; drop notice accounting; leak arbitrary names/protected facts; restore all sources exactly after each.
- [ ] Run complete changed Wizard/detector/guards/protected/token/accounting/context owner classes and relevant strict source guards; normal0warning build and scoped house/compact/Roslyn checks.
- [ ] Update DESIGN/CHAT-LOOP, record exact source/member delta and honest gate limits, seal clean commit/source/raw evidence package for parent independent review. No original completion credit until integration.

Qualification history: genuine initial combined red-r4 gave4failures/7controls (actual model-aware A/B/A evidence loss in both projections); expanded pre-runtime RED8/7. Focused expanded GREEN41/41 with exact protected/weak-witness/budget controls. Eight final causal changes fail4/8,4/8,2/6,1/7,2/2,2/4,3/3,8/12 (failures/controls), with all6,439 source files restored after each. Complete owning/build/style qualification and independent review remain pending. Initial compile-only import, invalid context-window fixtures and no-control first causal selection are preserved separately without overstated credit.
