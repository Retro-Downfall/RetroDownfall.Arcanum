# ADR 0001: Feature freeze and safe defaults

- Status: Accepted
- Date: 2026-07-21

## Context

Arcanum’s surface area shares one trust boundary. Correctness and security work must land before new capabilities. This decision locks safe defaults without rewriting orchestration.

## Decision

Until hardening exit gates pass:

- Do not land new A2A/Conclave/Simulacrum features, host-command MCP tools, ListenAny expansions, OpenAI surface expansions beyond honesty fixes, or dual-desktop parity work.
- Allowed: bugfixes, hardening, documentation that narrows advertised surface, tests.

### Edition

Runtime `Arcanum:Edition` / `ARCANUM_EDITION` resolves once to `Local` (default) or `Development`.

Local edition:

- Does not advertise or invoke `execute_command` / `run_spell_script` unless Development + `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1` (Degraded health).
- May advertise the in-process `search_workspace` and bound-session `apply_patch` reliability tools. `apply_patch` is an intrinsic Ward tool and provides a reversible per-call sequential transaction, not isolation or crash atomicity.
- Advertises `workspace_check` only when the current host is macOS with an active Seatbelt jail plus a trusted native `dotnet`, selected SDK/runtime, and trusted launch chain. Linux and Windows are unavailable. Closed argv does not make it low-risk: repository MSBuild tasks, generators, analyzers, and tests execute arbitrary code; source/package/SDK roots are read-only, network remains open, detached-descendant cleanup is best effort, and an operator Ward is always required while Wards are enabled.
- Does not map A2A server routes; does not advertise Conclave/A2A client tools.
- Diagnostic MCP invoke returns 404.

### Product honesty

- Advertise OpenAI **Chat Completions compatibility subset**.
- `POST /v1/moderations` always returns `501 not_supported`. Remove `Arcanum:Moderations` (obsolete-key rejection).
- Guardrails streaming mode is an enum defaulting to `Buffered`; explicit `Passthrough` is honored with a warning.
- Default `MaxToolInferenceRounds` is 8 (`TurnLimitsDefaults`).
- OpenAI batches force `DisableAllTools` (zero tools).
- Claim accurately: Local defaults remove the **arbitrary-command surface** (`execute_command` / `run_spell_script`), but an eligible macOS host may still expose the closed-profile `workspace_check`, which executes arbitrary workspace-authored code only after a Ward. Do not say Local performs no code execution, that fixed arguments make repositories trusted, or that the API key is no longer operator-equivalent.
- `search_workspace` is exact line-scoped literal/runtime-regex search over files, not Weave retrieval. `apply_patch` persists deterministic exact call/result receipts before a successful result reaches the model; ambiguous persistence retains recovery artifacts and fails the turn.

## Consequences

Forced Spells that ship `scripts/` and rely on `run_spell_script` will not execute those scripts under Local defaults. Dry-run cast remains unaffected. Operators needing arbitrary command selection must opt into Development + the startup escape hatch and accept Degraded health. `workspace_check` has no unsandboxed escape hatch and remains unavailable outside eligible macOS Seatbelt hosts; approving it accepts open-network and malicious detached-descendant residual risk.

The reliable editing loop changes no Grimoire schema. It uses existing `Entries` for mandatory `apply_patch` receipts and requires no migration or database reinstall.

See also: `docs/PRIVATE-BETA-NOTES.md`.
