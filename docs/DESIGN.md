# Arcanum — Design Document

This document captures the **architecture, design decisions, and tradeoffs** for the Retro Downfall **Arcanum** solution. The intended audience is **senior C# / .NET engineers** who will extend, review, or operate the system.

**Keeping this document accurate:** When any change under `src/` alters architecture, observable behavior, or names described here, update the relevant sections in the same change set. Pair operator-visible behavior changes with `README.md` updates.

---

## 1. Purpose and scope

**Arcanum** is a **single deployable CLI** that can:

1. Run **terminal-oriented commands** — currently `ask` (single-prompt LLM inference with optional Grimoire thread continuation), `chat` (interactive multi-turn REPL), `look` (workspace perception), `lore` (key-value CRUD), and `daemon` (OS-level background service lifecycle).
2. Act as a **long-running HTTP host** exposing a Minimal API surface (the `serve` command).

The codebase is organized as a **multi-project solution**: `Core` (domain primitives, contracts, configuration), `Infrastructure` (Serilog, Data Protection, encrypted Grimoire via EF Core + SQLCipher, workspace scanning, Eye of the World perception, MCP client layer with both subprocess and in-process transports), `Api` (HTTP surface, Ollama-backed intelligence provider, semantic spell routing, API-key security), and `Cli` (Spectre.Console.Cli entry point). All projects target **Native AOT readiness** where the toolchain allows.

Key subsystems described in later sections: hybrid hosting model (§5), HTTP JSON design (§8), intelligence pipeline with MCP tool integration (§10), local API security (§11), and Eye of the World situational awareness (§15).

---

## 2. Architectural goals

| Goal | Rationale |
|------|-----------|
| **Strict project boundaries** | Keeps compile-time dependencies honest, enables parallel ownership, and avoids the "everything references everything" failure mode. |
| **Hybrid process model** | One binary reduces deployment and versioning surface; operators choose mode via CLI verbs. |
| **Native AOT readiness for the host** | Eliminates the .NET runtime prerequisite so the CLI ships as a self-contained native binary. Secondary benefits: predictable startup and a smaller attack surface from reflection-heavy stacks — balanced against ecosystem limitations (§9). |
| **Minimal API over MVC** | Fewer moving parts, explicit endpoint mapping, and alignment with ASP.NET Core's AOT-oriented request pipeline. |
| **Source-generated JSON and request delegates** | Required for credible trimming and Native AOT compatibility; avoids runtime reflection. |

---

## 3. Repository and solution layout

### 3.1 `src/` per project

Projects live under `src/` rather than the repository root for shorter CI paths, room for future top-level folders (`build/`, `docs/`, `test/`, `tools/`), and alignment with common monorepo conventions.

### 3.2 `Directory.Build.props`

Shared MSBuild properties: `TargetFramework` (`net10.0`), `Nullable` (`enable`), `ImplicitUsings` (`enable`), `LangVersion` (`latest`). Individual `.csproj` files focus on what differentiates each project.

### 3.3 Package versions

Package versions are tracked in `.csproj` files (the source of truth). All first-party `Microsoft.*` packages are pinned to **10.0.7**; `Microsoft.Extensions.AI*` to **10.5.2**. Upgrades should be deliberate — re-run `dotnet publish` with AOT analysis and verify zero warnings before committing.

### 3.4 Configuration reference (`ArcanumSettings`)

Operator-facing settings bind under the `Arcanum` JSON object in `arcanum.json` (see `README.md`). The config file lives alongside the Grimoire in `ArcanumPaths.GrimoireDirectory` (`~/.config/arcanum/` on macOS and Linux, `%USERPROFILE%\.config\arcanum\` on Windows). Environment variables use prefix `ARCANUM_` with nested `__` segments.

| Configuration path | Type | Default | Purpose |
|--------------------|------|---------|---------|
| `Arcanum:Host:Port` | `int` | `5001` | Kestrel listen port. |
| `Arcanum:Host:RetainedLogFileCount` | `int` | `7` | Serilog rolling file retention (days). |
| `Arcanum:Host:EnableEnterpriseTelemetry` | `bool` | `false` | When `true`, Serilog adds a console sink with `CompactJsonFormatter` (structured JSON for log ingestion). |
| `Arcanum:Security:MaxApiKeyHeaderUtf16Chars` | `int` | `512` | Rejects oversized API key headers before UTF-8 conversion. |
| `Arcanum:Ollama:Endpoint` | `string` | `http://localhost:11434` | Base URL for the Ollama HTTP API. |
| `Arcanum:Ollama:DefaultModel` | `string` | `llama3.2` | Model id when `PingRequest.model` is omitted. |
| `Arcanum:Ollama:ContextWindowLimit` | `int` | `8192` | Ollama `num_ctx` and `chat` Mana bar denominator. |
| `Arcanum:Bureau:Enabled` | `bool` | `false` | Placeholder for future Bureau integration. |
| `Arcanum:Daemon:Jobs` | array | `[]` | Unseen Servant background jobs (see `README.md`). Each entry: `name`, `intervalMinutes`, `targetSpell`, `enabled` (default `true`). Phase 2: runtime overrides via **`IUnseenServantPacer`** and MCP **`adjust_initiative`** (§5.5.2). |
| `Arcanum:Intelligence:ExecuteCommandTimeoutSeconds` | `int` | `30` | Hard timeout for MCP `execute_command` and `run_spell_script`. |
| `Arcanum:Intelligence:SemanticRouterPreflightTimeoutSeconds` | `int` | `15` | Max wait for spell-router preflight call. |
| `Arcanum:Intelligence:SemanticRouterMaxTokens` | `int` | `50` | Spell-router preflight `MaxOutputTokens`. |
| `Arcanum:Intelligence:SemanticRouterTemperature` | `float` | `0` | Spell-router preflight temperature. |
| `Arcanum:Intelligence:McpRequestTimeoutSeconds` | `int` | `60` | Default per-request timeout for `McpClient` JSON-RPC. |
| `Arcanum:Intelligence:McpMaxPaginationPages` | `int` | `32` | Max `tools/list` pagination iterations. |
| `Arcanum:Intelligence:ListDirectoryMaxPaths` | `int` | `500` | Max paths from in-process `list_directory`. |
| `Arcanum:Intelligence:EnableLoreSystem` | `bool` | `true` | Gates `read_lore`, `scribe_lore`, `delete_lore` MCP tools. |
| `Arcanum:Intelligence:EnableArchiveSearch` | `bool` | `true` | Gates `search_archives` MCP tool. |
| `Arcanum:Intelligence:ArchiveSearchMaxResults` | `int` | `5` | Max rows per `search_archives` call. |
| `Arcanum:Intelligence:ArchiveSearchMaxQueryLength` | `int` | `512` | Max query length before FTS sanitization. |
| `Arcanum:Intelligence:CampaignLogThreshold` | `int` | `25` | Message-count safety valve for Campaign Log consolidation. |
| `Arcanum:Intelligence:CampaignLogIdleTimeoutMinutes` | `int` | `240` | Idle minutes before a conversation is eligible for consolidation. |
| `Arcanum:Intelligence:CampaignLogSweepIntervalMinutes` | `int` | `15` | Background sweep interval for Campaign Log enqueue. |
| `Arcanum:Perception:MaxEnumerationSteps` | `int` | `50000` | File walk budget for Eye of the World. |
| `Arcanum:Perception:MaxTableOfContentsLines` | `int` | `20` | TOC line budget for `PatternSnapshot`. |
| `Arcanum:Cli:MaxAttachFileSizeBytes` | `long` | `1048576` | Per-file staging limit for `chat /attach`. |
| `Arcanum:Cli:MaxAttachedFilesPerRequest` | `int` | `32` | Max attached files per inference request. |
| `Arcanum:Cli:MaxAttachedFileRelativePathChars` | `int` | `4096` | Max `RelativePath` length per attachment. |
| `Arcanum:Cli:Theme` | `ArcanumTheme` | `SystemDefault` | CLI appearance: `Light`, `Dark`, or `SystemDefault` (uses `IThemeDetector` once at process start). |
| `Arcanum:Cli:ThemeColors` | object | Core defaults | Nested `Light` / `Dark`, each with `Text`, `Heading`, `Highlight`, `Error`, `Muted` as `#RRGGBB` strings (Spectre palette is built in **Cli**). |

All numeric settings have runtime clamps defined in `ArcanumSettingClamps`. When adding a property to `ArcanumSettings`, extend this table in the same change set.

---

## 4. Project model and dependency graph

**Dependency chain:** `Cli` → `Api` → `Infrastructure` → `Core`. `Cli` also references `Core` and `Infrastructure` directly for standalone DI setup (Data Protection, `ISecretStore`, `AddArcanumEyeOfTheWorld`).

### 4.1 `RetroDownfall.Arcanum.Core` (class library)

**Role:** Domain primitives, shared contracts, configuration, security abstractions, and cross-cutting types with **no** ASP.NET Core hosting dependency.

**Namespace areas:**

- **`Primitives/`** — `Error` (readonly record struct), `Result` / `Result<T>` (success/failure with implicit conversions), `ApiResponse<T>` (sealed record wire envelope).
- **`Configuration/`** — `ArcanumSettings` (root options), `DaemonSettings` / `UnseenServantJob`, `ConfigurationBootstrapper` (loads `arcanum.json` + `ARCANUM_` env vars).
- **`Security/`** — `ISecretStore` (API key read/write contract; concrete implementation in Infrastructure).
- **`Intelligence/`** — `IArcanumIntelligenceProvider` (`ExecutePromptAsync` / `StreamPromptAsync`), `PingRequest` (sealed record carrying `Prompt`, optional `StatelessMessages` as `List<CoreChatMessage>` for stateless multi-turn without Grimoire history, model, workspace path, context snapshot, conversation id, attached files, optional `ChronosyncDelta`, optional `OverrideSpellName` to load a specific spell without semantic routing, and behavioral flags), `CoreChatMessage`, `IntelligenceEvent` / `IntelligenceEventType`, `AttachedFileDto`.
- **`Storage/`** — `ArcanumPaths`, POCO entities (`Conversation`, `ChatMessage`, `MageSetting`, `WorkspaceContext`), `IGrimoireRepository`, `ICampaignLoggerQueue`.
- **`Chronosync/`** — `ChronosyncReport`, `IChronosyncEngine` (temporal workspace delta vs Grimoire baseline).
- **`Serialization/`** — `GrimoireJsonContext` (source-generated `PatternSnapshot` JSON for Grimoire columns; distinct from Api `ArcanumJsonContext`).
- **`Pattern/`** — `IEyeOfTheWorld`, `DomainType`, `PatternSnapshot`.
- **`Workspace/`** — `IWorkspaceScanner`.

**MSBuild:** `<IsAotCompatible>true</IsAotCompatible>`.

**Non-goals for Core:** Web types, DI registration extensions that pull in hosting, or HTTP-specific middleware.

### 4.2 `RetroDownfall.Arcanum.Infrastructure` (class library)

**Role:** OS-adjacent services — Serilog, Data Protection, encrypted Grimoire (EF Core 10 + SQLCipher, HKDF-derived passphrase, compiled model), workspace scanning, Eye of the World, and the **MCP client layer**.

**MCP architecture:** `IMcpTransport` is implemented by `McpProcessTransport` (subprocess stdio) and `InProcessMcpTransport` (newline-delimited JSON over `Channel<string>` pairs). `ArcanumInternalToolServer` runs on the in-process leg, handling `initialize`, `tools/list`, and `tools/call` with Native AOT-safe JSON schemas via `McpJsonSerializerContext`. `McpClient` manages JSON-RPC correlation. `McpBridgeTool` wraps `tools/call` as an `AIFunction`. `McpConnectionManager` (singleton) loads global `~/.config/arcanum/mcp.json`, starts per-partition in-process servers (including a no-workspace sentinel for `ask_human`), merges profile and optional workspace `mcp.json` servers, and returns deduped `McpBridgeTool` instances (local wins on duplicate names).

**In-process MCP tools:**

| Tool | Purpose |
|------|---------|
| `read_file_chunk` | Read a line range from a file under the workspace root. |
| `replace_text_block` | Replace a verbatim text block in a workspace file. |
| `write_file` | Create or overwrite a workspace file. |
| `list_directory` | List filesystem entries (recursive with skip rules; capped by `ListDirectoryMaxPaths`). |
| `execute_command` | Spawn a process without a shell (configurable timeout, `Kill(entireProcessTree: true)` on timeout). |
| `ask_human` | Prompt the operator for input (available even without a workspace). |
| `read_lore` / `scribe_lore` / `delete_lore` | Grimoire `MageSettings` key-value store (gated by `EnableLoreSystem`). |
| `search_archives` | FTS5 `MATCH` over `ChatMessage` rows (gated by `EnableArchiveSearch`). |

All file/directory tools require **relative paths** under the partition workspace root; rooted paths and escapes are rejected. Lore and archive tools resolve `IGrimoireRepository` via `IServiceScopeFactory` per call.

**Other key types:** `AddArcanumInfrastructure` (DI extension wiring all infrastructure services, including **`IUnseenServantPacer`** for Unseen Servant interval overrides), `AddArcanumDaemonServices` (`UnseenServantService` — §5.5), `AddArcanumEyeOfTheWorld` (narrow registration for perception only), `AddArcanumThemeDetection` (registers `IThemeDetector` → `ThemeDetector`: Windows `AppsUseLightTheme` registry read with `[UnconditionalSuppressMessage("AOT","IL3050")]`, macOS CoreFoundation `CFPreferencesCopyAppValue` for `AppleInterfaceStyle` with `IntPtr`/`CFRelease` string marshalling, Linux `GTK_THEME` / `COLORFGBG` heuristics, dark fallback on failure), `LoggingBootstrapper`, `DataProtectionSecretStore`, `ArcanumMasterKeyBootstrapper`, `GrimoireKeyDerivation`, `ArcanumDbContext` (compiled model), `GrimoireRepository`, `ChronosyncEngine`, `GrimoireDatabaseHostedService`, `CampaignLoggerQueue` / `CampaignLoggerBackgroundService`, `PhysicalWorkspaceScanner`, `EyeOfTheWorldService`, `CodexReader` (cascades global + local `CODEX.md`), `SpellScanner` (discovers `SPELL.md` files with YAML frontmatter, no YamlDotNet).

**MSBuild:** `IsTrimmable`, `PublishAot` (library signal for IL analysis), `EnableConfigurationBindingGenerator`.

**Non-goals for Infrastructure:** Minimal API route mapping, OpenAPI, or Ollama-specific code.

### 4.3 `RetroDownfall.Arcanum.Api` (class library, not executable)

**Role:** HTTP surface composition — endpoint mapping, JSON contracts, intelligence provider implementation, API-key filter, and bootstrap extensions callable from any host.

**Critical decision:** The Api project is a `Microsoft.NET.Sdk` class library with `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. This separates *composition* from *hosting*: the library describes routes and serialization; it does not own process lifetime.

**API surface (`MapArcanumEndpoints`):**

| Verb | Path | Purpose |
|------|------|---------|
| GET | `/api/health` | Health check. |
| POST | `/api/intelligence/ping` | Buffered inference. |
| POST | `/api/intelligence/ping-stream` | NDJSON streaming inference. |
| POST | `/api/intelligence/human-response` | Submit human-in-the-loop answer. |
| POST | `/api/intelligence/arsenal` | Spell + MCP server status. |
| POST | `/api/mcp/reload` | Reload MCP connections. |
| GET | `/api/conversations` | List recent conversations. |
| GET | `/api/conversations/{id}` | Conversation detail (with summary). |
| GET | `/api/conversations/{id}/messages` | Conversation messages. |
| DELETE | `/api/conversations/{id}` | Delete conversation. |
| POST | `/api/conversations/{id}/rest` | Enqueue for Campaign Log. |
| GET | `/api/lore` | List all lore entries. |
| GET | `/api/lore/{key}` | Get lore by key. |
| POST | `/api/lore` | Upsert lore entry. |
| DELETE | `/api/lore/{key}` | Delete lore entry. |
| GET | `/api/daemon/jobs` | List Unseen Servant jobs with base and effective polling intervals. |
| POST | `/api/daemon/jobs/{name}/initiative` | Set adaptive initiative (dynamic interval) for a job by name; returns updated status. |

All routes return `ApiResponse<T>` envelopes except `POST /v1/chat/completions`, which uses OpenAI-shaped JSON. The `/api` and `/v1` groups are protected by `ApiKeyEndpointFilter` (section 11), including the OpenAPI document and Scalar reference UI on `/api` (`MapOpenApi` / `MapScalarApiReference` are registered on the same keyed group, so browsers need a valid API key like any other `/api` caller).

**Key types:** `ApiBootstrapper` (`AddArcanumApiServices` / `MapArcanumEndpoints`), `OllamaIntelligenceProvider` (§10), `SemanticRouter` (§10.2.2), `ArcanumLocalTimeTool` / `ArcanumSpellScriptTool` (sealed `AIFunction` with static `JsonDocument` schemas), `ApiKeyEndpointFilter` (§11), `ArcanumJsonContext` (§8.2).

**MSBuild:** `IsAotCompatible`, `EnableRequestDelegateGenerator` (essential for Minimal API endpoints in a referenced class library), `EnableConfigurationBindingGenerator`.

### 4.4 `RetroDownfall.Arcanum.Cli` (console executable)

**Role:** Single entry assembly — process argv, dispatch commands, and when asked, construct the ASP.NET Core pipeline and run Kestrel.

**Commands:**

| Command | Purpose |
|---------|---------|
| `serve` | Builds `WebApplication` with slim defaults, configures Kestrel, registers API services, runs the host (§5.3). |
| `ask` | Single-prompt streaming inference via NDJSON. Resolves cwd, runs Eye of the World and Chronosync (scoped `IChronosyncEngine`), sends `PingRequest` with workspace context, `ChronosyncDelta`, and optional conversation continuation. |
| `chat` | Interactive multi-turn REPL with Mana bar, slash commands (`/exit`, `/clear`, `/help`, `/new`, `/model`, `/look`, `/tools`, `/mcp`, `/arsenal`, `/history`, `/resume`, `/delete`, `/rest`, `/log`, `/attach`), per-turn cancellation, inline `@` file staging, and swap-at-end Markdig rendering via `MarkdigSpectreRenderer`. |
| `look` | Prints `PatternSnapshot` from Eye of the World (no HTTP dependency). |
| `lore list\|get\|set\|delete` | CRUD on `MageSettings` via `/api/lore`. |
| `daemon install\|uninstall\|status` | OS-specific background service lifecycle (Windows `sc`, macOS `launchd`, Linux `systemctl --user`). |

**Key types:** `ArcanumApiClient` (wraps `IHttpClientFactory` + `ISecretStore`; handles NDJSON streaming, conversation management, lore, and MCP operations via `ArcanumJsonContext`), `CliSessionManager` (plain-text `cli-session.txt` for conversation id persistence), `IThemePalette` / `ConfiguredThemePalette` (Spectre colors from `Arcanum:Cli:ThemeColors`; `IThemePalette` extension methods for markup), `MarkdigSpectreRenderer` (AOT-safe AST walker — no reflection, no `Markdig.Renderers.*`), `CliTypeRegistrar` / `CliTypeResolver` (Spectre DI bridge).

**MSBuild:** `PublishAot` (the shipping native image), `<TrimmerRootAssembly Include="Spectre.Console.Cli" />`, `[DynamicDependency]` on all command types. The `IL3050` warning on `CommandApp` is suppressed.

### 4.5 `RetroDownfall.Arcanum.Api.DevHost` (console executable, debug-only)

Thin host for F5 debugging the HTTP stack without Spectre. References `Api`, `Core`, and `Infrastructure`; mirrors `ServeCommand` wiring. Not the production entrypoint; `PublishAot` is not enabled. On first run generates an API key and prints it to stdout.

---

## 5. Hybrid hosting model

### 5.1 Process roles

| Mode | Trigger | Behavior |
|------|---------|----------|
| **CLI / help** | No arguments | Spectre prints standard usage. |
| **HTTP host** | `serve` | Builds `WebApplication` with slim defaults, blocks until shutdown. |
| **Ask** | `ask <PROMPT>` | Streams single-prompt inference via NDJSON; exits 0/1/130. |
| **Chat** | `chat` | Multi-turn REPL with per-turn cancellation and swap-at-end rendering. |
| **Look** | `look` | Prints `PatternSnapshot` (no HTTP). |
| **Lore** | `lore list\|get\|set\|delete` | CRUD via `/api/lore`. |
| **Daemon** | `daemon install\|uninstall\|status` | OS-specific background service lifecycle. |

### 5.2 Why Spectre.Console.Cli

**Decision:** Use Spectre.Console.Cli for command parsing and dispatch.

**Reasons:** Mature command model, consistent help, straightforward verb registration. Keeps `Program.cs` thin.

**Tradeoff:** Spectre is reflection-heavy and carries trim/AOT warnings. Mitigated with `[UnconditionalSuppressMessage]`, `[DynamicDependency]`, and `<TrimmerRootAssembly>`. Version pinned to **0.55.0**.

### 5.3 `ServeCommand` lifecycle

1. Cancellation token check from Spectre.
2. `WebApplication.CreateSlimBuilder()` (§6).
3. `UseWindowsService` / `UseSystemd` (cross-platform no-ops on other OSes).
4. Kestrel: `ListenLocalhost(port)` unless `ARCANUM_HOST_ANY` is set (§7).
5. `ClearProviders()` so Serilog replaces default logging.
6. `AddArcanumConfiguration()` loads `arcanum.json` + env vars.
7. `AddArcanumApiServices(configuration)` registers all services (§8.3), including `AddArcanumDaemonServices` for the Unseen Servant (§5.5).
8. `ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync` **before** `Build()`.
9. `Build()` → `MapArcanumEndpoints()` → `RunAsync()`. `Log.CloseAndFlush()` in `finally`.

### 5.4 Grimoire persistence (Infrastructure + Api)

**Role:** Local-first conversation history in an SQLCipher-encrypted SQLite file under `~/.config/arcanum/`.

**Composition:**

- **`GrimoireDatabaseHostedService`** — initializes SQLCipher, derives DB passphrase from the master API key via HKDF, runs `MigrateAsync` on first use, `FailFast` on key mismatch.
- **`CampaignLoggerQueue` / `CampaignLoggerBackgroundService`** — bounded `Channel<Guid>` plus a background service that runs hybrid sweeps (message-count threshold + idle timeout) and processes queue entries by advancing `LastSummarizedMessageAt`. Operators may also enqueue via `POST /api/conversations/{id}/rest`.
- **`ArcanumDbContext`** — compiled model; SQLCipher passphrase from hosted service.
- **`GrimoireRepository`** — implements `IGrimoireRepository` (17 methods; the interface is the authoritative reference).
- **`ChronosyncEngine`** — implements `IChronosyncEngine`: compares the current `PatternSnapshot` to the latest `WorkspaceContext` row for that path, persists a new baseline row, and returns a `ChronosyncReport` (headless; no HTTP or Spectre in Phase 1).

#### 5.4.1 Grimoire data model

| Entity | Table | Primary key | Notable |
|--------|-------|-------------|---------|
| `Conversation` | `Conversations` | `Id` (Guid) | `Title`, nullable `Summary`, nullable `LastSummarizedMessageAt`; index on `CreatedAt`; cascade-deletes messages. |
| `ChatMessage` | `ChatMessages` | `Id` (Guid) | FK to `Conversation`; composite index on `(ConversationId, Timestamp)`; `Role` (enum → int); FTS5 virtual table + triggers for `search_archives`. |
| `MageSetting` | `MageSettings` | `Key` (string) | `Value`, `UpdatedAt`; consumed by Lore tools. |
| `WorkspaceContext` | `WorkspaceContexts` | `Id` (Guid) | `CreatedAt` (`DateTimeOffset`), `WorkspacePath` (mapped column `RootPath`, max 4096), `SerializedSnapshot` (JSON `PatternSnapshot` via `GrimoireJsonContext`). **Chronosync reporting** appends a row after each analysis; “latest” for a path is `ORDER BY CreatedAt DESC`. Composite index on `(RootPath, CreatedAt)`. |

**Supporting DTOs (Core):** `ConversationSummaryDto`, `ConversationDetailDto`, `ConversationMessageDto`, `LoreDto`, `UpsertLoreRequest`, `ChronosyncReport`, `ArcanumPaths`.

#### 5.4.2 Temporal context: Session-Based Consolidation and Chronosync

Arcanum’s **Session-Based Consolidation model of AI memory** spans two layers: **conversation** consolidation (Campaign Logger — §8.7) advances `LastSummarizedMessageAt` and future `Conversation.Summary` work, while **Chronosync reporting** supplies **temporal workspace** context — what changed on disk while the operator was away. `IChronosyncEngine` compares the live Eye-of-the-World `PatternSnapshot` to the last Grimoire-stored snapshot for the same `RootPath` and emits a **`ChronosyncReport`** (`PreviousSnapshotTime`, `NewThreads`, `MissingThreads`, `DomainChanged`, `PreviousDomain`) for downstream session consolidation (for example model memory prompts in a later phase). It is orthogonal to Campaign Logger thresholds; both contribute to the same mental model of “what the AI should know without re-reading the tree.”

#### 5.4.3 Design-time factory (`ArcanumDbContextFactory`)

`IDesignTimeDbContextFactory<ArcanumDbContext>` for `dotnet ef` tooling — uses `ARCANUM_GRIMOIRE_DEV_KEY` (fallback placeholder), a temp-directory database, and a no-op `ISecretStore`.

### 5.5 Unseen Servant

The **Unseen Servant** is a proactive background scheduler for headless inference when the HTTP host is running (`serve` or `Api.DevHost`). `AddArcanumDaemonServices` registers **`UnseenServantService`**, an ASP.NET Core **`BackgroundService`** in Infrastructure.

#### 5.5.1 Phase 1 — schedule and execution

**Schedule:** A **`PeriodicTimer`** ticks every **one minute**. For each configured **`UnseenServantJob`** under `Arcanum:Daemon:Jobs`, the service checks the effective interval in minutes (see §5.5.2; clamped via **`ArcanumSettingClamps.UnseenServantIntervalMinutes`**) against an in-memory **`ConcurrentDictionary`** of last completion times. Jobs are **not** persisted across process restarts: on cold start, every enabled job is treated as due on the **first** tick after startup (no watermark on disk).

**Execution:** Due jobs are dispatched with **`Task.Run`** so long inference does not block the timer loop. A per-key **`_runningJobs`** guard prevents overlapping runs for the same job. Each run creates a **new DI scope** (`IServiceScopeFactory.CreateAsyncScope`), resolves **`IArcanumIntelligenceProvider`**, and calls **`ExecutePromptAsync`** with a kickoff prompt that includes the current effective polling interval, **`UnattendedMode: true`**, **`OverrideSpellName`** set from `targetSpell`, and **`WorkingDirectory`** empty so **`SpellScanner`** discovers global spells under `~/.config/arcanum/spells/`. The host **`stoppingToken`** is passed through to **`ExecutePromptAsync`** so shutdown cancels in-flight work. A **`finally`** block always records **`lastRun`** and clears the running guard so a failing job (for example Ollama unreachable) does not tight-loop every minute.

**Shutdown:** Dispatched `Task.Run` jobs are not tracked in a registry or awaited during `StopAsync`. When the host shuts down, the main timer loop exits and the `stoppingToken` cooperatively cancels in-flight `ExecutePromptAsync` calls. Spawned tasks may run briefly after the loop exits; `RunJobAsync` has comprehensive `catch`/`finally` handling so unobserved exceptions do not leak. This is an intentional simplification — a full task-tracking registry is not justified given the cooperative cancellation path.

**Spell selection:** When **`PingRequest.OverrideSpellName`** is set, **`OllamaIntelligenceProvider`** resolves the spell by frontmatter **`name`** or parent folder name (same convention as spell discovery) and **skips** the **`SemanticRouter`** preflight; otherwise routing behaves as before.

#### 5.5.2 Phase 2 — Adaptive initiative (dynamic polling)

**`IUnseenServantPacer`** (singleton, registered in **`AddArcanumInfrastructure`**) holds process-local interval overrides in a **`ConcurrentDictionary<string, int>`** (`StringComparer.Ordinal`). **`SetDynamicInterval(jobName, intervalMinutes)`** trims `jobName`, clamps **`intervalMinutes`** with **`ArcanumSettingClamps.UnseenServantIntervalMinutes`**, and stores the result under the trimmed job name. **`GetEffectiveInterval(job)`** returns a clamped value: it prefers an override keyed by the composite **`$"{job.Name}\0{job.TargetSpell}"`** (aligned with the scheduler’s per-job tracking key), else an override keyed by trimmed **`job.Name`**, else **`job.IntervalMinutes`**. **`UnseenServantService`** applies the clamp again when computing the wait so scheduling and prompts stay consistent.

**MCP:** The in-process server (**`ArcanumInternalToolServer`**) exposes **`adjust_initiative`** (`job_name`, `interval_minutes`). The server receives **`IUnseenServantPacer`** at construction (singleton, threaded through **`InProcessMcpTransport.CreatePair`** and **`McpConnectionManager`**) and calls **`SetDynamicInterval`** synchronously. Tool arguments deserialize through **`McpJsonSerializerContext`** (**`AdjustInitiativeArgs`**) for Native AOT safety.

**HTTP (Phase 3):** External clients use the same pacer via **`GET /api/daemon/jobs`** (returns **`UnseenServantJobStatusDto[]`**) and **`POST /api/daemon/jobs/{name}/initiative`** with body **`AdjustInitiativeRequestDto`**; responses use **`ArcanumJsonContext`** and the usual **`ApiKeyEndpointFilter`** on `/api`.

---

## 6. `WebApplication.CreateSlimBuilder` vs `CreateBuilder`

**Decision:** Use `CreateSlimBuilder` for the `serve` command.

- Smaller default service graph — fewer registered defaults for trimming/AOT to analyze.
- Explicit opt-in for features that full `CreateBuilder` wires by default.
- When the product grows (e.g. SignalR), services must be consciously added.

---

## 7. Kestrel URL binding

Default: **loopback only, port from `Arcanum:Host:Port`** (default 5001). `ARCANUM_HOST_ANY=1` switches to `ListenAnyIP` for container publish. `Api.DevHost` always uses `ListenLocalhost`.

---

## 8. HTTP JSON and Minimal API design (`Api` project)

### 8.1 Wire contract: the `ApiResponse<T>` envelope

```csharp
public sealed record ApiResponse<T>(T? Data, bool IsSuccess, Error? Error, string? TraceId = null);
```

- One envelope shape for the whole API. `sealed record` for value equality and immutability.
- `Error?` is literal `null` on success. `TraceId` from `Activity.Current?.Id ?? HttpContext.TraceIdentifier`.
- `ApiResponse<T>.FromResult` is the single mapping point from `Result<T>` to wire envelope.

### 8.2 `ArcanumJsonContext` — source-generated, public

`ArcanumJsonContext` is the source-generated `JsonSerializerContext` with `CamelCase` naming for all HTTP wire types. It is registered at index 0 of `TypeInfoResolverChain` so Minimal API responses use source-generated `JsonTypeInfo`.

**Rule:** Every wire payload type `T` used in an `ApiResponse<T>` must have a `[JsonSerializable]` registration on this context. When adding a new endpoint with a new payload type, extend the context in the same change set.

**Grimoire blobs:** `WorkspaceContext.SerializedSnapshot` is **not** serialized through this class. Core defines **`GrimoireJsonContext`** (`RetroDownfall.Arcanum.Core.Serialization`) with the same CamelCase options for `PatternSnapshot` + `DomainType` so Infrastructure (`ChronosyncEngine`) stays AOT-safe without referencing the Api assembly.

**MCP JSON-RPC:** `McpJsonSerializerContext` (Infrastructure) is a separate context for JSON-RPC 2.0 over stdio/in-process channels. It uses explicit `[JsonPropertyName]` for spec-correct member names. `McpConfigJsonSerializerContext` handles `mcp.json` deserialization. Neither is registered on `HttpJsonOptions`.

### 8.3 Service registration in `AddArcanumApiServices`

`ApiBootstrapper.AddArcanumApiServices(IServiceCollection, IConfiguration)` registers:

- `AddArcanumInfrastructure` (Serilog, options, Data Protection, secrets, Grimoire, workspace, Eye of the World, Chronosync engine, MCP).
- `AddArcanumDaemonServices` (`UnseenServantService` hosted scheduler; §5.5).
- `ApiKeyEndpointFilter` (singleton).
- OpenAPI + JSON options (ArcanumJsonContext at head of resolver chain).
- Named `HttpClient("Ollama")` with `Timeout = InfiniteTimeSpan`.
- Scoped `OllamaApiClient` / `IOllamaApiClient` / `IChatClient` / `IArcanumIntelligenceProvider`.

### 8.4 Returning the envelope from a Minimal API handler

Successful endpoints use `Results.Ok(ApiResponse<T>.FromResult(result, traceId))`. Failable endpoints use `Results.Json` with the source-generated `JsonTypeInfo` and an explicit HTTP status code. No anonymous DTOs; no reflection-based model binding.

### 8.5 NDJSON streaming pipeline

`/api/intelligence/ping-stream` uses NDJSON (`application/x-ndjson`) for real-time token streaming:

- **Server:** Events serialized via `Utf8JsonWriter` + `ArcanumJsonContext`, newline-terminated, flushed per event. Linked `CancellationTokenSource` for connection abort.
- **Client (`ArcanumApiClient`):** Reads UTF-8 lines, deserializes each with `ArcanumJsonContext.Default.IntelligenceEvent`. Malformed frames yield a fabricated error event and continue (single bad frame does not terminate the session). The terminal `result` event carries **total token usage** in `Data`, not assistant text — clients accumulate `token` frames for the answer body.

### 8.6 Request Delegate Generator

`<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>` on `Api` ensures Minimal API endpoints in a referenced class library are source-generated.

### 8.7 Session-Based Consolidation (Campaign Logger)

Three mechanisms trigger Campaign Log consolidation:

1. **Message-count threshold** (`CampaignLogThreshold`) — safety valve for unbounded growth.
2. **Idle timeout** (`CampaignLogIdleTimeoutMinutes`) — natural session boundary.
3. **Explicit rest** — `POST /api/conversations/{id}/rest`.

The consumer advances `LastSummarizedMessageAt` as a watermark; LLM-authored `Conversation.Summary` is a future phase.

Under the same **Session-Based Consolidation model of AI memory**, **Chronosync reporting** (§5.4.2) addresses **spatial** drift: thread lines and `DomainType` deltas vs the last persisted `PatternSnapshot`, not chat log length. Campaign Logger and Chronosync are separate triggers; Phase 2 folds `ChronosyncReport` into the system prompt via `PingRequest.ChronosyncDelta`; MCP context remains separate.

---

## 9. Native AOT and trimming

### 9.1 Why Native AOT

The deciding factor for Native AOT is **zero runtime prerequisite**: the published binary is fully self-contained and does not require the .NET runtime or SDK to be installed on the target machine. This is critical for Arcanum's distribution model — operators install one native executable, not an application **plus** a framework. Without AOT, every machine running Arcanum would need a compatible .NET 10 runtime, adding a setup step and a version-drift risk that undermines the "single deployable CLI" goal (§1).

Additional benefits:

- **Fast cold start.** No JIT compilation on first run; the binary is machine code from the outset. This matters for short-lived CLI commands (`ask`, `look`) where JIT warmup would dominate wall time.
- **Smaller deployment footprint.** Trimming removes unused framework code, producing a binary significantly smaller than a self-contained framework-dependent publish.
- **Reduced reflection surface.** Source-generated JSON (`JsonSerializerContext`), source-generated request delegates (RDG), and hand-authored `AIFunction` tools avoid reflection at runtime, which both satisfies the IL linker and narrows the code surface area.
- **Predictable memory profile.** No background JIT allocations or tiered-compilation transitions; memory behavior matches what profiling shows.

### 9.2 What is AOT-optimized today

- **`Cli` publish** (`<PublishAot>true</PublishAot>`) produces a native binary via ILCompiler over the full closure (`Cli` + `Api` + `Infrastructure` + `Core` + framework + third-party assemblies).
- **`Infrastructure`** additionally sets `PublishAot` / `IsTrimmable` as a library signal so the ILCompiler analyzes it in the publish graph — it is not shipped as its own binary.
- **`Api` / `Core`** declare `<IsAotCompatible>true</IsAotCompatible>` to opt into AOT-oriented analyzers. Libraries in the closure should remain AOT-compatible to avoid blocking future hosts.

### 9.3 Tradeoffs and constraints

- **Spectre.Console.Cli** is reflection-heavy. Mitigated with `[UnconditionalSuppressMessage]`, `[DynamicDependency]` on all command types, and `<TrimmerRootAssembly Include="Spectre.Console.Cli" />`. If future Spectre versions break under AOT, fallback options include a source-generated CLI parser or splitting into two executables.
- **EF Core** compiled model is required (`dotnet ef dbcontext optimize`). Precompiled queries are disabled (`EFPrecompileQueriesStage = none`) because certain repository LINQ patterns are not yet compatible.
- **`dotnet build`** is warning-clean in Debug and Release. **`dotnet publish`** on macOS may show clang `.pcm` notices (toolchain noise, not IL diagnostics).

### 9.4 AOT discipline for new code

- Every HTTP payload type needs a `[JsonSerializable]` registration on `ArcanumJsonContext`.
- Grimoire `PatternSnapshot` blobs use `GrimoireJsonContext` with explicit `JsonTypeInfo` — no reflection-based `JsonSerializer` overloads for those columns.
- MCP wire types use `McpJsonSerializerContext` exclusively — no reflection-based `JsonSerializer` overloads.
- Minimal API handlers must not return anonymous DTOs or use unbounded reflection-based model binding.
- New `AIFunction` tools must use hand-authored `JsonDocument` schemas, not `AIFunctionFactory.Create`.

---

## 10. Intelligence pipeline

### 10.1 Architecture

The intelligence layer follows a **provider pattern**: `Core` defines `IArcanumIntelligenceProvider`, `Api` provides the Ollama implementation.

- **Ollama** runs locally, keeping inference off external APIs during development.
- **OllamaSharp** provides the native Ollama API surface.
- **Microsoft.Extensions.AI** provides `IChatClient` so swapping backends later requires only a new registration.

### 10.2 `OllamaIntelligenceProvider` design

**Model resolution:** `PingRequest.Model` when non-empty, otherwise `ArcanumSettings.Ollama.DefaultModel`. Case-insensitive matching handles Ollama's `:latest` tag convention via `ModelNameMatches`.

**Model availability:** `IsModelLocalAsync` checks local models; when missing, `EnsureModelExistsAsync` triggers on-demand pull with progress.

**Streaming:** `StreamPromptAsync` yields `IntelligenceEvent` objects — `status` (model checks, download progress), `conversationBound` (canonical conversation id), `token` (incremental text), `toolCall` / `toolResult` (tool execution diagnostics), `result` (total token usage as decimal string), `error`.

**Operator-safe errors:** Inference failures use fixed generic strings for clients and Grimoire; full exceptions are logged internally only.

### 10.2.1 Built-in tools and MCP workspace tools

Tool registration is built in `OllamaIntelligenceProvider` per inference attempt:

1. `ArcanumLocalTimeTool` — always registered.
2. `ArcanumSpellScriptTool` — registered when the active spell has `scripts/` files (even when `DisableMcpTools` is true).
3. MCP tools — merged from `McpConnectionManager.GetAvailableToolsAsync` unless `DisableMcpTools` is true.

The canonical tool list is in §4.2. `run_spell_script` runs with `UseShellExecute = false`, cwd fixed to the spell's `scripts/` directory, bare filename only (prefix containment), extension-based runner map, and the same timeout/kill-tree behavior as `execute_command`.

When `WorkingDirectory` is empty, filesystem tools return a workspace-not-configured error; `ask_human`, Lore, and `search_archives` still work.

### 10.2.2 Semantic spell routing (pre-flight → main loop)

**Problem:** Operators want versioned markdown "spells" (workflows, checklists, personas) without pasting them into `CODEX.md`. Only one spell should apply per prompt.

**Solution — two passes:**

1. **Discovery (`SpellScanner`):** Scans `~/.config/arcanum/spells/` then the workspace for `SPELL.md` files. Parses YAML frontmatter (`name:`, `description:`) without YamlDotNet. Workspace spells override global spells on name collision (case-insensitive). Sibling `scripts/` directory file names populate `AvailableScripts`.

2. **Pre-flight routing (`SemanticRouter`):** Single `IChatClient.GetResponseAsync` with low max output tokens, zero temperature, no tools, bounded timeout. Returns a spell name or `NONE`. Failures and timeouts resolve to no spell — main inference is unchanged.

3. **Main inference:** `SystemPromptBuilder` appends `### Active Operational Spell` with the spell's full markdown, plus `### Available Spell Scripts` when scripts exist.

### 10.3 Scoped registration

`OllamaApiClient`, `IOllamaApiClient`, `IChatClient`, and `IArcanumIntelligenceProvider` are **scoped** so concurrent requests do not share mutable state.

### 10.4 Grimoire integration

The provider persists through `IGrimoireRepository`. When `conversationId` is set, prior turns are loaded for `IChatClient`. A dynamic `ChatRole.System` message from `SystemPromptBuilder` is prepended in memory (not persisted to Grimoire). Tool rounds are persisted as bracket-formatted `ChatMessage` rows. Persistence failures on the buffered path are logged as warnings only.

### 10.5 Spatial context on inference

**Problem:** The API daemon's cwd is not the operator's shell cwd.

**Solution:** `PingRequest` carries `WorkingDirectory`, `ContextSnapshot` (`PatternSnapshot`), optional `ConversationId`, optional `StatelessMessages` (`CoreChatMessage` transcript for enterprise or OpenAI-compatible callers without Grimoire thread replay), optional `AttachedFiles`, and optional `ChronosyncDelta` (`ChronosyncReport`). The CLI resolves `Environment.CurrentDirectory`, runs Eye of the World, runs `IChronosyncEngine` inside a DI scope against the local Grimoire, and populates these fields before each HTTP call.

**CLI Grimoire bootstrap:** `ask` and `chat` call `IGrimoireCliInitialization` once per process so SQLCipher passphrase setup and first-run migrations match the API host (`GrimoireDatabaseBootstrapper`, shared with `GrimoireDatabaseHostedService`).

**`SystemPromptBuilder.Build` ordering:**

1. Base persona.
2. `### Workspace Context` / `### Table of Contents` (from `ContextSnapshot`).
3. `### Chronosync Report (Temporal Delta)` (from `PingRequest.ChronosyncDelta` when it carries a prior snapshot time and a non-empty diff — new threads, missing threads, or domain change; uses `PreviousDomain` and current snapshot domain when available; omitted when null, no prior baseline, or zero net change).
4. `### Master Codex (CODEX.md)` (global cascaded with optional local).
5. `### Active Operational Spell` (from `SemanticRouter`).
6. `### Available Spell Scripts` (when scripts exist).
7. `### Attached Files for this Turn` (ephemeral, not persisted to Grimoire).
8. `### Output Formatting Directive` (when `CliTerminalFormatting` is true — restricts model to headings, bold, italic, and code blocks for terminal rendering).

The same `WorkingDirectory` scopes `McpConnectionManager`, `CodexReader`, and `SpellScanner`.

---

## 11. Local API security

### 11.1 Threat model

Arcanum runs on **loopback only** for **single-user local development**. Even on localhost, every `/api` and `/v1` request must present a valid API key (zero-trust local). A client with the key can invoke `execute_command` — that is operator-equivalent power within the workspace tree.

### 11.2 API key lifecycle

1. `ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync` runs **before** `Build()`.
2. If no key exists, a cryptographically random 32-byte key is generated, Base64-encoded, and saved via `ISecretStore`.
3. Encrypted via ASP.NET Core Data Protection (`SetApplicationName("ArcanumCore")`, purpose `Arcanum.Core.ApiKey`) as `security.dat`.

### 11.3 Request authentication

`ApiKeyEndpointFilter` (singleton) accepts the API key from either header, in this order:

1. **`X-Arcanum-Key`** when present (legacy Arcanum header).
2. Otherwise `Authorization: Bearer` followed by the raw key (OpenAI-compatible clients). The `Bearer` prefix is case-insensitive; only the trimmed token after the first space is compared.

The filter then:

1. Rejects values exceeding `MaxApiKeyHeaderUtf16Chars` with 401.
2. Caches the decrypted key's UTF-8 bytes after first successful load (no per-request I/O).
3. Compares with `CryptographicOperations.FixedTimeEquals` (timing-safe).
4. Uses `stackalloc` for keys <= 256 bytes (avoids heap allocation).

### 11.4 CORS (serve host)

`AddArcanumApiServices` registers a permissive CORS policy named **`AllowAll`** (`AllowAnyOrigin`, `AllowAnyHeader`, `AllowAnyMethod`). `UseArcanumCors` runs early in the pipeline (before custom exception middleware and endpoint mapping) so browser-based tools can call the API without preflight failures.

### 11.5 OpenAPI and Scalar

`MapOpenApi` and `MapScalarApiReference` are attached to the same `MapGroup("/api")` that applies `ApiKeyEndpointFilter`, so fetching **`openapi/v1.json`**, **`scalar`**, or **`scalar/v1`** requires a valid API key (same headers as section 11.3) alongside every other `/api` endpoint. The OpenAI-shaped **`POST /v1/chat/completions`** route lives under `MapGroup("/v1")` with the same filter but is not included in that OpenAPI document.

---

## 12. C# language and coding conventions

- **File-scoped namespaces** used consistently.
- **Primary constructor-style DTOs** — positional records for `Error`, `ApiResponse<T>`, `PingRequest`, `IntelligenceEvent`. No `[JsonPropertyName]` attributes; casing comes from `[JsonSourceGenerationOptions]`.
- **Primary constructors on services** for DI injection.
- **`IDisposable`** on infrastructure services with `SemaphoreSlim` or `ServiceProvider` ownership.
- **Blank line after each line of C# code** for visual breathing room.

---

## 13. Testing strategy (future)

The design supports host-level integration tests via `WebApplicationFactory`-style hosts referencing the `Api` assembly. No test projects exist yet.

---

## 14. Extension guidelines for future contributors

1. **New HTTP routes:** Add in `MapArcanumEndpoints`. Return `ApiResponse<T>` via `FromResult`. Extend `ArcanumJsonContext` for new payload types. Use `.WithName(...)` for OpenAPI.
2. **New domain operations:** Return `Result` / `Result<T>`; rely on implicit conversions.
3. **New CLI verbs:** Add `AsyncCommand` under `Cli/Commands`, register in `Program.Configure`, add `[DynamicDependency]`. Lightweight verbs should use `AddArcanumEyeOfTheWorld()` rather than `AddArcanumInfrastructure`.
4. **New intelligence providers:** Implement `IArcanumIntelligenceProvider` in `Api`. Follow the `OllamaIntelligenceProvider` pattern.
5. **Domain logic:** Place in `Core`; keep `Api` free of business orchestration.
6. **Breaking JSON contracts:** Treat all wire types as versioned contracts. Property casing is fixed at the context level.
7. **Situational perception:** Keep `Core.Pattern` free of filesystem references. Put implementations in `Infrastructure.Pattern`.

---

## 15. Eye of the World — situational awareness (`IEyeOfTheWorld`)

### 15.1 Problem and product intent

Operators and agents pay a **context tax** when dropped into an arbitrary directory. **Eye of the World** answers that with a single async call returning a `PatternSnapshot`: an inferred `DomainType` plus a bounded table of contents (`Threads`, typically 20 lines) of labeled, human- and LLM-readable signatures.

**Non-goal:** No deep parsers. Everything derived from paths, file names, extensions, and timestamps.

### 15.2 Contract (`Core.Pattern`)

| Type | Role |
|------|------|
| `DomainType` | `SoftwareEngineering`, `Administration`, `Research`, `Unknown`. |
| `PatternSnapshot` | `Domain`, `RootPath`, `Threads` (string[]). |
| `IEyeOfTheWorld` | `PerceivePatternAsync` — invalid/missing directories return `Unknown` (no throw). |

### 15.3 Enumeration and noise control

`EyeOfTheWorldService` offloads filesystem I/O to the thread pool. Traversal uses `Directory.EnumerateFiles` with `RecurseSubdirectories`, `IgnoreInaccessible`, and `AttributesToSkip = Hidden | System`. Segment-based ignores: `bin`, `obj`, `.git`, `node_modules`, `.vs`, `.nuget`, `packages`, `dist`, `build`. Hard cap on enumeration steps (`MaxEnumerationSteps`) prevents pathological trees. Cooperative cancellation at three levels (enumeration loop, TOC building, Unknown sorting).

### 15.4 Domain classification

1. **`SoftwareEngineering`** — any strong artifact (`.sln`, `.csproj`, `package.json`, `Dockerfile`, `go.mod`, `Cargo.toml`, etc.) or >= 25 developer source files.
2. **`Administration`** — >= 3 office-style files (`.pdf`, `.xlsx`, `.docx`, etc.) and >= prose counts.
3. **`Research`** — >= 4 `.md` / `.txt` files exceeding office counts.
4. **`Unknown`** — fallback.

### 15.5 Signature table of contents

Non-`Unknown` domains: merge buckets in priority order (solutions → projects → packages → Dockerfiles → manifests → documents → notes), ordered by relative path, deduped, take 20. Software domains backfill from near-root document/note buckets when under 20 lines.

`Unknown` domains: rank by `LastWriteTimeUtc` descending (secondary: `CreationTimeUtc`), emit as `File:` lines.

### 15.6 Dependency injection split

`AddArcanumEyeOfTheWorld()` registers `IEyeOfTheWorld` → `EyeOfTheWorldService` only (no Grimoire, no Serilog). Used by CLI `look` and `ask` paths. `AddArcanumInfrastructure` chains it.

### 15.7 Tradeoffs

- Heuristic misclassification is possible; tuning thresholds is the escape hatch.
- No content indexing — RAG belongs in future layers.
- TOC is deliberately small for context windows.

---

## 16. Known limitations and future work

### 16.1 Inference

- **Single user prompt per HTTP request.** Multi-turn is via `conversationId` + Grimoire history reload.
- **Single-model routing only.** No multi-model routing, fallback, or load balancing.
- **Models without tool support** are retried once without tools after detecting rejection.
- **Deferred:** Richer skill catalogs, human-in-the-loop approval gates before high-risk actions.

### 16.2 Persistence

- **EF Core migrations** versioned under `Data/Migrations/`. Legacy files without `__EFMigrationsHistory` need manual baseline (see README).
- **`BureauSettings.Enabled`** has no consumers.
- **`cli-session.txt`** stores one last conversation id — not multi-user, not cloud sync.

### 16.3 Security and identity

- No user identity, sessions, or OAuth. Loopback + API key only.
- API key rotation requires deleting `security.dat`, recreating Grimoire, and restarting.

### 16.4 Testing

- No test projects exist. The design supports `WebApplicationFactory`-style integration tests (§13).

### 16.5 CLI

- **Line-counter for swap is naive.** Multi-cell glyphs and ANSI escapes are not measured; the swap may erase extra rows or leave stray lines. The renderer never throws.
- **Status/tool diagnostics share the TTY.** Intermixed stderr/stdout lines can desynchronize the cursor count during tool-heavy turns.

---

## 17. Glossary

| Term | Meaning |
|------|---------|
| **RDG** | ASP.NET Core Request Delegate Generator for Minimal API route handlers. |
| **`JsonSerializerContext`** | System.Text.Json source generator context for AOT-safe serialization. |
| **`CreateSlimBuilder`** | ASP.NET Core API returning a `WebApplicationBuilder` with reduced defaults. |
| **`IsAotCompatible`** | MSBuild signal that a library is authored for AOT analysis. |
| **`PublishAot`** | Enables Native AOT publishing (on `Cli`) or IL analysis (on `Infrastructure`). |
| **NDJSON** | Newline-Delimited JSON for streaming `IntelligenceEvent`s. |
| **Data Protection** | ASP.NET Core encryption system used for the local API key at rest. |
| **Grimoire** | Encrypted local SQLite (EF Core + SQLCipher) for conversation persistence. |
| **`AddArcanumInfrastructure`** | DI extension registering all infrastructure services (Serilog, options, secrets, Grimoire, workspace, perception, MCP). |
| **`AddArcanumEyeOfTheWorld`** | Narrow DI extension: `IEyeOfTheWorld` only (no Grimoire or Serilog). |
| **Eye of the World** | Situational directory perception — `EyeOfTheWorldService` in Infrastructure (§15). |
| **`PatternSnapshot`** | `DomainType` + `RootPath` + `Threads` (bounded TOC lines). |
| **`IGrimoireRepository`** | Core contract for Grimoire CRUD — 17 methods covering conversations, messages, lore, archive search, and workspace snapshot rows (§5.4). |
| **`Chronosync reporting`** | Headless comparison of current `PatternSnapshot` vs latest `WorkspaceContext`; persists baseline and returns `ChronosyncReport` (§5.4.2). |
| **`ChronosyncReport`** | DTO: `PreviousSnapshotTime`, `NewThreads`, `MissingThreads`, `DomainChanged`, `PreviousDomain`. |
| **`IChronosyncEngine`** | `AnalyzeAndSyncAsync` — implemented by `ChronosyncEngine` in Infrastructure. |
| **`GrimoireJsonContext`** | Core source-generated JSON context for `PatternSnapshot` stored in `WorkspaceContext.SerializedSnapshot` (§8.2). |
| **`ArcanumDbContextFactory`** | Design-time EF factory using a temp DB (§5.4.3). |
| **`AddArcanumDaemonManagement`** | DI extension for OS-specific daemon lifecycle. |
| **MCP** | Model Context Protocol — tool servers via JSON-RPC over stdio or in-process channels (§4.2). |
| **`McpJsonSerializerContext`** | Source-generated context for JSON-RPC DTOs and MCP wire types. |
| **`McpConfigJsonSerializerContext`** | Source-generated context for `mcp.json` deserialization. |
| **`McpConnectionManager`** | Singleton managing global and per-partition MCP connections (§4.2). |
| **`ArcanumInternalToolServer`** | In-process MCP server with native tools (§4.2). |
| **`MarkdigSpectreRenderer`** | AOT-safe Markdown → Spectre `IRenderable` walker for `chat` swap-at-end rendering. |
| **`IThemeDetector`** | OS dark/light preference for `Arcanum:Cli:Theme` = `SystemDefault` (no Spectre in Infrastructure). |
| **`IThemePalette`** | Semantic Spectre colors for all CLI markup (`RetroDownfall.Arcanum.Cli.UX`). |
| **Output Formatting Directive** | System prompt block restricting model output to terminal-safe Markdown subset (§10.5). |

---

## 18. Document maintenance

Any PR that changes **architecture, contracts, configuration, persistence, MCP surfaces, or CLI commands** must update this document in the same change set. Treat `DESIGN.md` as mandatory alongside code; do not close work with only README or code-level changes.

---

*End of design document.*
