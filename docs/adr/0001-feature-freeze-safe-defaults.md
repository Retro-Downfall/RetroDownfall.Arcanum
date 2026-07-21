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
- Does not map A2A server routes; does not advertise Conclave/A2A client tools.
- Diagnostic MCP invoke returns 404.

### Product honesty

- Advertise OpenAI **Chat Completions compatibility subset**.
- `POST /v1/moderations` always returns `501 not_supported`. Remove `Arcanum:Moderations` (obsolete-key rejection).
- Guardrails streaming mode is an enum defaulting to `Buffered`; explicit `Passthrough` is honored with a warning.
- Default `MaxToolInferenceRounds` is 8 (`TurnLimitsDefaults`).
- OpenAI batches force `DisableAllTools` (zero tools).
- Claim accurately: default inference no longer exposes arbitrary host process execution — not that the API key is no longer operator-equivalent.

## Consequences

Forced Spells that ship `scripts/` and rely on `run_spell_script` will not execute those scripts under Local defaults. Dry-run cast remains unaffected. Operators needing shell tools must opt into Development + the startup escape hatch and accept Degraded health.

See also: `docs/PRIVATE-BETA-NOTES.md`.
