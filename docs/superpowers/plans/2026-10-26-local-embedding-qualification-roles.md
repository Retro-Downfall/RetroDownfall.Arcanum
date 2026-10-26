# Local embedding qualification model roles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans inline; no children. Root owns independent review and integration.

**Goal:** Make the existing Long Rest qualification accept legitimate shared Flint chat/default roles while preserving exact request-purpose, model and credential evidence.

**Architecture:** Reuse the existing isolated configuration, real fake-forwarder fixture and `LongRestQualificationRun.AssertServedBy` grader. Deduplicate equal chat/default tags and declare both as text-only. Shared tags cannot identify request purpose, so actual classified request kind and credential remain authoritative; distinct-tag noninterference remains enforced.

**Tech Stack:** Existing .NET 10/xUnit, ASP.NET fake loopback upstream/recording forwarder, source-generated configuration.

**Spec:** Parent-approved bounded assignment and `/private/tmp/arcanum-pdf-embedding-qualification-preflight.md`, Long Rest adaptation seam. Base `9a6807bda7fad2700d0a2a7a5533a20841183af3`.

## Global Constraints

- Preserve `codex/local-qualification-model-roles` at c2e935e3; use separate `codex/local-embedding-qualification-roles`.
- Offline contract/fake-proxy tests only; no live models, discovery, published harness, installs or children. Explicit user pause remains.
- No production gates/defaults, vector semantics, calibration thresholds or coverage changes. PDF stays outside this candidate.
- Preserve exact model/credential grading, required summary/adjudication traffic and distinct Fast/Default noninterference; do not merely remove a failing check.

## Review Focus

- Shared `flint:40b` roles: one config entry, both roles retained, reasoning-off metadata preserved for Long Rest.
- Distinct roles: two text-only entries and unchanged default-tag misrouting refusal.
- Real recorded requests: correct summary/adjudication/embedding tags and entry credentials required; no hand-invented traffic substitutes for forwarder tests.
- Shared model tags do not authorize an ordinary/generic chat request as Long Rest traffic.
- Unknown/swapped credentials and wrong tags still refuse; metadata requests cannot masquerade as required positive chat evidence.

### Task 1: Shared role configuration and request grading

**Files:** modify LongRestQualificationConfiguration.cs, LongRestQualificationRun.Evidence.cs; extend existing LocalOllamaLongRestQualificationContractTests and HarnessContractTests with a focused ModelRoles partial; owning DESIGN travels.

- [x] Add same-Flint config test proving Core validation already permits equal Default/Fast tags, one advertised text-only model and retained reasoning/tool facts. Run RED.
- [x] Expose the existing grader to the offline friend test assembly without changing behavior; use existing fake upstream/forwarder to record real summary/adjudication/embedding requests. Same-Flint correct traffic must have no findings; prove RED against the current unconditional default-name refusal.
- [x] Add distinct-tag controls and wrong model/credential/purpose cases. Implement the smallest role-sensitive correction and truthful deduplicated configuration; update stale capability expectations.
- [x] Complete affected offline classes, causal mutants with byte-exact restoration, normal solution build, owned C#/shell/diff checks and owning docs.
- [x] Commit explicit owned paths, freeze SHA/tree and run final offline gates against that candidate. Seal full-context diff, hashes/logs/TRX/report for root's independent review; no live qualification claim.

## Verification receipt

- Behavioral RED: 7 failures / 13 controls in the first 20 new cases; no fixture errors. The final extension has 31 new cases, including distinct-tag controls.
- Both complete Long Rest contract classes: 152 passed, no failures or skips.
- Four causal mutants killed: blanket default refusal (2 failures), missing distinct-default refusal (2), bypassed chat credential (8), duplicated shared entry (1). All seven owned paths restored byte for byte after each.
- Normal solution build: 0 warnings/errors. Eight explicit offline packaging classes: 409 passed, no failures/skips. Owned house/compact/Roslyn formatting and diff checks pass.
- Exact commit/tree, final source equality and immutable review package are recorded in `/private/tmp/arcanum-embedding-roles-report.md` and its sealed review package. No live model, embedding, discovery, installation or PDF qualification was run.
