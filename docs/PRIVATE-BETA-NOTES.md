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

- **macOS:** filesystem jail active via deprecated `/usr/bin/sandbox-exec`
- **Linux:** Landlock helper in-tree but **inactive** — command tools fail closed unless `Arcanum:Security:AllowUnsandboxedToolChildren=true`
- **Windows:** no filesystem jail (Job Objects only); Sanctum path-boundary may deny command tools
- Network isolation for tool children is **not provided**

Run `arcanum doctor` and inspect `GET /api/health` component `ToolChildSandbox`.

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

Accurate claim: default inference no longer exposes arbitrary host process execution. An API key still authorizes privileged file, network, and MCP operations.

## Inference accounting, idempotency, reasoning, and context budgets (Grimoire reinstall required)

New raw-SQL tables: `InferenceRuns`, `BillableOperations`, `BudgetReservations`, `CostAdjustments`, `IdempotencyClaims`.

**Stop/delete/reinstall the Grimoire database before running this build** — there is no user migration path, and the existing accounting install script changed in place to add `BillableOperations.ReasoningTokens`. Stop every Arcanum host/daemon and back up anything needed, then delete all three SQLite files before starting the host:

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
