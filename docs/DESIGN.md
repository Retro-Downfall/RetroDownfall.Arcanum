# Arcanum — Design Document

This document captures the **architecture, design decisions, and tradeoffs** for the Retro Downfall **Arcanum** solution. The intended audience is **senior C# / .NET engineers** who will extend, review, or operate the system.

**Keeping this document accurate:** When any change under `src/` alters architecture, observable behavior, or names described here, update the relevant sections in the same change set. Pair operator-visible behavior changes with `README.md` updates.

---

## 1. Purpose and scope

**Arcanum** is a **single deployable CLI** that can:

1. Run **terminal-oriented commands** — currently `ask` (single-prompt LLM inference with optional Grimoire thread continuation), `chat` (interactive multi-turn REPL), `look` (workspace perception), `lore` (key-value CRUD), and `daemon` (OS-level background service lifecycle plus **API-first** monitoring of Unseen Servant jobs via `daemon jobs`, `daemon initiative`, and Comm Link smoke tests via `daemon alert` when Kestrel is up).
2. Act as a **long-running HTTP host** exposing a Minimal API surface (the `serve` command).

The codebase is organized as a **multi-project solution**: `Core` (domain primitives, contracts, configuration), `Infrastructure` (Serilog, Data Protection, encrypted Grimoire via EF Core + SQLCipher, workspace scanning, Eye of the World perception, MCP client layer with both subprocess and in-process transports), `Api` (HTTP surface, multi-provider intelligence hub, semantic spell routing, API-key security), and `Cli` (Spectre.Console.Cli entry point). All projects target **Native AOT readiness** where the toolchain allows.

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

Shared MSBuild properties: `TargetFramework` (`net10.0`), `Nullable` (`enable`), `ImplicitUsings` (`enable`), `LangVersion` (`latest`). A solution-wide **`PackageReference`** to **`Microsoft.Bcl.Memory`** (currently **10.0.7**) overrides vulnerable transitive versions (mitigates **CVE-2026-26127**). Individual `.csproj` files focus on what differentiates each project.

### 3.3 Package versions

Most package versions are tracked in `.csproj` files (the per-project source of truth). **`Microsoft.Bcl.Memory`** is the exception: it is pinned once in **`Directory.Build.props`** so every project’s graph resolves to a patched build. All other first-party `Microsoft.*` packages in project files are pinned to **10.0.7**; `Microsoft.Extensions.AI` and **`Microsoft.Extensions.AI.OpenAI`** to **10.5.2**. Upgrades should be deliberate — re-run `dotnet publish` with AOT analysis and verify zero warnings before committing.

### 3.4 Configuration reference (`ArcanumSettings`)

Operator-facing settings bind under the `Arcanum` JSON object in `arcanum.json` (see `README.md`). The config file lives alongside the Grimoire in `ArcanumPaths.GrimoireDirectory` (`~/.config/arcanum/` on macOS and Linux, `%USERPROFILE%\.config\arcanum\` on Windows). Environment variables use prefix `ARCANUM_` with nested `__` segments.

| Configuration path | Type | Default | Purpose |
|--------------------|------|---------|---------|
| `Arcanum:Host:Port` | `int` | `5001` | Kestrel listen port. |
| `Arcanum:Host:RetainedLogFileCount` | `int` | `7` | Serilog rolling file retention (days). |
| `Arcanum:Host:EnableEnterpriseTelemetry` | `bool` | `false` | When `true`, Serilog adds a console sink with `CompactJsonFormatter` (structured JSON for log ingestion). |
| `Arcanum:Host:CorsAllowedOrigins` | `string[]` | localhost loopback (`5001`, `3000`) | Origins allowed by the **`ArcanumCors`** policy. Use `["*"]` to allow any origin (browser-callable; risk = key exfiltration on a compromised page). Empty array falls back to the localhost defaults. |
| `Arcanum:Host:EnableScalarUi` | `bool` | `false` | Mounts **`MapScalarApiReference`** under **`/api/scalar`**. The interactive HTML uses inline `<script>` / `<style>`; CSP header on the route restricts everything else (`default-src 'self'; frame-ancestors 'none'; base-uri 'none'`). Default is **off** to keep the published surface CSP-clean (`/api/openapi/v1.json` is always available). |
| `Arcanum:Host:SystemFingerprint` | `string?` | `null` | Optional override for the **`system_fingerprint`** field returned by `/v1/chat/completions`. When `null`, `OpenAiV1Endpoints` derives one from `AssemblyInformationalVersionAttribute` (for example `arcanum-0.1.0-beta`). Set explicitly to pin a fingerprint per deployment. |
| `Arcanum:Host:ListenAny` | `bool` | `false` | When `true`, Kestrel uses `ListenAnyIP` instead of `ListenLocalhost`. The environment variable `ARCANUM_HOST_ANY` always wins as an override so container deployments don't need rebuilds. |
| `Arcanum:Host:MaxRequestBodyBytes` | `long` | `10485760` (10 MiB) | Kestrel `MaxRequestBodySize`. Clamp 256 KiB – 1 GiB. |
| `Arcanum:Host:RateLimit:Enabled` | `bool` | `false` | When `true`, `AddArcanumApiServices` registers `AddRateLimiter` and `ServeCommand`/DevHost call `UseRateLimiter()`. Both the `/api` and `/v1` endpoint groups get `RequireRateLimiting("ArcanumRateLimit")`. |
| `Arcanum:Host:RateLimit:PermitLimit` | `int` | `120` | Requests permitted per partition per window. Clamp 1 – 1,000,000. |
| `Arcanum:Host:RateLimit:WindowSeconds` | `int` | `60` | Fixed window length (seconds). Clamp 1 – 86,400. |
| `Arcanum:Host:RateLimit:QueueLimit` | `int` | `0` | Maximum queued requests per partition. `0` rejects with HTTP 429 immediately; positive values serve queued requests when the window replenishes. Clamp 0 – 1,000,000. |
| `Arcanum:CommLink:WebhookTimeoutSeconds` | `int` | `15` | Timeout (seconds) configured on the named `HttpClient("CommLinkWebhook")`. Clamp 1 – 120. |
| `Arcanum:CommLink:AllowedSchemes` | `string[]` | `["https","http"]` | URI schemes the webhook dispatcher is allowed to call. Use `["https"]` to require TLS. Non-matching URLs are skipped with a warning (no HTTP call). |
| `Arcanum:Intelligence:MaxToolInferenceRounds` | `int` | `8` | Hard cap on agentic tool rounds per inference turn. Clamp 1 – 64. |
| `Arcanum:Intelligence:CompressionPreflightMinMessages` | `int` | `6` | Minimum assembled-message count before context-compression preflight runs (short threads skip tokenizer cost). Clamp 0 – 100. |
| `Arcanum:Intelligence:PerMessageTemplateOverheadTokens` | `int` | `4` | Per-message overhead (tokens) added to the pre-flight count to approximate chat-template framing. Clamp 0 – 32. |
| `Arcanum:Intelligence:TokenizerEncoding` | `string` | `"o200k_base"` | Tiktoken encoding name used by `InferenceTokenizerResolver`. Unknown names log a warning and fall back to `o200k_base`. |
| `Arcanum:Grimoire:MaxMessagesPerConversationLoad` | `int` | `1000` | Maximum messages loaded into memory by `GetConversationAsync` (most recent N, returned in chronological order). Clamp 50 – 100,000. Used to bound RAM on very long Grimoire threads. |
| `Arcanum:Security:MaxApiKeyHeaderUtf16Chars` | `int` | `512` | Rejects oversized API key headers before UTF-8 conversion. |
| `Arcanum:Security:ApiKeyCacheTtlSeconds` | `int` | `30` | TTL for the cached **SHA-256 digest** of the expected API key in `ApiKeyEndpointFilter`. After the TTL, the filter re-reads `ISecretStore` so on-disk rotation propagates without a restart. Clamp 1–3600. |
| `Arcanum:DefaultModel` | `string?` | `null` | When non-empty, must match a `models` entry on some provider (see `ProviderResolver`); used when `PingRequest.Model` is omitted. If null/empty, the first model of the first provider is used. |
| `Arcanum:FastModel` | `string?` | `null` | When non-empty, must match a `models` entry on some provider. **Campaign Logger** headless summarization passes this as `PingRequest.Model` when set; if null/empty, summarization falls back to `DefaultModel` then the first configured model (same resolution order as `ProviderResolver` for an explicit model). |
| `Arcanum:Providers` | array | `[]` | Multi-provider hub. Each element: `name`, `type` (`Ollama` or `OpenAICompatible`), `endpoint`, `apiKey` (optional; use `ARCANUM_` env vars for secrets), `models` (string[]), `contextWindowLimit` (int, default **8192**, clamped via `ArcanumSettingClamps.ContextWindowLimit`). `OpenAICompatible` targets OpenAI-shaped HTTP APIs (DeepSeek, Groq, GitHub Models, LM Studio, etc.). |
| `Arcanum:Bureau:Enabled` | `bool` | `false` | Placeholder for future Bureau integration. |
| `Arcanum:CommLink:WebhookUrl` | `string` | `null` | Optional absolute URL for **Comm Link** outbound JSON `POST` alerts (`WebhookCommLinkDispatcher`). When unset, dispatchers log and return success without HTTP. |
| `Arcanum:Daemon:Jobs` | array | `[]` | Unseen Servant background jobs (see `README.md`). Each entry: `name`, `intervalMinutes`, `targetSpell`, `enabled` (default `true`). Phase 2: runtime overrides via **`IUnseenServantPacer`** and MCP **`adjust_initiative`** (§5.5.2). Phase 4 (§5.5.3): when **`Arcanum:Intelligence:EnableLoreSystem`** is **`true`**, per-job lore key **`daemon_state_{job.Name}`** is pre-fetched and injected into the headless kickoff with **`scribe_lore`** instructions; when **`false`**, kickoff remains Phase 1 stateless (no lore read, no tool instructions). Phase 5 (§5.5.4): both kickoff variants instruct the model to call MCP **`use_commlink`** for high-alpha / critical operator alerts. |
| `Arcanum:Intelligence:ExecuteCommandTimeoutSeconds` | `int` | `30` | Hard wall-clock cap for MCP `execute_command` and `run_spell_script` (clamped 1–600s); cooperative cancel also terminates spawned process trees immediately, independent of this timeout. |
| `Arcanum:Intelligence:ToolOutputCapBytes` | `long` | `1048576` (1 MiB) | Combined byte cap on stdout + stderr captured from `execute_command` and `run_spell_script` (split evenly per stream). Streams are truncated with a `[truncated: …]` marker beyond the cap; clamp 64 KiB – 64 MiB. |
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
| `Arcanum:Intelligence:ContextWindowCompressionThreshold` | `int` | `85` | Percentage of the resolved provider `contextWindowLimit` at which **read-time** context compression is considered (clamped 50–100 via `ArcanumSettingClamps.ContextWindowCompressionThreshold`). Headroom for chat-template and tool-schema overhead not counted in the pre-flight pass. |
| `Arcanum:Intelligence:EnableContextCompression` | `bool` | `true` | When `true`, `HubIntelligenceProvider` runs pre-flight token counting and may swap older Grimoire messages for `Conversation.Summary` in the assembled system prompt without deleting rows. When `false`, compression is skipped. |
| `Arcanum:Intelligence:EnableTokenTracking` | `bool` | `true` | When `true`, after each successful buffered or streamed inference turn with a bound `ConversationId`, the hub calls **`IGrimoireRepository.IncrementConversationTokensAsync`** so **`Conversation.TotalTokensUsed`** reflects cumulative reported usage. Wire responses (NDJSON `result`, OpenAI `usage`) are still emitted when `false`; only Grimoire persistence is skipped. |
| `Arcanum:Perception:MaxEnumerationSteps` | `int` | `50000` | File walk budget for Eye of the World. |
| `Arcanum:Perception:MaxTableOfContentsLines` | `int` | `20` | TOC line budget for `PatternSnapshot`. |
| `Arcanum:Perception:AllowedWorkspaceRoots` | `string[]` | `[]` | Optional allowlist of absolute roots that `GET /api/perception/look` may scan. Empty (default) keeps historical behaviour (any readable directory; API key still required). When non-empty, `directory` must resolve under one of these roots — otherwise the endpoint returns **`403`** with `Error.Code = "Perception.PathNotAllowed"`. |
| `Arcanum:Daemon:MaxConcurrentJobs` | `int` | `8` | Hard concurrency cap on Unseen Servant jobs the scheduler dispatches per minute; excess jobs defer. Clamp 1–1024. |
| `Arcanum:Daemon:ShutdownDrainTimeoutSeconds` | `int` | `10` | Time (seconds) `StopAsync` waits for in-flight Unseen Servant jobs (`Task` registry) to drain after the host begins shutting down. `0` disables waiting. Clamp 0–600. |
| `Arcanum:Cli:MaxAttachFileSizeBytes` | `long` | `1048576` | Per-file staging limit for `chat /attach`. |
| `Arcanum:Cli:MaxAttachedFilesPerRequest` | `int` | `32` | Max attached files per inference request. |
| `Arcanum:Cli:MaxAttachedFileRelativePathChars` | `int` | `4096` | Max `RelativePath` length per attachment. |
| `Arcanum:Cli:Theme` | `ArcanumTheme` | `SystemDefault` | CLI appearance: `Light`, `Dark`, or `SystemDefault` (uses `IThemeDetector` once at process start). |
| `Arcanum:Cli:ThemeColors` | object | Core defaults | Nested `Light` / `Dark`, each with `Text`, `Heading`, `Highlight`, `Error`, `Muted` as `#RRGGBB` strings (Spectre palette is built in **Cli**). |
| `Arcanum:Cli:ShowManaBar` | `bool` | `true` | When `true`, the **`chat`** REPL prints the context-window mana bar before each prompt (when a model resolves). Set `false` to suppress it (e.g. scripting / piped input). |

All numeric settings have runtime clamps defined in `ArcanumSettingClamps`. When adding a property to `ArcanumSettings`, extend this table in the same change set.

---

## 4. Project model and dependency graph

**Dependency chain:** `Cli` → `Api` → `Infrastructure` → `Core`. `Cli` also references `Core` and `Infrastructure` directly for standalone DI setup (Data Protection, `ISecretStore`, `AddArcanumEyeOfTheWorld`).

### 4.1 `RetroDownfall.Arcanum.Core` (class library)

**Role:** Domain primitives, shared contracts, configuration, security abstractions, and cross-cutting types with **no** ASP.NET Core hosting dependency.

**Namespace areas:**

- **`Primitives/`** — `Error` (readonly record struct), `Result` / `Result<T>` (success/failure with implicit conversions), `ApiResponse<T>` (sealed record wire envelope).
- **`CommLink/`** — `ICommLinkDispatcher`, `CommLinkMessage` (readonly record struct), `CommLinkSeverity` (string-enum JSON via `[JsonConverter(typeof(JsonStringEnumConverter<CommLinkSeverity>))]`).
- **`Configuration/`** — `ArcanumSettings` (root options; `Providers`, `DefaultModel`, `FastModel`), `ProviderSettings`, `AiProviderKind`, `ProviderResolver`, `CommLinkSettings`, `DaemonSettings` / `UnseenServantJob`, `ConfigurationBootstrapper` (loads `arcanum.json` + `ARCANUM_` env vars).
- **`Security/`** — `ISecretStore` (API key read/write contract; concrete implementation in Infrastructure).
- **`Intelligence/`** — `IArcanumIntelligenceProvider` (`ExecutePromptAsync` returns **`Result<PromptTurnResult>`** with text, optional **`ChatCompletionUsage`**, optional `List<PromptToolCall>`, and `FinishReason`; `StreamPromptAsync`), `PingRequest` (sealed record carrying `Prompt`, optional `StatelessMessages` as `List<CoreChatMessage>` for stateless multi-turn without Grimoire history, model, workspace path, context snapshot, conversation id, attached files, optional `ChronosyncDelta`, optional `OverrideSpellName` to load a specific spell without semantic routing, optional `SkipSpellRouting` to bypass `SpellScanner` and `SemanticRouter` entirely for internal headless tasks, behavioral flags, **and OpenAI-shaped inference parameters: `Temperature`, `TopP`, `MaxOutputTokens`, `Stop`, `Seed`, `ResponseFormat`, `PresencePenalty`, `FrequencyPenalty`, `User`, `ParallelToolCalls`** — applied by `HubIntelligenceProvider.ApplyInferenceParameters`), `CoreChatMessage` (`Role`, `Content`, optional `Name`, `ToolCallId`, `ToolCalls` (`CoreToolCall[]`), `ContentParts` (`CoreContentPart[]` for multimodal)), `IntelligenceEvent` / `IntelligenceEventType` (terminal **`result`** includes optional structured **`usage`**; **`toolCall`** and **`toolResult`** carry structured `IntelligenceToolCallEvent` payloads for OpenAI bridges), `IntelligenceStatusMessages` (shared NDJSON **`status`** string literals such as memory compression notice), `AttachedFileDto`, `PromptResponseDto` (envelope payload for `/api/intelligence/ping`).
- **`Storage/`** — `ArcanumPaths`, POCO entities (`Conversation`, `ChatMessage`, `MageSetting`, `WorkspaceContext`), `IGrimoireRepository`, `ICampaignLoggerQueue`.
- **`Chronosync/`** — `ChronosyncReport`, `IChronosyncEngine` (temporal workspace delta vs Grimoire baseline).
- **`Serialization/`** — `GrimoireJsonContext` (source-generated `PatternSnapshot` JSON for Grimoire columns; distinct from Api `ArcanumJsonContext`).
- **`Pattern/`** — `IEyeOfTheWorld`, `DomainType`, `PatternSnapshot`.
- **`Workspace/`** — `IWorkspaceScanner`.

**MSBuild:** `<IsAotCompatible>true</IsAotCompatible>`.

**Non-goals for Core:** Web types, DI registration extensions that pull in hosting, or HTTP-specific middleware.

### 4.2 `RetroDownfall.Arcanum.Infrastructure` (class library)

**Role:** OS-adjacent services — Serilog, Data Protection, encrypted Grimoire (EF Core 10 + SQLCipher, HKDF-derived passphrase, compiled model), workspace scanning, Eye of the World, and the **MCP client layer**.

**MCP architecture:** `IMcpTransport` is implemented by `McpProcessTransport` (subprocess stdio) and `InProcessMcpTransport` (newline-delimited JSON over `Channel<string>` pairs). `ArcanumInternalToolServer` runs on the in-process leg, handling `initialize`, `tools/list`, and `tools/call` with Native AOT-safe JSON schemas via `McpJsonSerializerContext`. `McpClient` manages JSON-RPC correlation. `McpBridgeTool` wraps `tools/call` as an `AIFunction`. `McpConnectionManager` (singleton) loads global `~/.config/arcanum/mcp.json`, starts per-partition in-process servers (including a no-workspace sentinel for `ask_human`), merges profile and optional workspace `mcp.json` servers, and returns deduped `McpBridgeTool` instances (local wins on duplicate names). Per-workspace state is stored as **`ConcurrentDictionary<string, Lazy<T>>`** with `LazyThreadSafetyMode.ExecutionAndPublication`, so racing `GetOrAdd` calls never produce an extra `SemaphoreSlim` or partition record that escapes disposal.

**Per-request cancellation (in-process only):** For each `InProcessMcpTransport` partition, `McpRequestCancellationBroker` maps JSON-RPC request ids to the caller’s `CancellationToken` before `McpClient` writes the request line. `ArcanumInternalToolServer` resolves the same id (via `McpClient.NormalizeRpcId`) and links that token with the transport lifetime token so `tools/call` handlers (including `execute_command`) observe operator cancel immediately. Each broker registration installs a `CancellationToken.Register` callback that auto-removes and disposes the entry when the caller token fires, so a client crash or unhandled exception cannot leak `CancellationTokenSource` instances. **Known limitation:** `McpProcessTransport` subprocess MCP does not propagate cooperative cancel into a remote in-flight `tools/call`; cancel ends the host-side wait; tearing down the transport kills the server process tree but does not send JSON-RPC cancel semantics to arbitrary third-party servers.

**In-process MCP tools:**

| Tool | Purpose |
|------|---------|
| `read_file_chunk` | Read a line range from a file under the workspace root. |
| `replace_text_block` | Replace a verbatim text block in a workspace file. |
| `write_file` | Create or overwrite a workspace file. |
| `list_directory` | List filesystem entries (recursive with skip rules; capped by `ListDirectoryMaxPaths`). |
| `execute_command` | Spawn a process without a shell. Required `command`; arguments accepted as either pre-tokenized `argumentList: string[]` (preferred) or a single `arguments` string the host tokenizes (quoted substrings stay together; whitespace separates tokens). Both forms append to `ProcessStartInfo.ArgumentList` — `ProcessStartInfo.Arguments` is never used. Configurable timeout, `Kill(entireProcessTree: true)` on timeout or cooperative cancel; `CancellationToken.Register` for immediate kill when the linked inference token fires. stdout/stderr capped via `Arcanum:Intelligence:ToolOutputCapBytes`. |
| `ask_human` | Prompt the operator for input (available even without a workspace). |
| `read_lore` / `scribe_lore` / `delete_lore` | Grimoire `MageSettings` key-value store (gated by `EnableLoreSystem`). |
| `search_archives` | FTS5 `MATCH` over `ChatMessage` rows (gated by `EnableArchiveSearch`). |
| `use_commlink` | Comm Link operator alert (`title`, `body`, `severity`, optional `source`). Always listed; resolves **`ICommLinkDispatcher`** per call via `IServiceScopeFactory`. |

All file/directory tools require **relative paths** under the partition workspace root; rooted paths and escapes are rejected. Containment is checked **both lexically** (case-insensitive on Windows) **and after symlink resolution** via `File.ResolveLinkTarget` / `Directory.ResolveLinkTarget` (`returnFinalTarget: true`) — a symlink planted inside the workspace whose final target leaves the workspace is rejected. `ArcanumSpellScriptTool` applies the same check before invoking a spell script. Lore and archive tools resolve `IGrimoireRepository` via `IServiceScopeFactory` per call.

**Other key types:** `AddArcanumInfrastructure` (DI extension wiring all infrastructure services, including **`IUnseenServantPacer`** for Unseen Servant interval overrides, **`ICommLinkDispatcher`** → **`CommLinkMultiplexer`** over **`WebhookCommLinkDispatcher`**, named **`HttpClient("CommLinkWebhook")`** with timeout from `Arcanum:CommLink:WebhookTimeoutSeconds` and a `ConfigurePrimaryHttpMessageHandler` that disables `AllowAutoRedirect`, and Infrastructure-local **`CommLinkInfrastructureJsonContext`** for outbound webhook JSON), `AddArcanumDaemonServices` (`UnseenServantService` — §5.5), `AddArcanumEyeOfTheWorld` (narrow registration for perception only), `AddArcanumThemeDetection` (registers `IThemeDetector` → `ThemeDetector`: Windows `AppsUseLightTheme` registry read with `[UnconditionalSuppressMessage("AOT","IL3050")]`, macOS CoreFoundation `CFPreferencesCopyAppValue` for `AppleInterfaceStyle` with `IntPtr`/`CFRelease` string marshalling, Linux `GTK_THEME` / `COLORFGBG` heuristics, dark fallback on failure), `LoggingBootstrapper`, `DataProtectionSecretStore`, `ArcanumMasterKeyBootstrapper`, `GrimoireKeyDerivation`, `ArcanumDbContext` (compiled model), `GrimoireRepository`, `ChronosyncEngine`, `GrimoireDatabaseHostedService`, `CampaignLoggerQueue` / `CampaignLoggerBackgroundService`, `PhysicalWorkspaceScanner`, `EyeOfTheWorldService`, `CodexReader` (cascades global + local `CODEX.md`), `SpellScanner` (discovers `SPELL.md` files with YAML frontmatter, no YamlDotNet).

**MSBuild:** `IsTrimmable`, `PublishAot` (library signal for IL analysis), `EnableConfigurationBindingGenerator`.

**Non-goals for Infrastructure:** Minimal API route mapping, OpenAPI, or Ollama-specific code.

### 4.3 `RetroDownfall.Arcanum.Api` (class library, not executable)

**Role:** HTTP surface composition — endpoint mapping, JSON contracts, intelligence provider implementation, API-key filter, and bootstrap extensions callable from any host.

**Critical decision:** The Api project is a `Microsoft.NET.Sdk` class library with `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. This separates *composition* from *hosting*: the library describes routes and serialization; it does not own process lifetime.

**API surface (`MapArcanumEndpoints`):**

| Verb | Path | Purpose |
|------|------|---------|
| GET | `/api/health` | Health check. |
| GET | `/api/perception/look` | Eye of the World snapshot (optional `directory` query). |
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
| POST | `/api/commlink/send` | Dispatch a **Comm Link** alert (`CommLinkMessageRequestDto`); **200** + `ApiResponse<bool>`; **400** validation; **502** + envelope on webhook HTTP failure. |
| POST | `/v1/chat/completions` | OpenAI-compatible chat (JSON or SSE); **not** wrapped in `ApiResponse<T>`. Full parameter parsing (§8.8); inference-side application of `temperature`, `top_p`, `max_(completion_)?tokens`, `presence_penalty`, `frequency_penalty`, `seed`, `stop`, `response_format`. |
| GET | `/v1/models` | OpenAI-compatible models list (flattened configured models across providers); **not** wrapped in `ApiResponse<T>`. Stable per-process `created` timestamp. |

**JSON wire shape (`/api` and shared primitives):** Request and response bodies for JSON endpoints under `/api` use the `ApiResponse<T>` envelope (`Data`, `IsSuccess`, `Error`, `TraceId`) unless the route is one of the following: **`POST /api/intelligence/ping-stream`** (NDJSON event lines, `application/x-ndjson`, not an envelope — §8.5); **`GET /api/openapi/v1.json`** / **`GET /api/scalar`** (OpenAPI document and Scalar UI, not application `ApiResponse`); **`POST /v1/chat/completions`** (OpenAI-shaped JSON or `text/event-stream`, §4.3 table); **`GET /v1/models`** (OpenAI-shaped JSON list, §4.3 table). **`DELETE /api/conversations/{id}`** returns **200** with `ApiResponse<bool>` on success; **`POST /api/conversations/{id}/rest`** returns **202** with `ApiResponse<bool>` when the job is queued. **`POST /api/commlink/send`** returns **502** with `ApiResponse<bool>` when the outbound webhook HTTP call fails (non-success status or transport error).

The `/api` and `/v1` groups are protected by `ApiKeyEndpointFilter` (section 11), including the OpenAPI document and Scalar reference UI on `/api` (`MapOpenApi` / `MapScalarApiReference` are registered on the same keyed group, so browsers need a valid API key like any other `/api` caller).

**Key types:** `ApiBootstrapper` (`AddArcanumApiServices` / `MapArcanumEndpoints`), `HubIntelligenceProvider` (§10), `IChatClientFactory` / `ChatClientFactory` (§10), `ProviderResolver` (`Core.Configuration`), `SemanticRouter` (§10.2.2), `ArcanumLocalTimeTool` / `ArcanumSystemInfoTool` / `ArcanumSpellScriptTool` (sealed `AIFunction` subclasses with `public const string ToolName` and static `JsonDocument` schemas; tool ids use snake_case — `get_local_system_time`, `get_arcanum_system_info`, `run_spell_script`), `ApiKeyEndpointFilter` (§11), `ArcanumJsonContext` (§8.2).

**MSBuild:** `IsAotCompatible`, `EnableRequestDelegateGenerator` (essential for Minimal API endpoints in a referenced class library), `EnableConfigurationBindingGenerator`.

### 4.4 `RetroDownfall.Arcanum.Cli` (console executable)

**Role:** Single entry assembly — process argv, dispatch commands, and when asked, construct the ASP.NET Core pipeline and run Kestrel.

**Commands:**

| Command | Purpose |
|---------|---------|
| `serve` | Builds `WebApplication` with slim defaults, configures Kestrel, registers API services, runs the host (§5.3). |
| `ask` | Single-prompt streaming inference via NDJSON. Resolves cwd, runs Eye of the World and Chronosync (scoped `IChronosyncEngine`), sends `PingRequest` with workspace context, `ChronosyncDelta`, and optional conversation continuation. |
| `chat` | Interactive multi-turn REPL with Mana bar, slash commands (`/exit`, `/clear`, `/help`, `/new`, `/model`, `/look`, `/tools`, `/mcp`, `/arsenal`, `/history`, `/resume`, `/delete`, `/rest`, `/log`, `/memory`, `/summary`, `/attach`), per-turn cancellation, inline `@` file staging, and swap-at-end Markdig rendering via `MarkdigSpectreRenderer`. When a **`MemoryCompressionNotice`** status is received, the Mana bar gains a persistent muted **Memory Compressed** suffix until **`/new`**. |
| `look` | Prints `PatternSnapshot` from Eye of the World (no HTTP dependency). |
| `doctor` | Environment diagnostics: assembly version, OS, runtime, Grimoire path checks, and API health probe with 2-second timeout. No infrastructure services required beyond `IHttpClientFactory`, `ISecretStore`, and `IOptions<ArcanumSettings>`. |
| `lore list\|get\|set\|delete` | CRUD on `MageSettings` via `/api/lore`. |
| `daemon install\|uninstall\|status` | OS-specific background service lifecycle (Windows `sc`, macOS `launchd`, Linux `systemctl --user`). |
| `daemon jobs` | Lists Unseen Servant jobs (name, spell, base vs effective interval, enabled) via **`GET /api/daemon/jobs`**; requires **`arcanum serve`** (or equivalent host) and stored API key. |
| `daemon initiative <JOB_NAME> <MINUTES>` | Sets adaptive initiative for a job via **`POST /api/daemon/jobs/{name}/initiative`** with **`AdjustInitiativeRequestDto`**; prints updated **effective** interval (server-clamped). Same connectivity requirements as `daemon jobs`. |
| `daemon alert <MESSAGE>` | Sends a **Comm Link** smoke alert via **`POST /api/commlink/send`** with **`CommLinkMessageRequestDto`** (options: `--title`, `--severity`, `--source`). Same connectivity requirements as `daemon jobs`. |

**Key types:** `ArcanumApiClient` (wraps `IHttpClientFactory` + `ISecretStore`; handles NDJSON streaming, conversation management, lore, MCP reload / arsenal, **Unseen Servant daemon HTTP** (`GetDaemonJobsAsync`, `AdjustDaemonJobInitiativeAsync`), **Comm Link** (`SendCommLinkAlertAsync`), and other `/api` operations via `ArcanumJsonContext`), `CliSessionManager` (plain-text `cli-session.txt` for conversation id persistence), `IThemePalette` / `ConfiguredThemePalette` (Spectre colors from `Arcanum:Cli:ThemeColors`; `IThemePalette` extension methods for markup), `MarkdigSpectreRenderer` (AOT-safe AST walker — no reflection, no `Markdig.Renderers.*`), `CliTypeRegistrar` / `CliTypeResolver` (Spectre DI bridge).

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
| **Doctor** | `doctor` | Runs local environment checks (version, paths, API health); no HTTP dependency for path checks. |
| **Lore** | `lore list\|get\|set\|delete` | CRUD via `/api/lore`. |
| **Daemon** | `daemon install\|uninstall\|status` | OS-specific background service lifecycle. |
| **Daemon (HTTP)** | `daemon jobs`, `daemon initiative <JOB_NAME> <MINUTES>`, `daemon alert <MESSAGE>` | Operator control of Unseen Servant intervals via **`ArcanumApiClient`** → `/api/daemon/*` (§5.5.2); Comm Link smoke tests via **`SendCommLinkAlertAsync`** → **`POST /api/commlink/send`**. |

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
- **`CampaignLoggerQueue` / `CampaignLoggerBackgroundService`** — bounded `Channel<Guid>` plus a background service that runs hybrid sweeps (message-count threshold + idle timeout) and processes queue entries by performing **headless LLM summarization**: a stateless `PingRequest` with `SkipSpellRouting`, `DisableMcpTools`, `UnattendedMode`, optional `Arcanum:FastModel` (else `DefaultModel`), and null `ConversationId`; on success, `UpdateConversationCampaignRollupAsync` atomically sets `Conversation.Summary` and `LastSummarizedMessageAt` to the latest batched message timestamp. On inference failure (`Result.IsFailure` or exception), the watermark is **not** advanced so the conversation stays eligible. Operators may also enqueue via `POST /api/conversations/{id}/rest`.
- **`ArcanumDbContext`** — compiled model; SQLCipher passphrase from hosted service.
- **`GrimoireRepository`** — implements `IGrimoireRepository` (19 methods; the interface is the authoritative reference). `GetConversationAsync` loads the conversation header (no eager `Include`) and a bounded, chronologically-ordered window of the most-recent `Arcanum:Grimoire:MaxMessagesPerConversationLoad` `ChatMessage` rows (default 1000) so very long threads do not blow host RAM. Older messages still exist in SQL — Campaign Logger summaries (§8.7) and FTS5 `search_archives` cover the long tail.
- **`ChronosyncEngine`** — implements `IChronosyncEngine`: compares the current `PatternSnapshot` to the latest `WorkspaceContext` row for that path, persists a new baseline row, and returns a `ChronosyncReport` (headless; no HTTP or Spectre in Phase 1).

#### 5.4.1 Grimoire data model

| Entity | Table | Primary key | Notable |
|--------|-------|-------------|---------|
| `Conversation` | `Conversations` | `Id` (Guid) | `Title`, nullable `Summary`, nullable `LastSummarizedMessageAt`, **`TotalTokensUsed`** (running sum of reported `total_tokens` per inference turn); index on `CreatedAt`; cascade-deletes messages. |
| `ChatMessage` | `ChatMessages` | `Id` (Guid) | FK to `Conversation`; composite index on `(ConversationId, Timestamp)`; `Role` (enum → int); FTS5 virtual table + triggers for `search_archives`. |
| `MageSetting` | `MageSettings` | `Key` (string) | `Value`, `UpdatedAt`; consumed by Lore tools. |
| `WorkspaceContext` | `WorkspaceContexts` | `Id` (Guid) | `CreatedAt` (`DateTimeOffset`), `WorkspacePath` (mapped column `RootPath`, max 4096), `SerializedSnapshot` (JSON `PatternSnapshot` via `GrimoireJsonContext`). **Chronosync reporting** appends a row after each analysis; “latest” for a path is `ORDER BY CreatedAt DESC`. Composite index on `(RootPath, CreatedAt)`. |

**Supporting DTOs (Core):** `ConversationSummaryDto` / `ConversationDetailDto` (both include **`TotalTokensUsed`** for API surfaces), `ConversationMessageDto`, `LoreDto`, `UpsertLoreRequest`, `ChronosyncReport`, `ArcanumPaths`, `ChatCompletionUsage` (OpenAI-shaped `usage` for NDJSON and `/v1` responses), `PromptTurnResult` (buffered inference text + usage).

#### 5.4.2 Temporal context: Session-Based Consolidation and Chronosync

Arcanum’s **Session-Based Consolidation model of AI memory** spans two layers: **conversation** consolidation (Campaign Logger — §8.7) writes **`Conversation.Summary`** and advances **`LastSummarizedMessageAt`** after successful headless summarization, while **Chronosync reporting** supplies **temporal workspace** context — what changed on disk while the operator was away. `IChronosyncEngine` compares the live Eye-of-the-World `PatternSnapshot` to the last Grimoire-stored snapshot for the same `RootPath` and emits a **`ChronosyncReport`** (`PreviousSnapshotTime`, `NewThreads`, `MissingThreads`, `DomainChanged`, `PreviousDomain`) for downstream session consolidation (for example model memory prompts in a later phase). It is orthogonal to Campaign Logger thresholds; both contribute to the same mental model of “what the AI should know without re-reading the tree.”

#### 5.4.3 Design-time factory (`ArcanumDbContextFactory`)

`IDesignTimeDbContextFactory<ArcanumDbContext>` for `dotnet ef` tooling — uses `ARCANUM_GRIMOIRE_DEV_KEY` (fallback placeholder), a temp-directory database, and a no-op `ISecretStore`.

### 5.5 Unseen Servant

The **Unseen Servant** is a proactive background scheduler for headless inference when the HTTP host is running (`serve` or `Api.DevHost`). `AddArcanumDaemonServices` registers **`UnseenServantService`**, an ASP.NET Core **`BackgroundService`** in Infrastructure.

#### 5.5.1 Phase 1 — schedule and execution

**Schedule:** A **`PeriodicTimer`** ticks every **one minute**. For each configured **`UnseenServantJob`** under `Arcanum:Daemon:Jobs`, the service checks the effective interval in minutes (see §5.5.2; clamped via **`ArcanumSettingClamps.UnseenServantIntervalMinutes`**) against an in-memory **`ConcurrentDictionary`** of last completion times. Jobs are **not** persisted across process restarts: on cold start, every enabled job is treated as due on the **first** tick after startup (no watermark on disk).

**Execution:** Due jobs are dispatched with **`Task.Run`** so long inference does not block the timer loop. A per-key **`_runningJobs`** guard prevents overlapping runs for the same job. Each run creates a **new DI scope** (`IServiceScopeFactory.CreateAsyncScope`), resolves **`IArcanumIntelligenceProvider`**, and calls **`ExecutePromptAsync`** with **`UnattendedMode: true`**, **`OverrideSpellName`** set from `targetSpell`, and **`WorkingDirectory`** empty so **`SpellScanner`** discovers global spells under `~/.config/arcanum/spells/`. The kickoff is **either** a Phase 1 multiline prompt (effective interval plus **`use_commlink`** escalation instructions; §5.5.4) **when** **`Intelligence.EnableLoreSystem`** is **`false`**, **or** a lore-aware prompt built with a **raw interpolated string literal** (**`$"""`**): job name, interval, injected **Previous State** from Grimoire, instructions to use **`scribe_lore`** on **`daemon_state_{job.Name}`**, and the same **`use_commlink`** escalation block when lore is enabled. When lore is enabled, the same scope resolves **`IGrimoireRepository`** and **`GetLoreAsync`** is wrapped in **`try`/`catch`** — failures log a warning and run with null prior state so scheduling is not skipped. The host **`stoppingToken`** is passed through to **`ExecutePromptAsync`** so shutdown cancels in-flight work. A **`finally`** block always records **`lastRun`** and clears the running guard so a failing job (for example Ollama unreachable) does not tight-loop every minute.

**Shutdown:** Each dispatched `Task.Run` is registered in a process-wide `_activeJobTasks` `ConcurrentDictionary<Guid, Task>` and removed in its own `finally`. `UnseenServantService.StopAsync` snapshots that dictionary and awaits `Task.WhenAll` with a bounded `CancellationTokenSource` set from **`Arcanum:Daemon:ShutdownDrainTimeoutSeconds`** (default 10 s; `0` disables waiting); jobs that exceed the window are logged but not force-killed beyond the `stoppingToken` cooperative cancel already plumbed into `ExecutePromptAsync`. `RunJobAsync` retains its comprehensive `catch`/`finally` so unobserved exceptions do not leak. The outer scheduler loop is wrapped in try/catch — a single tick exception is logged and the loop continues to the next minute instead of faulting the hosted service.

**Concurrency cap:** **`Arcanum:Daemon:MaxConcurrentJobs`** (default `8`; clamp 1–1024) caps the count of jobs in flight at any moment. When the cap is reached on a tick, additional due jobs are deferred (logged at Debug level) and re-evaluated on the next tick — so a configuration with twenty enabled jobs that all become due simultaneously will not overwhelm the LLM backend.

**Spell selection:** When **`PingRequest.OverrideSpellName`** is set, **`HubIntelligenceProvider`** resolves the spell by frontmatter **`name`** or parent folder name (same convention as spell discovery) and **skips** the **`SemanticRouter`** preflight; otherwise routing behaves as before.

When **`PingRequest.SkipSpellRouting`** is **`true`**, **`HubIntelligenceProvider`** bypasses **`SpellScanner`**, **`OverrideSpellName`**, and **`SemanticRouter`** entirely — **`activeSpell`** is **`null`** and no spell disk IO occurs. Used by Campaign Logger summarization and other internal headless tasks.

#### 5.5.2 Phase 2 — Adaptive initiative (dynamic polling)

**`IUnseenServantPacer`** (singleton, registered in **`AddArcanumInfrastructure`**) holds process-local interval overrides in a **`ConcurrentDictionary<string, int>`** (`StringComparer.Ordinal`). **`SetDynamicInterval(jobName, intervalMinutes)`** trims `jobName`, clamps **`intervalMinutes`** with **`ArcanumSettingClamps.UnseenServantIntervalMinutes`**, and stores the result under the trimmed job name. **`GetEffectiveInterval(job)`** returns a clamped value: it prefers an override keyed by the composite **`$"{job.Name}\0{job.TargetSpell}"`** (aligned with the scheduler’s per-job tracking key), else an override keyed by trimmed **`job.Name`**, else **`job.IntervalMinutes`**. **`UnseenServantService`** applies the clamp again when computing the wait so scheduling and prompts stay consistent.

**MCP:** The in-process server (**`ArcanumInternalToolServer`**) exposes **`adjust_initiative`** (`job_name`, `interval_minutes`). The server receives **`IUnseenServantPacer`** at construction (singleton, threaded through **`InProcessMcpTransport.CreatePair`** and **`McpConnectionManager`**) and calls **`SetDynamicInterval`** synchronously. Tool arguments deserialize through **`McpJsonSerializerContext`** (**`AdjustInitiativeArgs`**) for Native AOT safety.

**HTTP (Phase 3):** External clients use the same pacer via **`GET /api/daemon/jobs`** (returns **`ApiResponse<UnseenServantJobStatusDto[]>`** on the wire; **`Data`** holds **`UnseenServantJobStatusDto[]`**) and **`POST /api/daemon/jobs/{name}/initiative`** with body **`AdjustInitiativeRequestDto`** (`intervalMinutes`); success returns **`ApiResponse<UnseenServantJobStatusDto>`**. Both routes use **`ArcanumJsonContext`** for JSON and **`ApiKeyEndpointFilter`** on `/api`.

**CLI (first-party operator):** **`DaemonJobsCommand`** (`arcanum daemon jobs`) calls **`ArcanumApiClient.GetDaemonJobsAsync`**, deserializes the envelope with **`ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDtoArray`** (source-generated name for **`ApiResponse<UnseenServantJobStatusDto[]>`**), and renders a Spectre **`Table`** using **`IThemePalette`** (including **`HeadingTableColumn`** for headers); when **`EffectiveIntervalMinutes`** differs from **`BaseIntervalMinutes`**, the effective column uses **`HighlightMarkup`** so overrides are visible without hard-coded colors. **`DaemonInitiativeCommand`** (`arcanum daemon initiative …`) calls **`AdjustDaemonJobInitiativeAsync`**; the job name is **`Uri.EscapeDataString`**-encoded in the path segment. **`DaemonAlertCommand`** uses **`SendCommLinkAlertAsync`**. Public C# API on the client follows the same pattern as **`lore`** / **`conversations`**: methods return **`Result<T>`** after interpreting **`IsSuccess`** / **`Error`** on the wire envelope. **`Program`** registers all three commands under the **`daemon`** branch with **`AddTransient`** plus **`[DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DaemonJobsCommand))]`**, **`[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DaemonInitiativeCommand))]`**, and **`[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DaemonAlertCommand))]`** ( **`All`** preserves **`CommandArgument`** / **`CommandOption`** properties Spectre discovers under trimming).

#### 5.5.3 Phase 4 — Stateful memory (lore auto-injection)

**Auto-injection** avoids an extra LLM round-trip that would **`read_lore`** first: **`UnseenServantService`** loads **`LoreDto?`** for **`daemon_state_{job.Name}`** ( **`job.Name`** from **`UnseenServantJob`**) before **`ExecutePromptAsync`** and embeds the value in the kickoff. This runs **only** when **`Arcanum:Intelligence:EnableLoreSystem`** is **`true`** (same flag that gates **`read_lore`** / **`scribe_lore`** / **`delete_lore`** in MCP — see §4.2). When the flag is **`false`**, the model is **not** told to call **`scribe_lore`** because those tools are absent. **`GetLoreAsync`** is **try**/**catch**-wrapped: on failure, **`ILogger`** records a warning and the job proceeds with empty prior state so the minute scheduler does not throw away the interval. Headless **`PingRequest`** still uses an empty **`WorkingDirectory`** so spells come from the global tree; internal lore tools remain available for unattended runs as documented for **`ArcanumInternalToolServer`**.

#### 5.5.4 Phase 5 — Comm Link escalation (kickoff + MCP)

**Kickoff:** Both lore-disabled and lore-enabled Unseen Servant kickoffs append an explicit instruction: if the model detects a **high-alpha** or **critical** condition requiring immediate human attention, it **MUST** call in-process MCP **`use_commlink`** with an appropriate **`severity`** (`Info`, `Warning`, or `Critical`).

**Runtime:** **`use_commlink`** is always advertised in **`tools/list`** (not feature-flagged). The handler resolves **`ICommLinkDispatcher`** per call via **`IServiceScopeFactory`**. **`CommLinkMultiplexer`** fans out to **`WebhookCommLinkDispatcher`**, which **`POST`**s JSON serialized through **`CommLinkInfrastructureJsonContext`** / **`WebhookPayloadDto`**. Missing or invalid **`Arcanum:CommLink:WebhookUrl`** logs a warning and returns success so headless jobs do not fail closed.

**CLI:** **`DaemonAlertCommand`** mirrors **`DaemonInitiativeCommand`** Spectre trimming rules — **`[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DaemonAlertCommand))]`** on **`Program`**.

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

- **`ApiResponse<T>`** is the default envelope for JSON under **`/api`**; streaming and OpenAI compatibility are exceptions (§4.3, §8.5). `sealed record` for value equality and immutability.
- `Error?` is literal `null` on success. `TraceId` from `Activity.Current?.Id ?? HttpContext.TraceIdentifier`.
- `ApiResponse<T>.FromResult` is the single mapping point from `Result<T>` to wire envelope.

### 8.2 `ArcanumJsonContext` — source-generated, public

`ArcanumJsonContext` is the source-generated `JsonSerializerContext` with `CamelCase` naming for all HTTP wire types. It is registered at index 0 of `TypeInfoResolverChain` so Minimal API responses use source-generated `JsonTypeInfo`.

**Rule:** Every wire payload type `T` used in an `ApiResponse<T>` must have a `[JsonSerializable]` registration on this context. OpenAI-shaped **`/v1`** payloads (`OpenAiChatRequest`, `OpenAiModelListResponse`, error bodies, and related nested types) must also be registered for `Results.Json` and streaming serialization. When adding a new endpoint with a new payload type, extend the context in the same change set.

**Grimoire blobs:** `WorkspaceContext.SerializedSnapshot` is **not** serialized through this class. Core defines **`GrimoireJsonContext`** (`RetroDownfall.Arcanum.Core.Serialization`) with the same CamelCase options for `PatternSnapshot` + `DomainType` so Infrastructure (`ChronosyncEngine`) stays AOT-safe without referencing the Api assembly.

**MCP JSON-RPC:** `McpJsonSerializerContext` (Infrastructure) is a separate context for JSON-RPC 2.0 over stdio/in-process channels. It uses explicit `[JsonPropertyName]` for spec-correct member names. `McpConfigJsonSerializerContext` handles `mcp.json` deserialization. Neither is registered on `HttpJsonOptions`.

### 8.3 Service registration in `AddArcanumApiServices`

`ApiBootstrapper.AddArcanumApiServices(IServiceCollection, IConfiguration)` registers:

- `AddArcanumInfrastructure` (Serilog, options, Data Protection, secrets, Grimoire, workspace, Eye of the World, Chronosync engine, MCP, Comm Link dispatchers + **`HttpClient("CommLinkWebhook")`**).
- `AddArcanumDaemonServices` (`UnseenServantService` hosted scheduler; §5.5).
- `ApiKeyEndpointFilter` (singleton; reads `IOptionsMonitor<ArcanumSettings>.CurrentValue` when clamping header limits so `arcanum.json` reload applies).
- OpenAPI + JSON options (ArcanumJsonContext at head of resolver chain).
- Named `HttpClient("OllamaProvider")` with `Timeout = InfiniteTimeSpan` (per-request `BaseAddress` is set from the resolved provider in `ChatClientFactory`).
- Singleton `IChatClientFactory` / `ChatClientFactory` (reads `IOptionsMonitor<ArcanumSettings>.CurrentValue` only inside `ResolveClient` for hot-reload).
- Singleton **`InferenceTokenizerResolver`** (process-cached **`Microsoft.ML.Tokenizers`** Tiktoken `o200k_base` via **`TiktokenTokenizer.CreateForEncoding`** and companion package **`Microsoft.ML.Tokenizers.Data.O200kBase`**; used only for pre-flight counting).
- Scoped `IArcanumIntelligenceProvider` / `HubIntelligenceProvider` (uses `IOptionsSnapshot<ArcanumSettings>.Value` so each request sees one consistent settings snapshot).
- Minimal API handlers under **`/v1`** (`OpenAiV1Endpoints`) take `IOptionsSnapshot<ArcanumSettings>` for the same per-request snapshot semantics.

**Infrastructure (via `AddArcanumInfrastructure`):** singleton **`McpConnectionManager`** uses **`IOptionsMonitor<ArcanumSettings>.CurrentValue`** when reading intelligence timeouts and related clamps. Scoped **`GrimoireRepository`** uses **`IOptionsSnapshot<ArcanumSettings>.Value`**. Singleton **`EyeOfTheWorldService`** uses **`IOptionsMonitor<ArcanumSettings>.CurrentValue`** (via lazy clamped properties) because singletons must not capture a single `IOptionsSnapshot` for the process lifetime.

### 8.4 Returning the envelope from a Minimal API handler

Successful endpoints use `Results.Ok(ApiResponse<T>.FromResult(result, traceId))`. Failable endpoints use `Results.Json` with the source-generated `JsonTypeInfo` and an explicit HTTP status code. No anonymous DTOs; no reflection-based model binding.

**Selected status contracts:**

- **`POST /api/intelligence/ping`** — **400** + `ApiResponse<string>` for validation (missing prompt / messages); **200** + `ApiResponse<string>` on success; **500** + `ApiResponse<string>` when inference returns `Result` failure (model/provider error — intentional, not a generic server crash).

- **`POST /api/intelligence/human-response`** — **400** validation; **404** + `ApiResponse<bool>` failure when no waiter exists for `promptId` (`Intelligence.HumanPromptNotFound`); **200** + `ApiResponse<bool>` with `Data: true` when the answer is accepted.

- **`POST /api/mcp/reload`** and **`POST /api/intelligence/arsenal`** — Optional JSON body **`OptionalWorkspaceRequest`** (`{ "workingDirectory": "..." }` only). Responses remain `ApiResponse<T>` as today.

### 8.5 NDJSON streaming pipeline

`/api/intelligence/ping-stream` uses NDJSON (`application/x-ndjson`) for real-time token streaming:

- **Server:** Events serialized via `Utf8JsonWriter` + `ArcanumJsonContext`, newline-terminated, flushed per event. Linked `CancellationTokenSource` for connection abort.
- **Wire shape:** Each line is an `IntelligenceEvent` with **camelCase string** discriminator **`type`**: **`"status"`**, **`"conversationBound"`**, **`"token"`**, **`"result"`**, **`"error"`**, **`"toolCall"`**, **`"toolResult"`**. The enum is annotated with `[JsonConverter(typeof(JsonStringEnumConverter<IntelligenceEventType>))]` and per-member `[JsonStringEnumMemberName]` so the AOT JSON source generator emits and accepts the exact strings (no `JsonNamingPolicy` dependency).
- **Client (`ArcanumApiClient`):** Reads UTF-8 lines, deserializes each with `ArcanumJsonContext.Default.IntelligenceEvent`. Malformed frames yield a fabricated error event and continue (single bad frame does not terminate the session). The terminal **`result`** event carries OpenAI-shaped **`usage`** (`prompt_tokens`, `completion_tokens`, `total_tokens`) on the `IntelligenceEvent` payload; **`data`** still duplicates **`total_tokens`** as a decimal string for backward compatibility. Assistant text is not in `result` — clients accumulate **`token`** frames for the answer body.

### 8.6 Request Delegate Generator

`<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>` on `Api` ensures Minimal API endpoints in a referenced class library are source-generated.

### 8.7 Session-Based Consolidation (Campaign Logger)

Three mechanisms trigger Campaign Log consolidation:

1. **Message-count threshold** (`CampaignLogThreshold`) — safety valve for unbounded growth.
2. **Idle timeout** (`CampaignLogIdleTimeoutMinutes`) — natural session boundary.
3. **Explicit rest** — `POST /api/conversations/{id}/rest`.

The queue consumer resolves **`IArcanumIntelligenceProvider`** in a per-item DI scope alongside **`IGrimoireRepository`**, loads the conversation with messages, and batches rows with **`Timestamp > (LastSummarizedMessageAt ?? DateTime.MinValue)`**. It builds a stateless **`PingRequest`**: empty `Prompt`, `StatelessMessages` (system persona + user payload with prior summary and batched turns), **`SkipSpellRouting: true`**, **`DisableMcpTools: true`**, **`UnattendedMode: true`**, **`Model`** from **`Arcanum:FastModel`** when set else **`Arcanum:DefaultModel`**, else omitted for first-provider fallback, and **no** `ConversationId` so the hub does not append a new **`ChatMessage`**. On **`ExecutePromptAsync`** success, **`UpdateConversationCampaignRollupAsync`** atomically persists the LLM text into **`Conversation.Summary`** and sets **`LastSummarizedMessageAt`** to the latest batched message time. On **`Result.IsFailure`** or exception, **no** DB update — the conversation remains eligible on the next sweep. The intelligence hub **reads** `Summary` for optional read-time compression (§10.2.3).

Under the same **Session-Based Consolidation model of AI memory**, **Chronosync reporting** (§5.4.2) addresses **spatial** drift: thread lines and `DomainType` deltas vs the last persisted `PatternSnapshot`, not chat log length. Campaign Logger and Chronosync are separate triggers; Phase 2 folds `ChronosyncReport` into the system prompt via `PingRequest.ChronosyncDelta`; MCP context remains separate.

### 8.8 OpenAI `/v1` parity surface

`OpenAiV1Endpoints` accepts and parses the maximum-parity OpenAI Chat Completions surface:

**Request body** (`OpenAiChatRequest`): `model` (required, validated), `messages` (required, non-empty; each `role` validated against `system|user|assistant|tool|developer`), `stream`, `temperature`, `top_p`, `max_tokens` and `max_completion_tokens` (newer alias preferred), `presence_penalty`, `frequency_penalty`, `seed`, `n`, `user`, `stop` (string or string[]), `response_format` (`text` | `json_object` | `json_schema` with optional `json_schema`), `stream_options { include_usage }`, `tools` (function array, `OpenAiToolDefinition`), `tool_choice` (kept as `JsonElement` for pass-through), `parallel_tool_calls`, `logprobs`, `top_logprobs`.

**Polymorphic message content**: `messages[].content` can be `null`, a `string`, or an array of `OpenAiContentPart` (`{type: "text", text}` or `{type: "image_url", image_url: { url, detail }}`). Parsing is AOT-safe through a custom `JsonConverter<OpenAiMessageContent>` that dispatches on `Utf8JsonReader.TokenType` and delegates array reads to the source-generated `ArcanumJsonContext.Default.OpenAiContentPartArray` `JsonTypeInfo`. The mapper concatenates text parts (separated by `\n`) into a flat string for storage/logging while preserving an `IReadOnlyList<CoreContentPart>` on `CoreChatMessage.ContentParts`. The hub composes `Microsoft.Extensions.AI` multi-part messages: text parts become `TextContent`, `http(s)://` image URLs become `UriContent(uri, "image/*")` so vision-capable providers (for example OpenAI proper via `Microsoft.Extensions.AI.OpenAI`) see them.

**Tool messages**: `role = "tool"` messages with `tool_call_id` map to `FunctionResultContent` for the hub. `role = "assistant"` messages with `tool_calls` map to `FunctionCallContent` entries with arguments JSON parsed into `Dictionary<string, object?>` (object values are `JsonElement` clones for downstream serialization). The bridge is symmetric so OpenAI clients can replay full transcripts including assistant tool calls and tool results.

**Inference-side application** of parameters happens in `HubIntelligenceProvider.ApplyInferenceParameters`:

- `temperature` → `ChatOptions.Temperature` (clamp 0–2)
- `top_p` → `ChatOptions.TopP` (clamp 0–1)
- `max_(completion_)?tokens` → `ChatOptions.MaxOutputTokens` (positive only)
- `presence_penalty` / `frequency_penalty` → corresponding `ChatOptions` fields (clamp −2..2)
- `seed` → `ChatOptions.Seed`
- `stop` → `ChatOptions.StopSequences`
- `response_format` → `ChatOptions.ResponseFormat` (`json_object`/`json_schema` → `ChatResponseFormat.Json`; `text` → `ChatResponseFormat.Text`)

`n`, `user`, `parallel_tool_calls`, `logprobs`, `top_logprobs`, `tool_choice`, and the `tools` array are parsed for forward-compat / API completeness but are not yet enforced (`n` is treated as `1`; `logprobs` field on responses is always `null`).

**Non-streaming response** (`OpenAiChatResponse`): includes `choices[]` with `index`, `message: {role, content, tool_calls?, refusal: null}`, `finish_reason`, `logprobs: null`. Top level includes `system_fingerprint` (configurable; see §3.4) and `service_tier: null`. When Arcanum executed tools server-side during the turn, the assistant message exposes the calls as `tool_calls` for observability while `finish_reason` stays `"stop"` (because the conversation is genuinely complete — the model produced final text after the tool result). Strict OpenAI semantics expect `finish_reason: "tool_calls"` with `content: null` to indicate the *client* should execute and re-call; Arcanum never returns that shape because tools run in-process.

**Streaming SSE** (`OpenAiChatChunk` over `text/event-stream`):

- Frame 0: `delta: {role: "assistant"}` (per OpenAI convention).
- Token frames: `delta: {content: "..."}` for each `IntelligenceEventType.Token`.
- Tool-call frames: `delta: {tool_calls: [{index, id, type: "function", function: {name, arguments}}]}` for each `IntelligenceEventType.ToolCall`. Arcanum emits one complete chunk per call (arguments JSON is delivered intact rather than fragmented across chunks).
- Terminal frame(s):
  - When `stream_options.include_usage = true`: a content-empty final chunk with `finish_reason: "stop"`, then a `choices: []` chunk with the `usage` payload, then `data: [DONE]`.
  - Otherwise: a content-empty final chunk with `finish_reason: "stop"`, then `data: [DONE]`.
- `Cache-Control: no-cache` and `X-Accel-Buffering: no` headers set up front.

**Streaming errors** are emitted as a single SSE chunk in the OpenAI error shape — `{"error":{"message":"...","type":"api_error","code":"inference_failed","param":null}}` — followed by `data: [DONE]`. This is **not** sent as `delta.content`, so clients can branch on the `error` key without mistaking it for assistant output. The earlier behaviour of leaking `IntelligenceEvent` error messages as model output is gone.

**Cancellation** (`OperationCanceledException`) inside the stream is caught and the terminal frames (`finish_reason: "stop"` + `[DONE]`) are best-effort emitted with `CancellationToken.None` so clients that are still listening see a clean termination. Writes that fail (because the connection is gone) are caught and logged at warning.

**Error envelope** (`OpenAiErrorResponse`): includes `message`, `type`, `param`, and `code`. Buffered error responses now populate `code` from `Result.Error.Code` (opaque short identifier) so OpenAI-style clients can branch programmatically. Validation errors emit `code = "missing_required_parameter"` / `"invalid_value"` / `"model_not_found"` / `"invalid_json"` / `"missing_body"` as appropriate.

### 8.9 NDJSON anti-buffering headers (`/api/intelligence/ping-stream`)

The NDJSON streaming endpoint sets `Cache-Control: no-cache` and `X-Accel-Buffering: no` (parity with the SSE endpoint in §8.5/§8.8) so reverse proxies (nginx, Cloudflare, k8s ingress) do not coalesce incremental frames.

### 8.10 Buffered `/api/intelligence/ping` envelope

The buffered ping endpoint wraps a **`PromptResponseDto`** (Core) inside `ApiResponse<T>`: `text` (assistant content), `usage` (OpenAI-shape token counts when reported), `toolCalls` (the assistant-issued calls executed server-side, when any), `finishReason`. Previously the envelope held only the assistant text as a bare `string`; clients now get the full turn context without falling back to NDJSON.

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
- Outbound Comm Link webhook bodies use `CommLinkInfrastructureJsonContext` / `WebhookPayloadDto` exclusively — no `PostAsJsonAsync` with anonymous DTOs.
- Minimal API handlers must not return anonymous DTOs or use unbounded reflection-based model binding.
- New `AIFunction` tools must use hand-authored `JsonDocument` schemas, not `AIFunctionFactory.Create`.

---

## 10. Intelligence pipeline

### 10.1 Architecture

The intelligence layer follows a **provider pattern**: `Core` defines `IArcanumIntelligenceProvider`, `Api` implements **`HubIntelligenceProvider`** behind a factory-built **`IChatClient`** per request.

- **`ProviderResolver`** (`Core.Configuration`) maps `PingRequest.Model` (or `ArcanumSettings.DefaultModel`, or the first configured model) to a `ProviderSettings` row and canonical model id — no hard-coded default model literals. Internal callers (Campaign Logger) supply an explicit `PingRequest.Model` from **`Arcanum:FastModel`** when set, else **`Arcanum:DefaultModel`**, before falling back to the first configured model.
- **`IChatClientFactory`** (`ChatClientFactory`, singleton) resolves `AiProviderKind.Ollama` via **OllamaSharp** `OllamaApiClient` + pooled `HttpClient`, or `OpenAICompatible` via **`Microsoft.Extensions.AI.OpenAI`** / OpenAI .NET `ChatClient` + custom `endpoint` + `AsIChatClient()`.
- **Microsoft.Extensions.AI** provides the shared `IChatClient` surface for routing, tools, and streaming.

### 10.2 `HubIntelligenceProvider` design

**Model resolution:** `ProviderResolver.TryResolveProviderForModel` on the current `ArcanumSettings` snapshot. Explicit request/default model strings must match a configured `models` entry or resolution fails (configuration error).

**Model availability (Ollama only):** `IsModelLocalAsync` / `EnsureModelExistsAsync` / streaming pull run **only** when the resolved provider is `Ollama`. OpenAI-compatible hosts do not support Ollama pull semantics.

**Streaming:** `StreamPromptAsync` yields `IntelligenceEvent` objects — `status` (model checks, download progress), `conversationBound` (canonical conversation id), `token` (incremental text), `toolCall` / `toolResult` (tool execution diagnostics), **`result`** (structured **`usage`** plus legacy **`data`** total string), `error`.

**Operator-safe errors:** Inference failures use fixed generic strings for clients and Grimoire; full exceptions are logged internally only.

### 10.2.1 Built-in tools and MCP workspace tools

Tool registration is built in `HubIntelligenceProvider` per inference attempt:

1. `ArcanumLocalTimeTool` (`get_local_system_time`) — always registered. Returns the current local system time in ISO 8601.
2. `ArcanumSystemInfoTool` (`get_arcanum_system_info`) — always registered. Returns host OS description, CPU architecture, and .NET runtime version.
3. `ArcanumSpellScriptTool` (`run_spell_script`) — registered when the active spell has `scripts/` files (even when `DisableMcpTools` is true).
4. MCP tools — merged from `McpConnectionManager.GetAvailableToolsAsync` unless `DisableMcpTools` is true.

All hub built-in tool ids use snake_case, consistent with in-process MCP tools.

The canonical tool list is in §4.2. `run_spell_script` runs with `UseShellExecute = false`, cwd fixed to the spell's `scripts/` directory, bare filename only (prefix containment), extension-based runner map, and the same timeout, cooperative-cancel, and kill-tree behavior as `execute_command` (including `CancellationToken.Register` for immediate process kill).

When `WorkingDirectory` is empty, filesystem tools return a workspace-not-configured error; `ask_human`, Lore, and `search_archives` still work.

### 10.2.2 Semantic spell routing (pre-flight → main loop)

**Problem:** Operators want versioned markdown "spells" (workflows, checklists, personas) without pasting them into `CODEX.md`. Only one spell should apply per prompt.

**Solution — two passes:**

1. **Discovery (`SpellScanner`):** Scans `~/.config/arcanum/spells/` then the workspace for `SPELL.md` files. Parses YAML frontmatter (`name:`, `description:`) without YamlDotNet. Workspace spells override global spells on name collision (case-insensitive). Sibling `scripts/` directory file names populate `AvailableScripts`.

2. **Pre-flight routing (`SemanticRouter`):** Single `IChatClient.GetResponseAsync` with low max output tokens, zero temperature, no tools, bounded timeout, and `ChatOptions.ResponseFormat = ChatResponseFormat.Json`. The model must return a single JSON object with exactly one camelCase key `spellName` whose value is either the exact matching spell name or `NONE`. The hub deserializes with `JsonSerializer.Deserialize(..., ArcanumJsonContext.Default.SemanticSpellResponse)` after stripping optional markdown code fences; on `JsonException` or non-matching name, `activeSpell` is `null`. Failures and timeouts resolve to no spell — main inference is unchanged.

3. **Main inference:** `SystemPromptBuilder` appends `### Active Operational Spell` with the spell's full markdown, plus `### Available Spell Scripts` when scripts exist.

**`SkipSpellRouting`:** When **`PingRequest.SkipSpellRouting`** is **`true`**, **`HubIntelligenceProvider`** skips both **`SpellScanner.ScanAsync`** and **`SemanticRouter.DetermineActiveSpellAsync`**, sets **`activeSpell`** to **`null`**, and does not evaluate **`OverrideSpellName`**. This avoids spell disk IO and router LLM cost for internal background tasks (Campaign Logger). **`CodexReader.ReadCodexAsync`** still runs; with an empty **`WorkingDirectory`** (Campaign Logger), codex content is null.

### 10.2.3 Pre-flight token counting and read-time context compression

After the dynamic system prompt is prepended to the in-memory `ChatMessage` list (and before the main `GetResponseAsync` / `GetStreamingResponseAsync` call), **`HubIntelligenceProvider`** may apply **read-time** compression when **`Arcanum:Intelligence:EnableContextCompression`** is **`true`**:

- **Fast path:** if the assembled message count is **at or below** `Arcanum:Intelligence:CompressionPreflightMinMessages` (default 6), tokenization is skipped (short threads are assumed under budget).
- **Tokenizer:** singleton **`InferenceTokenizerResolver`** returns a cached tokenizer keyed on the encoding name from `Arcanum:Intelligence:TokenizerEncoding` (default `o200k_base`). Unknown encodings log a warning and fall back to `o200k_base` so the hub never throws on misconfig. The cache uses `OrdinalIgnoreCase` keys.
- **Counting:** **`InferenceTokenCounter`** sums `Tokenizer.CountTokens` over flattened message text plus the configurable per-message overhead `Arcanum:Intelligence:PerMessageTemplateOverheadTokens` (default 4). **Tool definitions** attached in `ChatOptions` are **not** included; the **`ContextWindowCompressionThreshold`** percentage headroom is intended to absorb that gap.
- **Threshold:** compared to `ContextWindowLimit(provider) * ContextWindowCompressionThreshold / 100` (both clamped).
- **Swap:** when over threshold, **`Conversation.Summary`** and **`Conversation.LastSummarizedMessageAt`** must both be present; otherwise a **warning** is logged and history is left unfiltered. When present, Grimoire messages with `Timestamp <= LastSummarizedMessageAt` are omitted from the inference transcript and the summary is injected via **`SystemPromptBuilder.Build(..., campaignSummary: ...)`** as `### Campaign Summary (compressed context)` (see §10.5). **No `ChatMessage` rows are deleted.**
- **NDJSON:** when compression applies on **`ping-stream`**, a **`status`** event is emitted with message **`IntelligenceStatusMessages.MemoryCompressionNotice`** (shared const in **`RetroDownfall.Arcanum.Core.Intelligence`**) immediately before streaming inference begins (after `conversationBound` when bound). Buffered **`ping`** logs the same string at **Information** when compression runs.
- **Native AOT:** tokenizer creation uses the **`Microsoft.ML.Tokenizers.Data.O200kBase`** data assembly so vocabulary is linker-friendly; **`dotnet publish`** on **`Cli`** should remain warning-clean aside from known Spectre / transitive advisory noise.

### 10.3 Registration lifetimes

`IArcanumIntelligenceProvider` / `HubIntelligenceProvider` are **scoped** (one instance per request scope). `IChatClientFactory` is **singleton**; each call to `ResolveClient` returns a **`ChatClientLease`** that owns a fresh `IChatClient` (and related `HttpClient` for Ollama) for that inference turn — concurrent requests do not share client instances.

### 10.4 Grimoire integration

The provider persists through `IGrimoireRepository`. When `conversationId` is set, prior turns are loaded for `IChatClient`. A dynamic `ChatRole.System` message from `SystemPromptBuilder` is prepended in memory (not persisted to Grimoire). Tool rounds are persisted as bracket-formatted `ChatMessage` rows. After a successful inference turn (buffered or streamed), when **`Arcanum:Intelligence:EnableTokenTracking`** is **`true`** and a conversation is bound, **`IncrementConversationTokensAsync`** atomically adds the turn’s reported **`total_tokens`** to **`Conversation.TotalTokensUsed`**. Persistence failures on the buffered path are logged as warnings only.

### 10.5 Spatial context on inference

**Problem:** The API daemon's cwd is not the operator's shell cwd.

**Solution:** `PingRequest` carries `WorkingDirectory`, `ContextSnapshot` (`PatternSnapshot`), optional `ConversationId`, optional `StatelessMessages` (`CoreChatMessage` transcript for enterprise or OpenAI-compatible callers without Grimoire thread replay), optional `AttachedFiles`, and optional `ChronosyncDelta` (`ChronosyncReport`). The CLI resolves `Environment.CurrentDirectory`, runs Eye of the World, runs `IChronosyncEngine` inside a DI scope against the local Grimoire, and populates these fields before each HTTP call.

**CLI Grimoire bootstrap:** `ask` and `chat` call `IGrimoireCliInitialization` once per process so SQLCipher passphrase setup and first-run migrations match the API host (`GrimoireDatabaseBootstrapper`, shared with `GrimoireDatabaseHostedService`).

**`SystemPromptBuilder.Build` ordering:**

1. Base persona.
2. `### Workspace Context` / `### Table of Contents` (from `ContextSnapshot`).
3. `### Chronosync Report (Temporal Delta)` (from `PingRequest.ChronosyncDelta` when it carries a prior snapshot time and a non-empty diff — new threads, missing threads, or domain change; uses `PreviousDomain` and current snapshot domain when available; omitted when null, no prior baseline, or zero net change).
4. `### Master Codex (CODEX.md)` (global cascaded with optional local).
5. `### Campaign Summary (compressed context)` (optional; only when read-time compression injects **`Conversation.Summary`** — placed after Codex, before the active spell).
6. `### Active Operational Spell` (from `SemanticRouter` when spell routing runs; omitted when `SkipSpellRouting` is true).
7. `### Available Spell Scripts` (when scripts exist).
8. `### Attached Files for this Turn` (ephemeral, not persisted to Grimoire).
9. `### Output Formatting Directive` (when `CliTerminalFormatting` is true — restricts model to headings, bold, italic, and code blocks for terminal rendering).

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

1. **`X-Arcanum-Key`** when present (legacy Arcanum header). **Multiple values reject with 401** — the filter explicitly disallows ambiguous duplicate headers.
2. Otherwise `Authorization: Bearer` followed by the raw key (OpenAI-compatible clients). The `Bearer` prefix is case-insensitive; only the trimmed token after the first space is compared. **Multiple `Authorization` values reject with 401**.

The filter then:

1. Rejects values exceeding `MaxApiKeyHeaderUtf16Chars` with 401.
2. Caches a **SHA-256 digest** of the expected key (32 bytes, fixed size) for **`Arcanum:Security:ApiKeyCacheTtlSeconds`** (default 30 s) so on-disk key rotation propagates without restart. The plaintext expected key never lives in long-term memory beyond computing the digest, and the temporary UTF-8 buffer is zeroed.
3. Hashes the inbound header through `SHA256.TryHashData` into a stack buffer and compares both 32-byte digests with `CryptographicOperations.FixedTimeEquals` — constant-time **and** length-independent (no early-return on size mismatch).
4. Uses `stackalloc` for the header UTF-8 buffer when `<= 256` bytes; the 32-byte digest buffer is always on the stack.

### 11.4 CORS (serve host)

`AddArcanumApiServices` registers a CORS policy named **`ArcanumCors`** whose **allowed origins are read from `Arcanum:Host:CorsAllowedOrigins`** at startup. Defaults to localhost loopback (`http://localhost:5001`, `http://127.0.0.1:5001`, `http://localhost:3000`, `http://127.0.0.1:3000`). Operators who need to allow any browser origin (for example LibreChat installations on arbitrary hosts) can set the property to `["*"]` — Arcanum then calls `AllowAnyOrigin` and adds the same `AllowAnyHeader` / `AllowAnyMethod` it always has. `UseArcanumCors` runs early in the pipeline so browser-based tools can preflight without endpoint contention. `AllowAnyHeader` / `AllowAnyMethod` are retained unconditionally because callers always present custom headers (`X-Arcanum-Key`) and use varied verbs.

### 11.5 OpenAPI and Scalar

`MapOpenApi` runs unconditionally under the keyed `/api` group, so `openapi/v1.json` always requires the API key. **`MapScalarApiReference`** is **gated by `Arcanum:Host:EnableScalarUi`** (default **`false`**). When enabled, the Scalar route lives in a sub-group with a CSP filter that emits `Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'` and `X-Content-Type-Options: nosniff` on every response. `'unsafe-inline'` is retained because Scalar's bootstrap relies on inline `<script>` / `<style>`; everything else is restricted to same-origin. The OpenAI-shaped **`POST /v1/chat/completions`** and **`GET /v1/models`** routes live under `MapGroup("/v1")` with the same API-key filter and are not advertised in the OpenAPI document.

### 11.6 Symlink containment for tool paths

`ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck` performs the lexical prefix check (case-insensitive on Windows) **and** resolves the candidate's final symlink target via `File.ResolveLinkTarget(returnFinalTarget: true)` / `Directory.ResolveLinkTarget`. If the resolved target leaves the workspace, the request is rejected. `ArcanumInternalToolServer.TryResolveSandboxedPath` and `ArcanumSpellScriptTool` both call this guard so an attacker-planted symlink inside the workspace cannot pivot outside.

### 11.7 In-process `execute_command` argument handling

The tool accepts arguments in **either** of two forms:

- **`argumentList: ["status", "--porcelain"]`** — preferred. Each entry is appended verbatim to `ProcessStartInfo.ArgumentList`. No shell, no OS-level re-parsing.
- **`arguments: "status --porcelain"`** — legacy single-string form. The host tokenizes via the same algorithm `ArcanumSpellScriptTool` uses (quoted substrings stay together; whitespace separates tokens) and then appends each token to `ArgumentList`.

`Arguments` is **never** assigned to `ProcessStartInfo.Arguments` directly, so model output cannot smuggle additional argv via shell metacharacters.

### 11.8 Tool output caps

`execute_command` and `run_spell_script` both read stdout/stderr through a `ReadStreamCappedAsync` helper that enforces **`Arcanum:Intelligence:ToolOutputCapBytes`** split evenly per stream. Beyond the cap, the stream is silently closed and a `[truncated: exceeded N bytes]` marker is appended. UTF-8 boundary safety is preserved by `ChooseSafeCharCount`. This prevents a verbose tool from exhausting host memory.

### 11.9 Sanitized public error envelopes

Inference-pipeline errors must not leak internal exception text to clients:

- **`HubIntelligenceProvider.ExecutePromptAsync`** / **`StreamPromptAsync`** — model-resolution failures return the public string `"The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel."`; full exception is logged via `ILogger.LogWarning`.
- **`POST /v1/chat/completions`** — buffered failures return the public string `"Inference failed. See server logs for details."`; never the raw `Result.Error.Message`.
- **`WebhookCommLinkDispatcher`** — outbound webhook exceptions return the public code `CommLink.WebhookException` with the generic message `"Comm Link webhook POST failed. See server logs for details."`; the actual exception is logged.

### 11.10 Comm Link webhook scheme allowlist and redirect handling

`WebhookCommLinkDispatcher` validates the scheme of `Arcanum:CommLink:WebhookUrl` against `Arcanum:CommLink:AllowedSchemes` (default `["https","http"]`). URLs with disallowed schemes are skipped with a warning so a misconfigured `file://` or `ftp://` URL never causes a network/filesystem call. The named `HttpClient("CommLinkWebhook")` is configured with `HttpClientHandler.AllowAutoRedirect = false`, eliminating SSRF amplification where a webhook endpoint could 302 to an internal target (`http://169.254.169.254`, RFC1918, etc.). The client `Timeout` reads from `Arcanum:CommLink:WebhookTimeoutSeconds` (default 15s, clamp 1–120).

### 11.11 Kestrel limits and optional rate limiter

`ServeCommand.ConfigureKestrel` reads `Arcanum:Host:MaxRequestBodyBytes` (default 10 MiB; clamped 256 KiB – 1 GiB) and applies it as `KestrelServerOptions.Limits.MaxRequestBodySize`. When `Arcanum:Host:RateLimit:Enabled` is `true`, `AddArcanumApiServices` calls `AddRateLimiter` with a fixed-window policy named **`ArcanumRateLimit`**; both `/api` and `/v1` `MapGroup` routes apply `RequireRateLimiting("ArcanumRateLimit")`. Partition keys prefer `X-Arcanum-Key` header value (one bucket per credential), fall back to `Authorization`, and finally to the remote IP. `RejectionStatusCode = 429`. `Arcanum:Host:RateLimit:QueueLimit` enables queueing (`OldestFirst`, `AutoReplenishment = true`); the default `0` rejects excess requests immediately.

### 11.12 `Arcanum:Host:ListenAny` versus `ARCANUM_HOST_ANY`

The environment variable always wins. When the env var is unset or empty, `ServeCommand.ShouldBindHostAny` falls back to the configuration property. This preserves the historical container-friendly override while making the binding visible in `arcanum.json` for first-party operators.

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
4. **New intelligence providers:** Implement `IArcanumIntelligenceProvider` in `Api`. Follow the `HubIntelligenceProvider` + `IChatClientFactory` pattern (or extend the factory for new `AiProviderKind` values).
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
- **Pre-flight token counts** use a single **`o200k_base`** Tiktoken approximation and omit tool-schema tokens; **`ContextWindowCompressionThreshold`** provides headroom. Iterative per-message trimming beyond one summary swap is not implemented.
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
| **`AddArcanumInfrastructure`** | DI extension registering all infrastructure services (Serilog, options, secrets, Grimoire, workspace, perception, MCP, Comm Link). |
| **Comm Link** | Modular operator alerting: **`ICommLinkDispatcher`**, **`CommLinkMultiplexer`**, **`WebhookCommLinkDispatcher`**, MCP **`use_commlink`**, **`POST /api/commlink/send`**, CLI **`daemon alert`**. |
| **`ICommLinkDispatcher`** | Core contract: **`DispatchAsync(CommLinkMessage, CancellationToken)`** → **`Result`**. |
| **`CommLinkMultiplexer`** | Infrastructure **`ICommLinkDispatcher`** implementation broadcasting to inner dispatchers (sequential; first failure wins). |
| **`CommLinkInfrastructureJsonContext`** | Infrastructure-only source-generated JSON context for outbound **`WebhookPayloadDto`** (not on `HttpJsonOptions`). |
| **`AddArcanumEyeOfTheWorld`** | Narrow DI extension: `IEyeOfTheWorld` only (no Grimoire or Serilog). |
| **Eye of the World** | Situational directory perception — `EyeOfTheWorldService` in Infrastructure (§15). |
| **`PatternSnapshot`** | `DomainType` + `RootPath` + `Threads` (bounded TOC lines). |
| **`IGrimoireRepository`** | Core contract for Grimoire CRUD — 19 methods covering conversations, messages, lore, archive search, workspace snapshot rows, and **`IncrementConversationTokensAsync`** (§5.4). |
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
