# AGENTS.md

> Full agent orientation lives in **[`README.md`](README.md)** — read it before making non-trivial changes. `docs/Arcanum.DESIGN.md` is the authoritative architecture/ persistence/testing reference; `docs/Arcanum.API.md` is the exact HTTP contract; `docs/Arcanum.Command.Reference.md` is the complete CLI surface. This file is a fast-start summary, not a replacement.

## What this is
**Arcanum** is a .NET 10, local-first AI assistant/inference hub. One `arcanum` binary is both the HTTP host (`arcanum serve`) and thin CLI clients (`run`, `watch`, `session`, …) over the same API. Windows/Linux ship Native AOT; macOS ships Native AOT too when LLVM `lld` is installed (`brew install lld`), and degrades to a signed folder-based self-contained publish when it is not.

## Project layout & dependency direction
`Cli → Api → Infrastructure → Core` (Infrastructure also references the isolated `Secrets` project).
- **`Core`** — domain primitives, `Result`/`Result<T>`, `ApiResponse<T>`, config POCOs, source-gen JSON contexts (`GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext`).
- **`Infrastructure`** — Grimoire (EF Core + SQLCipher), MCP client layer, Serilog, workspace tools. Schema lives as one `.sql` file per object under `Infrastructure/Data/Schema/**` — **edit the schema by adding/editing a file**, never a numbered migration (`Data/Migrations` is design-time scaffolding only, never applied).
- **`NativeSqlCipher`** — assets only: the hermetic SQLCipher library (built from pinned upstream sources with statically linked OpenSSL) for each shipping RID, plus `native-source-manifest.json`. Shipping RIDs are `osx-arm64`, `win-x64`, `win-arm64`; **there is no fallback** — a RID without a verified asset fails the build. Never add a `SQLitePCLRaw.bundle*` package, and never open a SQLite connection without `SqliteNativeRuntime.Instance.Initialize()` first.
- **`Api`** — class library (not an exe): `MapArcanumEndpoints`, `WizardIntelligenceProvider`, `TurnEngine`, `ToolExecutionPipeline`, `ArcanumJsonContext`, `/v1` OpenAI compat endpoints.
- **`Cli`** — the shipping executable; calls the running host's API rather than reaching into Infrastructure directly.
- **`Compendium.Ux` / `TheForge.Ux`** — Avalonia desktop apps (config editor / inference IDE), HTTP-only clients of the same API.

## Non-negotiable conventions (see README "standards" section for full rationale)
1. **Native AOT.** No reflection-based `JsonSerializer`, no `AIFunctionFactory.Create`, no anonymous DTOs. Every `/api` payload type gets a `[JsonSerializable]` entry on `ArcanumJsonContext`. Config POCOs under `Arcanum:…` must use `{ get; set; }`, **not `init`** (the config binding generator silently drops `init`-only members).
2. **API-first.** Business logic goes in `Core`; `Api` is composition/orchestration; `Cli` is thin HTTP calls via `ArcanumApiClient`. New behavior = new endpoint in `MapArcanumEndpoints` returning `ApiResponse<T>.FromResult`, registered on `ArcanumJsonContext`.
3. **`Result`/`Result<T>` flow** for domain ops; the endpoint is the one place that turns a `Result` into an envelope + status code.
4. **C# house style:** one blank line after each line of code (not around braces/control statements); file-scoped namespaces; positional records for DTOs; **no `[JsonPropertyName]`** on `/api` wire types (OpenAI `/v1` and MCP JSON-RPC types are the explicit exceptions); primary constructors for DI.
5. **Thematic (D&D) naming.** New domain concepts must fit the existing metaphor table in the README (`Campaign`, `Spell`, `Ward`, `Sanctum`, `Grimoire`, `Apprentice`, `The Weave`, …) unless they're genuinely universal terms like `Prompt`/`Workspace`. Propose new names before implementing.
6. **Docs travel with code.** Architecture/persistence/testing → `Arcanum.DESIGN.md`; API contracts → `Arcanum.API.md`; CLI surface → `Arcanum.Command.Reference.md`; config keys → `Compendium.README.md`; agent orientation → the root `README.md`. Update the owning doc in the same change set as the code.
7. **Strict CSP:** first-party browser UI externalizes all JS/CSS (no inline first-party `<script>`).

## Build, test, verify
```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
./scripts/coverage.sh --threshold        # Cobertura + HTML coverage, tiered gates
./scripts/verify-aot-il-warnings.sh      # AOT publish closure must be free of first-party IL/AOT warnings
./scripts/verify-native-sqlcipher.sh --rid osx-arm64   # native provenance, hashes, symbols, compile options
```
Run everything from the repo root. Don't rely on `workspace_check` as a bootstrap verifier for untrusted repos — it executes repo-authored code and requires explicit feature enablement, trusted workspace bytes, and an eligible macOS containment/runtime chain. Its Ward record is informational.

## Adding things — quick checklists
- **New endpoint:** add to `MapArcanumEndpoints` → return `ApiResponse<T>` (or a documented streaming shape) → register the payload type on `ArcanumJsonContext` → `.WithName(...)` → explicit `JsonTypeInfo` on any failable `Results.Json` → update `Arcanum.DESIGN.md` §4.3 and the README's API map.
- **New CLI verb:** handler under `Cli/Commands`, wired in `CliCommandTree`; use `IConsoleDispatcher` for stdout/stderr, `IConfirmationPrompt` for destructive ops, a defined `CliExitCode`. Prefer `AddArcanumEyeOfTheWorld()` over full infrastructure DI for lightweight verbs.
- **New inference provider:** add an `AiProviderKind`, extend `IChatClientFactory`; providers are OpenAI-compatible only (including Ollama via `/v1`) — no hard-coded model names, resolve via `ProviderResolver` + `Arcanum:Providers`.
- **New MCP tool:** implement on `ArcanumInternalToolServer` with a hand-authored JSON schema (`McpJsonSerializerContext`); honor `WorkspacePathPolicy` containment; treat `ToolOutputCapBytes` as one response/page allocation; decide its attunement, explicit `NoForbiddenArts` advertisement behavior, tool-specific authority, and Sanctum policy. Every server-executed call receives the same informational Ward audit pair.
- **Long-running work:** use `ILongRunningOperationCoordinator`; add exactly one descriptor to `LongRunningOperationRecoveryRegistry` **and** an idempotent registered recovery handler (contract tests enforce both); store only minimum encrypted checkpoint state — never a live Task/token/process/DI object.

## Exit codes & CLI automation contract
`0` success · `1` generic error · `2` invalid command line/config/confirmation · `3` network error · `130` cancellation. Every direct command supports `--json` (one JSON doc on stdout, diagnostics on stderr), `--plain`, `--yes`, `--no-context`.

## Local dev run
```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- <cmd>
```
