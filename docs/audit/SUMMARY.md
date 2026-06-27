# Arcanum Codebase Audit — Master Summary

> **Read-only audit.** This directory catalogs reliability, performance, correctness, security, AOT/trim, concurrency, resource-disposal, maintainability, test-coverage, and house-style findings across the entire Arcanum solution. **No source code is changed by this audit** — every entry is an observation plus a recommended fix, to be remediated in a later, separately-approved pass.

Audit performed base-module-up: **Core → Infrastructure → Api → Cli/DevHost → cross-cutting**.

## Reports

| Report | Scope |
|--------|-------|
| [00-core.md](00-core.md) | `RetroDownfall.Arcanum.Core` — primitives, configuration + clamps, path policies, source-gen JSON contexts, domain contracts, Proving Grounds |
| [01-infrastructure.md](01-infrastructure.md) | `RetroDownfall.Arcanum.Infrastructure` — Grimoire (EF Core + SQLCipher), MCP layer, llama-server, background services, Comm Link, security, caching |
| [02-api.md](02-api.md) | `RetroDownfall.Arcanum.Api` — inference pipeline, streaming writers, 122 endpoints, security filter, `/v1` OpenAI parity |
| [03-cli-devhost.md](03-cli-devhost.md) | `RetroDownfall.Arcanum.Cli` + `Api.DevHost` — API client, commands, rendering, DevHost wiring |
| [99-cross-cutting.md](99-cross-cutting.md) | Systemic sweeps (concurrency/disposal, AOT/serialization, security, docs accuracy) |

## Severity taxonomy

| Severity | Meaning |
|----------|---------|
| **P0** | Data loss, deadlock/hang, security hole, crash, or correctness bug with user-visible damage. Fix before anything else. |
| **P1** | A real reliability or performance bug that manifests under realistic load or failure conditions. |
| **P2** | Latent risk or measurable inefficiency that is not yet biting but should be corrected. |
| **P3** | Maintainability, dead code, naming, docs, or house-style. |

## Review dimensions

`reliability` · `concurrency` · `resource-safety` · `performance` · `correctness` · `security` · `aot/trim` · `maintainability` · `tests` · `docs/style`

## Finding template

```
### [Pn][dimension] <short title>
- **Location:** `path/to/File.cs:LINE`
- **Observation:** what the code does today.
- **Impact:** why it matters / when it bites.
- **Recommendation:** the concrete fix (not applied in this pass).
```

## Findings tally

| Report | P1 | P2 | P3 |
|--------|----|----|----|
| [00-core.md](00-core.md) | 1 | 11 | 11 |
| [01-infrastructure.md](01-infrastructure.md) | 13 | ≈41 | ≈21 |
| [02-api.md](02-api.md) | 1 | 16 | 10 |
| [03-cli-devhost.md](03-cli-devhost.md) | 1 | 7 | 9 |
| **Total** | **16** | **≈75** | **≈51** |

Overall the codebase is **well-engineered**: the AOT/source-gen posture is clean (no `AIFunctionFactory.Create`, no reflection JSON, no `async void`, no real sync-over-async), SSRF egress is DNS-pin-hardened, path containment is layered, and cancellation is wired end-to-end through inference. The findings are concentrated in **reliability, resource-growth, and consistency** — and many share a small number of root causes (see [99-cross-cutting.md](99-cross-cutting.md)). No P0 was confirmed (two candidate P0s — a llama cache-key traversal and the SpellScanner cycle — were verified down to "disproved" and "P1" respectively).

## Master P1 index (the critical list)

| # | Dimension | Finding | Report |
|---|-----------|---------|--------|
| 1 | reliability | `ConfigurationValidator` + outbound-URL validation never run at startup — semantically invalid `arcanum.json` boots and fails at runtime | [00](00-core.md) |
| 2 | performance | `GrimoireRepository.GetSessionAsync` loads the entire session entry set on every inference turn | [01](01-infrastructure.md) |
| 3 | correctness | `SessionRepository.AddEntryAsync` never maintains `UnsummarizedEntryCount` → summarization drift | [01](01-infrastructure.md) |
| 4 | reliability | `SpellScanner` directory BFS has no visited/depth/step cap → symlink-cycle hang | [01](01-infrastructure.md) |
| 5 | security | `SpellScanner` opens spell files without handle/symlink revalidation (containment escape) | [01](01-infrastructure.md) |
| 6 | performance | Spell read/search re-scan and fully re-parse the workspace on every request | [01](01-infrastructure.md) |
| 7 | reliability | Spell update writes `SPELL.md` non-atomically (crash → corruption) | [01](01-infrastructure.md) |
| 8 | correctness | Scan-time SKILL.json bounds use hardcoded defaults, ignoring configured `Spells:*` | [01](01-infrastructure.md) |
| 9 | performance | `PhysicalWorkspaceScanner` recurses the whole tree with no bound | [01](01-infrastructure.md) |
| 10 | security | `execute_command` spawns children with the full host environment (provider API keys) | [01](01-infrastructure.md) |
| 11 | reliability | Apprentice intervene+resume persists `Running` before acquiring an execution slot → stuck | [01](01-infrastructure.md) |
| 12 | reliability | Apprentice crash-recovery silently drops resumable apprentices when the queue is full | [01](01-infrastructure.md) |
| 13 | reliability | llama-server port arithmetic can exceed 65535 → spurious startup failures | [01](01-infrastructure.md) |
| 14 | resource-safety | Unexpected llama-server exit leaks the `Process` and its event handlers (crash-loop) | [01](01-infrastructure.md) |
| 15 | correctness | Read-time context compression can silently drop un-summarized middle messages | [02](02-api.md) |
| 16 | reliability | `ask`/`chat` `FailFast` (process crash) when no master API key is stored | [03](03-cli-devhost.md) |

## Recommended remediation ordering

Grouped to maximize leverage (several P1s collapse into a single systemic fix — see [99-cross-cutting.md](99-cross-cutting.md)):

**Wave 1 — correctness & highest leverage**
1. Run `ConfigurationValidator` + `OutboundUrlGuard.ValidateArcanumSettingsAsync` at startup (fixes **#1**, subsumes **#13** via a port-sum check, and catches CommLink/model/MCP misconfig). *(systemic S1)*
2. Anchor the session load window at the summary watermark in SQL (fixes **#15** data loss **and** **#2** hot-path perf together). *(systemic S2)*
3. Maintain `UnsummarizedEntryCount` on all write paths / consolidate the two session-write paths (**#3**, systemic S7).

**Wave 2 — reliability & security**
4. `SpellScanner`: add cycle/depth caps and handle/symlink revalidation; make spell writes atomic; cache scans; honor configured SKILL.json bounds; bound `PhysicalWorkspaceScanner` (**#4–#9**).
5. Scrub child-process environments for `execute_command` and global MCP (**#10**, systemic S8).
6. CLI: return a clean `Security.MissingApiKey` + exit 1 instead of `FailFast` (**#16**).
7. Apprentice engine: acquire slot before persisting `Running`; log/persist on recovery-queue-full; dispose the llama `Process` on unexpected exit (**#11, #12, #14**).

**Wave 3 — systemic hardening (P2)**
8. Introduce a self-evicting keyed-lock/cache utility for the unbounded `SemaphoreSlim`/state maps (S3).
9. Surface suppressed failures (log + typed result) instead of silent success/drop (S4).
10. Honor `IOptionsMonitor.OnChange` for capacity/LRU settings or document startup-only (S5).
11. Make soft-cap admission atomic (S6); make remaining durable writes atomic (S9); treat streaming write `IOException` as disconnect (S10).

**Wave 4 — polish (P3)**
12. Run the `scripts/align-csharp-blanklines.sh` formatter pass repo-wide (S11); fold doc-accuracy items into the Arcanum Documentation Audit; clear remaining P3s.

> All items above are **observations + recommendations only**. No source files were modified by this audit. Each fix should be made with its accompanying `docs/DESIGN.md`/`README.md` update per the repo's "docs travel with code" rule.
