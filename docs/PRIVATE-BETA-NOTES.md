# Private beta notes (Windows / Linux)

These notes ship with private-beta archives produced by:

- `scripts/packaging/linux/package-linux.sh`
- `scripts/packaging/windows/package-windows.ps1`
- `.github/workflows/private-beta-release.yml` (Windows + Linux, includes The Forge)
- `.github/workflows/build-windows-x64.yml` (Windows x64 Arcanum + Compendium only; `-SkipForge`)

macOS Apple Silicon release remains on the separate signed/notarized path (`docs/RELEASE-MACOS.md`).

## What’s in the archive

| Artifact | Kind |
|----------|------|
| `arcanum` / `arcanum.exe` | Native AOT CLI/host |
| `the-forge-*` folder | Self-contained Avalonia desktop app (**not** Native AOT) |
| `compendium-*` folder | Self-contained Avalonia config editor (**not** Native AOT) |
| `SHA256SUMS` | Checksums for the compressed archives |

Windows/Linux private-beta builds are **unsigned by default**. Windows SmartScreen may warn on first launch of unsigned binaries; that is expected for this beta channel. Optional Authenticode signing is available only when packaging with `-Sign` and `WINDOWS_CERT_*` credentials.

## Linux quick start

1. Extract: `tar -xzf arcanum-linux-x64.tar.gz`
2. Ensure the binary is executable: `chmod +x arcanum-linux-x64/arcanum`
3. First run: `./arcanum serve` (generates the master API key; use `arcanum key show` to recover it)
4. Config / Grimoire live under the Arcanum data directory (typically `~/.local/share/arcanum` / `~/.config/arcanum` depending on platform helpers — see `arcanum doctor`)
5. Launch The Forge / Compendium from their extracted folders (run the published app host binary)

If Secret Service / libsecret is unavailable, The Forge may prompt for an API key or accept the process-only env override `THEFORGE_ARCANUM_KEY` (never written to `the-forge.json`).

## Windows quick start

1. Extract the `.zip` archives
2. Run `arcanum.exe serve` from an elevated-optional normal user shell
3. If SmartScreen blocks the unsigned binary, use “More info” → “Run anyway” for private-beta testing only
4. Recover the API key with `arcanum key show` or paste into The Forge when prompted
5. Launch The Forge / Compendium `.exe` from their extracted folders

## Tool-child sandbox (beta honesty)

- **macOS:** filesystem jail active via deprecated `/usr/bin/sandbox-exec`. `workspace_check` is advertised only when this Seatbelt jail plus trusted `dotnet`/SDK/launch-chain health checks pass.
- **Linux:** Landlock helper in-tree but **inactive** — command tools fail closed unless `Arcanum:Security:AllowUnsandboxedToolChildren=true`. `workspace_check` is unavailable regardless of that escape hatch.
- **Windows:** no filesystem jail (Job Objects only); Sanctum path-boundary may deny command tools. `workspace_check` is unavailable.
- Network isolation for tool children is **not provided**

Run `arcanum doctor` and inspect `GET /api/health` components `ToolChildSandbox` and `WorkspaceCheck`.

`workspace_check` executes workspace-authored MSBuild tasks, source generators, analyzers, and tests even though the model selects only a closed profile. On eligible macOS hosts its source, package cache, `dotnet`, SDK, and runtime roots are read-only and all ordinary output goes to owner-only per-run roots, but network egress remains open. Process-group/descendant cleanup is best effort; an intentionally malicious detached descendant may survive and continue exfiltrating readable source/package data. Ward approval is explicit acceptance of that residual risk. Do not approve an untrusted repository merely because the argv surface is fixed.

## Safe defaults (breaking for beta operators)

- **Edition:** default `Local` (`Arcanum:Edition` / `ARCANUM_EDITION`). Development unlocks gated surfaces.
- **Host process tools off:** `execute_command` and `run_spell_script` are not advertised or invoked unless `Edition=Development` **and** `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1`. When enabled, `GET /api/health` component `HostProcessTools` is **Degraded**.
- **Forced Spells with scripts:** scripts will not run under Local defaults (dry-run cast still works).
- **Batches are text-only:** OpenAI batch lines force zero tools and share live `/v1/chat/completions` request-shape validation; budget is reserved once per batch.
- **Moderation:** `POST /v1/moderations` always returns **501 `not_supported`**. Remove any `Arcanum:Moderations` block from `arcanum.json` or startup fails with an obsolete-key migration error.
- **Guardrails:** when enabled, streaming defaults to **buffered** (blocked assistant output is not streamed live). Explicit `passthrough` is still allowed with a warning.
- **Tool rounds:** default `MaxToolInferenceRounds` is **8** (was 100).
- **Compatibility claim:** OpenAI **Chat Completions compatibility subset** — not full parity; images/audio/moderation are unsupported.
- **A2A / Conclave / diagnostic MCP:** gated to Development edition.

Accurate claim: Local defaults remove arbitrary **command selection** (`execute_command` / `run_spell_script`). They do not prove that no code executes: an eligible macOS host may advertise Ward-gated `workspace_check`, which runs repository code under a closed profile. An API key still authorizes privileged file, network, and MCP operations.

## Reliable workspace editing loop

- `search_workspace` is available on supported hosts as strict-UTF-8, deterministic, line-scoped literal or bounded runtime-regex search. Patterns do not span lines; regex uses non-backtracking first and a bounded interpreted fallback, never dynamic compilation. It does not query The Weave.
- `apply_patch` is a bound-session intrinsic Ward tool. It parses the complete canonical unified diff, plans and fingerprints every file/hunk before mutation, then commits one call as a reversible **sequential, observable, non-isolated** transaction. It offers rollback and normalized relative recovery artifacts, not process-wide isolation or crash atomicity.
- The exact bounded patch result is persisted with deterministic assistant `ToolCall` then system `ToolResult` Entries before a successful result reaches the model. A persistence failure rolls back; an ambiguous result retains the applied patch/recovery artifacts and fails the turn. Multiple patch calls remain independent transactions.
- `workspace_check` enforces `--no-restore`, requires a pre-existing read-only NuGet package cache, seeds validated restore artifacts into per-run roots, and returns capped typed diagnostics plus stdout/stderr fallback. It is **not available in these Windows/Linux archives**.
- `WorkspacePathPolicy` containment and handle identity are always primary. Campaign Sanctum, when enabled, adds policy; it is not required for base containment.

The reliable editing loop adds no database table or column and requires **no Grimoire reinstall**.

## Existing inference-accounting upgrade (older Grimoire only)

New raw-SQL tables: `InferenceRuns`, `BillableOperations`, `BudgetReservations`, `CostAdjustments`, `IdempotencyClaims`.

This notice is unrelated to the reliable editing loop. If the developer database was created before the existing accounting install script gained `BillableOperations.ReasoningTokens`, there is no migration path: stop every Arcanum host/daemon, back up anything needed, then delete all three SQLite files before starting the host. Databases already created by the current script need no reinstall:

```bash
rm -f -- "$HOME/.config/arcanum/arcanum.db" "$HOME/.config/arcanum/arcanum.db-wal" "$HOME/.config/arcanum/arcanum.db-shm"
```

```powershell
Remove-Item -Force -ErrorAction SilentlyContinue `
  "$HOME\.config\arcanum\arcanum.db", `
  "$HOME\.config\arcanum\arcanum.db-wal", `
  "$HOME\.config\arcanum\arcanum.db-shm"
```

- Daily budget enforcement uses committed billable ops + outstanding reservations (session totals are projection only). Chat, embeddings, routing, and Lexicon extraction are ledgered; non-billable: `GET /models`, `POST /api/providers/test`, `POST /api/intelligence/mana`.
- Reasoning usage is a completion-token subset, can use an optional separate price, and is stored as a count only. Reasoning text remains ephemeral and answer-separated.
- `Idempotency-Key` uses claim-key ≠ fingerprint; fingerprint mismatch → **409**; only terminal completed responses replay.
- Tool results are token-budget truncated before returning to the model.
- Before each provider call, messages + tool schemas + reserved output are checked against the model context window; exhaustion → **429** `Hub.ContextBudgetExceeded` (or `Hub.TurnBudgetExceeded` for model-call ceilings). Compaction/delete paths keep ToolCall/ToolResult pairs intact.
- Disconnect policy default `Auto`: continue-then-replay when `Idempotency-Key` is present (claim can Complete for replay; accounting fully ledgered); otherwise cancel → Abandoned (unused reservation released; partial billed cost still ledgered). Override with `Arcanum:Intelligence:DisconnectPolicy`.

## RAG / The Weave

sqlite-vec is **not** shipped in this beta. When embeddings are enabled, search uses managed SIMD fallback (preview/performance-limited; 50,000 row scan budget). The Forge shows a non-blocking banner; `GET /api/meta` exposes typed `embeddingsVectorMode` (`disabled` | `managed` | `vec0` | `unavailable`).

## Local packaging

```bash
# On Linux host:
./scripts/packaging/linux/package-linux.sh --version 0.1.0-beta.1 --output-dir ./dist

# On Windows host (PowerShell):
.\scripts\packaging\windows\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist
# Arcanum + Compendium only (omit The Forge):
.\scripts\packaging\windows\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist -SkipForge
```

Cross-OS artifacts:

- Full private beta (Windows + Linux, includes The Forge): `.github/workflows/private-beta-release.yml` (`workflow_dispatch`)
- Windows x64 Arcanum + Compendium only: `.github/workflows/build-windows-x64.yml` (`workflow_dispatch`)
