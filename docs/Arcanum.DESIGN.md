# Arcanum — Design Document

This document captures the **architecture, design decisions, and tradeoffs** for the Retro Downfall **Arcanum** solution. The intended audience is **senior C# / .NET engineers** who will extend, review, or operate the system.

**Keeping this document accurate:** When any change under `src/` alters architecture, observable behavior, or names described here, update the relevant sections in the same change set. Pair operator-visible behavior changes with `README.md` updates.

---

## 1. Purpose and scope

**Arcanum** is a **single deployable CLI** that can:

1. Run **terminal-oriented commands** — currently `ask` (single-prompt LLM inference with optional Grimoire thread continuation), `chat` (interactive multi-turn REPL), `look` (workspace perception), `lore` (key-value CRUD), `daemon` (OS-level background service lifecycle plus **API-first** monitoring of Unseen Servant jobs via `daemon jobs`, `daemon initiative`, and Comm Link smoke tests via `daemon alert` when Kestrel is up), plus campaign/session/spell/prompt/ward/trial/apprentice/model/provider verbs that are thin clients over the same HTTP API.
2. Act as a **long-running HTTP host** exposing a Minimal API surface (the `serve` command).

The codebase is organized as a **multi-project solution**: `Core` (domain primitives, contracts, configuration), `Infrastructure` (Serilog, Data Protection, encrypted Grimoire via EF Core + SQLCipher, workspace scanning, Eye of the World perception, MCP client layer with both subprocess and in-process transports), `Api` (HTTP surface, multi-provider intelligence hub, semantic spell routing, API-key security), and `Cli` (ConsoleAppFramework entry point). All projects target **Native AOT readiness** where the toolchain allows.

Key subsystems described in later sections: hybrid hosting model (§5), HTTP JSON design (§8), intelligence pipeline with MCP tool integration (§10), local API security (§11), and Eye of the World situational awareness (§15).

**Provider support (canonical):** Arcanum currently supports OpenAI-compatible HTTP providers only. Ollama is supported through its OpenAI-compatible `/v1` endpoint when configured as `type: "OpenAICompatible"`. Arcanum-managed local inference is removed: no managed local provider kind, no local inference process lifecycle, no local weight-file downloads/cache, no local-model management UI, and no dedicated local-model HTTP or CLI control plane.

---

## 2. Architectural goals

| Goal | Rationale |
|------|-----------|
| **Strict project boundaries** | Keeps compile-time dependencies honest, enables parallel ownership, and avoids the "everything references everything" failure mode. |
| **Hybrid process model** | One binary reduces deployment and versioning surface; operators choose mode via CLI verbs. |
| **Native AOT readiness for the host** | Eliminates the .NET runtime prerequisite so the CLI ships as a self-contained native binary. Secondary benefits: predictable startup and a smaller attack surface from reflection-heavy stacks — balanced against ecosystem limitations (§9). |
| **Minimal API over MVC** | Fewer moving parts, explicit endpoint mapping, and alignment with ASP.NET Core's AOT-oriented request pipeline. |
| **Source-generated JSON and request delegates** | Required for credible trimming and Native AOT compatibility; avoids runtime reflection. |

### 2.2 Remediation architectural gate (deferred policy changes)

The following items require explicit product/architecture sign-off before implementation. Completed findings remain documented in owning sections (env scrubbing §11.7 / MCP host; watermarks §5.5.5).

| Finding | Topic | Current decision |
|---------|-------|------------------|
| **#50** | Generate runtime `.sql` + `MigrationOrder` from EF migrations (Approach B) | **Deferred.** Hand-authored SQL scripts + transactional migrator (Approach A). |
| **#10** | OpenAI `/v1` default tool policy for workspace-less requests | **Deferred.** Exposure unchanged until product decides agentic-by-default vs allowlist. |
| **Audit #2** | Windows FS jail for `execute_command` / `run_spell_script` | **Deferred.** Degraded posture; Sanctum path-boundary denies these tools when required. |
| **Audit #12** | Global concurrency for on-demand daemon runs | **Deferred.** `Daemon:MaxConcurrentJobs` is the scheduled Unseen Servant cap only. |
| **Audit #20** | Default-deny network in macOS tool-child jail | **Deferred.** Jail is filesystem-only; network intentionally allowed. |
| **Audit #21** | Full per-binary/per-var child env allowlist | **Deferred.** Partial scrubbing shipped; full allowlist remains open. |
| **Audit #22** | Filesystem-jail external MCP stdio servers | **Deferred.** External MCP servers remain trusted operator-configured processes. |
| **Audit #24** | Require non-empty A2A `AllowedRemoteAgents` | **Deferred.** Empty allowlist + SSRF guard still permits public HTTPS. |
| **Audit #25** | Comm Link webhook HMAC signing | **Deferred.** New secret/config surface; needs Compendium when approved. |
| **Audit #29** | Bind HITL answers to session/stream identity | **Deferred.** PromptId remains the single-user ownership capability. |
| **Audit #31** | First-run `serve` key stdout vs stderr | **Deferred.** Explicit onboarding contract; auto-launch already suppresses raw print. |
| **Audit #33** | Redact CLI tool-result dumps | **Deferred.** Authenticated native clients intentionally receive diagnostics (subject to output caps). |
| **Audit #34** | Private-network provider probe policy | **Deferred.** Loopback/LAN providers (e.g. Ollama) intentionally supported. |
| **Audit readiness** | Providers Unhealthy → overall HTTP 503 | **Deferred.** Providers Unhealthy contributes at most overall Degraded (HTTP 200); Grimoire remains the readiness-critical gate for 503. |

### 2.1 Naming conventions

See [Arcanum.README.md §Naming metaphor](Arcanum.README.md#naming-metaphor) for the complete metaphor. DESIGN.md uses the thematic names throughout.

---

## 3. Repository and solution layout

### 3.1 `src/` per project

Projects live under `src/` rather than the repository root for shorter CI paths, room for future top-level folders (`build/`, `docs/`, `test/`, `tools/`), and alignment with common monorepo conventions.

### 3.2 `Directory.Build.props`

Shared MSBuild: `TargetFramework` `net10.0`, `Nullable`/`ImplicitUsings` enable, `LangVersion` latest, `<Version>0.1.0-beta</Version>`. Solution-wide **`Microsoft.Bcl.Memory`** (**10.0.8**) overrides vulnerable transitive versions (**CVE-2026-26127** via **`Microsoft.ML.Tokenizers.Data.O200kBase`** netstandard2.0 shims — not Native AOT). Per-project `.csproj` files hold what differs.


### 3.3 Package versions

`Microsoft.Bcl.Memory` pinned once in §3.2. Other first-party `Microsoft.*` packages pin in individual `.csproj` files (currently **10.0.8**; `Microsoft.Extensions.AI*` **10.8.1**). Ollama via OpenAI-compatible `/v1` — no `OllamaSharp`. Tokenizers packages **2.0.0** still need the Bcl.Memory override until upstream updates.


### 3.4 Configuration reference (`ArcanumSettings`)

Operator-facing settings bind under the `Arcanum` JSON object in `arcanum.json` (see `README.md`). The config file lives alongside the Grimoire in `ArcanumPaths.GrimoireDirectory` (`~/.config/arcanum/` on macOS and Linux, `%USERPROFILE%\.config\arcanum\` on Windows). Environment variables use prefix `ARCANUM_` with nested `__` segments.

> **Compendium** is the visual editor for this table — every row below maps 1:1 to a `SettingDescriptor` row in `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs`, which drives the form controls, descriptions, clamp bounds, and enum dropdowns. See §4.6 and [`docs/Compendium.README.md`](Compendium.README.md). `SettingDescriptorParityTests` and `SettingDescriptorCoverageTests` guard against drift between this table and the editor.

| Configuration path | Type | Default | Clamp | Purpose |
|--------------------|------|---------|-------|---------|
| `Arcanum:Host:Port` | `int` | `5001` | 1 – 65,535 | Kestrel HTTP listen port. |
| `Arcanum:Host:Https:Enabled` | `bool` | `false` | — | Loopback: when `true`, Kestrel adds an HTTPS listener alongside HTTP. |
| `Arcanum:Host:Https:Port` | `int` | `5443` | 1 – 65,535 | TLS listen port; must differ from `Host:Port`. |
| `Arcanum:Host:Https:CertificatePath` | `string?` | `null` | — | PFX path when `PrivateKeyPath` is empty; PEM certificate path when `PrivateKeyPath` is set. |
| `Arcanum:Host:Https:PrivateKeyPath` | `string?` | `null` | — | Optional PEM private key. |
| `Arcanum:Host:Https:CertificatePassword` | `string?` | `null` | — | Optional PFX password (passwordless PFX allowed). |
| `Arcanum:Host:RetainedLogFileCount` | `int` | `7` | 1 – 366 | Serilog rolling file retention (days). |
| `Arcanum:Host:EnableEnterpriseTelemetry` | `bool` | `false` | — | When `true`, Serilog adds a console sink with `CompactJsonFormatter` (structured JSON for log ingestion). |
| `Arcanum:Host:CorsAllowedOrigins` | `string[]` | localhost loopback (`5001`, `3000`) | — | Origins allowed by the **`ArcanumCors`** policy. |
| `Arcanum:Host:EnableScalarUi` | `bool` | `false` | — | Mounts **`MapScalarApiReference`** under **`/api/scalar`**. |
| `Arcanum:Host:SystemFingerprint` | `string?` | `null` | — | Optional override for the **`system_fingerprint`** field returned by `/v1/chat/completions`. |
| `Arcanum:Host:Workspace` | `string?` | `null` | — | Default workspace root for spell management routes (`/api/spells`) when `?workspace=` is omitted (`SpellWorkspaceResolver`; §8.14). |
| `Arcanum:Host:ListenAny` | `bool` | `false` | — | When `true` (or `ARCANUM_HOST_ANY`), Kestrel binds **HTTPS-only** via `ListenAnyIP` on `Host:Https:Port`. |
| `Arcanum:Host:MaxRequestBodyBytes` | `long` | `10485760` (10 MiB) | 256 KiB – 1 GiB | Kestrel `MaxRequestBodySize`. |
| `Arcanum:Host:RateLimit:Enabled` | `bool` | `false` | — | When `true`, `AddArcanumApiServices` registers `AddRateLimiter` and `ServeCommand`/DevHost call `UseRateLimiter()`. |
| `Arcanum:Host:RateLimit:PermitLimit` | `int` | `120` | 1 – 1,000,000 | Requests permitted per partition per window. |
| `Arcanum:Host:RateLimit:WindowSeconds` | `int` | `60` | 1 – 86,400 | Fixed window length (seconds). |
| `Arcanum:Host:RateLimit:QueueLimit` | `int` | `0` | 0 – 1,000,000 | Maximum queued requests per partition. |
| `Arcanum:Host:AuditLog:Enabled` | `bool` | `false` | — | Master toggle for the persisted inference audit log. No file I/O when `false`. |
| `Arcanum:Host:AuditLog:FilePath` | `string` | `~/.config/arcanum/audit.jsonl` | — | Base path; directory + filename stem are combined with a UTC date to produce each day's `{stem}-{yyyyMMdd}.jsonl` file. |
| `Arcanum:Host:AuditLog:MaxSizeMb` | `int` | `100` | 10 – 1,000 | Soft per-day-file size cap; further writes for that day are dropped (logged once) once reached. |
| `Arcanum:Host:AuditLog:RetentionDays` | `int` | `7` | 1 – 365 | Dated files older than this are deleted the first time a new UTC day's file is created. |
| `Arcanum:Host:AuditLog:RedactToolArguments` | `bool` | `true` | — | When `true`, only tool *names* are captured (never arguments). |
| `Arcanum:Server:PidFilePath` | `string?` | `~/.config/arcanum/arcanum.pid` | — | PID file written on host start, removed on graceful shutdown when it still contains this process's PID. |
| `Arcanum:Edition` | `string` / enum | `Local` | — | Runtime edition (`Local` default or `Development`). Resolves once from `Arcanum:Edition` / `ARCANUM_EDITION` (ADR 0001). Local does not advertise/invoke host-process tools unless Development + `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1`. |
| `Arcanum:DefaultModel` | `string?` | `null` | — | When non-empty, must match a `models` entry on some provider (see `ProviderResolver`); used when `PingRequest.Model` is omitted. |
| `Arcanum:FastModel` | `string?` | `null` | — | When non-empty, must match a `models` entry on some provider. |
| `Arcanum:Providers` | array | `[]` | element `contextWindowLimit` 256 – 2,097,152 | Multi-provider hub. Model entries may be legacy strings or objects with `name`, `supportsVision`, and optional `reasoning`. |
| `Arcanum:Providers[].Models[].Reasoning:ControlSupport` | enum | `none` | — | Closed values: `none`, `effort`, `budget`, `effortAndBudget`. The last value declares both alternatives; one request still cannot send effort and budget together. |
| `Arcanum:Providers[].Models[].Reasoning:SupportsSummary` | `bool` | `false` | — | Provider/model can return a client-safe reasoning summary. |
| `Arcanum:Providers[].Models[].Reasoning:SupportsFull` | `bool` | `false` | — | Provider/model can return client-safe full reasoning. This never authorizes protected reasoning disclosure. |
| `Arcanum:Providers[].Models[].Reasoning:SupportsStreaming` | `bool` | `false` | — | Client-safe reasoning may be projected incrementally on streaming calls. |
| `Arcanum:Providers[].Models[].Reasoning:ReportsReasoningTokens` | `bool` | `false` | — | Provider reports reasoning-token usage as a completion-token subset. |
| `Arcanum:Providers[].Models[].Reasoning:AllowsClientOutput` | `bool` | `false` | — | Explicit permission to project client-safe summary/full reasoning. |
| `Arcanum:Providers[].Models[].Reasoning:WireDialect` | enum | `standard` | — | Closed values: `standard`, `openRouter`, `topLevelReasoningBudget`, `anthropicThinking`; configured explicitly, never inferred from names. |
| `Arcanum:Providers[].Models[].Reasoning:MaxBudgetTokens` | `int?` | `null` | 1 – 2,097,152 | Optional model-specific ceiling for numeric reasoning budgets; valid only with `budget` or `effortAndBudget`. |
| `Arcanum:Conclave:Enabled` | `bool` | `false` | — | Enables **The Conclave**: the cross-Apprentice delegation surface. |
| `Arcanum:Conclave:MaxDelegationDepth` | `int` | `3` | 0 – 20 | Maximum delegation depth from a Conclave root Apprentice (0 = root only, no children). |
| `Arcanum:Conclave:MaxDescendantsPerRoot` | `int` | `16` | 1 – 200 | Maximum total descendant Apprentices allowed under one Conclave root (breadth cap). |
| `Arcanum:Conclave:A2A:Enabled` | `bool` | `false` | — | Master toggle for the A2A (Agent-to-Agent) protocol surface |
| `Arcanum:Conclave:A2A:ServerEnabled` | `bool` | `false` | — | When `true` (and `A2A:Enabled`), exposes Arcanum Apprentices as an A2A server: external agents send messages that spawn headless Apprentices, mapped under `A2A:ServerPath`. |
| `Arcanum:Conclave:A2A:ServerPath` | `string` | `/api/conclave/a2a` | — | HTTP path under which the A2A JSON-RPC endpoints and the authenticated Agent Card (`{ServerPath}/agent-card`) are mapped, inside the `/api` route group (`ApiKeyEndpointFilter` applies). |
| `Arcanum:Conclave:A2A:AgentCardName` | `string?` | `null` | — | Display name advertised on the A2A Agent Card ("Heraldry"). |
| `Arcanum:Conclave:A2A:AgentCardDescription` | `string?` | `null` | — | Display description advertised on the A2A Agent Card ("Heraldry"). |
| `Arcanum:Conclave:A2A:ClientEnabled` | `bool` | `false` | — | When `true` (and `A2A:Enabled`), advertises the in-process `dispatch_sending` MCP tool so an Apprentice may delegate a Sending to an external A2A agent (the Archmage Client). |
| `Arcanum:Conclave:A2A:MaxExternalTasks` | `int` | `50` | 1 – 500 | Maximum concurrently in-flight client-side (`dispatch_sending`) delegations, enforced by an in-memory semaphore (not a persisted counter — external tasks are not written to the Grimoire). |
| `Arcanum:Conclave:A2A:ExternalTaskTimeoutMinutes` | `int` | `60` | 5 – 1,440 | Per-delegation wall-clock timeout for a blocking `dispatch_sending` call. |
| `Arcanum:Conclave:A2A:AllowedRemoteAgents` | `string[]` | `[]` | — | Optional allowlist of remote Agent Card URLs/origins `dispatch_sending` may target. |
| `Arcanum:Conclave:A2A:DefaultWorkspace` | `string` | `""` | — | Fallback workspace for inbound A2A tasks (server side) when the request carries no workspace/campaign hint. |
| `Arcanum:Intelligence:ExecuteCommandTimeoutSeconds` | `int` | `30` | 1 – 600 | Hard wall-clock cap for MCP `execute_command` and `run_spell_script` (runtime-coupled to `Mcp:RequestTimeoutSeconds`); cooperative cancel also terminates spawned process trees immediately, independent of this timeout. |
| `Arcanum:Intelligence:InferenceTimeoutSeconds` | `int` | `600` | 5 – 3,600 | Wall-clock cap for a single inference turn (buffered or streaming), including tool rounds. |
| `Arcanum:Intelligence:ToolOutputCapBytes` | `long` | `1048576` (1 MiB) | 64 KiB – 64 MiB | Combined byte cap on stdout + stderr captured from `execute_command` and `run_spell_script` (split evenly per stream). |
| `Arcanum:Intelligence:MaxToolInferenceRounds` | `int` | `8` | 1 – 100 | Hard cap on agentic tool rounds per inference turn (`TurnLimitsDefaults.MaxToolRounds`). |
| `Arcanum:Intelligence:ReservedOutputTokens` | `int` | `1024` | 0 – 128,000 | Tokens reserved for model output during per-call context preflight when the request omits `MaxOutputTokens`. |
| `Arcanum:Intelligence:DisconnectPolicy` | enum | `Auto` | — | Client disconnect policy (ADR 0003): `CancelAbandoned`, `ContinueThenReplay`, or `Auto` (continue when `Idempotency-Key` present, else cancel). |
| `Arcanum:Intelligence:TolerateToolFailures` | `bool` | `true` | — | When `true`, an unexpected exception from a single tool invocation during a **buffered** turn is caught and synthesized into a tool result instead of failing the whole turn with `Hub.Error`. Streaming always tolerates tool invocation failures (mode policy; ADR 0004). |
| `Arcanum:Intelligence:CompressionPreflightMinMessages` | `int` | `6` | 0 – 100 | Minimum assembled-message count before context-compression preflight runs (short threads skip tokenizer cost). |
| `Arcanum:Intelligence:PerMessageTemplateOverheadTokens` | `int` | `4` | 0 – 32 | Per-message overhead (tokens) added to the pre-flight count to approximate chat-template framing. |
| `Arcanum:Intelligence:TokenizerEncoding` | `string` | `"o200k_base"` | — | Tiktoken encoding name used by `InferenceTokenizerResolver`. |
| `Arcanum:Intelligence:SemanticRouterPreflightTimeoutSeconds` | `int` | `15` | 1 – 600 | Max wait for spell-router preflight call. |
| `Arcanum:Intelligence:SemanticRouterMaxTokens` | `int` | `128` | 1 – 4,096 | Spell-router preflight `MaxOutputTokens`. |
| `Arcanum:Intelligence:SemanticRouterTemperature` | `float` | `0` | 0 – 2 | Spell-router preflight temperature. |
| `Arcanum:Intelligence:ListDirectoryMaxPaths` | `int` | `500` | 1 – 2,000 | Max paths from in-process `list_directory`. |
| `Arcanum:Intelligence:EnableLoreSystem` | `bool` | `true` | — | **Legacy / operator-only.** No longer gates any MCP tool — the Lore MCP tools are removed. |
| `Arcanum:Intelligence:EnableLexiconSystem` | `bool` | `true` | — | Gates `scribe_lexicon` / `delete_lexicon` and Lexicon DATA injection. Operators who previously set `EnableLoreSystem: false` must set this to `false`. |
| `Arcanum:Intelligence:LexiconMaxMatchedEntries` | `int` | `16` | 1 – 100 | Max Lexicon entries returned per inference-turn `MatchEntitiesAsync`. |
| `Arcanum:Intelligence:LexiconMaxInjectedBytes` | `int` | `4096` | 256 – 65,536 | Hard cap (bytes) on the rendered `### Lexicon (Known Context)` DATA block. |
| `Arcanum:Intelligence:EnableArchiveSearch` | `bool` | `true` | — | Gates `search_archives` MCP tool. |
| `Arcanum:Intelligence:ArchiveSearchMaxResults` | `int` | `5` | 1 – 100 | Max rows per `search_archives` call. |
| `Arcanum:Intelligence:ArchiveSearchMaxQueryLength` | `int` | `512` | 32 – 4,096 | Max query length before FTS sanitization. |
| `Arcanum:Intelligence:CampaignLogThreshold` | `int` | `25` | 1 – 10,000 | Message-count safety valve for Campaign Log consolidation. |
| `Arcanum:Intelligence:CampaignLogIdleTimeoutMinutes` | `int` | `240` | 1 – 43,200 | Idle minutes before a session is eligible for consolidation. |
| `Arcanum:Intelligence:CampaignLogSweepIntervalMinutes` | `int` | `15` | 1 – 1,440 | Background sweep interval for Campaign Log enqueue. |
| `Arcanum:Intelligence:ContextWindowCompressionThreshold` | `int` | `85` | 50 – 100 | Percentage of the resolved provider `contextWindowLimit` at which **read-time** context compression is considered. |
| `Arcanum:Intelligence:EnableContextCompression` | `bool` | `true` | — | When `true`, `WizardIntelligenceProvider` runs pre-flight token counting and may swap older Grimoire entries for `Session.Summary` in the assembled system prompt without deleting rows. |
| `Arcanum:Intelligence:EnableTokenTracking` | `bool` | `true` | — | When `true`, after each successful buffered or streamed inference turn with a bound `SessionId`, the hub calls **`IGrimoireRepository.IncrementSessionTokensAsync`** so **`Session.TotalTokensUsed`** reflects cumulative reported usage. |
| `Arcanum:Intelligence:UseFastModelForSpellRouting` | `bool` | `false` | — | When `true`, semantic spell-router preflight uses **`Arcanum:FastModel`** (when configured) instead of the turn's model; falls back to the turn model otherwise. |
| `Arcanum:Intelligence:MaxOpenApiMessages` | `int` | `1000` | 1 – 10,000 | Maximum messages accepted in a single OpenAI `/v1/chat/completions` request before rejection. |
| `Arcanum:Intelligence:MaxStatelessMessages` | `int` | `100` | 1 – 10,000 | Maximum messages accepted on a stateless (no-session) native inference request. |
| `Arcanum:Intelligence:MaxContentPartsPerMessage` | `int` | `64` | 1 – 1,024 | Maximum multimodal `content[]` parts per `/v1/chat/completions` message; exceeding it (or an unsupported part `type`) is rejected `400 invalid_value` before mapping. |
| `Arcanum:Intelligence:MaxPingPromptChars` | `int` | `32768` | 1 – 262,144 | Maximum prompt length (chars) for `POST /api/intelligence/ping(-stream)`; also bounds `AdditionalSystemPrompt`. |
| `Arcanum:Intelligence:MaxPlanSteps` | `int` | `30` | 1 – 200 | Maximum steps accepted in a parsed Apprentice plan. |
| `Arcanum:Mcp:RequestTimeoutSeconds` | `int` | `60` | 1 – 600 | Default per-request timeout for `McpClient` JSON-RPC. |
| `Arcanum:Mcp:MaxPaginationPages` | `int` | `32` | 1 – 256 | Max `tools/list` pagination iterations. |
| `Arcanum:Mcp:BootstrapBlocksStartup` | `bool` | `true` | — | When `true` (default), AlwaysOn MCP servers finish bootstrapping before Kestrel accepts requests. |
| `Arcanum:Mcp:MaxServers` | `int` | `50` | 1 – 500 | Maximum MCP servers registered across user + workspace `mcp.json`. |
| `Arcanum:Mcp:MaxToolsPerServer` | `int` | `256` | 1 – 2,048 | Maximum tools accepted from a single MCP server's `tools/list`. |
| `Arcanum:Mcp:MaxToolsPerListPage` | `int` | `64` | 1 – 256 | Maximum tools accepted per `tools/list` page. |
| `Arcanum:Mcp:MaxToolsTotalBytes` | `int` | `1048576` (1 MiB) | 64 KiB – 16 MiB | Maximum cumulative bytes of tool schemas held in memory across all servers. |
| `Arcanum:Mcp:MaxJsonRpcLineBytes` | `int` | `2228224` | 64 KiB – 8 MiB | Maximum length of a single newline-delimited JSON-RPC frame (also caps each Streamable HTTP JSON body / SSE event). |
| `Arcanum:Mcp:HttpRequestTimeoutSeconds` | `int` | `120` | 10 – 600 | Timeout for the named `HttpClient("McpHttp")` Streamable HTTP transport (headers phase; the per-request JSON-RPC timeout governs streamed bodies). |
| `Arcanum:Mcp:AllowedHttpHosts` | `string[]` | `[]` | — | Hosts permitted over plaintext `http` for Streamable HTTP MCP servers. |
| `Arcanum:Perception:MaxEnumerationSteps` | `int` | `50000` | 1 – 10,000,000 | File walk budget for Eye of the World. |
| `Arcanum:Perception:MaxTableOfContentsLines` | `int` | `20` | 1 – 500 | TOC line budget for `PatternSnapshot`. |
| `Arcanum:Perception:AllowedWorkspaceRoots` | `string[]` | `[]` | — | Allowlist of absolute roots that `GET /api/perception/look` may scan. |
| `Arcanum:Spells:AllowedWorkspaceRoots` | `string[]` | `[]` | — | Allowlist of absolute roots for spell CRUD routes (`/api/spells`). |
| `Arcanum:Spells:MaxFileSizeBytes` | `long` | `262144` (256 KiB) | 1 KiB – 1 MiB | Maximum `SPELL.md` / frontmatter read size for spell list, get, search, and execute routes. |
| `Arcanum:Spells:MetadataScanCacheTtlSeconds` | `int` | `5` | 0 – 300 | TTL for the in-process spell-metadata scan cache used by routing and Arcane Resonance; `0` disables. |
| `Arcanum:Spells:MaxDependencies` | `int` | `20` | 0 – 100 | Maximum `dependencies` entries accepted in a spell's `SPELL.json` (Arcane Resonance graph). |
| `Arcanum:Spells:MaxDeclaredTools` | `int` | `50` | 0 – 256 | Maximum `declaredTools` entries in a spell's `SPELL.json` (Artifact Attunement allowlist). |
| `Arcanum:Spells:MaxResonantDependencies` | `int` | `10` | 0 – 50 | Maximum resonant dependencies resolved into the system prompt at execution. |
| `Arcanum:Spells:MaxResonantBytes` | `int` | `131072` (128 KiB) | 4 KiB – 1 MiB | Maximum total bytes of concatenated resonant dependency bodies. |
| `Arcanum:Campaigns:AllowedRoots` | `string[]` | `[]` | — | Allowlist of absolute roots for **`POST /api/campaigns`** and **`POST /api/workspaces`**. |
| `Arcanum:Campaigns:MaxCampaigns` | `int` | `500` | 10 – 10,000 | Maximum registered campaigns in the Grimoire database |
| `Arcanum:Cli:DoctorHealthTimeoutSeconds` | `int` | `2` | 1 – 60 | Timeout (seconds) for the `arcanum doctor` API health probe (`GET /api/health`). |
| `Arcanum:Cli:ApiRequestTimeoutSeconds` | `int` | `60` | 1 – 600 | Timeout (seconds) for non-streaming CLI API calls (`lore`, `daemon jobs`, session queries, etc.). |
| `Arcanum:Cli:MaxAttachFileSizeBytes` | `long` | `1048576` | 1 KiB – 100 MiB | Per-file staging limit for `chat /attach`. |
| `Arcanum:Cli:MaxAttachedFilesPerRequest` | `int` | `32` | 1 – 256 | Max attached files per inference request. |
| `Arcanum:Cli:MaxAttachedFileRelativePathChars` | `int` | `4096` | 256 – 8,192 | Max `RelativePath` length per attachment. |
| `Arcanum:Cli:Theme` | `ArcanumTheme` | `SystemDefault` | — | CLI appearance: `Light`, `Dark`, or `SystemDefault` (uses `IThemeDetector` once at process start). |
| `Arcanum:Cli:ThemeColors` | object | Core defaults | — | Nested `Light` / `Dark`, each with `Text`, `Heading`, `Highlight`, `Error`, `Muted` as `#RRGGBB` strings (Spectre palette is built in **Cli**). |
| `Arcanum:Cli:ShowManaBar` | `bool` | `true` | — | When `true`, the **`chat`** REPL prints the context-window mana bar before each prompt (when a model resolves). |
| `Arcanum:Security:MaxApiKeyHeaderUtf16Chars` | `int` | `512` | 128 – 8,192 | Rejects oversized API key headers before UTF-8 conversion. |
| `Arcanum:Security:ApiKeyCacheTtlSeconds` | `int` | `30` | 1 – 3,600 | TTL for the cached **SHA-256 digest** of the expected API key in `ApiKeyEndpointFilter`. |
| `Arcanum:Daemon:Jobs` | array | `[]` | per-job `intervalMinutes` 1 – 10,080 | Unseen Servant background jobs. |
| `Arcanum:Daemon:MaxConcurrentJobs` | `int` | `8` | 1 – 1,024 | Hard concurrency cap on Unseen Servant jobs the scheduler dispatches per minute; excess jobs defer. |
| `Arcanum:Daemon:ShutdownDrainTimeoutSeconds` | `int` | `10` | 0 – 600 | Time (seconds) `StopAsync` waits for in-flight Unseen Servant jobs (`Task` registry) to drain after the host begins shutting down. |
| `Arcanum:Daemon:ExecutionHistoryLimit` | `int` | `100` | 10 – 10,000 | Maximum in-memory execution records retained per daemon job in `InMemoryDaemonExecutionRepository`. |
| `Arcanum:CommLink:WebhookUrl` | `string?` | `null` | — | Optional absolute URL for **Comm Link** outbound JSON `POST` alerts (`WebhookCommLinkDispatcher`). |
| `Arcanum:CommLink:WebhookTimeoutSeconds` | `int` | `15` | 1 – 120 | Timeout (seconds) configured on the named `HttpClient("CommLinkWebhook")`. |
| `Arcanum:CommLink:AllowedSchemes` | `string[]` | `["https"]` | — | URI schemes the webhook dispatcher is allowed to call. |
| `Arcanum:CommLink:AllowedHosts` | `string[]` | `[]` | — | Optional allowlist of webhook hosts (e.g. `hooks.example.com`). |
| `Arcanum:Grimoire:MaxMessagesPerConversationLoad` | `int` | `1000` | 50 – 5,000 | Target size of the most-recent entry window `GetSessionAsync` loads (server-side, chronological order). |
| `Arcanum:Grimoire:WorkspaceContextRetentionCount` | `int` | `10` | 1 – 1,000 | Number of Chronosync `WorkspaceContext` snapshots retained per workspace path; older rows are purged after each new baseline. |
| `Arcanum:Grimoire:DefaultLoreListLimit` | `int` | `100` | 1 – 10,000 | Default page size for `GET /api/lore` when `limit` is omitted. |
| `Arcanum:EventBus:ChannelCapacity` | `int` | `256` | 64 – 65,536 | Per-subscriber bounded channel capacity for the in-memory SSE event bus (`IEventBus`). |
| `Arcanum:EventBus:HeartbeatSeconds` | `int` | `30` | 0 – 300 | SSE keep-alive comment interval for `/api/events/*`, session stream, and Chronicle (`0` disables). |
| `Arcanum:EventBus:MaxSseConnections` | `int` | `50` | 1 – 100 | Global cap on concurrent SSE connections across all streams; excess returns `503` `Api.TooManyConnections` |
| `Arcanum:EventBus:MaxSseConnectionsPerType` | `int` | `20` | 1 – 50 | Per-event-type cap (daemon, MCP, logs, session, Chronicle) on concurrent SSE connections, enforced in addition to the global cap; guarantees each stream family a fair share of the pool so one greedy client cannot starve the others. |
| `Arcanum:Logs:RingBufferCapacity` | `int` | `10000` | 1,000 – 100,000 | In-memory log ring buffer capacity. |
| `Arcanum:Logs:MinLevelInBuffer` | `LogLevel` | `information` | — | Minimum Serilog level captured into the ring buffer (`trace`, `debug`, `information`, `warning`, `error`, `critical`). |
| `Arcanum:Workspaces:MaxFileReadSizeBytes` | `long` | `1048576` | 1 KiB – 10 MiB | Maximum file size (bytes) for **`GET /api/workspaces/{id}/files/contents`** |
| `Arcanum:Workspaces:ListDirectoryMaxDepth` | `int` | `64` | 1 – 256 | Maximum directory depth for recursive workspace file listing (`GET /api/workspaces/{id}/files?recursive=true`). |
| `Arcanum:Workspaces:EnableFileWrite` | `bool` | `false` | — | Master toggle for the workspace file write/modify/delete surface (**`PUT`**/**`PATCH`**/**`DELETE .../files`**, **`POST .../files/directory`**). |
| `Arcanum:Workspaces:MaxFileWriteSizeBytes` | `long` | `1048576` | 1 KiB – 10 MiB | Maximum content size accepted by **`PUT /api/workspaces/{id}/files/contents`** (and the `newString` on **`PATCH .../files/contents`**) |
| `Arcanum:Workspaces:MaxReplaceTextBlockBytes` | `long` | `524288` | 1 KiB – 4 MiB | Maximum combined size of `oldString` + `newString` on **`PATCH /api/workspaces/{id}/files/contents`** |
| `Arcanum:Sessions:DefaultQueryLimit` | `int` | `100` | 1 – 10,000 | Default page size for **`GET /api/sessions`** |
| `Arcanum:Sessions:MaxStreamReplayEntries` | `int` | `500` | 1 – 10,000 | Maximum entries replayed on **`GET /api/sessions/{id}/stream`** connect (most recent N, ascending) |
| `Arcanum:Sessions:MaxForkDepth` | `int` | `3` | 0 – 20 | Maximum lineage depth for **`POST /api/sessions/{id}/fork`**; exceeding it returns `Session.ForkDepthExceeded` |
| `Arcanum:Sessions:AllowMemoryManagement` | `bool` | `false` | — | Master gate for session memory-management endpoints. |
| `Arcanum:Sessions:MaxPinnedEntries` | `int` | `10` | 0 – 100 | Maximum pinned entries per session. |
| `Arcanum:Security:IdempotencyTtlHours` | `int` | `24` | 1 – 168 | How long a cached `Idempotency-Key` response is replayed before it is treated as expired |
| `Arcanum:Security:IdempotencyMaxResponseBytes` | `int` | `10,485,760` (10 MiB) | 1 MiB – 100 MiB | Maximum buffered response size cached for an `Idempotency-Key` request; larger responses still stream fully to the client but are never cached |
| `Arcanum:Security:AllowUnsandboxedToolChildren` | `bool` | `false` | — | When `false`, tool children require OS FS jail where active (macOS Seatbelt); Linux Landlock inactive this beta (fail-closed unless escape hatch); Windows no FS jail (health Degraded). Sanctum path-boundary still denies these tools. Surfaced by `doctor` / health `ToolChildSandbox`. |
| `Arcanum:Moderations` | — | — | — | **Obsolete key** — if present in `arcanum.json`, startup fails. `POST /v1/moderations` always returns **501** `not_supported`. |
| `Arcanum:Files:MaxUploadSizeBytes` | `long` | `536,870,912` (512 MiB) | 1 MiB – 10 GiB | Maximum upload size for **`POST /v1/files`**; exceeding it returns **413** `Files.TooLarge` |
| `Arcanum:Files:AllowedMimeTypes` | `string[]` | `[]` (all allowed) | — | Optional operator allow-list of declared upload `Content-Type` values for **`POST /v1/files`** |
| `Arcanum:Batches:MaxConcurrentBatches` | `int` | `3` | 1 – 20 | Maximum `/v1/batches` processed concurrently across the whole server |
| `Arcanum:Batches:MaxRequestsPerBatch` | `int` | `50,000` | 1 – 1,000,000 | Maximum JSONL request lines accepted from a single batch input file |
| `Arcanum:Batches:BatchExpiryHours` | `int` | `24` | 1 – 168 | How long a non-terminal batch is allowed to run before being force-expired |
| `Arcanum:Batches:MaxConcurrentRequestsPerBatch` | `int` | `1` | 1 – 10 | Maximum chat-completion requests run concurrently within a single batch |
| `Arcanum:Sessions:MaxEntriesPerSession` | `int` | `100000` | 100 – 1,000,000 | Maximum entries appended to one session before rejection. |
| `Arcanum:Sessions:MaxEntryContentBytes` | `int` | `1048576` (1 MiB) | 1 KiB – 16 MiB | Maximum content bytes per entry; also caps stateless `/v1` and ping message content, and **`POST /api/intelligence/human-response` answer** UTF-8 size (rejected with `Validation.InvalidBody` before `TrySubmitResponse`). |
| `Arcanum:Ward:Enabled` | `bool` | `true` | — | When `true`, **Forbidden Arts** (high-risk tool calls) are gated behind an operator-resolvable ward before execution |
| `Arcanum:Ward:ForbiddenArts` | `string[]` | `execute_command`, `write_file`, `replace_text_block`, `delete_lexicon`, `run_spell_script` | — | Tool names that require ward resolution when `Enabled` is `true`. |
| `Arcanum:Ward:TimeoutSeconds` | `int` | `120` | 10 – 600 | Max seconds an active ward waits for operator resolution before auto-denying. |
| `Arcanum:Ward:MaxActiveWards` | `int` | `50` | 1 – 500 | Maximum simultaneously-pending wards before new Forbidden Art requests are auto-denied. |
| `Arcanum:Ward:AutoDenyInUnattendedMode` | `bool` | `true` | — | When `true` and `PingRequest.UnattendedMode` is `true`, Forbidden Arts are denied immediately without placing a ward (prevents daemon jobs from hanging). |
| `Arcanum:Ward:UnattendedMode` | `bool` | `false` | — | Default for operator-facing surfaces (Command Center; `ask`/`chat` without `--unattended`). |
| `Arcanum:Apprentices:Enabled` | `bool` | `true` | — | When `false`, **`ApprenticeService`** does not start or resume Apprentices |
| `Arcanum:Apprentices:MaxConcurrentApprentices` | `int` | `5` | 1 – 50 | Maximum Apprentices executing concurrently. |
| `Arcanum:Apprentices:StepTimeoutMinutes` | `int` | `30` | 5 – 120 | Per-step execution timeout for **`StreamPromptAsync`**. |
| `Arcanum:Apprentices:ChronicleChannelCapacity` | `int` | `1000` | 100 – 10,000 | Bounded **`ChronicleHub`** channel capacity per Apprentice. |
| `Arcanum:Apprentices:MaxStepRetries` | `int` | `2` | 0 – 10 | **Second Wind:** maximum retry attempts per step before escalation or failure. |
| `Arcanum:Apprentices:RetryBackoffSeconds` | `int` | `5` | 1 – 300 | Base delay (seconds) for exponential backoff between step retries. |
| `Arcanum:Apprentices:RetryBackoffMaxSeconds` | `int` | `60` | 1 – 3,600 | Maximum backoff delay (seconds) between step retries. |
| `Arcanum:Apprentices:EnableShiftingFate` | `bool` | `true` | — | When `true`, the **Wizard** evaluates each completed step and may rewrite the pending plan tail (**Shifting Fate**; §5.7). |
| `Arcanum:Apprentices:EnableDivineIntervention` | `bool` | `true` | — | When `true`, exhausted retries or `petition_dungeon_master` transition the Apprentice to **`Escalated`** instead of **`Failed`** |
| `Arcanum:Apprentices:MaxSimulacra` | `int` | `3` | 1 – 10 | **Simulacrum:** maximum plan steps flagged `isParallel` executed concurrently within one Apprentice. |
| `Arcanum:Apprentices:MaxRunSteps` | `int` | `100` | 1 – 500 | Per-run cap on steps executed in a single **`RunApprenticeAsync`** invocation (counts completed steps in that invocation, including Simulacrum groups). |
| `Arcanum:Apprentices:MaxRunDurationMinutes` | `int` | `480` | 5 – 10,080 | Per-run wall-clock budget (minutes) for a single execution invocation. |
| `Arcanum:Apprentices:MaxReweavesPerRun` | `int` | `10` | 0 – 100 | Maximum **Shifting Fate** re-weaves allowed per run invocation (`0` disables further automatic re-weaves after the budget is exhausted). |
| `Arcanum:Apprentices:MaxPendingStarts` | `int` | `100` | 1 – 1,000 | Bounded queue for Apprentices waiting on a concurrency slot when **`MaxConcurrentApprentices`** is saturated (`Apprentice.PendingQueueFull` when full). |
| `Arcanum:Codex:MaxSizeBytes` | `long` | `262144` (256 KiB) | 1 KiB – 1 MiB | Maximum `CODEX.md` content size for `PUT /api/codex` and `PUT /api/campaigns/{id}/codex`. |
| `Arcanum:ProvingGrounds:MaxInquisitorsPerTrial` | `int` | `20` | 1 – 200 | Maximum **Inquisitors** on a single **Trial** submitted to **The Proving Grounds** |
| `Arcanum:ProvingGrounds:SemanticJudgeMaxTokens` | `int` | `8` | 1 – 256 | Maximum completion tokens for a **Semantic Inquisitor** FastModel judge call |
| `Arcanum:ProvingGrounds:SemanticJudgeTimeoutSeconds` | `int` | `60` | 1 – 600 | Wall-clock timeout (seconds) for a Semantic Inquisitor judge inference call |
| `Arcanum:Prompts:MaxParameterValueChars` | `int` | `4096` | 256 – 65,536 | Maximum length (chars) of a single prompt parameter value on render/execute. |
| `Arcanum:Resilience:Enabled` | `bool` | `false` | — | When `true`, `ProviderHealthProbeService` starts periodic provider probing and `ProviderResolver.ResolveCandidates` / the hub's fallback loop become active. |
| `Arcanum:Resilience:HealthProbeIntervalSeconds` | `int` | `30` | 5 – 600 | Interval between health probes for providers currently considered healthy. |
| `Arcanum:Resilience:HealthRecoveryProbeIntervalSeconds` | `int` | `60` | 5 – 3,600 | Slower interval between health probes for providers currently marked unhealthy, to avoid hammering a down provider. |
| `Arcanum:Resilience:HealthFailureThreshold` | `int` | `3` | 1 – 100 | Consecutive probe or inference failures before a provider is marked Unhealthy and excluded from fallback candidates. |
| `Arcanum:Resilience:MaxFallbackAttempts` | `int` | `3` | 1 – 10 | Maximum candidate providers tried per inference turn before giving up. |
| `Arcanum:Resilience:HealthProbeTimeoutSeconds` | `int` | `5` | 1 – 30 | HTTP timeout for each individual health probe call (`GET /models` for OpenAI-compatible providers). |
| `Arcanum:Metrics:Enabled` | `bool` | `true` | — | When `true`, `GET /metrics` renders Prometheus text format; when `false`, the endpoint returns `404` |
| `Arcanum:Metrics:RequireApiKey` | `bool` | `true` | — | When `true` (default), `GET /metrics` is registered with `ApiKeyEndpointFilter` (`X-Arcanum-Key` or `Authorization: Bearer`). |
| `Arcanum:Embeddings:Enabled` | `bool` | `false` | — | Master toggle for RAG (**The Weave** and **Divination**; §21). |
| `Arcanum:Embeddings:Provider` | `string?` | `null` | — | Provider name (from `Arcanum:Providers`) used to imprint text into The Weave. |
| `Arcanum:Embeddings:Model` | `string?` | `null` | — | Embedding model advertised by the configured provider (e.g. `nomic-embed-text`). |
| `Arcanum:Embeddings:Dimensions` | `int` | `768` | 64 – 4,096 | Expected imprinted vector dimension; must match the model's output. |
| `Arcanum:Embeddings:BatchSize` | `int` | `32` | 1 – 256 | Maximum texts imprinted per embedding API call; batches are sent sequentially, not in parallel. |
| `Arcanum:Embeddings:ChunkSizeChars` | `int` | `1000` | 128 – 8,192 | Maximum characters per chunk when imprinting long documents (naive sliding window; §21.5). |
| `Arcanum:Embeddings:ChunkOverlapChars` | `int` | `100` | 0 – 1,024 | Overlap in characters between adjacent chunks. |
| `Arcanum:Embeddings:SimilarityThreshold` | `float` | `0.70` | 0.0 – 1.0 | Minimum cosine similarity for a Divination result to be included. |
| `Arcanum:Embeddings:MaxResults` | `int` | `5` | 1 – 50 | Default maximum results per Divination call; individual features may override. |
| `Arcanum:Embeddings:RequestTimeoutSeconds` | `int` | `30` | 5 – 300 | Timeout for a single embedding API call (enforced via a linked `CancellationTokenSource`, independent of provider-native timeout support). |
| `Arcanum:Embeddings:MaxEmbeddingInputChars` | `int` | `1,000,000` | 1,000 – 10,000,000 | Maximum total UTF-16 character count across all inputs in a single `POST /v1/embeddings` request; exceeding it returns **400** `invalid_request_error`/`invalid_value`. |
| `Arcanum:Embeddings:SessionSearchEnabled` | `bool` | `false` | — | Phase 2 feature flag: session semantic search (`EntryWeavingService` + `POST /api/sessions/divine`; §21.6). |
| `Arcanum:Embeddings:EmbeddingQueueIntervalSeconds` | `int` | `10` | 1 – 300 | Phase 2: interval between `EntryWeavingService` embedding queue processing ticks. |
| `Arcanum:Embeddings:CodebaseRetrievalEnabled` | `bool` | `false` | — | Phase 3 feature flag: semantic codebase retrieval (`WorkspaceIndexingService` + `POST /api/workspaces/{id}/files/divine`; §21.7). |
| `Arcanum:Embeddings:Codebase:MaxFilesToIndex` | `int` | `500` | 1 – 10,000 | Phase 3: maximum files embedded per workspace during a single indexing tick. |
| `Arcanum:Embeddings:Codebase:MaxFileSizeChars` | `int` | `50000` | 1,000 – 500,000 | Phase 3: files larger than this (characters) are skipped during indexing. |
| `Arcanum:Embeddings:Codebase:FileExtensions` | `string[]` | `[".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".md", ".txt", ".json", ".yaml", ".yml"]` | — | Phase 3: file extensions eligible for indexing (case-insensitive). |
| `Arcanum:Embeddings:Codebase:IndexingIntervalMinutes` | `int` | `60` | 5 – 1,440 | Phase 3: background re-indexing interval for workspaces with active inference. |
| `Arcanum:Embeddings:Codebase:MaxRetrievedChunks` | `int` | `5` | 1 – 50 | Phase 3: maximum file chunks injected into the system prompt per inference turn. |
| `Arcanum:Embeddings:SagaEnabled` | `bool` | `false` | — | Phase 4 feature flag: **Saga**, Arcanum's long-term associative memory (`SagaExtractionService` + `/api/saga/*` + `read_saga`; §21.8). |
| `Arcanum:Embeddings:Saga:ExtractionEnabled` | `bool` | `true` | — | Phase 4: when `SagaEnabled` is `true`, controls whether the background `SagaExtractionService` runs. |
| `Arcanum:Embeddings:Saga:MaxMemoriesPerSession` | `int` | `50` | 1 – 1,000 | Phase 4: maximum Saga memories associated with a single session. |
| `Arcanum:Embeddings:Saga:MaxMemoriesTotal` | `int` | `10000` | 100 – 1,000,000 | Phase 4: maximum total Saga memories across all sessions. |
| `Arcanum:Embeddings:Saga:ExtractionModel` | `string?` | `null` | — | Phase 4: model used for memory extraction. |
| `Arcanum:Embeddings:Saga:ExtractionMaxTokens` | `int` | `500` | 100 – 4,096 | Phase 4: maximum output tokens for the extraction LLM call. |
| `Arcanum:Embeddings:Saga:ExtractionIntervalMinutes` | `int` | `15` | 1 – 1,440 | Phase 4: interval, in minutes, `SagaExtractionService` is expected to process its extraction queue against — informational; the service itself is event-driven (enqueued after successful inference turns), not polling. |
| `Arcanum:Embeddings:Saga:ExtractionWindowEntries` | `int` | `10` | 2 – 50 | Phase 4: number of recent Grimoire entries reviewed per extraction call. |
| `Arcanum:Embeddings:SemanticSpellRoutingEnabled` | `bool` | `false` | — | Phase 5 feature flag: embedding-based spell routing pre-filter (`SemanticSpellRouter`; §21.9); when `false`, the existing LLM-based `SemanticRouter` is unchanged. |
| `Arcanum:Embeddings:SpellRoutingHybridMode` | `bool` | `false` | — | Phase 5: when `true` and `SemanticSpellRoutingEnabled` is also `true`, embedding similarity pre-filters the spell catalog to the top `SpellRoutingHybridTopK` candidates before the LLM-based `SemanticRouter` picks from. |
| `Arcanum:Embeddings:SpellRoutingHybridTopK` | `int` | `3` | 1 – 20 | Phase 5: number of top candidates passed to the LLM-based `SemanticRouter` in hybrid mode. |
| `Arcanum:Scrying:Enabled` | `bool` | `true` | — | **Scrying** (vision/multimodality) master kill-switch |
| `Arcanum:Scrying:MaxImageBytes` | `long` | `1048576` (1 MiB) | 1 KiB – 20 MiB | Maximum bytes per image, measured against the decoded `data:` URI payload (CLI Scrying foci and any inline base64 `image_url`). |
| `Arcanum:Scrying:MaxImagesPerRequest` | `int` | `10` | 1 – 100 | Maximum images per inference request (native `ScryingFoci` and `/v1` `image_url` parts combined). |
| `Arcanum:Scrying:AllowedMimeTypes` | `string[]` | `["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"]` | — | Allowed image MIME types. |
| `Arcanum:Attachments:Enabled` | `bool` | `true` | — | Master switch for session attachment persistence (host `ISessionAttachmentStore`; §10.2.5). |
| `Arcanum:Attachments:MaxReferencesPerTurn` | `int` | `8` | 1 – 32 | Combined per-turn budget for user `AttachmentReferences` + model `attach_session_file` injections. |
| `Arcanum:Attachments:MaxVersionsPerLogicalKey` | `int` | `20` | 1 – 100 | Soft version cap per `(session, logicalKey)`; further distinct-byte versions are rejected. |
| `Arcanum:Attachments:MaxBytesPerSession` | `long` | `268435456` (256 MiB) | 1 MiB – 10 GiB | Soft per-session byte budget across bound attachment files; exceeding it rejects the turn before inference. |
| `Arcanum:Attachments:PendingRetentionHours` | `int` | `24` | 1 – 168 | Startup GC age for stale `Pending` rows and matching `_pending/{turnId}` directories. |
| `Arcanum:Attachments:MaxIndexItemsInPrompt` | `int` | `40` | 1 – 200 | Cap on Session Attachments Index lines injected into the system prompt (metadata only). |
| `Arcanum:Attachments:MaxIndexBytesInPrompt` | `int` | `4096` | 256 – 64,000 | Cap on Session Attachments Index UTF-16 size in the system prompt. |
| `Arcanum:Attachments:EnableModelAttachTool` | `bool` | `true` | — | When `true` (and `Enabled` + a current session), advertise/run the internal MCP tool `attach_session_file` |
| `Arcanum:WebBrowsing:Enabled` | `bool` | `false` | — | Master toggle for the built-in **`browse_web`** tool |
| `Arcanum:WebBrowsing:MaxContentBytes` | `int` | `50000` | 1,000 – 1,000,000 | Hard byte cap on a fetched page's response body read by `browse_web`. |
| `Arcanum:WebBrowsing:RequestTimeoutSeconds` | `int` | `10` | 1 – 60 | Wall-clock timeout for the named `HttpClient(ArcanumBrowseWeb)` used by `browse_web`. |
| `Arcanum:WebBrowsing:MaxLinks` | `int` | `10` | 0 – 100 | Maximum absolute `http(s)` links extracted and returned by `browse_web`. |
| `Arcanum:ClientToolForwarding:Enabled` | `bool` | `false` | — | When `true`, client-supplied `tools` and `tool_choice` on `POST /v1/chat/completions` are forwarded to the resolved provider instead of rejected. |
| `Arcanum:ClientToolForwarding:MaxClientTools` | `int` | `20` | 1 – 100 | Maximum number of client-supplied tools accepted per `POST /v1/chat/completions` request. |
| `Arcanum:Guardrails:Enabled` | `bool` | `false` | — | Master toggle for the content guardrails pipeline |
| `Arcanum:Guardrails:DetectPii` | `bool` | `true` | — | When `true` (default), email / phone / SSN / credit-card patterns in input messages are detected via `[GeneratedRegex]` source generators (AOT-clean) and the turn is rejected with `Guardrails.PiiDetected` (HTTP 400) before inference runs. |
| `Arcanum:Guardrails:BlockToxicity` | `bool` | `false` | — | When `true`, input or output containing any `ToxicityBlocklist` keyword is rejected with `Guardrails.Blocked`. |
| `Arcanum:Guardrails:ToxicityBlocklist` | `string[]` | `[]` | — | Case-insensitive substring blocklist matched against input and output text. |
| `Arcanum:Guardrails:AllowedTopics` | `string[]` | `[]` | — | Optional allow-list of regex patterns; non-empty requires input match before inference. |
| `Arcanum:Guardrails:BlockedTopics` | `string[]` | `[]` | — | Optional block-list of regex patterns. |
| `Arcanum:Guardrails:StreamingMode` | `string` / enum | `"buffered"` (`GuardrailsStreamingMode.Buffered`) | — | Streaming output-filter mode: `buffered` (default; holds tokens until the filter passes) or `passthrough` (real-time tokens, post-hoc filter — honored with a configuration warning; ADR 0001). |
| `Arcanum:Guardrails:AuditLog:Enabled` | `bool` | `false` | — | Master toggle for the persisted guardrails audit log |
| `Arcanum:Guardrails:AuditLog:FilePath` | `string` | `~/.config/arcanum/guardrails.jsonl` | — | Base path; the directory is where dated `guardrails-YYYYMMDD.jsonl` files are written (one per UTC day). |
| `Arcanum:Guardrails:AuditLog:MaxSizeMb` | `int` | `100` | 10 – 1,000 | Soft per-day-file size cap; further writes for that day are dropped once reached. |
| `Arcanum:Guardrails:AuditLog:RetentionDays` | `int` | `7` | 1 – 365 | Dated log files older than this are deleted automatically. |
| `Arcanum:Pricing:ModelPricing` | `object` | `{}` | — | Dictionary of model-name → `ModelPricingEntry` (`InputPer1M`, `OutputPer1M`, `CachedPer1M`, and nullable `ReasoningPer1M` in USD per 1M tokens). When `ReasoningPer1M` is unset, reasoning uses that entry's `OutputPer1M`; explicit `0` means free reasoning tokens. |
| `Arcanum:Pricing:DefaultPricing` | `object` | `{ InputPer1M: 0, OutputPer1M: 0, CachedPer1M: 0, ReasoningPer1M: null }` | — | Fallback pricing for unmapped models (default free). Reasoning is an output-token subset and falls back to `OutputPer1M` only when `ReasoningPer1M` is null. |
| `Arcanum:Budget:Enabled` | `bool` | `false` | — | Master toggle for daily budget enforcement. |
| `Arcanum:Budget:DailyLimitUsd` | `decimal` | `0` | 0 – 1,000,000 | Maximum daily spend before inference is rejected (HTTP 429). |
| `Arcanum:Budget:AlertThresholdPercent` | `int` | `80` | 1 – 100 | Percentage of `DailyLimitUsd` at which a Comm Link warning is dispatched (once per threshold per UTC day). |
| `Arcanum:StructuredOutput:Enabled` | `bool` | `true` | — | Master toggle for JSON Schema validation and retry. |
| `Arcanum:StructuredOutput:MaxValidationRetries` | `int` | `2` | 0 – 10 | Maximum retry attempts when the model's response fails schema validation. |
| `Arcanum:StructuredOutput:UseProviderConstrainedDecoding` | `bool` | `true` | — | When `true`, injects provider-side constrained decoding (`strict: true` via `OpenAiRequestAugmentingHandler` for OpenAI-compatible providers). |
| `Arcanum:StructuredOutput:StrictMode` | `bool` | `false` | — | When `true`, schema validation failure returns HTTP 400 instead of best-effort with warning. |
| `Arcanum:StructuredOutput:SchemaMaxDepth` | `int` | `10` | 1 – 50 | Maximum nesting depth allowed in JSON Schema (prevents pathological schemas). |

**Campaign `SanctumConfigJson` (Grimoire column, not `ArcanumSettings`):** each `Campaign` row stores a JSON `SanctumConfig` blob. When enabled (`Enabled` default `false` for backward compatibility), `SanctumGuard` enforces path boundaries (`AllowedPaths`), network policy (`AllowAll`/`AllowList`/`DenyAll`), and `DisabledTools` at tool-invocation time (§11.15). `ResourceLimits` is split across two enforcement layers:

- **In-process:** `MaxFileWriteMb` enforced on `write_file`/`replace_text_block`; `read_file_chunk` line range capped at 2,000 lines.
- **OS-level:** `MaxCpuSeconds` / `MaxMemoryMb` / `MaxFileDescriptors` enforced at the OS level (setrlimit / cgroups v2) on Unix; on Windows, Job Objects enforce CPU time, process/job memory, and `MaxProcessCount` (`ACTIVE_PROCESS`) — see §11.15. Open file descriptors are not enforceable via Job Objects.

`MaxBreachCount` (default 1,000, clamp 100 – 100,000) bounds per-campaign `SanctumBreaches` retention (§11.15, §16.2), separate from the API query page size. Configure via `PUT /api/campaigns/{campaignId}/sanctum`; review breaches via `GET /api/campaigns/{campaignId}/sanctum/breaches` (`limit`, `before`, `tool`).

**Sanctum resource-limit clamps (Grimoire-column JSON, not `arcanum.json`):** the `SanctumConfig.ResourceLimits` block is not bound from `Arcanum:*`; values are bounded by `ArcanumSettingClamps` at the use site — `MaxProcessMemoryMb` 64 – 8,192; `MaxProcessCount` 1 – 100; `MaxFileWriteMb` 1 – 1,024; `ProcessTimeoutSeconds` 10 – 3,600; `MaxCpuSeconds`/`MaxMemoryMb`/`MaxFileDescriptors` 0 = unlimited (clamp maxes 3,600 / 32,768 / 65,536); breach query `limit` 1 – 1,000; `MaxBreachCount` 100 – 100,000.

**Startup validation.** On host start (`serve` and DevHost), an `IStartupFilter` (`ConfigurationStartupValidator`) runs `ConfigurationValidator.Validate` against the bound `ArcanumSettings` **before** the request pipeline serves. Semantically invalid configuration — an unknown `DefaultModel`/`FastModel`, MCP timeout / JSON-RPC line-size ordering, or missing/relative allow-list roots — aborts startup with a clear logged message (controlled abort, not `Environment.FailFast`) instead of booting and failing later at runtime. The validator is null-tolerant for hand-edited configs: explicit `null` sub-objects (`intelligence`, `mcp`, `campaigns`, …) and a `null` provider `models` fall back to defaults rather than throwing. The same validator backs `POST /api/config/validate`; outbound-URL/SSRF checks (`OutboundUrlGuard`) continue to run on config writes (`PUT /api/config`).

**Obsolete-key rejection.** `ConfigurationValidator.RejectObsoleteKeys` / `RejectObsoleteJsonKeys` hard-fail when removed configuration surfaces still appear in `arcanum.json`, `ARCANUM_` env overlays, or API PUT/validate bodies. Rejected surfaces: the former top-level managed local-inference options block, the former top-level prompt-cache options block (`Arcanum:Cache` / `cache`), and former per-provider local-inference nests (including any nested model URL map) or a bare provider-level model URL map, and obsolete provider `type` values such as `LlamaCppServer` (inspected on raw `IConfiguration` / JSON before enum binding). Exact rejection message strings are owned by `ConfigurationValidator` (intentional). These keys are no longer bound; silent ignore would hide broken configs. Migration: configure local or remote inference as `type: "OpenAICompatible"` (Ollama via `http://localhost:11434/v1`); list models under `Providers[].Models`; treat prompt caching as provider-managed and gate metrics with `ProviderSettings.SupportsPromptCaching`.

All numeric settings have runtime clamps defined in `ArcanumSettingClamps`, and every consumer applies the corresponding clamp at the use site. When adding a property to `ArcanumSettings`:

1. Define the property on the relevant nested record with an XML doc summary and a sensible default.
2. Add a matching `ArcanumSettingClamps.<Name>` helper if the value is numeric (size, count, duration, threshold).
3. Apply the clamp at every read site (do not store the raw value).
4. Inject via **`IOptionsMonitor<ArcanumSettings>`** for singleton consumers (hot-reload friendly) or **`IOptionsSnapshot<ArcanumSettings>`** for scoped/per-request consumers. Singletons must never capture an `IOptionsSnapshot` value for the process lifetime.
5. Extend this table and the README **Configuration** table in the same change set.
6. Add a matching `SettingDescriptor` row in `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs` and run `SettingDescriptorParityTests` + `SettingDescriptorCoverageTests` (in `tests/RetroDownfall.Compendium.Tests/`, assembly `RetroDownfall.Compendium.Ux.Tests`) so the visual editor stays in sync with the clamp bounds and covers the new field.

#### 3.4.1 Degraded-mode fallback matrix

Single-host failure behavior referenced from the settings above:

| Condition | Behavior |
|-----------|----------|
| Provider unreachable / stalled | Actionable public message (provider name + endpoint + hint); buffered turns return **`Hub.Error`**; streaming turns emit an **`Error`** frame. Wall-clock stall beyond **`InferenceTimeoutSeconds`** returns **`Hub.Timeout`**. |
| MCP server failed bootstrap (AlwaysOn) | Prominent startup warning; server excluded from toolset; surfaced in **`GET /api/health`** MCP component counts. |
| Grimoire SQLITE_BUSY / locked | Bounded exponential backoff on writes (`SqliteBusyRetry`); then surfaced as API/CLI failure if still contended. |
| Disk full / partial `security.dat` write | Atomic temp+rename on `security.dat`; corrupt store fails with recovery guidance (§16.3) instead of silent key regen when a Grimoire DB exists. |
| Data Protection keyring corrupt | See §16.3 rotate-or-restore steps; **`arcanum key show`** reads the local store only (no HTTP). |

---

## 4. Project model and dependency graph

**Dependency chain:** `Cli` → `Api` → `Infrastructure` → `Core`. `Cli` also references `Core` and `Infrastructure` directly for standalone DI setup (Data Protection, `ISecretStore`, `AddArcanumEyeOfTheWorld`).

### 4.1 `RetroDownfall.Arcanum.Core` (class library)

**Role:** Domain primitives, shared contracts, configuration, security abstractions, and cross-cutting types with **no** ASP.NET Core hosting dependency.

**Ownership boundaries (namespaces):** `Primitives/` (`Error`, `Result`, `ApiResponse<T>`), `CommLink/`, `Events/` (`IEventBus`, daemon SSE types), `Daemons/` (wire DTOs for `/api/daemons`), `Configuration/` (`ArcanumSettings`, providers, validators, bootstrapper), `Security/` (`ISecretStore` contract), `Intelligence/` (`IArcanumIntelligenceProvider`, `PingRequest`, NDJSON event types), `Storage/` (Grimoire POCOs + `IGrimoireRepository`), `Chronosync/`, `Serialization/` (Core JSON contexts distinct from Api `ArcanumJsonContext`), `Pattern/` (Eye of the World), `Workspace/`.

**MSBuild:** `<IsAotCompatible>true</IsAotCompatible>`.

**Non-goals:** Web types, hosting DI extensions, HTTP middleware.

### 4.2 `RetroDownfall.Arcanum.Infrastructure` (class library)

**Role:** OS-adjacent implementations of Core contracts — Serilog, Data Protection, SQLCipher Grimoire (EF Core 10 + compiled model), workspace scanning, Eye of the World, MCP client layer, Comm Link, Unseen Servant, RAG storage/background writers.

**Project boundary:** Implementations of Core contracts live here. Interfaces stay in Core unless noted (`IUnseenServantPacer`, `IThemeDetector`).

**MCP (SDK on the wire):** Client/transport uses **`ModelContextProtocol.Core`**; Arcanum owns `IMcpClient` → `SdkMcpClientWrapper`, in-process `ArcanumInternalToolServer` (unchanged handlers/framing), and `IMcpConnectionManager` → `McpConnectionManager` (global + workspace `mcp.json`, caps, trust gate, transport factory). Caps (`MaxPaginationPages` / tools / bytes) and `McpBridgeTool` output/fallback semantics are unchanged. Streamable HTTP via SDK `HttpClientTransport` + SSRF-guarded `HttpClient("McpHttp")`; legacy SSE → `Mcp.SseNotSupported`. Stdio env: strip by default; `inheritEnv` allowlist cannot override absolute denials (`ARCANUM_*`, loader hijacks). Per-request cancel: SDK `notifications/cancelled`; internal server tracks in-flight calls concurrently.

**In-process MCP tools (canonical list):** `read_file_chunk`, `replace_text_block`, `write_file`, `list_directory`, `execute_command` (no shell; `ArgumentList` only), `ask_human` (streaming attended only), `scribe_lexicon`/`delete_lexicon` (`EnableLexiconSystem`; delete is Forbidden Art), `search_archives`, `send_commlink_alert` (legacy call alias `use_commlink`), `petition_dungeon_master`, `adjust_initiative`, `cast_sending` / `dispatch_sending` (Conclave/A2A gates), `read_saga` (Saga flags), `attach_session_file` (Attachments flags; post-tool content injection). Relative paths only; lexical + symlink-resolved containment.

**Other DI surfaces:** `AddArcanumInfrastructure`, `AddArcanumDaemonServices`, `AddArcanumEyeOfTheWorld`, `AddArcanumThemeDetection`, Grimoire/`Chronosync`/`CampaignLoggerQueue`/`Loremaster`, `InMemoryEventBus`, Comm Link multiplex/webhook.

**RAG ownership:** Weave/Divination schema + managed/vec0 search in Infrastructure (`DivinationService`, `WeaveSchemaInitializer`, `SqliteVecExtensionLoader`); `EmbeddingBlobCodec` in **Core**; `IWeaveService` implemented in **Api** (§21.1). Background: `EntryWeavingService`, `WorkspaceIndexingService`, `SagaExtractionService`/`SagaMemoryStore`. Phase 5: `SpellWeaveCache`.

**MSBuild:** `IsTrimmable`, `PublishAot` (analysis signal), `EnableConfigurationBindingGenerator`; `FrameworkReference` AspNetCore for hosting abstractions.

**Non-goals:** Minimal API route mapping or OpenAPI.

### 4.3 `RetroDownfall.Arcanum.Api` (class library, not executable)

**Role:** HTTP surface composition — endpoint mapping, JSON contracts, intelligence provider implementation, API-key filter, and bootstrap extensions callable from any host.

**Critical decision:** The Api project is a `Microsoft.NET.Sdk` class library with `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. This separates *composition* from *hosting*: the library describes routes and serialization; it does not own process lifetime.

**Breaking architecture (sessions):** The former bounded **in-memory** conversation store (`/api/conversations`, §8.18) is **removed**. **Grimoire `Sessions` / `Entries`** are the single source of truth for The Forge, CLI, intelligence persistence, search, export, and analytics under **`/api/sessions`** (§11.16). Hard delete remains internal (`IGrimoireRepository.PurgeSessionAsync`); public **`DELETE /api/sessions/{id}`** archives (soft delete).

**API surface (`MapArcanumEndpoints`):**

| Method | Path | Contract/purpose |
|--------|------|-----------------|
| GET | `/metrics` | Prometheus text-format metrics. |
| GET | `/api/health` | Health check. |
| GET | `/api/meta` | Instance metadata and feature flags for sidecar discovery (`ApiResponse<InstanceMetadataDto>`). |
| GET | `/api/budget` | Daily budget snapshot (`ApiResponse<BudgetSummaryDto>`: enabled, daily limit, today's spend, remaining, spent percent, alert threshold; §22.2). |
| GET | `/api/grimoire/stats` | Grimoire database statistics (`ApiResponse<GrimoireStatsDto>`; database + WAL byte sizes and per-table row counts via `GrimoireStatsService`). |
| GET | `/api/config` | Read live `ArcanumSettings` with secrets and URLs redacted (apiKey, endpoint, WebhookUrl → `"***"`; `ApiResponse<ArcanumSettings>`; §8.12). |
| PUT | `/api/config` | Validate and write a full settings snapshot to `arcanum.json` (`ApiResponse<bool>`; §8.12). |
| POST | `/api/config/validate` | Validate settings without writing (`ApiResponse<bool>`; §8.12). |
| GET | `/api/models` | Flatten configured models across all providers (`ApiResponse<ModelInfoDto[]>`; endpoint redacted as `"***"`; read-only, no connectivity checks; §8.12). |
| GET | `/api/providers` | List configured providers with `apiKey`/`endpoint` redacted (`ApiResponse<ProviderInfoDto[]>`; read-only; §8.12). |
| GET | `/api/perception/look` | Eye of the World snapshot (optional `directory` query; requires `Arcanum:Perception:AllowedWorkspaceRoots`; **403** when unset). |
| POST | `/api/intelligence/ping` | Buffered inference. |
| POST | `/api/intelligence/ping-stream` | NDJSON streaming inference (same `PingRequest` extensions as buffered ping). |
| POST | `/api/intelligence/human-response` | Submit human-in-the-loop answer. |
| POST | `/api/intelligence/arsenal` | Spell names, metadata-only `SpellSummary[]`, native tools, and MCP server status. |
| POST | `/api/intelligence/mana` | Read-only diagnostic Mana (token) counter (`ApiResponse<ManaCountResult>`; body `ManaCountRequest` { `messages`, `prompt`, `model`, `tools` }). |
| GET | `/api/mcp` | List managed MCP servers (`ApiResponse<McpServerInfo[]>`; §5.6). |
| GET | `/api/mcp/{name}` | One managed MCP server (`ApiResponse<McpServerInfo>`); optional `workingDirectory` query for disambiguation. |
| POST | `/api/mcp/{name}/start` | Start one MCP server (`ApiResponse<bool>`); optional `workingDirectory` query. |
| POST | `/api/mcp/{name}/stop` | Stop one MCP server (`ApiResponse<bool>`); optional `workingDirectory` query. |
| POST | `/api/mcp/{name}/restart` | Restart one MCP server (`ApiResponse<bool>`); optional `workingDirectory` query. |
| POST | `/api/mcp/trust-workspace` | Approve a workspace-local `mcp.json` for auto-start (`ApiResponse<bool>`; body `{ "workingDirectory": "..." }`; §5.6). |
| POST | `/api/mcp/reload` | Reload MCP connections (global nuclear reload — §5.6). |
| POST | `/api/mcp/tools/invoke` | **Diagnostic MCP Invocation** — policy-constrained direct invoke of an **external** MCP tool by an operator (`ApiResponse<McpToolInvokeResponse>`. |
| GET | `/api/sessions` | Search/list Grimoire sessions (`ApiResponse<SessionQueryResult>`; §11.16). |
| POST | `/api/sessions` | Create session (`ApiResponse<SessionDetailDto>`; **201**). |
| GET | `/api/sessions/analytics` | Session analytics (`ApiResponse<SessionAnalytics>`; §11.16). |
| GET | `/api/sessions/{id}` | Session metadata (`ApiResponse<SessionDetailDto>`; **404** when missing). |
| GET | `/api/sessions/{id}/entries` | Entry history (`ApiResponse<EntryDto[]>`; optional `offset`, `limit`, keyset cursor params `beforeCreatedAt`, `beforeId`, and `?countOnly=true` to. |
| POST | `/api/sessions/{id}/entries` | Append entry manually (**404** / **400**; publishes live SSE). |
| PATCH | `/api/sessions/{id}` | Update title or status. |
| DELETE | `/api/sessions/{id}` | Archive session (**204**; soft delete). |
| GET | `/api/sessions/{id}/export` | Export JSON or Markdown (`ApiResponse<SessionExportResult>`). |
| POST | `/api/sessions/{id}/rest` | Enqueue Campaign Log consolidation (**202** + `ApiResponse<bool>` when accepted; **503** + `Session.RestQueueFull` when the bounded queue rejects). |
| GET | `/api/sessions/{id}/stream` | SSE replay + live entry stream. |
| GET | `/api/sessions/{id}/attachments` | List **bound** session attachments (`ApiResponse<SessionAttachmentDto[]>`; includes `RelativePath` for Reveal; §10.2.5). |
| POST | `/api/sessions/{id}/fork` | Create an independent branch of a session, optionally truncated at `upToEntryId` (**201**; §11.16.1). |
| POST | `/api/embeddings/reset` | Truncate embedding tables for RAG dimension-change recovery (requires `?confirm=true`; optional `?scope=all\|entry\|workspaceFile\|saga`, default. |
| DELETE | `/api/sessions/{id}/entries/{entryId}` | Delete a single entry from a session (**204**). |
| POST | `/api/sessions/{id}/entries/{entryId}/pin` | Pin an entry so it is always included in inference context, even when compression would otherwise drop it. |
| DELETE | `/api/sessions/{id}/entries/{entryId}/pin` | Unpin a previously pinned entry. |
| POST | `/api/sessions/{id}/compact` | Manually compress session context by deleting the oldest non-pinned entries until the token count is below the effective threshold. |
| POST | `/api/sessions/divine` | RAG Phase 2 — semantic search over Grimoire entries embedded by `EntryWeavingService` (`ApiResponse<SemanticSearchResult>`; body. |
| GET | `/api/lore` | List lore entries (`ApiResponse<ListPageResult<LoreDto>>`; paginated — optional `?limit=` (default `Arcanum:Grimoire:DefaultLoreListLimit`), `?offset=`). |
| GET | `/api/lore/{key}` | Get lore by key. |
| POST | `/api/lore` | Upsert lore entry. |
| DELETE | `/api/lore/{key}` | Delete lore entry. |
| GET | `/api/saga` | RAG Phase 4 — paginated listing of Saga memories (`ApiResponse<SagaMemoryDto[]>`; optional `?q=` substring, `?sessionId=`, `?limit=` [1–10,000. |
| POST | `/api/saga/divine` | RAG Phase 4 — semantic search over Saga memories (`ApiResponse<SagaSearchResult>`; body `SagaSearchRequest` { `query`, `limit` }; **503**. |
| DELETE | `/api/saga/{id}` | RAG Phase 4 — delete a single Saga memory (**204**; **404** `Saga.NotFound`; §21.8). |
| DELETE | `/api/saga` | RAG Phase 4 — delete every Saga memory, embedding, and extraction watermark (**204**; requires `?confirm=true`, else **400** `Saga.NotEmpty`; §21.8). |
| GET | `/api/saga/stats` | RAG Phase 4 — aggregate Saga memory summary (`ApiResponse<SagaStats>`: total count, session count, oldest/newest `CreatedAt`; §21.8). |
| GET | `/api/spells` | List built-in + workspace spells (`ApiResponse<SpellSummary[]>`; optional `workspace` query; §8.14). |
| GET | `/api/spells/{name}` | Spell detail (`ApiResponse<SpellDetail>`; optional `workspace` query; **404** when missing). |
| POST | `/api/spells` | Create workspace spell (`ApiResponse<bool>`; optional `workspace` query; **400** validation). |
| PUT | `/api/spells/{name}` | Update workspace spell (`ApiResponse<bool>`; optional `workspace` query; **400** on built-in or validation failure). |
| DELETE | `/api/spells/{name}` | Delete workspace spell (**204** on success; **400** on built-in or validation failure; §8.14). |
| GET | `/api/spells/search` | Multi-source spell search (`ApiResponse<SpellSummary[]>`; `?q=`, `?tag=`, `?tool=`, `?source=`, `?campaignId=`, `?workspace=`; §8.14). |
| POST | `/api/spells/{name}/validate` | Validate spell metadata and declared tools (`ApiResponse<SpellValidationResultDto>`; §8.14). |
| POST | `/api/spells/{name}/export` | Export portable spell bundle (`ApiResponse<SpellExportDto>`; §8.14). |
| POST | `/api/spells/import` | Import spell into workspace (`ApiResponse<SpellSummary>`; **400** `Spell.NameCollision`; §8.14). |
| POST | `/api/spells/{name}/execute` | Forced-spell buffered inference (`ApiResponse<PromptResponseDto>`; body `SpellExecuteRequest`; optional `?workspace=`, `?version=` (string label); **404** `Spell.NotFound`; §19). |
| POST | `/api/spells/{name}/execute-stream` | Forced-spell NDJSON streaming inference (same request/query as execute; §19). |
| GET | `/api/spells/{name}/versions` | List `SPELL.md` (active row) and `SPELL.v{label}.md` files (`ApiResponse<SpellVersionDto[]>`; **string** `version` label, `isActive` flag; optional. |
| GET | `/api/spells/{name}/versions/{version}` | Read a spell version's editable body (`ApiResponse<SpellVersionDetailDto>`; optional `?workspace=`, `?campaignId=`; use version `(active)` for. |
| POST | `/api/spells/{name}/versions` | Create a new spell version file (`ApiResponse<SpellVersionDto>`; body `CreateSpellVersionRequest` { `version`, `body`, `workspace` }; **201**. |
| PUT | `/api/spells/{name}/versions/{version}` | Overwrite an existing version's body, preserving frontmatter (`ApiResponse<SpellVersionDto>`; body `UpdateSpellVersionRequest`; **404** when the version does not exist; §8.14). |
| POST | `/api/spells/{name}/versions/{version}/activate` | Activate a version, swapping its content into `SPELL.md` and preserving the prior active content as `SPELL.v{previousLabel}.md`. |
| POST | `/api/spells/{name}/clone` | Clone a spell (built-in or workspace) into a new workspace spell (`ApiResponse<SpellSummary>`; body `CloneSpellRequest` { `newName`, `workspace` }. |
| POST | `/api/spells/{name}/cast` | Dry-run cast preview: assembled system prompt, resonant dependencies, attuned tools, and spell scripts, **without** LLM inference. |
| GET | `/api/campaigns` | List Grimoire-backed campaigns (`ApiResponse<ListPageResult<CampaignDto>>`; optional `?type=`; §19). |
| GET | `/api/campaigns/by-path` | Lookup campaign by filesystem path (`ApiResponse<CampaignDto>`; required `?path=`; **404** `Campaign.NotFound`; §19). |
| GET | `/api/campaigns/{id}` | Campaign detail (`ApiResponse<CampaignDto>`; **404** when missing; §19). |
| POST | `/api/campaigns` | Register campaign directory (`ApiResponse<CampaignDto>`; **201** + `Location`; creates `.arcanum/`; §19). |
| PUT | `/api/campaigns/{id}` | Update campaign (`ApiResponse<CampaignDto>`; §19). |
| DELETE | `/api/campaigns/{id}` | Remove campaign (**204**; §19). |
| GET | `/api/campaigns/{id}/spells` | Spells scoped to a campaign, merging built-ins with campaign spells shadowing them (`ApiResponse<SpellSummary[]>`; `?q=`, `?tag=`, `?tool=`; **404** `Campaign.NotFound`; §19). |
| GET | `/api/campaigns/{id}/prompts` | Prompts scoped to a campaign (`ApiResponse<ListPageResult<PromptSummaryDto>>`; `?q=`, `?tag=`; **404** `Campaign.NotFound`; §19). |
| GET | `/api/campaigns/{id}/sessions` | Sessions scoped to a campaign (`ApiResponse<SessionQueryResult>`; `?status=`, `?search=`, `?limit=`, `?beforeUpdatedAt=`; **404** `Campaign.NotFound`; §19). |
| POST | `/api/campaigns/{id}/export` | Export spells + prompts + settings (`ApiResponse<CampaignExportDto>`; §19). |
| POST | `/api/campaigns/{id}/import` | Import portable campaign bundle (`ApiResponse<CampaignImportResultDto>`; §19). |
| GET | `/api/campaigns/{id}/codex` | Read campaign `CODEX.md` (`ApiResponse<CodexContentDto>`; `exists: false` when file absent; **404** `Campaign.NotFound`; §19). |
| PUT | `/api/campaigns/{id}/codex` | Create or overwrite campaign `CODEX.md` (`ApiResponse<CodexContentDto>`; body `{ "content": "..." }`; **400** when over `Arcanum:Codex:MaxSizeBytes`; §19). |
| DELETE | `/api/campaigns/{id}/codex` | Delete campaign `CODEX.md` (**204**; §19). |
| GET | `/api/codex` | Read global `~/.config/arcanum/CODEX.md` (`ApiResponse<CodexContentDto>`; §19). |
| PUT | `/api/codex` | Create or overwrite global CODEX (`ApiResponse<CodexContentDto>`; §19). |
| DELETE | `/api/codex` | Delete global CODEX (**204**; §19). |
| GET | `/api/campaigns/{campaignId}/sanctum` | Campaign Sanctum config (`ApiResponse<SanctumConfig>`; default `Enabled: false`; §11.15). |
| PUT | `/api/campaigns/{campaignId}/sanctum` | Update Sanctum config (`ApiResponse<SanctumConfig>`; body `SanctumConfig`). |
| GET | `/api/campaigns/{campaignId}/sanctum/breaches` | Paginated Sanctum breach history (`ApiResponse<SanctumBreachQueryResult>`; `?limit=` default 100 clamp 1–1,000, `?before=` ISO 8601 cursor, `?tool=` filter). |
| GET | `/api/wards` | List active wards (`ApiResponse<WardDto[]>`; §11.14). |
| GET | `/api/wards/{id}` | Active ward detail (`ApiResponse<WardDto>`; **404** `Ward.NotFound`). |
| POST | `/api/wards/{id}` | Resolve a ward (`ResolveWardRequest`: `allow`, optional `reason`); returns `ApiResponse<WardResolutionDto>`. |
| GET | `/api/prompts` | List/search prompts (`ApiResponse<ListPageResult<PromptSummaryDto>>`; `?campaignId=`, `?q=`, `?tag=`; §19). |
| GET | `/api/prompts/{id}` | Prompt detail (`ApiResponse<PromptDetailDto>`; **404** `Prompt.NotFound`; §19). |
| GET | `/api/prompts/by-name/{name}/versions` | List versions for a prompt name (`ApiResponse<PromptVersionDto[]>`; optional `?campaignId=`; §19). |
| POST | `/api/prompts` | Create prompt version (`ApiResponse<PromptDetailDto>`; **201**; **400** `Prompt.DuplicateVersion`; §19). |
| PUT | `/api/prompts/{id}` | Update prompt (`ApiResponse<PromptDetailDto>`; §19). |
| DELETE | `/api/prompts/{id}` | Delete prompt (**204**; §19). |
| POST | `/api/prompts/{id}/render` | Render template with parameters (`ApiResponse<PromptRenderResultDto>`; **400** `Prompt.MissingParameter` / `Prompt.UnknownParameter`; §19). |
| POST | `/api/prompts/{id}/test` | Assemble system prompt without LLM (`ApiResponse<PromptTestResultDto>`; §19). |
| POST | `/api/prompts/{id}/execute` | Render template and run session-backed inference (`ApiResponse<PromptResponseDto>`; body `PromptExecuteRequest`; honors `sessionId`; §19). |
| POST | `/api/prompts/{id}/execute-stream` | Same as execute with NDJSON `IntelligenceEvent` stream. |
| POST | `/api/prompts/{id}/export` | Portable prompt JSON (`ApiResponse<PromptExportDto>`; §19). |
| POST | `/api/prompts/import` | Import prompt (`ApiResponse<PromptSummaryDto>`; §19). |
| POST | `/api/prompts/{id}/clone` | Clone a prompt to a new name/version, optionally overriding the campaign scope (`ApiResponse<PromptDetailDto>`; body `ClonePromptRequest` {. |
| GET | `/api/apprentices` | List Apprentices (`ApiResponse<ListPageResult<ApprenticeSummaryDto>>`; optional `?campaignId=`, `?status=`, `?limit=`, `?beforeUpdatedAt=`; §19.6). |
| GET | `/api/apprentices/{id}` | Apprentice detail (`ApiResponse<ApprenticeDetailDto>`; **404** `Apprentice.NotFound`; §19.6). |
| POST | `/api/apprentices` | Create Apprentice (`ApiResponse<ApprenticeDetailDto>`; **201** + `Location`; §19.6). |
| DELETE | `/api/apprentices/{id}` | Delete terminal Apprentice (**204**; **409** `Apprentice.Running`; §19.6). |
| POST | `/api/apprentices/{id}/start` | Start plan generation and execution (**202**; **409** `Apprentice.AlreadyRunning`; §5.7). |
| POST | `/api/apprentices/{id}/pause` | Pause at step boundary (**202**; §5.7). |
| POST | `/api/apprentices/{id}/resume` | Resume from checkpoint (**202**; **409** `Apprentice.NotPaused`; §5.7). |
| POST | `/api/apprentices/{id}/cancel` | Cancel execution (**202**; §5.7). |
| POST | `/api/apprentices/{id}/reweave` | Replace pending plan steps (`ApiResponse<ApprenticeDetailDto>`; **400** `Apprentice.InvalidPlan`; **409** `Apprentice.CannotReweave`; §5.7). |
| POST | `/api/apprentices/{id}/intervene` | Resolve **Escalated** Apprentice with DM guidance (**202**; **409** `Apprentice.NotEscalated`; §5.7). |
| POST | `/api/apprentices/{id}/cast` | **The Conclave** cross-Apprentice delegation: mint a child Apprentice from a parent (`ApiResponse<ApprenticeDetailDto>`; **201**; gated by. |
| GET | `/api/apprentices/{id}/chronicle` | Chronicle SSE stream (`text/event-stream`; §5.7, §19.6). |
| — | `/api/conclave/a2a/*` | A2A (Agent-to-Agent) JSON-RPC surface (`MapA2A`), mapped only when `Arcanum:Conclave:Enabled && A2A:Enabled && A2A:ServerEnabled`. |
| GET | `/api/conclave/a2a/agent-card` | Authenticated A2A Agent Card ("Heraldry") — not the public, unauthenticated `/.well-known/agent-card.json` convention. |
| GET | `/api/workspaces` | List registered workspaces (`ApiResponse<WorkspaceInfo[]>`; §8.17). |
| GET | `/api/workspaces/{id}` | Workspace metadata (`ApiResponse<WorkspaceInfo>`; **404** when missing). |
| POST | `/api/workspaces` | Register a workspace directory (`ApiResponse<WorkspaceInfo>`; **201** with `Location`; **400** validation). |
| PUT | `/api/workspaces/{id}` | Update workspace name/type (`ApiResponse<WorkspaceInfo>`; **404** when missing). |
| DELETE | `/api/workspaces/{id}` | Unregister workspace (**204** on success; **404** when missing). |
| GET | `/api/workspaces/{id}/files` | List files in a registered workspace (`ApiResponse<FileListResult>`; optional `relativePath`, `recursive`, `searchPattern`; §8.17). |
| GET | `/api/workspaces/{id}/files/info` | File or directory metadata (`ApiResponse<FileEntry>`; optional `relativePath`; §8.17). |
| GET | `/api/workspaces/{id}/files/contents` | Read file contents as UTF-8 text (`ApiResponse<FileReadResult>`; required `relativePath`; §8.17). |
| HEAD | `/api/workspaces/{id}/files/contents` | Size/freshness check for a file. |
| PUT | `/api/workspaces/{id}/files/contents` | Create or overwrite a file (`ApiResponse<FileWriteResult>`; **200**; required `relativePath`; gated by `Arcanum:Workspaces:EnableFileWrite`, else. |
| PATCH | `/api/workspaces/{id}/files/contents` | Replace a verbatim text block in an existing file (`ApiResponse<TextBlockReplaceResult>`; **200**; required `relativePath`; §8.17). |
| DELETE | `/api/workspaces/{id}/files` | Delete a file or directory (`ApiResponse<FileDeleteResult>`; **200**; required `relativePath`; optional `recursive`; §8.17). |
| POST | `/api/workspaces/{id}/files/directory` | Create a directory, including parents (`ApiResponse<DirectoryCreateResult>`; **201**; required `relativePath`; §8.17). |
| POST | `/api/workspaces/{id}/files/divine` | RAG Phase 3 — semantic search over a workspace's indexed files (`ApiResponse<WorkspaceSearchResult[]>`; body `WorkspaceSemanticSearchRequest` {. |
| POST | `/api/workspaces/{id}/files/index` | RAG Phase 3 — kick off an immediate background re-index of the workspace via `WorkspaceIndexingService.IndexNowAsync` (`ApiResponse<bool>`; **202**. |
| GET | `/api/workspaces/{id}/files/index/status` | RAG Phase 7 — read-only indexing status for a workspace (`ApiResponse<WorkspaceIndexStatusDto>`: vector mode/diagnostic, `IndexingEnabled`, total. |
| GET | `/api/workspaces/{id}/files/chunks` | RAG Phase 7 — bounded, paginated chunk previews for a workspace (`ApiResponse<WorkspaceFileChunkPage>`; optional `relativePath` filter, clamped. |
| GET | `/api/unseen-servant/jobs` | List Unseen Servant jobs with base and effective polling intervals (**canonical** Unseen Servant pacer API; §8.15). |
| POST | `/api/unseen-servant/jobs/{name}/initiative` | Set adaptive initiative (dynamic interval) for a job by name; returns updated status. |
| GET | `/api/daemon/jobs` | **Deprecated alias** of `GET /api/unseen-servant/jobs` (singular `daemon` retained for compatibility). |
| POST | `/api/daemon/jobs/{name}/initiative` | **Deprecated alias** of `POST /api/unseen-servant/jobs/{name}/initiative`. |
| GET | `/api/daemons` | List registered daemon jobs (`ApiResponse<DaemonJobInfo[]>`; **plural** `daemons` — registry; §8.15). |
| GET | `/api/daemons/{id}` | Daemon job metadata (`ApiResponse<DaemonJobInfo>`; **404** when missing). |
| POST | `/api/daemons/{id}/run` | Run a daemon job on demand; returns `ApiResponse<DaemonExecutionSummary>` with execution id (**400** when not found, disabled, or already running on-demand). |
| GET | `/api/daemons/{id}/history` | Execution history for a daemon (`ApiResponse<DaemonExecutionSummary[]>`). |
| GET | `/api/executions/{id}` | Execution detail (`ApiResponse<DaemonExecutionDetail>`; **404** when missing). |
| POST | `/api/executions/{id}/cancel` | Cancel a running execution; returns updated `ApiResponse<DaemonExecutionSummary>` (**400** `Daemon.NotRunning` when not running). |
| GET | `/api/logs` | Paginated in-memory log query (`ApiResponse<LogQueryResult>`; optional `minLevel`, `category`, `from`, `to`, `search`, `limit`, `beforeSequence`; §8.16). |
| GET | `/api/audit` | Persisted inference audit log query (`ApiResponse<InferenceAuditRecord[]>`; optional `from`, `to`, `model`, `sessionId`, `limit`; §8.26). |
| GET | `/api/guardrails/audit` | Persisted guardrails violation audit log query (`ApiResponse<GuardrailAuditRecord[]>`; optional `from`, `to`, `stage`, `violationType`, `sessionId`, `limit`; §8.27). |
| GET | `/api/events/daemon` | SSE stream of `DaemonEvent` frames (daemon job lifecycle for scheduled and on-demand runs); **not** wrapped in `ApiResponse<T>`. |
| GET | `/api/events/mcp` | SSE stream of `McpServerEvent` frames (MCP server lifecycle); **not** wrapped in `ApiResponse<T>`. |
| GET | `/api/events/logs` | SSE stream of `LogEntry` frames (live log tail from ring buffer); **not** wrapped in `ApiResponse<T>`. |
| POST | `/api/commlink/send` | Dispatch a **Comm Link** alert (`CommLinkMessageRequestDto`); **200** + `ApiResponse<bool>`; **400** validation; **502** + envelope on webhook HTTP failure. |
| POST | `/api/tools/invoke` | Diagnostic built-in tool invocation (`ApiResponse<ToolInvokeResponse>`; §11.27). |
| POST | `/api/providers/test` | Read-only provider connectivity probe (`ApiResponse<ProviderTestResult>`; body `endpoint`, optional `apiKey`, `type` = `OpenAICompatible`; does not write `arcanum.json`; §19). |
| POST | `/api/proving-grounds/trials/run` | Run an ephemeral **Trial** through **The Proving Grounds** (`Trial` body → `ApiResponse<TrialResult>`; §20). |
| POST | `/v1/chat/completions` | OpenAI-compatible chat (JSON or SSE); **not** wrapped in `ApiResponse<T>`. |
| POST | `/v1/embeddings` | OpenAI-compatible embeddings; **not** wrapped in `ApiResponse<T>`. |
| POST | `/v1/moderations` | Always **501** `not_supported` — not implemented; `Arcanum:Moderations` is an obsolete key. |
| POST | `/v1/images/{generations,edits,variations}` | Always **501** `not_supported` — not implemented yet. |
| POST | `/v1/audio/{transcriptions,translations,speech}` | Always **501** `not_supported` — not implemented yet. |
| POST | `/v1/files` | Upload standalone file storage, `multipart/form-data`; **201** + `OpenAiFileObject`. |
| GET | `/v1/files` | List uploaded files, optional `?purpose=` filter. |
| GET | `/v1/files/{id}` | File metadata; **404** for unknown/malformed id. |
| DELETE | `/v1/files/{id}` | Deletes metadata row + on-disk bytes. |
| GET | `/v1/files/{id}/content` | Raw bytes; always `Content-Disposition: attachment`. |
| POST | `/v1/batches` | Create an async bulk chat-completion job over an uploaded JSONL file; **200** + `OpenAiBatchObject`, `status: "validating"`. |
| GET | `/v1/batches` | List batches, optional `?status=` filter. |
| GET | `/v1/batches/{id}` | Batch status + `request_counts`; **404** for unknown/malformed id. |
| POST | `/v1/batches/{id}/cancel` | Idempotent cancel; stops in-flight processing within ~2s. |
| POST | `/v1/batches/{id}/reset` | Reset a stuck `in_progress` batch back to `validating` (input file must still exist on disk; **409** if currently in-flight; **200** `OpenAiBatchObject`; §11.21). |
| GET | `/v1/models` | OpenAI-compatible models list (flattened configured models across providers via the same `ModelInfoBuilder` that backs `GET /api/models`); **not** wrapped in `ApiResponse<T>`. |
**JSON wire shape (`/api` and shared primitives):** JSON endpoints under `/api` use the `ApiResponse<T>` envelope (`Data`, `IsSuccess`, `Error`, `TraceId`) except for these non-envelope routes:

| Route | Wire format | Section |
|-------|-------------|---------|
| `POST /api/intelligence/ping-stream` | NDJSON event lines (`application/x-ndjson`) | §8.5 |
| `POST /api/spells/{name}/execute-stream` | NDJSON `IntelligenceEvent` lines (`application/x-ndjson`) | §19 |
| `POST /api/prompts/{id}/execute-stream` | NDJSON `IntelligenceEvent` lines (`application/x-ndjson`) | §19 |
| `GET /api/events/daemon` | SSE `DaemonEvent` frames (`text/event-stream`) | §8.11 |
| `GET /api/events/mcp` | SSE `McpServerEvent` frames (`text/event-stream`) | §8.13 |
| `GET /api/events/logs` | SSE `LogEntry` frames (`text/event-stream`) | §8.16 |
| `GET /api/sessions/{id}/stream` | SSE entry frames (`text/event-stream`) | §11.16 |
| `GET /api/apprentices/{id}/chronicle` | SSE Chronicle frames (`text/event-stream`) | §5.7 |
| `GET /api/openapi/v1.json` / `GET /api/scalar` | OpenAPI document and Scalar UI (not application `ApiResponse`) | §11.5 |
| `POST /v1/chat/completions` | OpenAI-shaped JSON or `text/event-stream` | §4.3 table |
| `GET /v1/models` | OpenAI-shaped JSON list | §4.3 table |

Envelope-payload specifics:

- **`GET /api/meta`** wraps **`InstanceMetadataDto`** (version, OS, runtime, process identity, Grimoire paths, effective host binding, and intelligence feature flags).
- **`GET /api/config`** / **`PUT /api/config`** / **`POST /api/config/validate`** use **`ArcanumSettings`** as the payload type (§8.12): read returns redacted provider `apiKey`, `endpoint`, and `CommLink.WebhookUrl` values (`"***"`); write accepts the same shape and merges `"***"` placeholders from the current snapshot so secrets and URLs are preserved without a round-trip. Obsolete managed-local and prompt-cache keys are rejected (§3.4 obsolete-key rejection).
- **`DELETE /api/sessions/{id}`** returns **204** with no body on success (soft-delete archive; idempotent — §11.16); **`POST /api/sessions/{id}/rest`** returns **202** with `ApiResponse<bool>` when the job is queued, or **503** with `Session.RestQueueFull` when enqueue is rejected.
- **`POST /api/commlink/send`** returns **502** with `ApiResponse<bool>` when the outbound webhook HTTP call fails (non-success status or transport error).

**Daemon route families:** **`/api/unseen-servant/*`** (canonical) and the deprecated **`/api/daemon/*`** alias manage Unseen Servant job **configuration** and runtime scheduling intervals (`GET /api/unseen-servant/jobs`, `POST /api/unseen-servant/jobs/{name}/initiative`). **`/api/daemons/*`** and **`/api/executions/*`** (plural) are the daemon job **registry** and **execution history** API for all registered `IDaemonJob` types (§8.15). The singular `daemon` vs plural `daemons` distinction is intentional: Unseen Servant **interval control** vs daemon job **registry**.

The `/api` and `/v1` groups are protected by `ApiKeyEndpointFilter` (section 11), including the OpenAPI document and Scalar reference UI on `/api` (`MapOpenApi` / `MapScalarApiReference` are registered on the same keyed group, so browsers need a valid API key like any other `/api` caller).

**Composition roots:** `ApiBootstrapper`, `WizardIntelligenceProvider`, `ChatClientFactory`, filters/endpoints under `MapArcanumEndpoints`; Weave/`SemanticSpellRouter` live here (§10, §21).


**MSBuild:** `IsAotCompatible`, `EnableRequestDelegateGenerator` (essential for Minimal API endpoints in a referenced class library), `EnableConfigurationBindingGenerator`.

### 4.4 `RetroDownfall.Arcanum.Cli` (console executable)

**Role:** Single entry assembly — process argv, dispatch commands, and when asked, construct the ASP.NET Core pipeline and run Kestrel. Carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />` so the same binary can self-host Kestrel for `serve`.

**Commands:**

| Command | Purpose |
|---------|---------|
| `serve` | Builds `WebApplication` with slim defaults, configures Kestrel, registers API services, runs the host (§5.3). When `ARCANUM_AUTO_LAUNCHED=1`, suppresses the Listening line and the raw first-run key print (hint: `arcanum key show`); redirects Console.Out/Error to an owner-only bootstrap log under `{ArcanumPaths.GrimoireDirectory}/logs/auto-serve-bootstrap.log`. |
| `ask` | Single-prompt streaming inference via NDJSON. Resolves cwd, runs Eye of the World and Chronosync (scoped `IChronosyncEngine`), sends `PingRequest` with workspace context, `ChronosyncDelta`, and optional session continuation. Interactive sessions call `IArcanumServeLauncher.EnsureRunningAsync` before the first stream (auto-start gate). |
| `chat` | Interactive multi-turn REPL with Figlet banner (`ArcanumBannerRenderer`), Mana bar, slash commands (`/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model [name]`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/memory`, `/summary`, `/mana`, `/attach`), per-turn cancellation, inline `@` file staging, and swap-at-end Markdig rendering via `MarkdigSpectreRenderer`. On wide interactive color terminals (≥100×24), generation uses a Spectre `Layout` live dashboard (`ChatLayoutRenderer`) with MCP/model/server sidebars; narrow / redirected / `NO_COLOR` keeps the simple streaming path. Auto-starts `serve` via `IArcanumServeLauncher` when needed. `/mcp reload` is parsed as the verb `/mcp` with the required argument `reload`; the verb alone prints a usage hint. When a **`MemoryCompressionNotice`** status is received, the Mana bar gains a persistent muted **Memory Compressed** suffix until **`/new`**. Direct `arcanum chat` stays frameless Spectre; bare interactive `arcanum` opens the Command Center (below), not this REPL. |
| *(bare)* | **Command Center v2** (Terminal.Gui 2.4.17): bare interactive `arcanum` with `ARCANUM_NO_COMMAND_CENTER` unset. Fixed viewport — header / left sessions (UpdatedAt desc; overlay picker when narrow) / transcript (follow-tail) / composer / footer. Chat + allowlisted slash via `ShellCommandDispatcher` / `CommandCenterChatRunner` / `SessionWorkspaceService` (no Spectre, no CAF recursion, no `ChatCommand`). Resume loads ≤200 recent entries; `CliSessionManager` last-session restore with stale → New Session. Attachments: `/attach`, `/attachments` (+ `add`/`reveal`), `@path`; host persists when `Arcanum:Attachments:Enabled` (§10.2.5 / §16.6). Coalesced streaming (~50ms). Size gate **inside** the host after TG Init (≥80×12 floor); too small or init failure → exit **1**. Bare non-interactive / `ARCANUM_NO_COMMAND_CENTER=1` → usage/help exit **0**. `NO_COLOR` / `ARCANUM_NO_COLOR` select monochrome theme only — they do **not** block the TUI. Auto-serve via `IArcanumServeLauncher`. Types under `Cli/CommandCenter/`. |
| `look` | Prints `PatternSnapshot` from Eye of the World (no HTTP dependency). |
| `doctor` | Environment diagnostics across panels — **System** (version/OS/runtime/TTY/color), **Paths**, **Configuration** (`arcanum.json` parse), **MCP** (`mcp.json`), and a **Tokenizer** smoke test — plus an **API Health** probe (`GET /api/health`) with a configurable timeout (`Arcanum:Cli:DoctorHealthTimeoutSeconds`, default 2s). A hard-check failure exits **1**; an unreachable or timed-out API is a **non-fatal warning** (still exits 0). Pass `--fix-permissions` to apply owner-only permissions to the Grimoire database, `arcanum.json`, and secret store. No infrastructure services required beyond `IHttpClientFactory`, `ISecretStore`, and `IOptions<ArcanumSettings>`. |
| `key show` | Prints the stored master API key from the OS credential store (`ISecretStore` → keychain with `security.dat` fallback) to **stderr**. CLI-only, **no HTTP** (§16.3). |
| `key set` | Stores a master API key into the OS credential store (mirrors to `security.dat`). Argument, stdin, or interactive secret prompt (§16.3). |
| `lore list\|get\|set\|delete` | CRUD on `MageSettings` via `/api/lore`. |
| `daemon install\|uninstall\|status` | OS-specific background service lifecycle (Windows `sc`, macOS `launchd`, Linux `systemctl --user`). |
| `daemon jobs` | Lists Unseen Servant jobs (name, spell, base vs effective interval, enabled) via **`GET /api/unseen-servant/jobs`**; requires **`arcanum serve`** (or equivalent host) and stored API key. |
| `daemon initiative <JOB_NAME> <MINUTES>` | Sets adaptive initiative for a job via **`POST /api/unseen-servant/jobs/{name}/initiative`** with **`AdjustInitiativeRequestDto`**; prints updated **effective** interval (server-clamped). Same connectivity requirements as `daemon jobs`. |
| `daemon alert <MESSAGE>` | Sends a **Comm Link** smoke alert via **`POST /api/commlink/send`** with **`CommLinkMessageRequestDto`** (options: `--title`, `--severity`, `--source`). Same connectivity requirements as `daemon jobs`. |
| `campaign list\|get\|create\|update\|delete\|export\|import\|spells\|prompts\|sessions` | The Forge campaign registry via **`/api/campaigns`**. `list` accepts `--type`; `create` requires `--name`/`--path` (`--type` defaults to `campaign`); `export`/`import <ID>` round-trip `CampaignExportDto` as JSON (stdout or `--output`/`--file`); `spells`/`prompts`/`sessions <ID>` list campaign-scoped resources via `GET /api/campaigns/{id}/spells\|prompts\|sessions` (campaign spells shadow built-ins of the same name). |
| `campaign codex get\|put\|delete` | Manage the campaign's `CODEX.md` via **`/api/campaigns/{id}/codex`**. `put` reads content from `--file` (or inline `@file` convention, see below). |
| `spell list\|get\|create\|update\|delete\|search\|validate\|execute\|versions\|export\|import\|cast\|clone` | The Forge spell CRUD + execution via **`/api/spells`**. `create`/`update` require `--workspace`; `create` accepts `--body`, repeatable `--tag`/`--declared-tool`/`--dependency` (writes `SPELL.json`); `execute` sends `SpellExecuteRequest` (`--version` takes a **string label**, not an integer) and prints the response text (plus a themed tool-call summary on stderr when `ToolCalls` is non-empty); `search` filters by `--query`/`--tag`/`--tool`/`--source`; `cast <NAME>` is a **dry-run** preview (`POST /api/spells/{name}/cast`) rendering the assembled system prompt, resonant dependencies, attuned tools, and spell scripts without consuming inference tokens; `clone <NAME> --new-name <N>` clones a spell (built-in or workspace) into the workspace (`POST /api/spells/{name}/clone`). |
| `spell version create\|update\|activate` | Nested branch for named spell **version files** (`SPELL.v{label}.md`) via **`/api/spells/{name}/versions`**. `create`/`update <NAME> --version <LABEL> --body <TEXT_OR_FILE>` write a version file (label: alphanumeric + dots); `activate <NAME> --version <LABEL>` swaps the version into `SPELL.md`, preserving the prior active content as `SPELL.v{previousLabel}.md` (printed as a themed note). |
| `prompt list\|get\|versions\|create\|update\|delete\|render\|test\|execute\|export\|import\|clone` | The Forge prompt CRUD + template rendering via **`/api/prompts`**. `render`/`execute` accept repeatable `--param key=value`; `test` assembles the system prompt using the prompt's default parameters (no LLM cost); `execute` prints the response text (plus tool-call summary on stderr); `clone <ID> --new-name <N> --new-version <V>` clones to a new name/version, optionally overriding `--campaign` (`POST /api/prompts/{id}/clone`). |
| `ward list\|get\|resolve` | Ward approval gates via **`/api/wards`**. `resolve <ID>` requires exactly one of `--allow`/`--deny` (mutually exclusive) plus optional `--reason`; 404 `Ward.NotFound` and 409 `Ward.AlreadyResolved` are rendered as themed messages. |
| `trial run` | The Proving Grounds via **`POST /api/proving-grounds/trials/run`**. `--target` (`spell`\|`prompt`\|`apprenticeGoal`) + `--target-value`; repeatable `--inquisitor` (inline JSON or `@file`) and `--var key=value`. Renders Passed/Failed, a verdicts table, and the output (truncated to 500 chars); exits `1` when the Trial fails. |
| `apprentice list\|get\|create\|delete\|start\|pause\|resume\|cancel\|reweave\|intervene\|cast\|chronicle` | The Forge Apprentice orchestration via **`/api/apprentices`**. `create` accepts `--goal` (inline or `@file`; `--name` defaults to a truncated goal); `reweave` reads a JSON `PlanStep[]` from `--plan` (inline or `@file`); `cast` surfaces 409 `Apprentice.ConclaveDisabled` as a themed explanation; `chronicle <ID>` is an SSE consumer (see below). |
| `model list` | List configured models across all providers via **`GET /api/models`** (themed table: Model, Provider, Type, Context Window). Endpoint redacted; read-only. |
| `provider list` | List configured providers via **`GET /api/providers`** (themed table: Name, Type, Endpoint, Models count, Context Window, Has Model Map). `apiKey`/`endpoint` redacted; read-only. |
| `session divine <QUERY>` | RAG Phase 2 — semantic search over Grimoire entries via **`POST /api/sessions/divine`**; options `--limit`, `--campaign`, `--status` (§21.6). |
| `saga list` | RAG Phase 4 — paginated listing of Saga memories via **`GET /api/saga`**; options `--query`, `--session`, `--limit`, `--offset` (§21.8). |
| `saga divine <QUERY>` | RAG Phase 4 — semantic search over Saga memories via **`POST /api/saga/divine`**; option `--limit` (§21.8). |
| `saga delete <ID>` | RAG Phase 4 — delete a single Saga memory via **`DELETE /api/saga/{id}`** (themed confirmation on success; §21.8). |
| `saga stats` | RAG Phase 4 — bordered panel summary of Saga memory storage via **`GET /api/saga/stats`** (§21.8). |

**`@filename` convention:** `--body`, `--template`, `--goal`, `--plan`, and `--inquisitor` accept either inline text/JSON or `@filename` to read the value from a file. This is a CLI-wide convention for non-interactive commands, distinct from the `chat` REPL's inline `@path` staging within prompt text — both read file contents, but the flag-value form is positional to an option while the REPL form is inline in free text.

**`apprentice chronicle` (SSE consumer):** opens `GET /api/apprentices/{id}/chronicle`, parses `data: {...}` frames (ignoring `:` heartbeats, stopping on `[DONE]`), and prints `[timestamp] type message` per event (failed-lifecycle events in the `Error` palette color). The `eventsDropped` event type (slow-reader backpressure) is rendered as a themed warning rather than a normal event. Ctrl+C cancels the stream (exit `130`).

**Inference flag ranges** (`ask` + `chat`, validated by `InferenceFlagBinder` before the request is sent): `--temperature` 0–2, `--top-p` 0–1, `--max-tokens` ≥ 1 (no upper clamp), `--seed` any 64-bit integer (no clamp), `--presence-penalty` / `--frequency-penalty` −2..2, repeatable `--stop` (multiple values), `--response-format` accepting `text` / `json_object` / `json_schema` with `json` as an alias for `json_object`, and `-c` / `--campaign <ID>` to set `PingRequest.CampaignId` (resolves `workingDirectory` from the Grimoire campaign path server-side; 400 `Campaign.NotFound` if unknown). Both verbs also accept `-n` / `--new` (new session), `-m` / `--model`, and `--unattended`; `chat` adds `--no-tools` and shows the campaign ID in its startup banner when set.

**CLI exit codes:** `ask` returns `0` on success, `1` on empty prompt / flag-parse / stream / API error, and **`130`** when an in-flight turn is cancelled (Ctrl+C). `chat` returns `0` normally and `1` if any turn failed during the session; an in-turn Ctrl+C cancels the current turn and returns to the `Mage >` prompt (it does **not** exit `130`). **Command Center** returns `0` on clean `/exit`/`/quit`, bare non-interactive usage, or `ARCANUM_NO_COMMAND_CENTER=1`; returns `1` when the terminal is too small after TG Init or TG bootstrap fails. `apprentice chronicle` returns `130` on Ctrl+C. `trial run` returns `1` when the Trial fails (`TrialResult.Passed == false`), separate from HTTP/validation failures. Other non-streaming verbs return `0` on success and `1` on failure.

**Composition:** `ArcanumApiClient`, CAF command tree (`CliApplicationFactory`), theme/Spectre UX, Command Center (`Cli/CommandCenter/`), `IArcanumServeLauncher`. Discover verbs in `Cli/Commands/`.


### 4.4.1 Auto-launch serve lifecycle

Interactive `chat` / `ask` / **Command Center** call `IArcanumServeLauncher.EnsureRunningAsync` after Grimoire init (Command Center: after host entry, before TG Run):

1. Gate: `ICliEnvironment.IsInteractive` and `ARCANUM_NO_AUTO_SERVE` unset. `NO_COLOR` does **not** disable auto-serve (it only gates color + live layout / Command Center theme).
2. Authenticated `GET /api/health` (re-reads `ISecretStore` on each poll). Map: 200 → already running; 401/403 → auth failed (do not spawn); 503 → brief retry then failed (do not spawn); TLS failure / timeout → failed (do not spawn — something answered); connection refused / network unreachable / DNS → definite no-listener → proceed.
3. If effective ListenAny needs interactive acknowledgement → failed with guidance (do not auto-ack).
4. Spawn via `IServeProcessLauncher` with `ARCANUM_AUTO_LAUNCHED=1` (direct `ProcessStartInfo`, no shell). Poll until authenticated 200 or deadline. Post-spawn 401 with null key keeps polling (first-run key race); post-spawn 401 with a non-null key across attempts → auth failed.
5. Canonical PID file remains owned by `PidFileService` under `{ArcanumPaths.GrimoireDirectory}/arcanum.pid`. The launcher never deletes it on health failure.

Non-goal in this phase: `arcanum serve stop` / `daemon stop` for the auto-launched process.

**MSBuild:** `PublishAot` (the shipping native image on non-macOS RIDs), `IsAotCompatible`, `EnableConfigurationBindingGenerator`. `ConsoleAppFramework` and `ConsoleAppFramework.Abstractions` are analyzer/source-generator packages with no runtime DLL reference, so no `TrimmerRootAssembly`, `[DynamicDependency]`, or IL-warning suppression is needed for CLI parsing. **Terminal.Gui** is referenced only from `Cli`; first-party AOT IL for the Command Center bootstrap is gated by `./scripts/verify-aot-il-warnings.sh` (method-level suppressions on `CommandCenterApp` only — no project-level blanket suppress). Transitive vulnerable packages: `dotnet list package --vulnerable --include-transitive` on the Cli project.

### 4.5 `RetroDownfall.Arcanum.Api.DevHost` (console executable, debug-only)

Thin host for F5 debugging the HTTP stack without Spectre. References `Api`, `Core`, and `Infrastructure`; mirrors `ServeCommand` wiring. Not the production entrypoint. To catch AOT issues during F5, the project sets `PublishAot`, `IsAotCompatible`, and `EnableConfigurationBindingGenerator` as **analysis signals** (not a shipped native image). On first run generates an API key and prints it to stdout.

### 4.6 `RetroDownfall.Compendium.Ux` (.NET 10 Avalonia desktop configuration editor)

Visual editor for §3.4 — reads/writes `arcanum.json` only (no inference/daemon/Grimoire/MCP). References **Core** only; local Data Protection mirror for `dp:v1:` secrets + HTTPS cert password. `SettingDescriptor` table drives controls/clamps; parity + coverage tests guard drift. See [`Compendium.README.md`](Compendium.README.md).


---

## 5. Hybrid hosting model

### 5.1 Process roles

One binary; the CLI verb selects the process role (per-command detail in §4.4). The defining axis is process lifetime:

- **No arguments** — Spectre prints standard usage.
- **`serve`** — the long-running HTTP host: builds `WebApplication` with slim defaults and blocks until shutdown.
- **`ask`** — streams single-prompt inference via NDJSON, then exits (0/1/130).
- **`chat`** — multi-turn REPL with per-turn cancellation and swap-at-end rendering.
- Short-lived verbs — `look` / `doctor` run local checks (no HTTP for path checks); `lore`, `daemon jobs|initiative|alert` call the running host's `/api` (Unseen Servant interval control via the canonical `/api/unseen-servant/*`, with `/api/daemon/*` retained only as a deprecated alias, §5.5.2; Comm Link smoke tests via `POST /api/commlink/send`); `daemon install|uninstall|status` drives OS service lifecycle. Bare interactive `arcanum` opens the Command Center (long-lived TUI) until `/exit`; direct `chat` remains the frameless Spectre REPL.

### 5.2 Why ConsoleAppFramework

Source-generated parsing (AOT-clean, no reflection). Spectre remains for rendering. `RepeatableOptionMerger` rewrites repeated flags into CAF JSON-array syntax; XML-doc aliases preserve legacy camelCase option spellings.

### 5.3 `ServeCommand` lifecycle

1. Cancellation token check on the injected `CancellationToken` (ConsoleAppFramework wires SIGINT/SIGTERM to it automatically because the method declares a `CancellationToken` parameter).
2. `WebApplication.CreateSlimBuilder()` (§6).
3. `UseWindowsService` / `UseSystemd` (cross-platform no-ops on other OSes).
4. Kestrel: `ListenLocalhost(port)` unless `ARCANUM_HOST_ANY` is set (§7).
5. `ClearProviders()` so Serilog replaces default logging.
6. `AddArcanumConfiguration()` loads `arcanum.json` + env vars.
7. `AddArcanumApiServices(configuration)` registers all services (§8.3), including `AddArcanumDaemonServices` for the Unseen Servant (§5.5).
8. `ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync` **before** `Build()`.
9. `Build()` → `MapArcanumEndpoints()` → `RunAsync()`. `PidFileService` writes the PID file during host `StartAsync` (§8.19). `Log.CloseAndFlush()` in `finally`.

### 5.4 Grimoire persistence (Infrastructure + Api)

**Role:** Local-first session history in an SQLCipher-encrypted SQLite file under `~/.config/arcanum/`.

**Composition:**

- **`GrimoireDatabaseHostedService`** — initializes SQLCipher, resolves the DB passphrase from a dedicated Grimoire encryption secret using PBKDF2-HMAC-SHA256 (600,000 iterations) with a unique 16-byte salt stored in a `{grimoire.db}.kdf` sidecar, falls back to legacy API-key HKDF for databases without a sidecar, and applies embedded SQL schema migrations via **`GrimoireDatabaseBootstrapper`** → **`GrimoireSqlSchemaMigrator`** (raw SQLite + `__EFMigrationsHistory`; AOT-safe; no `MigrateAsync` on the host), then `IGrimoireDbReadiness.MarkReady()`; `FailFast` on key mismatch. Legacy databases are transparently re-encrypted to the new KDF on unlock. The same bootstrapper runs from the CLI (`ask` / `chat`) so host and CLI share one migration path (§10.5).
- **`CampaignLoggerQueue` / `Loremaster`** — bounded `Channel<Guid>` (capacity 100) with **non-blocking `TryQueue`**: duplicate session ids coalesce via a pending-marker map; a full channel rejects with a warning log and clears the marker so the session remains eligible for a later sweep (internal sweeps fail-open). Explicit `POST /api/sessions/{id}/rest` returns **202** when accepted/coalesced and **503** + `Session.RestQueueFull` when rejected. Background service `Loremaster` (formerly `CampaignLoggerBackgroundService`) runs hybrid sweeps using **`Session.UnsummarizedEntryCount`** (incremented on every entry append — both the inference path and The Forge `POST /api/sessions/{id}/entries` path, each serialized per-session via **`SessionEntryPersistence`** / **`SessionWriteLock`** + **`SqliteBusyRetry`** so concurrent appends never lose an increment; reset on summarize) instead of full-table `Entries` aggregation. The consume path loads session headers via **`GetSessionHeaderAsync`** (no entry hydration). Headless summarization uses a stateless `PingRequest` with `SkipSpellRouting`, `DisableMcpTools`, `UnattendedMode`, optional `Arcanum:FastModel` (else `DefaultModel`); on success, `UpdateSessionCampaignRollupAsync` atomically sets `Session.Summary`, `LastSummarizedMessageAt`, and the remaining unsummarized count. On inference failure, the watermark is **not** advanced.
- **`ArcanumDbContext`** — compiled model; SQLCipher passphrase from hosted service.
- **`SessionRepository`** — implements **`ISessionRepository`** for Forge session CRUD, entry append, export, and analytics. Entry writes delegate shared invariants (lock, retry, limits, counter, UpdatedAt) to internal **`SessionEntryPersistence`**. **`AddEntryAsync`** returns **`Result<Entry>`** for expected domain outcomes (not found, archived, entry limits). **`UpdateSessionAsync`** patches Title/Status only — Grimoire-owned counters and rollups are never clobbered from caller-supplied `Session` rows.
- **`GrimoireRepository`** — implements `IGrimoireRepository` (the interface is the authoritative reference). Entry append/finalize/discard paths delegate the same **`SessionEntryPersistence`** invariants. `GetSessionAsync` loads the session header (no eager `Include`) and a bounded, chronologically-ordered window of the most-recent `Arcanum:Grimoire:MaxMessagesPerConversationLoad` `Entry` rows (default 1000) so very long threads do not blow host RAM. The window is pushed down server-side as parameterized SQL (`ORDER BY "CreatedAt" DESC LIMIT n` — the SQLite provider cannot `ORDER BY`/compare a `DateTimeOffset` in LINQ, and `CreatedAt` is stored as sortable UTC text) and is widened to at least the number of entries after `LastSummarizedMessageAt`, guaranteeing read-time compression sees every un-summarized message. Older entries still exist in SQL — Campaign Logger summaries (§8.7) and FTS5 `search_archives` cover the long tail.
- **`ChronosyncEngine`** — implements `IChronosyncEngine`: compares the current `PatternSnapshot` to the latest `WorkspaceContext` row for that path, persists a new baseline row, and returns a `ChronosyncReport` (headless; no HTTP or Spectre).

**Outcome-model policy:** Repository and service boundaries return **`Result` / `Result<T>`** with wire-stable **`Error.Code`** values from **`ErrorCodes`** for expected, recoverable domain outcomes (not found, validation, limits, state conflicts). Reserve thrown exceptions for unrecoverable infrastructure faults, programmer errors, and transport layers where a catch-and-fallback is intentional (for example cooperative cancel on SSE). HTTP endpoints map **`Result.Error.Code`** to status codes exclusively via **`ArcanumErrorMapper`** — never by parsing exception messages.

#### 5.4.1 Grimoire data model

| Entity | Table | Primary key | Notable |
|--------|-------|-------------|---------|
| `Session` | `Sessions` | `Id` (Guid) | Optional `CampaignId`, `Status` (default `active`), `Title` (nullable), `CreatedAt`, `UpdatedAt`, nullable `Summary`, nullable `LastSummarizedMessageAt`, **`TotalTokensUsed`**, **`UnsummarizedEntryCount`** (entries after watermark; default `0`; `-1` reserved for lazy backfill if ever needed); indexes on `CreatedAt`, `Status`, `UpdatedAt`, `(CampaignId, Status, UpdatedAt)`, `UnsummarizedEntryCount`; cascade-deletes entries. |
| `Entry` | `Entries` | `Id` (Guid) | FK to `Session`; composite index on `(SessionId, CreatedAt)`; index on `Role`; `Role` (enum → int); `ModelUsed` (non-null); optional tool columns; FTS5 virtual table `Entries_fts` + triggers for `search_archives`. |
| `MageSetting` | `MageSettings` | `Key` (string) | `Value`, `UpdatedAt`; operator key-value surface (`/api/lore`, `arcanum lore`). No longer model-directed memory — the Lore MCP tools are removed; agent memory is The Lexicon (§10.6). |
| `WorkspaceContext` | `WorkspaceContexts` | `Id` (Guid) | `CreatedAt` (`DateTimeOffset`), `WorkspacePath` (mapped column `RootPath`, max 4096), `SerializedSnapshot` (JSON `PatternSnapshot` via `GrimoireJsonContext`). **Chronosync reporting** appends a row after each analysis; “latest” for a path is `ORDER BY CreatedAt DESC`. Composite index on `(RootPath, CreatedAt)`. |

**Supporting DTOs (Core):** `GrimoireEntryDto`, `LoreDto`, `UpsertLoreRequest`, `ChronosyncReport`, `ArcanumPaths`, `ChatCompletionUsage` (OpenAI-shaped `usage` for NDJSON and `/v1` responses), `PromptTurnResult` (buffered inference text + usage). The Forge session DTOs live under **`Core.TheForge`** (`SessionDetailDto`, `EntryDto`, etc.).

#### 5.4.2 Temporal context: Session-Based Consolidation and Chronosync

Arcanum’s **Session-Based Consolidation model of AI memory** spans two layers: **session** consolidation (Campaign Logger — §8.7) writes **`Session.Summary`** and advances **`LastSummarizedMessageAt`** after successful headless summarization, while **Chronosync reporting** supplies **temporal workspace** context — what changed on disk while the operator was away. `IChronosyncEngine` compares the live Eye-of-the-World `PatternSnapshot` to the last Grimoire-stored snapshot for the same `RootPath` and emits a **`ChronosyncReport`** (`PreviousSnapshotTime`, `NewThreads`, `MissingThreads`, `DomainChanged`, `PreviousDomain`) for downstream session consolidation (for example model memory prompts in a later phase). It is orthogonal to Campaign Logger thresholds; both contribute to the same mental model of “what the AI should know without re-reading the tree.”

#### 5.4.3 Design-time factory (`ArcanumDbContextFactory`)

`IDesignTimeDbContextFactory<ArcanumDbContext>` for `dotnet ef` tooling — uses `ARCANUM_GRIMOIRE_DEV_KEY` (fallback placeholder), a temp-directory database, and a no-op `ISecretStore`.

### 5.5 Unseen Servant

The **Unseen Servant** is a proactive background scheduler for headless inference when the HTTP host is running (`serve` or `Api.DevHost`). `AddArcanumDaemonServices` registers **`UnseenServantService`**, an ASP.NET Core **`BackgroundService`** in Infrastructure.

#### 5.5.1 Schedule and execution

`PeriodicTimer` every minute; due jobs via effective interval + tracker (watermarks hydrated §5.5.5). `Task.Run` + per-key overlap guard; new DI scope; `ExecutePromptAsync` with `UnattendedMode`, `OverrideSpellName`, empty `WorkingDirectory` (global spells). Lexicon previous-state injection when `EnableLexiconSystem` (§5.5.3). Shutdown drains `_activeJobTasks` for `ShutdownDrainTimeoutSeconds`. Concurrency: `MaxConcurrentJobs` (excess deferred). `OverrideSpellName` skips SemanticRouter; `SkipSpellRouting` skips all spell IO (Campaign Logger / internal).

#### 5.5.2 Adaptive initiative (dynamic polling)

`IUnseenServantPacer` holds interval overrides keyed by `{Name}\0{TargetSpell}` (same composite as tracker) — **runtime cache with Grimoire write-through** on `adjust_initiative` / `POST /api/unseen-servant/jobs/{name}/initiative` (deprecated `/api/daemon/*` aliases). Hydrated from `UnseenServantWatermarks.EffectiveIntervalMinutes` at startup (§5.5.5). CLI: `arcanum daemon jobs|initiative|alert`. SSE: `DaemonEvent` started/completed/failed/intervalChanged on `GET /api/events/daemon`.

#### 5.5.3 Stateful memory (Lexicon auto-injection)

**Auto-injection** avoids an extra LLM round-trip that would read memory first: **`UnseenServantDaemonJob`** loads the **Lexicon** daemon-state entity for **`daemon_state:{job.Name}:{shortHash(targetSpell)}`** (type **`DaemonState`**) via **`ILexiconService.GetByNameAsync`** before **`ExecutePromptAsync`** and embeds its facts in the kickoff under **`### Previous State`**. This runs **only** when **`Arcanum:Intelligence:EnableLexiconSystem`** is **`true`** (same flag that gates **`scribe_lexicon`** / **`delete_lexicon`** in MCP). When the flag is **`false`**, previous-state injection is skipped and the model is **not** told to call **`scribe_lexicon`** because those tools are absent. Load failures or missing entries log a warning and proceed with empty prior state so the minute scheduler is not skipped. Headless **`PingRequest`** still uses an empty **`WorkingDirectory`** so spells come from the global tree; internal Lexicon tools remain available for unattended runs when enabled.

#### 5.5.4 Comm Link escalation (kickoff + MCP)

**Kickoff:** Every Unseen Servant kickoff appends an explicit instruction: if the model detects a **high-alpha** or **critical** condition requiring immediate human attention, it **MUST** call in-process MCP **`send_commlink_alert`** with an appropriate **`severity`** (`Info`, `Warning`, or `Critical`).

**Runtime:** **`send_commlink_alert`** is advertised in the fixed internal **`tools/list`** catalog (not feature-flagged). The handler resolves **`ICommLinkDispatcher`** per call via **`IServiceScopeFactory`**. Dispatch returns typed **`CommLinkDeliveryResult`**: **`Delivered`**, **`Suppressed`** (no webhook / policy skip), or a failed **`Result`** (transport/HTTP error). **`CommLinkMultiplexer`** aggregates sinks (any delivery wins; partial delivery + failure → Delivered with logged failure). **`WebhookCommLinkDispatcher`** **`POST`**s generic JSON (`title`, `body`, `severity`, `source`, `timestampUtc`) — Telegram/WhatsApp need an automation relay. Webhook URLs are secrets (log host only). Legacy **`use_commlink`** remains a tools/call-only alias.

#### 5.5.5 Watermark persistence

Grimoire `UnseenServantWatermarks` (raw SQL store; schema in PERSISTENCE). Write-through on job completion and initiative change (failures warn; in-memory still updates). Startup hydration before first tick keeps real `LastRunAt` (overdue jobs stay due — see PERSISTENCE §6); `LastResult` is process-local. Startup jitter not persisted. Sanctum breaches are Grimoire-backed (§11.15).

### 5.6 MCP host lifecycle

**Purpose:** Let first-party clients observe and control individual MCP servers without reloading the entire host.

**Registry:** **`McpConnectionManager`** maintains a thread-safe registry keyed by **`(serverName, scopeWorkingDirectory)`** where **`scopeWorkingDirectory == null`** means a global `~/.config/arcanum/mcp.json` entry and a non-null value is the normalized workspace root for a workspace-local `mcp.json` entry. Workspace-local entries are registered **lazily** when that workspace partition is first touched (inference, arsenal, or reload); **`GET /api/mcp`** lists them only after that access.

**`mcp.json` extensions:** Each server entry supports **`alwaysOn`** (default `true`), optional **`cwd`** (subprocess working directory for stdio servers), an optional **`type`** transport selector (`"stdio"` | `"http"` | `"sse"`), optional **`url`** (a URL infers the **Streamable HTTP** transport when `type` is omitted; an explicit `type: "sse"` selects the legacy SSE transport, still unsupported → **`Mcp.SseNotSupported`**), and an optional **`inheritEnv`** string array naming host environment variables an stdio server may inherit despite the default env-strip (e.g. `["PATH","HOME"]` for `npx`). HTTP endpoints must be `https` unless their host is listed in `Arcanum:Mcp:AllowedHttpHosts`, and are SSRF-validated via `OutboundUrlGuard` before connect.

**Workspace-local trust gate:** Workspace `mcp.json` servers are **not registered** until the operator approves the workspace via **`POST /api/mcp/trust-workspace`** (`{ "workingDirectory": "<root>" }`). Approvals persist at `~/.config/arcanum/trusted-mcp-workspaces.json` as workspace path → SHA-256 of the current `mcp.json` bytes. **`TrustedMcpWorkspaceStore.IsTrustedAsync` / `TrustAsync` always open and hash the current bounded file bytes** — path, length, timestamp, or a previously computed digest alone never authorize execution. **`alwaysOn` is ignored** for workspace-local entries until trusted. **`POST /api/mcp/{name}/start`** and **`RestartAsync`** (including Running/Error respawn) with a workspace scope also require trust (`Mcp.WorkspaceNotTrusted`). Global MCP servers (`ScopeWorkingDirectory == null`) are unaffected by this gate.

**Auto-start:** **`McpServerBootstrapHostedService`** calls **`IMcpConnectionManager.InitializeAsync`** on host start to load the global registry and start all **`alwaysOn`** global servers. **`StopAsync`** calls **`StopAllAsync`** for graceful shutdown. Unaffected by the ModelContextProtocol SDK migration — its calls into `IMcpConnectionManager` are unchanged in signature and behavior.

**Lifecycle API:** **`StartAsync`**, **`StopAsync`**, and **`RestartAsync`** are idempotent (`Running`/`Starting` start → success; `Stopped`/`Error` stop → success; restart while stopped → start). Per-server **`SemaphoreSlim`** gates mutations. State transitions publish **`McpServerEvent`** on **`IEventBus`** **after** releasing the gate. Each entry's live client is a **`SdkMcpClientWrapper`** (the only `IMcpClient` implementation — see §4.2) wrapping an official SDK `McpClient` session; unexpected subprocess exit or a dropped/expired Streamable HTTP session both transition a running server to **`error`** and publish an event, via the wrapper's `OnTransportEnded` callback observing the SDK client's `Completion` task (rather than a stdio-specific process-exit handler, this now applies uniformly to stdio and HTTP).

**Disambiguation:** Lifecycle routes accept optional **`?workingDirectory=`** (workspace root). When omitted and multiple registry entries share the same name, the API returns **400** **`Mcp.AmbiguousServer`**.

**`POST /api/mcp/reload`:** Preserves the existing **global nuclear reload** semantics: dispose all partition clients, clear caches, reset global bootstrap, re-read global `mcp.json`, restart **`alwaysOn`** globals. The optional **`workingDirectory`** body field is **informational only** (logged); workspace partitions are not immediately re-built.

**Inference:** **`GetAvailableToolsAsync`** merge order is unchanged (internal → global → workspace local). Only **running** managed servers contribute tools; **`alwaysOn: false`** servers stay stopped until explicitly started.

### 5.7 Apprentice orchestration

**Purpose:** Goal-driven autonomous sub-agents (**Apprentices**) that the Dungeon Master creates, starts, and monitors. The hub provider (Wizard, **`WizardIntelligenceProvider`**) generates a plan, then the Apprentice executes each step with **`UnattendedMode: true`**, checkpointing progress in the Grimoire.

**Persistence:** **`Apprentices`** table (Grimoire DB) stores goal, JSON plan, status, workspace path, optional campaign and session FKs, and checkpoint blob. **`IApprenticeRepository`** / **`ApprenticeRepository`** (scoped).

**Runtime:** **`ApprenticeService`** (`BackgroundService`, singleton **`IApprenticeRuntime`**) runs alongside **`UnseenServantService`** without modifying it. On host start, **`GetResumableAsync()`** re-spawns tasks for **`Running`** Apprentices (crash recovery). Concurrency is capped by **`Arcanum:Apprentices:MaxConcurrentApprentices`** using an atomic **`ApprenticeConcurrencyGate`** (increment-then-compare, matching **`SseConnectionGate`**); excess **`/start`** requests queue up to **`MaxPendingStarts`**, while **`/resume`** and **`/intervene`** fail fast with **`Apprentice.MaxReached`** when no slot is available.

**Execution loop:** Planning → optional plan generation via **`ExecutePromptAsync`** (`SkipSpellRouting: true`) → step loop via **`StreamPromptAsync`** with per-step timeout, **Second Wind** retry/backoff, optional **Shifting Fate** re-weave after each completed step (bounded by **`MaxReweavesPerRun`**), per-run **`MaxRunSteps`** / **`MaxRunDurationMinutes`** budgets, and **Divine Intervention** escalation → Grimoire session spans all steps via **`SessionId`**. Forbidden Arts respect **`Ward:AutoDenyInUnattendedMode`** (auto-deny in unattended mode).

**Second Wind (retry/backoff):** Transient step failures (inference timeout, provider errors) retry up to **`Arcanum:Apprentices:MaxStepRetries`** with exponential backoff (`RetryBackoffSeconds`, capped by `RetryBackoffMaxSeconds`) and **full jitter** (uniform delay in `[1s, ceiling]` to reduce synchronized retries). Each retry emits **`stepRetrying`** on the Chronicle. Ward/forbidden-art denials remain terminal (**`Failed`**).

**Shifting Fate (plan revision):** After each completed step, when **`EnableShiftingFate`** is `true`, the **Wizard** runs a lightweight re-weave evaluation (until **`MaxReweavesPerRun`** is exhausted). If strategy must change, the pending plan tail is replaced and **`planRevised`** is emitted. Operators may call **`POST /api/apprentices/{id}/reweave`** only while the Apprentice is **`Paused`** or **`Escalated`** (not while **`Running`**) to avoid racing the execution loop.

**Divine Intervention (DM escalation):** When retries exhaust (if **`EnableDivineIntervention`**) or the Apprentice calls in-process MCP **`petition_dungeon_master`**, the stream consumer correlates by tool **`CallId`**: records a pending petition on ToolCall, continues pumping so the tool runs, then parses ToolResult `notificationStatus` (`delivered` / `suppressed` / `failed`). Only **`delivered`** counts as already alerted; otherwise a fallback Critical Comm Link may fire. Status becomes **`Escalated`**, **`apprenticeEscalated`** is emitted. The DM resolves via **`POST /api/apprentices/{id}/intervene`** (slot acquired **before** any state mutation; capacity failure returns **`Apprentice.MaxReached`** with no persistence); guidance is injected into the next step prompt and **`apprenticeIntervened`** is emitted.

**The Conclave & Cast Sending (cross-Apprentice delegation):** Gated by **`Arcanum:Conclave:Enabled`**. The Conclave is the overarching network in which the Master coordinates multiple Apprentices. When enabled, an Apprentice may call the in-process MCP tool **`cast_sending`** (`goal`, optional `name`) to delegate a sub-task outside its immediate spell: the shared **`ConclaveArchmage`** service (also backing **`POST /api/apprentices/{id}/cast`**) mints a child Apprentice in the caller's workspace and returns its id, subject to **`MaxDelegationDepth`** and **`MaxDescendantsPerRoot`** (`ConclaveLineage`). The orchestrator detects the `cast_sending` tool result, stamps the child's **`ParentApprenticeId`** into the child's `CheckpointData` JSON (no schema change), emits **`castSent`**, and best-effort **`StartAsync`** the child through the atomic concurrency gate. Lineage surfaces on **`ApprenticeDetailDto.ParentApprenticeId`** (a `[NotMapped]` entity convenience property hydrated from the checkpoint).

**Simulacrum (parallel steps):** A **`PlanStep`** may set **`isParallel: true`**. Contiguous parallel steps form a Simulacrum group executed concurrently via **`Task.WhenAll`**, bounded by **`Arcanum:Apprentices:MaxSimulacra`** (default 3, clamp 1–10) using a `SemaphoreSlim`. Each branch runs in its **own** `AsyncServiceScope` — its own `IArcanumIntelligenceProvider` and pooled `ArcanumDbContext` — so no EF Core `DbContext` is shared across threads; branch inference is **stateless** (no shared `SessionId` writes). All branches complete before the orchestrator persists every step result and advances **`CurrentStep`** past the group on its single context (single-writer), then runs one **Shifting Fate** evaluation for the group. Emits **`simulacrumStarted`** / **`simulacrumCompleted`**. Note: the shared in-process MCP server serializes tool I/O across branches, so parallelism primarily reduces inference latency.

**Apprentice statuses:** `Idle`, `Planning`, `Running`, `Paused`, `Escalated`, `Completed`, `Failed`, `Cancelled`. **`Escalated`** is non-terminal and awaits DM intervention; it is not auto-resumed on host restart.

**Chronicle event types (lifecycle):** `apprenticeStarted`, `planGenerated`, `stepStarted`, `stepRetrying`, `stepCompleted`, `stepFailed`, `planRevised`, `apprenticeEscalated`, `apprenticeIntervened`, `apprenticePaused`, `apprenticeResumed`, `apprenticeCompleted`, `apprenticeFailed`, `apprenticeCancelled`, `eventsDropped` (slow-reader backpressure marker), plus pass-through `toolCall`, `toolResult`, `warded`, `wardResolved`.

**Chronicle:** **`ChronicleHub`** (per-Apprentice bounded channel, `DropOldest`) decouples execution from **`GET /api/apprentices/{id}/chronicle`** SSE. When a subscriber's channel is full, the oldest event is dropped and an **`eventsDropped`** marker is emitted so operators know the stream is lossy. Late connect replays plan state from DB, emits **`apprenticeEscalated`** when status is **`Escalated`**, then streams live. Pass-through Wizard events (`toolCall`, `toolResult`, `warded`, `wardResolved`) are flattened on the wire (no nested `wizardEvent`).

**Control API:** **`POST .../start|pause|resume|cancel|reweave|intervene`** delegate to **`IApprenticeRuntime`**. Pause cancels the in-flight step CTS (without disposing it — disposal happens in **`CleanupExecution`** after the task drains); **`cancel`** follows the same cancel-not-dispose pattern so the run exits cooperatively without **`ObjectDisposedException`** overwriting **`Cancelled`** with **`Failed`**. Resume continues from **`CurrentStep`**; intervene resumes from **`Escalated`** only.

**CLI stubs:** **`arcanum apprentice create|start|chronicle`** print route tables (The Forge stub pattern).

### 5.7.1 A2A and The Conclave

External door into The Conclave: A2A **server** (inbound → Apprentices) and **client** (`dispatch_sending`). Layered gates: `Conclave:Enabled` + `A2A:Enabled` + Server/Client flags; per-call `IOptionsMonitor` (route mapped at boot). Packages AOT-clean (`verify-aot-il-warnings.sh`).

**Server:** mapped under `A2A:ServerPath` on `/api` (API key required) — **no** unauthenticated `/.well-known/agent-card.json`. Handler mints Apprentice via `ConclaveArchmage`, relays Chronicle to A2A task states. Workspace: `A2A:DefaultWorkspace` → `Host:Workspace` → CWD; empty `Campaigns:AllowedRoots` still denies.

**Client:** `dispatch_sending` validates URL via allowlist (if non-empty) **and** `OutboundUrlGuard`; `MaxExternalTasks` semaphore (non-blocking reject); depth not enforced at MCP layer (same limitation as `cast_sending`). Chronicle: `sendingDispatched`/`Completed`/`Failed` on caller stream. Agent Card ("Heraldry") built per-request from settings.


## 6. `WebApplication.CreateSlimBuilder` vs `CreateBuilder`

**Decision:** Use `CreateSlimBuilder` for the `serve` command.

- Smaller default service graph — fewer registered defaults for trimming/AOT to analyze.
- Explicit opt-in for features that full `CreateBuilder` wires by default.
- When the product grows (e.g. SignalR), services must be consciously added.

---

## 7. Kestrel URL binding

Default: **loopback only, HTTP port from `Arcanum:Host:Port`** (default 5001). `ARCANUM_HOST_ANY=1` (or `Arcanum:Host:ListenAny`) switches to **HTTPS-only** `ListenAnyIP` on `Arcanum:Host:Https:Port` for container / LAN publish. `Api.DevHost` always uses `ListenLocalhost`.

Both `arcanum serve` and `Api.DevHost` call **`ArcanumKestrelConfigurator`** (`Api/Hosting`), which:

1. Sets `KestrelServerOptions.Limits.MaxRequestBodySize` once globally (applies to all listeners).
2. **Loopback (`ListenAny` false):** binds plaintext HTTP on `Host:Port`; when `Arcanum:Host:Https:Enabled` is `true`, loads the certificate via **`HttpsCertificateLoader`** and adds a second TLS listener on `Arcanum:Host:Https:Port`.
3. **All-interfaces (`ListenAny` / `ARCANUM_HOST_ANY`):** requires `Host:Https:Enabled` and a loadable certificate; binds **only** `ListenAnyIP(HttpsPort)` with TLS. Plaintext any-IP HTTP is never bound. Startup fails before binding if HTTPS is disabled or the certificate cannot be loaded.

On loopback, HTTP remains enabled when HTTPS is on — HTTPS is additional, not a replacement. Cert load failures use a sanitized message (path, PFX/PEM mode, generic reason; never the password).

Self-signed certificates generated by Compendium use loopback SANs only (`localhost`, `127.0.0.1`, `::1`) and are **not** installed into the OS trust store. Remote clients connecting by hostname/IP under ListenAny need a certificate whose SAN includes that name. Clients (CLI, Forge, doctor) do **not** bypass TLS validation.

`GET /api/meta` exposes `HttpUrl` (`null` when ListenAny — HTTP unbound; otherwise `http://localhost:{Port}`), `HttpsEnabled`, `HttpsPort`, and `HttpsUrl` (`null` when HTTPS is not bound; otherwise `https://localhost:{HttpsPort}`).

---

## 8. HTTP JSON and Minimal API design (`Api` project)

### 8.1 Wire contract: the `ApiResponse<T>` envelope

```csharp
public sealed record ApiResponse<T>(T? Data, bool IsSuccess, Error? Error, string? TraceId = null);
```

- **`ApiResponse<T>`** is the default envelope for JSON under **`/api`**; streaming and OpenAI compatibility are exceptions (§4.3, §8.5). `sealed record` for value equality and immutability.
- `Error?` is literal `null` on success. `TraceId` from `Activity.Current?.Id ?? HttpContext.TraceIdentifier`.
- `ApiResponse<T>.FromResult` is the single mapping point from `Result<T>` to wire envelope.
- **404 bodies:** JSON routes under `/api` return an `ApiResponse<T>` envelope on **404** (for example `Campaign.NotFound`, `Session.NotFound`) — not an empty body. Use `Results.Json(..., ArcanumJsonContext.Default.ApiResponse…, statusCode: 404)` or `Results.NotFound(envelope)` so clients always receive `isSuccess`, `error`, and `traceId`.

### 8.2 `ArcanumJsonContext` — source-generated, public

CamelCase source-gen context for HTTP wire types (index 0 of resolver chain). Every `ApiResponse<T>` payload and `/v1` DTO needs `[JsonSerializable]`. Separate contexts: `GrimoireJsonContext`, `ConfigurationJsonContext` (Core), `McpJsonSerializerContext` / `McpConfigJsonSerializerContext` (Infrastructure). `[JsonPropertyName]` only for external snake_case/spec wires (OpenAI `/v1`, MCP JSON-RPC, selected NDJSON tool fields) — not arbitrary `/api` DTOs.

### 8.3 Service registration in `AddArcanumApiServices`

Registers Infrastructure + daemon services, `ApiKeyEndpointFilter`, OpenAPI/JSON (`ArcanumJsonContext` head of chain), named OpenAI `HttpClient`, `IChatClientFactory`, tokenizer, scoped `WizardIntelligenceProvider`. Singletons use `IOptionsMonitor`; scoped/request use `IOptionsSnapshot`.

### 8.4 Returning the envelope from a Minimal API handler

Successful endpoints use `Results.Ok(ApiResponse<T>.FromResult(result, traceId))`. Failable endpoints use `Results.Json` with the source-generated `JsonTypeInfo` and an explicit HTTP status code. No anonymous DTOs; no reflection-based model binding.

**Selected status contracts:**

- **`POST /api/intelligence/ping`** — `ApiResponse<PromptResponseDto>` on every path: **400** for request/reasoning validation, **200** on success, and shared `ArcanumErrorMapper` status for inference failures (for example 404/403/400/503/500 by stable code). The payload contract is detailed in §8.10.

- **`POST /api/intelligence/human-response`** — **400** validation (including answer UTF-8 byte length above `Arcanum:Sessions:MaxEntryContentBytes`); **404** + `ApiResponse<bool>` failure when no waiter exists for `promptId` (`Intelligence.HumanPromptNotFound`); **200** + `ApiResponse<bool>` with `Data: true` when the answer is accepted.

- **`POST /api/mcp/reload`** and **`POST /api/intelligence/arsenal`** — Optional JSON body **`OptionalWorkspaceRequest`** (`{ "workingDirectory": "..." }` only). Responses remain `ApiResponse<T>` as today.

### 8.5 NDJSON streaming pipeline

`/api/intelligence/ping-stream` uses NDJSON (`application/x-ndjson`) for real-time token streaming:

- **Server:** Events serialized via `Utf8JsonWriter` + `ArcanumJsonContext`, newline-terminated, flushed per event. Writer: **`InferenceExecuteWriter`** (also used by spell/prompt `execute-stream`).
- **Wire shape:** Each line is an `IntelligenceEvent` with **camelCase string** discriminator **`type`**: **`"status"`**, **`"sessionBound"`**, **`"conversationBound"`** (deprecated alias emitted alongside **`sessionBound`** for one release), **`"token"`**, **`"reasoning"`**, **`"result"`**, **`"error"`**, **`"toolCall"`**, **`"toolResult"`**, **`"warded"`**, **`"wardResolved"`**, **`"toolError"`** (tolerated tool exception, emitted immediately before its `toolResult`; §10.2.1). The enum is annotated with `[JsonConverter(typeof(JsonStringEnumConverter<IntelligenceEventType>))]` and per-member `[JsonStringEnumMemberName]` so the AOT JSON source generator emits the canonical strings. **`PingRequest.SessionId`** continues a Grimoire thread; when omitted the hub creates a new session on first assistant turn.
- **Reasoning frame:** `type:"reasoning"` carries a typed, client-safe payload separate from answer `data`: `{"type":"reasoning","message":"client-safe summary","reasoning":{"text":"client-safe summary","output":"summary"}}` (the shared event envelope may also contain its normal null/default members). `reasoning.output` is exactly `none`, `summary`, or `full`; projected frames use `summary` or `full`. Provider `ProtectedData` is deliberately absent.
- **Disconnect / cancellation (`InferenceExecuteWriter`, ADR 0003):** `Arcanum:Intelligence:DisconnectPolicy` defaults to **`Auto`**. With an `Idempotency-Key`, continue-then-replay — do **not** link `RequestAborted` to the inference CTS; drain the hub enumerator and keep exact-byte capture so the claim may Complete. Without a key, cancel inference → claim Abandoned. Either way, ledger any provider-billed partial usage and reconcile/release the reservation. Inference wall-clock timeout (`Arcanum:Intelligence:InferenceTimeoutSeconds`) → **`Hub.Timeout`** error frame; host/caller cancellation → sanitized failure frame (**not** labeled timeout); other infra faults → sanitized error frame (detail stays in logs). Oversized capture after disconnect → Abandoned/non-replayable; never Complete a partial response.
- **Clients (`ArcanumApiClient` and The Forge):** `StreamReader` reassembles transport-fragmented UTF-8 into complete lines, including multibyte characters split across transport reads. Before strict source-generated deserialization, an AOT-safe `Utf8JsonReader` scan validates the root `type`. Canonical values are matched case-insensitively and normalized before `ArcanumJsonContext` / `TheForgeJsonContext` deserialization; a truly unknown, nonblank future string is silently skipped so later frames continue. Invalid JSON, a missing/non-string/blank discriminator, or any whitespace-padded discriminator is **malformed** and retains the surface's diagnostic behavior. This narrow pre-scan does not install a permissive enum converter or reflection serializer: direct source-generated deserialization remains strict. The terminal **`result`** event carries native **`usage`** (`prompt_tokens`, `completion_tokens`, `total_tokens`, optional `cached_tokens`, optional `reasoning_tokens`) on the `IntelligenceEvent` payload; **`data`** still duplicates **`total_tokens`** as a decimal string for backward compatibility, while the final answer remains in accumulated **`token`** frames and the result `message`. Assistant text is never reconstructed from legacy result `data`.

### 8.6 Request Delegate Generator

`<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>` on `Api` ensures Minimal API endpoints in a referenced class library are source-generated.

### 8.7 Session-Based Consolidation (Campaign Logger)

Three mechanisms trigger Campaign Log consolidation:

1. **Message-count threshold** (`CampaignLogThreshold`) — safety valve for unbounded growth.
2. **Idle timeout** (`CampaignLogIdleTimeoutMinutes`) — natural session boundary.
3. **Explicit rest** — `POST /api/sessions/{id}/rest`.

The queue consumer resolves **`IArcanumIntelligenceProvider`** in a per-item DI scope alongside **`IGrimoireRepository`**, loads the session header via **`GetSessionHeaderAsync`**, and batches rows with **`CreatedAt > (LastSummarizedMessageAt ?? DateTime.MinValue)`**. It builds a stateless **`PingRequest`**: empty `Prompt`, `StatelessMessages` (system persona + user payload with prior summary and batched turns), **`SkipSpellRouting: true`**, **`DisableMcpTools: true`**, **`UnattendedMode: true`**, **`Model`** from **`Arcanum:FastModel`** when set else **`Arcanum:DefaultModel`**, else omitted for first-provider fallback, and **no** `SessionId` so the hub does not append a new **`Entry`**. On **`ExecutePromptAsync`** success, **`UpdateSessionCampaignRollupAsync`** atomically persists the LLM text into **`Session.Summary`** and sets **`LastSummarizedMessageAt`** to the latest batched entry time. On **`Result.IsFailure`** or exception, **no** DB update — the session remains eligible on the next sweep. The intelligence hub **reads** `Summary` for optional read-time compression (§10.2.3).

Under the same **Session-Based Consolidation model of AI memory**, **Chronosync reporting** (§5.4.2) addresses **spatial** drift: thread lines and `DomainType` deltas vs the last persisted `PatternSnapshot`, not chat log length. Campaign Logger and Chronosync are separate triggers; the hub folds `ChronosyncReport` into the system prompt via `PingRequest.ChronosyncDelta`; MCP context remains separate.

### 8.8 OpenAI `/v1` Chat Completions compatibility subset

`OpenAiV1Endpoints` advertises a **Chat Completions compatibility subset** (ADR 0001) — honesty fixes only, not full OpenAI API parity. Moderations/images/audio remain **`501 not_supported`**. Polymorphic `content` (string | parts) is AOT-safe; unsupported part types / over `MaxContentPartsPerMessage` → **400** `invalid_value` before mapping. Vision parts map to MEAI `TextContent`/`UriContent`/`DataContent` (§10.2.4).

**Parameters applied** (`ApplyInferenceParameters`): temperature, top_p, max tokens, penalties, seed, stop, response_format. Reasoning controls are additive: `reasoning_effort` = `none|minimal|low|medium|high|xhigh`, `reasoning_budget` = positive integer, and `reasoning_output` = `none|summary|full`. `reasoning_effort` and `reasoning_budget` are mutually exclusive and map to native `PingRequest.reasoning`; capability validation runs before provider I/O for buffered and `stream:true` requests. `reasoning_output` is an Arcanum-local projection/exposure preference and is passed to Microsoft.Extensions.AI only as a best-effort hint. It is not a guaranteed provider wire control, and Arcanum does not patch an unsupported `reasoning_output` field into provider JSON. When omitted, the resolved capability chooses `full` when `SupportsFull`, otherwise `summary` when `SupportsSummary`; `AllowsClientOutput` is required, and streaming also requires `SupportsStreaming`. Native effort/output and configured control-support/wire-dialect enums are strict string-only AOT contracts. OpenAI `reasoning_effort` and `reasoning_output` are also string-only. A numeric enum (defined or undefined) or an unknown enum string fails JSON binding before semantic validation. `n` must be `1` when present. Client `tools`/`tool_choice` rejected **400** `unsupported_parameter` unless `ClientToolForwarding:Enabled` (then schema/count validation; §8.8.3).

**Responses:** buffered answers remain in `choices[].message.content`; additive reasoning is in `reasoning_summary` and/or `reasoning_content`. Streaming answers remain in `choices[].delta.content`; reasoning uses the same additive fields on the delta, in provider order. A client that ignores the fields still reads an unchanged answer. Usage keeps `completion_tokens` and `total_tokens` authoritative and projects the reasoning subset at `completion_tokens_details.reasoning_tokens`; cached prompt subsets use `prompt_tokens_details.cached_tokens`. Buffered `message.tool_calls` still reports server-executed calls (§8.8.1); streaming SSE includes keep-alives and usage only when requested by `stream_options`. Semantic reasoning failures are typed OpenAI error bodies/chunks, never `delta.content`: they use HTTP **400**, `type:"invalid_request_error"`, `param:"reasoning"`, and the reachable stable code `invalid_reasoning_options` (effort plus budget), `invalid_reasoning_budget` (budget outside 1–2,097,152), `unsupported_reasoning_control`, `reasoning_budget_exceeds_model_limit`, or `unsupported_reasoning_output`. Numeric/unknown reasoning enum JSON never reaches those semantic branches; strict binding returns HTTP **400** `invalid_request_error`, code `invalid_json`, and no parameter. Unknown model → **404** `model_not_found`; tool-loop/timeout → **503** `server_error`.

**Current streaming projection topology:** production `/v1/chat/completions` obtains native `IntelligenceEvent` frames from `WizardIntelligenceProvider` (`TurnExecutionCoordinator` → `IntelligenceEventProjection`) and maps them to SSE chunks in `OpenAiV1Endpoints`. That endpoint mapper is the authoritative compatibility implementation. `OpenAiSseProjection` is a separate semantic helper/characterization path, not the projection instance used by the production route. The two paths share reasoning-field and typed-error rules only; `OpenAiSseProjection` does not define production terminal usage chunks, `stream_options.include_usage`, or tool-argument fragmentation. Those wire contracts are covered directly by production endpoint tests rather than by an exact-parity claim.

#### 8.8.1 Server-executed tools on `/v1` (buffered + streaming tool_calls)

Arcanum executes MCP tools server-side; `/v1` surfaces calls for observability/replay. Buffered: `PromptTurnResult.ToolCalls` → `message.tool_calls`. Streaming: `ToolCall` events → `delta.tool_calls` (40-char argument fragments; monotonic per-response `index`; fresh `call_…` ids). **`toolResult` never surfaced** on `/v1`. Forwarding mode preserves provider-minted ids and returns `finish_reason: "tool_calls"` without executing client tools. Richer native surface: `/api/intelligence/ping(-stream)`.

#### 8.8.2 `GET /v1/models` capability enrichment

`ModelInfoBuilder` is shared with `GET /api/models`. Additive OpenAI fields: `context_window`, `supports_vision`, `provider_name`/`provider_type`, `supports_tools`/`supports_streaming` (always true), plus the same optional typed `reasoning` capability object returned by the native endpoint.

#### 8.8.3 Client tool security (forwarding mode)

When `ClientToolForwarding:Enabled`, Sanctum/Ward/tool audit do **not** apply to client-supplied tools (provider executes). Default remains reject.

### 8.9 NDJSON anti-buffering headers (`/api/intelligence/ping-stream`)

The NDJSON streaming endpoint sets `Cache-Control: no-cache` and `X-Accel-Buffering: no` (parity with the SSE endpoint in §8.5/§8.8) so reverse proxies (nginx, Cloudflare, k8s ingress) do not coalesce incremental frames.

### 8.10 Buffered `/api/intelligence/ping` envelope

The buffered ping endpoint wraps a **`PromptResponseDto`** (Core) inside `ApiResponse<T>`: `text` (assistant answer only), `usage` (native token counts, including additive top-level `reasoning_tokens`), `toolCalls` (the assistant-issued calls executed server-side, when any), `finishReason`, and `reasoning` (an ordered array of `{ text, output }` client-safe segments; empty by default). Reasoning is never concatenated into `text`. Previously the envelope held only the assistant text as a bare `string`; clients now get the full turn context without falling back to NDJSON.

### 8.10.1 Mana counter (`POST /api/intelligence/mana`)

Read-only token estimate (`ManaCountRequest` → `ManaCountResult`); no inference/Grimoire writes. Optional tool-schema estimate. **400** when neither `messages` nor `prompt` supplied.

### 8.11 Daemon event SSE bus (`GET /api/events/daemon`)

In-process `IEventBus` → bounded per-subscriber channels (`EventBus:ChannelCapacity`, `DropOldest`). Wire: `text/event-stream` `DaemonEvent` frames + best-effort `[DONE]`. Caps: global `MaxSseConnections` + per-type `MaxSseConnectionsPerType` via `SseConnectionGate` → **503** `Api.TooManyConnections`. Anti-buffering headers; API-key on `/api` group. Rate limit admits HTTP request only, not open-stream duration.

### 8.12 Configuration API (`GET` / `PUT` / `POST /api/config`)

Read: redacted secrets/URLs (`***`). Write: merge redacted placeholders from current snapshot; validate; atomic temp+rename to `arcanum.json`; encrypt provider keys + HTTPS cert password (`dp:v1:`). Validate-only: no write. Hot-reload via `reloadOnChange`; `ARCANUM_*` env still wins over file. Status: **400** `Configuration.ValidationFailed`, **500** `Configuration.WriteFailed`.

### 8.13 MCP server event SSE bus (`GET /api/events/mcp`)

`McpConnectionManager` publishes `McpServerEvent` on state changes. Same SSE back-pressure/caps/auth as §8.11.

### 8.14 Spell Management API (`/api/spells`)

Workspace resolution: `?workspace=` → `Host:Workspace` → CWD. CRUD needs resolvable workspace; empty `Spells:AllowedWorkspaceRoots` denies all (**403** `Spell.PathNotAllowed`). Built-ins under `~/.config/arcanum/spells/` are read-only (`Spell.BuiltinReadOnly`). Format: `SPELL.md` frontmatter + body; optional `SPELL.json` (legacy `SKILL.json` read fallback; writes always `SPELL.json`). Search shadow order: campaign > workspace > builtin. Versions: string labels `SPELL.v{label}.md` (`^[A-Za-z0-9.]+$`); activate swaps into `SPELL.md` and records `activeVersion`. Clone/cast/import quirks and status codes: §4.3. Per-workspace locks; delete only under `{workspace}/spells/{name}`.

### 8.15 Daemon job management (`/api/daemons`, `/api/executions`)

**Route families:** `/api/unseen-servant/*` (+ deprecated `/api/daemon/*`) = Unseen Servant interval control; `/api/daemons/*` + `/api/executions/*` = job registry + execution history. Watermarks: §5.5.5. On-demand `POST .../run` waits for completion; scheduled path shares `DaemonRunner` single-flight per daemon. History process-local (`ExecutionHistoryLimit`); detail includes correlated ring-buffer logs.

### 8.16 Log ring buffer (`GET /api/logs`, `GET /api/events/logs`)

Serilog → `SerilogLogRingBufferSink` → in-memory ring (`Logs:RingBufferCapacity`, overwrite oldest). Query filters + `beforeSequence` cursor. Live SSE same caps as §8.11. Not persisted across restarts. Deferred sink registration avoids Build()-time logging DI deadlock.

### 8.17 Workspace registry and file browser/writer (`/api/workspaces`)

Campaign-backed when Grimoire ready (`persisted: true`); else in-memory. Writes gated by `Workspaces:EnableFileWrite` (default off) → **403** `Workspace.FileWriteDisabled`. Path policy: reject `..`/absolute; symlink escape → `Workspace.SymbolicLinkEscape`; revalidate before I/O. Atomic temp+rename for PUT/PATCH. Size clamps: §3.4. PATCH ordinal replace with ambiguous/not-found codes. HEAD contents returns size/`Last-Modified` only.

### 8.18 Session API (superseded — see §11.16)

The former bounded **in-memory** conversation layer (`InMemoryConversationRepository`, `/api/conversations`, `Arcanum:Conversations:*`) is **removed**. Search, export, analytics, CRUD, manual entry append, SSE live stream, and Campaign Log **`/rest`** are unified on **Grimoire-backed** **`/api/sessions`**. See **§11.16 Session lifecycle** for the authoritative contract.

### 8.19 Server lifecycle (PID file)

Default `~/.config/arcanum/arcanum.pid`; disable with null/empty. Startup fails if live PID present; stale overwritten. Shutdown deletes only if still this PID. DevHost vs serve collide on default path unless overridden.

### 8.21 The Proving Grounds (`POST /api/proving-grounds/trials/run`)

Ephemeral Trial + Inquisitors (`regex` / `jsonSchema` / `semantic` FastModel judge). Targets: spell / prompt / apprenticeGoal. Terminology strict — industry LLM-test jargon prohibited. Errors §8.23.

### 8.22 Metrics endpoint (`GET /metrics`)

Prometheus text `0.0.4` via `System.Diagnostics.Metrics` + hand-rolled exporter (no OTel/prometheus-net — AOT). Catalog: HTTP requests, inference duration/tokens, tool outcomes, SSE gauge, active sessions (scrape-time query), Sanctum breaches (+ runtime meters via `MeterListener`). Path outside `/api`/`/v1`. `Metrics:Enabled` → **404** when false. `RequireApiKey` default true; forced true on ListenAny. Auth: `X-Arcanum-Key` or Bearer.

### 8.23 Error code catalog and HTTP status mapping

Wire-stable codes live on `ErrorCodes` (Core). HTTP mapping authority: `ArcanumErrorMapper.ResolveStatusCode` (Api). `ResolveStatusCodeDefaultBadRequest` treats unmapped codes as **400** on Apprentice/Campaign/Spell/Prompt/ProvingGrounds routes while still honoring explicit **500** mappings (`ProvingGrounds.InferenceFailed`, `Workspace.WriteFailed`, `Workspace.DeleteFailed`, `Saga.SearchFailed`, `Hub.Error`). Unrecognized strings (including `Hub.Error` via default arm) → **500**. Keep in sync with `ErrorCodes.cs` / `ArcanumErrorMapper.cs` (`ArcanumErrorMapperTests`).

**Default / unmapped:** unlisted codes → **500**; `ResolveStatusCodeDefaultBadRequest` downgrades unmapped → **400** except the explicit **500** set above.

**/api vs /v1:** native `/api` uses `ApiResponse<T>` + codes below. OpenAI `/v1` uses the OpenAI error envelope (`message`/`type`/`code`/`param`); hub failures map similarly (e.g. tool-loop/timeout → **503** `server_error`; unknown model → **404** `model_not_found`). Client-tool forwarding surfaces OpenAI codes `unsupported_parameter` / `too_many_tools` / `invalid_schema` while Core codes remain `ClientTools.*`.

| Codes (grouped) | HTTP | Semantics |
|-----------------|------|-----------|
| `Validation.InvalidPrompt`, `InvalidBody`, `InvalidQuery`, `InvalidProviderType`, `AttachedFiles` | 400 | Request shape / bounds validation |
| `Hub.ToolLoop`, `Hub.Timeout` | 503 | Inference tool-round or wall-clock timeout |
| `Hub.Model` | 404 | Model not in any provider `models` |
| `Hub.Error` | 500 | Generic inference failure (mapper default arm) |
| `Campaign.NotFound`; `Session.NotFound` / `EntryNotFound`; `Grimoire.LoreNotFound`; `Apprentice.NotFound`; `Workspace.NotFound` / `FileNotFound`; `Spell.NotFound`; `Prompt.NotFound`; `Intelligence.HumanPromptNotFound`; `Mcp.ServerNotFound` / `ToolNotFound`; `Daemon.NotFound`; `Files.NotFound`; `Batches.NotFound` / `InputFileNotFound`; `Saga.NotFound`; `ProvingGrounds.SpellNotFound` / `PromptNotFound`; `Workspace.ReplacementNotFound` | 404 | Missing resource |
| `Campaign.InvalidPath` / `MaxReached`; `Session.Archived` / `InvalidStatus` / `TooManyEntries` / `EntryTooLarge` / `MemoryManagementDisabled` / `EmptyContent`; `Apprentice.Disabled` / `PendingQueueFull` / `InvalidGuidance` / `InvalidPlan` / `InvalidGoal` / `InvalidWorkspace`; `Workspace.NameEmpty` / `SymbolicLinkEscape` / `PathTraversal` / `DirectoryNotEmpty` / `ReplacementAmbiguous` / `PathIsDirectory` / `PathIsFile`; `Spell.NoWorkspace` / `InvalidWorkspace` / `InvalidName` / `NameCollision` / `BuiltinReadOnly` / `DuplicateVersion` / `InvalidVersion`; `Prompt.CodexPathNotContained` / `DuplicateVersion` / `InvalidName` / `InvalidVersion` / `InvalidRequest`; `Mcp.AmbiguousServer` / `MissingWorkspace` / `ServerNotRunning` / `AmbiguousTool` / `ToolError`; `Sending.TaskRejected`; `Security.BlockedOutboundUrl` / `IdempotencyKeyTooLong`; `Files.InvalidMimeType`; `Batches.InvalidEndpoint`; `Embeddings.ConfirmationRequired`; `ProvingGrounds.InvalidTrial` / `TooManyInquisitors` / `WorkspaceNotAllowed`; `Saga.NotEmpty`; `Scrying.VisionNotSupported` / `TooManyImages` / `UnsupportedMimeType`; `WebBrowsing.TooLarge` (reserved; today truncates) / `InvalidUrl`; `ClientTools.Disabled` / `TooMany` / `InvalidSchema`; `Guardrails.PiiDetected` / `Blocked`; `StructuredOutput.ValidationFailed` / `SchemaInvalid` | 400 | Domain validation / policy refusal (non-auth) |
| `Campaign.PathNotAllowed`; `Workspace.PathNotAllowed` / `AccessDenied` / `FileWriteDisabled`; `Spell.PathNotAllowed`; `Sending.Disabled` / `AgentNotAllowed`; `Mcp.WorkspaceNotTrusted` / `DiagnosticBlocked`; `Scrying.FeatureDisabled`; `WebBrowsing.SsrfBlocked` | 403 | Path/network/feature deny |
| `Security.MissingApiKey` | 401 | Missing/invalid API key |
| `Session.TooManyPinned`; `Apprentice.AlreadyRunning` / `Running` / `NotPaused` / `CannotReweave` / `NotEscalated` / `MaxReached` / `ConclaveDisabled` / `ConclaveDepthExceeded` / `ConclaveBreadthExceeded` | 409 | State conflict |
| `Sending.MaxTasksReached`; `RateLimit.TooManyRequests` | 429 | Concurrency / rate limit |
| `Workspace.FileTooLarge`; `Files.TooLarge`; `Scrying.ImageTooLarge` | 413 | Payload too large |
| `Sending.AgentUnreachable` / `AgentCardInvalid`; `CommLink.Suppressed` | 502 | Downstream / webhook failure |
| `Api.TooManyConnections`; `Connection.Unreachable`; `Embeddings.ProviderUnavailable` / `FeatureDisabled` | 503 | Capacity / provider unavailable |
| `Sending.TaskTimeout`; `Mcp.DiagnosticTimeout`; `Connection.Timeout`; `WebBrowsing.Timeout` | 504 | Downstream timeout |
| `Workspace.WriteFailed` / `DeleteFailed`; `ProvingGrounds.InferenceFailed`; `Saga.SearchFailed` | 500 | Explicit infra/search failures (never downgraded by DefaultBadRequest) |

**Ollama:** legacy `Ollama.*` codes removed with `OllamaSharp`; Ollama providers surface as `Hub.Error` like other `OpenAICompatible` providers.

### 8.24 OpenAI embeddings (`POST /v1/embeddings`)

Composes `IWeaveService` + tokenizer. `model` must match `Embeddings:Model` or omit → else **404** `model_not_found`. Long inputs: chunk + mean-pool/L2. `encoding_format` float|base64 (`EmbeddingBlobCodec`). Idempotency-Key supported. Errors: OpenAI envelope (**400** invalid input/chars; **503** when Weave unavailable).

### 8.25 HTTP response compression

Brotli+Gzip via ASP.NET ResponseCompression; early pipeline. Excludes `text/event-stream` and `application/x-ndjson`. `EnableForHttps` left false (framework default).

### 8.26 Persisted inference audit log

Opt-in JSONL (`Host:AuditLog:*`); dated files, owner-only, soft size + retention. Records successful turns only (ping / ping-stream / v1-completion today). Never throws into inference. Query: `GET /api/audit`.

### 8.27 Content guardrails (PII / toxicity / topics)

Opt-in (`Guardrails:Enabled` default false). Input PII (GeneratedRegex) → `Guardrails.PiiDetected`; toxicity/topics → `Guardrails.Blocked`. StreamingMode default **`buffered`**; explicit **`passthrough`** is honored with a configuration warning (ADR 0001). Audit JSONL + `GET /api/guardrails/audit`. Redacted matched spans only in logs/errors.


## 9. Native AOT and trimming

### 9.1 Why Native AOT

Zero runtime prerequisite for the shipping CLI; fast cold start for short verbs; smaller trimmed footprint; reduced reflection surface via source-gen JSON/RDG/hand-authored tools.

### 9.2 What is AOT-optimized today

- **`Cli` publish** (`<PublishAot>true</PublishAot>` on non-macOS RIDs) produces a native binary via ILCompiler over the full closure (`Cli` + `Api` + `Infrastructure` + `Core` + framework + third-party assemblies). macOS RIDs use folder-based self-contained publish (see Cli csproj notes on ld-prime).
- **`Infrastructure`** additionally sets `PublishAot` / `IsTrimmable` as a library signal so the ILCompiler analyzes it in the publish graph — it is not shipped as its own binary.
- **`Api` / `Core`** declare `<IsAotCompatible>true</IsAotCompatible>` to opt into AOT-oriented analyzers. Libraries in the closure should remain AOT-compatible to avoid blocking future hosts.
- **Command Center (Terminal.Gui 2.4.17)** lives only in `Cli`. Bootstrap is isolated in `CommandCenterApp`; any `IL3050`/`IL2026` suppressions are method-level there and must remain first-party-clean under `./scripts/verify-aot-il-warnings.sh`. If that gate fails for Terminal.Gui, fall back to a Spectre Command Center–lite with the same entry rules (document here) — do not leave a dead spike mode.

### 9.3 Tradeoffs and constraints

- **ConsoleAppFramework v5** is source-generated with zero reflection — the CLI layer has no AOT tradeoffs.
- **EF Core** compiled model is required (`dotnet ef dbcontext optimize`). Precompiled queries are disabled (`EFPrecompileQueriesStage = none`) because certain repository LINQ patterns are not yet compatible.
- **`dotnet build`** is warning-clean in Debug and Release. **`dotnet publish`** on macOS may show clang `.pcm` notices (toolchain noise, not IL diagnostics). **Homebrew `dotnet`** ships a `nonportable.txt` marker that makes Native AOT link keg-only OpenSSL/Brotli (`-lssl`, `-lbrotli*`); without library search paths this fails with `ld: library 'ssl' not found`. **`RetroDownfall.Arcanum.Cli`** adds conditional `LinkerArg` entries for common Homebrew prefixes when publishing on macOS; use the official Microsoft .NET install if you prefer not to depend on those paths. The same `ItemGroup` forces **`-ld_classic`** on macOS: Xcode 15+'s newer `ld64` linker can crash on large Native AOT object files with `ld: Assertion failed: (_addend == uniqueIndex && "too many large addends")` — a confirmed upstream bug ([dotnet/runtime#119380](https://github.com/dotnet/runtime/issues/119380)) that the CLI's growing command surface can trigger; the classic linker sidesteps it (emits a benign `-ld_classic is deprecated` warning).

### 9.4 AOT discipline for new code

- Every HTTP payload type needs a `[JsonSerializable]` registration on `ArcanumJsonContext`.
- Grimoire `PatternSnapshot` blobs use `GrimoireJsonContext` with explicit `JsonTypeInfo` — no reflection-based `JsonSerializer` overloads for those columns.
- MCP wire types use `McpJsonSerializerContext` exclusively — no reflection-based `JsonSerializer` overloads.
- Outbound Comm Link webhook bodies use `CommLinkInfrastructureJsonContext` / `WebhookPayloadDto` exclusively (`title`, `body`, `severity`, `source`, `timestampUtc`) — no `PostAsJsonAsync` with anonymous DTOs.
- Minimal API handlers must not return anonymous DTOs or use unbounded reflection-based model binding.
- New `AIFunction` tools must use hand-authored `JsonDocument` schemas, not `AIFunctionFactory.Create`.
- **`ArcanumSettings` and nested config POCOs must use `{ get; set; }`**, not `init`. `EnableConfigurationBindingGenerator` silently skips `init`-only properties (dotnet/runtime#107856); reflection binding still works, so unit tests that call `.Bind()` can hide the bug until `arcanum serve` runs.

## 10. Intelligence pipeline

### 10.1 Architecture

The intelligence layer follows a **provider pattern**: `Core` defines `IArcanumIntelligenceProvider`, `Api` implements **`WizardIntelligenceProvider`** as a thin facade over **`TurnExecutionCoordinator`** / **`TurnEngine`** (ADR 0004). The engine owns the logical run and emits semantic `TurnEvent`s; buffered / NDJSON / OpenAI-SSE shapes are projections. HTTP writers own serialization and exact-byte idempotency capture.

- **`TurnEngine`** — logical-run producer (`ITurnEventSource`): preflight, reservation/run lifecycle, `TurnContextSeed` (once), provider candidates + fallback, `ProviderAttemptContext` (per attempt), **one** model/tool loop (`WizardIntelligenceProvider.RunInferenceAttemptAsync` parameterized by `TurnResponseMode`), validation, finalization. `ITurnPipelineRunner` remains a thin emitter adapter (buffered drain vs streaming map) into `TurnEventEmitter`; it does not own a second tool loop.
- **`TurnExecutionCoordinator`** — sole semantic consumer; applies exactly one of `BufferedTurnProjection`, `IntelligenceEventProjection`, or `OpenAiSseProjection` per request. Does not serialize HTTP.
- **`IModelCallExecutor`** (Core) — sole chat-provider invocation boundary (`ExecuteBufferedAsync` / `ExecuteStreamingAsync`) with `ModelCallPurpose` tagging. On Microsoft.Extensions.AI **10.8.1**, it classifies `TextContent` as answer and `TextReasoningContent` as reasoning, preserves raw provider content for same-provider continuation, and surfaces `UsageDetails.ReasoningTokenCount` without reconstructing hidden reasoning. Spell routing and Lexicon extraction also use the executor (auxiliary budgets and no client reasoning projection).
- **`ProviderResolver`** (`Core.Configuration`) maps `PingRequest.Model` (or `ArcanumSettings.DefaultModel`, or the first configured model) to a `ProviderSettings` row and canonical model id — no hard-coded default model literals. Internal callers (Campaign Logger) supply an explicit `PingRequest.Model` from **`Arcanum:FastModel`** when set, else **`Arcanum:DefaultModel`**, before falling back to the first configured model.
- **`IChatClientFactory`** (`ChatClientFactory`, singleton) resolves `AiProviderKind.OpenAICompatible` (including Ollama via its own `/v1` endpoint) via **`Microsoft.Extensions.AI.OpenAI`** / OpenAI .NET `ChatClient` + `IHttpClientFactory` + custom `endpoint` + `AsIChatClient()` with `OpenAiRequestAugmentingHandler`. A second overload, `ResolveClientAsync(ProviderSettings, string, CancellationToken)`, builds a lease for an explicit (provider, model) pair — bypassing `ProviderResolver` selection entirely — so the resilience fallback loop (below) can target a specific candidate.
- **Microsoft.Extensions.AI** provides the shared `IChatClient` surface for routing, tools, and streaming.
- **`ProviderResolver.ResolveCandidates(ArcanumSettings, string?, IProviderHealthTracker?)`** (Core) is the fallback-aware counterpart to `TryResolveProviderForModel`. It resolves the same target model (request model → `DefaultModel` → first provider's first advertised model) and returns the set of providers advertising it, in configured order. When the health tracker argument is `null` or `Arcanum:Resilience:Enabled` is `false`, it returns at most one candidate — identical to `TryResolveProviderForModel` (zero behavior change). When resilience is enabled, it excludes providers `IProviderHealthTracker.IsHealthy` reports as unhealthy; if that would leave zero candidates, the first match is returned anyway so the operator sees the real inference error instead of a spurious "no providers" failure. `TryResolveProviderForModel` itself is unchanged and remains the single-provider entry point used when resilience is disabled.
- **Provider health tracking** (`Core.Resilience` / `Infrastructure.Resilience`): `IProviderHealthTracker` is an in-memory, `ConcurrentDictionary`-backed singleton recording `ProviderHealthStatus` (name, `IsHealthy`, `LastChecked`, `ConsecutiveFailures`) per provider. Providers not yet observed are assumed healthy. `MarkFailed`/`MarkHealthy` are called both reactively (by the hub on a connectivity failure) and periodically (by `ProviderHealthProbeService`, a `BackgroundService` that probes every configured provider via `GET /models`). A provider becomes Unhealthy once `ConsecutiveFailures` reaches `Arcanum:Resilience:HealthFailureThreshold`; below that it is Degraded but still used. The probe service idles (1-second poll of `Enabled`) when resilience is disabled, and resets all tracked providers to Healthy on an `Enabled` true→false transition. State is in-memory only — a host restart starts every provider Healthy. `HealthChanged` fires on transitions but has no subscribers yet (reserved for future SSE observability).

### 10.2 `WizardIntelligenceProvider` design

**Facade:** Public `ExecutePromptAsync` / `StreamPromptAsync` build `TurnExecutionRequest` and call `TurnExecutionCoordinator` (Buffered / IntelligenceEvent projections). `HasIdempotencyKey` comes from `TurnIdempotencyAmbient` (set by the idempotency endpoint filter when the `Idempotency-Key` header is present) — not from `PingRequest`.

**Model resolution:** `ProviderResolver.TryResolveProviderForModel` on the current `ArcanumSettings` snapshot. Explicit request/default model strings must match a configured `models` entry, or resolution fails (configuration error).

**Reasoning request/capability contract:** Native requests use `reasoning.effort` (`none|minimal|low|medium|high|extraHigh`), `reasoning.budgetTokens` (1–2,097,152, additionally capped by the model), and `reasoning.output` (`none|summary|full`). A model object declares `reasoning.controlSupport`, `supportsSummary`, `supportsFull`, `supportsStreaming`, `reportsReasoningTokens`, `allowsClientOutput`, `wireDialect`, and optional `maxBudgetTokens` (§3.4). Stable native failures are `Validation.InvalidReasoningEffort`, `Validation.InvalidReasoningOutput`, `Validation.ReasoningEffortAndBudgetMutuallyExclusive`, `Validation.InvalidReasoningBudget`, `Validation.UnsupportedReasoningControl`, `Validation.ReasoningBudgetExceedsModelLimit`, and `Validation.UnsupportedReasoningOutput`; §8.8 lists their OpenAI code mappings. Validation is repeated for the actual direct or fallback candidate before its provider call, so explicit controls are never silently dropped.

**Provider mapping:** `ReasoningChatOptionsAdapter` maps effort/output through typed MEAI `ChatOptions.Reasoning`. MEAI 10.8.1 has no `Minimal` effort value, so OpenAI `minimal` is applied through a fresh concrete `ChatCompletionOptions`. Numeric budgets require one explicitly configured nonstandard closed dialect: `openRouter` → `reasoning.max_tokens`; `topLevelReasoningBudget` → top-level `reasoning_budget`; `anthropicThinking` → `thinking:{type:"enabled",budget_tokens:N}`. `standard` is the typed MEAI/OpenAI path and rejects numeric budgets. No provider/model-name detection occurs, and a request without reasoning leaves provider JSON unchanged.

**Fallback loop (`Arcanum:Resilience:Enabled` only):** When resilience is enabled and a health tracker is registered, both `ExecutePromptAsync` and `StreamPromptAsync` replace the single-resolution call with `ProviderResolver.ResolveCandidates` and try up to `Arcanum:Resilience:MaxFallbackAttempts` candidates in order. On a pre-commit connectivity failure (`HttpRequestException`, an HTTP timeout, or the inference wall-clock timeout) the hub calls `IProviderHealthTracker.MarkFailed` for that candidate, logs a `Warning` with the provider name and attempt count, and retries the next candidate; on success it calls `MarkHealthy` (clearing prior failures). Non-connectivity failures are returned immediately. Provider commitment occurs before projection on the first non-empty answer delta, **any** provider reasoning item (visible text or protected-only data, even when client output is disabled or buffered), a complete actionable tool proposal, or an empty successful round. After commitment a connectivity failure terminates the run: there is no provider fallback and the outer no-tools compatibility restart is also prohibited. When resilience is disabled (the default), both methods retain one candidate and one attempt.

**Reasoning separation and safety:** Answer and ephemeral reasoning have independent accumulators. Reasoning never enters answer token accumulation, structured-output validation, `PromptTurnResult.Text`, Grimoire assistant entries, audit/log text, or persistence. Client-safe reasoning projects only when the resolved model allows the requested output (and, for live frames, declares streaming support). MEAI `TextReasoningContent.ProtectedData` may remain on the raw in-memory assistant message only so the **same provider** can continue after a tool result; it is never projected, logged, audited, traced, exported, or stored. Buffered guardrails and strict structured-output mode hold both answer and reasoning frames until validation succeeds. Corrective strict retries discard the rejected candidate's reasoning/answer and release only the accepted replacement; output guardrails inspect the accepted answer plus projectable reasoning. Explicit guardrail passthrough retains its existing leakage warning. Reasoning is not transferred from the Master to Apprentices, Apprentice prompts/checkpoints/results, or Chronicle persistence.

**Streaming:** `StreamPromptAsync` yields `IntelligenceEvent` objects — `status` (model checks), `sessionBound` (canonical session id; `conversationBound` emitted as deprecated alias), `reasoning` (typed client-safe reasoning, separate from answer), `token` (incremental answer text), `toolCall` / `toolResult` (tool execution diagnostics), `toolError` (tolerated unexpected tool exception; §10.2.1), `warded` / `wardResolved` (Forbidden Arts gate; §11.14), **`result`** (structured **`usage`** plus legacy **`data`** total string), `error`.

**Forbidden Arts (wards):** After the hub emits `toolCall` for a gated tool, `ExecuteToolCallWithWardAsync` may emit `warded`, block on **`IWard.WardAsync`** until the operator resolves via **`POST /api/wards/{id}`** or the ward times out, then emit `wardResolved` and either execute the tool or feed a synthetic denial as `toolResult`. Buffered `/api/intelligence/ping` uses the same gate (the HTTP request may block for up to `Arcanum:Ward:TimeoutSeconds`). Per-campaign: **`CampaignSettings.RequireWardForForbiddenArts`** defaults to **`true`** on newly registered campaigns; set `false` via `PUT /api/campaigns/{id}` to opt out. When no campaign matches `WorkingDirectory`, wards apply when host `Ward:Enabled` is `true`.

**Sanctum (execution boundary):** After a tool call passes the Ward gate (or bypasses it), **`EnforceSanctumAsync`** runs before **`InvokeToolCallAsync`** when the request **`WorkingDirectory`** matches a campaign with **`SanctumConfig.Enabled`**. **`SanctumGuard`** validates disabled tools, filesystem paths (canonical resolution with symlink checks via **`WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck`**), and outbound Comm Link webhook URLs for **`use_commlink`**. **`SanctumMode.Strict`** blocks with a synthetic tool result; **`AuditOnly`** logs a breach and allows execution. Orthogonal to Wards: a Ward-allowed tool may still be Sanctum-blocked (§11.15).

**Operator-safe errors:** Inference failures use fixed generic strings for clients and Grimoire; full exceptions are logged internally only.

### 10.2.1 Built-in tools and MCP workspace tools

Tool registration is built in `WizardIntelligenceProvider` per inference attempt:

1. `ArcanumLocalTimeTool` (`get_local_system_time`) — always registered. Returns the current local system time in ISO 8601.
2. `ArcanumSystemInfoTool` (`get_arcanum_system_info`) — always registered. Returns host OS description, CPU architecture, and .NET runtime version.
3. `ArcanumSpellScriptTool` (`run_spell_script`) — registered when the active spell (or any **Arcane Resonance** dependency) has `scripts/` files (even when `DisableMcpTools` is true). Scripts are resolved across the primary spell and all resonant dependencies; duplicate filenames across spells return a tool-result error (not a host exception).
4. MCP tools — merged from `McpConnectionManager.GetAvailableToolsAsync` unless `DisableMcpTools` is true.

**Artifact Attunement:** When the active spell's **`SPELL.json`** `declaredTools` array is non-empty, **`WizardIntelligenceProvider`** restricts the advertised MCP toolset (both in-process **`arcanum-internal`** and external **`mcp.json`** servers) to that allowlist. Hub-native tools (`get_local_system_time`, `get_arcanum_system_info`, `run_spell_script`) are exempt. Empty or absent `declaredTools` leaves all MCP tools available. Excluded tool names are logged at **Debug**. A dependency spell's `declaredTools` describe the tools it needs when invoked directly; when pulled in as a dependency it does **not** widen the allowlist — the **primary** spell retains control over which tools the Wizard may wield.

**Attunement × Forbidden Arts invariant:** Artifact Attunement only **intersects** the host MCP toolset with `declaredTools` — it never widens it or introduces tools the host does not already expose. **`ToolPolicy.NoForbiddenArts`** (request-driven) may strip Forbidden Arts from the *advertised* set, but a spell that lists a Forbidden Art in `declaredTools` still receives that tool in the advertisement when the request does not use `NoForbiddenArts`. The **Ward** gate runs at **execution** time (after advertisement) and is orthogonal: a tool may be advertised yet blocked until an operator resolves the ward (or unattended mode auto-denies). `execute_command` always requires ward resolution when enabled, regardless of attunement.

All hub built-in tool ids use snake_case, consistent with in-process MCP tools.

The canonical tool list is in §4.2. `run_spell_script` runs with `UseShellExecute = false`, cwd fixed to the resolved spell's `scripts/` directory, bare filename only (prefix containment across primary + resonant roots), extension-based runner map, and the same timeout, cooperative-cancel, and kill-tree behavior as `execute_command` (including `CancellationToken.Register` for immediate process kill).

When `WorkingDirectory` is empty, filesystem tools return a workspace-not-configured error; `ask_human`, Lore, and `search_archives` still work.

**Graceful partial tool failure.** Expected tool errors (validation, ward denial, Sanctum strict block, an unregistered tool name) already return a structured tool-result string and never throw. An *unexpected* exception (an infrastructure fault — a bug in a tool implementation, a transport failure inside an MCP server, an unhandled edge case) is a different matter: on the **streaming** path (`ProcessSingleToolCallAsync(suppressInvocationFailures: true)`, always on) it is caught, logged at `Error` with the full exception, and synthesized into the tool result text `ToolExecutionPipeline.PublicToolFailureMessage(toolName)` — `"[Tool error: {toolName} failed with an internal error. The operator has been notified.]"` — so the model sees the failure and can decide how to proceed (retry, apologize, try something else) rather than the turn dying mid-stream. A distinct **`toolError`** NDJSON event (`IntelligenceEventType.ToolError`) is also emitted immediately before the corresponding `toolResult` frame so streaming clients can observe and surface the failure distinctly — native-NDJSON only, not surfaced on the OpenAI `/v1` bridge (falls through its default case exactly like `toolResult`, §8.8.1). On the **buffered** path (`/api/intelligence/ping`, Forge execute routes), the same tolerant behavior is now the default too, gated by **`Arcanum:Intelligence:TolerateToolFailures`** (default `true`); setting it `false` restores the original strict behavior where an unexpected tool exception fails the entire turn with `Hub.Error`.

### 10.2.2 Semantic spell routing (pre-flight → main loop)

**Problem:** Operators want versioned markdown "spells" (workflows, checklists, personas) without pasting them into `CODEX.md`. Only one spell should apply per prompt.

**Solution — two passes:**

1. **Discovery (`SpellScanner`):** Scans `~/.config/arcanum/spells/` then the workspace for `SPELL.md` files. **Routing** uses **`ScanMetadataAsync`** (YAML frontmatter only — `name`, `description`) without reading spell bodies or `scripts/`; after **`SemanticRouter`** (or **`OverrideSpellName`**) picks a match, **`LoadFullAsync`** hydrates that spell’s full markdown, scripts list, and optional sidecar metadata. **Canonical sidecar filename is `SPELL.json`**; if absent, the scanner falls back to legacy **`SKILL.json`**; when both exist, **`SPELL.json` wins**. Creates, updates, version activation, clone, and import **write `SPELL.json` only** (they never create a new `SKILL.json`). **`ScanAsync`** (full parse) remains for spell CRUD and search APIs. Workspace spells override global spells on name collision (case-insensitive). Traversal is bounded — a canonical-path (symlink-resolved) visited set makes directory-symlink cycles terminate, plus step-budget and depth caps — and every `SPELL.md` / sidecar read is revalidated with handle-based identity (`WorkspacePathPolicy.RevalidatePathBeforeIo`), so a file whose symlink target escapes the workspace is rejected. Scan-time sidecar validation honors the configured `Spells:MaxDependencies`/`MaxDeclaredTools`, and spell writes (`SPELL.md`, `SPELL.json`) are atomic (temp + flush + rename via `SpellAtomicFile`).

2. **Pre-flight routing — `SemanticSpellRouter` (RAG Phase 5, §21.9):** `WizardIntelligenceProvider.ResolveRoutedSpellAsync` calls `SemanticSpellRouter.ResolveAsync` (scoped, Api) instead of `SemanticRouter.DetermineActiveSpellAsync` directly. `SemanticSpellRouter` decides, per turn, which of three modes applies:
   - **Disabled** (`Arcanum:Embeddings:SemanticSpellRoutingEnabled = false`, the default): returns `SpellRoutingDecisionMode.FullGrimoire` — the hub builds the router `IChatClient` (including the optional `Arcanum:FastModel` lease) and calls the static `SemanticRouter.DetermineActiveSpellAsync` with the full catalog, unchanged from pre-Phase-5 behavior.
   - **Pure embedding mode** (enabled, `SpellRoutingHybridMode = false`): embeds the user prompt and every spell's description (`SpellWeaveCache`, §21.9), computes cosine similarity, and returns `DirectResonance` carrying the highest-similarity spell above `SimilarityThreshold` (or `null`) — **no LLM call**.
   - **Hybrid mode** (enabled, `SpellRoutingHybridMode = true`): same embedding similarity, but returns `FilteredDivination` carrying the top `SpellRoutingHybridTopK` candidates; the hub still builds the router client and calls `SemanticRouter.DetermineActiveSpellAsync(..., candidates: decision.Candidates)` — a reduced tools list, same JSON response protocol and timeout/fallback behavior as pure LLM routing.

   `SemanticRouter.DetermineActiveSpellAsync` itself is unchanged aside from gaining an optional `IReadOnlyList<SpellMetadata>? candidates = null` parameter: single `IChatClient.GetResponseAsync` with low max output tokens, zero temperature, no tools, bounded timeout, and `ChatOptions.ResponseFormat = ChatResponseFormat.Json`. The tools list offered to the LLM is `candidates ?? availableSpells` — `null` (every pre-Phase-5 call site) means the full catalog, unchanged. The model must return a single JSON object with exactly one camelCase key `spellName` whose value is either the exact matching spell name or `NONE`; name resolution always searches the full `availableSpells` list regardless of what was offered. The hub deserializes with `JsonSerializer.Deserialize(..., ArcanumJsonContext.Default.SemanticSpellResponse)` after stripping optional markdown code fences; on `JsonException` or non-matching name, `activeSpell` is `null`. Failures and timeouts resolve to no spell — main inference is unchanged. Any Phase 5 embedding-side failure (Weave unavailable, batch/prompt embed failure, unexpected exception) falls back to `FullGrimoire` at Debug log level — never a functional regression.

3. **Main inference:** `SystemPromptBuilder` appends `### Active Operational Spell` with the spell's full markdown, plus per-spell `#### Available Spell Scripts` when scripts exist.

**Arcane Resonance (spell dependencies):** After **`LoadFullAsync`** hydrates the primary spell, **`SpellDependencyResolver`** walks `SPELL.json` `dependencies` recursively (hard depth limit **3**, cycle- and duplicate-safe; missing names are logged and skipped). Resolved dependency markdown bodies are concatenated under `### Resonant Spells (Dependencies)` in the system prompt. Dependency edges are retained on the internal `ResolvedSpell` carrier for validation and debugging. The resolver performs its own **`ScanMetadataAsync`** pass (intentional double-scan — see `SpellDependencyResolver` source comment) so it remains self-contained when **`OverrideSpellPath`** bypasses routing's catalog scan.

**`CodexReader`:** Global and workspace **`CODEX.md`** reads are cached in a process-lifetime concurrent dictionary keyed by path; entries invalidate when **`LastWriteTimeUtc`** changes.

**`WizardIntelligenceProvider` turn context (M5):** Each inference turn resolves campaign / Sanctum / ward settings once (`TurnContext`), precomputes the unattended filtered tool list, and passes a single serialized tool-arguments snapshot through ward and Sanctum enforcement to avoid duplicate JSON work per tool call.

**`SkipSpellRouting`:** When **`PingRequest.SkipSpellRouting`** is **`true`**, **`WizardIntelligenceProvider`** skips both **`SpellScanner.ScanMetadataAsync`** / **`LoadFullAsync`** and **`SemanticSpellRouter.ResolveAsync`** / **`SemanticRouter.DetermineActiveSpellAsync`**, sets **`activeSpell`** to **`null`**, and does not evaluate **`OverrideSpellName`**. This avoids spell disk IO, embedding cost, and router LLM cost for internal background tasks (Campaign Logger, Saga extraction). **`CodexReader.ReadCodexAsync`** still runs; with an empty **`WorkingDirectory`** (Campaign Logger), codex content is null.

### 10.2.3 Pre-flight token counting and read-time context compression

After the dynamic system prompt is prepended to the in-memory message list (and before the main `GetResponseAsync` / `GetStreamingResponseAsync` call), **`WizardIntelligenceProvider`** may apply **read-time** compression when **`Arcanum:Intelligence:EnableContextCompression`** is **`true`**:

- **Fast path:** if the assembled message count is **at or below** `Arcanum:Intelligence:CompressionPreflightMinMessages` (default 6), tokenization is skipped (short threads are assumed under budget).
- **Tokenizer:** singleton **`InferenceTokenizerResolver`** returns a cached tokenizer keyed on the encoding name from `Arcanum:Intelligence:TokenizerEncoding` (default `o200k_base`). Unknown encodings log a warning and fall back to `o200k_base` so the hub never throws on misconfig. The cache uses `OrdinalIgnoreCase` keys.
- **Counting:** **`ManaPreflight`** (injected singleton) memoizes per-message `Tokenizer.CountTokens` results in a bounded LRU keyed by `(encodingName, content hash)`; sums flattened message text plus **`Arcanum:Intelligence:PerMessageTemplateOverheadTokens`** (default 4). **`IManaMeter`** / **`ManaMeter`** handles prompt render token counts. Tool definitions in `ChatOptions` are **not** included in the pre-flight pass; **`ContextWindowCompressionThreshold`** headroom absorbs that gap.
- **Threshold:** compared to `ContextWindowLimit(provider) * ContextWindowCompressionThreshold / 100` (both clamped).
- **Swap:** when over threshold, **`Session.Summary`** and **`Session.LastSummarizedMessageAt`** must both be present; otherwise a **warning** is logged and history is left unfiltered. When present, Grimoire entries with `CreatedAt <= LastSummarizedMessageAt` are omitted from the inference transcript and the summary is injected via **`SystemPromptBuilder.Build(..., campaignSummary: ...)`** as `### Campaign Summary (compressed context)` (see §10.5). **No `Entry` rows are deleted.**
- **NDJSON:** when compression applies on **`ping-stream`**, a **`status`** event is emitted with message **`IntelligenceStatusMessages.MemoryCompressionNotice`** (shared const in **`RetroDownfall.Arcanum.Core.Intelligence`**) immediately before streaming inference begins (after `sessionBound` / `conversationBound` when bound). Buffered **`ping`** logs the same string at **Information** when compression runs.
- **Native AOT:** tokenizer creation uses the **`Microsoft.ML.Tokenizers.Data.O200kBase`** data assembly so vocabulary is linker-friendly; **`dotnet publish`** on **`Cli`** should remain warning-clean aside from known Spectre / transitive advisory noise.

### 10.2.3.1 Performance findings

Closed audit items (writer reuse, scan/cache bounds, Loremaster counter, MCP line reader, trust digest LRU, `/api/meta` handles, Apprentice jitter) are implemented; acceptable-as-is notes live in code comments.


### 10.2.4 Scrying — the vision/multimodality capability gate

**Model capability declaration:** each `Arcanum:Providers[].models` entry is a **`ModelEntry`** (`Name`, `SupportsVision`, default `false`); the JSON binder (`ModelEntryJsonConverter`) accepts either a bare string (back-compat, `SupportsVision = false`) or an object `{ "name", "supportsVision" }`. `ProviderResolver.SupportsVision(ArcanumSettings, string?)` / `SupportsVision(ProviderSettings, string?)` resolve capability by exact (case-insensitive) model-name match against configured `models` entries.

**Gate placement — before any inference token:** `ScryingValidator` (`Core.Intelligence`) is the single validation surface shared by every inference entry point:

- `RequestContainsImages(PingRequest)` — scans `StatelessMessages[].ContentParts` (kind `image_url`) and `ScryingFoci`.
- `ValidateRequestImages(PingRequest, ScryingSettings)` — when images are present: `Scrying.Enabled` (else `Scrying.FeatureDisabled`, 403), per-request image count vs `MaxImagesPerRequest` (else `Scrying.TooManyImages`, 400), and — **for `data:`-URI images only** (native `ScryingFoci` and any `data:`-URI `image_url` part) — MIME allow-list (`Scrying.UnsupportedMimeType`, 400) and decoded byte size vs `MaxImageBytes` (`Scrying.ImageTooLarge`, 413). `http(s)` URL images are counted toward the cap but not size/MIME-checked — the downstream provider fetches and rejects them, avoiding a HEAD-request side-channel and added latency.

**`WizardIntelligenceProvider`** (`ExecutePromptAsync` and `StreamPromptAsync`) runs `ValidateScryingGate` immediately after `PingRequestBoundsValidator.Validate` and before model-lease resolution: it short-circuits when the request carries no images, otherwise runs `ScryingValidator.ValidateRequestImages` and then resolves the intended model via `ProviderResolver.TryResolveProviderForModel` (the same no-resilience resolution used elsewhere) purely to check `SupportsVision` — failing `Scrying.VisionNotSupported` (400) when unsupported. This is a client-input mismatch, not a provider-connectivity concern, so it is **never retried across resilience fallback candidates**; a model-resolution failure here is not itself an error (the existing `Hub.Model` path reports it later). This single gate covers `POST /api/intelligence/ping(-stream)`, spell/prompt execute routes, Unseen Servant daemon jobs, and Apprentice step execution — all route through `WizardIntelligenceProvider`.

**`OpenAiV1Endpoints`** (`/v1/chat/completions`) runs the equivalent gate independently, before the shared provider is called: after resolving `ProviderSettings`/canonical model, it checks `ScryingValidator.RequestContainsImages(ping)` on the mapped `PingRequest`, then `ScryingValidator.ValidateRequestImages`, then `ProviderResolver.SupportsVision(resolvedProvider, resolvedModel)` — returning an OpenAI-shaped `400 invalid_request_error` (`code: "vision_not_supported"`) or `403` (`code: "feature_disabled"`) as appropriate, before any inference call. This means the `WizardIntelligenceProvider`-level gate is a defense-in-depth backstop for `/v1`, not the primary enforcement point for that surface.

**Multimodal content mapping (`InferenceContextBuilder`):** `image_url` parts map to `Microsoft.Extensions.AI` content based on URI scheme — `data:` URIs decode to `DataContent` (raw bytes + parsed MIME) so the provider receives the actual payload; `http(s)` URIs map to `UriContent` unchanged (provider fetches). Native `PingRequest.ScryingFoci` / `AttachedFiles` are appended as `DataContent` / text onto the current turn's final message in `BuildInitialMeAiChatMessages`. When **`Arcanum:Attachments:Enabled`** and the host attachment store path is active (Command Center + serve host), those foci/files are **persisted before the model call** as session attachments — bytes under `~/.config/arcanum/attachments/` plus `SessionAttachments` Grimoire metadata (§10.2.5). **`arcanum chat`** (and frameless `ask` staging) remain **ephemeral in this pass** — threaded onto the in-memory chat message list only; `Entry` rows still store text content only.

**Configuration and errors:** see §3.4 (`Arcanum:Scrying:*`, `Arcanum:Attachments:*`) and §8.23 (`Scrying.*` codes).

### 10.2.5 Session attachments (disk + Grimoire pointers)

**Purpose:** Persist text attachments and Scrying images across Command Center turns so conversations can list, Reveal, re-attach, and let the model re-attach — without storing blobs inside SQLCipher.

**Ownership:** host-only `ISessionAttachmentStore` (serve process). CLI stages content via `PingRequest.AttachedFiles` / `ScryingFoci` / `AttachmentReferences`; the host re-validates and persists **before** inference (failure aborts the turn — the model never sees an attachment that did not persist). Listing: `GET /api/sessions/{id}/attachments` returns **bound** rows only.

**On-disk layout** (`ArcanumPaths.AttachmentsDirectory` → `{GrimoireDirectory}/attachments/`): `_pending/{turnId}/{logicalKey}/v1/{originalFileName}` until session-bound; then `{sessionId:N}/{logicalKey}/vN/{originalFileName}`. Owner-only permissions on the tree. Dedupe against the latest version hash (identical bytes → reuse id, no new `vN`).

**System prompt index:** metadata-only `### Session Attachments Index` (bounded by `MaxIndexItemsInPrompt` / `MaxIndexBytesInPrompt`); no bytes. Model pulls content via MCP `attach_session_file` (or the operator via `/attachments add`).

**Turn budget / injection:** `MaxReferencesPerTurn` is a **combined** cap for user `AttachmentReferences` + model `attach_session_file` injections on the same turn. Each logical key+version is injected **once** per turn (subsequent tool rounds do not re-inject). Image re-attach requires `Arcanum:Scrying:Enabled` and a model with `SupportsVision`; oversize images are **rejected, never truncated**.

**Model tool:** `attach_session_file` is an **internal MCP** tool (attunement-aware). After a **successful** call (`!Failed && !Denied` — Ward/Sanctum denials and tool failures do not inject), a dedicated post-tool path materializes `TextContent` / `DataContent`, then atomically consumes the turn budget / inject-once mark, and queues content for the **next** inference round. User extras from a multi-tool model response are appended **only after** every tool call and tool result from that round are on the transcript (never interleaved between tool exchanges). Injected/rehydrated text is framed as untrusted DATA (adaptive fences); attachment headings harden hostile path characters. Unexpected post-processing failures follow `TolerateToolFailures` / streaming tolerant behavior and never partially inject.

**Privacy:**

| Layer | Protection |
|-------|------------|
| Grimoire metadata (`SessionAttachments`) | SQLCipher-encrypted (same as other Grimoire tables) |
| Attachment **bytes** on disk | Owner-permission-protected under `~/.config/arcanum/attachments` — **not** SQLCipher-encrypted |
| OS disk encryption / backup | Operator responsibility |
| Full conversation continuity | Copy/restore `~/.config/arcanum/attachments` together with the DB |

See [PERSISTENCE.md](Arcanum.PERSISTENCE.md) for schema invariants, promote/fork/purge, reconcile, and uninstall/copy notes. Config: §3.4 (`Arcanum:Attachments:*`).

### 10.3 Registration lifetimes

`IArcanumIntelligenceProvider` / `WizardIntelligenceProvider` are **scoped** (one instance per request scope). `IChatClientFactory` is **singleton**; each call to **`ResolveClientAsync`** returns a **`ChatClientLease`** that owns a fresh `IChatClient` for that inference turn over the named OpenAI-compatible `HttpClient` pipeline.

### 10.4 Grimoire integration

The provider persists through `IGrimoireRepository`. When `sessionId` is set, prior turns are loaded for `IChatClient`. A dynamic `ChatRole.System` message from `SystemPromptBuilder` is prepended in memory (not persisted to Grimoire). Tool rounds are persisted as bracket-formatted `Entry` rows. Assistant entries contain **answer text only**: raw, visible, protected, and summarized reasoning are never written to Grimoire. After a successful inference turn (buffered or streamed), when **`Arcanum:Intelligence:EnableTokenTracking`** is **`true`** and a session is bound, **`IncrementSessionTokensAsync`** atomically adds the turn’s provider-authoritative reported **`total_tokens`** to **`Session.TotalTokensUsed`**. Persistence failures on the buffered path are logged as warnings only.

### 10.5 Spatial context on inference

**Problem:** the API host's cwd is not the operator's shell cwd.

**Solution:** `PingRequest` carries `WorkingDirectory`, `ContextSnapshot` (`PatternSnapshot`), optional `SessionId`, optional `StatelessMessages` (`CoreChatMessage[]` transcript for stateless callers), optional `AttachedFiles`, optional `ChronosyncDelta` (`ChronosyncReport`), and optional `DataStreams` (reserved for future real-time JSON injection). The CLI resolves `Environment.CurrentDirectory`, runs Eye of the World, runs `IChronosyncEngine` inside a DI scope against the local Grimoire, and populates these fields before each HTTP call. CLI bootstrap (`ask`, `chat`) reuses `IGrimoireCliInitialization` once per process so SQLCipher setup and first-run migrations match the host (`GrimoireDatabaseBootstrapper`, shared with `GrimoireDatabaseHostedService`).

**`SystemPromptBuilder.Build` ordering (DCI blocks):**

| Position | Block | Produced by |
|---|---|---|
| 0 | **Preamble** (base persona + the "INSTRUCTIONS override conflicting DATA" rule) | static content |
| 1 | **DATA** (`[None]` when empty): `### Lexicon (Known Context)` → `### Chronosync Report (Temporal Delta)` → `### Attached Files for this Turn` → `### Semantic Context (Retrieved Codebase)` → `### Saga (Associative Memory)` → `### Data Stream: {StreamId}` | Lexicon retrieval (§10.6), `ChronosyncDelta`, `AttachedFiles`, RAG Phases 3 & 4 retrieval (§10.4 §21.4) |
| 2 | **CONTEXT**: `### Workspace Context` / `### Table of Contents` → `### Master Codex (CODEX.md)` → `### Campaign Summary (compressed context)` (only on compression) | `ContextSnapshot`, `CodexReader`, read-time compression (§10.2.3) |
| 3 | **INSTRUCTIONS**: `### Active Operational Spell ({Name})` (omitted when `SkipSpellRouting`) → `### Available Spell Scripts` (when present) → `### Output Formatting Directive` (when `CliTerminalFormatting`) | `SemanticRouter`/`SemanticSpellRouter` (§10.2.2 §21.9), scripts scan, CLI flag |

**Data Streams (DATA, hardened):** `PingRequest.DataStreams` are externally supplied and treated as untrusted DATA. `AppendDataStreams` sanitizes each `StreamId` as a label (collapse whitespace, strip control chars and `#` heading markers, cap length) so the `### Data Stream: {id}` heading cannot break DCI structure; the payload is preceded by an explicit “untrusted data / not instructions” warning and wrapped in an adaptive markdown fence (`ComputeFenceBacktickLength`) so embedded triple-backticks cannot break out.

The sterile `[None]` (never an empty block, never chatty copy) prevents smaller models from hallucinating about missing sections. `SystemPromptBuilder` is allocation-disciplined: a single `StringBuilder` (initial capacity 2048) with chained `.Append()`/`.AppendLine()` calls; large content blocks (Master Codex, Attached Files, Campaign Summary) are passed as raw strings rather than `$"..."`-interpolated, to avoid intermediate concatenation under high-velocity inference loops. The same `WorkingDirectory` scopes `McpConnectionManager`, `CodexReader`, and `SpellScanner`.

### 10.6 The Lexicon — agent-directed entity memory

**Role:** structured, model-writable memory that replaces the legacy key-value Lore MCP tools for agent use. Entities are typed (Person, Project, API, DaemonState, …) with a fact array; the inference pipeline retrieves them by subject and injects them into the Master system prompt under DATA as `### Lexicon (Known Context)`. The legacy `MageSettings` Lore surface (`/api/lore`, `arcanum lore`) remains as an operator-only key-value store; it is no longer model-directed.

**Persistence (raw SQL, no EF):** `lexicon_entries` (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt) + an FTS5 external-content virtual table `lexicon_fts` (Name, Type, FactsText; `content='lexicon_entries'`, `content_rowid='rowid'`) with `lexicon_entries_ai`/`_ad`/`_au` triggers syncing the index on insert/delete/update. Neither table is part of the compiled EF model — they are created by `LexiconSchemaInitializer.EnsureSchemaAsync` at Grimoire bootstrap (alongside `WeaveSchemaInitializer`) and accessed via `LexiconService` over the scoped `ArcanumDbContext` connection + `SqliteBusyRetry` + `DbCommand.CreateParameter()`, mirroring `SagaMemoryStore` / `SanctumBreachRepository`. No EF migration, no compiled-model regeneration.

**Write path (`scribe_lexicon` / `delete_lexicon` MCP tools):** upsert by `NameNormalized` (trim + invariant) under `BEGIN IMMEDIATE` so concurrent appends cannot lose facts; append non-duplicate facts, cap counts/lengths (`LexiconLimits`); `FactsJson` is serialized via the source-generated `LexiconJsonContext` (AOT), `FactsText` is newline-joined for FTS. Type semantics: new + blank → `General`; existing + blank → keep; non-empty → refresh. `delete_lexicon` is a Forbidden Art; `scribe_lexicon` is un-gated. Both gated by `EnableLexiconSystem`.

**Read path (preflight + retrieval):** `SemanticRouter` now returns `SemanticSpellRoutingResult(Spell, Entities)` — the JSON contract is `{ "spellName": "...", "entities": [...] }`. When the router ran but supplied no entities (or routing was bypassed: `OverrideSpellName`, pure embedding `DirectResonance`, no-spell user-facing turns), `WizardIntelligenceProvider` runs `LexiconEntityExtractor` — a low-token JSON preflight on the fast model (`{ "entities": [...] }`) — so memory retrieval stays available even when spell selection avoided an LLM call. The `ShouldUseLexiconForTurn` gate skips only true internal headless tasks (`SkipSpellRouting && DisableMcpTools && UnattendedMode` — Campaign Logger, Saga extraction). `MatchEntitiesAsync` is tiered: exact `NameNormalized IN (...)` hits first, then column-weighted FTS5 `MATCH 'Term' ORDER BY bm25(lexicon_fts, 3.0, 2.0, 1.0) ASC` (3.0 Name, 2.0 Type, 1.0 FactsText — no Lucene caret boosting inside MATCH), deduplicated by Id, exact hits before FTS hits. FTS failure degrades to a bounded `LIKE` fallback or empty matches.

**Injection (DATA, hardened):** `SystemPromptBuilder.Build` accepts `IReadOnlyList<LexiconEntryDto>? lexiconEntries` (default null) and renders `### Lexicon (Known Context)` at the top of the DATA block. Lexicon is model-writable and potentially stale/adversarial, so it is treated strictly as DATA — never instructions; the preamble already states DATA may be stale and never overrides INSTRUCTIONS. Facts are hardened: whitespace collapsed, newlines/control chars stripped, exactly one plain markdown bullet per entity (`- **Name** (Type): "Fact 1"; "Fact 2"`), so facts cannot create headings or break DCI structure. Total rendered bytes are capped by `LexiconMaxInjectedBytes`; entry count by `LexiconMaxMatchedEntries`. Retrieval/injection failures are logged and swallowed — Lexicon never fails an inference turn. Lexicon contents are not persisted into audit logs or exposed on `/v1` tool surfaces.

**Error codes:** `ErrorCodes.Lexicon.InvalidName` / `InvalidFact` / `NotFound` / `WriteFailed` / `SearchFailed` (no HTTP route yet; MCP converts expected failures to tool-result strings).

---

## 11. Local API security

### 11.1 Threat model

Arcanum runs on **loopback only** for **single-user local development**. Even on localhost, every `/api` and `/v1` request must present a valid API key (zero-trust local). The API key remains privileged for file/network/MCP tool surfaces. Default **Local** edition does **not** advertise or invoke host-process tools (`execute_command` / `run_spell_script`) unless Development + `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1` (ADR 0001 / `HostProcessToolPolicy`).

### 11.2 API key lifecycle

1. `ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync` runs **before** `Build()`.
2. If no key exists, a cryptographically random 32-byte key is generated, Base64-encoded, and saved via `ISecretStore`.
3. **Primary store:** the OS credential store under the fixed shared identity `service=arcanum` / `account=master-api-key` (macOS Keychain, Windows Credential Manager, Linux Secret Service via libsecret). Implemented in `RetroDownfall.Arcanum.Secrets` (`IOsCredentialStore` / `OsCredentialStore`) and consumed by `OsKeychainSecretStore`.
4. **Mirror / fallback:** the same key is also written to Data Protection–encrypted `security.dat` (purpose `Arcanum.Core.ApiKey`, application name `ArcanumCore`) so headless Linux / locked-keychain environments can still boot. On read, keychain is preferred; if keychain is empty and `security.dat` decrypts, Arcanum **migrates** the key into the OS store once.
5. The Forge and other local clients read the **same** OS identity — they do not decrypt `security.dat`.

### 11.3 Request authentication

`ApiKeyEndpointFilter` (singleton) accepts the API key from either header, in this order:

1. **`X-Arcanum-Key`** when present (legacy Arcanum header). **Multiple values reject with 401** — the filter explicitly disallows ambiguous duplicate headers.
2. Otherwise `Authorization: Bearer` followed by the raw key (OpenAI-compatible clients). The `Bearer` prefix is case-insensitive; only the trimmed token after the first space is compared. **Multiple `Authorization` values reject with 401**.

The filter then:

1. Rejects values exceeding `MaxApiKeyHeaderUtf16Chars` with 401.
2. Caches a **SHA-256 digest** of the expected key (32 bytes, fixed size) for **`Arcanum:Security:ApiKeyCacheTtlSeconds`** (default 30 s) so on-disk key rotation propagates without restart. The plaintext expected key never lives in long-term memory beyond computing the digest, and the temporary UTF-8 buffer is zeroed.
3. Hashes the inbound header through `SHA256.TryHashData` into a stack buffer and compares both 32-byte digests with `CryptographicOperations.FixedTimeEquals` — constant-time **and** length-independent (no early-return on size mismatch).
4. Uses `stackalloc` for the header UTF-8 buffer when `<= 256` bytes; the 32-byte digest buffer is always on the stack.

Failed authentication returns **`ApiResponse<string>`** at **401** with error code **`Auth.Unauthorized`** (matches the `{Noun}.{Verb}` convention used elsewhere).

### 11.4 CORS (serve host)

`AddArcanumApiServices` registers a CORS policy named **`ArcanumCors`** whose **allowed origins are read from `Arcanum:Host:CorsAllowedOrigins`** at startup. Defaults to localhost loopback (`http://localhost:5001`, `http://127.0.0.1:5001`, `http://localhost:3000`, `http://127.0.0.1:3000`). Operators who need to allow any browser origin (for example LibreChat installations on arbitrary hosts) can set the property to `["*"]` — Arcanum then calls `AllowAnyOrigin` and adds the same `AllowAnyHeader` / `AllowAnyMethod` it always has. **When the effective host bind is all-interfaces** (`ListenAny` or `ARCANUM_HOST_ANY`), a configured `["*"]` origin is **downgraded** to the localhost defaults so wide-open CORS is not combined with a non-loopback listener. `UseArcanumCors` runs early in the pipeline so browser-based tools can preflight without endpoint contention. `AllowAnyHeader` / `AllowAnyMethod` are retained unconditionally because callers always present custom headers (`X-Arcanum-Key`) and use varied verbs.

### 11.5 OpenAPI and Scalar

`MapOpenApi` runs unconditionally under the keyed `/api` group, so `openapi/v1.json` always requires the API key. **`MapScalarApiReference`** is **gated by `Arcanum:Host:EnableScalarUi`** (default **`false`**). When enabled, the Scalar route lives in a sub-group with a CSP filter that emits `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'` and `X-Content-Type-Options: nosniff` on every response (same-origin scripts/styles only — matching `ApiBootstrapper`). First-party browser UI must keep JS/CSS in external files; Scalar is an opt-in third-party surface under this CSP. The OpenAI-shaped **`POST /v1/chat/completions`** and **`GET /v1/models`** routes live under `MapGroup("/v1")` with the same API-key filter and are not advertised in the OpenAPI document.

### 11.6 Symlink containment for tool paths

`WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck` performs the lexical prefix check (case-insensitive on Windows) **and** resolves the candidate's final symlink target via `File.ResolveLinkTarget(returnFinalTarget: true)` / `Directory.ResolveLinkTarget`. If the resolved target leaves the workspace, the request is rejected. `ArcanumInternalToolServer.TryResolveSandboxedPath` and `ArcanumSpellScriptTool` both call this guard so an attacker-planted symlink inside the workspace cannot pivot outside.

### 11.7 In-process `execute_command` argument handling

The tool accepts arguments in **either** of two forms:

- **`argumentList: ["status", "--porcelain"]`** — preferred. Each entry is appended verbatim to `ProcessStartInfo.ArgumentList`. No shell, no OS-level re-parsing.
- **`arguments: "status --porcelain"`** — legacy single-string form. The host tokenizes via the same algorithm `ArcanumSpellScriptTool` uses (quoted substrings stay together; whitespace separates tokens) and then appends each token to `ArgumentList`.

`Arguments` is **never** assigned to `ProcessStartInfo.Arguments` directly, so model output cannot smuggle additional argv via shell metacharacters.

**Child environment:** before spawn, `execute_command` and `run_spell_script` strip `ARCANUM_*` secret/config vars and loader/runtime hijack variables from the inherited environment while preserving `PATH`/`HOME`. MCP stdio servers use the same absolute-deny rules plus optional per-server `inheritEnv` (§5.6 / MCP host).

`execute_command` and `run_spell_script` both read stdout/stderr through a `ReadStreamCappedAsync` helper that enforces **`Arcanum:Intelligence:ToolOutputCapBytes`** split evenly per stream. Beyond the cap, the stream is silently closed and a `[truncated: exceeded N bytes]` marker is appended. UTF-8 boundary safety is preserved by `ChooseSafeCharCount`. This prevents a verbose tool from exhausting host memory.

**External MCP:** `McpBridgeTool` / `McpToolResultFormatter` apply the same **`ToolOutputCapBytes`** limit to bridged `tools/call` text results. `McpClient` bounds `tools/list` tool descriptions (8 KiB UTF-8) and input schemas (64 KiB UTF-8; oversized schemas fall back to an empty object schema).

### 11.9 Sanitized public error envelopes

Inference-pipeline errors must not leak internal exception text to clients:

- **`WizardIntelligenceProvider.ExecutePromptAsync`** / **`StreamPromptAsync`** — model-resolution failures return the public string `"The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel."`; full exception is logged via `ILogger.LogWarning`. Provider unreachable failures include provider name, endpoint, and remediation hint (no secrets). Inference wall-clock expiry returns **`Hub.Timeout`** (buffered) or an **`Error`** stream frame with the timeout message.
- **`ArcanumExceptionHandler`** (`IExceptionHandler`) — unhandled pipeline exceptions return **`Hub.Unhandled`** in the `ApiResponse<string>` envelope with the same **`TraceId`** logged server-side. **`JsonException`** from request binding or deserialization returns **400** with **`Validation.InvalidBody`** in the `ApiResponse<bool>` envelope.
- **`POST /v1/chat/completions`** — buffered failures return the public string `"Inference failed. See server logs for details."`; never the raw `Result.Error.Message`.
- **`WebhookCommLinkDispatcher`** — outbound webhook exceptions return the public code `CommLink.WebhookException` with the generic message `"Comm Link webhook POST failed. See server logs for details."`; the actual exception is logged.
- **`PUT /api/config`** — validation failures return **`ApiResponse<bool>`** at **400** with code `Configuration.ValidationFailed` (user-facing validation messages). Write failures return **`ApiResponse<bool>`** at **500** with code `Configuration.WriteFailed` (exception detail is logged server-side; the envelope message is safe to display in Studio).

See §8.23 for the full `ErrorCodes` → HTTP status catalog used by `ArcanumErrorMapper` across native `/api` routes.

### 11.10 Comm Link webhook scheme allowlist and redirect handling

`WebhookCommLinkDispatcher` validates the scheme of `Arcanum:CommLink:WebhookUrl` against `Arcanum:CommLink:AllowedSchemes` (default `["https"]`; add `"http"` explicitly to opt in to plaintext webhooks). URLs with disallowed schemes are skipped with a warning so a misconfigured `file://` or `ftp://` URL never causes a network/filesystem call. Before dispatch, **`OutboundUrlGuard`** rejects loopback, RFC1918, and link-local targets (including after DNS resolution). The named `HttpClient("CommLinkWebhook")` is configured with `HttpClientHandler.AllowAutoRedirect = false`, eliminating SSRF amplification where a webhook endpoint could 302 to an internal target (`http://169.254.169.254`, RFC1918, etc.). The client `Timeout` reads from `Arcanum:CommLink:WebhookTimeoutSeconds` (default 15s, clamp 1–120).

### 11.11 Outbound URL guard (SSRF hardening)

**`OutboundUrlGuard`** (`Infrastructure/Security`) is the shared policy for untrusted outbound `http`/`https` URLs. It requires an absolute URI, resolves the host, and rejects any address in loopback (`127.0.0.0/8`, `::1`), RFC1918 (`10/8`, `172.16/12`, `192.168/16`), carrier-grade NAT (`100.64.0.0/10`), IPv6 unspecified (`::`), link-local (`169.254/16`, `fe80::/10`), IPv6 unique-local (`fc00::/7`), or the hostname `localhost` / `*.localhost`.

**Applied at:**

- **`WebhookCommLinkDispatcher`** before `POST` (configured `Arcanum:CommLink:WebhookUrl`).
- **`PUT /api/config`** and **`POST /api/config/validate`** via **`OutboundUrlGuard.ValidateArcanumSettingsAsync`**: `CommLink.WebhookUrl` uses the strict guard; `OpenAICompatible` provider `endpoint` values use a relaxed check that still blocks link-local/metadata addresses but permits loopback and RFC1918 for local inference backends (for example Ollama's `/v1` endpoint at `http://localhost:11434/v1`).

**DNS-rebind pinning:** `OutboundUrlGuard.ResolveValidatedAddressesAsync` returns the validated address set for a hostname. Untrusted egress clients (`CommLinkWebhook`, `McpHttp`) wire `ConnectCallback` to resolve fresh at connect time, re-run `IsBlockedAddress` on the actually-dialed IP, and connect only to validated addresses. Provider inference and connectivity probes (`ChatClientFactory` endpoint cache, **`POST /api/providers/test`**) use **`OutboundUrlGuard.CreateProviderEgressHandler()`** — same connect-time pinning with **`allowPrivateAndLoopback: true`** so loopback/RFC1918 local backends remain reachable while link-local/metadata addresses stay blocked.

### 11.12 Kestrel limits and optional rate limiter

`ArcanumKestrelConfigurator` (shared by `ServeCommand` and `Api.DevHost`) reads `Arcanum:Host:MaxRequestBodyBytes` (default 10 MiB; clamped 256 KiB – 1 GiB) and applies it once as `KestrelServerOptions.Limits.MaxRequestBodySize` for all listeners (HTTP and HTTPS). When rate limiting is effective (§11.13), `AddArcanumApiServices` calls `AddRateLimiter` with a fixed-window policy named **`ArcanumRateLimit`**; both `/api` and `/v1` `MapGroup` routes apply `RequireRateLimiting("ArcanumRateLimit")`. Partition keys use the **remote IP address only** (per-credential bucketing was removed — one operator machine should not throttle itself across CLI verbs). `RejectionStatusCode = 429`. `Arcanum:Host:RateLimit:QueueLimit` enables queueing (`OldestFirst`, `AutoReplenishment = true`); the default `0` rejects excess requests immediately.

### 11.13 `Arcanum:Host:ListenAny` versus `ARCANUM_HOST_ANY`

The environment variable always wins. Recognized values: `1` or `true` (force all-interfaces bind), `0` or `false` (force loopback), or any other string that `bool.TryParse` accepts. When the env var is unset, empty, or unrecognized, `ArcanumEnvironment.IsHostAnyEnabled` falls back to the configuration property (`Arcanum:Host:ListenAny`). This preserves the historical container-friendly override while making the binding visible in `arcanum.json` for first-party operators. The effective value is exposed via **`GET /api/meta`** (`ListenAny` on `InstanceMetadataDto`).

**HTTPS-only any-IP:** When the effective bind is all-interfaces, Kestrel binds **only** `ListenAnyIP` on `Arcanum:Host:Https:Port` with TLS. `Host:Https:Enabled` and a loadable certificate are required; plaintext any-IP HTTP is refused. Local CLI clients resolve `https://localhost:{HttpsPort}`; Forge `the-forge.json` `BaseUrl` must match. Doctor probes the HTTPS health URL (and surfaces cert-trust / SAN guidance on failure).

**First-run acknowledgement:** When `ListenAny` is enabled from configuration (not via `ARCANUM_HOST_ANY`), interactive `arcanum serve` prompts once and writes `~/.config/arcanum/.listen-any-acknowledged`. Non-interactive hosts must set `ARCANUM_LISTEN_ANY_ACK=1`. Container operators using `ARCANUM_HOST_ANY` skip the prompt but still receive the security banner.

**Security banner:** On startup when all-interfaces bind is effective, `ArcanumSecurityStartupChecks` and `arcanum serve` emit a warning that HTTPS-only binding on all interfaces grants network-local clients operator-equivalent power if they obtain the API key, and remind operators to trust the TLS certificate (Compendium self-signed certs are loopback-SAN only).

**Automatic rate limiting:** When the effective bind is all-interfaces (`IsHostAnyEnabled` is `true`), `ArcanumEnvironment.IsRateLimitEnabled` returns `true` even if `Arcanum:Host:RateLimit:Enabled` is `false`. Explicit `RateLimit:Enabled: true` also enables the limiter on loopback. **Loopback-only binds** (`ListenLocalhost`) intentionally leave the limiter **off** by default so a single operator on `127.0.0.1` is not throttled during local development. This pairs network exposure with request admission control without requiring a separate operator toggle in container deployments.

### 11.13.1 Data at rest permissions

Sensitive paths are restricted to the current user at creation time via `SecureFilePermissions`:

- **Unix:** `File.SetUnixFileMode` — files `600` (`UserRead | UserWrite`), directories `700` (`UserRead | UserWrite | UserExecute`).
- **Windows:** `File.SetUnixFileMode` throws; owner-only ACL via `FileSystemAccessRule` (`Modify` for files, `FullControl` with inheritance for directories).

**Applied on create:** Grimoire `.db`, `arcanum.json`, `cli-session.txt`, Serilog rolling logs (`SecureSerilogFileHooks`), Data Protection secret files, and owner-only creation of `~/.config/arcanum` and `%ApplicationData%/arcanum/logs/`.

**Startup self-check:** `ArcanumSecurityStartupChecks` warns (does not fail) when any checked path is group/other-readable on Unix or grants read to `Everyone`/`Users` on Windows. Pre-existing files are not modified automatically — operators must fix permissions manually after the warning.

### 11.13.2 Deferred: native-library integrity verification

**Status: deferred** (requires build-pipeline manifest generation; not implemented).

At startup, Arcanum would verify shipped native dependencies (`e_sqlcipher`) against a bundled `native-libs.sha256` manifest (one `"<sha256hex>  <relative-path>"` line per file, sha256sum-compatible). On missing manifest or hash mismatch, startup would fail with `Internal.NativeLibraryIntegrity`. Manifests would be generated per RID at package/build time.

### 11.14 Wards (Forbidden Arts)

**Purpose:** Gate high-risk tool invocations (**Forbidden Arts**) until an operator explicitly allows or denies them. Separate from the `ask_human` MCP tool (information gathering).

**Engine:** Singleton **`IWard`** / **`WardGate`** (in-memory). Active wards are keyed by `wardId` (`Guid` string). **`WardAsync`** registers a `TaskCompletionSource`, honors caller cancellation (inference abort cleans up the ward), and auto-denies on timeout with reason `"The ward held until timeout — action was not allowed"`. **`Resolve`** atomically moves the active ward to a resolved tombstone before completing the waiter, so exactly one concurrent resolver succeeds and every competitor returns **`AlreadyResolved`** (HTTP **409**). Tombstones are retained for the clamped ward timeout plus 60 seconds and pruned against an injected `TimeProvider` (system time in production).

**Policy:** `Arcanum:Ward:Enabled` + tool ∈ `ForbiddenArts` + campaign `RequireWardForForbiddenArts` when a campaign matches `WorkingDirectory` (default `true` when no campaign; **`true`** on newly registered campaigns via `CampaignSettings.CreateDefault()`). `UnattendedMode` + `AutoDenyInUnattendedMode` skips the wait and denies immediately.

**Intentional exclusions from `ForbiddenArts`:**
- **`scribe_lexicon`** — append-only structured memory; non-destructive (appends non-duplicate facts). **`delete_lexicon`** remains gated because it is destructive.
- **`ask_human`** — separate HITL mechanism (information gathering, not execution).

**Future hardening (deferred):** Per-binary allowlist for **`execute_command`** (restrict which executables may be spawned beyond workspace path containment). Not implemented in phase 1; operators rely on Wards, Sanctum, and path containment today.

**API:** **`GET /api/wards`**, **`GET /api/wards/{id}`**, **`POST /api/wards/{id}`** (`allow`, optional `reason`). Protected by **`ApiKeyEndpointFilter`**. Wards are ephemeral by design — host restart drops all active wards (callers' `TaskCompletionSource` instances are gone with their processes). `WardGate` is a fresh, empty singleton on every process start, so there is nothing to actively deny on restart; the `HostRestartedReason` contract value (`"Host restarted — ward timed out"`) documents this behavior for future clients that need to distinguish restart-driven denial from timeout/capacity denial. See docs/Arcanum.PERSISTENCE.md §7.

**Streaming:** NDJSON frames `warded` and `wardResolved` on `/api/intelligence/ping-stream`. OpenAI `/v1` SSE bridge ignores these event types (transparent latency only).

**Related:** Sanctum **`ResourceLimits`** file-write and **`read_file_chunk`** line caps are enforced in **`ArcanumInternalToolServer`** (§11.15); external MCP bridge output is capped via **`Arcanum:Intelligence:ToolOutputCapBytes`** (§11.8).

### 11.15 Sanctum (campaign sandboxing)

**Purpose:** Per-campaign execution isolation — constrain tool file access, network egress, and tool availability within a defined boundary. Separate from **Wards** (operator approval) and from creation-time **`CampaignPathPolicy`** / **`Arcanum:Campaigns:AllowedRoots`**.

**Threat model (phase 1):**
- **Path escape** — `../` traversal, absolute paths outside the campaign workspace, symlink pivots (`File.ResolveLinkTarget` / `Directory.ResolveLinkTarget` with final-target check).
- **Network egress** — outbound Comm Link webhook URL when **`use_commlink`** runs (application-layer check; no kernel firewall on macOS).
- **Disabled tools** — tool names listed in **`SanctumConfig.DisabledTools`**.
- **Resource abuse** — **`ResourceLimits.MaxFileWriteMb`** enforced on in-process **`write_file`** / **`replace_text_block`** before I/O (via **`ISanctumGuard.GetEffectiveResourceLimitsForWorkspaceAsync`**); **`read_file_chunk`** bounded to 2,000 lines per request with capped **`startLine`**. **CPU time, memory, and open file descriptors are enforced at the OS level** on the child processes spawned by **`execute_command`** and **`run_spell_script`** (see "Kernel resource limits" below); on Windows, **`MaxProcessCount`** is also enforced via Job Object **`ACTIVE_PROCESS`**.

**Engine:** Scoped **`ISanctumGuard`** / **`SanctumGuard`** loads **`SanctumConfig`** from **`Campaign.SanctumConfigJson`** (`TheForgeJsonContext`). Breaches are recorded inline to the Grimoire-backed **`ISanctumBreachRepository`** / **`SanctumBreachRepository`** (raw SQL over the **`SanctumBreaches`** table, §16.2) — durable across host restarts. **`SanctumGuard`** and **`ISanctumBreachRepository`** are both scoped and share the same **`ArcanumDbContext`**, so the breach write is part of the same request scope as enforcement; no fire-and-forget is needed. Breaches raised for an unparseable/unknown campaign id are logged only (not persisted), since **`SanctumBreaches.CampaignId`** has a foreign key to **`Campaigns`**. Each insert enforces per-campaign retention (**`SanctumConfig.MaxBreachCount`**, default 1,000, clamp 100 – 100,000): oldest rows beyond the limit are deleted in the same transaction.

**Enforcement modes:** **`SanctumMode.Strict`** — block tool execution with a synthetic denial message. **`SanctumMode.AuditOnly`** — log breach, allow execution.

**Kernel resource limits (`ResourceLimits.MaxCpuSeconds` / `MaxMemoryMb` / `MaxFileDescriptors`, plus Windows `MaxProcessCount` / `MaxProcessMemoryMb`):** Applied via **`IProcessResourceLimiter`** (Core) / **`ProcessResourceLimiter`** (Infrastructure, `src/RetroDownfall.Arcanum.Infrastructure/Platform/`), invoked from **`CappedChildProcessRunner.RunAsync`** — the shared runner behind both **`execute_command`** (`ArcanumInternalToolServer`) and **`run_spell_script`** (**`ArcanumSpellScriptTool`**). This is OS-level enforcement (setrlimit / cgroups v2 / Windows Job Objects), not a container or VM boundary.
- **macOS:** no cgroups, so the limiter rewrites `ProcessStartInfo` to launch the target through a `/bin/sh -c 'ulimit -t …; ulimit -v …; ulimit -n …; exec "$@"' sh <file> <args…>` prelude. Every original argument is passed as its own `argv` entry (never string-interpolated into the script), so spaces/quotes/`$` pass through unmodified with no shell word-splitting or injection risk. `ulimit -v` maps to `RLIMIT_AS` (virtual address space, not physical RSS) — the best available memory proxy without cgroups.
- **Linux:** prefers cgroups v2. For each invocation the limiter creates a transient `/sys/fs/cgroup/arcanum-{guid}.scope/` directory (a GUID name, not a pid — `Apply()` runs before `Process.Start()`, so the child pid is not yet known; this also sidesteps any pid-reuse race), and writes `memory.max` / `memory.high` (bytes) and a best-effort `cpu.max` (`"1000000 1000000"`, i.e. capped to one core — cgroups v2 clamps the period to at most 1s, so `cpu.max` cannot express a cumulative CPU-time budget; it is a rate throttle only). The **same** `ulimit` shell prelude as macOS is still applied for CPU time and file descriptors (cgroups v2 has no FD controller, and only `RLIMIT_CPU` delivers a real SIGXCPU kill once the CPU-time budget is exhausted); when a cgroup is in play, the prelude's first line has the shell join it (`echo $$ > ".../cgroup.procs"`) before `exec`, so the eventual target process — pid-preserved across `exec` — ends up in the cgroup without the .NET side ever needing the child pid. If `/sys/fs/cgroup` is unmounted or not writable (no delegation), cgroup creation is skipped silently and memory falls back to the `ulimit -v` clause too.
- **Windows (Job Objects):** `Apply()` creates an anonymous Job Object, sets `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` plus process/job memory (`MaxMemoryMb` / `MaxProcessMemoryMb`), per-process user-mode CPU time (`MaxCpuSeconds`), and `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` (`MaxProcessCount`), and returns `ProcessResourceLimiterResult.AssignAfterStart`. **`CappedChildProcessRunner`** calls `Process.Start()`, then **`AssignProcessToJobObject` immediately** — before stdout/stderr reads or `WaitForExitAsync`. Closing the job handle (cleanup) kills any remaining descendants. Open file descriptors have no Job Object equivalent and are **not** enforced on Windows. **Post-start race (MVP limitation):** .NET cannot create the child suspended, so between `Start()` and `AssignProcessToJobObject` the child can briefly run (and theoretically spawn breakaway grandchildren) before the job binds — documented honestly; suspended-create is out of MVP. **Fail-closed:** if assignment fails after start (including when the process already belongs to an incompatible job), the runner kills the process tree and returns `ResourceLimitApplyFailed` — the child is never left running unbounded. Memory-limit kills that surface as NTSTATUS `STATUS_QUOTA_EXCEEDED` (`0xC0000044`) map to a `ResourceLimit` memory breach when a memory cap was configured; Windows CPU-time kills lack a stable exit code across versions, so wall-clock timeout remains the reliable CPU attribution path on Windows.
- **Detection (Unix):** after `WaitForExitAsync`, the child's exit code is checked for a signal kill using both possible conventions — a direct kernel report (negative signal, e.g. `-9`/`-24`/`-11`) or the shell convention (`128 + signal`) — and only when the corresponding limit was actually configured (`> 0`), to avoid misclassifying an unrelated `exit(137)` as a breach. SIGXCPU (24) maps to CPU; SIGKILL (9) / SIGSEGV (11) map to memory.
- **Breach recording:** a detected kill, or a failure to apply/assign limits, records a `ResourceLimit` breach (**`ISanctumGuard.RecordResourceLimitBreachAsync`**, resolving the campaign by workspace path) and returns a sanitized denial (**`ResourceLimitDenialFormatter`**) — e.g. *"Execution blocked: this tool exceeded the CPU time limit (30s). The invocation has been terminated and recorded as a breach."* The message never contains signal numbers, PIDs, cgroup paths, Job Object handles, or stack traces; that detail is available only in the breach audit log via the Sanctum breaches API.
- **Known gap:** cgroups v2 covers the entire process subtree (grandchildren included), but the `ulimit`/setrlimit path only bounds the direct child — a grandchild spawned by a tool script is not rlimit-bound on macOS (or on Linux when cgroups fell back to setrlimit). On Windows, Job Objects cover the job's process tree once assigned, subject to the post-start assign race above. Documented, not fixed, in this phase.

**MVP OS filesystem jail (macOS-ARM beta posture):** The same **`CappedChildProcessRunner`** composes env scrub → resource limits → **filesystem jail** → cwd / output caps / cancellation. This MVP is a **filesystem sandbox only** — it does **not** prevent network use by network-capable binaries. Sanctum network policy still applies at known tool boundaries (`browse_web`, Comm Link); `execute_command` network behavior is **not** solved by the FS jail.
- **macOS (active):** wraps the child with deprecated **`/usr/bin/sandbox-exec`** and an owner-only Seatbelt profile (deny-default + explicit allows). Access classes: workspace / Sanctum `AllowedPaths` → read+write; spell script roots (incl. global spells) → **read+execute** (no write unless also an AllowedPath/workspace); system runtime (`/bin`, `/usr`, `/System`, …) → read+execute, **no write**; per-invocation owner-only **`TMPDIR`** → read+write (no broad `/tmp`). Directory walk uses `(allow file-read* (vnode-type DIRECTORY))` for getcwd/dyld path resolution — **not** whole-volume file-content read. **Critical invariant:** no `(subpath "/")` / `(literal "/")` for file content. Network is explicitly allowed in the profile (filesystem-only MVP). Documented as active but **deprecated** — Apple may remove the tool; durable replacement deferred. Absence or profile setup failure fail-closes unless `Arcanum:Security:AllowUnsandboxedToolChildren=true`. Distinct from the Linux internal helper argv `__sandbox-exec`.
- **Linux (inactive for this beta):** Landlock / internal **`__sandbox-exec`** helper code remains **in-tree but is not invoked** (probe-first: not activated until Landlock-backed end-to-end wiring is validated). Default is fail-closed with the public message: *"Linux filesystem jail is not active in this beta. Set Arcanum:Security:AllowUnsandboxedToolChildren=true to run without FS confinement, or use macOS for sandboxed command tools."* Escape hatch runs unsandboxed with a warning; resource limits still apply where available. Do **not** conflate this helper with macOS `/usr/bin/sandbox-exec`.
- **Windows (no FS jail):** never described as filesystem-sandboxed. Result status is `NoFilesystemJail` (Job Objects only) when Sanctum path-boundary is off. Health/`arcanum doctor` report this as **Degraded** (documented ≠ Healthy). When Sanctum is **`Enabled` and `EnforcePathBoundary`**, `execute_command` / `run_spell_script` return `DeniedByWindowsSanctum` (*"Child process filesystem sandbox is unavailable on Windows while Sanctum path-boundary enforcement is enabled…"*). The escape hatch **does not** bypass that Sanctum denial.
- **Fail-closed:** when the jail cannot be applied and the escape hatch is false, the model-visible result is a clear expected denial (Linux beta message above, missing `/usr/bin/sandbox-exec`, profile setup failure, or Windows Sanctum denial) — **not** a Hub generic internal error / unhandled exception / provider failure.
- **Escape hatch:** `Arcanum:Security:AllowUnsandboxedToolChildren` (default `false`) logs a warning (platform, tool name, campaign id when available — no secret-bearing env/argv) and runs without FS confinement; rlimits / Job Objects still apply where available.
- **Operator visibility:** `ToolChildSandboxStatus` / `ToolChildSandboxCapabilityReporter` feed `arcanum doctor` (Tool Child Sandbox panel) and `GET /api/health` component `ToolChildSandbox` (Healthy only when FS jail is active or an equivalent safe state; Degraded for Windows no-FS-jail, Linux inactive fail-closed, escape hatch, or missing macOS sandbox-exec). Network isolation is always reported as **not provided**.

**TOCTOU mitigation:** In-process `read_file_chunk`, `replace_text_block`, and `write_file` capture the validated path's volume/file identity before open, open the handle, then revalidate containment by comparing the opened handle's dev/ino (Unix) or volume serial + file index (Windows) to the pre-open identity. Path containment still uses `WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck`. `replace_text_block` and `write_file` persist via same-directory temp file + atomic `File.Move`.

**API:** **`GET/PUT /api/campaigns/{campaignId}/sanctum`**, **`GET /api/campaigns/{campaignId}/sanctum/breaches`** (paginated: `limit` default 100 clamp 1–1,000, `before` ISO 8601 cursor, `tool` filter; returns `ApiResponse<SanctumBreachQueryResult>` with `Items` + `HasMore`). Protected by **`ApiKeyEndpointFilter`**. Default **`Enabled: false`** on existing and new campaigns (opt-in per campaign). Path-shaped breach detail fields (`RequestedPath`, `ResolvedPath`, `WorkspaceRoot`) are redacted to their filename component (**`SanctumPathRedactor`**) before serialization.

**Deferred:** Container/Firecracker isolation, network namespaces / network isolation for `execute_command`, durable macOS replacement for deprecated `sandbox-exec`, **reactivating Linux Landlock** for non-macOS betas, per-tool path allowlists beyond workspace + **`AllowedPaths`**, network proxy, filesystem overlays. Kernel resource limits (CPU/memory/file descriptors) — **Done**. macOS Seatbelt FS jail — **active for Apple Silicon beta** (deprecated tool). Linux Landlock — **code present, inactive**. Windows FS jail — **not provided** (Job Objects + Sanctum path-boundary denial only).

### 11.16 Session lifecycle (`/api/sessions`)

**Purpose:** Grimoire-backed multi-turn chat threads for The Forge, CLI, intelligence persistence, and operator tooling. **Sessions** and **Entries** replace the former split between in-memory `/api/conversations` and `/api/grimoire/conversations` (§8.18 — removed).

**Store:** `SessionRepository` (`ISessionRepository`) reads and writes `Sessions` / `Entries` through EF Core. Capacity is disk-backed (not RAM-bounded). **`GetSessionAsync`** (Grimoire) still loads a bounded entry window for inference (`Arcanum:Grimoire:MaxMessagesPerConversationLoad`). Session-list and entry-pagination reads that order or filter by `CreatedAt`/`UpdatedAt` are issued as **parameterized** raw SQL over the sortable UTC text columns (with `json_each` for the FTS id-set and `EXISTS` subqueries for role/model filters), because the EF Core SQLite provider cannot `ORDER BY`/compare a `DateTimeOffset` in LINQ; values are bound, never concatenated.

**Creation:**
- **`POST /api/sessions`** — explicit create with optional `campaignId` and `title`.
- **`POST /api/intelligence/ping-stream`** with null `sessionId` — hub calls `BeginAssistantReplyAsync`, persists user + assistant entries, emits **`sessionBound`** and deprecated **`conversationBound`** NDJSON frames.
- Auto-title: when `Title` is null, clients may set it via **`PATCH /api/sessions/{id}`**; inference may derive a title on first turn (hub behavior unchanged).

**Query (`GET /api/sessions`):** Returns **`ApiResponse<SessionQueryResult>`** with optional filters:
- **`campaignId`**, **`status`** (default `active`; pass `all` for every status including archived).
- **`search`**: substring on **`Title`** or any entry **`Content`**.
- **`title`**, **`role`**, **`model`**, **`from`** / **`to`** on session **`UpdatedAt`**.
- Cursor: **`beforeUpdatedAt`** + **`hasMore`**; default **`limit`** from **`Arcanum:Sessions:DefaultQueryLimit`**.

**Entries:**
- Inference turns append via the hub (`IGrimoireRepository`).
- **`POST /api/sessions/{id}/entries`** — manual append (operator or Studio); rejects archived sessions; publishes to **`SessionEventHub`** for live SSE subscribers.
- There is **no update API** for entry content after insert. Gated memory-management routes (when **`Arcanum:Sessions:AllowMemoryManagement`** is **`true`**) allow **delete**, **pin** / **unpin**, and **compact** (`DELETE …/entries/{entryId}`, `POST`/`DELETE …/entries/{entryId}/pin`, `POST …/compact`) — see §4.3.

**Metadata update (`PATCH /api/sessions/{id}`):** Accepts **`UpdateSessionRequest`** with optional **`title`** (`string?`) and **`status`** (`active` | `archived`). Only supplied (non-null) fields change; an empty or whitespace `title` clears it to `null`. An unrecognized `status` returns **400** `Session.InvalidStatus`. Setting `status` to `archived` has the same soft-delete effect as `DELETE /api/sessions/{id}` (PATCH returns **200** + the updated `SessionDetailDto` rather than **204**).

**Archive vs purge:**
- **`DELETE /api/sessions/{id}`** sets **`Status = archived`** (soft delete; **204**). Repeat calls are idempotent.
- **`IGrimoireRepository.PurgeSessionAsync`** — hard delete (cascade entries); **not** exposed on the public API.

**Export / analytics:**
- **`GET /api/sessions/{id}/export?format=json|markdown`**
- **`GET /api/sessions/analytics`** — aggregate counts over Grimoire (sessions, entries by role, tokens, per-model breakdowns).

**Live stream (`GET /api/sessions/{id}/stream`):** `text/event-stream`. Subscribes to **`SessionEventHub`** **before** the DB read (entries published during replay are not lost), replays the most recent **`Arcanum:Sessions:MaxStreamReplayEntries`** entries ascending (default 500, clamp 1–10,000), emits `data: {"type":"live"}\n\n`, then forwards live entries (hub inference + manual append), de-duplicating any already replayed. On disconnect, best-effort `data: [DONE]\n\n`.

**Campaign Log:** **`POST /api/sessions/{id}/rest`** returns **202** + **`ApiResponse<bool>`** when the session exists and is queued (§8.7), or **503** + **`Session.RestQueueFull`** when the bounded Campaign Logger queue rejects the enqueue.

#### 11.16.1 Session forking (`POST /api/sessions/{id}/fork`)

Creates a brand-new session that is an editable, independent branch of an existing one — useful for exploring an alternate reply or model without mutating the original transcript. Returns **`201 Created`** + `ApiResponse<SessionDetailDto>` on success.

**Request body (`ForkSessionRequest`, all fields optional):**
- **`title`** — defaults to `"Fork of {source title}"` (or `"Fork of Untitled Session"` when the source has no title).
- **`upToEntryId`** — copies only entries up to and including that entry (inclusive cutoff) instead of the full transcript; the entry must belong to the source session or the request fails with **404** `Session.EntryNotFound`.
- **`campaignId`** — defaults to the source session's `CampaignId`; pass an explicit value (including `null`) to override.

**Behavior:**
- The source session may be `active` or `archived` — archived sources may still be forked (the new fork always starts `active`).
- Copied entries get **new `Id` values** (never reuse the source's entry ids) but preserve `Role`, `Content`, tool metadata, and original `CreatedAt` ordering.
- The new session's **`ForkedFromSessionId`** is set to the source session's id, recording lineage; **`TotalTokensUsed`** starts at the sum of the copied entries' token usage (not the source's running total) and **`UnsummarizedEntryCount`** starts fresh.
- **Attachments:** copies **Bound** attachments into a new byte tree under the fork session id (new attachment ids; remapped `EntryId`s). Full fork includes Bound rows with `EntryId` null; a cutoff fork (`upToEntryId`) copies only rows whose non-null `EntryId` is among the copied entries. Bytes are pre-copied and hash-verified before the DB write; `Session` / `Entries` / `SessionAttachments` insert in one EF ambient transaction (raw SQL enlisted).
- **Fork depth guard:** the lineage chain (`ForkedFromSessionId` walked back to a root) is capped at **`Arcanum:Sessions:MaxForkDepth`** (default `3`, clamp 0–20). Exceeding it returns **409** `Session.ForkDepthExceeded` — protects against unbounded fork chains inflating storage and lineage-walk cost.
- Forking a session that is already at (or over) **`Arcanum:Sessions:MaxEntriesPerSession`** entries fails the same way a normal append would (`Session.TooManyEntries`).

Fork-specific error codes: `Session.NotFound` (source missing), `Session.EntryNotFound` (`upToEntryId` invalid or from another session), `Session.ForkDepthExceeded`.

**Error codes (§11.16 overall):** `Session.NotFound`, `Session.EmptyContent`, `Session.Archived`, `Session.InvalidStatus`, `Session.EntryNotFound`, `Session.ForkDepthExceeded`.

**Key types:** `Session`, `Entry`, `ISessionRepository`, `SessionRepository`, `SessionEventHub`, `SessionSettings`, `ForkSessionRequest`, The Forge DTOs under **`Core.TheForge`**.

### 11.17 `Idempotency-Key` request replay

Opt-in, client-supplied replay protection (Stripe-style semantics) for the eight side-effecting inference endpoints: **`POST /api/intelligence/ping`**, **`POST /api/intelligence/ping-stream`**, **`POST /v1/chat/completions`** (both buffered and streaming), **`POST /v1/embeddings`**, **`POST /api/spells/{name}/execute`**, **`POST /api/spells/{name}/execute-stream`**, **`POST /api/prompts/{id}/execute`**, and **`POST /api/prompts/{id}/execute-stream`**. Requests without an `Idempotency-Key` header are unaffected — the feature is entirely bypassed at effectively zero cost.

**Claim key ≠ fingerprint:** claim identity is `SHA-256(principal + API version + HTTP method + normalized route + Idempotency-Key)`. Fingerprint is `SHA-256(canonical body + route + selected Content-Type)`. Same key with a different fingerprint → **409** `Security.IdempotencyConflict`. Only **terminal** Completed claims (writer-marked, within byte cap) are replayable; cancelled/partial/over-cap streams → Abandoned. Durable table: `IdempotencyClaims` (raw SQL); legacy `IdempotencyKeys` remains for TTL sweep compatibility.

**Key and hashing (legacy note):** older docs described a single hash of key++body. Prefer the claim/fingerprint split above. `IdempotencyEndpointFilters` derives canonical body bytes one of two ways depending on how the endpoint binds its request:
- **`ForBoundArgument<TRequest>`** (`/api/intelligence/ping`, `/v1/embeddings`) — the already-model-bound request DTO is re-serialized through the same source-generated `JsonTypeInfo<TRequest>` used on the wire. No raw body re-read needed.
- **`ForRawBody`** (`/api/intelligence/ping-stream`, `/v1/chat/completions`) — these handlers read `HttpContext.Request.Body` themselves, so the filter calls `Request.EnableBuffering()`, copies the raw bytes for hashing, then rewinds the stream to position 0 before invoking the handler.

**Header validation:** an `Idempotency-Key` longer than 256 characters is rejected with **400** `Security.IdempotencyKeyTooLong` (`/api` `ApiResponse<string>` envelope, or `/v1` `invalid_request_error` envelope depending on route) *before* any body buffering or cache lookup — a fast, cheap rejection.

**Cache hit:** the handler is **never invoked** — `IdempotencyEndpointFilters` short-circuits with a small `IdempotencyReplayResult` that writes the cached status code, content type, and body bytes verbatim.

**Cache miss (buffered *and* streaming, same mechanism):** `HttpResponse.Body` is substituted with an `IdempotencyBufferingStream` that tees every write into a capped in-memory buffer while forwarding to the real response stream (and keeps buffering if the client disconnects under continue-then-replay). An `HttpResponse.OnCompleted` callback persists only when the writer marked the response terminal and the buffer stayed within cap.

**Disconnect (ADR 0003):** default `Arcanum:Intelligence:DisconnectPolicy=Auto` — with an `Idempotency-Key`, inference continues after client disconnect so the claim can Complete for later replay; without a key, cancel → Abandoned. Partial billed cost is still ledgered either way.

**Oversized responses are never cached, never truncated:** once the tee buffer would exceed `Arcanum:Security:IdempotencyMaxResponseBytes` it releases the memory it was holding and permanently stops accumulating; the client-visible response is completely unaffected — only the cache write is skipped. A `BufferingStream` failure (`OutOfMemoryException`, `ObjectDisposedException`) is handled the same way: stop buffering, keep streaming, skip the cache write, log a warning.

**TTL and expiry:** claim rows older than `Arcanum:Security:IdempotencyTtlHours` (default `24`, clamp 1–168) are swept by `UnseenServantService` (`IIdempotencyClaimStore.DeleteExpiredAsync` plus legacy `IIdempotencyStore`).

**Cleanup:** no dedicated `BackgroundService`. `UnseenServantService` (§21, the existing 1-minute scheduler tick) runs expiry deletes once at host startup and thereafter every hour. A sweep failure is logged and retried on the next scheduled tick — it never blocks the scheduler's other jobs.

**Persistence:** `IdempotencyClaims` (claim key hash, fingerprint, state machine, lease, optional response body, optional late-bound `RunId`) — embedded raw-SQL table (not part of the compiled EF model).

**Fail-open:** a cache backing-store failure (lookup or save) is logged and swallowed — an unavailable Grimoire connection must never block inference; the request simply executes fresh.

**Error codes:** `Security.IdempotencyKeyTooLong`, `Security.IdempotencyConflict`.

**Key types:** `IIdempotencyClaimStore`, `IdempotencyClaimStore`, `IdempotencyEndpointFilters`, `IdempotencyReplayResult`, `IdempotencyBufferingStream` (Api, `Security`); legacy `IIdempotencyStore` retained for sweep.

### 11.18 OpenAI moderations (`POST /v1/moderations`)

**Purpose:** OpenAI-compatible content moderation route. Arcanum does **not** run a moderation model. The endpoint always returns **501 Not Implemented** with `OpenAiErrorResponse` (`type: "invalid_request_error"`, `code: "not_supported"`), matching the images/audio stubs (§11.19).

**Config:** `Arcanum:Moderations` is an **obsolete key** — if present in `arcanum.json`, startup fails with a migration error instructing operators to remove the block. There is no enable toggle.

**Request / response shapes:** retained only so clients can deserialize error envelopes; success payloads are never returned.

**Error codes:** `not_supported` (501).

**Key types:** `OpenAiErrorResponse`, `OpenAiErrorDetail` (shared with other unsupported `/v1` stubs).

### 11.19 OpenAI images and audio stubs (`POST /v1/images/*`, `POST /v1/audio/*`)

**Purpose:** OpenAI route-surface completeness for clients that probe or unconditionally call these endpoints, without implementing any actual image generation/editing or audio transcription/synthesis yet.

**Routes:** `POST /v1/images/generations`, `/edits`, `/variations`; `POST /v1/audio/transcriptions`, `/translations`, `/speech`.

**Behavior — unconditional, no config toggle:** every route always returns **501 Not Implemented** with the standard `OpenAiErrorResponse` envelope (`type: "invalid_request_error"`, `code: "not_supported"`, `param: null`), regardless of any setting — the same contract as `/v1/moderations`. There is no partial or pass-through behavior worth toggling here. A config toggle is only worth adding once real functionality lands.

**Key types:** none new beyond the existing `OpenAiErrorResponse`/`OpenAiErrorDetail` — see `OpenAiV1UnsupportedStubs.cs`.

### 11.20 OpenAI files (`/v1/files`)

**Purpose:** OpenAI-compatible standalone file upload storage — primarily feeds `/v1/batches` (§11.21) `input_file_id`, but usable standalone. File bytes live on disk under `ArcanumPaths.FilesDirectory` (`~/.config/arcanum/files/`); the `UploadedFiles` Grimoire row is metadata only.

**Storage naming (security):** every uploaded file is stored under a **fresh `Guid`-named path** (`{FilesDirectory}/{id:N}`, computed by `UploadedFileStorage.ResolvePath`), never the client-supplied filename — path traversal and filename collisions are structurally impossible. The original filename is retained only as row metadata (`UploadedFileRecord.Filename`), used for `Content-Disposition` on download and echoed back in the wire `file` object.

**Endpoints:**
- **`POST /v1/files`** — `multipart/form-data`: `file` (binary, required) + `purpose` (string, required — any non-empty value; Arcanum does not enforce OpenAI's specific purpose enum since Phase 1 has no per-purpose behavior beyond what `/v1/batches` expects for `purpose: "batch"`). Returns **201** + `OpenAiFileObject`.
- **`GET /v1/files?purpose=`** — list, optionally filtered; **200** + `OpenAiFileListResponse`.
- **`GET /v1/files/{id}`** — metadata; **404** `not_found` for an unknown or malformed id.
- **`DELETE /v1/files/{id}`** — deletes the Grimoire row and the on-disk file (best-effort on the disk side — a failed disk delete never blocks the metadata delete); **200** + `OpenAiFileDeleteResponse`.
- **`GET /v1/files/{id}/content`** — raw bytes. **`Content-Type`** is the file's stored MIME type (falling back to `application/octet-stream` only if none was recorded — not hardcoded to octet-stream). **`Content-Disposition: attachment`** always — **never `inline`** — this is the primary XSS mitigation against an uploaded `.html`/`.svg` payload being rendered if a browser hits this URL directly; the extension/MIME cross-check below is secondary defense-in-depth, not the primary one.

**Wire id scheme:** `id` is `"file-{guid:N}"` (32 hex chars, no dashes). `GET`/`DELETE`/`.../content` parse this back to a `Guid`; a malformed id (wrong prefix, not valid hex) is treated as "not found" (**404**), never a **500**.

**Upload validation (in order — first failure wins):**
1. `file` present and non-empty, `purpose` present and non-empty → else **400** `missing_required_parameter`.
2. Filename must not exceed 255 characters, and (defense-in-depth; unreachable through any conformant HTTP client, whose header-quoting rejects embedded control characters before the request is even sent) must not contain an embedded null byte → else **400** `invalid_value`.
3. Size ≤ `Arcanum:Files:MaxUploadSizeBytes` (default 512 MiB, clamp 1 MiB – 10 GiB) → else **413** `invalid_value`. The endpoint's Kestrel request-body limit is raised to the clamp's 10 GiB ceiling (`WithFileUploadRequestBody`) precisely so the *handler* returns this structured JSON error instead of Kestrel aborting the connection below the operator's configured limit.
4. Extension/declared-Content-Type cross-check (`UploadedFileMimeValidator.IsExtensionMimeMismatch`) — a *known* extension (`.png`, `.jsonl`, etc.) paired with an unexpected declared type is rejected (**400** `invalid_value`); an *unrecognized* extension is always allowed through (nothing to cross-check against).
5. If `Arcanum:Files:AllowedMimeTypes` is non-empty, the declared type must be in that operator allow-list → else **400** `invalid_value`.

**Permissions:** the files directory and every stored file get owner-only permissions via the existing `SecureFilePermissions` helper (600 Unix / owner ACL Windows) — no new permission logic.

**Error codes:** `Files.NotFound` (404), `Files.TooLarge` (413), `Files.InvalidMimeType` (400) — registered in the shared catalog (§8.23) for consistency and reuse by `/v1/batches`, even though the `/v1/files` handlers themselves construct their OpenAI-shaped error envelopes directly (matching every other `/v1` endpoint) rather than routing through `ArcanumErrorMapper`.

**Key types:** `FilesSettings`, `IUploadedFileRepository`, `UploadedFileRecord`, `UploadedFileRepository` (Infrastructure), `UploadedFileStorage` (pure path helper), `UploadedFileMimeValidator`, `OpenAiFileObject`, `OpenAiFileListResponse`, `OpenAiFileDeleteResponse`.

### 11.21 OpenAI batches (`/v1/batches`)

**Purpose:** OpenAI-compatible asynchronous bulk chat-completion processing over an uploaded JSONL file (§11.20). **Phase 1 supports only `endpoint: "/v1/chat/completions"`** — other endpoint values are rejected with **400** `invalid_value`.

**Layering note (why the processor lives in the Api project, not Infrastructure):** every other background poller (`EntryWeavingService`, `SagaExtractionService`, `UnseenServantService`, ...) lives in `RetroDownfall.Arcanum.Infrastructure`. `BatchProcessingService` is the one exception — it must call `IArcanumIntelligenceProvider.ExecutePromptAsync` and construct/parse the `/v1` OpenAI DTOs (`OpenAiChatRequest`/`OpenAiChatResponse`/the JSONL wrapper types), all of which live in the **Api** project, and the dependency direction only ever goes Api → Infrastructure. Rather than move those DTOs down into Core (a large, unrelated refactor) or duplicate them, `BatchProcessingService` is registered and hosted from the Api project (`ApiBootstrapper.AddArcanumApiServices`, `services.AddHostedService(sp => sp.GetRequiredService<BatchProcessingService>())`), exactly mirroring how `IArcanumIntelligenceProvider`'s own concrete implementation (`WizardIntelligenceProvider`) is Api-hosted despite the interface living in Core.

**Endpoints** (metadata CRUD only — see below for the actual JSONL processing):
- **`POST /v1/batches`** — body `{input_file_id, endpoint, completion_window}` (`completion_window` accepted but not enforced — Arcanum's expiry timer is `Arcanum:Batches:BatchExpiryHours`, independent of this field). Validates `input_file_id` resolves to an existing uploaded file (§11.20) and `endpoint` equals `/v1/chat/completions`. Creates a `Batches` row with `status: "validating"` and returns immediately — **200** + `OpenAiBatchObject`. The actual processing happens out-of-band.
- **`GET /v1/batches/{id}`** — current status + `request_counts`; **404** for unknown/malformed id.
- **`GET /v1/batches?status=`** — list, optional status filter; `{object: "list", data: [...], has_more: false}` (`has_more` is always `false` — no pagination cursor yet).
- **`POST /v1/batches/{id}/cancel`** — sets `status: "cancelled"` if not already terminal; idempotent (cancelling an already-terminal batch just returns its current state, matching OpenAI rather than erroring on a double-cancel). `BatchProcessingService`'s cancellation watcher (below) observes this and stops in-flight processing promptly.
- **`POST /v1/batches/{id}/reset`** — operator recovery via shared `IBatchRecoveryService`: resets a batch stuck in `in_progress` back to `validating` (CAS) so the background processor will pick it up again. Rejects with **409** if `BatchProcessingService` currently has the batch in flight (best-effort race window; the real guard is the service's `_inFlight.TryAdd` when it actually starts processing). Rejects with **400** if the input file metadata or on-disk file is missing, because the batch cannot be safely reprocessed. This is an Arcanum extension, not an OpenAI standard route.

**Wire id scheme:** `"batch_{guid:N}"` (underscore, matching OpenAI's real batch ids — distinct from `/v1/files`' hyphenated `"file-{guid:N}"`).

**`request_counts` computation:** there are no dedicated count columns on `Batches` (matching the plan's exact schema — `Id, InputFileId, Endpoint, Status, CreatedAt, CompletedAt, OutputFileId, ErrorFileId`). `BatchRequestCounter` computes `{total, completed, failed}` on every `GET` by reading the input/output/error files directly off disk: `total` = non-empty line count in the input file; `completed`/`failed` = outcome counts parsed from the output file's `BatchJsonlResponseLine.Error` (`null` → completed, populated → failed) plus any parse-failure lines recorded in the error file. Best-effort — a file that is missing or fails to read contributes `0` rather than erroring the `GET`.

**Startup recovery:** `BatchProcessingService.StartAsync` calls `IBatchRecoveryService.ReconcileStrandedAsync` before Kestrel accepts work. Every DB-stranded `in_progress` batch is CAS-transitioned: → `validating` when input metadata + on-disk file exist; else → `failed` (reason logged only — no failure-reason column). Same recovery path powers `/reset`.

**`BatchProcessingService` (background processor):**
- Polls every 5 seconds via `PeriodicTimer` (same shape as `UnseenServantService`/`EntryWeavingService`).
- **Expiry sweep (every tick):** any non-terminal batch (`validating`/`in_progress`) older than `Arcanum:Batches:BatchExpiryHours` (default 24, clamp 1–168) is expired. If the batch is **not** currently in-flight in the processor, it is force-marked `status: "expired"` and its input/output/error files are deleted from disk (best-effort — a delete failure is logged and does not block the status update). If the batch **is** in-flight, the expiry sweep signals that batch's processing cancellation token and does **not** delete files; the processor/finalizer marks `expired` and performs file cleanup after cancel completes.
- **Dispatch:** picks up `validating` batches, bounded by `Arcanum:Batches:MaxConcurrentBatches` (default 3, clamp 1–20) concurrently in-flight across the whole server (tracked in an in-process `ConcurrentDictionary`). Crash mid-batch leaves `in_progress` until startup reconcile or `/reset`.
- **Per-batch processing:** sets `status: "in_progress"`, **streams** the input file line-by-line (does not load the entire JSONL into memory), parses each as a `BatchJsonlRequestLine` (OpenAI's real wrapper shape: `{custom_id, method, url, body: OpenAiChatRequest}` — not a bare chat request). A line that fails to parse is recorded to the **error file** as `{"line": N, "error": "..."}` (`BatchJsonlParseError`) and does not consume an inference call. A line that parses successfully is executed via `OpenAiV1Endpoints.ExecuteChatRequestForBatchAsync` (reuses the same `OpenAiChatCompletionMapper.ToPingRequest` mapping and buffered `OpenAiChatResponse` shape as live `POST /v1/chat/completions`, minus that endpoint's HTTP-layer pre-checks like multimodal part limits or `tools`/`tool_choice` rejection — a line that would trip one of those still gets a clean per-line failure via the intelligence provider's own validation) — the **outcome always goes to the output file** as a `BatchJsonlResponseLine`, whether it succeeded (`response` populated, `error: null`) or the inference call itself failed (`response: null`, `error` populated) — only JSON-parse failures go to the error file, matching OpenAI's own input-file-vs-per-request-error distinction.
- **Bounded per-batch concurrency:** valid lines within one batch run through `Parallel.ForEachAsync` bounded by `Arcanum:Batches:MaxConcurrentRequestsPerBatch` (default 1 — sequential; clamp 1–10), so one large batch can never monopolize the shared inference hub.
- **Mid-batch cancellation:** a side task polls the Grimoire every 2 seconds for this batch's `status` flipping to `"cancelled"` (set by `POST .../cancel`) and, if seen, cancels a linked `CancellationTokenSource` so `Parallel.ForEachAsync` stops promptly instead of draining every remaining line first; whatever output/error accumulated up to that point is still written and attached.
- **Finalization:** writes output/error JSONL **incrementally to temp files** as lines complete (bounded per-line memory), then moves non-empty temps into the uploaded-files directory via the same files repository as `/v1/files` (`purpose: "batch_output"` / `"error"`), then sets the batch's terminal status (`completed` or `cancelled`) plus `CompletedAt`/`OutputFileId`/`ErrorFileId`. An unhandled exception anywhere in this pipeline is caught at the top level and marks the batch `failed`.

**Error codes:** `Batches.NotFound` (404), `Batches.InvalidEndpoint` (400), `Batches.InputFileNotFound` (404) — registered in the shared catalog (§8.23) for consistency, even though the `/v1/batches` handlers construct their OpenAI-shaped error envelopes directly like every other `/v1` endpoint.

**Key types:** `BatchesSettings`, `IBatchRepository`, `BatchRecord`, `BatchStatuses`, `BatchRepository` (Infrastructure), `BatchProcessingService`, `IBatchRecoveryService` / `BatchRecoveryService`, `BatchRequestCounter`, `OpenAiBatchRequest`, `OpenAiBatchObject`, `OpenAiBatchRequestCounts`, `OpenAiBatchListResponse`, `BatchJsonlRequestLine`, `BatchJsonlResponseLine`, `BatchJsonlResponseBody`, `BatchJsonlError`, `BatchJsonlParseError`.

### 11.27 Built-in web browsing tool (`browse_web`)

**Purpose:** gives the inference toolset and the CLI a way to fetch a web page and extract its title, visible text, and top absolute links — for fact-checking, doc lookup, and link discovery — without an external MCP server. Disabled by default; gated by `Arcanum:WebBrowsing:Enabled`.

**Surface:**

- Inference toolset: when enabled, `WizardIntelligenceProvider.BuildToolSetWithMcpAsync` appends an `ArcanumBrowseWebTool` instance to every turn's toolset (alongside `ArcanumLocalTimeTool` / `ArcanumSystemInfoTool`). The model may call it like any other tool.
- Diagnostic endpoint: `POST /api/tools/invoke` (§4.3) takes `{ "toolName": "browse_web", "arguments": { "url": "...", "maxLinks": 10 } }` and returns the raw tool output as JSON. The CLI `arcanum browse <url>` command calls this endpoint and renders the title, a content preview, and the link list with `Spectre.Console`.

**Tool contract (`ArcanumBrowseWebTool : AIFunction`):** hand-authored `JsonDocument` schema (AOT-safe — no `AIFunctionFactory.Create` reflection), `ToolName = "browse_web"`. Parameters: `url` (string, required), `maxLinks` (int, optional, default `10`). Returns a `BrowseWebResult` JSON object: `{ "title", "content", "links": [...] }`.

**Egress security (two layers, both always on):**

1. **`OutboundUrlGuard.ValidateUntrustedUrlAsync`** runs before the fetch — rejects loopback, private (RFC 1918), link-local (169.254/10), CGNAT (100.64/10), and `::1`/`fe80::` hosts, with DNS-rebind IP pinning on the actual `SocketsHttpHandler` connection (§11.11). The named `HttpClient(ArcanumBrowseWeb)` is built with `OutboundUrlGuard.CreateUntrustedEgressHandler()` as its primary handler, so even a guard-bypassing redirect target is still connection-pinned to a public IP.
2. **Sanctum campaign network policy** — `ToolExecutionPipeline.ValidateToolPathsAndNetworkAsync` has a `case "browse_web":` arm that calls `ISanctumGuard.ValidateNetworkAsync(campaignId, targetUrl, toolName, ct)` against the active campaign's `NetworkPolicy` (`AllowAll` / `AllowList` / `DenyAll`). A non-allowed URL surfaces as a Sanctum breach and short-circuits the tool call before any egress (§11.15).

**Content capping:** the response body is read through `ReadCappedStringAsync` up to `Arcanum:WebBrowsing:MaxContentBytes` (default 50,000; clamp 1,000–1,000,000). The cap is applied at the read loop, not on `Content-Length`, so chunked/streaming responses are bounded too. Content beyond the cap is truncated with a `...(truncated)` marker and still returned to the model (partial content is more useful than a hard reject).

**HTML parsing:** uses `HtmlAgilityPack` (added to `RetroDownfall.Arcanum.Api.csproj`). The extractor walks `//body` (falling back to the document root), skips `script`/`style`/`noscript`/`nav`/`header`/`footer` subtrees (including nested text), and concatenates visible text nodes with whitespace normalization. Link extraction resolves relative `href`s against the page's base URI, deduplicates, filters to `http(s)` only, and caps at `maxLinks` (clamped 0–100). `AOT` cleanliness is verified by `./scripts/verify-aot-il-warnings.sh` after every phase — `HtmlAgilityPack` 1.12.x ships AOT-compatible assemblies.

**Timeouts and errors:** the named `HttpClient` has `Timeout = Arcanum:WebBrowsing:RequestTimeoutSeconds` (default 10; clamp 1–60). On timeout the tool returns `WebBrowsing.Timeout` (504 via the `/api/tools/invoke` error path); on SSRF block `WebBrowsing.SsrfBlocked` (403); on a missing/malformed/non-`http(s)` URL `WebBrowsing.InvalidUrl` (400). Non-success HTTP statuses are returned as a `BrowseWebResult` with the status code in `content` (the model can react to a 404/500), not as a tool exception — keeping the turn resilient. Unexpected exceptions are logged and surfaced to the model as a generic `[Tool error: ...]` string so the turn continues.

**DI registration:** `AddHttpClient(ArcanumBrowseWebConstants.HttpClientName, ...)` in `ServiceCollectionExtensions.AddArcanumInfrastructure`, configured with the clamped timeout and `OutboundUrlGuard.CreateUntrustedEgressHandler()` as the primary handler. `IBuiltInToolRegistry` (default `BuiltInToolRegistry`) is registered `AddScoped` and resolves `browse_web` only when `WebBrowsing.Enabled` is true.

**Key types:** `WebBrowsingSettings`, `ArcanumBrowseWebTool` (`Api/Intelligence/Tools/`), `ArcanumBrowseWebConstants` (`Infrastructure/Intelligence/`), `BrowseWebResult` (`Api/Models/`), `IBuiltInToolRegistry` / `BuiltInToolRegistry` (`Api/Intelligence/Tools/`), `ToolInvokeRequest` / `ToolInvokeResponse` (`Api/Models/`), `ToolInvokeEndpoints` (`Api/Intelligence/Tools/`), `BrowseCommand` (`Cli/Commands/`).

---

### 11.28 Diagnostic MCP Invocation (`POST /api/mcp/tools/invoke`)

**Purpose:** an operator-facing diagnostic endpoint to directly invoke **external** MCP tools by name, outside of an inference turn — for verifying that a configured MCP server actually responds, that tool arguments serialize correctly, and that output formatting/capping behaves as expected. It is **not** model execution and **not** an unrestricted tool bypass: it is policy-constrained, authenticated, and limited to external MCP servers.

**Policy (fallback — external MCP only):** routing internal-tool diagnostics through `ToolExecutionPipeline` (the preferred policy in the original plan) was assessed and deferred: `EnforceSanctumAsync` short-circuits when `turnContext.Campaign is null`, and `RequiresWardForTool` only wards `write_file`/`replace_text_block`/`delete_lexicon`/`run_spell_script` when `campaignRequiresWard=true`. A campaign-less diagnostic invoke would therefore get **no Sanctum path/network validation** and would **not** ward four of the five Forbidden Arts — closing those gaps means synthesizing a diagnostic `TurnContext` that forces `SanctumEnabled=true` + `CampaignRequiresWard=true` without a real campaign, which is new behavioral surface (a diagnostic invoke triggering Sanctum validation and Ward prompts against a workspace with no campaign). The fallback is materially lower-risk and matches the existing `ScryingPoolViewModel` direction ("external MCP direct invocation is not exposed by Arcanum yet").

**What is blocked:**

- The internal in-process server `arcanum-internal` (the clean discriminator — its tools are the high-risk internal handlers `execute_command`, `write_file`, `replace_text_block`, `delete_lexicon`, `read_file_chunk`, `list_directory`, etc.).
- All five Forbidden Art names (`execute_command`, `write_file`, `replace_text_block`, `delete_lexicon`, `run_spell_script`) are blocked **by name** before any server lookup, so a third-party MCP server that happens to expose a colliding name is still rejected with `Mcp.DiagnosticBlocked`.
- The blocked-tool error message is fixed: *"This tool cannot be invoked from the diagnostic endpoint because it is a Forbidden Art or requires the Wizard tool execution pipeline."*

**What is allowed:**

- Any tool exposed by a **running**, **visible**, **external** MCP server (i.e. not `arcanum-internal`). Workspace-local external servers must be trusted — `IMcpConnectionManager.GetServerStatusesAsync` already hides untrusted workspace-local servers, so they never reach the diagnostic service.
- Optional `workingDirectory` scopes the visible surface; optional `serverName` disambiguates when the same tool name is provided by more than one running external server (else **400** `Mcp.AmbiguousTool` listing the candidates).

**Inherited caps (no new knobs):**

- **Output cap:** `Arcanum:Intelligence:ToolOutputCapBytes` (default 1 MiB; clamp 64 KiB – 64 MiB) is enforced inside `McpBridgeTool` via `McpSecurityLimits.TruncateUtf8` (UTF-8-boundary-safe truncation with a `[truncated: exceeded N bytes]` marker). The diagnostic service detects that marker in the result text and sets `truncated: true` on the response.
- **Timeout:** `Arcanum:Mcp:RequestTimeoutSeconds` (default 60) is enforced as a linked `CancellationTokenSource` around `AIFunction.InvokeAsync`; on expiry the service returns **504** `Mcp.DiagnosticTimeout` (the MCP SDK's own per-request timeout still applies underneath).
- **Auth:** inherits `X-Arcanum-Key` from the `/api` group filter — no unauthenticated access.
- **Secrets:** exception messages are length-capped (512 chars) before being returned to the caller, as a defensive last step on the rare exception path. The MCP bridge already formats tool output, so this is not a substitute for the bridge's own handling.

**Invoke path:** after external-only discovery (statuses + optional `serverName` disambiguation; `arcanum-internal` excluded **before** candidate counting so an internal name collision never yields `AmbiguousTool`), the service calls **`IMcpConnectionManager.GetToolAsync(serverName, toolName, workingDirectory)`** to obtain the `AIFunction` bound to that managed server's own client — **never** re-resolving by bare name on the merged `GetAvailableToolsAsync` inference surface. `McpBridgeTool` remains `internal` to Infrastructure; the API project treats the result as `AIFunction`. The result text is parsed as JSON when possible (else wrapped as a JSON string) and returned as `McpToolInvokeResponse` { `result`, `serverName`, `toolName`, `durationMs`, `truncated` }. A tool that returns `isError: true` makes `McpBridgeTool` throw `InvalidOperationException`, which the service maps to **400** `Mcp.ToolError`.

**Built-ins unchanged:** `POST /api/tools/invoke` (§11.27) continues to expose only the low-risk built-in tools (`get_local_system_time`, `get_system_info`, `browse_web` when enabled) and does **not** go through Ward/Sanctum — acceptable only because that registry is deliberately limited. The two endpoints are complementary: `/api/tools/invoke` for built-ins, `/api/mcp/tools/invoke` for external MCP.

**Key types:** `McpToolInvokeRequest` / `McpToolInvokeResponse` (`Api/Models/`), `DiagnosticMcpInvocationService` / `DiagnosticMcpInvocationOutcome` (`Api/Mcp/`), `DiagnosticMcpInvocationEndpoints` (`Api/Mcp/`), mirrored The Forge DTOs `McpToolInvokeRequest` / `McpToolInvokeResponse` (`TheForge.Core/Models/`) + `TheForgeJsonContext` registrations.

**Tests:** `tests/RetroDownfall.Arcanum.Tests/Mcp/DiagnosticMcpInvocationServiceTests.cs` covers every Forbidden Art block, empty tool name, stopped server, internal-server filter, untrusted-workspace hiding, ambiguous tool, tool-not-found (named and unnamed), happy path, truncation marker, tool error (`isError: true`), timeout, non-JSON output wrapping, **internal+external name collision (external invoked; not ambiguous)**, **internal-only → ToolNotFound**, and **explicit wrong server → no fallback** — all with a fake `IMcpConnectionManager` + fake `AIFunction`, no API host required. Source-generated JSON round-trips for `McpToolInvokeRequest` / `McpToolInvokeResponse` / `DiagnosticMcpFixtureStoreDocument` are in `tests/RetroDownfall.TheForge.Tests/TheForgeJsonContextTests.cs`.

---

## 12. C# language and coding conventions

- **File-scoped namespaces** used consistently.
- **Primary constructor-style DTOs** — positional records for `Error`, `ApiResponse<T>`, `PingRequest`, `IntelligenceEvent`. No `[JsonPropertyName]` on `/api` DTOs; casing comes from `[JsonSourceGenerationOptions]`. **Exceptions:** OpenAI `/v1` types and MCP JSON-RPC contexts use explicit `[JsonPropertyName]` where an external spec mandates snake_case or JSON-RPC member names (§8.2).
- **Primary constructors on services** for DI injection.
- **`IDisposable`** on infrastructure services with `SemaphoreSlim` or `ServiceProvider` ownership.
- **Blank line after each line of C# code** for visual breathing room.
- **Convention scope (project-specific vs inherited).** The conventions in this section plus the README naming metaphor are **specific to Arcanum**. Organization-wide standards scoped to `Corp.Solution.*`-prefixed solutions — Dapper repositories over SQL Server stored procedures, the `Corp.Lib.*` / `Corp.Api.Configuration.Lib` NuGet stack, and Refit "Service Library" API contracts — **do not apply** here: Arcanum is local-first over its own EF Core + SQLCipher Grimoire (no SQL Server, no stored procedures) and ships as a single Native AOT binary. The always-on house rules still hold — one blank line after each C# statement (above), strict CSP with no inline JS/CSS on every web surface, and `README.md` + `DESIGN.md` updated in the same change set as code (§18).

---

## 13. Testing strategy

`tests/RetroDownfall.Arcanum.Tests` (xUnit, **1,500+** tests) exercises **Core**, **Infrastructure**, **Api**, and **Cli** on the normal CLR. Hand-written fakes only; no live LLM provider required for most cases.

### Coverage gate

| Tier | Target | Enforced by |
|------|--------|-------------|
| Line (post-exclusions) | ≥ 85% | `./scripts/coverage.sh --threshold` |
| Branch (post-exclusions) | ≥ 75% | same |
| Security-critical branch | 100% | `ApiKeyEndpointFilter`, `ApiKeyDigestCache`, `DataProtectionSecretStore`, `GrimoireKeyDerivation`, `McpSecurityLimits`, `SandboxedFileIo`, `SanctumGuard`, `ToolHelpers`, `OutboundUrlGuard`, `WardGate` |

Measured assemblies: `RetroDownfall.Arcanum.Core`, `.Infrastructure`, `.Api`, `.Cli`. `Api.DevHost` is referenced for `WebApplicationFactory` wiring but is **not** in the coverage denominator.

Configuration: `tests/RetroDownfall.Arcanum.Tests/coverage.runsettings`, `scripts/coverage.sh`, `scripts/coverage_threshold.py`. HTML report: `.tmp/coverage/report/index.html`.

### Exclusion policy

- **runsettings:** `obj/`, `*.g.cs`, EF migrations, JSON source-gen contexts, framework-generated assemblies.
- **`[ExcludeFromCodeCoverage] // Reason: ...` on types:** IHostedService/daemon managers, subprocess transports (`McpProcessTransport`, `McpConnectionManager`), interactive CLI entrypoints (`Program`, `ChatCommand`, `ServeCommand`, `DoctorCommand`), platform interop, HTTP streaming glue, and integration-heavy hubs covered by scenario matrices (e.g. `WizardIntelligenceProvider` with **84** `WizardIntelligenceProviderTests` scenarios).
- **JSON contract POCOs:** OpenAI `/v1` DTOs excluded; `OpenAiChatCompletionMapper` remains measured.

### Fixtures & collections

| Fixture / collection | Role |
|---------------------|------|
| `GrimoireFixture` + `[Collection("Grimoire")]` | Builds SQLCipher template DB once; per-test encrypted copy. `[SkippableFact]` when `e_sqlcipher` unavailable. |
| `ArcanumWebApplicationFactory` + `[Collection("ApiHost")]` | DevHost `WebApplicationFactory`, seeded Grimoire, fake intelligence + API key. Serial collection (`DisableParallelization`). |
| `TempWorkspace` | Mutable workspace trees under `%TEMP%/arcanum-tests/{guid}`. |
| `CliTestHarness` (uses production `CliApplicationFactory.RunAsync`) | Real-parser ConsoleAppFramework CLI command smoke tests (`[Collection("GlobalConsole")]`) and pure-helper tests. |
| `[Collection("WorkspacePathPolicy")]` | Serial tests for static path-validation seams. |

### Representative areas

| Area | Tests |
|------|-------|
| `WorkspacePathPolicy` / `SanctumGuard` / `OutboundUrlGuard` | Symlink fail-closed containment; network egress; API key filter |
| `WizardIntelligenceProvider` | 84-scenario matrix (spell routing, Sanctum, streaming, resilience fallback) |
| `ArcanumInternalToolServer` | In-process MCP tool handlers |
| `SpellRepository` / Grimoire repositories | SQLCipher CRUD via `GrimoireFixture` |
| API endpoints | Lore, wards, sessions, apprentices, spells, workspaces, meta, MCP, logs, `/v1/models` |
| CLI | `ArcanumApiClient`, commands via `CliApplicationFactory`, `MarkdigSpectreRenderer` |

Full conventions: [tests.README.md](tests.README.md).

### CI

GitHub Actions workflow [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs on `pull_request`, `push` to `main`, and `workflow_dispatch`:

1. **build-test** (`ubuntu-latest`): restore/build Arcanum + Compendium (Cli + both test projects; **The Forge temporarily excluded**), `dotnet test` for Compendium, then Arcanum tests plus enforced coverage thresholds via `./scripts/coverage.sh --threshold` (does not double-run Arcanum.Tests). HTML/Cobertura upload as `arcanum-coverage-report`.
2. **aot-il** (`ubuntu-latest`): `./scripts/verify-aot-il-warnings.sh` for the hosted Linux RID (documented host-default invocation).

Related packaging workflows (manual `workflow_dispatch`, not part of PR CI):

- [`.github/workflows/build-windows-x64.yml`](../.github/workflows/build-windows-x64.yml) — unsigned **win-x64** Arcanum (Native AOT) + Compendium zips (`package-windows.ps1 -SkipForge`).
- [`.github/workflows/private-beta-release.yml`](../.github/workflows/private-beta-release.yml) — Windows + Linux private-beta archives (includes The Forge).
- [`.github/workflows/release-macos-arm64.yml`](../.github/workflows/release-macos-arm64.yml) — signed/notarized macOS arm64 (see [RELEASE-MACOS.md](RELEASE-MACOS.md)).

SQLCipher-dependent tests keep their normal `[SkippableFact]` skip behavior when the native asset is unavailable.

---

## 14. Extension guidelines for future contributors

1. **New HTTP routes:** Add in `MapArcanumEndpoints`. Return `ApiResponse<T>` via `FromResult`. Extend `ArcanumJsonContext` for new payload types. Use `.WithName(...)` for OpenAPI.
2. **New domain operations:** Return `Result` / `Result<T>`; rely on implicit conversions.
3. **New CLI verbs:** Add a public method (with XML doc `<summary>`/`<param>` comments for the description/aliases) to an existing grouped command class under `Cli/Commands`, or a new class registered via `app.Add<T>("path")` in `CliApplicationFactory.RunAsync`; register the class's constructor dependencies in `ConfigureCliServices`. Lightweight verbs should use `AddArcanumEyeOfTheWorld()` rather than `AddArcanumInfrastructure`.
4. **New intelligence providers:** Implement `IArcanumIntelligenceProvider` in `Api`. Follow the `WizardIntelligenceProvider` + `IChatClientFactory` pattern (or extend the factory for new `AiProviderKind` values).
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

`EyeOfTheWorldService` offloads filesystem I/O to the thread pool. Traversal is a manual
breadth-first walk (same prune-before-descend shape as `WorkspaceIndexingService.EnumerateCandidateFiles`):
ignored directory segments (`bin`, `obj`, `.git`, `node_modules`, `.vs`, `.nuget`, `packages`, `dist`,
`build`) and symlink-escaping subdirectories are skipped **before** descending; inaccessible directories
are ignored; Hidden/System entries are skipped; symlink cycles terminate via a canonical visited-directory
set; and `MaxEnumerationSteps` bounds total filesystem entries visited. Cooperative cancellation at three
levels (enumeration loop, TOC building, Unknown sorting).

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

- **Single user prompt per HTTP request.** Multi-turn is via `sessionId` + Grimoire history reload.
- **Single-model routing only.** No multi-model routing, fallback, or load balancing.
- **Provider-level fallback is supported when `Arcanum:Resilience:Enabled` is true** — the hub retries on the next healthy provider with the same model after a connectivity failure.
- **Models without tool support** are retried once without tools after detecting rejection.
- **Ollama context window size:** When using Ollama via its OpenAI-compatible `/v1` endpoint, Arcanum can no longer inject `num_ctx` to control the context window size (the OpenAI Chat Completions API has no such parameter). Operators must configure Ollama's context size on the Ollama side (e.g. the `OLLAMA_NUM_CTX` environment variable). `ContextWindowLimit` in provider config still feeds Arcanum's read-time compression threshold and the CLI mana bar — set it to match Ollama's effective context size for accurate compression.
- **Pre-flight token counts** use a single **`o200k_base`** Tiktoken approximation and omit tool-schema tokens; **`ContextWindowCompressionThreshold`** provides headroom. Iterative per-message trimming beyond one summary swap is not implemented.
- **Deferred:** Richer skill catalogs. All five RAG phases are implemented (§21). Apprentice orchestration is implemented (§5.7); personality templates remain deferred (§19.6). Sanctum phase 1 is implemented with persistent breach audit, kernel resource limits, and an MVP OS filesystem jail for tool children (§11.15) under **macOS-ARM beta posture**: macOS deprecated `/usr/bin/sandbox-exec` Seatbelt is active; Linux Landlock / internal `__sandbox-exec` helper remains in-tree but **inactive** (fail-closed with the public beta denial unless escape hatch); Windows has **no FS jail** (Job Objects + Sanctum path-boundary denial; health reports Degraded). Operator visibility via `arcanum doctor` Tool Child Sandbox panel and `GET /api/health` `ToolChildSandbox`. Container/VM isolation, network namespaces / network isolation for `execute_command`, durable macOS Seatbelt replacement, Linux Landlock reactivation, and per-tool path allowlists beyond workspace + `AllowedPaths` remain deferred. The FS jail is **filesystem-only** — it does not isolate network use by child binaries.
- **The Weave's vector search has a graceful-degradation fallback.** When the `vec0` (sqlite-vec) acceleration index is unavailable — no native asset referenced (Phase 1 default is managed-only; §21.2), or the SQLCipher build of `SQLitePCLRaw` blocks extension loading — `DivinationService` transparently falls back to a SIMD-accelerated (`System.Numerics.Vector<float>`; §16.7) managed brute-force cosine scan over the plain BLOB tables. Functionally equivalent results; only large-corpus performance differs (§21.2, §21.4, §21.5).

### 16.2 Persistence

- **EF Core migrations** versioned under `Data/Migrations/` with companion embedded SQL under `Data/SqlMigrations/`. The AOT host applies schema via **`GrimoireSqlSchemaMigrator`** (raw SQLite + `__EFMigrationsHistory`), not `Database.MigrateAsync`. Legacy files without `__EFMigrationsHistory` need manual baseline (see README). Because there are no production Grimoire databases in the wild, the migration history is periodically **squashed** back down to a single `InitialCreate` baseline (§16.2 "Database migrations", README "Database migrations") instead of growing forever — every squash is verified to produce a byte-for-byte identical schema to the chain it replaces before the old files are deleted.
- **Migration atomicity (P2 #49):** **`GrimoireSqlSchemaMigrator`** wraps each embedded script and its matching `__EFMigrationsHistory` insert in one `SqliteTransaction`. Scripts do not contain their own `BEGIN`/`COMMIT` or history rows — the migrator owns both. Table-rebuild migrations use **`PRAGMA defer_foreign_keys=ON`** inside that single transaction instead of nested transactions or `foreign_keys` toggles (still an available technique for a future rename/rebuild; the current `InitialCreate.sql` baseline needs it nowhere since every table is created fresh). FTS backfills use **`INSERT OR IGNORE`** so re-apply after a partial legacy run is safe, and every `CREATE TABLE`/`CREATE INDEX` in the baseline is guarded with **`IF NOT EXISTS`** so a lost `__EFMigrationsHistory` row is safely recoverable by re-running the same script. On script failure the transaction rolls back and the migration id is not recorded, so the next start retries from a consistent state.
- **SQLite pragmas** (applied on every connection via **`SqliteConnectionPragmas`**): `journal_mode=WAL`, `busy_timeout=5000`, `foreign_keys=ON`, `synchronous=NORMAL`. WAL provides automatic crash recovery; write contention is retried via **`SqliteBusyRetry`** (bounded backoff on SQLITE_BUSY/locked).
- **`ConclaveSettings.Enabled`** (config **`Arcanum:Conclave:Enabled`**, renamed from the former reserved **`Arcanum:Bureau:Enabled`** no-op) gates **The Conclave** cross-Apprentice delegation (the **`cast_sending`** tool and **`POST /api/apprentices/{id}/cast`**). Apprentice lineage (**`ParentApprenticeId`**) is persisted inside the existing **`CheckpointData`** JSON column — deliberately **no** EF migration or compiled-model regeneration, and no top-level SQL index. XML docs in `ConclaveSettings.cs` and the §3.4 table call this out.
- **`cli-session.txt`** stores one last session id — not multi-user, not cloud sync.
- **`UnseenServantWatermarks`** (§5.5.5) is deliberately **not** part of the compiled EF model — it is accessed entirely via raw SQL through the scoped **`ArcanumDbContext`**'s connection (`GetDbConnection()`), following the FTS query pattern (**`ResolveFtsSessionIdsAsync`**/**`SearchArchivesAsync`**), so adding it required no `dotnet ef dbcontext optimize` regeneration.
- **Migration safety and configuration impact:** `UnseenServantWatermarks` and `SanctumBreaches` are folded into the `InitialCreate.sql` baseline (no production databases in the wild). See docs/Arcanum.PERSISTENCE.md for the full design rationale.
- **`SanctumBreaches`** (§11.15): raw SQL via `SanctumBreachRepository` (not in the compiled EF model); FK to `Campaigns` (`ON DELETE CASCADE`); retention enforced on every insert (`SanctumConfig.MaxBreachCount`, clamp 100 – 100,000).

### 16.3 Security and identity

- No user identity, sessions, or OAuth. Loopback + API key only.
- **Grimoire KDF:** New databases derive the SQLCipher passphrase via `GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret` using **PBKDF2-HMAC-SHA256** with **600,000 iterations** and a unique 16-byte salt stored in `{grimoire.db}.kdf`. Legacy databases (created before this change) are opened with the prior HKDF path and transparently re-encrypted to PBKDF2 on unlock. The dedicated encryption secret is stored alongside the master API key; rotating the API key alone does not break the Grimoire.
- **API key rotation:** For **legacy** databases that were still encrypted with the master API key, rotating the key was destructive. For **new** databases, the Grimoire is independent of the API key, so rotating the key only invalidates API authentication. To rotate the key on a new database, run `arcanum key set` (or replace the OS credential + `security.dat` mirror) and restart; the Grimoire `.db` and `.kdf` files can stay in place. If the Grimoire encryption secret itself is lost, the database is unrecoverable — there is no automatic key recovery or backdoor. When `grimoire-key.dat` exists but Data Protection cannot decrypt it (missing `key-*.xml` under `~/.config/arcanum/keys/`), bootstrap **FailFast**s with an explicit recovery message and does **not** fall back to the API key (that path previously produced a misleading “key verification failed”). Recovery is restore the matching DP key from backup, or delete `arcanum.db` + `arcanum.db.kdf` + `grimoire-key.dat` and start fresh.
- **`arcanum key show`** / **`arcanum key set`** read/write the master key via CLI DI (`ISecretStore` → OS keychain with `security.dat` fallback); no HTTP endpoint. Shared identity: `arcanum` / `master-api-key`. Linux requires `libsecret` and a running Secret Service for the primary path.

### 16.4 Testing

- `tests/RetroDownfall.Arcanum.Tests` (API, CLI, Infrastructure, Configuration, Intelligence, MCP, Weave/RAG, Security) and `tests/RetroDownfall.Compendium.Tests` (assembly `RetroDownfall.Compendium.Ux.Tests`; Compendium settings/converters) are the two test projects, both exercising `WebApplicationFactory`-style integration coverage per the strategy in §13, gated by the coverage threshold described there.

### 16.5 CLI

- **Line-counter for swap is naive.** Multi-cell glyphs and ANSI escapes are not measured; the swap may erase extra rows or leave stray lines. The renderer never throws.
- **Status/tool diagnostics share the TTY.** Intermixed stderr/stdout lines can desynchronize the cursor count during tool-heavy turns.

### 16.6 CLI UX surface (Spectre.Console + Command Center)

- **Command Center** (bare interactive `arcanum`): Terminal.Gui fixed viewport; hard-modal arbitration (Wards > HumanPrompt); attachments when enabled (§10.2.5); `ARCANUM_NO_COMMAND_CENTER=1` escapes to usage. Specs under `docs/superpowers/specs/`.
- **Frameless `ask`/`chat`:** Spectre banner, mana bar, `@file`/`@image` staging (chat ephemeral; CC host-persists), TTY/`NO_COLOR` theme gating, atomic `cli-session.txt`.
- **doctor:** themed panels + optional `--json` `DoctorReport`.

### 16.7 Reliability & Performance Hardening

Closed pass: SIMD cosine in `EmbeddingBlobCodec`; `/v1` SSE disconnect classification; surrogate-safe RAG chunk/truncation; `SqliteBusyRetry` ownership already compliant; AOT purity gated by `verify-aot-il-warnings.sh`.


---

## 17. Glossary

| Term | Meaning |
|------|---------|
| **RDG** | ASP.NET Core Request Delegate Generator for Minimal API handlers. |
| **NDJSON** | Newline-delimited JSON for streaming `IntelligenceEvent`s. |
| **Grimoire** | Encrypted local SQLite (EF Core + SQLCipher) session store. |
| **Comm Link** | Operator alerting (`ICommLinkDispatcher`, webhook, MCP `send_commlink_alert`, `POST /api/commlink/send`). |
| **Eye of the World** | Situational directory perception (§15). |
| **Chronosync** | Temporal workspace delta vs last `WorkspaceContext` baseline. |
| **MCP** | Model Context Protocol — tool servers over stdio / HTTP / in-process (§4.2). |
| **Heraldry** | A2A Agent Card (§5.7.1). |
| **Sending** | A2A task (inbound or outward via `dispatch_sending`). |
| **Archmage Client** | Outward A2A delegate (`dispatch_sending`). |
| **The Proving Grounds / Trial / Inquisitor** | Ephemeral LLM validation runner (§20). |
| **Resilience** | Provider health + fallback retry (§10.1); default off. |
| **The Weave / Divination / Imprint** | Embedding substrate / cosine search / stored vector (§21). |
| **Lore** | Legacy operator key-value (`/api/lore`); not model-directed. |
| **The Lexicon** | Model-writable entity memory (§10.6). |
| **Saga** | Auto-extracted associative memory (§21.8); operator-delete only. |
| **Arcane Resonance** | Spell `dependencies` injection (§10.2.2). |
| **Spell Routing** | Pre-flight spell selection (`FullGrimoire` / `DirectResonance` / `FilteredDivination`). |
| **`vec0`** | Optional sqlite-vec KNN index; managed cosine fallback when unavailable (§21.2). |
| **Output Formatting Directive** | Terminal-safe Markdown subset for CLI (§10.5). |

## 18. Document maintenance

Any PR that changes **architecture, contracts, configuration, persistence, MCP surfaces, or CLI commands** must update this document in the same change set. Treat `DESIGN.md` as mandatory alongside code; do not close work with only README or code-level changes.

---

## 19. The Forge — campaign, spell metadata, and prompt registry

Grimoire-backed campaigns, optional `SPELL.json` metadata, versioned prompts — without changing inference routing or `/v1` behaviour.

### 19.1 Data models

| Entity | Storage | Notes |
|--------|---------|-------|
| **Campaign** | `Campaigns` | Path, settings JSON, `SanctumConfigJson` (§11.15) |
| **Prompt** | `Prompts` | Versioned; nullable `CampaignId` (null = global); partial unique indexes |
| **Spell sidecar** | Disk `SPELL.json` | Sibling of `SPELL.md`; legacy `SKILL.json` read fallback |

### 19.2 Architecture

Campaign/Spell/Prompt endpoints → repositories → Grimoire; `CampaignBackedWorkspaceRegistry` bridges campaigns into `/api/workspaces`. Spell bodies remain on disk.

### 19.3 Key decisions

- Operator term **Campaign** / `/api/campaigns` (no `/api/skills`).
- `SPELL.json` preferred; writes never create `SKILL.json`.
- Export wire `spellJson` (legacy `skillJson` on import).
- Prompt `/test` = assemble only (`SkipSpellRouting`); `/execute(-stream)` = live inference.
- Schema via embedded SQL migrations each host start.

### 19.4 Disk layout

`{campaign}/.arcanum/` on register; prompts export under `.arcanum/prompts/`; workspace spells at `{workspace}/spells/{name}/SPELL.md` + optional `SPELL.json`.

### 19.5 Error codes

Forge codes on `ErrorCodes` + `ArcanumErrorMapper` (§8.23). A few endpoint-local literals remain at call sites.

### 19.6 Apprentice orchestration

Persistent agents + Chronicle SSE — behaviour in §5.7. Table `Apprentices`; Chronicle in-memory only. Conclave / Simulacrum / Second Wind / Shifting Fate / Divine Intervention: §5.7. Deferred: personality templates, vector memory, distributed execution.

## 20. The Proving Grounds — Trials and Inquisitors

Ephemeral Trial runner (no Grimoire persistence). Terminology: **The Proving Grounds** / **Trial** / **Inquisitor** only.

### 20.1 Data models (ephemeral)

In-memory `Trial` / `TrialResult` / polymorphic `Inquisitor` (`regex`, `jsonSchema`, `semantic`).

### 20.2 Architecture

`POST /api/proving-grounds/trials/run` → resolve target (spell / prompt / apprenticeGoal) → run inference or plan prompt → adjudicate Inquisitors → `TrialResult`.

### 20.3 Key decisions

Semantic judge uses FastModel→DefaultModel; jsonSchema is a lightweight subset (not full draft 2020-12). Industry LLM-test jargon prohibited in identifiers.

### 20.4 Error codes

§8.23 (`ProvingGrounds.*`).

### 20.5 Deferred

Persisted Trial suites, CI scheduling, richer schema validation.

## 21. The Weave, Divination, and Saga (RAG)

**Purpose:** RAG as five independently feature-flagged, gracefully-degrading phases. **The Weave** imprints text as vectors; **Divination** is cosine semantic search; **Saga** is auto-extracted long-term associative memory (distinct from operator Lore / Lexicon).

All five phases are implemented (§21.1–§21.2 foundation; §21.6–§21.9 features). DDL for Weave/Saga/workspace embedding tables: see [Arcanum.PERSISTENCE.md](Arcanum.PERSISTENCE.md). Behavioral invariants stay here.

### 21.1 Phase 1 — embedding infrastructure (shared foundation)

**Layering:** `IWeaveService` / `WeaveService` (**Api** — depends on `IEmbeddingGeneratorFactory` / OpenAI SDK packages, mirroring `ChatClientFactory`). `IDivinationService` / `DivinationService` + `WeaveSchemaInitializer` + `SqliteVecExtensionLoader` + `WeaveIndexAvailability` (**Infrastructure**). `EmbeddingBlobCodec` (**Core**).

**`IWeaveService`:** `IsAvailable` from live `IOptionsMonitor` (`Enabled` + Provider + Model). Disabled → `Embeddings.FeatureDisabled` (no HTTP). Provider/timeout → sanitized `Embeddings.ProviderUnavailable`. `EmbedBatchAsync` sequential by `BatchSize`. `ChunkAsync` naive sliding window (always runs).

**Factory:** resolves `Embeddings:Provider`/`Model` as `OpenAICompatible` (incl. Ollama `/v1`); process-lifetime cached leases.

**`IDivinationService.SearchAsync`:** callers pass vec0 table name + PK/embedding columns. If `WeaveIndexAvailability.IsVecAvailable`, vec0 KNN; else strip `_vec` and managed cosine over BLOB companion (`EmbeddingBlobCodec`, top-K heap, row budget). Never throws — sanitized `Result` failure.

### 21.2 Vector storage — vec0 acceleration with a managed fallback (always safe)

Per feature: durable **BLOB** table (always) + optional **`vec0`** virtual table (`distance_metric=cosine`) when extension loads. Schema created by `WeaveSchemaInitializer` after migrations (not a static embedded migration — dimensions interpolated from config). Extension load failure → managed-only; never fails startup.

**Phase 1 default: managed-only** (no sqlite-vec NuGet in-tree). vec0 is performance-only.

**Dimension mismatch:** bootstrap warns when configured `Dimensions` ≠ stored `Dim`; **does not** auto-truncate — operator must `POST /api/embeddings/reset?confirm=true` (+ optional `scope`) then re-index.

### 21.3 Configuration

Full `Arcanum:Embeddings:*` keys: §3.4. Shared foundation + phase flags (`SessionSearchEnabled`, `CodebaseRetrievalEnabled`, `SagaEnabled`, `SemanticSpellRoutingEnabled`, all default off). Feature flags require `Enabled`. Validator enforces Provider/Model when enabled.

### 21.4 Graceful degradation matrix

| Condition | Behavior |
|-----------|----------|
| `Embeddings:Enabled` = `false` | Pre-RAG behavior; embed APIs return `Embeddings.FeatureDisabled` immediately |
| Provider unreachable / timeout | Sanitized `Embeddings.ProviderUnavailable`; callers skip retrieval and continue |
| sqlite-vec unavailable (default) | `Mode=managed`; Divination uses BLOB cosine (`ManagedSearchRowBudget` = 50,000). Surfaced via health/`/api/meta`/`doctor` |
| vec0 claimed but unusable | `SearchAsync` returns sanitized failure (no throw) |
| `Dimensions` changed after data | Warning only; no auto-truncate |
| `SessionSearchEnabled` = `false` | `EntryWeavingService` idles; `POST /api/sessions/divine` → **503** `Embeddings.FeatureDisabled` |
| `CodebaseRetrievalEnabled` = `false` | `WorkspaceIndexingService` idles; no prompt injection; divine/index → **503** |
| No indexed chunks / empty WorkingDirectory | Empty results / skip retrieval — inference continues with `[None]` |
| `SagaEnabled` = `false` | Extraction skips; divine/read_saga gated; **browse/delete/stats not gated** |
| `Saga:ExtractionEnabled` = `false` | Extraction drops; retrieval/API reads unaffected |
| Extraction LLM failure | Watermark **not** advanced (retry later) |
| Malformed/empty extraction JSON | Watermark **is** advanced (“nothing this tick”) |
| Saga caps reached | Skip tick; watermark **not** advanced |
| `SemanticSpellRoutingEnabled` = `false` | `FullGrimoire` → existing LLM `SemanticRouter` unchanged |
| Spell Weave cache / prompt embed failure | Fall back to `FullGrimoire` (Debug log; no regression) |

### 21.5 Known limitations

Naive chunking; no auto re-index on model/dimension change (use `/api/embeddings/reset`); managed scan budgeted at 50k rows; Phase 3 polling (no `FileSystemWatcher`); Session Divination no cursor pagination; workspace index sequential in-process; Saga extraction naive (no dedupe); pure spell-routing ties break by stable sort only.

**Reset scopes:** `POST /api/embeddings/reset?confirm=true` with optional `scope=all|entry|workspaceFile|saga` (default `all`); unknown scope → **400** `Validation.InvalidBody`.

### 21.6 Phase 2 — Session Divination

**Service:** `EntryWeavingService` (`BackgroundService`) — idle unless `Enabled && SessionSearchEnabled`; ticks on `EmbeddingQueueIntervalSeconds`. Embeds not-yet-imprinted non-empty `Entries` (SQL `LEFT JOIN`, empty filtered in SQL, batch upsert BLOB ± vec0). Idempotent; failures retry next tick.

**API/CLI:** `POST /api/sessions/divine` + `arcanum session divine` — gates/errors in §4.3 / §8.23. Filters `CampaignId` / `Status` (default `"active"`; invalid status → **400**). `HasMore`/`NextCursor` always false/null.

### 21.7 Phase 3 — Semantic Codebase Retrieval

**Service:** `WorkspaceIndexingService` — idle unless `Enabled && CodebaseRetrievalEnabled`; registers workspaces from inference `WorkingDirectory`; interval + `IndexNowAsync` for `POST .../files/index` (**202**). Change detection via `FileLastWriteTime`; caps `MaxFilesToIndex` / `MaxFileSizeChars` / extensions; symlink prune; orphan cleanup only on non-truncated walks. Tables: `workspace_file_chunks` + embeddings BLOB/vec0 (PERSISTENCE).

**Inference:** `RetrieveSemanticContextAsync` injects `### Semantic Context (Retrieved Codebase)` (DATA); failures → `null` (never fail turn).

**API:** `.../files/divine`, `.../files/index`, read-only inspector `.../index/status` + `.../chunks` (no mutate; preview capped). Errors §4.3.

### 21.8 Phase 4 — Saga (long-term associative memory)

**Contrast:** Lore/Lexicon are explicit; Saga is auto-extracted, operator-delete-only (no `scribe_saga`/`delete_saga`).

**Store:** `ISagaMemoryStore` / `SagaMemoryStore` — `saga_memories` + embeddings + `saga_extraction_watermarks` (PERSISTENCE).

**Service:** `SagaExtractionService` — event-driven bounded channel (`EnqueueExtraction`, DropOldest); headless LLM extract after successful turns when `Enabled && SagaEnabled && ExtractionEnabled`. Caps, watermark rules, and degradation: §21.4.

**Retrieval:** `RetrieveSagaMemoriesAsync` → `### Saga (Associative Memory)` DATA. Shared query embed with Phase 3 per turn.

**Surfaces:** `/api/saga*` (§4.3), MCP `read_saga` (gated), CLI `arcanum saga …`.

### 21.9 Phase 5 — Semantic spell routing

**Modes** (`SemanticSpellRoutingEnabled` / `SpellRoutingHybridMode`): Disabled → `FullGrimoire` (LLM full catalog); pure → `DirectResonance` (no LLM); hybrid → `FilteredDivination` (top-K then LLM). Failures → `FullGrimoire`.

**`SpellWeaveCache`:** singleton description imprints; re-embed catalog on change under lock. **`SemanticSpellRouter`** is the sole hub entry (`ResolveRoutedSpellAsync`). `SemanticRouter` optional `candidates` param; name resolve still searches full catalog. `SkipSpellRouting` skips scanner + router (no embed cost).

## 22. Structured output, cost tracking, and prompt caching

Three Tier-2 intelligence-pipeline capabilities ship together.

### 22.1 Structured output enforcement (`Arcanum:StructuredOutput`)

- **Validation.** `JsonSchemaHelper` (Core `Primitives`) is an AOT-safe, reflection-free JSON Schema parser/validator built on `JsonDocument`. It supports a pragmatic subset: `object` (with `properties`, `required`, `additionalProperties:false`), `string`, `number`, `integer`, `boolean`, `array` (with `items`), `enum`. Unsupported features (`anyOf`, `oneOf`, `allOf`, `$ref`, `pattern`, `format`, `minimum`/`maximum`, `minLength`/`maxLength`, `uniqueItems`, `multipleOf`) are ignored. `Parse` and `Validate` each take a `maxDepth` parameter (default 10, clamped 1–50 by `ArcanumSettingClamps.JsonSchemaMaxDepth`); schemas or payloads exceeding the depth are rejected with `StructuredOutput.SchemaInvalid` (HTTP 400).
- **Retry.** `StructuredOutputValidator.ValidateAndRetryAsync` validates the buffered candidate and, on failure, appends a corrective system message naming the errors and re-invokes the model. Before retrying it estimates the error-message token count (`InferenceTokenizerResolver` first, else `length/4`) and compares against the provider's `ContextWindowLimit`; if the retry would not fit, it skips the retry and returns the best-effort result with a `context window too small for retry` warning. Strict streaming buffers answer/reasoning and uses the same bounded **buffered replacement call** when `MaxValidationRetries > 0`; rejected streamed content is never released, and only replacement answer/reasoning survives. `PromptTurnResult.Warnings` (an `init` property defaulting to `[]`) carries warnings out to the endpoint.
- **Failure behavior.** Best-effort by default: after exhausting retries the last response is returned with an `X-Arcanum-Structured-Output-Warning` response header and a `system_fingerprint` suffixed with `:arcanum:structured-output-warning`. `Arcanum:StructuredOutput:StrictMode: true` flips this to a hard `400 StructuredOutput.ValidationFailed` on the buffered path and an `Error` event that terminates the stream on the streaming path (no `Result` or buffered answer/reasoning frame is emitted). Best-effort streaming remains post-hoc and does not retry; strict streaming with zero configured retries is also post-hoc but withholds output and fails hard.
- **Provider-side constrained decoding.** `OpenAiRequestAugmentingHandler` augments outgoing `application/json` request bodies (streaming `text/event-stream` requests pass through unchanged): it injects `strict: true` into the `json_schema` wrapper; if the provider 400s mentioning `strict`, it retries once without the flag.
- **Wiring.** `StructuredOutputValidator` is a DI singleton; `WizardIntelligenceProvider.ExecutePromptAsync` invokes it for `response_format: json_schema` requests after the tool loop terminates.

### 22.2 Cost tracking and budget enforcement (`Arcanum:Pricing`, `Arcanum:Budget`)

Authoritative composition is **TurnLimits → Reservation → Per-call context budget → Reconcile** (ADR 0002). Seams: `ITurnRunWriter`, `IBudgetReservationService`, `IModelCallExecutor`, `ITurnBudget`.

- **Pricing.** `ModelPricingEntry` (`InputPer1M`, `OutputPer1M`, `CachedPer1M`, nullable `ReasoningPer1M` USD) is keyed by model name in `Arcanum:Pricing:ModelPricing`, with `DefaultPricing` (default free) as the fallback. Reasoning tokens are a subset of completion tokens: `CostCalculator` prices ordinary completion at `OutputPer1M` and reasoning at `ReasoningPer1M` (falling back to the output rate), without billing the subset twice. Configuration rejects rates outside 0–1,000,000 USD per million tokens; runtime arithmetic also clamps rates and saturates accumulated cost. Compendium exposes reasoning price as an optional override, so unset remains distinct from an explicit zero. Each `BillableOperations` row snapshots the applicable rates and token counts (ledger keys include provider/model/operation).
- **Usage authority.** Each provider call maps `InputTokenCount`, `OutputTokenCount`, `CachedInputTokenCount`, and `ReasoningTokenCount` independently. Cached tokens remain a prompt subset; reasoning remains a completion subset. If `TotalTokenCount` is present, that provider value is authoritative even when it disagrees with the subsets (including zero). Only a missing total is derived as clamped prompt + completion. Missing usage is safe and contributes zero; multi-round/tool/retry usage is accumulated call by call without adding either subset again.
- **Durable operation ordering.** Every completed provider call with reported usage is persisted as its own `BillableOperations` row before its cost enters the in-memory accumulator and before guardrails, structured-output checks, tool-loop finalization, or other post-processing can fail. Retries and tool continuations therefore remain billable without a duplicate final aggregate row. Routing and extraction provider I/O remains request-cancelable, but once either call completes with usage, its ledger write uses `CancellationToken.None` so a cancellation at the provider/accounting boundary cannot release the reservation as unspent. A durable-write failure marks accounting failed, propagates the failure, and leaves the reservation conservatively outstanding rather than releasing or reconciling unverifiable spend. Session projections, success metrics, and final success audit records remain success-only.
- **Reservation scope.** A supplied reasoning budget is conservative output headroom, not additional output: per call both dollar reservation and context preflight reserve `max(request MaxOutputTokens or configured ReservedOutputTokens default, Reasoning.BudgetTokens)`. Dollar reservation prices the reasoning subset separately only when its rate is higher and multiplies by the bounded model-call count. Actual reconciliation always uses provider-reported counts. Batch reservations preparse valid JSONL lines and sum each line's resolved model pricing, output limit, and reasoning budget; batch lines remain single-call and no-tools. Concurrent lines use independent per-turn call budgets while sharing only the run, reservation, and thread-safe cost accumulator; provider work remains parallel while writes through the shared scoped `TurnRunWriter` are serialized by the accounting root. Embedding input is sanitized/truncated before reservation; each successful provider batch is recorded immediately so earlier spend survives a later batch failure, and the owning operation reconciles on every exit.
- **Raw-SQL accounting boundary.** `BillableOperations.ReasoningTokens` is `INTEGER NOT NULL DEFAULT 0` in the edited embedded install script `20260721010000_AddInferenceAccountingAndIdempotencyClaims.sql`. `BillableOperations` deliberately has no EF entity and is outside `ArcanumDbContext`'s compiled model; `TurnRunWriter` inserts it with parameterized raw SQL. Do not add an EF migration or regenerate the compiled model for this column. The count-only `arcanum_inference_reasoning_tokens_total` metric and `InferenceAuditRecord.ReasoningTokens` contain no reasoning body.
- **Mandatory local reinstall.** The existing install script changed under its already-applied migration id. Before running this version against a local Grimoire created by the older script, stop every Arcanum host/daemon, back up anything needed, delete the database plus its `-wal`/`-shm` sidecars, then restart Arcanum so the database is installed from the current scripts. There is intentionally no data migration. Copy-pastable Bash and PowerShell commands are in [Arcanum.README, “Mandatory local Grimoire reinstall”](Arcanum.README.md#mandatory-local-grimoire-reinstall).
- **Spend authority.** Daily spend = **`BillableOperations.CompletedAt` (UTC day) + outstanding `BudgetReservations`**. `Sessions.TotalCostUsd` / `TotalTokensUsed` remain a **projection/cache** updated via `IncrementSessionTokensAndCostAsync` for UI convenience — not admission authority.
- **Budget gate.** `BudgetMonitor.CheckAsync` prefers `IBudgetReservationService` (committed + outstanding). It falls back to summing session `TotalCostUsd` for today only when the reservation service is unavailable. At 100% of `Arcanum:Budget:DailyLimitUsd` it returns `Budget.Exceeded` (HTTP 429 on the buffered path). At `AlertThresholdPercent` (default 80%) it dispatches a Comm Link warning and records a `BudgetAlerts` row.
- **Alert deduplication.** The `BudgetAlerts` table (migration `20260706040100_AddBudgetAlerts`) has a unique index `IX_BudgetAlerts_Threshold_Date` on `(Threshold, date(AlertedAt))`; `BudgetAlertRepository.RecordAlertAsync` swallows the resulting `SQLITE_CONSTRAINT` and returns `false` for duplicate inserts. `BudgetMonitor.TryDispatchAlertAsync` **inserts the alert row before dispatching the Comm Link notification**, so the unique index is the dedup authority under concurrent turns — the previous check-then-dispatch race that sent duplicate notifications is eliminated. `HasAlertedTodayAsync` is retained as a cheap pre-check but is no longer the sole dedup gate. Decimal columns (`SpendUsd`, `DailyLimitUsd`) are bound as `decimal`, not strings.
- **Endpoint.** `GET /api/budget` returns `BudgetSummaryDto` (enabled, daily limit, today's spend, remaining, spent percent, alert threshold). When budget is disabled, `TodaySpendUsd` is reported as `0` to avoid a Grimoire read.

### 22.3 Prompt caching (provider-managed)

- **Status.** The former `Arcanum:Cache` options block is removed and rejected as obsolete (§4). Prompt caching is entirely provider-managed for OpenAI-compatible endpoints.
- **OpenAI-compatible.** Caching is automatic at the provider; Arcanum reads `UsageDetails.CachedInputTokenCount` from Microsoft.Extensions.AI.Abstractions **10.8.1** via `WizardIntelligenceProvider.MapUsageDetails` and surfaces it on `ChatCompletionUsage.CachedTokens` (optional field, `JsonIgnore(WhenWritingDefault)`).
- **Metrics.** `ArcanumMetrics.PromptCacheTokensTotal` (`arcanum_prompt_cache_tokens_total`) and `PromptCacheHitsTotal` (`arcanum_prompt_cache_hits_total`) are recorded in `RecordInferenceMetrics` when `usage.CachedTokens > 0` and the provider has not disabled caching. Labels are strictly low-cardinality `provider` + `model` — no session, request, or user identifiers — to keep Prometheus cardinality bounded by the number of configured (provider, model) pairs. `ProviderSettings.SupportsPromptCaching` (default true for `OpenAICompatible`) gates recording.

---

*End of design document.*
