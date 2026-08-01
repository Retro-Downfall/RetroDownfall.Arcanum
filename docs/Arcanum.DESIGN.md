# Arcanum — Design Document

This document is the **technical architecture, design, API, persistence, runtime, packaging, and
testing source of truth** for the Retro Downfall **Arcanum** solution. The intended audience is
**senior C# / .NET engineers** who will extend, review, or operate the system.

Arcanum's documentation contract contains exactly five files:

- [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md) — **the source of truth for all of Arcanum's architecture and design details and decisions, plus a complete API reference**; nothing contradicts it, and when any other document disagrees it is the one corrected.
- [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md) — the human-readable companion to DESIGN (conceptual prose, navigation, turn-lifecycle and dependency-direction diagrams, and pointers into DESIGN for contracts).
- [`Arcanum.README.md`](Arcanum.README.md) — the agent/operator primer for Cursor prompts (summarized architecture/design, repo layout, invariants, verification commands, brief CLI quick reference) plus the required reinstall instruction.
- [`Compendium.README.md`](Compendium.README.md#complete-configuration-reference) — the only complete `arcanum.json` key/default/bounds and credential-reference listing.
- [`Arcanum.DEBUGGING.md`](Arcanum.DEBUGGING.md) — the verified breakpoint map and task-based debugging recipes (new in this pass).

When a change under `src/`, packaging scripts, or workflows alters a fact described here, update the
owning section in the same change set. Pair operator-visible behavior with `Arcanum.README.md`,
configuration-surface changes with `Compendium.README.md`, navigation updates with `Arcanum.Design.Human.md`, and debugging guides with `Arcanum.DEBUGGING.md`.

---

## 1. Purpose and scope

**Arcanum** is a **single deployable CLI** that can:

1. Run **terminal-oriented commands** — currently `ask` (single-prompt LLM inference with optional Grimoire thread continuation), `chat` (interactive multi-turn REPL), `look` (workspace perception), `lore` (key-value CRUD), `daemon` (OS-level background service lifecycle plus **API-first** monitoring of Unseen Servant jobs via `daemon jobs`, `daemon initiative`, and Comm Link smoke tests via `daemon alert` when Kestrel is up), plus campaign/session/spell/prompt/ward/trial/apprentice/model/provider verbs that are thin clients over the same HTTP API.
2. Act as a **long-running HTTP host** exposing a Minimal API surface (the `serve` command).

The codebase is organized as a **multi-project solution**: `Core` (domain primitives, contracts, configuration), `Infrastructure` (Serilog, Data Protection, encrypted Grimoire via EF Core + SQLCipher, workspace scanning, Eye of the World perception, MCP client layer with both subprocess and in-process transports), `Api` (HTTP surface, multi-provider intelligence hub, semantic spell routing, API-key security), and `Cli` (System.CommandLine 2.0.10 entry point). All projects target **Native AOT readiness** where the toolchain allows.

Key subsystems described in later sections: hybrid hosting model (§5), HTTP JSON design (§8), intelligence pipeline with MCP tool integration (§10), local API security (§11), and Eye of the World situational awareness (§15).

**Provider support (canonical):** Arcanum currently supports OpenAI-compatible HTTP providers only. Ollama is supported through its OpenAI-compatible `/v1` endpoint when configured as `type: "OpenAICompatible"`. Arcanum-managed local inference is removed: no managed local provider kind, no local inference process lifecycle, no local weight-file downloads/cache, no local-model management UI, and no dedicated local-model HTTP or CLI control plane.

---

## 2. Architectural goals

| Goal | Rationale |
|------|-----------|
| **Strict project boundaries** | Keeps compile-time dependencies honest, enables parallel ownership, and avoids the "everything references everything" failure mode. |
| **Hybrid process model** | One CLI/host entry point reduces deployment and versioning surface; operators choose mode via CLI verbs. |
| **Native AOT readiness for the host** | Windows/Linux ship one native executable; macOS remains self-contained while the shared code keeps AOT-safe source-generation constraints. Secondary benefits are predictable startup and a smaller reflection surface (§9). |
| **Minimal API over MVC** | Fewer moving parts, explicit endpoint mapping, and alignment with ASP.NET Core's AOT-oriented request pipeline. |
| **Source-generated JSON and request delegates** | Required for credible trimming and Native AOT compatibility; avoids runtime reflection. |

### 2.1 Safe defaults and product boundaries

- `Arcanum:Edition` defaults to **Local**. Host-process tools (`execute_command` and
  `run_spell_script`) require **Development** plus `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1`; enabling
  them is reported as Degraded health. Forced Spells may still be cast as dry runs under Local, but
  their scripts are not executed.
- Local may expose in-process `search_workspace` and bound-session `apply_patch`.
  `workspace_check` is separate and is advertised only on an eligible macOS host with active
  Seatbelt plus a trusted `dotnet`, SDK/runtime, and launch chain. Linux and Windows are unavailable,
  and `AllowUnsandboxedToolChildren` never enables it.
- A2A server routes, Conclave/A2A client tools, and diagnostic MCP invocation require Development
  in addition to their feature and policy gates. The diagnostic MCP route returns 404 outside
  Development.
- The API key remains operator-equivalent for authenticated file, network, MCP, and inference
  surfaces. Local removes arbitrary command selection by default; it does not claim that no
  repository code can execute because an eligible, Ward-approved `workspace_check` runs
  repository-authored build/test/analyzer code.
- The compatibility claim is the OpenAI **Chat Completions subset**. Moderations, images, and audio
  routes are explicit `501 not_supported` stubs. OpenAI batch lines use the shared chat request
  mapper but force all tools off.
- Streaming guardrails use the code-owned buffered policy: answer and projectable reasoning are
  withheld until accepted.
- Agentic workflows run under code-owned `TurnLimits` hard caps (model calls, tool rounds, tool
  calls, tool-result tokens/bytes, elapsed time, and estimated/reserved cost per turn; see
  `TurnAccountingHandle` for the current defaults). Progress continues while evidence changes and
  stops on terminal output, deterministic no-progress, cancellation, context admission, a
  `TurnBudget` cap, or cost admission. There is no operator-configurable count ceiling.

### 2.2 Current policy constraints

| Topic | Current behavior |
|-------|------------------|
| Runtime schema installation | Hand-authored embedded SQL scripts run through the transactional migrator. |
| Workspace-less `/v1` requests | Use the current server tool-exposure policy documented in §8.8 and §10.2.1. |
| Windows child execution | Per-invocation AppContainer filesystem jail with explicit allowed-root ACLs; Job Object assignment precedes untrusted target resume. Setup failure is fail-closed. |
| Daemon concurrency | `Daemon:MaxConcurrentJobs` limits scheduled Unseen Servant jobs, not every on-demand run. |
| macOS child network | Seatbelt is a filesystem jail only; child network remains available. |
| Child environment | Secret/config and loader-hijack variables are scrubbed; there is no full per-binary environment allowlist. |
| External MCP stdio | Operator-configured external processes are trusted and do not receive Arcanum's filesystem jail. |
| A2A remote allowlist | An empty `AllowedRemoteAgents` list still permits SSRF-guarded public HTTPS targets. |
| Comm Link authenticity | Webhooks are not HMAC-signed. |
| Human-input ownership | `PromptId` is the single-user ownership capability. |
| First-run key output | Interactive `serve` prints the generated key; auto-launched serve suppresses it. |
| Native CLI diagnostics | Authenticated clients receive bounded tool-result diagnostics. |
| Provider probes | Loopback/LAN provider endpoints are allowed; link-local/metadata targets remain blocked. |
| Readiness | Provider failures yield overall Degraded/HTTP 200; Grimoire Unhealthy is the primary HTTP 503 gate. Durable-operation reconciliation that is deferred or requires operator repair yields Degraded/HTTP 200. |

### 2.3 Naming conventions

See [Arcanum.README.md §Naming metaphor](Arcanum.README.md#naming-metaphor) for the complete metaphor. DESIGN.md uses the thematic names throughout.

---

## 3. Repository and solution layout

### 3.1 `src/` per project

Projects live under `src/` rather than the repository root for shorter CI paths, room for top-level folders (`build/`, `docs/`, `test/`, `tools/`), and alignment with common monorepo conventions.

### 3.2 `Directory.Build.props`

Shared MSBuild: `TargetFramework` `net10.0`, `Nullable`/`ImplicitUsings` enable, `LangVersion` latest, `<Version>0.1.0-beta</Version>`. `Directory.Build.props` retains the **`Microsoft.Bcl.Memory` 10.0.8** baseline, while every current source/test project explicitly applies `<PackageReference Update="Microsoft.Bcl.Memory" Version="10.0.10" />`; **10.0.10 is therefore the effective solution pin** protecting the `Microsoft.ML.Tokenizers.Data.O200kBase` netstandard2.0 shim path. Per-project `.csproj` files hold what differs.


### 3.3 Package versions

Effective `Microsoft.Bcl.Memory` pin: **10.0.10** (§3.2). The .NET 10 ASP.NET Core / Extensions / EF Core package family pins at **10.0.10**; `Microsoft.Extensions.AI*` is **10.8.1**, `Microsoft.OpenApi` is **2.7.5**, and tokenizer packages are **2.0.0**. Ollama uses OpenAI-compatible `/v1` — no `OllamaSharp`.


### 3.4 Public configuration reference

The sole complete key-by-key reference, including defaults, bounds, dynamic dictionary shapes,
credential environment references, and a minimal file, is
[Compendium's complete configuration reference](Compendium.README.md#complete-configuration-reference).

The public surface uses a strict inclusion rule. A setting remains bindable only when it is a
genuine deployment choice, factual provider/model contract, credential or secret reference,
security or permission policy, integration endpoint or allowlist, explicit feature opt-in,
operator-authored schedule, host-capacity choice, pricing fact, or user preference. Convenience,
diagnostic, retry, fallback, workflow-count, and implementation-mechanic knobs are code-owned.
Removed mechanics must not return as profile enums, an `Advanced` section, generic override bags,
compatibility aliases, or duplicate feature-toggle hierarchies. Opaque collections are limited to
closed operator-authored contracts such as provider/model facts, daemon schedules, allowlists,
pricing maps, and workspace-check profiles.

`ArcanumSettings` binds beneath an exact top-level `"Arcanum"` object. The source-generated
`ConfigurationJsonContext` is the recursively walked JSON schema and uses camel-case child names.
Operator-owned dictionary keys remain dynamic, but each dictionary value is validated against its
closed generated shape.

`ConfigurationBootstrapper` and `ConfigurationStartupValidator` inspect the raw tree before normal
binding. Unknown paths fail closed and are reported together. `ConfigurationValidator.Validate`
then checks semantic relationships such as provider/model references, endpoints, path allowlists,
and portable case-insensitively unique credential-reference names. Startup aborts before serving
requests when either gate fails; `PUT /api/config` and `POST /api/config/validate` use the same
schema and semantic validation.

The Native-AOT-safe CLI configuration surface is `arcanum config path|show|get|set|validate|edit|open`.
`ConfigurationPathAccessor` resolves case-insensitive dot paths (including explicit collection
indices such as `providers.0.endpoint`) through `ConfigurationJsonContext` property metadata; it
does not walk settings POCOs with `PropertyInfo`. Values are parsed to the generated target type,
then the complete snapshot passes the same outbound and semantic validation used by the API before
any write. `show` and `get` apply `ConfigurationRedactor`; provider endpoint updates reject argv
values and accept only redirected stdin or a hidden terminal prompt. Successful `set` output shows
the effective value, or `***` for a sensitive field.

Configuration commands prefer the authenticated loopback `/api/config` contract. Connection
unavailability (and first-run absence of the local master key) enters an explicit **local bootstrap**
mode that loads with `ConfigurationBootstrapper`, validates with `ConfigurationValidator` plus
`OutboundUrlGuard`, and writes through `ConfigurationWriter`/`AtomicFile`. Diagnostics name this
mode; it is not a second configuration model. `edit` writes an owner-only redacted temporary
`ArcanumConfigurationFile`, invokes `$VISUAL`, `$EDITOR`, or the platform editor, restores existing
masked endpoints, validates, atomically applies on editor exit 0, and deletes the temporary copy.
Recognized `ARCANUM_Arcanum__...`, `ARCANUM_EDITION`, and `ARCANUM_HOST_ANY` variables are reported
as override sources without printing their values.

Unknown and obsolete paths are hard failures: they are grouped into one actionable diagnostic and
are never silently ignored or accepted through deprecated aliases. Operators correct
`arcanum.json` and restart. A configuration-only correction does not require a Grimoire reinstall;
changing embedding dimensions still requires clearing/re-indexing embeddings or recreating the
local database.

The retained graph is content-based rather than count-based. `ArcanumSettings`, nested bindable
types, `SettingDescriptors`, validation, source-generated metadata, Compendium controls, and
`Compendium.README.md` must remain in parity. Every generated bindable property uses mutable
`{ get; set; }`; `init` is prohibited because the Native AOT configuration binding generator can
silently skip it.

Provider keys, PFX passwords, and CommLink webhook URLs never enter configuration. Configuration
stores environment-variable names; explicit references replace their defaults, and secret values
are resolved only at provider use, Kestrel bind, or CommLink dispatch. Configuration, health, and
editor surfaces expose references or presence only, never secret values.

Physical, protocol, storage, context-admission, retry, fallback, paging, size, and timeout
invariants are code-owned. Public numeric policy and capacity values are clamped at their use sites.
The non-bindable `ModelCapabilityCatalog` supplies conservative tokenization and prompt-cache
behavior without adding operator keys.

Campaign Sanctum policy is separate from `arcanum.json`: each campaign stores `SanctumConfigJson`
in the Grimoire and is managed through the campaign Sanctum API. `SanctumGuard` applies its
path/network/tool policy at invocation time, while process resource limits are enforced in-process
or by the operating system as described in §11.15. Sanctum supplements the unconditional
`WorkspacePathPolicy`; it does not replace or weaken it.

#### 3.4.1 Degraded-mode fallback matrix

Single-host failure behavior:

| Condition | Behavior |
|-----------|----------|
| Provider unreachable / stalled | Provider/transport failures surface as **`Hub.Error`** (buffered) or an **`Error`** frame (streaming). Arcanum imposes no hidden turn duration cap; caller or host cancellation ends the autonomous workflow. |
| MCP server failed bootstrap (AlwaysOn) | Prominent startup warning; server excluded from toolset; surfaced in **`GET /api/health`** MCP component counts. |
| `workspace_check` disabled or loses its trusted macOS jail/executable/SDK/launch-chain eligibility | Omitted from `tools/list`; direct stale calls return structured `status:"unavailable"` / `code:"capability_unavailable"`. `GET /api/health` component **`WorkspaceCheck`** reports `available=false` plus the reason: explicitly disabled is Healthy/non-degraded, while requested-but-unavailable platform/trust capability is Degraded. Linux and Windows are unavailable in this release. |
| Grimoire SQLITE_BUSY / locked | Bounded exponential backoff on writes (`SqliteBusyRetry`); the delay budget counts only code-scheduled backoff, never action time, profiler suspension, scheduler starvation, or retry-observer work. Persistent contention is then surfaced as API/CLI failure. |
| Disk full / partial `security.dat` write | Atomic temp+rename on `security.dat`; corrupt store fails with recovery guidance (§16.3) instead of silent key regen when a Grimoire DB exists. |
| Data Protection keyring corrupt | See §16.3 rotate-or-restore steps; **`arcanum key show`** reads the local store only (no HTTP). |
| Configuration host/API unavailable | `arcanum config` reports and uses local bootstrap mode with the canonical loader, validator, outbound guard, and atomic writer; validation failure leaves the prior file unchanged. |
| File-encryption key missing/corrupt or blob authentication fails | Startup/read fails closed; no replacement key is generated while ciphertext exists. `FileEncryption` health and `arcanum doctor` identify key/legacy/corrupt state; restore the OS credential or DP mirror + key ring (§5.4.6, §16.3). |

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

**In-process MCP tools (canonical list):** `read_file_chunk`, `replace_text_block`, `write_file`, `list_directory`, `search_workspace`, `apply_patch`, `workspace_check` (advertised only while eligible on macOS), `execute_command` (no shell; `ArgumentList` only), `ask_human` (streaming attended only), `scribe_lexicon`/`delete_lexicon` (`Arcanum:Features:Lexicon`; delete is Forbidden Art), `search_archives`, `send_commlink_alert`, `petition_dungeon_master`, `adjust_initiative`, `cast_sending` / `dispatch_sending` (Conclave/A2A feature gates), `read_saga` (`Arcanum:Features:Saga`), and `attach_session_file` / `refresh_session_file` (`Arcanum:Features:Attachments`; post-tool content injection). `search_workspace` is the exact bounded text-search surface and does not query The Weave. `apply_patch` is bound to a persisted assistant turn. All filesystem tools use `WorkspacePathPolicy`; campaign Sanctum is an additional conditional policy, not the primary containment boundary.

**Other DI surfaces:** `AddArcanumInfrastructure`, `AddArcanumDaemonServices`, `AddArcanumEyeOfTheWorld`, `AddArcanumThemeDetection`, Grimoire/`Chronosync`/`CampaignLoggerQueue`/`Loremaster`, `InMemoryEventBus`, Comm Link multiplex/webhook.

**RAG ownership:** Weave/Divination schema + managed/vec0 search in Infrastructure (`DivinationService`, `WeaveSchemaInitializer`, `SqliteVecExtensionLoader`); `EmbeddingBlobCodec` in **Core**; `IWeaveService` implemented in **Api** (§21.1). Background: `EntryWeavingService`, `WorkspaceIndexingService`, `SagaExtractionService`/`SagaMemoryStore`. Semantic spell routing cache: `SpellWeaveCache`.

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
| GET | `/api/config` | Read live `ArcanumSettings`; provider endpoints remain redacted, while secret environment-variable references are returned without resolving their values (`ApiResponse<ArcanumSettings>`; §8.12). |
| PUT | `/api/config` | Validate and write a full settings snapshot to `arcanum.json` (`ApiResponse<bool>`; §8.12). |
| POST | `/api/config/validate` | Validate settings without writing (`ApiResponse<bool>`; §8.12). |
| GET | `/api/models` | Flatten configured models across all providers (`ApiResponse<ModelInfoDto[]>`; endpoint redacted as `"***"`; read-only, no connectivity checks; §8.12). |
| GET | `/api/providers` | List configured providers with `apiKey`/`endpoint` redacted (`ApiResponse<ProviderInfoDto[]>`; read-only; §8.12). |
| GET | `/api/perception/look` | Eye of the World snapshot (optional `directory` query; requires `Arcanum:Security:PerceptionWorkspaceRoots`; **403** when unset). |
| POST | `/api/intelligence/ping` | Buffered inference. |
| POST | `/api/intelligence/ping-stream` | NDJSON streaming inference (same `PingRequest` extensions as buffered ping). |
| POST | `/api/intelligence/human-response` | Submit human-in-the-loop answer. |
| POST | `/api/intelligence/arsenal` | Spell names, metadata-only `SpellSummary[]`, native tools, and MCP server status. |
| POST | `/api/intelligence/mana` | Read-only diagnostic Mana (token) counter (`ApiResponse<ManaCountResult>`; body `ManaCountRequest` { `messages`, `prompt`, `model`, `tools` }). |
| POST | `/api/web/search` | First-class bounded web search (`WebSearchWorkflowRequest` → `ApiResponse<WebSearchWorkflowResult>`; citations and provider usage; §11.27). |
| POST | `/api/web/browse` | First-class bounded static page read (`WebBrowseWorkflowRequest` → `ApiResponse<WebBrowseWorkflowResult>`; JavaScript mode degrades explicitly when no renderer is configured; §11.27). |
| POST | `/api/web/research` | Server-owned bounded multi-hop research as NDJSON `WebResearchStreamFrame` lines (limits/progress/result/error; §11.27). |
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
| GET | `/api/sessions/{id}/attachments` | Revalidate tracked sources asynchronously, then list **bound** session attachments (`ApiResponse<SessionAttachmentDto[]>`; includes `indexingStatus`, the snapshot `RelativePath` for Reveal, and sanitized source provenance/refreshability; never an absolute source path; §10.2.5). |
| POST | `/api/sessions/{id}/attachments/{attachmentId}/refresh` | Operator-triggered secure refresh through the same source-validation/persistence core as `refresh_session_file`; returns `ApiResponse<AttachmentRefreshEvent>` only after the backend has reused or persisted the confirmed current version. |
| GET | `/api/sessions/{id}/context-pins` | List durable, structured session context pins. |
| POST | `/api/sessions/{id}/context-pins` | Create or update a context pin by `(session, kind, stable target)`; accepts file, directory snapshot, symbol/range, session entry, attachment, URL, and diagnostic kinds. |
| DELETE | `/api/sessions/{id}/context-pins/{pinId}` | Remove a durable context pin without changing `Entries.IsPinned`. |
| POST | `/api/sessions/{id}/fork` | Create an independent branch of a session, optionally truncated at `upToEntryId` (**201**; §11.16.1). |
| POST | `/api/embeddings/reset` | Truncate embedding tables for RAG dimension-change recovery (requires `?confirm=true`; optional `?scope=all\|entry\|workspaceFile\|saga\|sessionAttachment`, default `all`). |
| DELETE | `/api/sessions/{id}/entries/{entryId}` | Delete a single entry from a session (**204**). |
| POST | `/api/sessions/{id}/entries/{entryId}/pin` | Pin an entry so it is always included in inference context, even when compression would otherwise drop it. |
| DELETE | `/api/sessions/{id}/entries/{entryId}/pin` | Unpin a previously pinned entry. |
| POST | `/api/sessions/{id}/compact` | Manually compress session context by deleting the oldest non-pinned entries until the token count is below the effective threshold. |
| POST | `/api/sessions/divine` | Session Divination — semantic search over Grimoire entries embedded by `EntryWeavingService` (`ApiResponse<SemanticSearchResult>`; body. |
| GET | `/api/lore` | List lore entries (`ApiResponse<ListPageResult<LoreDto>>`; paginated with optional `?limit=` and `?offset=`; the default page size is code-owned). |
| GET | `/api/lore/{key}` | Get lore by key. |
| POST | `/api/lore` | Upsert lore entry. |
| DELETE | `/api/lore/{key}` | Delete lore entry. |
| GET | `/api/saga` | Paginated listing of Saga memories (`ApiResponse<SagaMemoryDto[]>`; optional `?q=` substring, `?sessionId=`, `?limit=` [1–10,000. |
| POST | `/api/saga/divine` | Semantic search over Saga memories (`ApiResponse<SagaSearchResult>`; body `SagaSearchRequest` { `query`, `limit` }; **503**. |
| DELETE | `/api/saga/{id}` | Delete a single Saga memory (**204**; **404** `Saga.NotFound`; §21.9). |
| DELETE | `/api/saga` | Delete every Saga memory, embedding, and extraction watermark (**204**; requires `?confirm=true`, else **400** `Saga.NotEmpty`; §21.9). |
| GET | `/api/saga/stats` | Aggregate Saga memory summary (`ApiResponse<SagaStats>`: total count, session count, oldest/newest `CreatedAt`; §21.9). |
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
| PUT | `/api/campaigns/{id}/codex` | Create or overwrite campaign `CODEX.md` (`ApiResponse<CodexContentDto>`; body `{ "content": "..." }`; **400** when over the code-owned CODEX size limit; §19). |
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
| — | `/api/conclave/a2a/*` | A2A (Agent-to-Agent) JSON-RPC surface (`MapA2A`), mapped only when `Arcanum:Features:Conclave && Arcanum:Features:A2AServer`. |
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
| POST | `/api/workspaces/{id}/files/divine` | Semantic search over a workspace's indexed files (`ApiResponse<WorkspaceSearchResult[]>`; body `WorkspaceSemanticSearchRequest` {. |
| POST | `/api/workspaces/{id}/files/index` | Kick off an immediate background re-index of the workspace via `WorkspaceIndexingService.IndexNowAsync` (`ApiResponse<bool>`; **202**. |
| GET | `/api/workspaces/{id}/files/index/status` | Read-only indexing status for a workspace (`ApiResponse<WorkspaceIndexStatusDto>`): vector mode/diagnostic, `IndexingEnabled`, durable file/chunk counts, and volatile `Watching`/`Degraded`/`Overflowed`/`Reconciling` plus last-event/last-success timestamps. |
| GET | `/api/workspaces/{id}/files/chunks` | Bounded, paginated chunk previews for a workspace (`ApiResponse<WorkspaceFileChunkPage>`; optional `relativePath` filter, clamped) including character offsets and one-based source line ranges. |
| GET | `/api/unseen-servant/jobs` | List Unseen Servant jobs with base and effective polling intervals (**canonical** Unseen Servant pacer API; §8.15). |
| POST | `/api/unseen-servant/jobs/{name}/initiative` | Set adaptive initiative (dynamic interval) for a job by name; returns updated status. |
| GET | `/api/daemons` | List registered daemon jobs (`ApiResponse<DaemonJobInfo[]>`; **plural** `daemons` — registry; §8.15). |
| GET | `/api/daemons/{id}` | Daemon job metadata (`ApiResponse<DaemonJobInfo>`; **404** when missing). |
| POST | `/api/daemons/{id}/run` | Run a daemon job on demand; returns `ApiResponse<DaemonExecutionSummary>` with execution id (**400** when not found, disabled, or already running on-demand). |
| GET | `/api/daemons/{id}/history` | Execution history for a daemon (`ApiResponse<DaemonExecutionSummary[]>`). |
| GET | `/api/executions/{id}` | Execution detail (`ApiResponse<DaemonExecutionDetail>`; **404** when missing). |
| POST | `/api/executions/{id}/cancel` | Cancel a running execution; returns updated `ApiResponse<DaemonExecutionSummary>` (**400** `Daemon.NotRunning` when not running). |
| GET | `/api/logs` | Paginated in-memory log query (`ApiResponse<LogQueryResult>`; optional `minLevel`, `category`, `from`, `to`, `search`, `limit`, `beforeSequence`; §8.16). |
| GET | `/api/audit` | Persisted inference audit log query (`ApiResponse<InferenceAuditRecord[]>`; optional `from`, `to`, `model`, `sessionId`, `limit`; §8.26). |
| GET | `/api/guardrails/audit` | Persisted guardrails violation audit log query (`ApiResponse<GuardrailAuditRecord[]>`; optional `from`, `to`, `stage`, `violationType`, `sessionId`, `limit`; §8.27). |
| GET | `/api/operations` | List durable operations with optional `kind`, `state`, `limit`, and `offset` filters. Returns safe summaries only; encrypted checkpoint payloads and references are never serialized (§10.8). |
| GET | `/api/operations/{id}` | Show one durable operation's lifecycle, links, lease, attempt, checkpoint version/presence, safe summary, and terminal error code. |
| POST | `/api/operations/{id}/cancel` | CAS-protected transition to `Cancelling`; **404** unknown, **409** stale/terminal. |
| POST | `/api/operations/{id}/retry` | CAS-protected reset of `Failed`, `Abandoned`, or `ReconciliationRequired` to `Pending`; checkpoint remains available to the recovery policy. |
| POST | `/api/operations/reconcile` | Run a bounded authenticated recovery pass and return `LongRunningOperationReconciliationSummary`. |
| GET | `/api/events/daemon` | SSE stream of `DaemonEvent` frames (daemon job lifecycle for scheduled and on-demand runs); **not** wrapped in `ApiResponse<T>`. |
| GET | `/api/events/mcp` | SSE stream of `McpServerEvent` frames (MCP server lifecycle); **not** wrapped in `ApiResponse<T>`. |
| GET | `/api/events/logs` | SSE stream of `LogEntry` frames (live log tail from ring buffer); **not** wrapped in `ApiResponse<T>`. |
| POST | `/api/commlink/send` | Dispatch a **Comm Link** alert (`CommLinkMessageRequestDto`); **200** + `ApiResponse<bool>`; **400** validation; **502** + envelope on webhook HTTP failure. |
| POST | `/api/tools/invoke` | Diagnostic built-in tool invocation (`ApiResponse<ToolInvokeResponse>`; §11.27). |
| POST | `/api/providers/test` | Read-only provider connectivity probe (`ApiResponse<ProviderTestResult>`; body `endpoint`, optional `apiKey`, `type` = `OpenAICompatible`; does not write `arcanum.json`; §19). |
| POST | `/api/proving-grounds/trials/run` | Run an ephemeral **Trial** through **The Proving Grounds** (`Trial` body → `ApiResponse<TrialResult>`; §20). |
| POST | `/v1/chat/completions` | OpenAI-compatible chat (JSON or SSE); **not** wrapped in `ApiResponse<T>`. |
| POST | `/v1/embeddings` | OpenAI-compatible embeddings; **not** wrapped in `ApiResponse<T>`. |
| POST | `/v1/moderations` | Always **501** `not_supported`; no configuration setting enables it. |
| POST | `/v1/images/{generations,edits,variations}` | Always **501** `not_supported`. |
| POST | `/v1/audio/{transcriptions,translations,speech}` | Always **501** `not_supported`. |
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
- **`GET /api/config`** / **`PUT /api/config`** / **`POST /api/config/validate`** use **`ArcanumSettings`** as the payload type (§8.12). Read masks provider endpoints and returns only environment-variable references for provider credentials, HTTPS certificate passwords, and CommLink—not their secret values. Raw bodies fail closed on every unknown/obsolete path before source-generated deserialization; writes merge only endpoint masks.
- **`DELETE /api/sessions/{id}`** returns **204** with no body on success (soft-delete archive; idempotent — §11.16); **`POST /api/sessions/{id}/rest`** returns **202** with `ApiResponse<bool>` when the job is queued, or **503** with `Session.RestQueueFull` when enqueue is rejected.
- **`POST /api/commlink/send`** returns **502** with `ApiResponse<bool>` when the outbound webhook HTTP call fails (non-success status or transport error).

**Daemon route families:** **`/api/unseen-servant/*`** manages Unseen Servant job **configuration** and runtime scheduling intervals (`GET /api/unseen-servant/jobs`, `POST /api/unseen-servant/jobs/{name}/initiative`). **`/api/daemons/*`** and **`/api/executions/*`** are the daemon job **registry** and **execution history** API for all registered `IDaemonJob` types (§8.15).

The `/api` and `/v1` groups are protected by `ApiKeyEndpointFilter` (section 11), including the OpenAPI document and Scalar reference UI on `/api` (`MapOpenApi` / `MapScalarApiReference` are registered on the same keyed group, so browsers need a valid API key like any other `/api` caller).

**Composition roots:** `ApiBootstrapper`, `WizardIntelligenceProvider`, `ChatClientFactory`, filters/endpoints under `MapArcanumEndpoints`; Weave/`SemanticSpellRouter` live here (§10, §21).


**MSBuild:** `IsAotCompatible`, `EnableRequestDelegateGenerator` (essential for Minimal API endpoints in a referenced class library), `EnableConfigurationBindingGenerator`.

### 4.4 `RetroDownfall.Arcanum.Cli` (console executable)

**Role:** Single entry assembly — process argv, dispatch commands, and when asked, construct the ASP.NET Core pipeline and run Kestrel. Carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />` so the same binary can self-host Kestrel for `serve`.

**Commands:**

**Resource selection contract:** Direct commands that act on sessions, campaigns, workspaces,
prompts, spells, Apprentices, models, providers, or MCP servers resolve through the client-side
`IResourceSelector<T>` framework. Resolution precedence is exact ID, exact case-insensitive name,
then a unique case-insensitive name prefix. An omitted selector opens a searchable Spectre picker
only when both stdin and stdout are attached to a terminal and the invocation is not `--json`;
redirected/non-interactive invocations instead fail with an actionable bounded candidate summary.
An ambiguous exact name or prefix always fails—recent selections never break ties. Escape cancels a
picker and returns success without issuing the read or mutation request.

`CliResourceCatalog` keeps the policy thin over the existing authenticated list APIs and follows
their offset or `beforeUpdatedAt` cursors in bounded pages of 100. Picker rows use resource-specific
safe columns: session title/campaign/updated time, campaign or workspace name/path, prompt
name/version, spell name/source, Apprentice name/status, model/provider, provider name/type, and MCP
name/scope-token/state/transport/tool count. Workspace-local MCP scope tokens are short hashes, so
duplicate names remain distinguishable without revealing paths. Provider endpoints and credential
references plus MCP URL, command, arguments, and working directory are excluded. Successful selections update the owner-only
`recent-resources.txt` ordering hint under the Grimoire directory; it stores only resource kind, ID,
and timestamp and has no resolution authority. Fuzzy matching remains a terminal search operation;
server APIs retain exact semantics.

**Active CLI context contract:** `arcanum use campaign <id-or-name>` (also
`arcanum campaign use <id-or-name>`), `use workspace <id-or-path>`, `use model <name>`, and
`use session <id>` select local defaults without mutating Campaign, Workspace, Model, or Session
server records. `arcanum use clear [campaign|workspace|model|session]`
clears one scope (or every scope when omitted). `arcanum context current` prints the effective
campaign, workspace, model, and session plus each value's source; `--json` returns the same typed
payload. All direct commands accept recursive `--no-context`, which bypasses saved values for that
invocation but still permits independent current-directory Campaign and Workspace detection.

Effective-value precedence is fixed: **explicit command option → active CLI context →
current-directory resource detection → server default**. The deepest containing Campaign supplies
Campaign context, while the deepest containing registered Workspace independently supplies
Workspace context. `ask` and `chat` accept explicit
`--workspace` and `--session` in addition to `--campaign` and `--model`, resolve every explicit or
saved server resource through the authenticated API, and use the effective workspace for Eye of
the World, Chronosync, MCP workspace scope, file staging, and `PingRequest.WorkingDirectory`.
Interactive inference prints a context line before work starts. Other option-bearing commands use
the matching saved default (workspace for Spell/version and Trial commands; Campaign for
Prompt/Apprentice/session-divination commands; session for Saga/prompt execution/Spell cast; model
for Trials) only when the explicit option is absent.

The versioned local state document is `{ArcanumPaths.GrimoireDirectory}/cli-context.json` (schema
version `1`). It contains only resource IDs, safe display names/paths, and a model name—never API
keys, endpoints, credential references, prompts, or transcript content. Writes use a sibling temp
file, durable flush, atomic replace, and owner-only permissions. Confirmed stale Campaign,
Workspace, Model, and Session references are reported before being cleared; transient API failures
do not clear state. An inherited workspace outside the invocation's current directory is warned
before operation. A Session whose Campaign differs from the effective Campaign fails inference
context resolution rather than silently crossing Campaigns. Arcanum's shipping CLI client connects
to the local loopback host, so current-directory resource detection may compare local paths. Every
workspace path sent to or returned by the API is nevertheless labelled as a **server-host path**;
a future remote-host client must require an explicit server path and cannot infer server filesystem
identity from a client path. `cli-session.txt` remains a temporary last-session mirror for older CLI flows,
while `cli-context.json.sessionId` is the active-context authority.

| Command | Purpose |
|---------|---------|
| `serve` | Builds `WebApplication` with slim defaults, configures Kestrel, registers API services, runs the host (§5.3). When `ARCANUM_AUTO_LAUNCHED=1`, suppresses the Listening line and the raw first-run key print (hint: `arcanum key show`); redirects Console.Out/Error to an owner-only bootstrap log under `{ArcanumPaths.GrimoireDirectory}/logs/auto-serve-bootstrap.log`. |
| `ask` | Single-prompt streaming inference via NDJSON. Resolves effective CLI context, prints it interactively, runs Eye of the World and Chronosync in the effective workspace, and sends `PingRequest` with optional Campaign/Model/Session. Explicit flags: `--campaign`, `--workspace`, `--model`, and `--session`; `--new` conflicts with `--session`. Interactive sessions auto-start the host before context validation and streaming. |
| `chat` | Interactive multi-turn REPL with Figlet banner, effective-context header, Mana bar, slash commands (`/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model [name]`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/memory`, `/summary`, `/mana`, `/attach`), per-turn cancellation, inline `@` file staging, and swap-at-end Markdig rendering. `--campaign`, `--workspace`, `--model`, and `--session` override active context; `--new` conflicts with `--session`. Wide interactive color terminals use the live dashboard; narrow, redirected, and `NO_COLOR` paths keep simple streaming. |
| *(bare)* | **Command Center v2** (Terminal.Gui 2.4.17): bare interactive `arcanum` with `ARCANUM_NO_COMMAND_CENTER` unset. Fixed viewport — header / left sessions (UpdatedAt desc; overlay picker when narrow) / transcript (follow-tail) / composer / footer. Chat + allowlisted slash via `ShellCommandDispatcher` / `CommandCenterChatRunner` / `SessionWorkspaceService` (no Spectre, no CAF recursion, no `ChatCommand`). Resume loads ≤200 recent entries; `CliSessionManager` last-session restore with stale → New Session. Branching uses the server fork contract: `/fork`, selected-entry `/fork at`, `/fork alternative`, `/fork confirm`, and `/branch parent|child`; `⑂` marks branches without changing session ordering. Successful forks load branch detail, transcript, and attachments before switching; failures leave the source active. Attachments: `/attach`, `/attachments` (+ `add`/`reveal`/`refresh`), `@path`; `[Snapshot]`, `[Live]`, and `[Stale]` rows show the loaded hash plus the last backend-observed disk hash/time. A debounced recursive `FileSystemWatcher` is only an invalidation hint: Command Center fetches revalidated backend DTOs before changing a badge, and `/attachments refresh <name>` reports Live only after the shared secure refresh core confirms the version. Host persists when `Arcanum:Features:Attachments` is enabled (§10.2.5 / §16.6). Structured persistent context is discoverable through `/help` and managed with `/context`, `/context pin <kind> <target>`, and `/context unpin <id>` (§10.2.6). Coalesced streaming (~50ms). Size gate **inside** the host after TG Init (≥80×12 floor); too small or init failure → exit **1**. Bare non-interactive / `ARCANUM_NO_COMMAND_CENTER=1` → usage/help exit **0**. `NO_COLOR` / `ARCANUM_NO_COLOR` select monochrome theme only — they do **not** block the TUI. Auto-serve via `IArcanumServeLauncher`. Types under `Cli/CommandCenter/`. |
| `look` | Prints `PatternSnapshot` from Eye of the World (no HTTP dependency). |
| `doctor` | Environment diagnostics across panels — **System** (version/OS/runtime/TTY/color), **Paths**, **Configuration** (`arcanum.json` parse), **MCP** (`mcp.json`), and a **Tokenizer** smoke test — plus an **API Health** probe (`GET /api/health`) with a code-owned 2-second timeout. A hard-check failure exits **1**; an unreachable or timed-out API is a **non-fatal warning** (still exits 0). Pass `--fix-permissions` to apply owner-only permissions to the Grimoire database, `arcanum.json`, and secret store. No infrastructure services required beyond `IHttpClientFactory`, `ISecretStore`, and `IOptions<ArcanumSettings>`. |
| `mcp list\|show\|start\|stop\|restart\|reload\|trust\|tools\|invoke` | Authenticated MCP administration over `/api/mcp*`. Safe status projection includes scope, transport, derived trust, lifecycle, tool count, and last error while omitting command/URL/arguments/environment. Server/tool ambiguity uses the picker only on an interactive TTY; redirected/JSON invocations reject deterministically. `--workspace` is the explicit workspace-scope input. |
| `tool list\|show\|invoke` | Workspace-aware built-in diagnostic discovery via `/api/intelligence/arsenal` and execution via `/api/tools/invoke`. The arsenal projects `IBuiltInToolRegistry.GetToolNames()` so enabled native web tools are included. |
| `search <query>` | First-class search through `POST /api/web/search`. `--count`, `--freshness day\|week\|month\|year`, repeatable `--include-domain` / `--exclude-domain`, recursive `--json`, `--save`, and `--attach-to-session` preserve typed citations and provider usage without a chat prompt. |
| `browse <url>` | First-class page read through `POST /api/web/browse`. `--render static\|javascript` makes rendering intent explicit; the current static provider preserves SSRF/redirect/content bounds, while unavailable JavaScript rendering returns an actionable retry with `--render static`. Supports `--json`, `--save`, and `--attach-to-session`. |
| `research <question>` | Server-owned multi-hop research through NDJSON `POST /api/web/research`. Bounds are visible on stderr before work (`--max-sources`, `--max-hops`, `--token-budget`, optional `--cost-budget`); progress stays on stderr and final terminal/Markdown/JSON content stays on stdout. `--model`, `--continue-session`, `--attach-to-session`, `--save`, and `--format terminal\|markdown\|json` are supported. |
| `key show` | Prints the stored master API key from the OS credential store (`ISecretStore` → keychain with `security.dat` fallback) to **stderr**. CLI-only, **no HTTP** (§16.3). |
| `key set` | Stores a master API key into the OS credential store (mirrors to `security.dat`). Argument, stdin, or interactive secret prompt (§16.3). |
| `use campaign\|workspace\|model\|session <value>` | Validate and select an owner-local active CLI default. Selection never updates a server resource row. |
| `use clear [scope]` | Clear all saved CLI context or only `campaign`, `workspace`, `model`, or `session`. |
| `context current` | Explain every effective value and its source; reports and then clears confirmed stale references. |
| `config path\|show\|get <key>\|set <key> [value]\|validate\|edit\|open` | Safe configuration inspection and mutation. Uses `/api/config` when available, otherwise explicitly reports canonical local bootstrap mode. Reads preserve API redaction; provider endpoints require stdin/hidden input. `edit` validates an owner-only temporary copy before atomic replacement; `open` launches Compendium or prints the exact path and `arcanum config edit` fallback. |
| `lore list\|get\|set\|delete` | CRUD on `MageSettings` via `/api/lore`. |
| `daemon install\|uninstall\|status` | OS-specific background service lifecycle (Windows `sc`, macOS `launchd`, Linux `systemctl --user`). |
| `daemon jobs` | Lists Unseen Servant jobs (name, spell, base vs effective interval, enabled) via **`GET /api/unseen-servant/jobs`**; requires **`arcanum serve`** (or equivalent host) and stored API key. |
| `daemon initiative <JOB_NAME> <MINUTES>` | Sets adaptive initiative for a job via **`POST /api/unseen-servant/jobs/{name}/initiative`** with **`AdjustInitiativeRequestDto`**; prints updated **effective** interval (server-clamped). Same connectivity requirements as `daemon jobs`. |
| `daemon alert <MESSAGE>` | Sends a **Comm Link** smoke alert via **`POST /api/commlink/send`** with **`CommLinkMessageRequestDto`** (options: `--title`, `--severity`, `--source`). Same connectivity requirements as `daemon jobs`. |
| `campaign list\|get\|create\|update\|delete\|export\|import\|spells\|prompts\|sessions\|use` | The Forge campaign registry via **`/api/campaigns`**. `campaign use` aliases shared active-context selection. Resource-taking verbs accept an optional campaign ID/name/prefix and use the shared picker when omitted interactively. `list` accepts `--type`; `create` requires `--name`/`--path` (`--type` defaults to `campaign`); export/import round-trip `CampaignExportDto`; scoped lists preserve spell shadowing. |
| `campaign codex get\|put\|delete` | Manage the campaign's `CODEX.md` via **`/api/campaigns/{id}/codex`**. `put` reads content from `--file` (or inline `@file` convention, see below). |
| `spell list\|get\|create\|update\|delete\|search\|validate\|execute\|versions\|export\|import\|cast\|clone` | The Forge spell CRUD + execution via **`/api/spells`**. `create`/`update` require `--workspace`; `create` accepts `--body`, repeatable `--tag`/`--declared-tool`/`--dependency` (writes `SPELL.json`); `execute` sends `SpellExecuteRequest` (`--version` takes a **string label**, not an integer) and prints the response text (plus a themed tool-call summary on stderr when `ToolCalls` is non-empty); `search` filters by `--query`/`--tag`/`--tool`/`--source`; `cast <NAME>` is a **dry-run** preview (`POST /api/spells/{name}/cast`) rendering the assembled system prompt, resonant dependencies, attuned tools, and spell scripts without consuming inference tokens; `clone <NAME> --new-name <N>` clones a spell (built-in or workspace) into the workspace (`POST /api/spells/{name}/clone`). |
| `spell version create\|update\|activate` | Nested branch for named spell **version files** (`SPELL.v{label}.md`) via **`/api/spells/{name}/versions`**. `create`/`update <NAME> --version <LABEL> --body <TEXT_OR_FILE>` write a version file (label: alphanumeric + dots); `activate <NAME> --version <LABEL>` swaps the version into `SPELL.md`, preserving the prior active content as `SPELL.v{previousLabel}.md` (printed as a themed note). |
| `prompt list\|get\|versions\|create\|update\|delete\|render\|test\|execute\|export\|import\|clone` | The Forge prompt CRUD + rendering via **`/api/prompts`**. Resource-taking verbs accept optional ID/name/prefix selection. `render`/`execute` accept repeatable `--param key=value`; `test` assembles without LLM cost; clone requires the new name/version. |
| `ward list\|get\|resolve` | Ward approval gates via **`/api/wards`**. `resolve <ID>` requires exactly one of `--allow`/`--deny` (mutually exclusive) plus optional `--reason`; 404 `Ward.NotFound` and 409 `Ward.AlreadyResolved` are rendered as themed messages. |
| `trial run` | The Proving Grounds via **`POST /api/proving-grounds/trials/run`**. `--target` (`spell`\|`prompt`\|`apprenticeGoal`) + `--target-value`; repeatable `--inquisitor` (inline JSON or `@file`) and `--var key=value`. Renders Passed/Failed, a verdicts table, and the output (truncated to 500 chars); exits `1` when the Trial fails. |
| `apprentice list\|get\|create\|delete\|start\|pause\|resume\|cancel\|reweave\|intervene\|cast\|chronicle` | The Forge Apprentice orchestration via **`/api/apprentices`**. Resource-taking verbs accept optional ID/name/prefix selection; cancellation occurs before mutation. `create` accepts `--goal`; `reweave` reads `PlanStep[]`; `chronicle` is SSE. |
| `model list\|get`, `provider list\|get` | List or safely select configured models/providers. `get` omits endpoints and credential details; model identity is `provider/model` when names collide. |
| `session list\|show\|get\|chat\|entries\|watch\|fork\|rename\|archive\|export\|rest\|attachments\|delete-entry\|pin-entry\|unpin-entry\|compact\|divine` | Complete session lifecycle and continuation over **`/api/sessions`**. Session arguments accept a GUID, exact title, unique prefix, or an interactive picker; `get` remains an alias for `show`. `list` filters by campaign/status/search/model/from/to; `show` combines metadata with attachment count and displays token/cost telemetry plus fork parent; `watch` consumes the session SSE stream. Fork/archive/export preserve archived-session semantics and use server APIs. Entry delete requires confirmation; delete/pin/unpin/compact retain the server's memory-management gate. Read commands support recursive `--json` (`watch` emits one JSON object per line). |
| `workspace list\|current\|register\|show\|tree\|info\|read\|search\|index\|index-status\|chunks\|unregister` | Operate through authenticated **`/api/workspaces`** routes. `register [path]` registers the current directory with one command for the bundled local host; explicit paths are server-host paths. `show` retains `get` as a compatibility alias. File reads/listing remain bounded server operations; `search`, `index`, `index-status`, and `chunks` expose The Weave without The Forge. Optional selectors resolve from explicit ID/name/path, saved Workspace context, then current-directory containment. `current` reports independent Campaign and Workspace mappings and offers the exact Campaign registration command when only a Workspace matches. |
| `mcp list\|get` | List/select MCP server safe status. Output excludes URL, command, arguments, working directory, and other secret-adjacent configuration. |
| `saga list` | Paginated listing of Saga memories via **`GET /api/saga`**; options `--query`, `--session`, `--limit`, `--offset` (§21.9). |
| `saga divine <QUERY>` | Semantic search over Saga memories via **`POST /api/saga/divine`**; option `--limit` (§21.9). |
| `saga delete <ID>` | Delete a single Saga memory via **`DELETE /api/saga/{id}`** (themed confirmation on success; §21.9). |
| `saga stats` | Bordered panel summary of Saga memory storage via **`GET /api/saga/stats`** (§21.9). |

**`@filename` convention:** `--body`, `--template`, `--goal`, `--plan`, and `--inquisitor` accept either inline text/JSON or `@filename` to read the value from a file. This is a CLI-wide convention for non-interactive commands, distinct from the `chat` REPL's inline `@path` staging within prompt text — both read file contents, but the flag-value form is positional to an option while the REPL form is inline in free text.

**`apprentice chronicle` (SSE consumer):** opens `GET /api/apprentices/{id}/chronicle`, parses `data: {...}` frames (ignoring `:` heartbeats, stopping on `[DONE]`), and prints `[timestamp] type message` per event (failed-lifecycle events in the `Error` palette color). The `eventsDropped` event type (slow-reader backpressure) is rendered as a themed warning rather than a normal event. Ctrl+C cancels the stream (exit `130`).

**Inference flag ranges** (`ask` + `chat`, validated by `InferenceFlagBinder` before the request is sent): `--temperature` 0–2, `--top-p` 0–1, `--max-tokens` ≥ 1 (no upper clamp), `--seed` any 64-bit integer (no clamp), `--presence-penalty` / `--frequency-penalty` −2..2, repeatable `--stop` (multiple values), and `--response-format` accepting `text` / `json_object` / `json_schema`. Supplied `-c` / `--campaign` and `-m` / `--model` values use safe ID/name/prefix resolution; omission preserves the existing no-campaign and configured-default-model behavior. Both verbs also accept `-n` / `--new` and `--unattended`; `chat` adds `--no-tools`.

**CLI exit codes:** `ask` returns `0` on success, `1` on empty prompt / flag-parse / stream / API error, and **`130`** when an in-flight turn is cancelled (Ctrl+C). `chat` returns `0` normally and `1` if any turn failed during the session; an in-turn Ctrl+C cancels the current turn and returns to the `Mage >` prompt (it does **not** exit `130`). **Command Center** returns `0` on clean `/exit`/`/quit`, bare non-interactive usage, or `ARCANUM_NO_COMMAND_CENTER=1`; returns `1` when the terminal is too small after TG Init or TG bootstrap fails. `apprentice chronicle` returns `130` on Ctrl+C. `trial run` returns `1` when the Trial fails (`TrialResult.Passed == false`), separate from HTTP/validation failures. Other non-streaming verbs return `0` on success and `1` on failure.

**Composition:** `ArcanumApiClient`, CAF command tree (`CliApplicationFactory`), theme/Spectre UX, Command Center (`Cli/CommandCenter/`), `IArcanumServeLauncher`. Discover verbs in `Cli/Commands/`.


### 4.4.1 Auto-launch serve lifecycle

Interactive `chat` / `ask` / **Command Center** call `IArcanumServeLauncher.EnsureRunningAsync` after Grimoire init (Command Center: after host entry, before TG Run):

1. Gate: `ICliEnvironment.IsInteractive` and `ARCANUM_NO_AUTO_SERVE` unset. `NO_COLOR` does **not** disable auto-serve (it only gates color + live layout / Command Center theme).
2. Authenticated `GET /api/health` (re-reads `ISecretStore` on each poll). Map: 200 → already running; 401/403 → auth failed (do not spawn); 503 → brief retry then failed (do not spawn); TLS failure / timeout → failed (do not spawn — something answered); connection refused / network unreachable / DNS → definite no-listener → proceed.
3. If effective ListenAny needs interactive acknowledgement → failed with guidance (do not auto-ack).
4. Spawn via `IServeProcessLauncher` with `ARCANUM_AUTO_LAUNCHED=1` (direct `ProcessStartInfo`, no shell). Poll until authenticated 200 or deadline. Post-spawn 401 with null key keeps polling (first-run key race); post-spawn 401 with a non-null key across attempts → auth failed.
5. Canonical PID file remains owned by `PidFileService` under `{ArcanumPaths.GrimoireDirectory}/arcanum.pid`. The launcher never deletes it on health failure.

Auto-launched processes do not expose `arcanum serve stop` or `daemon stop`.

**MSBuild:** `PublishAot` (the shipping native image on non-macOS RIDs), `IsAotCompatible`, `EnableConfigurationBindingGenerator`. `System.CommandLine 2.0.10` and `System.CommandLine 2.0.10.Abstractions` are analyzer/source-generator packages with no runtime DLL reference, so no `TrimmerRootAssembly`, `[DynamicDependency]`, or IL-warning suppression is needed for CLI parsing. **Terminal.Gui** is referenced only from `Cli`; first-party AOT IL for the Command Center bootstrap is gated by `./scripts/verify-aot-il-warnings.sh` (method-level suppressions on `CommandCenterApp` only — no project-level blanket suppress). Transitive vulnerable packages: `dotnet list package --vulnerable --include-transitive` on the Cli project.

### 4.5 `RetroDownfall.Arcanum.Api.DevHost` (console executable, debug-only)

Thin host for F5 debugging the HTTP stack without Spectre. References `Api`, `Core`, and `Infrastructure`; mirrors `ServeCommand` wiring. Not the production entrypoint. To catch AOT issues during F5, the project sets `PublishAot`, `IsAotCompatible`, and `EnableConfigurationBindingGenerator` as **analysis signals** (not a shipped native image). On first run generates an API key and prints it to stdout.

### 4.6 `RetroDownfall.Compendium.Ux` (.NET 10 Avalonia desktop configuration editor)

Visual editor for §3.4 — reads/writes `arcanum.json` only (no inference/daemon/Grimoire/MCP). References **Core** only and edits credential environment-variable references, never provider/PFX values. Its local certificate generator writes an owner-only PEM pair, avoiding a generated password. `SettingDescriptor` drives controls/clamps; parity + coverage tests guard drift. It launches from `arcanum config open`, existing Forge configuration actions, and the macOS application-menu **Settings...** item. See [`Compendium.README.md`](Compendium.README.md).

Descriptor-driven views cache only completed builds. They observe replacement field collections so
an asynchronous configuration load rebuilds controls even when the view was created first; view
construction and rebuilds perform no diagnostic file I/O.


---

## 5. Hybrid hosting model

### 5.1 Process roles

One binary; the CLI verb selects the process role (per-command detail in §4.4). The defining axis is process lifetime:

- **No arguments** — opens Command Center on an interactive TTY; prints standard usage when
  noninteractive or `ARCANUM_NO_COMMAND_CENTER=1`.
- **`serve`** — the long-running HTTP host: builds `WebApplication` with slim defaults and blocks until shutdown.
- **`ask`** — streams single-prompt inference via NDJSON, then exits (0/1/130).
- **`chat`** — multi-turn REPL with per-turn cancellation and swap-at-end rendering.
- Short-lived verbs — `look` / `doctor` run local checks (no HTTP for path checks); `lore`, `daemon jobs|initiative|alert` call the running host's `/api` (Unseen Servant interval control via `/api/unseen-servant/*`, §5.5.2; Comm Link smoke tests via `POST /api/commlink/send`); `daemon install|uninstall|status` drives OS service lifecycle. Bare interactive `arcanum` opens the Command Center (long-lived TUI) until `/exit`; direct `chat` remains the frameless Spectre REPL.

### 5.2 Why System.CommandLine 2.0.10

Source-generated parsing (AOT-clean, no reflection). Spectre remains for rendering. `RepeatableOptionMerger` rewrites repeated flags into CAF JSON-array syntax; XML-doc aliases preserve legacy camelCase option spellings.

Every direct command inherits three recursive root options, accepted before or after the verb:

- `--json` forces one valid JSON document on stdout. Commands with typed structured output write
  that type through `IConsoleDispatcher.WriteJson` and an explicit source-generated
  `JsonTypeInfo`; legacy text commands are captured at the process boundary and returned as
  `CliTextPayload { output, exitCode }`. ANSI is disabled while JSON is active, so terminal control
  sequences cannot corrupt a pipe such as `arcanum operation list --json | jq`.
- `--plain` disables ANSI color and terminal animation for that invocation. It does
  not persist or replace `Arcanum:Cli:Theme`.
- `--yes` is the only global auto-approval signal. `IConfirmationPrompt` returns immediately when
  it is present; otherwise a redirected-output invocation fails closed with
  `NonInteractiveConfirmationException` before reading stdin or writing a prompt. Command Center
  modals and inference `ask_human` are separate interactive protocols.

`IConsoleDispatcher` owns the process stream contract: requested text/JSON payloads go to stdout;
diagnostics, warnings, progress, and confirmation copy go to stderr. `CliInvocationContext` carries
the immutable per-invocation option snapshot without process-wide environment mutation. New command
code must use these services rather than writing directly to `Console` or serializing with a
reflection overload.

`CliExitCode` is the closed process contract: `0` success, `1` generic/runtime failure, `2`
configuration or command-line failure, `3` network failure, and `130` cancellation. Arbitrary
handler return values normalize to `1`. `CliApplicationFactory.RunAsync` disables
System.CommandLine's default exception printer and is the global exception boundary:
`CliFailureMapper` maps only exception categories to fixed public messages, never exception
messages, paths, PII, API keys, or stack traces. JSON invocations receive a source-generated
`CliErrorPayload`; the same fixed diagnostic goes to stderr.

### 5.3 `ServeCommand` lifecycle

1. Cancellation token check on the injected `CancellationToken` (System.CommandLine 2.0.10 wires SIGINT/SIGTERM to it automatically because the method declares a `CancellationToken` parameter).
2. `WebApplication.CreateSlimBuilder()` (§6).
3. `UseWindowsService` / `UseSystemd` (cross-platform no-ops on other OSes).
4. Kestrel: `ListenLocalhost(port)` unless `ARCANUM_HOST_ANY` is set (§7).
5. `ClearProviders()` so Serilog replaces default logging.
6. `AddArcanumConfiguration()` loads `arcanum.json` (JSON file only). Explicit environment overrides `ARCANUM_EDITION` and `ARCANUM_HOST_ANY`, plus secret references (`ARCANUM_PROVIDER_*`, `ARCANUM_HTTPS_CERTIFICATE_PASSWORD`, `ARCANUM_COMMLINK_WEBHOOK_URL`, `ARCANUM_GRIMOIRE_DEV_KEY`, `ARCANUM_HTTPS_CERTIFICATE_PASSWORD`) remain environment-backed.
7. `AddArcanumApiServices(configuration)` registers all services (§8.3), including `AddArcanumDaemonServices` for the Unseen Servant (§5.5).
8. `ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync` **before** `Build()`.
9. `Build()` → `MapArcanumEndpoints()` → `RunAsync()`. `PidFileService` writes the PID file during host `StartAsync` (§8.19). `Log.CloseAndFlush()` in `finally`.

### 5.4 Grimoire persistence (Infrastructure + Api)

**Role:** Local-first session history in an SQLCipher-encrypted SQLite file under `~/.config/arcanum/`.

**Composition:**

- **`GrimoireDatabaseHostedService`** — initializes SQLCipher, resolves the DB passphrase from a dedicated Grimoire encryption secret using PBKDF2-HMAC-SHA256 (600,000 iterations) with a unique 16-byte salt stored in a `{grimoire.db}.kdf` sidecar, falls back to legacy API-key HKDF for databases without a sidecar, and applies embedded SQL schema migrations via **`GrimoireDatabaseBootstrapper`** → **`GrimoireSqlSchemaMigrator`** (raw SQLite + `__EFMigrationsHistory`; AOT-safe; no `MigrateAsync` on the host), then `IGrimoireDbReadiness.MarkReady()`; `FailFast` on key mismatch. Legacy databases are transparently re-encrypted to the new KDF on unlock. The same bootstrapper runs from the CLI (`ask` / `chat`) so host and CLI share one migration path (§10.5).
- **`CampaignLoggerQueue` / `Loremaster`** — bounded `Channel<Guid>` (capacity 100 **session IDs**, not Entry rows) with **non-blocking `TryQueue`**: duplicate session ids coalesce via a pending-marker map; a full channel rejects with a warning log and clears the marker so the session remains eligible for a later sweep (internal sweeps fail-open). Explicit `POST /api/sessions/{id}/rest` returns **202** when accepted/coalesced and **503** + `Session.RestQueueFull` when rejected. Background service `Loremaster` (formerly `CampaignLoggerBackgroundService`) runs hybrid sweeps using **`Session.UnsummarizedEntryCount`** (incremented on every entry append — both the inference path and The Forge `POST /api/sessions/{id}/entries` path, each serialized per-session via **`SessionEntryPersistence`** / **`SessionWriteLock`** + **`SqliteBusyRetry`** so concurrent appends never lose an increment; reset on summarize) instead of full-table `Entries` aggregation. The consume path loads session headers via **`GetSessionHeaderAsync`** (no entry hydration). Headless summarization uses a stateless `PingRequest` with `SkipSpellRouting`, `DisableMcpTools`, `UnattendedMode`, optional `Arcanum:FastModel` (else `DefaultModel`); on success, `UpdateSessionCampaignRollupAsync` atomically sets `Session.Summary`, `LastSummarizedMessageAt`, and the remaining unsummarized count. On inference failure, the watermark is **not** advanced.
- **`ArcanumDbContext`** — compiled model; SQLCipher passphrase from hosted service.
- **`SessionRepository`** — implements **`ISessionRepository`** for Forge session CRUD, entry append, export, and analytics. Entry writes delegate shared invariants (lock, retry, limits, counter, UpdatedAt) to internal **`SessionEntryPersistence`**. **`AddEntryAsync`** returns **`Result<Entry>`** for expected domain outcomes (not found, archived, entry limits). **`UpdateSessionAsync`** patches Title/Status only — Grimoire-owned counters and rollups are never clobbered from caller-supplied `Session` rows.
- **`GrimoireRepository`** — implements `IGrimoireRepository` (the interface is the authoritative reference). Entry append/finalize/discard paths delegate the same **`SessionEntryPersistence`** invariants. `GetSessionAsync` loads the session header (no eager `Include`) and a code-owned bounded, chronologically ordered window of the most recent 1,000 `Entry` rows so very long threads do not exhaust host RAM. The window is pushed down server-side as parameterized SQL (`ORDER BY "CreatedAt" DESC LIMIT n` — the SQLite provider cannot `ORDER BY`/compare a `DateTimeOffset` in LINQ, and `CreatedAt` is stored as sortable UTC text) and is widened to at least the number of entries after `LastSummarizedMessageAt`, guaranteeing read-time compression sees every unsummarized message. Campaign Logger reads every row strictly after `LastSummarizedMessageAt` and widens through the complete `CreatedAt` group containing the boundary row, so a timestamp-only watermark can never split a tool call/result pair; if that complete window exceeds the clamped session entry ceiling it fails without advancing the watermark. That overflow is corrupt or pre-limit local data needing repair or reinstall, not normal-upgrade guidance. Older entries still exist in SQL — Campaign Logger summaries (§8.7) and FTS5 `search_archives` cover the long tail.
- **`CampaignRepository`** — `ICampaignRepository.AddAsync` returns `Result<Campaign>`. It attaches EF to a SQLite **immediate** transaction, counts and inserts under that same writer lock, and wraps the complete attempt in `SqliteBusyRetry`, so concurrent inserts at capacity minus one cannot both succeed. `Campaign.MaxReached` is reserved for the actual atomic capacity outcome; unrelated write/lock failures remain infrastructure exceptions.
- **`ChronosyncEngine`** — implements `IChronosyncEngine`: compares the current `PatternSnapshot` to the latest `WorkspaceContext` row for that path, persists a new baseline row, and returns a `ChronosyncReport` (headless; no HTTP or Spectre).

**Outcome-model policy:** Repository and service boundaries return **`Result` / `Result<T>`** with wire-stable **`Error.Code`** values from **`ErrorCodes`** for expected, recoverable domain outcomes (not found, validation, limits, state conflicts). Reserve thrown exceptions for unrecoverable infrastructure faults, programmer errors, and transport layers where a catch-and-fallback is intentional (for example cooperative cancel on SSE). HTTP endpoints map **`Result.Error.Code`** to status codes exclusively via **`ArcanumErrorMapper`** — never by parsing exception messages.

#### 5.4.1 Grimoire data model

| Entity | Table | Primary key | Notable |
|--------|-------|-------------|---------|
| `Session` | `Sessions` | `Id` (Guid) | Optional `CampaignId`, `Status` (default `active`), `Title` (nullable), `CreatedAt`, `UpdatedAt`, nullable `Summary`, nullable `LastSummarizedMessageAt`, **`TotalTokensUsed`**, **`TotalCostUsd`**, **`ForkedFromSessionId`**, **`UnsummarizedEntryCount`** (entries after watermark; default `0`; `-1` reserved for lazy backfill if ever needed); indexes on `CreatedAt`, `Status`, `UpdatedAt`, `(CampaignId, Status, UpdatedAt)`, `ForkedFromSessionId`, `UnsummarizedEntryCount`; cascade-deletes entries. |
| `Entry` | `Entries` | `Id` (Guid) | FK to `Session`; composite index on `(SessionId, CreatedAt)`; **unique** index on `(SessionId, Sequence)`; index on `Role`; `Role` (enum → int); `ModelUsed` (non-null); `Sequence` (`INTEGER NOT NULL`, application-assigned); optional tool columns; FTS5 virtual table `Entries_fts` + triggers for `search_archives`. |
| `MageSetting` | `MageSettings` | `Key` (string) | `Value`, `UpdatedAt`; operator key-value surface (`/api/lore`, `arcanum lore`). No longer model-directed memory — the Lore MCP tools are removed; agent memory is The Lexicon (§10.6). |
| `WorkspaceContext` | `WorkspaceContexts` | `Id` (Guid) | `CreatedAt` (`DateTimeOffset`), `WorkspacePath` (mapped column `RootPath`, max 4096), `SerializedSnapshot` (JSON `PatternSnapshot` via `GrimoireJsonContext`). **Chronosync reporting** appends a row after each analysis; “latest” for a path is `ORDER BY CreatedAt DESC`. Composite index on `(RootPath, CreatedAt)`. |

**Supporting DTOs (Core):** `GrimoireEntryDto`, `LoreDto`, `UpsertLoreRequest`, `ChronosyncReport`, `ArcanumPaths`, `ChatCompletionUsage` (OpenAI-shaped `usage` for NDJSON and `/v1` responses), `PromptTurnResult` (buffered inference text + usage). The Forge session DTOs live under **`Core.TheForge`** (`SessionDetailDto`, `EntryDto`, etc.).

**Entry ordering authority (`Entries.Sequence`).** `Sequence` is a strictly increasing per-session
append position and **the** intra-session chronological order. `CreatedAt` is not sufficient and
must never be the sole sort key: one turn writes its prompt and its answer under a single identical
`CreatedAt`, as does a tool call with its result, and `Id` is a random Guid that cannot break that
tie in append order — a `(CreatedAt, Id)` sort inverts those pairs for roughly half of all turns.
`CreatedAt` remains the wall-clock fact and the basis of the Campaign Logger watermark
(`Session.LastSummarizedMessageAt`), so watermark reads still *filter* on `CreatedAt` while
*ordering* by `Sequence`.

Repositories allocate sequences through `SessionEntryPersistence.ReserveSequenceRangeAsync`, which
reserves a consecutive range while holding the per-session write lock that already serializes every
entry append; a batch assigns its reserved values in append order. Gaps are permitted (a rolled-back
transaction burns its range), reuse is not: the **unique** `(SessionId, Sequence)` index turns a lost
allocation into a write failure instead of a silently reordered transcript. `Sequence` is
application-assigned (`ValueGeneratedNever`), so EF always writes the value the repository supplied.
It is deliberately absent from `ToolInteractionReceipt` identity comparison (§10.2.1) because it is
an ordering fact, not part of receipt identity. Fork copies carry the source sequence forward,
preserving order without renumbering.

Consequently `EntryTemporalQueries` orders and pages on `Sequence`: newest-first windows,
offset pages, ascending export/fork batches, and the SSE replay cursor
(`ISessionRepository.GetEntriesAfterAsync` takes an exclusive `Sequence`). The `GET
/api/sessions/{id}/entries` wire contract still accepts a `(beforeCreatedAt, beforeId)` cursor; the
repository resolves that entry's sequence and falls back to the timestamp/id predicate only when the
cursor entry has since been deleted.

#### 5.4.2 Temporal context: Session-Based Consolidation and Chronosync

Arcanum’s **Session-Based Consolidation model of AI memory** spans two layers: **session** consolidation (Campaign Logger — §8.7) writes **`Session.Summary`** and advances **`LastSummarizedMessageAt`** after successful headless summarization, while **Chronosync reporting** supplies **temporal workspace** context — what changed on disk while the operator was away. `IChronosyncEngine` compares the live Eye-of-the-World `PatternSnapshot` to the last Grimoire-stored snapshot for the same `RootPath` and emits a **`ChronosyncReport`** (`PreviousSnapshotTime`, `NewThreads`, `MissingThreads`, `DomainChanged`, `PreviousDomain`) for downstream session consolidation and model-memory prompts. It is orthogonal to Campaign Logger thresholds; both contribute to the same mental model of “what the AI should know without re-reading the tree.”

#### 5.4.3 Design-time factory (`ArcanumDbContextFactory`)

`IDesignTimeDbContextFactory<ArcanumDbContext>` for `dotnet ef` tooling — uses `ARCANUM_GRIMOIRE_DEV_KEY` (fallback placeholder), a temp-directory database, and a no-op `ISecretStore`.

#### 5.4.4 Persistence inventory and storage boundaries

The Grimoire is the primary persistence authority, but not every durable byte belongs in SQLCipher:

- The opt-in inference audit is dated JSONL under `~/.config/arcanum/` (default
  `audit-YYYYMMDD.jsonl`). It records metadata for successfully completed turns only; errors,
  cancellations, timeouts, and interrupted streams create no row. Tool names/counts may be retained,
  raw arguments are omitted by default, and tool results plus prompt/answer/reasoning bodies are
  never fields (§8.26).
- `/v1/files` and session-attachment metadata are Grimoire rows, while their bytes are owner-only,
  versioned authenticated-encryption envelopes under `files/` and `attachments/` respectively.
  SQLCipher protects the metadata; `IEncryptedBlobStore` independently protects the external blobs.
- Weave, Saga, workspace-imprint, and Lexicon tables are raw-SQL schemas initialized after the
  embedded install scripts. Optional `vec0` tables are acceleration only; BLOB tables remain the
  durable fallback (§21).

| State | Durable authority | Persistence contract |
|-------|-------------------|----------------------|
| Sessions, Entries, Campaigns, Prompts, Apprentices, WorkspaceContexts, operator Lore | EF-tracked Grimoire tables | Compiled-model entities; `MageSettings` remains operator-only Lore. |
| Lexicon entity memory | `lexicon_entries` + `lexicon_fts` + `lexicon_fact_attachment_provenance` | Raw SQL initialized idempotently by `LexiconSchemaInitializer`; attachment-derived facts retain typed per-fact provenance and dynamically report deleted sources as unavailable; no EF entity (§10.6). |
| Unseen Servant schedule state | `UnseenServantWatermarks` | Raw SQL; only last-run time and effective interval persist (§5.5.5). |
| Sanctum breach history | `SanctumBreaches` | Raw SQL, per-Campaign retention, durable across restart (§11.15). |
| Idempotency claims | `IdempotencyClaims` | Raw-SQL lease/state machine; legacy `IdempotencyKeys` may remain for expiry compatibility (§11.17). |
| Inference accounting | `InferenceRuns`, `BillableOperations`, `BudgetReservations`, `CostAdjustments` | Parameterized raw SQL through `ITurnRunWriter` / `IBudgetReservationService`; outside the compiled EF model (§22.2). |
| Budget alert deduplication | `BudgetAlerts` | Unique `(Threshold, date(AlertedAt))`; insert-before-dispatch is the concurrency authority (§22.2). |
| OpenAI file metadata | `UploadedFiles` | Bytes use a fresh GUID path under `ArcanumPaths.FilesDirectory`, never the client filename; `EncryptionVersion` and `EncryptionKeyId` identify the external envelope (§11.20). |
| Session attachment metadata | `SessionAttachments` | Raw SQL through `ISessionAttachmentStore`; `EncryptionVersion`/`EncryptionKeyId`, encrypted bytes, and lifecycle are in §10.2.5. |
| Session context pins | `SessionContextPins` | Raw SQL through `ISessionContextPinStore`; durable metadata only. Content is revalidated and materialized from its authoritative source on every turn (§10.2.6). |
| OpenAI batch metadata | `Batches` | No request-count columns; `GET` derives counts from input/output/error files (§11.21). |
| Embedding, attachment-retrieval, and Saga state | `entry_embeddings`[+`_vec`], workspace/attachment companions, `saga_memories`, `saga_memory_embeddings`[+`_vec`], `saga_extraction_watermarks`, `saga_memory_attachment_provenance`, `attachment_memory_consultations` | Created idempotently from canonical definitions in `WeaveSchemaInitializer`. Attachment chunks and derived Saga memories retain typed session/attachment/key/version/hash/materialized-time/source provenance. Campaign consultations are metadata-only and link to the finalized assistant entry so timestamp-group summary windows remain exact. While Arcanum has no users, schema changes replace those definitions directly and local/test databases are recreated; no compatibility upgrade path is maintained. Reset transactionally by `POST /api/embeddings/reset?confirm=true`. |
| Entry pinning | `Entries.IsPinned` | Pinned entries survive read-time compression and remain available to inference. |
| Mandatory `apply_patch` receipt | deterministic `Entries` rows | Exact assistant `ToolCall` then system `ToolResult`; no receipt table (§10.7.4). |
| Daemon execution history | process memory | `InMemoryDaemonExecutionRepository`; restart clears it. |
| Apprentice Chronicle | process memory | Bounded `ChronicleHub`; persisted Apprentice/plan/checkpoint state is replay authority (§5.7). |
| Active Wards and A2A task mappings | process memory by design | Nothing is resumed after restart; Apprentices/Sessions/Entries remain durable. |

Reasoning is an ephemeral boundary. Client-safe reasoning can be projected in buffered, NDJSON, or
OpenAI responses, but Grimoire Entries, exports, Apprentice state, and local history remain
answer-only. Provider `ProtectedData` may survive only in the same-provider in-memory continuation.
Accounting and audit surfaces may store reasoning token counts, never reasoning bodies.

#### 5.4.5 Schema installation, serialization, and crash consistency

The AOT host does not call `Database.MigrateAsync`. Embedded files under
`Infrastructure/Data/SqlMigrations/` use a 14-digit UTC prefix
(`<yyyyMMddHHmmss>_<Name>.sql`) and are applied in `GrimoireSqlSchemaMigrator.MigrationOrder`.
The migrator owns one `SqliteTransaction` per script and inserts the matching
`__EFMigrationsHistory` row in that same transaction; scripts contain DDL only and never their own
`BEGIN`, `COMMIT`, or history insert. The project file embeds the scripts by glob. Existing script
ids remain in append order; do not reorder/remove them except as part of a verified full-baseline
squash.

Arcanum has no supported user-data migration program. Schema history may be squashed into a verified
`InitialCreate` baseline because there is no production installed base: replay the old chain against
a scratch database and compare columns, indexes, triggers, and foreign keys before replacing it.
When an already-recorded install script changes incompatibly, developers must stop every host/daemon,
back up anything needed, delete `arcanum.db` plus `-wal`/`-shm`, and restart to reinstall. There is
intentionally no incremental or data migration in either case below. Copy-pastable developer commands
are in [Arcanum.README, “Local Grimoire reinstall”](Arcanum.README.md#local-grimoire-reinstall).

- `20260705171559_InitialCreate.sql` now declares `Entries.Sequence INTEGER NOT NULL` and the unique
  `IX_Entries_SessionId_Sequence` index (§5.4.1). Existing rows have no sequence to backfill
  meaningfully — their append order was never recorded — so a Grimoire created before this baseline
  **must** be deleted and reinstalled. The compiled EF model was regenerated for the new property.
- `20260721010000_AddInferenceAccountingAndIdempotencyClaims.sql`, whose original
  `BillableOperations` definition now includes `ReasoningTokens INTEGER NOT NULL DEFAULT 0`.

Structured values must use a source-generated context:

- `GrimoireJsonContext` for pattern-domain values;
- `TheForgeJsonContext` for Campaign/Session/Sanctum domain values;
- `ArcanumJsonContext` for API wire values; and
- a new narrowly scoped `JsonSerializerContext` when none of those domains fit.

Scalar raw-SQL tables need no JSON registration. Raw-SQL repositories reuse
`ArcanumDbContext.Database.GetDbConnection()` without disposing the EF-owned connection, create
provider-neutral parameters through `DbCommand.CreateParameter()`, and wrap SQLITE_BUSY/LOCKED work
in `SqliteBusyRetry`. They do not open an unrelated second connection to the encrypted database.

The compiled model is canonical for EF-tracked entities, while raw-SQL schemas remain outside it.
The historical EF `InitialCreate` C# migration and model snapshot are intentionally not a complete
runtime-schema inventory: `SessionAttachments`, `SessionContextPins`, and inference-accounting tables have no EF entities,
and additive SQL-backed surfaces do not imply compiled-model regeneration. Do not add a `DbSet` or
regenerate the compiled model for `BillableOperations.ReasoningTokens`.

More precisely, `Sessions.TotalCostUsd`, `Entries.IsPinned`, `Session.ForkedFromSessionId` and its
index, plus additive tables such as `UnseenServantWatermarks`, `SanctumBreaches`,
`IdempotencyKeys`, `UploadedFiles`, `Batches`, and `BudgetAlerts`, were installed through SQL-backed
schema work rather than a newly generated EF migration. The snapshot was hand-aligned for
`TotalCostUsd` and `ForkedFromSessionId` so design-time migration scaffolding does not invent those
columns again. `SessionAttachments`, `SessionContextPins`, `InferenceRuns`, `BillableOperations`, `BudgetReservations`,
and `CostAdjustments` remain intentionally absent from both EF tracking and the compiled model.

`apply_patch` deliberately crosses the filesystem/Grimoire boundary without claiming distributed
atomicity. It stages same-directory outputs/backups, mutates destinations sequentially, and while the
commit is still reversible appends the exact deterministic call/result rows in one SQLite
transaction. A committed/recovered receipt makes the filesystem transaction irreversible; a
definitive persistence failure rolls it back; an ambiguous readback retains applied files and
relative recovery artifacts and fails the turn. This is bounded rollback/recovery, not isolation or
crash atomicity: a process/power failure may leave `.arcanum-*` artifacts for operator inspection,
and cleanup removes an artifact only after identity revalidation.

#### 5.4.6 Versioned authenticated blob storage

`IEncryptedBlobStore` is the single storage boundary for session attachments, `/v1/files` uploads,
and batch input/output/error artifacts. `EncryptedBlobStore` writes the version-1 `ARCABLOB`
envelope with an explicit format version, AES-256-GCM algorithm id, bounded chunk size, purpose,
plaintext length, key id, random nonce prefix, and bounded authenticated metadata. Every plaintext
chunk is authenticated independently with a 16-byte GCM tag and a nonce formed from the random
per-file prefix plus a monotonically increasing chunk index. The canonical envelope header, chunk
index, chunk length, and caller metadata are authenticated; the plaintext-length field is enforced
by an exact monotonic envelope-length calculation so any alteration, truncation, or appended
ciphertext is rejected.

The store derives separate 256-bit keys for `SessionAttachment`, `UploadedFile`, and
`BatchArtifact` with HKDF-SHA256 labels from one random 256-bit file-encryption master key. That
master key is independent of both the API key and the SQLCipher Grimoire secret. Its primary
location is the OS credential store under service/account
`arcanum/file-encryption-master-key` (macOS Keychain, Windows Credential Manager, or Linux Secret
Service). `file-encryption-key.dat`, sealed by ASP.NET Core Data Protection purpose
`Arcanum.Core.FileEncryption.v1`, is a best-effort recovery mirror; it is not the primary store.
The key id is the first 64 bits of SHA-256 over the master key and is safe to persist for lookup.

Writes create an owner-only temporary ciphertext file in the destination directory, stream and
authenticate the content in bounded memory, flush it to durable storage, reopen and verify the
complete envelope, then atomically rename it over the destination and reapply owner-only
permissions. Cancellation or failure removes the ciphertext temp. Batch writers use the streaming
writer directly, so no plaintext JSONL staging file exists. Reads are sequential/non-seekable:
each complete chunk is authenticated before any byte from that chunk reaches the caller.
`/v1/files/{id}/content` streams decrypted bytes while preserving stored MIME type and forced
attachment disposition; attachment materialization, refresh/index validation, forks, batch
processing/counting, and reconciliation all pass through the same authenticated boundary.

`UploadedFiles` and `SessionAttachments` retain plaintext MIME, size, SHA-256, and lifecycle facts
inside SQLCipher while recording the envelope version and key id. Plaintext is never written to
logs or recovery artifacts. A file without `ARCABLOB` magic is legacy plaintext and is never
silently returned by the encrypted reader. Health and `arcanum doctor` report OS-key status plus
encrypted, legacy-plaintext, and corrupt blob counts. A corrupt tag, wrong purpose, unsupported
version, missing key, wrong key id, truncation, or trailing data fails closed.

Upgrade compatibility is explicit and metadata-led. `EncryptionVersion = 0` permits a legacy
plaintext read only during the migration window. The same metadata state also accepts a valid
`ARCABLOB` envelope so a process failure after atomic replacement but before the metadata
transaction does not break an active reader. `EncryptionVersion > 0` never downgrades to
plaintext. `BlobEncryptionMetadataStore` inventories both SQL-backed blob tables, and
`BlobEncryptionFileProcessor` verifies the recorded plaintext length/SHA-256, writes and verifies a
same-directory ciphertext replacement, atomically replaces the source, then commits envelope
version/key/hash metadata. A retry reconciles the replace-before-metadata state idempotently; no
only-valid copy is removed before the encrypted replacement authenticates.

`arcanum data encryption status|migrate|verify|rotate-key` is the operator lifecycle. Migration and
rotation are restart-idempotent `LongRunningOperations`; checkpoints contain counts and byte totals,
not file identifiers. Workers clamp concurrency to 1–8 and share an aggregate bytes/second throttle.
Cancellation is observed between files; an active file finishes its atomic transition before work
stops. Verification classifies missing files, corrupt envelopes, unknown keys, plaintext/envelope
metadata disagreement, and plaintext length/hash disagreement without deleting or naming the
affected file in output or metrics.

The file secret is backward-compatible with the original single Base64 key and upgrades on rotation
to one DP-wrapped key-ring document with an active write key plus retained read keys. Rotation
creates and durably saves the new active key before rewriting files, re-encrypts incrementally,
verifies every candidate, and retires an old key only when verification succeeds and no metadata row
references it. The complete key-ring value is mirrored to `file-encryption-key.dat`, so a backup
taken during migration/rotation carries every active key; restore accepts either the legacy
single-key value or the multi-key ring.

First startup creates the master key only when the OS secret is missing and no encrypted blobs
exist. If ciphertext already exists, missing/corrupt key state never generates a replacement:
restore the OS credential, or restore `file-encryption-key.dat` together with the matching
Data Protection `keys/` directory. A complete backup therefore includes the SQLCipher database and
sidecars, `attachments/`, `files/`, the OS credential (or its DP recovery mirror), and the
Data Protection key ring. Copy ciphertext and database metadata from the same backup generation;
restoring only one side can leave key ids or file pointers inconsistent.

### 5.5 Unseen Servant

The **Unseen Servant** is a proactive background scheduler for headless inference when the HTTP host is running (`serve` or `Api.DevHost`). `AddArcanumDaemonServices` registers **`UnseenServantService`**, an ASP.NET Core **`BackgroundService`** in Infrastructure.

#### 5.5.1 Schedule and execution

`PeriodicTimer` every minute; due jobs via effective interval + tracker (watermarks hydrated §5.5.5). `Task.Run` + per-key overlap guard; new DI scope; `ExecutePromptAsync` with `UnattendedMode`, `OverrideSpellName`, empty `WorkingDirectory` (global spells). Lexicon previous-state injection follows `Arcanum:Features:Lexicon` (§5.5.3). Shutdown uses a code-owned drain timeout. Concurrency comes from `Arcanum:Daemon:MaxConcurrentJobs`; excess work waits for another scheduler tick. `OverrideSpellName` skips SemanticRouter; `SkipSpellRouting` skips all spell IO (Campaign Logger / internal).

#### 5.5.2 Adaptive initiative (dynamic polling)

`IUnseenServantPacer` holds interval overrides keyed by `{Name}\0{TargetSpell}` (same composite as tracker) — **runtime cache with Grimoire write-through** on `adjust_initiative` / `POST /api/unseen-servant/jobs/{name}/initiative`. Hydrated from `UnseenServantWatermarks.EffectiveIntervalMinutes` at startup (§5.5.5). CLI: `arcanum daemon jobs|initiative|alert`. SSE: `DaemonEvent` started/completed/failed/intervalChanged on `GET /api/events/daemon`.

#### 5.5.3 Stateful memory (Lexicon auto-injection)

**Auto-injection** avoids an extra LLM round-trip that would read memory first: **`UnseenServantDaemonJob`** loads the **Lexicon** daemon-state entity for **`daemon_state:{job.Name}:{shortHash(targetSpell)}`** (type **`DaemonState`**) via **`ILexiconService.GetByNameAsync`** before **`ExecutePromptAsync`** and embeds its facts in the kickoff under **`### Previous State`**. This runs only when **`Arcanum:Features:Lexicon`** is enabled (the same feature gate controls **`scribe_lexicon`** / **`delete_lexicon`**). When disabled, previous-state injection is skipped and the tools are absent. Load failures or missing entries log a warning and proceed with empty prior state so the minute scheduler is not skipped. Headless **`PingRequest`** still uses an empty **`WorkingDirectory`** so spells come from the global tree; internal Lexicon tools remain available for unattended runs when enabled.

#### 5.5.4 Comm Link escalation (kickoff + MCP)

**Kickoff:** Every Unseen Servant kickoff appends an explicit instruction: if the model detects a **high-alpha** or **critical** condition requiring immediate human attention, it **MUST** call in-process MCP **`send_commlink_alert`** with an appropriate **`severity`** (`Info`, `Warning`, or `Critical`).

**Runtime:** **`send_commlink_alert`** is advertised in the fixed internal **`tools/list`** catalog (not feature-flagged). The handler resolves **`ICommLinkDispatcher`** per call via **`IServiceScopeFactory`**. Dispatch returns typed **`CommLinkDeliveryResult`**: **`Delivered`**, **`Suppressed`** (unset reference / policy skip), or a failed **`Result`** (transport/HTTP error). **`CommLinkMultiplexer`** aggregates sinks (any delivery wins; partial delivery + failure → Delivered with logged failure). **`WebhookCommLinkDispatcher`** resolves `WebhookUrlEnvironmentVariable` only at dispatch (default `ARCANUM_COMMLINK_WEBHOOK_URL`) and **`POST`**s generic JSON (`title`, `body`, `severity`, `source`, `timestampUtc`). The secret URL never enters config responses or errors and logs include at most its host.

#### 5.5.5 Watermark persistence

Grimoire `UnseenServantWatermarks` is a raw-SQL scalar table, outside the compiled EF model:

- `JobKey TEXT` is `{Name}\0{TargetSpell}`;
- `LastRunAt TEXT` is ISO-8601 UTC; and
- `EffectiveIntervalMinutes INTEGER` uses `0` for “no override; use configured interval.”

Writes are immediate on job completion and initiative change; WAL plus `SqliteBusyRetry` provide
durability/contention handling. A failed write warns and is swallowed, so process memory may diverge
until the next successful write. Cardinality is approximately one row per configured job key; there
is no TTL. Rows for removed jobs are inert, and callers may explicitly `DeleteAsync(jobKey)`.

`UnseenServantService` hydrates after Grimoire initialization and before its first tick:
`GetAllAsync()` feeds the tracker and pacer. `LastRunAt` and interval overrides persist;
`nextDueAt` is reconstructed as `LastRunAt + effective interval` (plus first-dispatch startup jitter).
`LastResult` is process-local: hydration seeds `"Overdue (host was down)"` or
`"Restored from Grimoire"`, never a persisted execution summary. An overdue row keeps its real
`LastRunAt`, so it remains due on the first tick; failed/lost/corrupt hydration warns and falls back
to startup jitter without failing the host. Startup jitter is never persisted. Sanctum breaches are
Grimoire-backed separately (§11.15).

### 5.6 MCP host lifecycle

**Purpose:** Let first-party clients observe and control individual MCP servers without reloading the entire host.

**Registry:** **`McpConnectionManager`** maintains a thread-safe registry keyed by **`(serverName, scopeWorkingDirectory)`** where **`scopeWorkingDirectory == null`** means a global `~/.config/arcanum/mcp.json` entry and a non-null value is the normalized workspace root for a workspace-local `mcp.json` entry. Workspace-local entries are registered **lazily** when that workspace partition is first touched (inference, arsenal, or reload); **`GET /api/mcp`** lists them only after that access.

**`mcp.json` extensions:** Each server entry supports **`alwaysOn`** (default `true`), optional **`cwd`** (subprocess working directory for stdio servers), an optional **`type`** transport selector (`"stdio"` | `"http"` | `"sse"`), optional **`url`** (a URL infers the **Streamable HTTP** transport when `type` is omitted; an explicit `type: "sse"` selects the legacy SSE transport, still unsupported → **`Mcp.SseNotSupported`**), and an optional **`inheritEnv`** string array naming host environment variables an stdio server may inherit despite the default env-strip (e.g. `["PATH","HOME"]` for `npx`). HTTP endpoints must be `https` unless their host is listed in `Arcanum:Integrations:Mcp:AllowedHttpHosts`, and are SSRF-validated via `OutboundUrlGuard` before connect.

**Workspace-local trust gate:** Workspace `mcp.json` servers are **not admitted** until the operator approves the workspace via **`POST /api/mcp/trust-workspace`** (`{ "workingDirectory": "<root>" }`). Approvals persist at `~/.config/arcanum/trusted-mcp-workspaces.json` as normalized workspace path → SHA-256 digest.

Admission is bound to the exact bytes parsed: `BuildMergedToolsForWorkspaceAsync` performs one `SecureFileReader` read capped by the code constant **`McpSecurityLimits.MaxMcpConfigBytes = 256 KiB`**, deserializes that buffer, hashes that same buffer, and asks `IsApprovedDigestAsync` whether the **just-parsed digest** is approved. It does not re-read `mcp.json` between parse and approval. The digest is carried on every `ManagedMcpServerEntry`; config B retires config-A entries with an old digest or removed name before registering replacements, and missing/invalid/oversized/unapproved files retire the whole workspace-local surface. Retired entries reject new lifecycle work, leave the registry/cache immediately, and drain/stop under a bounded cleanup path.

Freshness remains current-file based after admission. Cached surfaces call `TrustedMcpWorkspaceStore.GetSnapshotAsync`, which securely re-reads and hashes the current file; lifecycle visibility/start/restart checks require that current digest, the persisted approval, and the entry's source digest all match. A path, length, timestamp, cached digest, or stale entry alone never authorizes execution. **`alwaysOn` is ignored** until these checks pass. Global MCP servers (`ScopeWorkingDirectory == null`) have no workspace source digest and are unaffected.

The trust document has hard code limits, not operator settings: **8 MiB serialized bytes**, **256 entries**, **4,096 normalized path characters**, and exactly **64 hexadecimal characters** per SHA-256 digest. Reads use the same no-follow bounded `SecureFileReader`; writes use same-directory `AtomicFile.ReplaceAsync` and strict owner-only directory/temp/final permissions. Corrupt, oversized, or malformed documents fail closed; trust updates also fail closed when owner-only permissions cannot be verified. `MaxMcpConfigBytes` is deliberately separate from configurable `Arcanum:Mcp:MaxJsonRpcLineBytes` (transport frame/body cap) and `MaxToolsTotalBytes` (cumulative tool-schema memory cap).

**Auto-start:** **`McpServerBootstrapHostedService`** calls **`IMcpConnectionManager.InitializeAsync`** on host start to load the global registry and start all **`alwaysOn`** global servers. **`StopAsync`** calls **`StopAllAsync`** for graceful shutdown. Unaffected by the ModelContextProtocol SDK migration — its calls into `IMcpConnectionManager` are unchanged in signature and behavior.

**Lifecycle API:** **`StartAsync`**, **`StopAsync`**, and **`RestartAsync`** are idempotent (`Running`/`Starting` start → success; `Stopped`/`Error` stop → success; restart while stopped → start). Per-server **`SemaphoreSlim`** gates mutations. State transitions publish **`McpServerEvent`** on **`IEventBus`** **after** releasing the gate. Each entry's live client is a **`SdkMcpClientWrapper`** (the only `IMcpClient` implementation — see §4.2) wrapping an official SDK `McpClient` session; unexpected subprocess exit or a dropped/expired Streamable HTTP session both transition a running server to **`error`** and publish an event, via the wrapper's `OnTransportEnded` callback observing the SDK client's `Completion` task (rather than a stdio-specific process-exit handler, this now applies uniformly to stdio and HTTP).

**Disambiguation:** Lifecycle routes accept optional **`?workingDirectory=`** (workspace root). When omitted and multiple registry entries share the same name, the API returns **400** **`Mcp.AmbiguousServer`**.

**`POST /api/mcp/reload`:** Preserves the existing **global nuclear reload** semantics: dispose all partition clients, clear caches, reset global bootstrap, re-read global `mcp.json`, restart **`alwaysOn`** globals. The optional **`workingDirectory`** body field is **informational only** (logged); workspace partitions are not immediately re-built.

**CLI lifecycle projection:** `arcanum mcp list|show|start|stop|restart|reload|trust|tools` calls
these authenticated APIs through `ArcanumApiClient`; the CLI never opens `mcp.json` or manages a
child process directly. Safe list/detail views do not project `Command`, `Arguments`, `Url`, or
environment. A visible workspace-scoped entry is labelled `trusted` because registry visibility is
admitted only after the current digest passes the workspace trust gate; global entries report trust
as not required. Explicit `--workspace` selects a workspace partition, while the selected
`McpServerInfo.WorkingDirectory` is sent back as the lifecycle disambiguator. MCP resource
selection opts into interactive resolution for ambiguous names only on a real TTY; non-interactive
and JSON callers receive deterministic candidate diagnostics.

**Inference:** **`GetAvailableToolsAsync`** merge order is unchanged (internal → global → workspace local). Only **running** managed servers contribute tools; **`alwaysOn: false`** servers stay stopped until explicitly started.

### 5.7 Apprentice orchestration

**Purpose:** Goal-driven autonomous sub-agents (**Apprentices**) that the Dungeon Master creates, starts, and monitors. The **Master**, implemented by **`WizardIntelligenceProvider`**, generates a plan, then the Apprentice executes each step with **`UnattendedMode: true`**, checkpointing progress in the Grimoire.

**Persistence:** **`Apprentices`** table (Grimoire DB) stores goal, JSON plan, status, workspace path, optional campaign and session FKs, and checkpoint blob. **`IApprenticeRepository`** / **`ApprenticeRepository`** (scoped).

Only lifecycle state is durable. Chronicle subscriber channels and live Master frames are bounded
process-local streams, and A2A task-id mappings are runtime indexes; they disappear on restart.
Persisted Apprentice/Session/Entry/checkpoint rows remain the recovery authority. Reasoning frames
are ignored by `ApprenticeStreamFramePolicy` (unknown frame kinds also default to ignore) and
never enter step results, plans, prompts, checkpoints, or Chronicle replay.

**Runtime:** **`ApprenticeService`** (`BackgroundService`, singleton **`IApprenticeRuntime`**) runs alongside **`UnseenServantService`** without modifying it. On host start, **`GetResumableAsync()`** re-spawns tasks for **`Running`** Apprentices (crash recovery). `Arcanum:Execution:MaxConcurrentApprentices` is enforced by an atomic **`ApprenticeConcurrencyGate`** (increment-then-compare, matching **`SseConnectionGate`**); excess **`/start`** requests queue up to `Arcanum:Execution:MaxPendingApprenticeStarts`, while **`/resume`** and **`/intervene`** fail fast with **`Apprentice.MaxReached`** when no slot is available.

**Execution loop:** Planning → optional plan generation via **`ExecutePromptAsync`** (`SkipSpellRouting: true`) → step loop via **`StreamPromptAsync`** using the caller/host cancellation token → progress-based **Second Wind** recovery → optional **Shifting Fate** re-weave after each completed step → **Divine Intervention** escalation. The Grimoire session spans all steps via **`SessionId`**. There is no hidden step, run, or turn duration cap. Physical process/transport cleanup limits remain local to those operations.

**Second Wind (progress-based recovery):** Transient provider/tool failures are retried while their error/result/child evidence changes. Repeating the same recovery state stops deterministically and either escalates or fails according to **`EnableDivineIntervention`**. Each recovery emits **`stepRetrying`** on the Chronicle; there is no retry-count, backoff, or duration tuning. Ward/forbidden-art denials remain terminal (**`Failed`**).

**Shifting Fate (plan revision):** After each completed step, the **Master** runs the code-owned lightweight re-weave evaluation without a run-count ceiling. If strategy changes, the pending plan tail is replaced and **`planRevised`** is emitted; an identical merged plan is treated as no progress and ignored. Operators may call **`POST /api/apprentices/{id}/reweave`** only while the Apprentice is **`Paused`** or **`Escalated`** (not while **`Running`**) to avoid racing the execution loop.

**Divine Intervention (DM escalation):** When recovery repeats the same state (if **`EnableDivineIntervention`**) or the Apprentice calls in-process MCP **`petition_dungeon_master`**, the stream consumer correlates by tool **`CallId`**: records a pending petition on ToolCall, continues pumping so the tool runs, then parses ToolResult `notificationStatus` (`delivered` / `suppressed` / `failed`). Only **`delivered`** counts as already alerted; otherwise a fallback Critical Comm Link may fire. Status becomes **`Escalated`**, **`apprenticeEscalated`** is emitted. The DM resolves via **`POST /api/apprentices/{id}/intervene`** (slot acquired **before** any state mutation; capacity failure returns **`Apprentice.MaxReached`** with no persistence); guidance is injected into the next step prompt and **`apprenticeIntervened`** is emitted.

**The Conclave & Cast Sending (cross-Apprentice delegation):** Gated by **`Arcanum:Features:Conclave`**. The Conclave is the overarching network in which the Master coordinates multiple Apprentices. When enabled, an Apprentice may call the in-process MCP tool **`cast_sending`** (`goal`, optional `name`) to delegate a sub-task outside its immediate spell: the shared **`ConclaveArchmage`** service (also backing **`POST /api/apprentices/{id}/cast`**) mints a child Apprentice in the caller's workspace and returns its id, subject to code-owned lineage depth and breadth limits (`ConclaveLineage`). The orchestrator detects the `cast_sending` tool result, stamps the child's **`ParentApprenticeId`** into the child's `CheckpointData` JSON (no schema change), emits **`castSent`**, and best-effort **`StartAsync`** the child through the atomic concurrency gate. Lineage surfaces on **`ApprenticeDetailDto.ParentApprenticeId`** (a `[NotMapped]` entity convenience property hydrated from the checkpoint).

**Simulacrum (parallel steps):** A **`PlanStep`** may set **`isParallel: true`**. Contiguous parallel steps form a Simulacrum group executed concurrently via **`Task.WhenAll`**, bounded by the code-owned per-Apprentice parallelism limit using a `SemaphoreSlim`. Each branch runs in its **own** `AsyncServiceScope` — its own `IArcanumIntelligenceProvider` and pooled `ArcanumDbContext` — so no EF Core `DbContext` is shared across threads; branch inference is **stateless** (no shared `SessionId` writes). All branches complete before the orchestrator persists every step result and advances **`CurrentStep`** past the group on its single context (single-writer), then runs one **Shifting Fate** evaluation for the group. Emits **`simulacrumStarted`** / **`simulacrumCompleted`**. Note: the shared in-process MCP server serializes tool I/O across branches, so parallelism primarily reduces inference latency.

**Apprentice statuses:** `Idle`, `Planning`, `Running`, `Paused`, `Escalated`, `Completed`, `Failed`, `Cancelled`. **`Escalated`** is non-terminal and awaits DM intervention; it is not auto-resumed on host restart.

**Chronicle event types (lifecycle):** `apprenticeStarted`, `planGenerated`, `stepStarted`, `stepRetrying`, `stepCompleted`, `stepFailed`, `planRevised`, `apprenticeEscalated`, `apprenticeIntervened`, `apprenticePaused`, `apprenticeResumed`, `apprenticeCompleted`, `apprenticeFailed`, `apprenticeCancelled`, `eventsDropped` (slow-reader backpressure marker), plus pass-through `toolCall`, `toolResult`, `warded`, `wardResolved`.

**Chronicle:** **`ChronicleHub`** (per-Apprentice bounded channel, `DropOldest`) decouples execution from **`GET /api/apprentices/{id}/chronicle`** SSE. When a subscriber's channel is full, the oldest event is dropped and an **`eventsDropped`** marker is emitted so operators know the stream is lossy. Late connect replays plan state from DB, emits **`apprenticeEscalated`** when status is **`Escalated`**, then streams live. Pass-through Master events (`toolCall`, `toolResult`, `warded`, `wardResolved`) are flattened on the wire (the legacy nested field name `wizardEvent` is not used).

**Control API:** **`POST .../start|pause|resume|cancel|reweave|intervene`** delegate to **`IApprenticeRuntime`**. Pause cancels the in-flight step CTS (without disposing it — disposal happens in **`CleanupExecution`** after the task drains); **`cancel`** follows the same cancel-not-dispose pattern so the run exits cooperatively without **`ObjectDisposedException`** overwriting **`Cancelled`** with **`Failed`**. Resume continues from **`CurrentStep`**; intervene resumes from **`Escalated`** only.

**CLI stubs:** **`arcanum apprentice create|start|chronicle`** print route tables (The Forge stub pattern).

### 5.7.1 A2A and The Conclave

External door into The Conclave: A2A **server** (inbound → Apprentices) and **client** (`dispatch_sending`). Layered gates are `Arcanum:Features:Conclave` plus `A2AServer` and/or `A2AClient`; per-call `IOptionsMonitor` observes live settings while routes remain mapped at boot. Packages are AOT-clean (`verify-aot-il-warnings.sh`).

**Server:** mapped under `Arcanum:Integrations:A2A:ServerPath` on `/api` (API key required) — **no** unauthenticated `/.well-known/agent-card.json`. Handler mints an Apprentice via `ConclaveArchmage` and relays Chronicle to A2A task states. Workspace fallback is `Arcanum:Integrations:A2A:DefaultWorkspace` → `Arcanum:Workspaces:DefaultRoot` → CWD; an empty `Arcanum:Security:CampaignRoots` still denies registration.

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
2. **Loopback (`ListenAny` false):** binds plaintext HTTP on `Arcanum:Host:Port`; when `Arcanum:Host:Https:Enabled` is `true`, loads the certificate via **`HttpsCertificateLoader`** and adds a second TLS listener on `Arcanum:Host:Https:Port`.
3. **All-interfaces (`ListenAny` / `ARCANUM_HOST_ANY`):** requires `Arcanum:Host:Https:Enabled` and a loadable certificate; binds **only** `ListenAnyIP(HttpsPort)` with TLS. Plaintext any-IP HTTP is never bound. Startup fails before binding if HTTPS is disabled or the certificate cannot be loaded.

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

- **`POST /api/intelligence/human-response`** — **400** validation (including the code-owned answer UTF-8 byte limit); **404** + `ApiResponse<bool>` failure when no waiter exists for `promptId` (`Intelligence.HumanPromptNotFound`); **200** + `ApiResponse<bool>` with `Data: true` when the answer is accepted.

- **`POST /api/mcp/reload`** and **`POST /api/intelligence/arsenal`** — Optional JSON body **`OptionalWorkspaceRequest`** (`{ "workingDirectory": "..." }` only). Responses remain `ApiResponse<T>` as today.

### 8.5 NDJSON streaming pipeline

`/api/intelligence/ping-stream` uses NDJSON (`application/x-ndjson`) for real-time token streaming:

- **Server:** Events serialized via `Utf8JsonWriter` + `ArcanumJsonContext`, newline-terminated, flushed per event. Writer: **`InferenceExecuteWriter`** (also used by spell/prompt `execute-stream`).
- **Wire shape:** Each line is an `IntelligenceEvent` with **camelCase string** discriminator **`type`**: **`"status"`**, **`"sessionBound"`**, **`"conversationBound"`** (deprecated alias emitted alongside **`sessionBound`** for one release), **`"context"`**, **`"token"`**, **`"reasoning"`**, **`"result"`**, **`"error"`**, **`"toolCall"`**, **`"toolResult"`**, **`"warded"`**, **`"wardResolved"`**, **`"toolError"`** (tolerated tool exception, emitted immediately before its `toolResult`; §10.2.1). `context` carries the latest pre-call `ContextTokenBreakdown`; a second frame for the same call may add provider-reported input and variance after usage arrives. The enum is annotated with `[JsonConverter(typeof(JsonStringEnumConverter<IntelligenceEventType>))]` and per-member `[JsonStringEnumMemberName]` so the AOT JSON source generator emits the canonical strings. **`PingRequest.SessionId`** continues a Grimoire thread; when omitted the hub creates a new session on first assistant turn.
- **Reasoning frame:** `type:"reasoning"` carries a typed, client-safe payload separate from answer `data`: `{"type":"reasoning","message":"client-safe summary","reasoning":{"text":"client-safe summary","output":"summary"}}` (the shared event envelope may also contain its normal null/default members). `reasoning.output` is exactly `none`, `summary`, or `full`; projected frames use `summary` or `full`. Provider `ProtectedData` is deliberately absent.
- **Disconnect / cancellation (`InferenceExecuteWriter`):** the code-owned policy is **`Auto`**. With an `Idempotency-Key`, continue-then-replay — do **not** link `RequestAborted` to the inference token; drain the hub enumerator and keep exact-byte capture so the claim may Complete. Without a key, caller cancellation abandons the claim. Arcanum adds no inference deadline; caller/host cancellation propagates, while unexpected provider/transport cancellation is sanitized as a generic inference failure. Either way, ledger provider-billed partial usage and reconcile/release the reservation.
- **Clients (`ArcanumApiClient` and The Forge):** `StreamReader` reassembles transport-fragmented UTF-8 into complete lines, including multibyte characters split across transport reads. Before strict source-generated deserialization, an AOT-safe `Utf8JsonReader` scan validates the root `type`. Canonical values are matched case-insensitively and normalized before `ArcanumJsonContext` / `TheForgeJsonContext` deserialization; a truly unknown, nonblank future string is silently skipped so later frames continue. Invalid JSON, a missing/non-string/blank discriminator, or any whitespace-padded discriminator is **malformed** and retains the surface's diagnostic behavior. This narrow pre-scan does not install a permissive enum converter or reflection serializer: direct source-generated deserialization remains strict. The terminal **`result`** event carries native **`usage`** (`prompt_tokens`, `completion_tokens`, `total_tokens`, optional `cached_tokens`, optional `reasoning_tokens`) on the `IntelligenceEvent` payload; **`data`** still duplicates **`total_tokens`** as a decimal string for backward compatibility, while the final answer remains in accumulated **`token`** frames and the result `message`. Assistant text is never reconstructed from legacy result `data`.

### 8.6 Request Delegate Generator

`<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>` on `Api` ensures Minimal API endpoints in a referenced class library are source-generated.

### 8.7 Session-Based Consolidation (Campaign Logger)

Three mechanisms trigger Campaign Log consolidation:

1. **Message-count threshold** (`CampaignLogThreshold`) — safety valve for unbounded growth.
2. **Idle timeout** (`CampaignLogIdleTimeoutMinutes`) — natural session boundary.
3. **Explicit rest** — `POST /api/sessions/{id}/rest`.

The queue consumer resolves **`IArcanumIntelligenceProvider`** in a per-item DI scope alongside **`IGrimoireRepository`**, loads the session header via **`GetSessionHeaderAsync`**, and batches rows with **`CreatedAt > (LastSummarizedMessageAt ?? DateTime.MinValue)`**. It builds a stateless **`PingRequest`**: empty `Prompt`, `StatelessMessages` (system persona + user payload with prior summary and batched turns), **`SkipSpellRouting: true`**, **`DisableMcpTools: true`**, **`UnattendedMode: true`**, **`Model`** from **`Arcanum:FastModel`** when set else **`Arcanum:DefaultModel`**, else omitted for first-provider fallback, and **no** `SessionId` so the hub does not append a new **`Entry`**. On **`ExecutePromptAsync`** success, **`UpdateSessionCampaignRollupAsync`** atomically persists the LLM text into **`Session.Summary`** and sets **`LastSummarizedMessageAt`** to the latest batched entry time. On **`Result.IsFailure`** or exception, **no** DB update — the session remains eligible on the next sweep. The intelligence hub **reads** `Summary` for optional read-time compression (§10.2.3).

For attachment privacy, the successful source turn records only typed consultation metadata. The
Campaign Logger adds logical key, version, opaque attachment id, and source type for consultations
inside the summarized window; it never loads attachment bytes, automatically submits the session's
attachment index, or copies hashes/host paths into the summarizer payload. The prompt asks for useful
decisions and source references, not an attachment archive.

Under the same **Session-Based Consolidation model of AI memory**, **Chronosync reporting** (§5.4.2) addresses **spatial** drift: thread lines and `DomainType` deltas vs the last persisted `PatternSnapshot`, not chat log length. Campaign Logger and Chronosync are separate triggers; the hub folds `ChronosyncReport` into the system prompt via `PingRequest.ChronosyncDelta`; MCP context remains separate.

### 8.8 OpenAI `/v1` Chat Completions compatibility subset

`OpenAiV1Endpoints` advertises a **Chat Completions compatibility subset**, not full OpenAI API parity. Moderations/images/audio remain **`501 not_supported`**. Polymorphic `content` (string | parts) is AOT-safe; unsupported part types / over `MaxContentPartsPerMessage` → **400** `invalid_value` before mapping. Vision parts map to MEAI `TextContent`/`UriContent`/`DataContent` (§10.2.4).

**Parameters applied** (`ApplyInferenceParameters`): temperature, top_p, max tokens, penalties, seed, stop, response_format. Reasoning controls are additive: `reasoning_effort` = `none|minimal|low|medium|high|xhigh`, `reasoning_budget` = positive integer, and `reasoning_output` = `none|summary|full`. `reasoning_effort` and `reasoning_budget` are mutually exclusive and map to native `PingRequest.reasoning`; capability validation runs before provider I/O for buffered and `stream:true` requests. `reasoning_output` is an Arcanum-local projection/exposure preference and is passed to Microsoft.Extensions.AI only as a best-effort hint. It is not a guaranteed provider wire control, and Arcanum does not patch an unsupported `reasoning_output` field into provider JSON. When omitted, the resolved capability chooses `full` when `SupportsFull`, otherwise `summary` when `SupportsSummary`; `AllowsClientOutput` is required, and streaming also requires `SupportsStreaming`. Native effort/output and configured control-support/wire-dialect enums are strict string-only AOT contracts. OpenAI `reasoning_effort` and `reasoning_output` are also string-only. A numeric enum (defined or undefined) or an unknown enum string fails JSON binding before semantic validation. `n` must be `1` when present. Client `tools`/`tool_choice` rejected **400** `unsupported_parameter` unless `ClientToolForwarding:Enabled` (then schema/count validation; §8.8.3).

**Responses:** buffered answers remain in `choices[].message.content`; additive reasoning is in `reasoning_summary` and/or `reasoning_content`. Streaming answers remain in `choices[].delta.content`; reasoning uses the same additive fields on the delta, in provider order. A client that ignores the fields still reads an unchanged answer. Usage keeps `completion_tokens` and `total_tokens` authoritative and projects the reasoning subset at `completion_tokens_details.reasoning_tokens`; cached prompt subsets use `prompt_tokens_details.cached_tokens`. Buffered `message.tool_calls` still reports server-executed calls (§8.8.1); streaming SSE includes keep-alives and usage only when requested by `stream_options`. Semantic reasoning failures are typed OpenAI error bodies/chunks, never `delta.content`: they use HTTP **400**, `type:"invalid_request_error"`, `param:"reasoning"`, and the reachable stable code `invalid_reasoning_options` (effort plus budget), `invalid_reasoning_budget` (budget outside 1–2,097,152), `unsupported_reasoning_control`, `reasoning_budget_exceeds_model_limit`, or `unsupported_reasoning_output`. Numeric/unknown reasoning enum JSON never reaches those semantic branches; strict binding returns HTTP **400** `invalid_request_error`, code `invalid_json`, and no parameter. Unknown model → **404** `model_not_found`; tool-loop/timeout → **503** `server_error`.

**Current streaming projection topology:** production `/v1/chat/completions` obtains native `IntelligenceEvent` frames from `WizardIntelligenceProvider` (`TurnExecutionCoordinator` → `IntelligenceEventProjection`) and maps them to SSE chunks in `OpenAiV1Endpoints`. That endpoint mapper is the authoritative compatibility implementation. `OpenAiSseProjection` is a separate semantic helper/characterization path, not the projection instance used by the production route. The two paths share reasoning-field and typed-error rules only; `OpenAiSseProjection` does not define production terminal usage chunks, `stream_options.include_usage`, or tool-argument fragmentation. Those wire contracts are covered directly by production endpoint tests rather than by an exact-parity claim.

#### 8.8.1 Server-executed tools on `/v1` (buffered + streaming tool_calls)

Arcanum executes MCP tools server-side; `/v1` surfaces calls for observability/replay. Buffered: `PromptTurnResult.ToolCalls` → `message.tool_calls`. Streaming: `ToolCall` events → `delta.tool_calls` (40-char argument fragments; monotonic per-response `index`; fresh `call_…` ids). **`toolResult` never surfaced** on `/v1`. Forwarding mode preserves provider-minted ids and returns `finish_reason: "tool_calls"` without executing client tools. Richer native surface: `/api/intelligence/ping(-stream)`.

#### 8.8.2 `GET /v1/models` capability enrichment

`ModelInfoBuilder` is shared with `GET /api/models`. Additive OpenAI fields: `context_window`, `supports_vision`, `provider_name`/`provider_type`, `supports_tools`/`supports_streaming` (always true), plus the same optional typed `reasoning` capability object returned by the native endpoint.

#### 8.8.3 Client tool security (forwarding mode)

When `Arcanum:Features:ClientTools` is enabled, Sanctum/Ward/tool audit do **not** apply to client-supplied tools (provider executes). Default remains reject.

### 8.9 NDJSON anti-buffering headers (`/api/intelligence/ping-stream`)

The NDJSON streaming endpoint sets `Cache-Control: no-cache` and `X-Accel-Buffering: no` (parity with the SSE endpoint in §8.5/§8.8) so reverse proxies (nginx, Cloudflare, k8s ingress) do not coalesce incremental frames.

### 8.10 Buffered `/api/intelligence/ping` envelope

The buffered ping endpoint wraps a **`PromptResponseDto`** (Core) inside `ApiResponse<T>`: `text` (assistant answer only), `usage` (native token counts, including additive top-level `reasoning_tokens`), `toolCalls` (the assistant-issued calls executed server-side, when any), `finishReason`, and `reasoning` (an ordered array of `{ text, output }` client-safe segments; empty by default). Reasoning is never concatenated into `text`. Previously the envelope held only the assistant text as a bare `string`; clients now get the full turn context without falling back to NDJSON.

### 8.10.1 Mana counter (`POST /api/intelligence/mana`)

Read-only model-aware estimate (`ManaCountRequest` → `ManaCountResult`); no inference/Grimoire writes. `model` resolves the configured provider/canonical model profile, while an unconfigured model uses the conservative fallback. The result retains legacy `manaCount` / `encoding` / per-message fields and adds classification, profile id, safety margin, and the complete `ContextTokenBreakdown`. `tools:true` materializes the current native + MCP declarations and includes their names, descriptions, and full JSON schemas in both the total and source breakdown. **400** when neither `messages` nor `prompt` is supplied.

### 8.11 Daemon event SSE bus (`GET /api/events/daemon`)

In-process `IEventBus` uses code-owned bounded per-subscriber channels with `DropOldest`. Wire: `text/event-stream` `DaemonEvent` frames + best-effort `[DONE]`. `Arcanum:Execution:MaxSseConnections` and `MaxSseConnectionsPerType` feed `SseConnectionGate` → **503** `Api.TooManyConnections`. Anti-buffering headers; API key on the `/api` group. Rate limiting admits the HTTP request only, not open-stream duration.

### 8.12 Configuration API (`GET` / `PUT` / `POST /api/config`)

Read: redacted secret-bearing URLs/endpoints (`***`) plus non-secret credential references; environment values are never read into the response. Write: merge redacted URL placeholders from the current snapshot, validate, and atomically replace `arcanum.json`. Validate-only also merges recognized endpoint masks against the current snapshot before outbound and semantic validation, so an unchanged redacted `GET` document remains a valid update candidate; it never writes. Residual masks for new/unmatched providers fail closed. Provider API keys and PFX passwords are not accepted fields. The source-generated settings snapshot is loaded at process start, so configuration changes require a host restart; referenced secret environment values are resolved only at provider/certificate use. Status: **400** `Configuration.ValidationFailed`, **500** `Configuration.WriteFailed`.

### 8.13 MCP server event SSE bus (`GET /api/events/mcp`)

`McpConnectionManager` publishes `McpServerEvent` on state changes. Same SSE back-pressure/caps/auth as §8.11.

### 8.14 Spell Management API (`/api/spells`)

Workspace resolution: `?workspace=` → `Arcanum:Workspaces:DefaultRoot` → CWD. CRUD needs a resolvable workspace; empty `Arcanum:Security:SpellWorkspaceRoots` denies all (**403** `Spell.PathNotAllowed`). Built-ins under `~/.config/arcanum/spells/` are read-only (`Spell.BuiltinReadOnly`). Format: `SPELL.md` frontmatter + body; optional `SPELL.json` (legacy `SKILL.json` read fallback; writes always `SPELL.json`). Search shadow order: campaign > workspace > builtin. Versions: string labels `SPELL.v{label}.md` (`^[A-Za-z0-9.]+$`); activate swaps into `SPELL.md` and records `activeVersion`. Clone/cast/import quirks and status codes: §4.3. Per-workspace locks; delete only under `{workspace}/spells/{name}`.

### 8.15 Daemon job management (`/api/daemons`, `/api/executions`)

**Route families:** `/api/unseen-servant/*` = Unseen Servant interval control; `/api/daemons/*` + `/api/executions/*` = job registry + execution history. Watermarks: §5.5.5. On-demand `POST .../run` waits for completion; scheduled path shares `DaemonRunner` single-flight per daemon. History process-local (`ExecutionHistoryLimit`); detail includes correlated ring-buffer logs.

### 8.16 Log ring buffer (`GET /api/logs`, `GET /api/events/logs`)

Serilog → `SerilogLogRingBufferSink` → a code-owned bounded in-memory ring that overwrites the oldest entry. Query filters + `beforeSequence` cursor. Live SSE uses the same caps as §8.11. It is not persisted across restarts. Post-build sink registration avoids a Build()-time logging DI deadlock.

### 8.17 Workspace registry and file browser/writer (`/api/workspaces`)

Campaign-backed when Grimoire ready (`persisted: true`); else in-memory. Writes gated by `Arcanum:Workspaces:EnableFileWrite` (default off) → **403** `Workspace.FileWriteDisabled`. Path policy: reject `..`/absolute; symlink escape → `Workspace.SymbolicLinkEscape`; revalidate before I/O. Atomic temp+rename for PUT/PATCH. Size clamps: §3.4. PATCH ordinal replace with ambiguous/not-found codes. HEAD contents returns size/`Last-Modified` only.

The CLI exposes this boundary directly as `arcanum workspace list|current|register|show|tree|info|read|search|index|index-status|chunks|unregister`. `tree`, `info`, and `read` call the authenticated file-browser routes and never read the client filesystem directly. File writes remain absent from this command family, so `Arcanum:Workspaces:EnableFileWrite` is neither bypassed nor implicitly enabled. `register [path]` sends the path to the server registry; omission uses the client current directory only because the shipping CLI targets the bundled loopback host. Help and output call every such value a server-host path so this convenience cannot silently become a remote path assumption.

### 8.18 Session API

Search, export, analytics, CRUD, manual entry append, SSE live stream, and Campaign Log **`/rest`** use the Grimoire-backed **`/api/sessions`** surface. See **§11.16 Session lifecycle** for the authoritative contract.

### 8.19 Server lifecycle (PID file)

The code-owned path is `~/.config/arcanum/arcanum.pid`. Startup fails if a live PID is present; a stale file is overwritten. Shutdown deletes the file only if it still names this process. DevHost and `serve` share the same path and therefore cannot run concurrently.

### 8.21 The Proving Grounds (`POST /api/proving-grounds/trials/run`)

Ephemeral Trial + Inquisitors (`regex` / `jsonSchema` / `semantic` FastModel judge). Targets: spell / prompt / apprenticeGoal. Terminology strict — industry LLM-test jargon prohibited. Errors §8.23.

### 8.22 Metrics endpoint (`GET /metrics`)

Prometheus text `0.0.4` via `System.Diagnostics.Metrics` + hand-rolled exporter (no OTel/prometheus-net — AOT). Catalog: HTTP requests, inference duration/tokens, tool outcomes, SSE gauge, active sessions (scrape-time query), Sanctum breaches, plus `arcanum_estimated_input_tokens`, `arcanum_provider_reported_input_tokens`, absolute `arcanum_input_token_estimation_variance` (low-cardinality `direction=underestimated|overestimated|exact|inconsistent`), and `arcanum_context_budget_rejections_total`. `arcanum_tool_invocations_total` has the closed `outcome=success|denied|error` domain (Ward and Sanctum refusals are `denied`) and uses the invocation's canonical `tool_name` directly. Unknown names are therefore distinct label values; input/tool-name length limits bound each value's size, but the implementation does **not** enforce a closed label-value set or a global cardinality cap. `arcanum_apply_patch_artifact_cleanup_total` is count-only with closed `outcome=complete|retained`; it never labels paths, sessions, or receipt IDs. Token histograms use token-scale buckets rather than duration buckets; provider/model labels remain low-cardinality (+ runtime meters via `MeterListener`). Path outside `/api`/`/v1`. `Arcanum:Features:Metrics=false` → **404**. `Arcanum:Security:MetricsRequireApiKey` defaults true and is forced true on ListenAny. Auth: `X-Arcanum-Key` or Bearer.

### 8.23 Error code catalog and HTTP status mapping

Wire-stable codes live on `ErrorCodes` (Core). HTTP mapping authority: `ArcanumErrorMapper.ResolveStatusCode` (Api). `ResolveStatusCodeDefaultBadRequest` treats unmapped codes as **400** on Apprentice/Campaign/Spell/Prompt/ProvingGrounds routes while still honoring explicit **500** mappings (`ProvingGrounds.InferenceFailed`, `Workspace.WriteFailed`, `Workspace.DeleteFailed`, `Saga.SearchFailed`, `Hub.Error`). Unrecognized strings (including `Hub.Error` via default arm) → **500**. Keep in sync with `ErrorCodes.cs` / `ArcanumErrorMapper.cs` (`ArcanumErrorMapperTests`).

**Default / unmapped:** unlisted codes → **500**; `ResolveStatusCodeDefaultBadRequest` downgrades unmapped → **400** except the explicit **500** set above.

**/api vs /v1:** native `/api` uses `ApiResponse<T>` + codes below. OpenAI `/v1` uses the OpenAI error envelope (`message`/`type`/`code`/`param`); hub failures map similarly (e.g. timeout → **503** `server_error`; unknown model → **404** `model_not_found`). Client-tool forwarding surfaces OpenAI codes `unsupported_parameter` / `too_many_tools` / `invalid_schema` while Core codes remain `ClientTools.*`.

| Codes (grouped) | HTTP | Semantics |
|-----------------|------|-----------|
| `Validation.InvalidPrompt`, `InvalidBody`, `InvalidQuery`, `InvalidProviderType`, `AttachedFiles` | 400 | Request shape / bounds validation |
| `Hub.Model` | 404 | Model not in any provider `models` |
| `Hub.Error` | 500 | Generic inference failure (mapper default arm) |
| `Campaign.NotFound`; `Session.NotFound` / `EntryNotFound`; `Grimoire.LoreNotFound`; `Apprentice.NotFound`; `Workspace.NotFound` / `FileNotFound`; `Spell.NotFound`; `Prompt.NotFound`; `Intelligence.HumanPromptNotFound`; `Mcp.ServerNotFound` / `ToolNotFound`; `Daemon.NotFound`; `Files.NotFound`; `Batches.NotFound` / `InputFileNotFound`; `Saga.NotFound`; `ProvingGrounds.SpellNotFound` / `PromptNotFound`; `Workspace.ReplacementNotFound` | 404 | Missing resource |
| `Campaign.InvalidPath` / `MaxReached`; `Session.Archived` / `InvalidStatus` / `TooManyEntries` / `EntryTooLarge` / `MemoryManagementDisabled` / `EmptyContent`; `Apprentice.Disabled` / `PendingQueueFull` / `InvalidGuidance` / `InvalidPlan` / `InvalidGoal` / `InvalidWorkspace`; `Workspace.NameEmpty` / `SymbolicLinkEscape` / `PathTraversal` / `DirectoryNotEmpty` / `ReplacementAmbiguous` / `PathIsDirectory` / `PathIsFile`; `Spell.NoWorkspace` / `InvalidWorkspace` / `InvalidName` / `NameCollision` / `BuiltinReadOnly` / `DuplicateVersion` / `InvalidVersion`; `Prompt.CodexPathNotContained` / `DuplicateVersion` / `InvalidName` / `InvalidVersion` / `InvalidRequest`; `Mcp.AmbiguousServer` / `MissingWorkspace` / `ServerNotRunning` / `AmbiguousTool` / `ToolError`; `Sending.TaskRejected`; `Security.BlockedOutboundUrl` / `IdempotencyKeyTooLong`; `Files.InvalidMimeType`; `Batches.InvalidEndpoint`; `Embeddings.ConfirmationRequired`; `ProvingGrounds.InvalidTrial` / `WorkspaceNotAllowed`; `Saga.NotEmpty`; `Scrying.VisionNotSupported` / `TooManyImages` / `UnsupportedMimeType`; `WebBrowsing.TooLarge` (reserved; today truncates) / `InvalidUrl`; `ClientTools.Disabled` / `TooMany` / `InvalidSchema`; `Guardrails.PiiDetected` / `Blocked`; `StructuredOutput.ValidationFailed` / `SchemaInvalid` | 400 | Domain validation / policy refusal (non-auth) |
| `Campaign.PathNotAllowed`; `Workspace.PathNotAllowed` / `AccessDenied` / `FileWriteDisabled`; `Spell.PathNotAllowed`; `Sending.Disabled` / `AgentNotAllowed`; `Mcp.WorkspaceNotTrusted` / `DiagnosticBlocked`; `Scrying.FeatureDisabled`; `WebBrowsing.SsrfBlocked` | 403 | Path/network/feature deny |
| `Security.MissingApiKey` | 401 | Missing/invalid API key |
| `Session.TooManyPinned`; `Apprentice.AlreadyRunning` / `Running` / `NotPaused` / `CannotReweave` / `NotEscalated` / `MaxReached` / `ConclaveDisabled`; `Security.IdempotencyConflict`; `Security.IdempotencyInProgress` | 409 | State or idempotency conflict |
| `Sending.MaxTasksReached`; `RateLimit.TooManyRequests` | 429 | Concurrency / rate limit |
| `Workspace.FileTooLarge`; `Files.TooLarge`; `Scrying.ImageTooLarge` | 413 | Payload too large |
| `Sending.AgentUnreachable` / `AgentCardInvalid`; `CommLink.Suppressed` | 502 | Downstream / webhook failure |
| `Api.TooManyConnections`; `Connection.Unreachable`; `Embeddings.ProviderUnavailable` / `FeatureDisabled`; `Session.RestQueueFull` | 503 | Capacity / provider unavailable, or bounded Campaign Logger queue rejection |
| `Mcp.DiagnosticTimeout`; `Connection.Timeout`; `WebBrowsing.Timeout` | 504 | Bounded downstream transport/diagnostic operation timeout |
| `Workspace.WriteFailed` / `DeleteFailed`; `ProvingGrounds.InferenceFailed`; `Saga.SearchFailed` | 500 | Explicit infra/search failures (never downgraded by DefaultBadRequest) |

**Ollama:** providers use the `OpenAICompatible` contract and surface failures as `Hub.Error`.

### 8.24 OpenAI embeddings (`POST /v1/embeddings`)

Composes `IWeaveService` + tokenizer. `model` must match `Arcanum:Integrations:Embeddings:Model` or be omitted → else **404** `model_not_found`. Long inputs use code-owned chunking + mean-pool/L2. `encoding_format` is `float|base64` (`EmbeddingBlobCodec`). Idempotency-Key is supported. Errors use the OpenAI envelope (**400** invalid input/chars; **503** when The Weave is unavailable).

### 8.25 HTTP response compression

Brotli+Gzip via ASP.NET ResponseCompression; early pipeline. Excludes `text/event-stream` and `application/x-ndjson`. `EnableForHttps` left false (framework default).

### 8.26 Persisted inference audit log

Opt-in JSONL (`Arcanum:Host:AuditLog:*`); dated files, owner-only, soft size + retention. A row is written only after a turn completes successfully (ping / ping-stream / v1-completion today); errors, timeouts, cancellations, and interrupted streams are not audit rows. Tool names and counts are metadata; `Arcanum:Host:AuditLog:RedactToolArguments=true` (default) makes `toolArgumentsJson` null, while opting out records the exact raw argument snapshots at operator risk. Tool results, prompt/answer bodies, and reasoning bodies are never fields in this log. Audit failure is warning-only and never changes the already-successful turn. Query: `GET /api/audit`.

### 8.27 Content guardrails (PII / toxicity / topics)

Opt-in via `Arcanum:Features:Guardrails` (default false), with policy under `Arcanum:Security:Guardrails`. Input PII (GeneratedRegex) → `Guardrails.PiiDetected`; toxicity/topics → `Guardrails.Blocked`. Streaming output filtering is code-owned **buffered** mode. Audit JSONL + `GET /api/guardrails/audit`. Only redacted matched spans appear in logs/errors.


## 9. Native AOT and trimming

### 9.1 Why Native AOT

Zero runtime prerequisite for the shipping CLI; fast cold start for short verbs; smaller trimmed footprint; reduced reflection surface via source-gen JSON/RDG/hand-authored tools.

### 9.2 What is AOT-optimized today

- **`Cli` publish** (`<PublishAot>true</PublishAot>` on non-macOS RIDs) produces a native binary via ILCompiler over the full closure (`Cli` + `Api` + `Infrastructure` + `Core` + framework + third-party assemblies). macOS RIDs use folder-based self-contained publish (see Cli csproj notes on ld-prime).
- **`Infrastructure`** additionally sets `PublishAot` / `IsTrimmable` as a library signal so the ILCompiler analyzes it in the publish graph — it is not shipped as its own binary.
- **`Api` / `Core`** declare `<IsAotCompatible>true</IsAotCompatible>` to opt into AOT-oriented analyzers. Libraries in the closure should remain AOT-compatible for every host.
- **Command Center (Terminal.Gui 2.4.17)** lives only in `Cli`. Bootstrap is isolated in `CommandCenterApp`; any `IL3050`/`IL2026` suppressions are method-level there and must remain first-party-clean under `./scripts/verify-aot-il-warnings.sh`.

### 9.3 Tradeoffs and constraints

- **System.CommandLine 2.0.10 v5** is source-generated with zero reflection — the CLI layer has no AOT tradeoffs.
- **EF Core** compiled model is required (`dotnet ef dbcontext optimize`). Precompiled queries are disabled (`EFPrecompileQueriesStage = none`) because certain repository LINQ patterns are not yet compatible.
- **`dotnet build`** is warning-clean in Debug and Release. macOS Native AOT is currently disabled:
  macOS 27 / Xcode 26+ `ld-prime` can crash on the large AOT object closure
  (`dotnet/runtime#119380`), `ld_classic` is no longer supported, and the current toolchain's
  single-file apphost is not a reliable fallback. macOS therefore uses an untrimmed, folder-based
  self-contained publish; Windows/Linux remain Native AOT.

### 9.4 AOT discipline for new code

- Every HTTP payload type needs a `[JsonSerializable]` registration on `ArcanumJsonContext`.
- Grimoire `PatternSnapshot` blobs use `GrimoireJsonContext` with explicit `JsonTypeInfo` — no reflection-based `JsonSerializer` overloads for those columns.
- MCP wire types use `McpJsonSerializerContext` exclusively — no reflection-based `JsonSerializer` overloads.
- Outbound Comm Link webhook bodies use `CommLinkInfrastructureJsonContext` / `WebhookPayloadDto` exclusively (`title`, `body`, `severity`, `source`, `timestampUtc`) — no `PostAsJsonAsync` with anonymous DTOs.
- CLI process envelopes use `CliJsonContext` with explicit `JsonTypeInfo`; typed command payloads may
  use another source-generated context already authoritative for that DTO.
- Minimal API handlers must not return anonymous DTOs or use unbounded reflection-based model binding.
- New `AIFunction` tools must use hand-authored `JsonDocument` schemas, not `AIFunctionFactory.Create`.
- Runtime model-supplied regex must not use `RegexOptions.Compiled` or an input-derived cache. `search_workspace` tries the culture-invariant `NonBacktracking` engine first and falls back to the bounded interpreted engine only for otherwise-valid syntax that `NonBacktracking` does not support; fixed application patterns continue to use `[GeneratedRegex]`.
- **`ArcanumSettings` and nested config POCOs must use `{ get; set; }`**, not `init`. `EnableConfigurationBindingGenerator` silently skips `init`-only properties (dotnet/runtime#107856); reflection binding still works, so unit tests that call `.Bind()` can hide the bug until `arcanum serve` runs.

## 10. Intelligence pipeline

### 10.1 Architecture

The intelligence layer follows a **provider pattern**: `Core` defines `IArcanumIntelligenceProvider`, `Api` implements **`WizardIntelligenceProvider`** as a thin facade over **`TurnExecutionCoordinator`** / **`TurnEngine`**. The engine owns the logical run and emits semantic `TurnEvent`s; buffered / NDJSON / OpenAI-SSE shapes are projections. HTTP writers own serialization and exact-byte idempotency capture.

- **`TurnEngine`** — a bounded semantic shell (`ITurnEventSource`) around `TurnEventEmitter`: it selects the buffered/streaming method on **Wizard's `ITurnPipelineRunner` implementation**, terminalizes cancellation/failure safely, and emits one ordered semantic stream. The existing Wizard core still owns preflight, provider fallback, context, accounting, the single mode-parameterized model/tool loop, validation, and finalization. A root TurnEngine does **not** duplicate that loop. The `delegate_task` tool may deliberately start one fresh buffered child TurnEngine through `ITurnExecutionFacade`; the child uses the same pipeline implementation under the isolation and budget boundary below.
- **`TurnExecutionCoordinator`** — sole semantic consumer; applies exactly one projection and does not serialize HTTP. Buffered native and `/v1` calls materialize through `BufferedTurnProjection`; native streaming and production `/v1` streaming select `IntelligenceEventProjection`. **`OpenAiV1Endpoints` is the authoritative production `/v1` JSON/SSE mapper** over those results/frames. `OpenAiSseProjection` is a semantic helper/characterization path for shared reasoning/error rules, not the production endpoint instance.
- **`IModelTokenEstimator` / `ModelTokenEstimator`** — resolves a typed `ModelTokenizationProfile` by model override → provider default → built-in canonical model → conservative fallback, then produces one immutable `ContextTokenBreakdown` from the actual `ChatMessage` / `ChatOptions` payload. Rows distinguish history, system/Codex/Spell, tools (including full JSON schemas and call/result framing), Lexicon/Saga, workspace RAG, attachment RAG, explicit attachments, refreshed files, current prompt, structured output, provider framing, safety margin, reserved answer, and reserved reasoning. The wire/audit shape also exposes `HistoryTokens`, `ExplicitAttachmentTokens`, `RefreshedFileTokens`, `AttachmentRagTokens`, and `WorkspaceRagTokens` as direct attribution fields. The session-attachment metadata index is system context, not retrieved attachment RAG. Ledger pressure evictions add attachment/workspace dropped-chunk and estimated-token counters without changing the admitted component totals.
- **`IModelCallExecutor`** (Core contract; Api implementation) — sole chat-provider invocation boundary (`ExecuteBufferedAsync` / `ExecuteStreamingAsync`) with `ModelCallPurpose` tagging and no counter gate. A supplied `ModelCallContext` carries the already finalized breakdown from admission; the executor validates provider/model/profile, separate reserves, totals, and a SHA-256 payload fingerprint before reusing that single object (or computes one when an auxiliary caller has none). A stale payload is rejected before I/O rather than silently recounted after reservation. The executor also rejects context/cost overflow, emits estimate metrics, and reconciles provider input usage without overwriting the estimate. This applies to tool continuations, compatibility retries, structured-output corrections, routing, and Lexicon extraction. On Microsoft.Extensions.AI **10.8.1**, it also classifies `TextContent` as answer and `TextReasoningContent` as reasoning, preserves raw provider content for same-provider continuation, and surfaces `UsageDetails.ReasoningTokenCount` without reconstructing hidden reasoning.
- **`ProviderResolver`** (`Core.Configuration`) maps `PingRequest.Model` (or `ArcanumSettings.DefaultModel`, or the first configured model) to a `ProviderSettings` row and canonical model id — no hard-coded default model literals. Internal callers (Campaign Logger) supply an explicit `PingRequest.Model` from **`Arcanum:FastModel`** when set, else **`Arcanum:DefaultModel`**, before falling back to the first configured model.
- **`IChatClientFactory`** (`ChatClientFactory`, singleton) resolves `AiProviderKind.OpenAICompatible` (including Ollama via its own `/v1` endpoint) via **`Microsoft.Extensions.AI.OpenAI`** / OpenAI .NET `ChatClient` + `IHttpClientFactory` + custom `endpoint` + `AsIChatClient()` with `OpenAiRequestAugmentingHandler`. A second overload, `ResolveClientAsync(ProviderSettings, string, CancellationToken)`, builds a lease for an explicit (provider, model) pair — bypassing `ProviderResolver` selection entirely — so the resilience fallback loop (below) can target a specific candidate.
- **Microsoft.Extensions.AI** provides the shared `IChatClient` surface for routing, tools, and streaming.
- **`ProviderResolver.ResolveCandidates(ArcanumSettings, string?, IProviderHealthTracker?)`** (Core) is the fallback-aware counterpart to `TryResolveProviderForModel`. It resolves the same target model (request model → `DefaultModel` → first provider's first advertised model) and returns the providers advertising it in configured order. With a health tracker, it excludes providers reported unhealthy; if that would leave zero candidates, every compatible match is returned so stale health state cannot collapse fallback to one provider. A null tracker retains the single-candidate behavior used by isolated callers/tests.
- **Provider health tracking** (`Core.Resilience` / `Infrastructure.Resilience`): `IProviderHealthTracker` is an in-memory, `ConcurrentDictionary`-backed singleton recording `ProviderHealthStatus` (name, `IsHealthy`, `LastChecked`, `ConsecutiveFailures`) per provider. Providers not yet observed are assumed healthy. `MarkFailed`/`MarkHealthy` are called both reactively (by the hub on a connectivity failure) and periodically (by `ProviderHealthProbeService`, a `BackgroundService` that probes every configured provider via `GET /models`). Code-owned probe intervals, timeout, and failure threshold determine health transitions. State is in-memory only — a host restart starts every provider Healthy. `HealthChanged` fires on transitions but has no subscribers yet (reserved for future SSE observability).

### 10.2 `WizardIntelligenceProvider` design

**Facade:** Public `ExecutePromptAsync` / `StreamPromptAsync` build `TurnExecutionRequest` and call `TurnExecutionCoordinator` (Buffered / IntelligenceEvent projections). `HasIdempotencyKey` comes from `TurnIdempotencyAmbient` (set by the idempotency endpoint filter when the `Idempotency-Key` header is present) — not from `PingRequest`.

**Model resolution:** `ProviderResolver.TryResolveProviderForModel` on the current `ArcanumSettings` snapshot. Explicit request/default model strings must match a configured `models` entry, or resolution fails (configuration error).

**Reasoning request/capability contract:** Native requests use `reasoning.effort` (`none|minimal|low|medium|high|extraHigh`), `reasoning.budgetTokens` (1–2,097,152, additionally capped by the model), and `reasoning.output` (`none|summary|full`). A model object declares `reasoning.controlSupport`, `supportsSummary`, `supportsFull`, `supportsStreaming`, `reportsReasoningTokens`, `allowsClientOutput`, `wireDialect`, and optional `maxBudgetTokens` (§3.4). Stable native failures are `Validation.InvalidReasoningEffort`, `Validation.InvalidReasoningOutput`, `Validation.ReasoningEffortAndBudgetMutuallyExclusive`, `Validation.InvalidReasoningBudget`, `Validation.UnsupportedReasoningControl`, `Validation.ReasoningBudgetExceedsModelLimit`, and `Validation.UnsupportedReasoningOutput`; §8.8 lists their OpenAI code mappings. Validation is repeated for the actual direct or fallback candidate before its provider call, so explicit controls are never silently dropped.

**Provider mapping:** `ReasoningChatOptionsAdapter` maps effort/output through typed MEAI `ChatOptions.Reasoning`. MEAI 10.8.1 has no `Minimal` effort value, so OpenAI `minimal` is applied through a fresh concrete `ChatCompletionOptions`. Numeric budgets require one explicitly configured nonstandard closed dialect: `openRouter` → `reasoning.max_tokens`; `topLevelReasoningBudget` → top-level `reasoning_budget`; `anthropicThinking` → `thinking:{type:"enabled",budget_tokens:N}`. `standard` is the typed MEAI/OpenAI path and rejects numeric budgets. No provider/model-name detection occurs, and a request without reasoning leaves provider JSON unchanged.

**Automatic fallback loop:** With a health tracker registered, both `ExecutePromptAsync` and `StreamPromptAsync` use `ProviderResolver.ResolveCandidates` and try every distinct eligible candidate once, in order. On a pre-commit connectivity failure (`HttpRequestException` or a provider/HTTP transport timeout) the hub calls `IProviderHealthTracker.MarkFailed` for that candidate, logs a `Warning` with the provider name and candidate ordinal, and tries the next candidate; on success it calls `MarkHealthy` (clearing prior failures). Arcanum itself adds no turn deadline. Non-connectivity failures are returned immediately. Provider commitment occurs before projection on the first non-empty answer delta, **any** provider reasoning item (visible text or protected-only data, even when client output is disabled or buffered), a complete actionable tool proposal, or an empty successful round. After commitment a connectivity failure terminates the run: there is no provider fallback and the outer no-tools compatibility restart is also prohibited.

**Reasoning separation and safety:** Answer and ephemeral reasoning have independent accumulators. Reasoning never enters answer token accumulation, structured-output validation, `PromptTurnResult.Text`, Grimoire assistant entries, audit/log text, or persistence. Client-safe reasoning projects only when the resolved model allows the requested output (and, for live frames, declares streaming support). MEAI `TextReasoningContent.ProtectedData` may remain on the raw in-memory assistant message only so the **same provider** can continue after a tool result; it is never projected, logged, audited, traced, exported, or stored. Buffered guardrails and strict structured-output mode hold both answer and reasoning frames until validation succeeds. Corrective strict calls discard the rejected candidate's reasoning/answer and release only the accepted replacement; output guardrails inspect the accepted answer plus projectable reasoning. Explicit guardrail passthrough retains its existing leakage warning. Reasoning is not transferred from the Master to Apprentices, Apprentice prompts/checkpoints/results, or Chronicle persistence.

**Streaming:** `StreamPromptAsync` yields `IntelligenceEvent` objects — `status` (model checks), `sessionBound` (canonical session id; `conversationBound` emitted as deprecated alias), `reasoning` (typed client-safe reasoning, separate from answer), `token` (incremental answer text), `toolCall` / `toolResult` (tool execution diagnostics), `toolError` (tolerated unexpected tool exception; §10.2.1), `attachmentRefreshed` (sanitized native refresh observability; ignored by OpenAI projections), `warded` / `wardResolved` (Forbidden Arts gate; §11.14), **`result`** (structured **`usage`** plus legacy **`data`** total string), `error`.

**Forbidden Arts (wards):** After the hub emits `toolCall` for a gated tool, `ExecuteToolCallWithWardAsync` may emit `warded`, block on **`IWard.WardAsync`** until the operator resolves via **`POST /api/wards/{id}`** or the code-owned wait expires, then emit `wardResolved` and either execute the tool or feed a synthetic denial as `toolResult`. Per-campaign, **`CampaignSettings.RequireWardForForbiddenArts`** defaults to **`true`** on newly registered campaigns; set `false` via `PUT /api/campaigns/{id}` to opt out. When no campaign matches `WorkingDirectory`, wards apply when `Arcanum:Security:Ward:Enabled` is true.

**Sanctum (execution boundary):** After a tool call passes the Ward gate (or bypasses it), **`EnforceSanctumAsync`** runs before **`InvokeToolCallAsync`** when the request **`WorkingDirectory`** matches a campaign with **`SanctumConfig.Enabled`**. **`SanctumGuard`** validates disabled tools, filesystem paths (canonical resolution with symlink checks via **`WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck`**), and model-supplied network targets such as `read_url` (and its legacy `browse_web` alias). CommLink has no model-supplied URL: its secret environment value is resolved and SSRF-validated inside the dispatcher. **`SanctumMode.Strict`** blocks with a synthetic tool result; **`AuditOnly`** logs a breach and allows execution.

**Operator-safe errors:** Inference failures use fixed generic strings for clients and Grimoire; changed failure paths log safe operation metadata and exception type rather than raw provider exception text.

**Cancellation boundary:** caller cancellation is distinct from a provider-originated `OperationCanceledException`. TurnEngine lets the producer finish cancellation terminalization on the emitter with `CancellationToken.None`, drains the semantic channel independently, then rethrows the caller token so cancellation propagates only after cleanup. An `OperationCanceledException` observed while the caller token is not cancelled is treated as a provider failure: logs contain safe metadata/exception type only and the client receives the fixed generic failure, never the exception message.

### 10.2.1 Built-in tools and MCP workspace tools

Tool registration is built in `WizardIntelligenceProvider` per inference attempt:

1. `ArcanumLocalTimeTool` (`get_local_system_time`) — always registered. Returns the current local system time in ISO 8601.
2. `ArcanumSystemInfoTool` (`get_arcanum_system_info`) — always registered. Returns host OS description, CPU architecture, and .NET runtime version.
3. `ArcanumDelegateTaskTool` (`delegate_task`) — registered only on the primary loop. It accepts a self-contained `prompt`, optional explicit `{path,content}` file values, and a required token or USD ceiling plus bounded `max_turns`. It starts one buffered child TurnEngine, waits, and returns only the child summary. The ordinary parent stream exposes the `delegate_task` `toolCall` / terminal `toolResult` as its running indicator; child answer/reasoning/tool frames are never projected into the parent chat.
4. `ArcanumSpellScriptTool` (`run_spell_script`) — registered only when the active spell (or any **Arcane Resonance** dependency) has `scripts/` files **and** host-process tools are enabled by `Arcanum:Edition=Development` plus `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1` (even when `DisableMcpTools` is true). Local edition does not advertise it. Scripts are resolved across the primary spell and all resonant dependencies; duplicate filenames across spells return a tool-result error (not a host exception).
5. MCP tools — merged from `McpConnectionManager.GetAvailableToolsAsync` unless `DisableMcpTools` is true.

**Artifact Attunement:** When the active spell's **`SPELL.json`** `declaredTools` array is non-empty, **`WizardIntelligenceProvider`** restricts the attunable set to that allowlist. That set covers in-process **`arcanum-internal`** and external **`mcp.json`** tools plus **`web_search` / `read_url`**. Exactly four hub-native tools are exempt: `get_local_system_time`, `get_arcanum_system_info`, `delegate_task`, and `run_spell_script`; native web tools are built in but are not additional exemptions. Empty or absent `declaredTools` leaves every otherwise-enabled tool available. Excluded MCP names are logged at **Debug**. A dependency spell's `declaredTools` describe the tools it needs when invoked directly; when pulled in as a dependency it does **not** widen the allowlist — the **primary** spell retains control over which tools the Master may wield.

Spell validation recognizes all canonical built-in names plus the legacy `browse_web` alias, so web-tool declarations do not produce false "not found in configured MCP servers" warnings. Dry-run cast preview canonicalizes the alias to `read_url` and applies the same web enablement and attunement decision as live inference, plus the existing MCP `ArtifactAttunement` intersection.

**Attunement × Forbidden Arts invariant:** Artifact Attunement only **intersects** the host MCP toolset with `declaredTools` — it never widens it or introduces tools the host does not already expose. **`ToolPolicy.NoForbiddenArts`** (request-driven) may strip Forbidden Arts from the *advertised* set, but a spell that lists a Forbidden Art in `declaredTools` still receives that tool in the advertisement when the request does not use `NoForbiddenArts`. The **Ward** gate runs at **execution** time (after advertisement) and is orthogonal: a tool may be advertised yet blocked until an operator resolves the ward (or unattended mode auto-denies). While Wards are enabled, `execute_command`, `apply_patch`, and `workspace_check` are intrinsic Ward candidates regardless of attunement or operator additions under `Arcanum:Security:Ward:ForbiddenArts`.

All hub built-in tool ids use snake_case, consistent with in-process MCP tools.

The canonical tool list is in §4.2. When its Development + startup-environment gate is open, `run_spell_script` runs with `UseShellExecute = false`, cwd fixed to the resolved spell's `scripts/` directory, bare filename only (prefix containment across primary + resonant roots), extension-based runner map, and the same timeout, cooperative-cancel, and kill-tree behavior as `execute_command` (including `CancellationToken.Register` for immediate process kill).

**Reliable workspace tools (`arcanum-internal`):**

- **`search_workspace`** accepts required `pattern`, `mode:"literal"|"regex"`, and `caseSensitive`, plus optional workspace-relative `root`, at most 64 slash-normalized `globs`, and at most 64 allowlisted extensions. It decodes strict UTF-8 text, matches each logical line independently (patterns never span newlines), and returns 1-based line/column matches in deterministic path/line/column order. Literal matching is ordinal; regex is culture-invariant `NonBacktracking` first with a bounded interpreted fallback for unsupported features, never dynamic compilation. It is a direct filesystem search, not Weave/Divination. Normal statuses are `ok`, `no_match`, `invalid_pattern`, `invalid_request`, `timed_out`, or `capped`; cap/skip counters and `regexEngine:"non_backtracking"|"interpreted"` make partial results explicit. Its tool-owned elapsed limit becomes a structured `timed_out` result; caller cancellation is checked throughout traversal, decoding, and matching and propagates as cancellation rather than being relabeled as timeout.
- **`apply_patch`** accepts a canonical unified diff plus optional `dryRun`. `UnifiedDiffParser` is pure and workspace-independent; `WorkspacePatchPlanner` then validates every normalized path, regular-file identity, fingerprint, strict-UTF-8 document, and hunk (including bounded unique relocation) before any destination mutation. `dryRun` performs the same parse/plan without creating directories, staging, or writing. A real call stages every output and backup in the destination directory, then changes destinations sequentially. That per-call transaction is reversible but **observable and non-isolated**; it provides rollback semantics, not process-wide isolation or crash atomicity. Multiple `apply_patch` calls in one turn are independent transactions.
- `apply_patch` requires a bound persisted session, assistant Entry, exact argument snapshot, model, and deterministic invocation identity; there is no stateless fallback. After filesystem commit, the exact bounded result remains reversible while the Grimoire appends deterministic assistant `ToolCall` then system `ToolResult` rows. `NewlyCommitted` / `RecoveredCommitted` retain the patch and trigger bounded artifact cleanup; `Failed` rolls it back; `Ambiguous` retains the applied patch plus normalized relative recovery artifacts and fails the turn. Caller cancellation propagates. Cancellation before or during commit first attempts reverse rollback under an independent cleanup deadline; cancellation during receipt handoff records any attached persistence classification carried by the exception rather than converting it to a normal tool result. Rollback also removes transaction-created parent directories deepest-first, but only after identity revalidation and only while empty; a no-follow directory handle is held from creation until completed rollback, irreversible classification, or terminal abandon so delete/recreate races cannot pass through Unix inode reuse. Terminal ambiguous and incomplete-rollback handoffs release the handle without deleting retained recovery artifacts. Replaced or newly populated directories are retained and reported for recovery. An incomplete rollback returns `status/code:"rollback_incomplete"` with deterministic affected/recovery paths. Results preserve manifest order in `files`; recovery arrays are normalized, distinct, and deterministically sorted. Output capping drops trailing file items first, then recovery-array tails, without losing status or total/omitted counts.
- Supported patch records are strict-text create, modify, delete, and explicit rename, including new regular-file Unix mode and exact newline/BOM preservation. Binary, submodule, symlink, hard-linked, mode-only, copy, unsafe alias/topology/cycle, and unsupported metadata records fail before mutation. There is no durable transaction journal or startup crash recovery; same-directory artifacts are the bounded operator recovery surface if the process dies between sequential changes or before receipt classification.
- **`workspace_check`** exposes closed `dotnet-build`, `dotnet-test`, and `dotnet-lint` profiles plus validated operator-owned profiles. The model chooses only a profile ID and allowlisted option values; the server renders every argv token and enforces `--no-restore`. This is still arbitrary-code execution: MSBuild tasks, source generators, analyzers, and tests come from the workspace. It therefore remains an intrinsic Ward tool while Wards are enabled.
- `workspace_check` is advertised only when enabled on **macOS** with a working `/usr/bin/sandbox-exec` Seatbelt jail, trusted native `dotnet`, trusted selected SDK/runtime, and root-owned launch chain. Linux and Windows return unavailable and are not advertised in this release; `AllowUnsandboxedToolChildren` does not enable this tool. SDK selection safely snapshots the first `global.json` found while walking from the workspace root toward the filesystem root (64 KiB maximum), accepts `sdk.version`, the supported `rollForward` values, `allowPrerelease`, and at most 16 `sdk.paths`, then materializes a sanitized copy in an owner-only resolver root. Every search path must resolve beneath the pinned trusted `dotnet` root (`$host$` means that root); the trusted muxer's selected installed SDK must remain beneath an allowed SDK root. Without `global.json`, selection uses the pinned installation's `sdk` directory. The SDK entry point is captured and revalidated by both stable file identity and SHA-256 from the same open handle, so Unix inode reuse cannot disguise replacement. Source, the pre-existing global NuGet package cache, `dotnet`, SDK, and runtime are read-only. Restore-affecting project artifacts are validated and seeded into owner-only per-run output/intermediate/test/home/cache/temp roots outside the source; profiles never restore. Seeding has fixed fail-closed caps of 128 projects, 640 copied files, and 64 MiB copied bytes per run; restore-input discovery/fingerprinting additionally allows at most 64 inputs and 8 MiB aggregate input bytes per project. Executable, SDK, package-cache, launch-chain, selected SDK/runtime, and restore-input identities are revalidated immediately before spawn.
- Seatbelt is a filesystem boundary only: network egress remains open. macOS process-group and descendant cleanup is best effort; an intentionally malicious detached descendant may survive and continue using allowed network egress to exfiltrate readable source/package data. Ward approval is the operator's acceptance of that residual risk, not a claim that fixed argv makes a repository trusted. A host-bound inference deadline must fit the clamped check timeout plus 30 seconds of cleanup grace; preflight is separately capped at 10 seconds. Tool-owned timeout kills the observed tree and returns `timed_out`; caller cancellation kills/cleans and propagates. Structured `msBuild`, `vsTest`, or `dotNetFormat` diagnostics are capped with typed counts and bounded stdout/stderr fallback. Statuses are `ok`, `failed`, `timed_out`, `invalid_request`, `unavailable`, `restore_required`, or `insufficient_deadline`; `GET /api/health` component `WorkspaceCheck` reports current eligibility.

When `WorkingDirectory` is empty, filesystem tools return a workspace-not-configured error; `ask_human`, Lore, and `search_archives` still work.

**Graceful partial tool failure.** Expected tool errors (validation, ward denial, Sanctum strict block, an unregistered tool name) already return a structured tool-result string and never throw. An *unexpected* exception (an infrastructure fault — a bug in a tool implementation, a transport failure inside an MCP server, an unhandled edge case) is caught on both buffered and streaming paths, logged at `Error` with the tool name, call id, and exception **type** but never the raw exception object or message, and synthesized into the tool result text `ToolExecutionPipeline.PublicToolFailureMessage(toolName)` — `"[Tool error: {toolName} failed with an internal error. The operator has been notified.]"` — so the model sees the failure and can decide how to proceed rather than the turn dying mid-stream. This tolerant mode policy is code-owned. A distinct **`toolError`** NDJSON event (`IntelligenceEventType.ToolError`) is emitted immediately before the corresponding `toolResult` frame so streaming clients can observe and surface the failure distinctly — native NDJSON only, not surfaced on the OpenAI `/v1` bridge (falls through its default case exactly like `toolResult`, §8.8.1).

**Tool-result materialization:** unstructured result text is normalized through shared `Utf8Truncation` helpers before fit checks. Prefix and prefix+suffix slicing is surrogate-safe, uses UTF-8 byte checks plus saturating token-character arithmetic, and reserves room for the truncation marker; when the full marker itself cannot fit, the marker is safely truncated. The final marker **and** retained content therefore stay inside both the token and UTF-8 byte bounds, including for malformed UTF-16 input.

**Apprentice denial classification:** semantic `ToolInvocationCompleted` carries `Denied` into `IntelligenceEvent.ToolDenied`, an internal `[JsonIgnore]` bit. `ApprenticeService` fails the step from that structured non-wire signal, never by searching tool names or result text for a denial phrase. Reasoning frames are ignored before this classification and never become Apprentice result, Chronicle, checkpoint, or denial evidence.

### 10.2.2 Semantic spell routing (pre-flight → main loop)

**Problem:** Operators want versioned markdown "spells" (workflows, checklists, personas) without pasting them into `CODEX.md`. Only one spell should apply per prompt.

**Solution — two passes:**

1. **Discovery (`SpellScanner`):** Scans `~/.config/arcanum/spells/` then the workspace for `SPELL.md` files. **Routing** uses **`ScanMetadataAsync`** (YAML frontmatter only — `name`, `description`) without reading spell bodies or `scripts/`; after **`SemanticRouter`** (or **`OverrideSpellName`**) picks a match, **`LoadFullAsync`** hydrates that spell’s full markdown, scripts list, and optional sidecar metadata. **Canonical sidecar filename is `SPELL.json`**; if absent, the scanner falls back to legacy **`SKILL.json`**; when both exist, **`SPELL.json` wins**. Creates, updates, version activation, clone, and import **write `SPELL.json` only** (they never create a new `SKILL.json`). **`ScanAsync`** (full parse) remains for spell CRUD and search APIs. Workspace spells override global spells on name collision (case-insensitive). Traversal is bounded — a canonical-path (symlink-resolved) visited set makes directory-symlink cycles terminate, plus code-owned step, depth, dependency, and declared-tool caps — and every `SPELL.md` / sidecar read is revalidated with handle-based identity (`WorkspacePathPolicy.RevalidatePathBeforeIo`), so a file whose symlink target escapes the workspace is rejected. Spell writes (`SPELL.md`, `SPELL.json`) are atomic (temp + flush + rename via `SpellAtomicFile`).

2. **Pre-flight routing — `SemanticSpellRouter` (§21.10):** `WizardIntelligenceProvider.ResolveRoutedSpellAsync` calls `SemanticSpellRouter.ResolveAsync` (scoped, Api) instead of `SemanticRouter.DetermineActiveSpellAsync` directly. `SemanticSpellRouter` decides, per turn, which of three modes applies:
   - **Disabled** (`Arcanum:Features:SemanticSpellRouting = false`, the default): returns `SpellRoutingDecisionMode.FullGrimoire` — the hub builds the router `IChatClient` (including the optional `Arcanum:FastModel` lease) and calls the static `SemanticRouter.DetermineActiveSpellAsync` with the full catalog.
   - **Pure embedding mode** (the current code-owned mode): embeds the user prompt and every spell's description (`SpellWeaveCache`, §21.10), computes cosine similarity, and returns `DirectResonance` carrying the highest-similarity spell above the internal threshold (or `null`) — **no LLM call**.
   - **Hybrid mode** (reserved internal mode): applies the same embedding similarity, but returns `FilteredDivination` carrying a code-owned top-K candidate set; the hub still builds the router client and calls `SemanticRouter.DetermineActiveSpellAsync(..., candidates: decision.Candidates)` — a reduced tools list, same JSON response protocol and timeout/fallback behavior as pure LLM routing.

   `SemanticRouter.DetermineActiveSpellAsync` accepts an optional `IReadOnlyList<SpellMetadata>? candidates = null` parameter: single `IChatClient.GetResponseAsync` with low max output tokens, zero temperature, no tools, bounded timeout, and `ChatOptions.ResponseFormat = ChatResponseFormat.Json`. The tools list offered to the LLM is `candidates ?? availableSpells`; `null` means the full catalog. The model must return a single JSON object with exactly one camelCase key `spellName` whose value is either the exact matching spell name or `NONE`; name resolution always searches the full `availableSpells` list regardless of what was offered. The hub deserializes with `JsonSerializer.Deserialize(..., ArcanumJsonContext.Default.SemanticSpellResponse)` after stripping optional markdown code fences; on `JsonException` or non-matching name, `activeSpell` is `null`. Failures and timeouts resolve to no spell. Any embedding pre-filter failure (Weave unavailable, batch/prompt embed failure, unexpected exception) falls back to `FullGrimoire` at Debug log level.

3. **Main inference:** `SystemPromptBuilder` appends `### Active Operational Spell` with the spell's full markdown, plus per-spell `#### Available Spell Scripts` when scripts exist.

**Arcane Resonance (spell dependencies):** After **`LoadFullAsync`** hydrates the primary spell, **`SpellDependencyResolver`** walks `SPELL.json` `dependencies` recursively (hard depth limit **3**, cycle- and duplicate-safe; missing names are logged and skipped). Resolved dependency markdown bodies are concatenated under `### Resonant Spells (Dependencies)` in the system prompt. Dependency edges are retained on the internal `ResolvedSpell` carrier for validation and debugging. The resolver performs its own **`ScanMetadataAsync`** pass (intentional double-scan — see `SpellDependencyResolver` source comment) so it remains self-contained when **`OverrideSpellPath`** bypasses routing's catalog scan.

**`CodexReader`:** Global and workspace **`CODEX.md`** reads are cached in a process-lifetime concurrent dictionary keyed by path; entries invalidate when **`LastWriteTimeUtc`** changes.

**`WizardIntelligenceProvider` turn context:** Each inference turn resolves campaign / Sanctum / ward settings once (`TurnContext`), precomputes the unattended filtered tool list, and passes a single serialized tool-arguments snapshot through ward and Sanctum enforcement to avoid duplicate JSON work per tool call.

**`SkipSpellRouting`:** When **`PingRequest.SkipSpellRouting`** is **`true`**, **`WizardIntelligenceProvider`** skips both **`SpellScanner.ScanMetadataAsync`** / **`LoadFullAsync`** and **`SemanticSpellRouter.ResolveAsync`** / **`SemanticRouter.DetermineActiveSpellAsync`**, sets **`activeSpell`** to **`null`**, and does not evaluate **`OverrideSpellName`**. This avoids spell disk IO, embedding cost, and router LLM cost for internal background tasks (Campaign Logger, Saga extraction). **`CodexReader.ReadCodexAsync`** still runs; with an empty **`WorkingDirectory`** (Campaign Logger), codex content is null.

### 10.2.3 Pre-flight token counting and read-time context compression

After the dynamic system prompt, rehydrated attachments, and final tool set have been materialized into the in-memory `ChatMessage` / `ChatOptions` payload, **`WizardIntelligenceProvider`** applies the code-owned **read-time compression** policy:

- **Profile resolution:** `ModelTokenEstimator` resolves model override → provider default → verified built-in canonical-model profile → conservative fallback. Built-in exact `o200k_base` recognition is limited to canonical families on the official `api.openai.com` endpoint; local/proxy aliases need an explicit exact profile. Unknown models use the greater of fallback-tokenizer count or UTF-8 byte count, then add the configured safety margin; a failed explicit tokenizer load or unavailable provider-tokenizer API is downgraded rather than silently called exact.
- **Complete accounting:** the same `ContextTokenBreakdown` shape counts system/Codex/Spell, session history and pins, current input, every text/content part, tool-call/result framing, full tool names/descriptions/JSON schemas, Lexicon/Saga, workspace and attachment RAG, explicit/refreshed attachments, structured-output schema, provider framing/stop overhead, answer reserve, and reasoning reserve. Images without a usable provider formula consume a configurable conservative reserve and carry explicit `unknown` quality; generic byte length is never labeled exact.
- **Threshold:** the complete materialized total is compared to `ContextWindowLimit(provider) * ContextWindowCompressionThreshold / 100` (both clamped). Live calls no longer skip this decision merely because a thread is short; `CompressionPreflightMinMessages` remains only for the manual compact operation.
- **Swap:** when over threshold, **`Session.Summary`** and **`Session.LastSummarizedMessageAt`** must both be present; otherwise a **warning** is logged and history is left unfiltered. When present, Grimoire entries with `CreatedAt <= LastSummarizedMessageAt` are omitted from the inference transcript and the summary is injected via **`SystemPromptBuilder.Build(..., campaignSummary: ...)`** as `### Campaign Summary (compressed context)` (see §10.5). **No `Entry` rows are deleted.** The rebuilt payload is measured again with the same profile and tool/options payload.
- **Per-call admission:** immediately before every provider call, including structured-output retries, `EnsureContextBudget` first removes the lowest-priority semantic materializations (Saga → workspace RAG → attachment RAG), then may remove oldest complete in-memory tool exchanges. It never removes accepted explicit attachments or half of a tool exchange. It finalizes one breakdown, adjusts the reservation, and passes that same object to `IModelCallExecutor` for identity validation and enforcement. This repeats after each tool result and structured-output correction, so a continuation cannot reuse an obsolete initial count; explicit content that still cannot fit returns `Hub.ContextBudgetExceeded` instead of being silently discarded.
- **Diagnostics and authority:** native streams emit `context` frames; `/api/intelligence/mana`, audit/session telemetry, Command Center `/mana`, and the non-focusable Command Center Context pane expose profile, classification, margin, and source rows. The pane renders chat history, explicit attachments, refreshed files, attachment RAG, and workspace RAG from the latest immutable call breakdown. It initially labels the total `estimated`, then replaces only the displayed total with valid provider-reported input labeled `billed` when the post-usage frame arrives. Provider-reported usage remains authoritative after a call and is attached separately with signed variance; historical reported values are never rewritten. If the per-turn materialization ledger evicts attachment/workspace semantic chunks for context pressure, the same breakdown carries aggregate dropped chunk/token counters and the pane shows a warning.
- **Delegated Mana:** `DelegatedManaTracker` is scoped through `SubagentExecutionAmbient` and charges provider-authoritative total tokens plus dynamically priced USD cost after every child model call. `BeginModelCall` enforces the delegated turn ceiling before provider I/O. Token/cost overrun is detected immediately after usage is available and terminates the child after its billable operation is recorded; `SubagentParentContextInjector` appends the exact system message `Subagent task failed: Delegated budget exhausted.` to the parent context. The tracker uses interlocked counters plus a cost lock so future parallel children cannot race the ceiling.
- **Native AOT:** tokenizer creation uses the **`Microsoft.ML.Tokenizers.Data.O200kBase`** data assembly; all new wire/audit/config contracts are source-generated and linker-safe.

**Child context isolation:** `SubagentRunner` creates a new stateless request containing exactly one code-owned isolated system instruction, the delegated user prompt, and explicit file values. It carries no parent `SessionId`, transcript, `ContextSnapshot`, Chronosync delta, campaign, data streams, workspace root, Codex, session pins, Lexicon, Saga, attachment index, or semantic retrieval. When parent attachment content was materialized, every delegated file must name an attachment id in the parent turn's materialized allowlist; the effective child permission is therefore the intersection of parent authority and the child request. Child tools are disabled. `SubagentExecutionAmbient.MaxSubagentDepth = 1` prevents child-to-grandchild delegation even if a tool surface is accidentally widened later.

### 10.2.3.1 Performance findings

Closed audit items (writer reuse, scan/cache bounds, Loremaster counter, MCP line reader, trust digest LRU, `/api/meta` handles, Apprentice jitter) are implemented; acceptable-as-is notes live in code comments.


### 10.2.4 Scrying — the vision/multimodality capability gate

**Model capability declaration:** each `Arcanum:Providers[].models` entry is a **`ModelEntry`** (`Name`, `SupportsVision`, optional `Reasoning`); the JSON binder (`ModelEntryJsonConverter`) accepts either a bare string (optional capabilities absent) or an object. `ProviderResolver.SupportsVision(ArcanumSettings, string?)` / `SupportsVision(ProviderSettings, string?)` resolve capability by exact (case-insensitive) model-name match against configured `models` entries.

**Gate placement — before any inference token:** `ScryingValidator` (`Core.Intelligence`) is the single validation surface shared by every inference entry point:

- `RequestContainsImages(PingRequest)` — scans `StatelessMessages[].ContentParts` (kind `image_url`) and `ScryingFoci`.
- `ValidateRequestImages(PingRequest, ScryingSettings)` — when images are present: `Scrying.Enabled` (else `Scrying.FeatureDisabled`, 403), per-request image count vs `MaxImagesPerRequest` (else `Scrying.TooManyImages`, 400), and — **for `data:`-URI images only** (native `ScryingFoci` and any `data:`-URI `image_url` part) — MIME allow-list (`Scrying.UnsupportedMimeType`, 400) and decoded byte size vs `MaxImageBytes` (`Scrying.ImageTooLarge`, 413). `http(s)` URL images are counted toward the cap but not size/MIME-checked — the downstream provider fetches and rejects them, avoiding a HEAD-request side-channel and added latency.

**`WizardIntelligenceProvider`** (`ExecutePromptAsync` and `StreamPromptAsync`) runs `ValidateScryingGate` immediately after `PingRequestBoundsValidator.Validate` and before model-lease resolution: it short-circuits when the request carries no images, otherwise runs `ScryingValidator.ValidateRequestImages` and then resolves the intended model via `ProviderResolver.TryResolveProviderForModel` (the same no-resilience resolution used elsewhere) purely to check `SupportsVision` — failing `Scrying.VisionNotSupported` (400) when unsupported. This is a client-input mismatch, not a provider-connectivity concern, so it is **never retried across resilience fallback candidates**; a model-resolution failure here is not itself an error (the existing `Hub.Model` path reports it later). This single gate covers `POST /api/intelligence/ping(-stream)`, spell/prompt execute routes, Unseen Servant daemon jobs, and Apprentice step execution — all route through `WizardIntelligenceProvider`.

**`OpenAiV1Endpoints`** (`/v1/chat/completions`) runs the equivalent gate independently, before the shared provider is called: after resolving `ProviderSettings`/canonical model, it checks `ScryingValidator.RequestContainsImages(ping)` on the mapped `PingRequest`, then `ScryingValidator.ValidateRequestImages`, then `ProviderResolver.SupportsVision(resolvedProvider, resolvedModel)` — returning an OpenAI-shaped `400 invalid_request_error` (`code: "vision_not_supported"`) or `403` (`code: "feature_disabled"`) as appropriate, before any inference call. This means the `WizardIntelligenceProvider`-level gate is a defense-in-depth backstop for `/v1`, not the primary enforcement point for that surface.

**Multimodal content mapping (`InferenceContextBuilder`):** `image_url` parts map to `Microsoft.Extensions.AI` content based on URI scheme — `data:` URIs decode to `DataContent` (raw bytes + parsed MIME) so the provider receives the actual payload; `http(s)` URIs map to `UriContent` unchanged (provider fetches). Native `PingRequest.ScryingFoci` / `AttachedFiles` are appended as `DataContent` / text onto the current turn's final message in `BuildInitialMeAiChatMessages`. When **`Arcanum:Features:Attachments`** is enabled and the host attachment store path is active (Command Center + serve host), those foci/files are **persisted before the model call** as session attachments — bytes under `~/.config/arcanum/attachments/` plus `SessionAttachments` Grimoire metadata (§10.2.5). **`arcanum chat`** (and frameless `ask` staging) remain **ephemeral in this pass** — threaded onto the in-memory chat message list only; `Entry` rows still store text content only.

**Multimodal token accounting:** an image is charged by a configured/provider formula only when that formula is actually available. Otherwise `UnknownImageTokenReserve` (or the profile override) is applied per image with `unknown` classification and reduced confidence. Arcanum never derives or reports an exact image-token count from encoded byte length.

**Configuration and errors:** see §3.4 (`Arcanum:Features:Scrying`, `Arcanum:Features:Attachments`, and `Arcanum:Security:AllowedImageMimeTypes`) and §8.23 (the preserved `Scrying.*` error codes).

### 10.2.5 Session attachments (disk + Grimoire pointers)

**Purpose:** Persist text attachments and Scrying images across Command Center turns so conversations can list, Reveal, re-attach, and let the model re-attach — without storing blobs inside SQLCipher.

**Ownership:** host-only `ISessionAttachmentStore` (serve process). CLI stages content via `PingRequest.AttachedFiles` / `ScryingFoci` / `AttachmentReferences`; the host re-validates and persists **before** inference (failure aborts the turn — the model never sees an attachment that did not persist). Listing: `GET /api/sessions/{id}/attachments` first revalidates every tracked source off the UI thread, persists its latest sanitized observations/status, and then returns **bound** rows only.

**On-disk layout** (`ArcanumPaths.AttachmentsDirectory` → `{GrimoireDirectory}/attachments/`):
`_pending/{turnId}/{logicalKey}/v1/{originalFileName}` until session-bound; then
`{sessionId:N}/{logicalKey}/vN/{originalFileName}`. The filename is a metadata-derived locator;
the file content is an `ARCABLOB` authenticated-encryption envelope, not plaintext. Owner-only
permissions remain defense in depth. Dedupe uses the plaintext SHA-256 retained inside SQLCipher
(identical bytes → reuse id, no new `vN`).

**System prompt index:** metadata-only `### Session Attachments Index` (bounded by `MaxIndexItemsInPrompt` / `MaxIndexBytesInPrompt`); no bytes. Model pulls snapshot content via MCP `attach_session_file`, requests the verified live source via `refresh_session_file`, or the operator uses `/attachments add`.

**Semantic attachment retrieval:** opt-in `Arcanum:Features:AttachmentRetrieval` indexes every eligible Bound version through `SessionAttachmentIndexingService` after creation, promotion, refresh, or fork. The processor reads plaintext only through `ISessionAttachmentStore.ReadBytesAsync`, which opens the final authenticated encrypted-blob abstraction. Default inference retrieval is scoped to the bound `SessionId` and only chunks from the newest Bound version of each logical key carry that session's `RetrievalScope`; historical chunks remain durable and require explicit historical search. Retrieved excerpts are injected as adaptive-fenced untrusted DATA under `### Retrieved Session Attachment Context`, directly after current-turn attached-file content and before the metadata index and workspace semantic context. Each excerpt carries a sanitized filename, logical key, version, opaque attachment id, character/line range, content hash, similarity, and an explicit untrusted-DATA warning. The DTO status is one of `NotEligible`, `Pending`, `Indexed`, `Failed`, or `Stale`.

**Unified turn materialization ledger:** one non-persisted `ContextMaterializationLedger` is created per logical turn and shared with the existing model/tool loop through a turn-local ambient reference. Stable identity is source kind + opaque source id + version/content hash + whole/chunk range. Entries record explicit/model/semantic origin, sanitized label, content hash, estimated tokens, materialized bytes, trust classification, injected state, and provider round. Deterministic priority is current-turn attachments → explicit attachment references → explicit context pins → `attach_session_file` → `refresh_session_file` → attachment RAG → workspace RAG → Saga. Identical content/ranges across paths are admitted once; an explicit whole version suppresses its semantic chunks; refresh replaces older-version semantic chunks before the continuation call. Failed materialization is never recorded, and the ledger is cleared at logical-turn finalization.

**Retrieval bounds:** attachment semantic admission clamps `MaxRetrievedChunks` (1–50), `MaxRetrievedAttachments` (1–100), `MaxRetrievedBytes` (1 KiB–16 MiB), and `MaxRetrievedTokens` (128–1,048,576), in addition to the shared 0–1 similarity threshold. Explicit sources are admitted first and do not consume these semantic limits.

**Turn budget / injection:** the code-owned per-turn reference cap is shared by user `AttachmentReferences` and model `attach_session_file` / `refresh_session_file` injections. The unified ledger performs inject-once by stable identity and content/range across every explicit and semantic path; subsequent tool rounds cannot re-inject the same materialization. Image re-attach or refresh requires `Arcanum:Features:Scrying`, an allowed image MIME, and a model with `SupportsVision`; oversize images are **rejected, never truncated**.

**Model tool:** `attach_session_file` is an **internal MCP** tool (attunement-aware). After a **successful** call (`!Failed && !Denied` — Ward/Sanctum denials and tool failures do not inject), a dedicated post-tool path materializes `TextContent` / `DataContent`, then atomically consumes the turn budget / inject-once mark, and queues content for the **next** inference round. User extras from a multi-tool model response are appended **only after** every tool call and tool result from that round are on the transcript (never interleaved between tool exchanges). Injected/rehydrated text is framed as untrusted DATA (adaptive fences); attachment headings harden hostile path characters. Unexpected post-processing failures follow the code-owned tolerant mode policy and never partially inject.

`refresh_session_file` is the corresponding host-authorized live-source tool. Its hand-authored schema accepts exactly one `attachmentId` or `logicalKey`; session, source path, model, campaign, assistant Entry, and turn-visible attachment set are host-owned. It resolves only a Bound current-session attachment visible when the logical turn began. A case-insensitive logical-key match that names more than one case-distinct key fails as ambiguous. Snapshot-only, missing, inaccessible, unsafe, changed-workspace, or corrupt provenance fails closed with a structured result.

The source resolver reconstructs only the stored workspace-relative provenance, verifies workspace identity and lexical/canonical containment, rejects a changed symlink target, compares path and open-handle identities, applies Sanctum to the actual canonical source, and reads from that handle under a kind-specific byte cap. It reads the handle twice and requires identical hashes so a file changing during the read is rejected. MIME and strict UTF-8/Scrying/vision policy are reapplied before persistence. A hash matching the latest Bound version reuses its row and encrypted blob; changed bytes use the existing session/logical-key gates to enforce `MaxBytesPerSession` and `MaxVersionsPerLogicalKey` while atomically inserting the next version with current hash, MIME, length, source observations, timestamp, and assistant Entry binding.

The structured result reports attachment id, logical key, version, creation/queue booleans, sanitized relative source path, hash, byte length, freshness time, and bounded error information. Refreshed text is untrusted DATA labeled with sanitized filename, logical key, version, and freshness. The original user Entry is never rewritten. `refresh_session_file` participates in Artifact Attunement and `ToolPolicy.ReadOnlyTools`; it is not intrinsically Warded or a default Forbidden Art, but operator-configured Forbidden Arts still apply. Sanctum always evaluates the hidden resolved source when a campaign exists. Successful refreshes emit native `attachmentRefreshed`; OpenAI projections intentionally ignore it.

**Command Center state and manual refresh:** the attachment list maps snapshot-only provenance to `[Snapshot]`, verified matching workspace provenance to `[Live]`, and every other tracked-source condition to `[Stale]`. Each row renders the version and the snapshot `ContentSha256` that is loaded into model context; tracked rows also render the last backend-observed disk hash and write time. `CommandCenterAttachmentDriftMonitor` uses one recursive `FileSystemWatcher` over the active working directory, debounces create/change/delete/rename events, and calls the listing endpoint. It never hashes a file or promotes a badge locally. `/attachments refresh <logicalName>` resolves the latest visible row, calls `POST /api/sessions/{id}/attachments/{attachmentId}/refresh`, and prints `[Live]` only from the returned backend confirmation. The endpoint calls `ToolExecutionPipeline.RefreshSessionAttachmentAsync`, which shares selector ownership, source resolver, Sanctum, MIME/size policy, version gates, encrypted persistence, and indexing enqueue with `refresh_session_file`; because no model turn is active, it does not queue content injection.

**Metadata invariants:** `SessionAttachments` is installed by the embedded
`20260719180000_AddSessionAttachments` script and accessed through scoped
`ArcanumDbContext` raw SQL + `SqliteBusyRetry`, not an EF `DbSet`.

| Column group | Bound attachment | Pending attachment |
|--------------|------------------|--------------------|
| `Id` | set | set |
| `SessionId` | non-null | null |
| `EntryId` | set when the user Entry is known; otherwise nullable | null |
| `PendingTurnId` | null | non-null |
| `State` | `Bound` | `Pending` |
| `LogicalKey`, `OriginalFileName`, `Version`, `RelativePath`, `ContentSha256`, `MimeType`, `ByteLength`, `Kind`, `CreatedAt` | populated | populated |
| `SourceKind`, `SourceStatus` | `SnapshotOnly`/`NotApplicable` unless host-verified | same |
| `SourceWorkspaceIdentity`, `SourceRelativePath`, `SourceCanonicalPath`, `SourceContentSha256`, `SourceFileIdentity`, `SourceLastWriteAt`, `SourceByteLength`, `SourceDiagnosticReason` | optional encrypted provenance | optional encrypted provenance |
| `EncryptionVersion`, `EncryptionKeyId` | current envelope version/key id | current envelope version/key id |

**Source provenance and refreshability:** attachment bytes always remain a durable snapshot; the
existing attachment-store `RelativePath` always points to that snapshot and is never reinterpreted
as an original file locator. A host-trusted caller may supply an `AttachmentSourceClaim` to
`PersistNewFromSourceAsync`. The scoped `IAttachmentSourceResolver` accepts it only when the active
configured workspace exists and the source passes lexical containment, canonical/symlink
containment, pre-open path identity, post-open handle identity, and immediate pre-I/O revalidation.
It hashes the bytes read from the verified handle. Matching bytes produce `Refreshable`; differing
bytes are retained as a safe `PriorVersion` snapshot. Ordinary native/API `AttachedFileDto` paths
are untrusted labels and always persist as `SnapshotOnly`; remote clients cannot nominate a live
host path.

Public `SessionAttachmentDto` exposes only source kind/status, refreshability, opaque workspace
identity, sanitized workspace-relative path, safe hash/time/length observations, and a bounded
diagnostic reason. `SourceCanonicalPath` and file identity remain encrypted raw-SQL metadata and
are never returned by the API. On restart reconciliation revalidates workspace identity,
containment, file identity, availability, and observed content. Missing, moved, inaccessible,
unsafe, changed-workspace, or corrupt metadata changes source status without deleting the row or
snapshot bytes. Forks copy source metadata with the snapshot and revalidation applies in the fork.
Watcher-based rename repair is an optional future optimization; correctness never depends on a
watcher.

The complete per-round ordering is maintained in [Arcanum.CHAT-LOOP.md](Arcanum.CHAT-LOOP.md): a
tool result cannot alter the provider request that produced it, so refreshed content is appended
only to the next request in the same logical turn and only after the round's complete tool transcript.

The source columns are part of the canonical hand-authored
`20260719180000_AddSessionAttachments.sql` table definition and remain outside the compiled EF
model. This release intentionally does not ship an upgrade migration: installations with an older
Grimoire schema must recreate the database when installing the latest version.

**Lifecycle and consistency:**

- Before session binding, bytes and rows live under `_pending/{turnId}` with `State=Pending`.
  `SessionBound` / the first persisted user Entry promotes by copying bytes into the Session tree,
  then updates the rows to Bound in a DB transaction. This is not an atomic filesystem move.
- Persistence completes before model inference; failure closes the turn before the model sees bytes.
- Fork pre-copies ciphertext, authenticates every chunk, and verifies the decrypted plaintext hash,
  then inserts the new Session, Entries, and attachment rows in one EF ambient transaction with raw
  SQL enlisted. A failed DB transaction deletes the partial fork tree.
- Hard purge deletes attachment rows with Session/Entry rows in one DB transaction, then
  best-effort removes the Session attachment tree under an independent cleanup token. A failed
  filesystem delete is logged and left for reconcile.
- Hard-deleting an Entry sets matching `SessionAttachments.EntryId` to null in the same transaction;
  the Bound row and bytes remain owned by the Session.

`SessionAttachmentPendingGcHostedService` reconciles once at startup. It removes stale Pending rows
and matching pending directories after a code-owned retention window; removes Bound rows whose
Session no longer exists before best-effort directory cleanup; deletes rows for missing/escaping
files; and deletes unreferenced files under the attachment tree. Invalid `_pending` child names are
warned and left untouched rather than passed to an identity-unsafe delete. Code-owned per-Session
byte and per-logical-key version caps reject new writes; Bound files are not background-pruned.
Reconciliation also fails closed when source metadata is malformed or no longer resolves, updating
only its availability/status fields and preserving an otherwise valid attachment snapshot.
Encrypted-file validation authenticates every referenced snapshot; legacy plaintext and corrupt
envelopes are logged and surfaced by health/doctor rather than ever being returned as attachment
content.

**Privacy:**

| Layer | Protection |
|-------|------------|
| Grimoire metadata (`SessionAttachments`) | SQLCipher-encrypted (same as other Grimoire tables) |
| Attachment **bytes** on disk | Chunk-authenticated AES-256-GCM `ARCABLOB` envelope plus owner-only permissions under `~/.config/arcanum/attachments` |
| File-encryption master key | OS credential store `arcanum/file-encryption-master-key`; DP-sealed recovery mirror in `file-encryption-key.dat` |
| OS disk encryption / backup | Operator responsibility |
| Full conversation continuity | Copy/restore attachments, DB, file-encryption key or recovery mirror, and DP key ring as one generation |

Deleting or reinstalling only `arcanum.db` leaves orphan encrypted attachment bytes. A full backup,
restore, reset, or uninstall must copy/remove `~/.config/arcanum/attachments` with the database and
must preserve the matching file-encryption key material described in §5.4.6. This tree is distinct
from `/v1/files`, whose encrypted envelopes use `files/{guid}`. Configuration authority remains the
Compendium reference linked from §3.4.

### 10.2.6 Structured mentions and durable context pins

`SessionContextPinKind` is the closed structured-mention vocabulary: `File`,
`DirectorySnapshot`, `SymbolRange`, `SessionEntry`, `Attachment`, `Url`, and `Diagnostic`.
The existing free-text `@path` staging contract remains backward compatible and turn-scoped; pins
are an additive, explicit session facility. Command Center exposes the vocabulary in `/help` and
provides `/context [list]`, `/context pin <kind> <target>`, and `/context unpin <pin-id>`.

The raw-SQL `SessionContextPins` table stores the session id, kind, stable target identifier,
display label, optional content hash/version, and created/updated timestamps. The unique key is
`(SessionId, Kind, TargetIdentifier)`, so pinning an existing target updates metadata without
duplicating it. The foreign key cascades on session deletion. This table is intentionally absent
from the compiled EF model and is installed by
`20260730010000_AddSessionContextPins.sql`; `ISessionContextPinStore` owns access.

Materialization happens afresh before every bound inference turn:

- File, directory, and symbol/range paths are resolved relative to `PingRequest.WorkingDirectory`.
  Lexical workspace containment and final symlink-target containment both fail closed. Missing
  sources produce a labeled `Missing` block. A file whose SHA-256 differs from the optional pinned
  version produces `Modified` and injects only the current bytes with the new hash disclosed; old
  bytes are never cached in the pin row.
- Directory snapshots are deterministic ordinal path/size listings, never full recursive content,
  and stop at 64 files. Symbol ranges require `path:start-end` and stop at 2,000 lines.
- Entry targets must parse as an entry id belonging to the same session. Attachment targets accept
  an attachment id or logical key and must resolve to a bound text attachment in the same session.
  Diagnostic text is stored as the stable target itself. URL metadata may be pinned and listed, but
  implicit materialization reports `Unsupported`; URL retrieval must enter through guarded web
  browsing and never an unrestricted `HttpClient`.
- Each pin is limited to 64 KiB, each turn to 32 pins and 256 KiB. Truncation and omitted-pin counts
  are explicit. These appended `TextContent` parts flow through the normal model-aware context/mana
  estimator, so their tokens are visible without a parallel estimate.

Every materialized item is surrounded by an adaptive backtick fence and explicit source kind,
label, stable id, freshness status, and diagnostic fields inside an
`UNTRUSTED SESSION CONTEXT DATA` envelope. Models must treat these bytes as data, never
instructions. A single failure becomes an error/status block and cannot mutate or corrupt the
session. `Entries.IsPinned` remains the independent transcript-compression contract: context pins
neither set it nor change which historical entries compression retains.

### 10.3 Registration lifetimes

`IArcanumIntelligenceProvider` / `WizardIntelligenceProvider` are **scoped** (one instance per request scope). `IChatClientFactory` is **singleton**; each call to **`ResolveClientAsync`** returns a **`ChatClientLease`** that owns a fresh `IChatClient` for that inference turn over the named OpenAI-compatible `HttpClient` pipeline.

### 10.4 Telemetry and observability

`TelemetryService` subscribes to `ArcanumMetrics` (`Meter` from `System.Diagnostics.Metrics`) and produces process-level `TelemetrySnapshot` aggregates. Each child terminal transition calls `ISubagentTelemetrySink.RecordSubagentRun` once, atomically rolling run outcome, delegated tokens/cost, and latency into `TelemetrySnapshot.Subagents`; raw child turns are not telemetry events. Command Center's non-focusable `TelemetryPane` consumes the native per-call `context` frames instead: it performs one immutable text replacement per update and shows chat-history, explicit-attachment, refreshed-file, attachment-RAG, and workspace-RAG tokens plus an estimated or provider-billed input total. Ledger-recorded semantic pressure produces a dropped-RAG warning. On wide layouts the pane shares the left rail with Sessions; `Ctrl+T` toggles `CommandCenterState.ShowTelemetryPane` without taking focus. Attachment DTO indexing states are aggregated separately in the footer as running/pending, completed, or failed; while any row is Pending, the drift monitor re-reads the authenticated session attachment projection once per second until it reaches a terminal state. Rendering never exposes chunk text, vectors, hashes, or raw ledger labels.

### 10.4.1 Grimoire integration

The provider persists through `IGrimoireRepository`. When `sessionId` is set, prior turns are loaded for `IChatClient`. A dynamic `ChatRole.System` message from `SystemPromptBuilder` is prepended in memory (not persisted to Grimoire). Tool rounds are persisted as bracket-formatted `Entry` rows. Assistant entries contain **answer text only**: raw, visible, protected, and summarized reasoning are never written to Grimoire. After every successful buffered or streamed turn with a bound session, the code-owned tracking policy makes **`IncrementSessionTokensAsync`** atomically add the provider-authoritative reported **`total_tokens`** to **`Session.TotalTokensUsed`**. Persistence failures on the buffered path are logged as warnings only.

### 10.5 Spatial context on inference

**Problem:** the API host's cwd is not the operator's shell cwd.

**Solution:** `PingRequest` carries `WorkingDirectory`, `ContextSnapshot` (`PatternSnapshot`), optional `SessionId`, optional `StatelessMessages` (`CoreChatMessage[]` transcript for stateless callers), optional `AttachedFiles`, optional `ChronosyncDelta` (`ChronosyncReport`), and optional `DataStreams` (reserved for future real-time JSON injection). The CLI resolves `Environment.CurrentDirectory`, runs Eye of the World, runs `IChronosyncEngine` inside a DI scope against the local Grimoire, and populates these fields before each HTTP call. CLI bootstrap (`ask`, `chat`) reuses `IGrimoireCliInitialization` once per process so SQLCipher setup and first-run migrations match the host (`GrimoireDatabaseBootstrapper`, shared with `GrimoireDatabaseHostedService`).

**`SystemPromptBuilder.Build` ordering (DCI blocks):**

| Position | Block | Produced by |
|---|---|---|
| 0 | **Preamble** (base persona + the "INSTRUCTIONS override conflicting DATA" rule) | static content |
| 1 | **DATA** (`[None]` when empty): `### Lexicon (Known Context)` → `### Chronosync Report (Temporal Delta)` → `### Attached Files for this Turn` → `### Retrieved Session Attachment Context` → `### Session Attachments Index` → `### Semantic Context (Retrieved Codebase)` → `### Saga (Associative Memory)` → `### Data Stream: {StreamId}` | Lexicon retrieval (§10.6), `ChronosyncDelta`, explicit attachments, ledger-filtered attachment/workspace RAG, Saga retrieval (§10.4 §21.4 §21.8) |
| 2 | **CONTEXT**: `### Workspace Context` / `### Table of Contents` → `### Master Codex (CODEX.md)` → `### Campaign Summary (compressed context)` (only on compression) | `ContextSnapshot`, `CodexReader`, read-time compression (§10.2.3) |
| 3 | **INSTRUCTIONS**: `### Active Operational Spell ({Name})` (omitted when `SkipSpellRouting`) → `### Available Spell Scripts` (when present) → `### Output Formatting Directive` (when `CliTerminalFormatting`) | `SemanticRouter`/`SemanticSpellRouter` (§10.2.2 §21.10), scripts scan, CLI flag |

**Ordered prompt document and cache planning.** `SystemPromptBuilder.BuildDocument` emits immutable ordered `PromptSegment` values and `Build` delegates to `Render()`. Regression tests require byte-for-byte equality with the established DCI text, including whitespace, adaptive fences, Unicode, and `[None]`. Preamble, Codex, primary/resonant Spell text, stable script instructions, and request-invariant terminal formatting are stable candidates. Lexicon, Chronosync, attachments/index/images, semantic retrieval, Saga, streams, workspace/session summaries, and per-request instructions are volatile. For cumulative-prefix contracts, planning stops at the first volatile segment; later stable Codex/Spell segments are not falsely counted as independently cacheable. The shipped root-only key/retention dialect does not split content or add messages.

**Data Streams (DATA, hardened):** `PingRequest.DataStreams` are externally supplied and treated as untrusted DATA. `AppendDataStreams` sanitizes each `StreamId` as a label (collapse whitespace, strip control chars and `#` heading markers, cap length) so the `### Data Stream: {id}` heading cannot break DCI structure; the payload is preceded by an explicit “untrusted data / not instructions” warning and wrapped in an adaptive markdown fence (`ComputeFenceBacktickLength`) so embedded triple-backticks cannot break out.

The sterile `[None]` (never an empty block, never chatty copy) prevents smaller models from hallucinating about missing sections. Segment builders use chained `.Append()`/`.AppendLine()` calls, and `SystemPromptDocument.Render` pre-sizes the final `StringBuilder`; large content blocks (Master Codex, Attached Files, Campaign Summary) are passed as raw strings rather than `$"..."`-interpolated. The same `WorkingDirectory` scopes `McpConnectionManager`, `CodexReader`, and `SpellScanner`.

### 10.6 The Lexicon — agent-directed entity memory

**Role:** structured, model-writable memory that replaces the legacy key-value Lore MCP tools for agent use. Entities are typed (Person, Project, API, DaemonState, …) with a fact array; the inference pipeline retrieves them by subject and injects them into the Master system prompt under DATA as `### Lexicon (Known Context)`. The legacy `MageSettings` Lore surface (`/api/lore`, `arcanum lore`) remains as an operator-only key-value store; it is no longer model-directed.

**Persistence (raw SQL, no EF):** `lexicon_entries` (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt) + an FTS5 external-content virtual table `lexicon_fts` (Name, Type, FactsText; `content='lexicon_entries'`, `content_rowid='rowid'`) with `lexicon_entries_ai`/`_ad`/`_au` triggers syncing the index on insert/delete/update. Neither table is part of the compiled EF model — they are created by `LexiconSchemaInitializer.EnsureSchemaAsync` at Grimoire bootstrap (alongside `WeaveSchemaInitializer`) and accessed via `LexiconService` over the scoped `ArcanumDbContext` connection + `SqliteBusyRetry` + `DbCommand.CreateParameter()`, mirroring `SagaMemoryStore` / `SanctumBreachRepository`. No EF migration, no compiled-model regeneration.

Initialization uses idempotent `CREATE ... IF NOT EXISTS`. Failure is logged and swallowed so this
optional memory cannot prevent host startup; reads degrade to empty and writes to logged failures
when the schema is unavailable. The FTS triggers copy `Name`, `Type`, and the newline-joined
`FactsText` projection (using the FTS5 delete command for the old row); SQLite never parses
`FactsJson`, which remains the source-generated durable fact array. Existing operator
`MageSettings` Lore rows are not migrated into the net-new Lexicon.

**Write path (`scribe_lexicon` / `delete_lexicon` MCP tools):** upsert by `NameNormalized` (trim + invariant) under `BEGIN IMMEDIATE` so concurrent appends cannot lose facts; append non-duplicate facts, cap counts/lengths (`LexiconLimits`); `FactsJson` is serialized via the source-generated `LexiconJsonContext` (AOT), `FactsText` is newline-joined for FTS. Type semantics: new + blank → `General`; existing + blank → keep; non-empty → refresh. `delete_lexicon` is a Forbidden Art; `scribe_lexicon` is ungated. Both follow `Arcanum:Features:Lexicon`.

**Read path (preflight + retrieval):** `SemanticRouter` now returns `SemanticSpellRoutingResult(Spell, Entities)` — the JSON contract is `{ "spellName": "...", "entities": [...] }`. When the router ran but supplied no entities (or routing was bypassed: `OverrideSpellName`, pure embedding `DirectResonance`, no-spell user-facing turns), `WizardIntelligenceProvider` runs `LexiconEntityExtractor` — a low-token JSON preflight on the fast model (`{ "entities": [...] }`) — so memory retrieval stays available even when spell selection avoided an LLM call. The `ShouldUseLexiconForTurn` gate skips only true internal headless tasks (`SkipSpellRouting && DisableMcpTools && UnattendedMode` — Campaign Logger, Saga extraction). `MatchEntitiesAsync` is tiered: exact `NameNormalized IN (...)` hits first, then column-weighted FTS5 `MATCH 'Term' ORDER BY bm25(lexicon_fts, 3.0, 2.0, 1.0) ASC` (3.0 Name, 2.0 Type, 1.0 FactsText — no Lucene caret boosting inside MATCH), deduplicated by Id, exact hits before FTS hits. FTS failure degrades to a bounded `LIKE` fallback or empty matches.

**Injection (DATA, hardened):** `SystemPromptBuilder.Build` accepts `IReadOnlyList<LexiconEntryDto>? lexiconEntries` (default null) and renders `### Lexicon (Known Context)` at the top of the DATA block. Lexicon is model-writable and potentially stale/adversarial, so it is treated strictly as DATA — never instructions; the preamble already states DATA may be stale and never overrides INSTRUCTIONS. Facts are hardened: whitespace collapsed, newlines/control chars stripped, exactly one plain markdown bullet per entity (`- **Name** (Type): "Fact 1"; "Fact 2"`), so facts cannot create headings or break DCI structure. Total rendered bytes are capped by `LexiconMaxInjectedBytes`; entry count by `LexiconMaxMatchedEntries`. Retrieval/injection failures are logged and swallowed — Lexicon never fails an inference turn. Lexicon contents are not persisted into audit logs or exposed on `/v1` tool surfaces.

**Error codes:** `ErrorCodes.Lexicon.InvalidName` / `InvalidFact` / `NotFound` / `WriteFailed` / `SearchFailed` (no HTTP route yet; MCP converts expected failures to tool-result strings).

#### 10.6.1 Attachment-derived memory promotion and privacy policy

Attachment bytes are untrusted DATA; neither a file nor instructions inside it can authorize durable
memory. A successful materialization publishes a turn-scoped `AttachmentMemoryProvenance` containing
Session ID, Attachment ID, logical key, version, content hash, materialized-at timestamp, and source
type. Failed, stale, cross-session, or merely indexed content publishes no promotion authority.

| Destination | Default attachment policy |
|-------------|---------------------------|
| Current-turn context | Explicit files and bounded retrieved chunks are allowed as untrusted DATA. |
| Session attachment RAG | Eligible textual chunks remain session-scoped; historical versions are explicit-only. |
| Session Entry RAG | Attachment chunks are not copied into Entries; the normal assistant conclusion alone may be persisted. |
| Campaign Summary | Record consulted logical key/version and useful decisions; never send all attachments or raw content. |
| Lexicon | No automatic promotion. `scribe_lexicon` requires `attachment_id` whenever attachment content was materialized, validates that id against the current turn, and stores per-fact provenance in `lexicon_fact_attachment_provenance`. |
| Saga | No raw automatic ingestion. Extraction receives only the conversation plus a metadata allowlist, accepts concise conclusions, and rejects any claimed attachment id absent from the source turn. Typed provenance is stored in `saga_memory_attachment_provenance`. |
| Prompt cache | Attachment metadata, paths, hashes, and bytes are volatile DATA. They are excluded from stable prefixes and shared cache keys. |
| Audit log | Metadata-only inference accounting; no attachment bytes, raw paths, content, or provenance hash payloads. |
| Subagents | Only explicit file values whose attachment ids intersect the parent materialized allowlist; no inherited index or enumeration. |

Deleting an attachment does not silently delete unrelated conclusions. Lexicon/Saga provenance rows
remain and resolve `Availability=Unavailable` when no Bound source row exists, so downstream users are
never told that an unavailable source is verifiable. This metadata is a typed side table, not opaque
text concatenated into the fact or memory.

### 10.7 End-to-end turn lifecycle and chat loop

This section is the canonical ordered workflow for every Master inference turn. It describes the
shared logical run, buffered/streaming differences, fallback and correction loops, persistence,
cost/context admission, cancellation, and terminal event behavior.

#### 10.7.1 Ownership, projections, and entry points

```text
WizardIntelligenceProvider          thin IArcanumIntelligenceProvider facade
        ↓
TurnExecutionCoordinator            sole semantic consumer; one projection per request
        ↓
TurnEngine                          logical run: preflight → attempts → loop → finalization
        ↓
TurnEventEmitter                    ordered internal Channel<TurnEvent>
        ↓
Exactly one projection
  ├── BufferedTurnProjection        → Result<PromptTurnResult>
  ├── IntelligenceEventProjection   → Channel<IntelligenceEvent>
  └── OpenAiSseProjection           → semantic helper/characterization only
        ↓
HTTP writer
  ├── native NDJSON
  └── production /v1 IntelligenceEvent → OpenAI SSE mapping
```

`ExecutePromptAsync` and `StreamPromptAsync` create an internal `TurnExecutionRequest`.
`HasIdempotencyKey` comes only from `TurnIdempotencyAmbient`, which the HTTP idempotency filter sets;
it is never accepted from a public `PingRequest` body. The coordinator consumes semantic events and
selects exactly one projection; it does not serialize HTTP. Native routes serialize
`IntelligenceEvent` as NDJSON. Production `/v1/chat/completions` receives those same native events
and reshapes them in `OpenAiV1Endpoints`, whose mapper owns terminal usage and tool-argument
fragmentation. `OpenAiSseProjection` shares reasoning/error characterization but is not the
production projection instance. SSE keep-alives are transport-only, and exact-byte replay capture
stays in the HTTP writer/idempotency layer.

Semantic events are internal and non-durable. Every writer, including Ward and human-input
observers, uses the bounded `TurnEventEmitter` channel; sequence numbers are strictly monotonic,
request events are emitted before waiting, and the terminal guard suppresses everything after the
first terminal event. The current event vocabulary is:

```text
RunStarted, TurnStatusChanged, SessionBound, ContextCompressed, ContextAccounted,
ProviderAttemptStarted, ProviderSelected, ProviderAttemptCommitted,
ProviderAttemptCompleted, ProviderAttemptFailed,
ModelCallStarted, TextDelta, ReasoningDelta, ModelCallCompleted, ModelCallFailed,
ToolCallProposed, ApprovalRequested, ApprovalResolved,
HumanInputRequested, HumanInputReceived,
ToolInvocationStarted, ToolInvocationCompleted,
OutputValidated, RunCompleted, RunFailed, RunAbandoned
```

The logical-run invariants are:

- exactly one `RunStarted` and exactly one terminal `RunCompleted`, `RunFailed`, or
  `RunAbandoned`;
- every provider attempt starts once and ends completed or failed; at most one attempt commits, and
  commitment precedes provider-derived client-visible events;
- every model call starts once and ends completed or failed;
- approval, human-input, and tool-invocation pairs complete unless terminal cancellation
  interrupts them;
- answer and reasoning remain distinct through execution, events, projections, validation, and
  persistence; provider `ProtectedData` never becomes an event;
- tool calls remain sequential within a round, call/result groups remain paired, and attachment
  injection occurs only after all exchanges in that round;
- client-forwarded calls bypass server authorization and invocation; and
- run, Grimoire, reservation, accounting, idempotency, finalization, and provider-lease ownership
  execute at most once.

`RunCompleted` is the authoritative buffered result: final answer, ordered client-safe reasoning,
usage, observed tool calls, finish reason, warnings, Session id, and structured-output warning
state. Keep-alives and captured replay bytes remain transport concerns, never semantic events.

| Surface | Current route | Turn call |
|---------|---------------|-----------|
| Buffered native ping | `POST /api/intelligence/ping` | `ExecutePromptAsync` |
| Streaming native ping | `POST /api/intelligence/ping-stream` | `StreamPromptAsync` through `InferenceExecuteWriter` (NDJSON) |
| Spell execution | `POST /api/spells/{name}/execute` | `ExecutePromptAsync` |
| Streaming Spell execution | `POST /api/spells/{name}/execute-stream` | `StreamPromptAsync` through `InferenceExecuteWriter` |
| Prompt execution | `POST /api/prompts/{id}/execute` and `.../execute-stream` | buffered or streaming |
| Buffered OpenAI compatibility | `POST /v1/chat/completions` with `stream:false` | mapped stateless `PingRequest` → `ExecutePromptAsync` |
| Streaming OpenAI compatibility | `POST /v1/chat/completions` with `stream:true` | mapped stateless `PingRequest` → `StreamPromptAsync` → OpenAI SSE mapper |

The OpenAI mapper sets `SessionId=null`, `UnattendedMode=true`, and populates
`StatelessMessages`. `InferenceExecuteWriter` writes each native event as one
`application/x-ndjson` line.

All provider chat I/O in this pipeline goes through Core `IModelCallExecutor`
(`ExecuteBufferedAsync` / `ExecuteStreamingAsync`): initial inference, tool continuation,
structured-output correction, Spell routing, and Lexicon extraction. Tool invocation failures use
the same code-owned tolerant policy in buffered and streaming modes.

#### 10.7.2 Shared preflight, provider attempts, and context seed

Before any inference provider call, both response modes execute these gates in order:

1. `GuardrailsPipeline.FilterInputAsync`: PII, toxicity, and authored topic policy; disabled
   guardrails pass through.
2. Attached-file validation.
3. `PingRequestBoundsValidator`.
4. Scrying validation: image count, size, allowed MIME, and model vision capability. User and
   model-driven Session re-attachments share the same Scrying gate, code-owned reference budget,
   and inject-once key/version rule; an oversize image is rejected, never truncated.
5. Empty-prompt validation, skipped for stateless message lists.
6. Daily budget admission. `BudgetMonitor` prefers completed `BillableOperations` plus outstanding
   `BudgetReservations`; Session cost is only the unavailable-service fallback/projection. Exceeded
   budget is `Budget.Exceeded` / HTTP 429, and alert deduplication is once per threshold per UTC day.

Caller/host cancellation then flows unchanged into lease resolution, model calls, tools, and
persistence cleanup. Arcanum adds no hidden turn deadline.

`ChatClientFactory` resolves an OpenAI-compatible client (including Ollama through `/v1`) over the
named HTTP pipeline; the returned `ChatClientLease` owns that attempt's `IChatClient`. Prompt caching
never skips model I/O and is emitted only for exact built-in capability-catalog matches (§22.3).

With `IProviderHealthTracker`, each distinct compatible provider candidate is tried once in
configured order. Only connectivity-class failures (including connection/socket failures and
transport timeouts) may advance to the next candidate. Auth, model, request, 429, and provider 5xx
responses surface immediately. A streaming attempt may fall back only before provider commitment:
status/session binding does not commit; the first answer delta, any reasoning item (including
protected-only or client-withheld reasoning), a complete actionable tool proposal, or an empty
successful round does. After commitment, provider swapping and the no-tools compatibility restart
are prohibited.

`TurnContextSeed` is built once per logical run, while each provider receives an isolated
`ProviderAttemptContext`. Context assembly is ordered:

1. Load the Session and bounded Entry window, or no thread for a stateless request.
2. Begin the Grimoire turn by inserting an in-flight assistant Entry and capturing the
   `(sessionId, assistantEntryId)` handle.
3. Read bounded workspace `CODEX.md`.
4. Resolve an explicit Spell/version or run `SemanticSpellRouter`; a pure direct resonance avoids
   the LLM, hybrid narrows candidates, and otherwise `SemanticRouter` runs its bounded FastModel
   preflight. Timeout/failure means no active Spell.
5. Imprint the retrieval query once through The Weave.
6. Retrieve semantic workspace context with that imprint.
7. Retrieve Saga memories with the same imprint.
8. Build the ordered stable/volatile DCI system document from Codex, primary/resonant Spells,
   attachments, Lexicon, semantic context, and Saga.
9. Build built-in + MCP tools, then intersect with Artifact Attunement. Development/startup gates
   control host-process tools; Local edition strips colliding host-process names. Client-forwarding
   mode instead creates wrappers for client declarations.
10. Resolve Campaign, Ward requirement, and Sanctum policy; filter tools and omit `ask_human`
    unless the turn is attended streaming with a live HITL emitter. Buffered turns never advertise
    `ask_human`.

#### 10.7.3 The iterative model/tool loop

`RunInferenceAttemptAsync`, parameterized by `TurnResponseMode`, owns one semantic loop for both
projections. An outer compatibility loop normally executes once and may restart once without tools
only when the provider rejects tool support before commitment. Its inner loop is the evidence-driven
chat loop:

1. Materialize history/current input, dynamic system prompt, rehydrated attachments, final tools,
   and structured-output schema. Read-time compression may replace old transcript rows with the
   Session summary in memory; it never deletes Entries and keeps tool-call/result halves paired.
2. Before **every** provider call, including continuations/corrections, build a fresh complete
   `ContextTokenBreakdown`, trim only oldest complete in-memory tool exchanges if necessary, and
   reject overflow with `Hub.ContextBudgetExceeded`. Raise the current-call USD reservation through
   `TurnAccountingHandle`; a failed raise blocks provider I/O. The initial estimate is never reused.
3. Invoke `IModelCallExecutor`. It identity-validates the finalized payload/breakdown, validates the
   candidate cache plan, records estimate/admission metrics, and performs I/O. Eligible cache and
   reasoning options are applied to clones, not reusable turn state. `TextContent` is answer;
   `TextReasoningContent` is separately normalized. Protected reasoning remains only on the raw
   same-provider assistant content needed for continuation.
4. Attach reported input and signed estimate variance to that call, then accumulate prompt,
   completion, provider-total, cached, and reasoning counts. Reasoning is already a completion
   subset and cached input a prompt subset, so neither is added twice. A present provider total is
   authoritative; only a missing total is derived. Cache observations/savings are recorded once per
   completed call.
5. Collect every non-informational `FunctionCallContent` from the response.
6. If there are no actionable calls, exit to finalization.
7. If `ForwardClientTools` is active, record `PromptToolCall` values, set
   `finishReason=tool_calls`, and exit without server execution.
8. Otherwise process each call through Ward → Sanctum → invocation. Results are bounded for the
   next model context and added to observed-tool/audit metadata. `search_workspace` is direct,
   exact, line-scoped filesystem search, not a Weave query. `workspace_check` executes
   repository-authored .NET work under its separate macOS-only capability and child deadline.
9. Append one assistant message containing the normalized function call plus raw same-provider
   reasoning, followed by a tool message containing `FunctionResultContent(callId, resultText)`.
10. Persist the tool interaction for stateful turns and publish it to the live Session hub, subject
    to the mandatory `apply_patch` receipt path below. Reasoning is not persisted.
11. After every call/result pair in the round is appended, add any queued `attach_session_file` or
    `refresh_session_file` `TextContent` / `DataContent` as one User message. Never interleave this
    content between tool exchanges. Reconcile the turn ledger first so a whole reattachment removes
    same-version semantic chunks and a refresh removes stale-version chunks before continuation.
12. Return to step 2 with the augmented message list.

There is no arbitrary model-call, tool-round, correction-attempt, step, run, or turn-duration cap.
Changing evidence may continue until terminal output, client-tool forwarding, caller/host
cancellation, context admission, or cost admission. Progress-sensitive loops stop on repeated state,
not a public attempt counter. Physical transport/process cleanup deadlines remain local bounds, not
workflow ceilings.

#### 10.7.4 Ward/Sanctum order and tool persistence

For each tool proposal:

1. An intrinsic/configured Ward candidate in unattended auto-deny mode receives a synthetic denial
   without waiting for an operator.
2. A non-Forbidden Art skips Ward and proceeds to Sanctum.
3. A Forbidden Art emits `warded`, awaits `IWard.WardAsync`, emits `wardResolved`, and either returns
   the denial or proceeds.
4. Campaign Sanctum validates tool allowlist, model-supplied paths, and network targets. Independent
   `WorkspacePathPolicy`, symlink/handle identity, and tool-specific validation always apply whether
   or not a Campaign exists.
5. The selected `AIFunction` is invoked. An unexpected exception is logged and converted to
   `PublicToolFailureMessage`; streaming also emits `toolError` before the matching `toolResult`.

Normal `search_workspace`, `workspace_check`, and other stateful interactions append assistant
`ToolCall` and system `ToolResult` Entries through `TryAppendToolInteractionAsync`.
`SessionEventHub` is only live process-local fan-out: every subscriber uses a bounded `DropOldest`
channel, slow readers may miss Entries with a warning, and subscriptions vanish on restart.
Persisted Entries are replay authority.

`apply_patch` is the exception because its filesystem result must not reach the next model round
without a proven receipt. It requires an already-persisted Session/assistant binding, creates one
reversible filesystem transaction per invocation, and while that transaction remains reversible
durably appends deterministic assistant-call then system-result rows containing the exact argument
snapshot and exact bounded result. `ReceiptHandled=true` suppresses generic duplicate append.

Receipt outcomes are exactly `NewlyCommitted`, `RecoveredCommitted`, `Failed`, and `Ambiguous`.
Committed/recovered keeps the patch and makes the transaction irreversible after bounded cleanup;
failed rolls back and returns `conflict/receipt_failed` (or `rollback_incomplete` with relative
recovery paths); ambiguous keeps applied files/recovery artifacts but fails the turn so the model
cannot rely on unproved persistence. Deterministic UUIDv8 receipt/Entry IDs derive from canonical
invocation identity, tool-call identity, round/call ordinals, and normalized tool/row kind; patch
text, arguments, and result bodies are not ID inputs. Retries with matching rows classify recovered;
partial/mismatched/unreadable rows are ambiguous.

The receipt domain is `RetroDownfall.Arcanum/receipt-format-v1`: required invocation id and optional
provider call id are trimmed/NFC (blank optional id becomes empty), round/call ordinals are
nonnegative signed 32-bit big-endian, tool name is trimmed/NFC/invariant-lowercase, and UTF-8 strings
carry signed 32-bit big-endian length prefixes. `RetroDownfall.Arcanum/entry-format-v1` derives the
call/result Entry IDs from the receipt's `D` string plus row kind `call` or `result`. In both cases,
the first 16 SHA-256 bytes are marked UUID version 8 with the RFC variant.

Caller cancellation propagates. Before/during a patch commit it first triggers reverse rollback
under an independent cleanup deadline; cancellation during receipt handoff preserves any attached
persistence classification. Identity-matching empty transaction-created directories are removed
deepest-first; unsafe cleanup retains and reports the artifact. `search_workspace` also propagates
caller cancellation, while only its own elapsed cap maps to structured `timed_out`. Multiple patch
calls in one model round remain independent transactions.

#### 10.7.5 Streaming pump, finalization, and wire order

Streaming adds a chunk-pump loop inside the shared tool round. `ModelCallTextDelta` accumulates only
answer text and may emit `token`; `ModelCallReasoningUpdate` accumulates only ephemeral reasoning and
may emit typed `reasoning`. Raw tool/usage/finish/protected-reasoning updates are retained to combine
the round into a response. The first reasoning update commits the provider before projection,
including protected-only or withheld reasoning.

After each streamed round: combine updates → accumulate usage → collect calls → either finish,
forward calls, or for each server call emit `toolCall`, Ward frames, optional `toolError`, and
`toolResult`, append/persist the exchange, then start a fresh admitted model call. Successful
`attach_session_file` and `refresh_session_file` content is queued only after every tool result in
that round and appears in the next round. A successful refresh additionally emits the native
`attachmentRefreshed` frame after its `toolResult`; OpenAI SSE omits that native-only event.

Output guardrails use the code-owned buffered policy. Guardrails or
`response_format.json_schema.strict:true` hold answer and reasoning runs together in provider order.
Safety inspection sees the accepted answer plus projectable reasoning. Success releases runs in
order; rejection releases none. Provider commitment occurs on raw updates before that visibility
decision.

Buffered finalization:

1. For JSON Schema, validate answer text and issue progress-based corrective calls while invalid
   output/correction state changes. A repeated state, cancellation, context rejection, or cost
   rejection stops correction. Each candidate replaces both rejected answer and its reasoning.
2. Run output guardrails over accepted answer + projectable reasoning.
3. Finalize the answer-only assistant Entry and publish it.
4. Increment Session token/cost projections.
5. Enqueue Saga extraction.
6. Record metrics and successful-turn audit metadata.
7. Return `PromptTurnResult` with answer, accumulated usage, observed calls, finish reason, warnings,
   and a separate ordered reasoning segment list.

Streaming mirrors those steps. Best-effort schema validation is post-hoc with no correction after
released output; strict schema mode uses buffered replacement calls and releases only the accepted
replacement after validation/guardrails. Failure emits terminal `error` and no `result`. Success
releases buffered reasoning/token runs in order and emits terminal `result`. A `finally` path resolves
the Grimoire turn as interrupted when a consumer abandons enumeration.

A typical native NDJSON sequence is:

1. `status` (generation started);
2. `sessionBound`, then deprecated `conversationBound`, when stateful;
3. optional compression `status`;
4. per provider call, `context` with profile/source rows/estimate/margin/answer+reasoning reserves,
   optionally followed by an updated `context` carrying reported input and signed variance;
5. provider-order `reasoning` and `token` frames (withheld together when buffered);
6. per tool: `toolCall`, optional `warded`/`wardResolved`, optional `toolError`, then `toolResult`;
7. repeat calls/rounds as needed; and
8. terminal `result` (answer in `message`, legacy total-token decimal in `data`, typed usage,
   finish reason, warnings) or terminal `error`.

OpenAI SSE filters native `context`, Ward, tool-result, and tool-error diagnostics as documented in
§8.8. The Session live stream at `GET /api/sessions/{id}/stream` is a separate SSE channel: it
subscribes before replay, emits persisted recent Entries, then pumps lossy `SessionEventHub` updates.
It is independent of the inference stream.

#### 10.7.6 Loop termination summary

| Loop | Purpose | Termination |
|------|---------|-------------|
| Provider fallback | Try each distinct eligible provider after a pre-commit connectivity failure | Commitment, success, non-connectivity failure, cancellation, or candidate exhaustion |
| No-tools compatibility restart | Retry an uncommitted provider attempt without unsupported tools | At most once; any answer/reasoning/actionable call/empty success prohibits restart |
| Model/tool loop | Model → tools → persisted exchange → newly admitted continuation | Final answer, client-tool forwarding, cancellation, context rejection, or cost rejection |
| Streaming chunk pump | Consume `ExecuteStreamingAsync` updates | End of provider stream or read failure |
| Structured-output correction | Replace invalid JSON-schema candidates while state changes | Valid output, repeated state, cancellation, context rejection, or cost rejection |

### 10.8 Durable operation ledger and restart reconciliation

Long-running work may not treat an in-memory `Task`, enumerator, process handle,
`CancellationToken`, live stream, Ward, or DI object as recovery state. The shared lifecycle is
`ILongRunningOperationStore` plus the scoped `ILongRunningOperationCoordinator`, backed by the
raw-SQL `LongRunningOperations` table in the SQLCipher-encrypted Grimoire. Migration
`20260730020000_AddLongRunningOperations` is append-only in `GrimoireSqlSchemaMigrator`.

Each row contains operation kind and policy; `Pending`, `Running`, `Waiting`, `Cancelling`,
`Completed`, `Failed`, `Abandoned`, or `ReconciliationRequired`; root/parent, Session, inference
run, budget reservation, and idempotency-claim links; created/started/heartbeat/completed times;
lease owner/expiry; attempt count; checkpoint version plus encrypted payload or reference; a
bounded public-safe summary; terminal error code; and a monotonically increasing revision.
Checkpoint payloads and references remain inside SQLCipher. API/CLI responses project only
`HasCheckpoint`, its version, and `PublicSummary`.

Lifecycle writes use SQL compare-and-swap:

- lease acquisition changes `Pending`, expired `Running`, or expired `Waiting` to `Running`,
  records the owner/expiry, increments attempt and revision, and succeeds for only one worker;
- heartbeats renew only a live lease still owned by that worker; operation authors use bounded
  leases (5 seconds through 15 minutes) and stop immediately after renewal failure;
- checkpoints require the owner and exact previous checkpoint version, so duplicate/out-of-order
  writes cannot overwrite newer recovery state;
- cancellation, retry, and terminal transitions require the exact row revision. Terminal and
  repair-required transitions release the lease.

The code-owned recovery-policy inventory is:

| Kind | Policy | Recovery intent |
|------|--------|-----------------|
| `inference-run` | `ReconcileAndComplete` | Reconcile accounting/reservation evidence; never replay a live stream. |
| `subagent` | `AbandonSafely` | A crashed child has no resumable conversational context; expire its lease and mark it abandoned without replaying provider work. |
| `budget-reservation` | `ReconcileAndComplete` | Idempotently release a stranded reservation when actual cost cannot be established. |
| `batch` | `RestartIdempotently` | Restart from durable batch/input/output status; do not duplicate completed lines. |
| `apprentice` | `ResumeFromCheckpoint` | Resume from its versioned durable checkpoint and parent/child lineage. |
| `attachment-promotion` | `ReconcileAndComplete` | Inspect durable file/row state and finish or roll back promotion. |
| `workspace-index` | `RestartIdempotently` | Re-enumerate deterministically; durable indexed rows remain the authority. |
| `idempotency-claim` | `ReconcileAndComplete` | Complete, fail, or explicitly abandon the linked claim so it cannot stay stranded. |
| `blob-encryption-migration` | `RestartIdempotently` | Re-scan metadata and reconcile plaintext, envelope, and replace-before-metadata states. |
| `blob-encryption-key-rotation` | `RestartIdempotently` | Continue toward the active write key while retaining every still-referenced prior key. |

`LongRunningOperationReconciler` selects only expired `Running`/`Waiting` leases, acquires a fresh
two-minute recovery lease, dispatches by bounded kind, rejects unsupported checkpoint versions
before handler code, maps corrupt checkpoints to `operation.checkpoint_corrupt`, and applies the
handler result with the acquired revision. Missing handlers and unexpected recovery failures
become `ReconciliationRequired`, never guessed success. `BudgetReservationRecoveryHandler` is the
first concrete shared handler and calls the reservation service's idempotent release transition.

Host startup runs reconciliation after Grimoire migration and before subsequently registered
durable workloads. It examines at most 100 operations with concurrency 4 and a 10-second total
budget. If optional recovery exceeds that budget, startup continues in explicit degraded mode and
operators run `arcanum operation reconcile`; the host never claims that deferred work completed.
Manual reconciliation examines at most 500 operations with concurrency 4.

All `/api/operations*` routes inherit `/api` API-key authentication and rate limiting. Operator
commands are `arcanum operation list [--kind …] [--state …]`, `show <id>`, `cancel <id>`,
`retry <id>`, and `reconcile`. `GET /api/health` includes `DurableOperations`; `arcanum doctor`
reports that component's safe detail. Prometheus exposes `arcanum_operations{kind,state}` gauges
and `arcanum_operation_reconciliation_total{kind,outcome}`. Kind/state/outcome labels come from
closed vocabularies; operation IDs, summaries, and user content are never metric labels.

---

## 11. Local API security

### 11.1 Threat model

Arcanum runs on **loopback only** for **single-user local development**. Even on localhost, every `/api` and `/v1` request must present a valid API key (zero-trust local). The API key remains privileged for file/network/MCP tool surfaces. Default **Local** edition does **not** advertise or invoke host-process tools (`execute_command` / `run_spell_script`) unless Development + `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1` (`HostProcessToolPolicy`).

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
2. Caches a **SHA-256 digest** of the expected key (32 bytes, fixed size) for a code-owned short TTL so on-disk key rotation propagates without restart. The plaintext expected key never lives in long-term memory beyond computing the digest, and the temporary UTF-8 buffer is zeroed.
3. Hashes the inbound header through `SHA256.TryHashData` into a stack buffer and compares both 32-byte digests with `CryptographicOperations.FixedTimeEquals` — constant-time **and** length-independent (no early-return on size mismatch).
4. Uses `stackalloc` for the header UTF-8 buffer when `<= 256` bytes; the 32-byte digest buffer is always on the stack.

Failed authentication returns **`ApiResponse<string>`** at **401** with error code **`Auth.Unauthorized`** (matches the `{Noun}.{Verb}` convention used elsewhere).

### 11.4 CORS (serve host)

`AddArcanumApiServices` registers a CORS policy named **`ArcanumCors`** whose **allowed origins are read from `Arcanum:Host:CorsAllowedOrigins`** at startup. Defaults to localhost loopback (`http://localhost:5001`, `http://127.0.0.1:5001`, `http://localhost:3000`, `http://127.0.0.1:3000`). Operators who need to allow any browser origin (for example LibreChat installations on arbitrary hosts) can set the property to `["*"]` — Arcanum then calls `AllowAnyOrigin` and adds the same `AllowAnyHeader` / `AllowAnyMethod` it always has. **When the effective host bind is all-interfaces** (`ListenAny` or `ARCANUM_HOST_ANY`), a configured `["*"]` origin is **downgraded** to the localhost defaults so wide-open CORS is not combined with a non-loopback listener. `UseArcanumCors` runs early in the pipeline so browser-based tools can preflight without endpoint contention. `AllowAnyHeader` / `AllowAnyMethod` are retained unconditionally because callers always present custom headers (`X-Arcanum-Key`) and use varied verbs.

### 11.5 OpenAPI and Scalar

`MapOpenApi` runs unconditionally under the keyed `/api` group, so `openapi/v1.json` always requires the API key. **`MapScalarApiReference`** is **gated by `Arcanum:Features:ScalarUi`** (default **`false`**). When enabled, the Scalar route lives in a sub-group with a CSP filter that emits `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'` and `X-Content-Type-Options: nosniff` on every response (same-origin scripts/styles only — matching `ApiBootstrapper`). First-party browser UI must keep JS/CSS in external files; Scalar is an opt-in third-party surface under this CSP. The OpenAI-shaped **`POST /v1/chat/completions`** and **`GET /v1/models`** routes live under `MapGroup("/v1")` with the same API-key filter and are not advertised in the OpenAPI document.

### 11.6 Symlink containment for tool paths

`WorkspacePathPolicy` is the **primary, unconditional** containment policy for workspace tools; it does not depend on a Campaign or Sanctum. Tier 1 (`IsPathUnderWorkspace`, `IsPathUnderWorkspaceWithSymlinkCheck`, `RevalidatePathBeforeIo`) performs normalized lexical containment (case-insensitive on Windows), walks every existing component, and resolves final symlink targets. Escaping or unresolvable components fail closed, including a not-yet-created leaf below a symlinked parent. Tier 2 (`SandboxedFileIo` / mutation fingerprint services) captures expected file identity, opens the handle, and revalidates dev/inode or Windows volume/file index to close lexical-check-to-open races for trust-sensitive reads and mutations.

**Shared secure reads:** `SecureFileReader` opens once with link following disabled (`O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC` on supported Unix; reparse-point/overlapped open on Windows), proves from the opened handle that the object is a regular file with exactly one hard link, optionally matches the pre-open identity, and reads only from that handle. The nonblocking Unix open makes FIFO rejection bounded; FIFOs, devices, symlinks/reparse points, hard links, unknown metadata, and identity swaps fail closed. It reads at most `maxBytes + 1` through an incrementally grown `ArrayPool<byte>` buffer, revalidates handle identity/kind/link count after the read, strictly decodes UTF-8, and clears every rented buffer on return/disposal.

`McpConnectionManager`/`TrustedMcpWorkspaceStore`, `SandboxedFileIo` (therefore `read_file_chunk` and `replace_text_block`), `PhysicalFileSystemBrowser.ReadAsync`, and AtomicFile's backup-source copy use this shared primitive where applicable. Sandboxed and workspace-browser reads additionally revalidate post-open workspace containment/current path identity. Atomic writes stage and fingerprint content, verify the moved destination, and restore only a backup whose captured identity **and content fingerprint** still match; the restored destination is fingerprinted again. Temporary/backup cleanup uses identity-owned deletion, not a blind path delete.

`search_workspace`, the `apply_patch` planner/coordinator, direct file tools, spell scanning, and API workspace I/O all use this policy. A matching Campaign with `SanctumConfig.Enabled` adds its tool/path/network allowlist in `ToolExecutionPipeline`; no campaign or disabled Sanctum means only that additional gate is absent. Sanctum never replaces or weakens `WorkspacePathPolicy`.

### 11.7 In-process `execute_command` argument handling

The tool accepts arguments in **either** of two forms:

- **`argumentList: ["status", "--porcelain"]`** — preferred. Each entry is appended verbatim to `ProcessStartInfo.ArgumentList`. No shell, no OS-level re-parsing.
- **`arguments: "status --porcelain"`** — legacy single-string form. The host tokenizes via the same algorithm `ArcanumSpellScriptTool` uses (quoted substrings stay together; whitespace separates tokens) and then appends each token to `ArgumentList`.

`Arguments` is **never** assigned to `ProcessStartInfo.Arguments` directly, so model output cannot smuggle additional argv via shell metacharacters.

`workspace_check` is stricter still: its schema has no executable or raw argv field. `WorkspaceCheckProfileCatalog` maps a closed profile ID and exact allowlisted option values to `ProcessStartInfo.ArgumentList`, always adds `--no-restore`, pins the trusted native `dotnet` host and selected SDK entry point, and rejects response files, scripts, shells, command interpreters, or runtime-reserved output/restore tokens in custom profiles. This controls argv but does **not** make execution non-arbitrary: workspace-owned MSBuild tasks, generators, analyzers, and tests execute code.

**Child environment:** before spawn, `execute_command` and `run_spell_script` strip `ARCANUM_*` secret/config vars and loader/runtime hijack variables from the inherited environment while preserving `PATH`/`HOME`. MCP stdio servers use the same absolute-deny rules plus optional per-server `inheritEnv` (§5.6 / MCP host).

`execute_command` and `run_spell_script` both read stdout/stderr through a `ReadStreamCappedAsync` helper that enforces the code-owned combined output cap, split evenly per stream. Beyond the cap, the stream is silently closed and a `[truncated: exceeded N bytes]` marker is appended. UTF-8 boundary safety is preserved by `ChooseSafeCharCount`. This prevents a verbose tool from exhausting host memory.

**External MCP:** `McpBridgeTool` / `McpToolResultFormatter` apply the same **`ToolOutputCapBytes`** limit to bridged `tools/call` text results. `McpClient` bounds `tools/list` tool descriptions (8 KiB UTF-8) and input schemas (64 KiB UTF-8; oversized schemas fall back to an empty object schema).

### 11.9 Sanitized public error envelopes

Inference-pipeline errors must not leak internal exception text to clients:

- **`WizardIntelligenceProvider.ExecutePromptAsync`** / **`StreamPromptAsync`** — model-resolution failures return the public string `"The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel."`; full exception is logged via `ILogger.LogWarning`. Provider failures use sanitized public errors. Caller/host cancellation propagates; there is no hidden Master turn timeout.
- **`ArcanumExceptionHandler`** (`IExceptionHandler`) — unhandled pipeline exceptions return **`Hub.Unhandled`** in the `ApiResponse<string>` envelope with the same **`TraceId`** logged server-side. **`JsonException`** from request binding or deserialization returns **400** with **`Validation.InvalidBody`** in the `ApiResponse<bool>` envelope.
- **`POST /v1/chat/completions`** — both buffered and SSE generic failures return exactly `"Inference failed. See server logs for details."`, and model-not-configured uses the same exact string as the native path; never the raw `Result.Error.Message`.
- **`WebhookCommLinkDispatcher`** — outbound webhook exceptions return the public code `CommLink.WebhookException` with the generic message `"Comm Link webhook POST failed. See server logs for details."`; logs retain only host and exception type, never the secret URL or exception text.
- **`PUT /api/config`** — validation failures return **`ApiResponse<bool>`** at **400** with code `Configuration.ValidationFailed` (user-facing validation messages). Write failures return **`ApiResponse<bool>`** at **500** with code `Configuration.WriteFailed` (exception detail is logged server-side; the envelope message is safe to display in Studio).

Changed inference, attachment, tool, CLI-session, and TurnEngine failure paths log safe operation identifiers and exception **types** without attaching raw exception objects/messages where canary-bearing provider/file data could leak. Tests assert canary text is absent from both public payloads and captured log entries. See §8.23 for the full `ErrorCodes` → HTTP status catalog used by `ArcanumErrorMapper` across native `/api` routes.

### 11.10 Comm Link webhook scheme allowlist and redirect handling

`WebhookCommLinkDispatcher` resolves the secret URL from `Arcanum:Integrations:CommLink:WebhookUrlEnvironmentVariable` only when dispatch begins (deterministic default `ARCANUM_COMMLINK_WEBHOOK_URL`). It validates the resolved scheme against `AllowedSchemes` (default `["https"]`) and host against `AllowedHosts`. **`OutboundUrlGuard`** then rejects loopback, RFC1918, and link-local targets, including after DNS resolution. The named `HttpClient("CommLinkWebhook")` disables redirects and default factory URI logging; dispatcher logs contain at most the host. Its code-owned transport timeout bounds only the POST operation.

### 11.11 Outbound URL guard (SSRF hardening)

**`OutboundUrlGuard`** (`Infrastructure/Security`) is the shared policy for untrusted outbound `http`/`https` URLs. It requires an absolute URI, resolves the host, and rejects any address in loopback (`127.0.0.0/8`, `::1`), RFC1918 (`10/8`, `172.16/12`, `192.168/16`), carrier-grade NAT (`100.64.0.0/10`), IPv6 unspecified (`::`), link-local (`169.254/16`, `fe80::/10`), IPv6 unique-local (`fc00::/7`), or the hostname `localhost` / `*.localhost`.

**Applied at:**

- **`WebhookCommLinkDispatcher`** immediately before `POST`, after resolving the environment reference.
- **`PUT /api/config`** and **`POST /api/config/validate`** via **`OutboundUrlGuard.ValidateArcanumSettingsAsync`** validate only public provider endpoints. CommLink's secret URL is intentionally unavailable until dispatch.

**DNS-rebind pinning:** `OutboundUrlGuard.ResolveValidatedAddressesAsync` returns the validated address set for a hostname. Untrusted egress clients (`CommLinkWebhook`, `McpHttp`) wire `ConnectCallback` to resolve fresh at connect time, re-run `IsBlockedAddress` on the actually-dialed IP, and connect only to validated addresses. Provider inference and connectivity probes (`ChatClientFactory` endpoint cache, **`POST /api/providers/test`**) use **`OutboundUrlGuard.CreateProviderEgressHandler()`** — same connect-time pinning with **`allowPrivateAndLoopback: true`** so loopback/RFC1918 local backends remain reachable while link-local/metadata addresses stay blocked.

### 11.12 Kestrel limits and optional rate limiter

`ArcanumKestrelConfigurator` (shared by `ServeCommand` and `Api.DevHost`) applies the code-owned request-body limit once as `KestrelServerOptions.Limits.MaxRequestBodySize` for all listeners (HTTP and HTTPS). When automatic rate limiting is effective (§11.13), `AddArcanumApiServices` calls `AddRateLimiter` with a fixed-window policy named **`ArcanumRateLimit`**; both `/api` and `/v1` `MapGroup` routes apply `RequireRateLimiting("ArcanumRateLimit")`. Partition keys use the **remote IP address only**. `RejectionStatusCode = 429`, and the code-owned queue limit is zero so excess requests are rejected immediately.

### 11.13 `Arcanum:Host:ListenAny` versus `ARCANUM_HOST_ANY`

The environment variable always wins. Recognized values: `1` or `true` (force all-interfaces bind), `0` or `false` (force loopback), or any other string that `bool.TryParse` accepts. When the env var is unset, empty, or unrecognized, `ArcanumEnvironment.IsHostAnyEnabled` falls back to the configuration property (`Arcanum:Host:ListenAny`). This keeps the container-friendly override while making the binding visible in `arcanum.json` for first-party operators. The effective value is exposed via **`GET /api/meta`** (`ListenAny` on `InstanceMetadataDto`).

**HTTPS-only any-IP:** When the effective bind is all-interfaces, Kestrel binds **only** `ListenAnyIP` on `Arcanum:Host:Https:Port` with TLS. `Arcanum:Host:Https:Enabled` and a loadable certificate are required; plaintext any-IP HTTP is refused. Local CLI clients resolve `https://localhost:{HttpsPort}`; Forge `the-forge.json` `BaseUrl` must match. Doctor probes the HTTPS health URL (and surfaces cert-trust / SAN guidance on failure).

**First-run acknowledgement:** When `ListenAny` is enabled from configuration (not via `ARCANUM_HOST_ANY`), interactive `arcanum serve` prompts once and writes `~/.config/arcanum/.listen-any-acknowledged`. Non-interactive hosts must set `ARCANUM_LISTEN_ANY_ACK=1`. Container operators using `ARCANUM_HOST_ANY` skip the prompt but still receive the security banner.

**Security banner:** On startup when all-interfaces bind is effective, `ArcanumSecurityStartupChecks` and `arcanum serve` emit a warning that HTTPS-only binding on all interfaces grants network-local clients operator-equivalent power if they obtain the API key, and remind operators to trust the TLS certificate (Compendium self-signed certs are loopback-SAN only).

**Automatic rate limiting:** When the effective bind is all interfaces (`IsHostAnyEnabled` is `true`), `ArcanumEnvironment.IsRateLimitEnabled` returns `true`. **Loopback-only binds** (`ListenLocalhost`) intentionally leave the limiter **off** so a single operator on `127.0.0.1` is not throttled during local development. This code-owned policy pairs network exposure with request admission control without a separate operator toggle.

### 11.13.1 Data at rest permissions

Sensitive paths are restricted to the current user at creation time via `SecureFilePermissions`:

- **Unix:** `File.SetUnixFileMode` — files `600` (`UserRead | UserWrite`), directories `700` (`UserRead | UserWrite | UserExecute`).
- **Windows:** `File.SetUnixFileMode` throws; owner-only ACL via `FileSystemAccessRule` (`Modify` for files, `FullControl` with inheritance for directories).

**Applied on create:** Grimoire `.db`, `arcanum.json`, `cli-context.json`, `cli-session.txt`, Serilog rolling logs
(`SecureSerilogFileHooks`), Data Protection secret files, encrypted attachment/upload/batch
envelopes and their same-directory ciphertext temps, and owner-only creation of
`~/.config/arcanum` and `%ApplicationData%/arcanum/logs/`.

**Startup self-check:** `ArcanumSecurityStartupChecks` warns (does not fail) when any checked path is group/other-readable on Unix or grants read to `Everyone`/`Users` on Windows. Pre-existing files are not modified automatically — operators must fix permissions manually after the warning.

### 11.14 Wards (Forbidden Arts)

**Purpose:** Gate high-risk tool invocations (**Forbidden Arts**) until an operator explicitly allows or denies them. Separate from the `ask_human` MCP tool (information gathering).

**Engine:** Singleton **`IWard`** / **`WardGate`** (in-memory). Active wards are keyed by `wardId` (`Guid` string). **`WardAsync`** registers a `TaskCompletionSource`, honors caller cancellation (inference abort cleans up the ward), and auto-denies on timeout with reason `"The ward held until timeout — action was not allowed"`. **`Resolve`** atomically moves the active ward to a resolved tombstone before completing the waiter, so exactly one concurrent resolver succeeds and every competitor returns **`AlreadyResolved`** (HTTP **409**). Tombstones are retained for the clamped ward timeout plus 60 seconds and pruned against an injected `TimeProvider` (system time in production).

**Policy:** while `Arcanum:Security:Ward:Enabled` is true, `ToolRiskClassifier` makes `execute_command`, `apply_patch`, and `workspace_check` **intrinsic** Ward tools. Replacing the operator-addition list at `Arcanum:Security:Ward:ForbiddenArts` or selecting `ToolPolicy.NoForbiddenArts` cannot turn them into unwarded execution; the latter removes them from advertisement. Other configured Forbidden Arts require a matching campaign's `RequireWardForForbiddenArts` (default `true` when no campaign; **`true`** on newly registered campaigns via `CampaignSettings.CreateDefault()`). `UnattendedMode` + `AutoDenyInUnattendedMode` skips the wait and denies immediately. `workspace_check` Ward disclosure explicitly states that it executes repository code, leaves network egress open, and cannot guarantee cleanup of an intentionally detached descendant.

**Intentional exclusions from `ForbiddenArts`:**
- **`scribe_lexicon`** — append-only structured memory; non-destructive (appends non-duplicate facts). **`delete_lexicon`** remains gated because it is destructive.
- **`ask_human`** — separate HITL mechanism (information gathering, not execution).

**API:** **`GET /api/wards`**, **`GET /api/wards/{id}`**, **`POST /api/wards/{id}`** (`allow`, optional `reason`). Protected by **`ApiKeyEndpointFilter`**. Wards are ephemeral by design — host restart drops all active wards (callers' `TaskCompletionSource` instances are gone with their processes). `WardGate` is a fresh, empty singleton on every process start, so there is nothing to actively deny on restart; the `HostRestartedReason` contract value (`"Host restarted — ward timed out"`) lets clients distinguish restart-driven denial from timeout/capacity denial. The durable/non-durable state inventory is §5.4.4.

**Streaming:** NDJSON frames `warded` and `wardResolved` on `/api/intelligence/ping-stream`. OpenAI `/v1` SSE bridge ignores these event types (transparent latency only).

**Related:** Sanctum **`ResourceLimits`** file-write and **`read_file_chunk`** line caps are enforced in **`ArcanumInternalToolServer`** (§11.15); external MCP bridge output uses the code-owned tool-output cap (§11.8).

### 11.15 Sanctum (campaign sandboxing)

**Purpose:** Per-campaign execution isolation — constrain tool file access, network egress, and tool availability within a defined boundary. Separate from **Wards** (operator approval) and from creation-time **`CampaignPathPolicy`** / **`Arcanum:Security:CampaignRoots`**.

**Threat model:**
- **Path escape** — `../` traversal, absolute paths outside the campaign workspace, symlink pivots (`File.ResolveLinkTarget` / `Directory.ResolveLinkTarget` with final-target check).
- **Network egress** — outbound Comm Link webhook URL when **`send_commlink_alert`** runs (application-layer check; no kernel firewall on macOS).
- **Disabled tools** — tool names listed in **`SanctumConfig.DisabledTools`**.
- **Resource abuse** — **`ResourceLimits.MaxFileWriteMb`** enforced on in-process **`write_file`** / **`replace_text_block`** before I/O (via **`ISanctumGuard.GetEffectiveResourceLimitsForWorkspaceAsync`**); **`read_file_chunk`** bounded to 2,000 lines per request with capped **`startLine`**. **CPU time, memory, and open file descriptors are enforced at the OS level** on the child processes spawned by **`execute_command`** and **`run_spell_script`** (see "Kernel resource limits" below); on Windows, **`MaxProcessCount`** is also enforced via Job Object **`ACTIVE_PROCESS`**.

**Engine:** Scoped **`ISanctumGuard`** / **`SanctumGuard`** loads **`SanctumConfig`** from **`Campaign.SanctumConfigJson`** (`TheForgeJsonContext`). Breaches are recorded inline to the Grimoire-backed **`ISanctumBreachRepository`** / **`SanctumBreachRepository`** (raw SQL over the **`SanctumBreaches`** table, §16.2) — durable across host restarts. **`SanctumGuard`** and **`ISanctumBreachRepository`** are both scoped and share the same **`ArcanumDbContext`**, so the breach write is part of the same request scope as enforcement; no fire-and-forget is needed. Breaches raised for an unparseable/unknown campaign id are logged only (not persisted), since **`SanctumBreaches.CampaignId`** has a foreign key to **`Campaigns`**. Each insert enforces per-campaign retention (**`SanctumConfig.MaxBreachCount`**, default 1,000, clamp 100 – 100,000): oldest rows beyond the limit are deleted in the same transaction.

**Enforcement modes:** **`SanctumMode.Strict`** — block tool execution with a synthetic denial message. **`SanctumMode.AuditOnly`** — log breach, allow execution.

**Kernel resource limits (`ResourceLimits.MaxCpuSeconds` / `MaxMemoryMb` / `MaxFileDescriptors`, plus Windows `MaxProcessCount` / `MaxProcessMemoryMb`):** Applied via **`IProcessResourceLimiter`** (Core) / **`ProcessResourceLimiter`** (Infrastructure, `src/RetroDownfall.Arcanum.Infrastructure/Platform/`), invoked from **`CappedChildProcessRunner.RunAsync`** — the shared runner behind both **`execute_command`** (`ArcanumInternalToolServer`) and **`run_spell_script`** (**`ArcanumSpellScriptTool`**). This is OS-level enforcement (setrlimit / cgroups v2 / Windows Job Objects), not a container or VM boundary.
- **macOS:** no cgroups, so the limiter rewrites `ProcessStartInfo` to launch the target through a `/bin/sh -c 'ulimit -t …; ulimit -v …; ulimit -n …; exec "$@"' sh <file> <args…>` prelude. Every original argument is passed as its own `argv` entry (never string-interpolated into the script), so spaces/quotes/`$` pass through unmodified with no shell word-splitting or injection risk. `ulimit -v` maps to `RLIMIT_AS` (virtual address space, not physical RSS) — the best available memory proxy without cgroups.
- **Linux:** prefers cgroups v2. For each invocation the limiter creates a transient `/sys/fs/cgroup/arcanum-{guid}.scope/` directory (a GUID name, not a pid — `Apply()` runs before `Process.Start()`, so the child pid is not yet known; this also sidesteps any pid-reuse race), and writes `memory.max` / `memory.high` (bytes) and a best-effort `cpu.max` (`"1000000 1000000"`, i.e. capped to one core — cgroups v2 clamps the period to at most 1s, so `cpu.max` cannot express a cumulative CPU-time budget; it is a rate throttle only). The **same** `ulimit` shell prelude as macOS is still applied for CPU time and file descriptors (cgroups v2 has no FD controller, and only `RLIMIT_CPU` delivers a real SIGXCPU kill once the CPU-time budget is exhausted); when a cgroup is in play, the prelude's first line has the shell join it (`echo $$ > ".../cgroup.procs"`) before `exec`, so the eventual target process — pid-preserved across `exec` — ends up in the cgroup without the .NET side ever needing the child pid. If `/sys/fs/cgroup` is unmounted or not writable (no delegation), cgroup creation is skipped silently and memory falls back to the `ulimit -v` clause too.
- **Unix descendant cleanup:** on Linux, `CappedChildProcessRunner` launches directly through the trusted util-linux `setsid --` executable when present, so the target enters a new session/process group before it can fork and the runner can kill that captured group after timeout, cancellation, or normal parent exit. This closes the post-`Process.Start` group-assignment race without relying on a shell trap. Hosts without `setsid` and macOS retain the monitored-shell/process-group fallback; macOS additionally uses ancestry tracking, but intentionally detached descendants remain a documented best-effort boundary.
- **Windows (Job Objects):** `Apply()` creates an anonymous Job Object, sets `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` plus process/job memory (`MaxMemoryMb` / `MaxProcessMemoryMb`), per-process user-mode CPU time (`MaxCpuSeconds`), and `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` (`MaxProcessCount`), and returns `ProcessResourceLimiterResult.AssignAfterStart`. The directly started process is the trusted hidden AppContainer broker, which does not interpret or execute repository code. It waits until `IsProcessInJob` confirms assignment, creates the untrusted target with `CREATE_SUSPENDED` and `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`, and only then resumes it. The target and every non-breakaway descendant therefore inherit the already-active Job boundary; failure to observe the job, create the profile, apply ACLs, create the process, or resume it fails closed. Closing the job handle kills remaining descendants. Open file descriptors have no Job Object equivalent and are **not** enforced on Windows. Memory-limit kills that surface as NTSTATUS `STATUS_QUOTA_EXCEEDED` (`0xC0000044`) map to a `ResourceLimit` memory breach when a memory cap was configured; Windows CPU-time kills lack a stable exit code across versions, so wall-clock timeout remains the reliable CPU attribution path on Windows.
- **Detection (Unix):** after `WaitForExitAsync`, the child's exit code is checked for a signal kill using both possible conventions — a direct kernel report (negative signal, e.g. `-9`/`-24`/`-11`) or the shell convention (`128 + signal`) — and only when the corresponding limit was actually configured (`> 0`), to avoid misclassifying an unrelated `exit(137)` as a breach. SIGXCPU (24) maps to CPU; SIGKILL (9) / SIGSEGV (11) map to memory.
- **Breach recording:** a detected kill, or a failure to apply/assign limits, records a `ResourceLimit` breach (**`ISanctumGuard.RecordResourceLimitBreachAsync`**, resolving the campaign by workspace path) and returns a sanitized denial (**`ResourceLimitDenialFormatter`**) — e.g. *"Execution blocked: this tool exceeded the CPU time limit (30s). The invocation has been terminated and recorded as a breach."* The message never contains signal numbers, PIDs, cgroup paths, Job Object handles, or stack traces; that detail is available only in the breach audit log via the Sanctum breaches API.
- **Known gap:** cgroups v2 covers the entire process subtree (grandchildren included), but the `ulimit`/setrlimit path only bounds the direct child — a grandchild spawned by a tool script is not rlimit-bound on macOS (or on Linux when cgroups fell back to setrlimit). On Windows, Job Objects cover the job's process tree once assigned, subject to the post-start assign race above.

**OS filesystem jail (macOS-ARM beta posture):** The same **`CappedChildProcessRunner`** composes env scrub → resource limits → **filesystem jail** → cwd / output caps / cancellation. This is a **filesystem sandbox only** — it does **not** prevent network use by network-capable binaries. Sanctum network policy still applies to model-supplied network targets such as `read_url`; CommLink enforces its URL policy at dispatch. `execute_command` network behavior is **not** solved by the FS jail.
- **macOS (active):** wraps the child with deprecated **`/usr/bin/sandbox-exec`** and an owner-only Seatbelt profile (deny-default + explicit allows). Access classes: workspace / Sanctum `AllowedPaths` → read+write; spell script roots (incl. global spells) → **read+execute** (no write unless also an AllowedPath/workspace); system runtime (`/bin`, `/usr`, `/System`, …) → read+execute, **no write**; per-invocation owner-only **`TMPDIR`** → read+write (no broad `/tmp`). Directory walk uses `(allow file-read* (vnode-type DIRECTORY))` for getcwd/dyld path resolution — **not** whole-volume file-content read. **Critical invariant:** no `(subpath "/")` / `(literal "/")` for file content. Network is explicitly allowed in the filesystem-only profile. Apple may remove the deprecated tool; absence or profile setup failure fail-closes unless `Arcanum:Security:AllowUnsandboxedToolChildren=true`. Distinct from the Linux internal helper argv `__sandbox-exec`.
- **Linux (inactive for this beta):** Landlock / internal **`__sandbox-exec`** helper code remains **in-tree but is not invoked** (probe-first: not activated until Landlock-backed end-to-end wiring is validated). Default is fail-closed with the public message: *"Linux filesystem jail is not active in this beta. Set Arcanum:Security:AllowUnsandboxedToolChildren=true to run without FS confinement, or use macOS for sandboxed command tools."* Escape hatch runs unsandboxed with a warning; resource limits still apply where available. Do **not** conflate this helper with macOS `/usr/bin/sandbox-exec`.
- **Windows (active AppContainer jail; ADR):** selected design is a fresh AppContainer profile/SID per invocation, explicit inherited ACL grants on canonical local-drive roots, and a trusted broker that launches the target suspended only after its Job membership is confirmed. Read/write is granted only to workspace/Sanctum allowed roots and the owner-only invocation temp directory; runtime and spell-script roots receive read/execute. UNC, device/extended paths, alternate data streams, traversal components, and reparse-point roots are rejected before ACL mutation. Original security descriptors are restored in reverse order and the profile/temp/config are removed on success, failure, or cancellation; a random non-reused SID limits residual authority after a host crash, where immediate ACL restoration cannot be guaranteed. AppContainer was chosen over (a) a restricted token plus a filesystem broker, which would require routing every filesystem operation and remains vulnerable to unbrokered runtime calls; (b) temporary ACLs alone, which do not remove the user's ambient access token; and (c) Windows Sandbox/WDAG or Hyper-V containers, which are heavyweight optional features unsuitable for per-tool local execution. Job Objects remain the process-tree/resource boundary and are not represented as filesystem isolation. Health/`arcanum doctor` report Healthy only when the live AppContainer API probe succeeds; otherwise command tools fail closed. `AllowUnsandboxedToolChildren` may permit a non-Sanctum invocation to run without confinement, but never overrides a Sanctum strict path-boundary requirement.
- **Fail-closed:** when the jail cannot be applied and the escape hatch is false, the model-visible result is a clear expected denial (Linux beta message above, missing `/usr/bin/sandbox-exec`, profile setup failure, or Windows Sanctum denial) — **not** a Hub generic internal error / unhandled exception / provider failure.
- **Escape hatch:** `Arcanum:Security:AllowUnsandboxedToolChildren` (default `false`) logs a warning (platform, tool name, campaign id when available — no secret-bearing env/argv) and runs without FS confinement; rlimits / Job Objects still apply where available.
- **Operator visibility:** `ToolChildSandboxStatus` / `ToolChildSandboxCapabilityReporter` feed `arcanum doctor` (Tool Child Sandbox panel) and `GET /api/health` component `ToolChildSandbox` (Healthy only when FS jail is active or an equivalent safe state; Degraded for Windows no-FS-jail, Linux inactive fail-closed, escape hatch, or missing macOS sandbox-exec). Network isolation is always reported as **not provided**.

macOS profile and per-invocation temp roots are created owner-only and captured as no-follow identity-owned artifacts. `CappedChildProcessRunner` attempts their cleanup on every exit path (including jail denial, pre-start failure, cancellation, timeout, and normal completion) within the remaining cleanup deadline. `IdentityOwnedFileSystemCleanup` revalidates kind/identity/single-link ownership, moves the object without overwrite into a fresh owner-only same-parent quarantine, verifies identity again, and only then recursively deletes it. A swapped symlink/path replacement, unknown metadata, permission failure, or elapsed deadline is retained and logged as incomplete rather than deleting an unproved object.

**`workspace_check` is a separate fail-closed capability:** unlike `execute_command` / `run_spell_script`, it never honors `AllowUnsandboxedToolChildren`. `tools/list` includes it only when `WorkspaceCheckExecutionPolicy` and the live runtime agree that macOS Seatbelt, the trusted `dotnet` executable, selected SDK/runtime, and root-owned launch chain are usable; Linux, Windows, and other hosts are unavailable. Each invocation additionally requires and revalidates the canonical pre-existing package cache and restore-input identities. Source and package-cache roots are read-only, while fresh owner-only per-run roots receive seeded restore artifacts and all build/intermediate/test/CLI/cache/temp output. It uses Sanctum-derived OS resource limits even without campaign path-policy dependence. The filesystem profile explicitly permits network. Process-group and descendant cleanup are best effort, and the platform self-test deliberately refuses to claim hard detached-descendant containment; operator Ward approval accepts that a malicious immediate reparent may survive and exfiltrate readable data.

**TOCTOU mitigation:** In-process `read_file_chunk`, `replace_text_block`, and `write_file` capture the validated path's volume/file identity before open, open the handle, then revalidate containment by comparing the opened handle's dev/ino (Unix) or volume serial + file index (Windows) to the pre-open identity. Path containment still uses `WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck`. `replace_text_block` and `write_file` persist via same-directory temp file + atomic `File.Move`.

**API:** **`GET/PUT /api/campaigns/{campaignId}/sanctum`**, **`GET /api/campaigns/{campaignId}/sanctum/breaches`** (paginated: `limit` default 100 clamp 1–1,000, `before` ISO 8601 cursor, `tool` filter; returns `ApiResponse<SanctumBreachQueryResult>` with `Items` + `HasMore`). Protected by **`ApiKeyEndpointFilter`**. Default **`Enabled: false`** on existing and new campaigns (opt-in per campaign). Path-shaped breach detail fields (`RequestedPath`, `ResolvedPath`, `WorkspaceRoot`) are redacted to their filename component (**`SanctumPathRedactor`**) before serialization.

### 11.16 Session lifecycle (`/api/sessions`)

**Purpose:** Grimoire-backed multi-turn chat threads for The Forge, CLI, intelligence persistence, and operator tooling. **Sessions** and **Entries** are the single conversation store (§8.18).

**Store:** `SessionRepository` (`ISessionRepository`) reads and writes `Sessions` / `Entries` through EF Core. Capacity is disk-backed (not RAM-bounded). **`GetSessionAsync`** (Grimoire) still loads a code-owned bounded entry window for inference. Session-list and entry-pagination reads that order or filter by `CreatedAt`/`UpdatedAt` are issued as **parameterized** raw SQL over the sortable UTC text columns (with `json_each` for the FTS id-set and `EXISTS` subqueries for role/model filters), because the EF Core SQLite provider cannot `ORDER BY`/compare a `DateTimeOffset` in LINQ; values are bound, never concatenated.

**Creation:**
- **`POST /api/sessions`** — explicit create with optional `campaignId` and `title`.
- **`POST /api/intelligence/ping-stream`** with null `sessionId` — hub calls `BeginAssistantReplyAsync`, persists user + assistant entries, emits **`sessionBound`** and deprecated **`conversationBound`** NDJSON frames.
- Auto-title: when `Title` is null, clients may set it via **`PATCH /api/sessions/{id}`**; inference may derive a title on first turn (hub behavior unchanged).

**Query (`GET /api/sessions`):** Returns **`ApiResponse<SessionQueryResult>`** with optional filters:
- **`campaignId`**, **`status`** (default `active`; pass `all` for every status including archived).
- **`search`**: substring on **`Title`** or any entry **`Content`**.
- **`title`**, **`role`**, **`model`**, **`from`** / **`to`** on session **`UpdatedAt`**.
- Cursor: **`beforeUpdatedAt`** + **`hasMore`**; the default **`limit`** is code-owned.

**Entries:**
- Inference turns append via the hub (`IGrimoireRepository`).
- **`POST /api/sessions/{id}/entries`** — manual append (operator or Studio); rejects archived sessions; publishes to **`SessionEventHub`** for live SSE subscribers.
- There is **no update API** for entry content after insert. Gated memory-management routes (when **`Arcanum:Features:MemoryManagement`** is true) allow **delete**, **pin** / **unpin**, and **compact** (`DELETE …/entries/{entryId}`, `POST`/`DELETE …/entries/{entryId}/pin`, `POST …/compact`) — see §4.3.

**CLI lifecycle:** `arcanum session` exposes list/show/chat/entries/watch/fork/rename/archive/export/rest/attachments/delete-entry/pin-entry/unpin-entry/compact without opening the Grimoire. Session selectors use the shared ID/title/prefix picker; entry selectors first resolve a session, then page its entry API. `arcanum session chat [session]` and root `arcanum chat --session <id-or-title>` both enter the existing chat loop. `session show` combines `GET /api/sessions/{id}` and the attachments metadata endpoint; `SessionDetailDto` includes the persisted `TotalTokensUsed`, `TotalCostUsd`, and `ForkedFromSessionId` projections. Archived sessions remain readable, exportable, and forkable. API error codes flow through unchanged so feature-gate and lifecycle failures remain actionable.

**Metadata update (`PATCH /api/sessions/{id}`):** Accepts **`UpdateSessionRequest`** with optional **`title`** (`string?`) and **`status`** (`active` | `archived`). Only supplied (non-null) fields change; an empty or whitespace `title` clears it to `null`. An unrecognized `status` returns **400** `Session.InvalidStatus`. Setting `status` to `archived` has the same soft-delete effect as `DELETE /api/sessions/{id}` (PATCH returns **200** + the updated `SessionDetailDto` rather than **204**).

**Archive vs purge:**
- **`DELETE /api/sessions/{id}`** sets **`Status = archived`** (soft delete; **204**). Repeat calls are idempotent.
- **`IGrimoireRepository.PurgeSessionAsync`** — hard delete (cascade entries); **not** exposed on the public API.

**Export / analytics:**
- **`GET /api/sessions/{id}/export?format=json|markdown`**
- **`GET /api/sessions/analytics`** — aggregate counts over Grimoire (sessions, entries by role, tokens, per-model breakdowns).

**Live stream (`GET /api/sessions/{id}/stream`):** `text/event-stream`. Subscribes to **`SessionEventHub`** **before** the DB read (entries published during replay are not lost), replays a code-owned bounded window of the most recent entries in ascending order, emits `data: {"type":"live"}\n\n`, then forwards live entries (hub inference + manual append), de-duplicating any already replayed. Each subscriber channel is bounded `DropOldest`; a slow reader may miss live Entries and receives a warning in server logs. Restart drops subscriptions but not Entries, so Grimoire replay remains authoritative. On disconnect, best-effort `data: [DONE]\n\n`. CLI `session watch [session] [--since <entry-id>]` ignores the live sentinel, stops on `[DONE]`, renders terminal lines normally, and emits newline-delimited `EntryDto` JSON with `--json`.

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
- `SessionSummaryDto.ForkedFromSessionId` exposes the same lineage in session-query results so clients can render branches while retaining the primary `UpdatedAt DESC` order.
- **Attachments:** copies **Bound** attachments into a new byte tree under the fork session id (new attachment ids; remapped `EntryId`s). Full fork includes Bound rows with `EntryId` null; a cutoff fork (`upToEntryId`) copies only rows whose non-null `EntryId` is among the copied entries. Bytes are pre-copied and hash-verified before the DB write; `Session` / `Entries` / `SessionAttachments` insert in one EF ambient transaction (raw SQL enlisted).
- **Fork depth guard:** the lineage chain (`ForkedFromSessionId` walked back to a root) uses a code-owned depth cap. Exceeding it returns **409** `Session.ForkDepthExceeded` — protecting against unbounded fork chains inflating storage and lineage-walk cost.
- Forking a session that is already at the code-owned entry cap fails the same way a normal append would (`Session.TooManyEntries`).

Fork-specific error codes: `Session.NotFound` (source missing), `Session.EntryNotFound` (`upToEntryId` invalid or from another session), `Session.ForkDepthExceeded`.

**Command Center workflow:** `/fork` creates a complete branch; `/fork at` uses the selected
entry as the inclusive cutoff (the explicit-id form is retained for scripts); `/fork alternative`
cuts off before the selected assistant answer and submits its preceding user prompt again only
after the branch opens. `/branch parent` and `/branch child` navigate visible lineage. The header and
session list use compact `⑂` markers. Before copying at least ten attachments or 10 MiB, the
operator must explicitly continue with `/fork confirm`. Fork requests run asynchronously, and the
active binding is changed only after the fork's detail, entries, and attachment metadata reload
successfully. Depth, missing-cutoff, and attachment-copy failures provide recovery guidance and
leave the source transcript selected.

**Error codes (§11.16 overall):** `Session.NotFound`, `Session.EmptyContent`, `Session.Archived`, `Session.InvalidStatus`, `Session.EntryNotFound`, `Session.ForkDepthExceeded`.

**Key types:** `Session`, `Entry`, `ISessionRepository`, `SessionRepository`, `SessionEventHub`, `SessionSettings`, `ForkSessionRequest`, The Forge DTOs under **`Core.TheForge`**.

### 11.17 `Idempotency-Key` request replay

Opt-in, client-supplied replay protection (Stripe-style semantics) for the eight side-effecting inference endpoints: **`POST /api/intelligence/ping`**, **`POST /api/intelligence/ping-stream`**, **`POST /v1/chat/completions`** (both buffered and streaming), **`POST /v1/embeddings`**, **`POST /api/spells/{name}/execute`**, **`POST /api/spells/{name}/execute-stream`**, **`POST /api/prompts/{id}/execute`**, and **`POST /api/prompts/{id}/execute-stream`**. Requests without an `Idempotency-Key` header are unaffected — the feature is entirely bypassed at effectively zero cost.

**Claim key ≠ fingerprint:** claim identity is `SHA-256(principal + API version + HTTP method + normalized route + Idempotency-Key)`. Fingerprint is `SHA-256(canonical body + route + selected Content-Type)`. Same key with a different fingerprint → **409** `Security.IdempotencyConflict`. Only **terminal** Completed claims (writer-marked, within byte cap) are replayable; cancelled/partial/over-cap streams → Abandoned. Durable table: `IdempotencyClaims` (raw SQL); legacy `IdempotencyKeys` remains for TTL sweep compatibility.

**Coordination and ownership:** an in-process `ConcurrentDictionary` local-flight coordinator is acquired **before** durable lookup/acquire, so same-process competitors wait for the leader's response completion and then re-read/replay instead of racing the handler. Durable owner IDs are `{process-instance-guid}:{execution-guid}`. A live orphan carrying this process-instance prefix can be retired and reclaimed; a live claim owned by another process is confirmed as cross-process work and returns **409** without invoking the handler: native `Security.IdempotencyInProgress`, or OpenAI `code: "idempotency_in_progress"` in the standard OpenAI error envelope. Completed same-fingerprint claims replay status/content type/body verbatim; fingerprint mismatch remains `Security.IdempotencyConflict` / OpenAI `idempotency_conflict`, also 409.

**Lease and heartbeat:** production timing is currently code-owned, not configurable: a **five-minute lease**, one-minute heartbeat interval, and 24-hour maximum owned execution lifetime. The heartbeat renews only `Running` rows still owned by the exact owner ID. A false renewal, heartbeat timeout, unsafe repeated failure near lease expiry, or maximum-lifetime expiry cancels a linked ownership-loss token supplied to the endpoint while leaving the original caller token unchanged; the old owner stops instead of continuing after another process can reclaim.

**Key and hashing (legacy note):** older docs described a single hash of key++body. Prefer the claim/fingerprint split above. `IdempotencyEndpointFilters` derives canonical body bytes one of two ways depending on how the endpoint binds its request:
- **`ForBoundArgument<TRequest>`** (`/api/intelligence/ping`, `/v1/embeddings`) — the already-model-bound request DTO is re-serialized through the same source-generated `JsonTypeInfo<TRequest>` used on the wire. No raw body re-read needed.
- **`ForRawBody`** (`/api/intelligence/ping-stream`, `/v1/chat/completions`) — these handlers read `HttpContext.Request.Body` themselves, so the filter calls `Request.EnableBuffering()`, copies the raw bytes for hashing, then rewinds the stream to position 0 before invoking the handler.

**Header validation:** an `Idempotency-Key` longer than 256 characters is rejected with **400** `Security.IdempotencyKeyTooLong` (`/api` `ApiResponse<string>` envelope, or `/v1` `invalid_request_error` envelope depending on route) *before* any body buffering or cache lookup — a fast, cheap rejection.

**Cache hit:** the handler is **never invoked** — `IdempotencyEndpointFilters` short-circuits with a small `IdempotencyReplayResult` that writes the cached status code, content type, and body bytes verbatim.

**Cache miss (buffered *and* streaming, same mechanism):** `HttpResponse.Body` is substituted with an `IdempotencyBufferingStream` that tees every write into a capped in-memory buffer while forwarding to the real response stream (and keeps buffering if the client disconnects under continue-then-replay). An `HttpResponse.OnCompleted` callback persists only when the writer marked the response terminal and the buffer stayed within cap. Explicitly terminal zero-byte responses (including 204) persist an empty string and replay with an empty body; an unmarked ordinary zero-byte/partial response is Abandoned.

**Disconnect:** the code-owned `Auto` policy continues inference after client disconnect when an `Idempotency-Key` is present so the claim can Complete for later replay; without a key, cancel → Abandoned. Partial billed cost is still ledgered either way.

**Oversized responses are never cached, never truncated:** once the tee buffer would exceed the code-owned replay-buffer cap it releases the memory it was holding and permanently stops accumulating; the client-visible response is completely unaffected — only the cache write is skipped. A `BufferingStream` failure (`OutOfMemoryException`, `ObjectDisposedException`) is handled the same way: stop buffering, keep streaming, skip the cache write, log a warning.

**TTL and expiry:** claim rows older than the code-owned replay TTL are swept by `UnseenServantService` (`IIdempotencyClaimStore.DeleteExpiredAsync` plus legacy `IIdempotencyStore`).

**Cleanup:** no dedicated `BackgroundService`. `UnseenServantService` (§21, the existing 1-minute scheduler tick) runs expiry deletes once at host startup and thereafter every hour. A sweep failure is logged and retried on the next scheduled tick — it never blocks the scheduler's other jobs.

**Persistence:** `IdempotencyClaims` (claim key hash, fingerprint, state machine, lease, optional response body, optional late-bound `RunId`) — embedded raw-SQL table (not part of the compiled EF model).

**Failure boundaries:** lookup/acquire/same-process-retirement failure occurs before handler execution and fails open through the still-held local flight, so that request executes the handler exactly once without durable replay. After handler execution starts, completion/abandon/failure-save faults are logged and the coordinator is released, but the handler is **never re-entered**. Partial, caller-aborted, unmarked-empty, and over-cap responses stay Abandoned/non-replayable; a later request may execute fresh. Empty explicitly terminal responses remain replayable as above.

**Error codes:** `Security.IdempotencyKeyTooLong`, `Security.IdempotencyConflict`, `Security.IdempotencyInProgress`.

**Key types:** `IIdempotencyClaimStore`, `IdempotencyClaimStore`, `IdempotencyEndpointFilters`, `IdempotencyReplayResult`, `IdempotencyBufferingStream` (Api, `Security`); legacy `IIdempotencyStore` retained for sweep.

### 11.18 OpenAI moderations (`POST /v1/moderations`)

**Purpose:** OpenAI-compatible content moderation route. Arcanum does **not** run a moderation model. The endpoint always returns **501 Not Implemented** with `OpenAiErrorResponse` (`type: "invalid_request_error"`, `code: "not_supported"`), matching the images/audio stubs (§11.19).

**Configuration:** no setting enables this route.

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

**Storage naming and encryption (security):** every uploaded file is stored under a **fresh
`Guid`-named path** (`{FilesDirectory}/{id:N}`, computed by
`UploadedFileStorage.ResolvePath`), never the client-supplied filename — path traversal and
filename collisions are structurally impossible. The bytes are a purpose-bound `ARCABLOB`
authenticated-encryption envelope (§5.4.6). The original filename is retained only as SQLCipher row
metadata (`UploadedFileRecord.Filename`), used for `Content-Disposition` on download and echoed back
in the wire `file` object. `EncryptionVersion`, `EncryptionKeyId`, and `PlaintextSha256` identify
and verify the envelope without exposing key material. During the supported migration window,
version-zero rows use the mixed-mode rules in §5.4.6; new uploads always record version one, key id,
length, and SHA-256.

**Endpoints:**
- **`POST /v1/files`** — `multipart/form-data`: `file` (binary, required) + `purpose` (string,
  required — any non-empty value; Arcanum does not enforce OpenAI's specific purpose enum because
  it has no per-purpose behavior beyond what `/v1/batches` expects for `purpose: "batch"`).
  Plaintext is streamed directly into an atomic encrypted write. Returns **201** +
  `OpenAiFileObject`.
- **`GET /v1/files?purpose=`** — list, optionally filtered; **200** + `OpenAiFileListResponse`.
- **`GET /v1/files/{id}`** — metadata; **404** `not_found` for an unknown or malformed id.
- **`DELETE /v1/files/{id}`** — deletes the Grimoire row and the on-disk file (best-effort on the disk side — a failed disk delete never blocks the metadata delete); **200** + `OpenAiFileDeleteResponse`.
- **`GET /v1/files/{id}/content`** — authenticates and streams decrypted bytes; it never buffers
  the complete file. **`Content-Type`** is the file's stored MIME type (falling back to
  `application/octet-stream` only if none was recorded — not hardcoded to octet-stream).
  **`Content-Disposition: attachment`** always — **never `inline`** — this is the primary XSS
  mitigation against an uploaded `.html`/`.svg` payload being rendered if a browser hits this URL
  directly; the extension/MIME cross-check below is secondary defense-in-depth, not the primary
  one. A missing/wrong key or invalid envelope fails closed; ciphertext is never returned.

**Wire id scheme:** `id` is `"file-{guid:N}"` (32 hex chars, no dashes). `GET`/`DELETE`/`.../content` parse this back to a `Guid`; a malformed id (wrong prefix, not valid hex) is treated as "not found" (**404**), never a **500**.

**Upload validation (in order — first failure wins):**
1. `file` present and non-empty, `purpose` present and non-empty → else **400** `missing_required_parameter`.
2. Filename must not exceed 255 characters, and (defense-in-depth; unreachable through any conformant HTTP client, whose header-quoting rejects embedded control characters before the request is even sent) must not contain an embedded null byte → else **400** `invalid_value`.
3. Size must fit the code-owned upload envelope → else **413** `invalid_value`. The endpoint's Kestrel request-body limit is raised to the physical ceiling (`WithFileUploadRequestBody`) so the *handler* returns this structured JSON error instead of Kestrel aborting the connection first.
4. Extension/declared-Content-Type cross-check (`UploadedFileMimeValidator.IsExtensionMimeMismatch`) — a *known* extension (`.png`, `.jsonl`, etc.) paired with an unexpected declared type is rejected (**400** `invalid_value`); an *unrecognized* extension is always allowed through (nothing to cross-check against).
5. If `Arcanum:Security:AllowedUploadMimeTypes` is non-empty, the declared type must be in that operator allowlist → else **400** `invalid_value`.

**Permissions:** the files directory, every encrypted envelope, and same-directory ciphertext temp
get owner-only permissions via `SecureFilePermissions` (600 Unix / owner ACL Windows). Atomic
flush/verify/rename behavior is §5.4.6.

**Error codes:** `Files.NotFound` (404), `Files.TooLarge` (413), `Files.InvalidMimeType` (400) — registered in the shared catalog (§8.23) for consistency and reuse by `/v1/batches`, even though the `/v1/files` handlers themselves construct their OpenAI-shaped error envelopes directly (matching every other `/v1` endpoint) rather than routing through `ArcanumErrorMapper`.

**Key types:** `FilesSettings`, `IUploadedFileRepository`, `UploadedFileRecord`, `UploadedFileRepository` (Infrastructure), `UploadedFileStorage` (pure path helper), `UploadedFileMimeValidator`, `OpenAiFileObject`, `OpenAiFileListResponse`, `OpenAiFileDeleteResponse`.

**Native CLI surface:** `arcanum file upload <path>` streams multipart bytes through
`FileBatchApiClient` and defaults `purpose` to `batch`; `--purpose` and `--content-type` make both
wire fields explicit. `file list [--purpose]`, `file show <id>`, `file download <id> [--output]`,
and `file delete <id>` call only the authenticated `/v1/files` routes. Successful JSON mode emits
the bare OpenAI wire object/list/delete response, not `ApiResponse<T>`. Downloads first retrieve
metadata, discard all server-supplied path components, sanitize the leaf filename, stream content
to a same-directory uniquely named stage file, flush, and atomically replace the destination.
Existing destinations and deletion require `IConfirmationPrompt`; redirected/noninteractive use
fails closed unless recursive `--yes` grants the mutation. The API filename is metadata only and
never becomes an unchecked local path.

### 11.21 OpenAI batches (`/v1/batches`)

**Purpose:** OpenAI-compatible asynchronous bulk chat-completion processing over an uploaded JSONL file (§11.20). Only `endpoint: "/v1/chat/completions"` is supported; other endpoint values are rejected with **400** `invalid_value`.

**Layering note (why the processor lives in the Api project, not Infrastructure):** every other background poller (`EntryWeavingService`, `SagaExtractionService`, `UnseenServantService`, ...) lives in `RetroDownfall.Arcanum.Infrastructure`. `BatchProcessingService` is the one exception — it must call `IArcanumIntelligenceProvider.ExecutePromptAsync` and construct/parse the `/v1` OpenAI DTOs (`OpenAiChatRequest`/`OpenAiChatResponse`/the JSONL wrapper types), all of which live in the **Api** project, and the dependency direction only ever goes Api → Infrastructure. Rather than move those DTOs down into Core (a large, unrelated refactor) or duplicate them, `BatchProcessingService` is registered and hosted from the Api project (`ApiBootstrapper.AddArcanumApiServices`, `services.AddHostedService(sp => sp.GetRequiredService<BatchProcessingService>())`), exactly mirroring how `IArcanumIntelligenceProvider`'s own concrete implementation (`WizardIntelligenceProvider`) is Api-hosted despite the interface living in Core.

**Endpoints** (metadata CRUD only — see below for the actual JSONL processing):
- **`POST /v1/batches`** — body `{input_file_id, endpoint, completion_window}` (`completion_window` is accepted but not enforced; expiry is code-owned). Validates `input_file_id` resolves to an existing uploaded file (§11.20) and `endpoint` equals `/v1/chat/completions`. Creates a `Batches` row with `status: "validating"` and returns immediately — **200** + `OpenAiBatchObject`. The actual processing happens out-of-band.
- **`GET /v1/batches/{id}`** — current status + `request_counts`; **404** for unknown/malformed id.
- **`GET /v1/batches?status=`** — list, optional status filter; `{object: "list", data: [...], has_more: false}` (`has_more` is always `false` — no pagination cursor yet).
- **`POST /v1/batches/{id}/cancel`** — sets `status: "cancelled"` if not already terminal; idempotent (cancelling an already-terminal batch just returns its current state, matching OpenAI rather than erroring on a double-cancel). `BatchProcessingService`'s cancellation watcher (below) observes this and stops in-flight processing promptly.
- **`POST /v1/batches/{id}/reset`** — operator recovery via shared `IBatchRecoveryService`: resets a batch stuck in `in_progress` back to `validating` (CAS) so the background processor will pick it up again. Rejects with **409** if `BatchProcessingService` currently has the batch in flight (best-effort race window; the real guard is the service's `_inFlight.TryAdd` when it actually starts processing). Rejects with **400** if the input file metadata or on-disk file is missing, because the batch cannot be safely reprocessed. This is an Arcanum extension, not an OpenAI standard route.

**Wire id scheme:** `"batch_{guid:N}"` (underscore, matching OpenAI's real batch ids — distinct from `/v1/files`' hyphenated `"file-{guid:N}"`).

**`request_counts` computation:** there are no dedicated count columns on `Batches` (`Id,
InputFileId, Endpoint, Status, CreatedAt, CompletedAt, OutputFileId, ErrorFileId`).
`BatchRequestCounter` computes `{total, completed, failed}` on every `GET` by opening authenticated
plaintext streams for the encrypted input/output/error artifacts: `total` = non-empty input line
count; `completed`/`failed` = outcome counts parsed from the output file's
`BatchJsonlResponseLine.Error` (`null` → completed, populated → failed) plus parse-failure lines in
the error file. Best-effort — a missing, unreadable, unauthenticated, or undecryptable file
contributes `0` rather than erroring the metadata `GET`.

**Startup recovery:** `BatchProcessingService.StartAsync` calls
`IBatchRecoveryService.ReconcileStrandedAsync` before Kestrel accepts work. Every DB-stranded
`in_progress` batch is CAS-transitioned: → `validating` only when input metadata exists and the
complete encrypted input authenticates; else → `failed` (reason logged only — no failure-reason
column). Same recovery path powers `/reset`.

**`BatchProcessingService` (background processor):**
- Polls every 5 seconds via `PeriodicTimer` (same shape as `UnseenServantService`/`EntryWeavingService`).
- **Expiry sweep (every tick):** any non-terminal batch (`validating`/`in_progress`) older than the code-owned expiry is expired. If the batch is **not** currently in-flight in the processor, it is force-marked `status: "expired"` and its input/output/error files are deleted from disk (best-effort — a delete failure is logged and does not block the status update). If the batch **is** in-flight, the expiry sweep signals that batch's processing cancellation token and does **not** delete files; the processor/finalizer marks `expired` and performs file cleanup after cancel completes.
- **Dispatch:** picks up `validating` batches, bounded by `Arcanum:Execution:MaxConcurrentBatches` across the whole server (tracked in an in-process `ConcurrentDictionary`). Crash mid-batch leaves `in_progress` until startup reconcile or `/reset`.
- **Per-batch processing:** sets `status: "in_progress"`, authenticates and **streams** the decrypted
  input line-by-line (does not load the entire JSONL into memory), and parses each as a
  `BatchJsonlRequestLine` (OpenAI's real wrapper shape:
  `{custom_id, method, url, body: OpenAiChatRequest}` — not a bare chat request). A line that fails
  to parse is recorded to the **error file** as `{"line": N, "error": "..."}`
  (`BatchJsonlParseError`) and does not consume an inference call. A line that parses successfully
  is executed via `OpenAiV1Endpoints.ExecuteChatRequestForBatchAsync` (reuses the same
  `OpenAiChatCompletionMapper.ToPingRequest` mapping and buffered `OpenAiChatResponse` shape as live
  `POST /v1/chat/completions`, minus that endpoint's HTTP-layer pre-checks like multimodal part
  limits or `tools`/`tool_choice` rejection — a line that would trip one of those still gets a clean
  per-line failure via the intelligence provider's own validation) — the **outcome always goes to
  the output file** as a `BatchJsonlResponseLine`, whether it succeeded (`response` populated,
  `error: null`) or the inference call itself failed (`response: null`, `error` populated) — only
  JSON-parse failures go to the error file, matching OpenAI's own input-file-vs-per-request-error
  distinction.
- **Bounded per-batch concurrency:** valid lines within one batch run through `Parallel.ForEachAsync` bounded by `Arcanum:Execution:MaxConcurrentRequestsPerBatch`, so one large batch can never monopolize the shared inference hub.
- **Mid-batch cancellation:** a side task polls the Grimoire every 2 seconds for this batch's `status` flipping to `"cancelled"` (set by `POST .../cancel`) and, if seen, cancels a linked `CancellationTokenSource` so `Parallel.ForEachAsync` stops promptly instead of draining every remaining line first; whatever output/error accumulated up to that point is still written and attached.
- **Finalization:** writes output/error JSONL **incrementally into owner-only encrypted stage
  envelopes** as lines complete (bounded per-line/chunk memory; no plaintext temp), verifies and
  moves non-empty ciphertext into the uploaded-files directory via the same repository as
  `/v1/files` (`purpose: "batch_output"` / `"error"` and the distinct `BatchArtifact` derived key),
  then sets the terminal status (`completed` or `cancelled`) plus
  `CompletedAt`/`OutputFileId`/`ErrorFileId`. An unhandled exception anywhere in this pipeline is
  caught at the top level and marks the batch `failed`.

**Error codes:** `Batches.NotFound` (404), `Batches.InvalidEndpoint` (400), `Batches.InputFileNotFound` (404) — registered in the shared catalog (§8.23) for consistency, even though the `/v1/batches` handlers construct their OpenAI-shaped error envelopes directly like every other `/v1` endpoint.

**Key types:** `BatchesSettings`, `IBatchRepository`, `BatchRecord`, `BatchStatuses`, `BatchRepository` (Infrastructure), `BatchProcessingService`, `IBatchRecoveryService` / `BatchRecoveryService`, `BatchRequestCounter`, `OpenAiBatchRequest`, `OpenAiBatchObject`, `OpenAiBatchRequestCounts`, `OpenAiBatchListResponse`, `BatchJsonlRequestLine`, `BatchJsonlResponseLine`, `BatchJsonlResponseBody`, `BatchJsonlError`, `BatchJsonlParseError`.

**Native CLI surface:** `arcanum batch create <input-file>` accepts either an existing `file-*`
id or a local JSONL path. A local path is streamed through a client preflight that catches only
obvious wrapper errors (valid JSON object, unique nonblank `custom_id`, exact `POST`, exact
`/v1/chat/completions`, object `body`) with a line number; the server remains authoritative for
the full chat request and batch policy. A passing local file is uploaded with `purpose: batch` and
`application/jsonl`, then its returned id is posted to `/v1/batches`, so one command starts the
asynchronous batch. `batch list [--status]`, `show`, `cancel`, and `reset` preserve the server's
request-count, idempotent cancellation, and stuck-only reset semantics. `batch watch <id>` polls
with cancellation-aware exponential backoff bounded from 1 ms through 10 seconds and stops only
on `completed`, `failed`, `cancelled`, or `expired`; JSON mode writes only the terminal object.
`batch output|errors <id> [--output]` resolves the server-owned artifact id and uses the same safe,
atomic, overwrite-confirmed download path as `file download`. All successful structured output is
the bare OpenAI shape or a source-generated local download receipt.

### 11.27 Native web research (`web_search` / `read_url`)

**Purpose and gate:** the provider-neutral `IWebResearchProvider` contract supports synthesized
search and direct URL reading without an MCP server or embedded browser. The family is disabled by
default and gated by `Arcanum:Features:WebBrowsing`. `Arcanum:Integrations:WebResearch` selects the
search provider and `sonar` / `sonar-pro` model; timeouts, redirect counts, body caps, result caps,
and citation/link limits remain code-owned.

**Tool surface:** new inference catalogs advertise `web_search({query})` and
`read_url({url})`. Both are hand-authored `AIFunction` schemas and return source-generated,
structured JSON envelopes bounded below the generic tool-result materializer ceiling.
`web_search` returns a synthesized, untrusted-framed answer, ordered one-based citations, truncation
metadata, and provider usage. `read_url` returns untrusted-framed Markdown, final URL, title, and
bounded links. Artifact Attunement applies to both. A legacy spell declaration of `browse_web`
canonicalizes to `read_url`; `browse_web` remains only as a direct-invoke compatibility alias and
is not advertised in new model toolsets. The first-class CLI uses the typed `/api/web/*` workflow
endpoints rather than exposing the legacy tool schema.

**First-class workflow surface:** `POST /api/web/search` accepts a result/citation count (1–20),
optional freshness (`day`, `week`, `month`, `year`), and bounded include/exclude domain lists.
Perplexity receives `search_recency_filter` and a combined `search_domain_filter` (excluded domains
use the provider's `-domain` notation); the configured citation ceiling remains authoritative.
`POST /api/web/browse` accepts `static` or `javascript`. Static mode delegates to
`LocalHttpWebProvider`; JavaScript mode returns **503**
`WebResearch.JavaScriptRenderingUnavailable` with an explicit static-mode fallback until a
server renderer is configured. No client-side renderer or egress bypass exists.

`POST /api/web/research` is the only research orchestrator. `WebResearchWorkflowService` validates
1–20 sources, 1–5 hops, 64–32,768 synthesis tokens, and a nonnegative optional reported-cost
ceiling. It emits NDJSON `limits`, `progress`, `result`, or `error` frames. Search hops execute on
the server, citation URLs are deduplicated before at most `maxSources` static reads, and the final
model call receives a bounded untrusted-data prompt with all tools disabled. The synthesis model,
token accounting, inference audit (`requestType:research`), and optional existing `SessionId` stay
inside the host. Reported search-provider cost is checked between hops; no additional hop begins
after the ceiling is exceeded. Progress stages are `searching`, `fetching`, `rendering`, and
`synthesizing`.

The CLI reserves stdout for the final payload and stderr for visible limits, progress, save/attach
receipts, and errors, so piping remains deterministic. Terminal and Markdown outputs contain
numbered citation references; JSON emits one source-generated `Web*WorkflowResult`. `--save`
performs a same-directory temporary write followed by atomic replacement. `--attach-to-session`
persists the final Markdown through `ISessionAttachmentStore` only when attachments are enabled and
the target session exists. `--continue-session` binds the server synthesis turn to the selected
session; session selectors use the shared exact-ID/exact-name/unique-prefix rules.

**Providers:** `PerplexityWebProvider` performs one non-streaming
`POST https://api.perplexity.ai/v1/sonar` call and preserves provider citation indices/order,
search-result metadata, token counts, query counts, and reported cost. It performs no automatic
retry of the billable POST. `LocalHttpWebProvider` follows a bounded number of manually validated
redirects, accepts static textual content, and uses `HtmlAgilityPack` to remove active/hidden
content and convert headings, prose, lists, links, quotes, and code to bounded Markdown. It never
executes JavaScript and no Playwright/Puppeteer/Chromium dependency is permitted.

**Credential boundary:** Perplexity keys are resolved at invocation time. An exact configured
environment reference (default `ARCANUM_PERPLEXITY_API_KEY`) takes precedence; otherwise
`IWebResearchCredentialStore` reads the OS credential manager with an owner-only,
Data Protection-encrypted fallback. `arcanum key provider set|status|delete perplexity` manages the
local credential without an HTTP call, and no command, configuration DTO, log, or metric exposes
the key value.

**Egress and campaign policy:** both named clients use
`OutboundUrlGuard.CreateUntrustedEgressHandler()`, so DNS-rebind, loopback, private, link-local, and
CGNAT targets are rejected at connection time. `read_url` also validates the initial URL and every
redirect before sending. `ToolExecutionPipeline` applies the campaign Sanctum network policy to
model-supplied `read_url` targets and the legacy alias before egress.

**Deadlines and errors:** each complete provider operation has a strict 15-second linked deadline,
including credential resolution, redirects, headers, and bounded body reading; caller cancellation
is propagated rather than relabeled. Stable `WebResearch.*` errors distinguish missing credential,
authentication-or-credits failure, provider-declared quota exhaustion, rate limiting, invalid
response, timeout, invalid/blocked URL, redirect overflow, unsupported content, bot protection,
JavaScript-only shells, and empty content. HTTP 403 and effectively empty application shells return
a structured `read_url` error with `suggestedTool:"web_search"`.

**Telemetry and DI:** providers record aggregate-only request outcome, duration, prompt/completion/
total/reasoning/citation tokens, search-query count, and reported cost through `ArcanumMetrics`;
query text, URLs, response bodies, and credentials never become tags. Endpoint mapping eagerly
starts `TelemetryService` before traffic is accepted. `WebResearchProviderCatalog` rejects
duplicate names and is injected into the tools. Separate Perplexity and local HTTP named clients
use the untrusted-egress handler with system proxies disabled (so DNS validation/pinning cannot be
bypassed) and infinite client timeout because the linked operation-level deadline is authoritative.

**Key types:** `IWebResearchProvider`, `IWebResearchProviderCatalog`, `WebResearchProviderCatalog`,
`PerplexityWebProvider`, `LocalHttpWebProvider`, `WebResearchCredentialStore`,
`ArcanumWebSearchTool`, `ArcanumReadUrlTool`, `WebToolResultSerializer`, and
`WebResearchTelemetry`, plus `WebResearchWorkflowService`, `WebWorkflowEndpoints`,
`WebWorkflowCommands`, and the `Web*WorkflowRequest` / `Web*WorkflowResult` contracts.

---

### 11.28 Diagnostic MCP Invocation (`POST /api/mcp/tools/invoke`)

**Purpose:** an operator-facing diagnostic endpoint to directly invoke **external** MCP tools by name, outside of an inference turn — for verifying that a configured MCP server actually responds, that tool arguments serialize correctly, and that output formatting/capping behaves as expected. It is **not** model execution and **not** an unrestricted tool bypass: it is policy-constrained, authenticated, and limited to external MCP servers.

**Policy — external MCP only:** internal-tool diagnostics cannot safely use `ToolExecutionPipeline` without a real campaign: `EnforceSanctumAsync` short-circuits when `turnContext.Campaign is null`, and `RequiresWardForTool` only wards `write_file`/`replace_text_block`/`delete_lexicon`/`run_spell_script` when `campaignRequiresWard=true`. A campaign-less diagnostic invoke would therefore get **no Sanctum path/network validation** and would **not** ward four of the five Forbidden Arts. The endpoint consequently excludes internal tools and permits only policy-constrained external MCP tools.

**What is blocked:**

- The internal in-process server `arcanum-internal` (the clean discriminator — its tools are the high-risk internal handlers `execute_command`, `write_file`, `replace_text_block`, `delete_lexicon`, `read_file_chunk`, `list_directory`, etc.).
- High-risk names (`execute_command`, `write_file`, `replace_text_block`, `delete_lexicon`, `run_spell_script`, `apply_patch`, and `workspace_check`) are blocked **by name** before any server lookup, so a third-party MCP server that exposes a colliding name is still rejected with `Mcp.DiagnosticBlocked`.
- The blocked-tool error message is fixed: *"This tool cannot be invoked from the diagnostic endpoint because it is a Forbidden Art or requires the Master tool execution pipeline."*

**What is allowed:**

- Any tool exposed by a **running**, **visible**, **external** MCP server (i.e. not `arcanum-internal`). Workspace-local external servers must be trusted — `IMcpConnectionManager.GetServerStatusesAsync` already hides untrusted workspace-local servers, so they never reach the diagnostic service.
- Optional `workingDirectory` scopes the visible surface; optional `serverName` disambiguates when the same tool name is provided by more than one running external server (else **400** `Mcp.AmbiguousTool` listing the candidates).

Collision behavior is fail-closed and provenance-preserving: blocked names remain blocked even on an external server; for an allowed name, `arcanum-internal` is removed before external candidate counting, multiple external providers require `serverName`, and the selected server-bound `AIFunction` is invoked directly rather than re-resolving the bare name on the merged inference surface.

**Inherited caps (no new knobs):**

- **Output cap:** the code-owned MCP bridge output cap is enforced inside `McpBridgeTool` via `McpSecurityLimits.TruncateUtf8` (UTF-8-boundary-safe truncation with a `[truncated: exceeded N bytes]` marker). The diagnostic service detects that marker in the result text and sets `truncated: true` on the response.
- **Timeout:** the code-owned MCP request timeout is enforced as a linked `CancellationTokenSource` around `AIFunction.InvokeAsync`; on expiry the service returns **504** `Mcp.DiagnosticTimeout` (the MCP SDK's own per-request timeout still applies underneath).
- **Auth:** inherits `X-Arcanum-Key` from the `/api` group filter — no unauthenticated access.
- **Secrets:** exception messages are length-capped (512 chars) before being returned to the caller, as a defensive last step on the rare exception path. The MCP bridge already formats tool output, so this is not a substitute for the bridge's own handling.

**Invoke path:** after external-only discovery (statuses + optional `serverName` disambiguation; `arcanum-internal` excluded **before** candidate counting so an internal name collision never yields `AmbiguousTool`), the service calls **`IMcpConnectionManager.GetToolAsync(serverName, toolName, workingDirectory)`** to obtain the `AIFunction` bound to that managed server's own client — **never** re-resolving by bare name on the merged `GetAvailableToolsAsync` inference surface. `McpBridgeTool` remains `internal` to Infrastructure; the API project treats the result as `AIFunction`. The result text is parsed as JSON when possible (else wrapped as a JSON string) and returned as `McpToolInvokeResponse` { `result`, `serverName`, `toolName`, `durationMs`, `truncated` }. A tool that returns `isError: true` makes `McpBridgeTool` throw `InvalidOperationException`, which the service maps to **400** `Mcp.ToolError`.

**Built-ins unchanged:** `POST /api/tools/invoke` (§11.27) exposes only the bounded built-in tools (`get_local_system_time`, `get_arcanum_system_info`, and `web_search` / `read_url` when enabled); it also accepts `browse_web` as an invocation-only compatibility alias. It does **not** go through Ward/Sanctum — acceptable only because the registry is deliberately limited and web egress retains the unconditional SSRF guard. The two endpoints are complementary: `/api/tools/invoke` for built-ins, `/api/mcp/tools/invoke` for external MCP.

**CLI diagnostics:** `arcanum mcp invoke <tool>` discovers running external candidates from the
workspace arsenal, excludes `arcanum-internal`, resolves server/tool ambiguity interactively only
on a TTY, and sends the provenance-preserving `serverName` plus optional `workingDirectory` to this
endpoint. `arcanum tool list|show|invoke` uses the arsenal's live
`IBuiltInToolRegistry.GetToolNames()` projection and `/api/tools/invoke`. Both invocation families
accept one JSON object inline, through `@file`, or through redirected stdin. The CLI disables
System.CommandLine response-file substitution so `@file` remains an Arcanum convention, then caps
the UTF-8 input at **1 MiB**, caps JSON nesting at **64**, and validates an object before any invoke
request. Server-side diagnostic timeout, output truncation, `Mcp.DiagnosticBlocked`, workspace
trust, and external-only checks remain authoritative; the CLI cannot widen them.

**Key types:** `McpToolInvokeRequest` / `McpToolInvokeResponse` (`Api/Models/`), `DiagnosticMcpInvocationService` / `DiagnosticMcpInvocationOutcome` (`Api/Mcp/`), `DiagnosticMcpInvocationEndpoints` (`Api/Mcp/`), mirrored The Forge DTOs `McpToolInvokeRequest` / `McpToolInvokeResponse` (`TheForge.Core/Models/`) + `TheForgeJsonContext` registrations.

**Tests:** `tests/RetroDownfall.Arcanum.Tests/Mcp/DiagnosticMcpInvocationServiceTests.cs` covers every Forbidden Art block, empty tool name, stopped server, internal-server filter, untrusted-workspace hiding, ambiguous tool, tool-not-found (named and unnamed), happy path, truncation marker, tool error (`isError: true`), timeout, non-JSON output wrapping, **internal+external name collision (external invoked; not ambiguous)**, **internal-only → ToolNotFound**, and **explicit wrong server → no fallback** — all with a fake `IMcpConnectionManager` + fake `AIFunction`, no API host required. Source-generated JSON round-trips for `McpToolInvokeRequest` / `McpToolInvokeResponse` / `DiagnosticMcpFixtureStoreDocument` are in `tests/RetroDownfall.TheForge.Tests/TheForgeJsonContextTests.cs`.

---

## 12. C# language and coding conventions

- **File-scoped namespaces** used consistently.
- **Primary constructor-style DTOs** — positional records for `Error`, `ApiResponse<T>`, `PingRequest`, `IntelligenceEvent`. No `[JsonPropertyName]` on `/api` DTOs; casing comes from `[JsonSourceGenerationOptions]`. **Exceptions:** OpenAI `/v1` types and MCP JSON-RPC contexts use explicit `[JsonPropertyName]` where an external spec mandates snake_case or JSON-RPC member names (§8.2).
- **Primary constructors on services** for DI injection.
- **`IDisposable`** on infrastructure services with `SemaphoreSlim` or `ServiceProvider` ownership.
- **Blank line after each line of C# code** for visual breathing room.
- **Convention scope (project-specific vs inherited).** The conventions in this section plus the README naming metaphor are **specific to Arcanum**. Organization-wide standards scoped to `Corp.Solution.*`-prefixed solutions — Dapper repositories over SQL Server stored procedures, the `Corp.Lib.*` / `Corp.Api.Configuration.Lib` NuGet stack, and Refit "Service Library" API contracts — **do not apply** here: Arcanum is local-first over its own EF Core + SQLCipher Grimoire (no SQL Server, no stored procedures) and ships its CLI/host as Native AOT on Windows/Linux plus a self-contained macOS fallback. The always-on house rules still hold — one blank line after each C# statement (above), strict CSP with no inline JS/CSS on every web surface, and the four-document contract updated with code (§18).

---

## 13. Testing strategy

`tests/RetroDownfall.Arcanum.Tests` is the xUnit suite for the Core, Infrastructure, Api, and Cli
shipping assemblies. It runs on the normal CLR, not as Native AOT, and uses hand-written fakes only
(no Moq). A live model provider is unnecessary for ordinary test runs.

Any suite that redirects `ArcanumPaths` or touches configuration, session, MCP, or file roots must
set both host environments to `Testing`, point `ARCANUM_TEST_HOME` at a uniquely owned temporary root
**before the first path access**, and restore every variable during cleanup. Process-global
mutations use the nonparallel `ProcessEnvironment` collection; a suite that already requires another
`DisableParallelization` collection (xUnit assigns one collection per class) must provide the same
serialization and the same guarded test-home invariant. Tests never read, back up, rewrite, migrate,
or delete the developer's real `~/.config/arcanum` files.

### 13.1 Commands and coverage authority

From the repository root:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
./scripts/coverage.sh
./scripts/coverage.sh --threshold
```

Windows serial verification uses host PowerShell and the normal user NuGet cache:

```powershell
$env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --configuration Release -- xUnit.ParallelizeTestCollections=false
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj --configuration Release
```

Run `scripts/coverage.sh --threshold` from Git Bash for the normal parallel coverage gate. Threshold
evaluation prefers Python and falls back to Windows PowerShell on Windows; the xUnit run itself
remains parallel.

| Post-exclusion metric | Default/local target | Ubuntu CI target |
|-----------------------|----------------------|------------------|
| Line coverage | ≥ 80% | ≥ 80% |
| Branch coverage | ≥ 70% | ≥ 70% |
| Security-critical branch coverage | 100% | 100% |

The security-critical set is `ApiKeyEndpointFilter`, `ApiKeyDigestCache`,
`DataProtectionSecretStore`, `GrimoireKeyDerivation`, `McpSecurityLimits`,
`TrustedMcpWorkspaceStore`, `SandboxedFileIo`, `SecureFileReader`,
`IdentityOwnedFileSystemCleanup`, `SanctumGuard`, `OutboundUrlGuard`,
`HostProcessToolPolicy`, `IdempotencyClaimStore`, `BudgetReservationService`, and `WardGate`. The
Python and PowerShell gates hold the same set under a parity test, and both fail when a listed type
is absent from the Cobertura report, so a rename or new exclusion cannot silently count as 100%.
Ubuntu executes a different
set of OS-specific branches while the denominator still includes all shipping assemblies. Local and
CI aggregate floors are therefore both 80% line coverage and 70% branch coverage; environment
overrides remain available for platform-specific validation. These aggregate floors do not relax
the 100% security-critical gate.
Both environment values are validated as finite percentages from 0 through 100.

The coverage denominator includes Core, Infrastructure, and Api. Cli interactive behavior is
scenario-tested but excluded from coverlet's Include filters; Terminal.Gui Command Center UX is not
line-covered. The Forge and Compendium run as separate test projects. Configuration lives in
`tests/RetroDownfall.Arcanum.Tests/coverage.runsettings`, `scripts/coverage.sh`, and
`scripts/coverage_threshold.py`; HTML is written to `.tmp/coverage/report/index.html`.

CI's authoritative Arcanum gate is:

```yaml
env:
  COVERAGE_LINE_TARGET: "80"
  COVERAGE_BRANCH_TARGET: "70"
- run: |
    python3 -m unittest scripts/coverage_threshold_test.py
    ./scripts/coverage.sh --threshold
```

Do not publish a historical test count or coverage percentage as a release claim. GitHub Actions
uploads HTML + Cobertura as `arcanum-coverage-report`. Compendium runs as a separate `dotnet test`
step. The Forge remains excluded from CI build/test until its test project and Ux solution build are
re-enabled in `.github/workflows/ci.yml`; use the Windows command above meanwhile.

### 13.2 Test data, ownership, and parallel collections

- Checked-in immutable inputs live under `TestData/<Feature>/` and are copied with
  `CopyToOutputDirectory=PreserveNewest`. Copy them into a temp root before mutation.
- Mutable workspace trees, Grimoire copies, and CODEX writes use `Support/TempWorkspace` or fixture
  helpers. Cleanup owns one exact unique root and never infers an ancestor by walking parents.
- API-host tests set `ARCANUM_TEST_HOME` while the environment is `Testing`. This is required on
  Windows because changing `HOME`, `USERPROFILE`, or `APPDATA` after process start does not redirect
  .NET known-folder paths.
- Concurrency tests synchronize on observable state transitions, not narrow scheduler windows.
  `SseStreamWriterTests` holds one `MoveNextAsync` behind a signal until a heartbeat write is
  observed, then releases the enumerator; its long `WaitAsync` bounds are deadlock guards and never
  cancel the stream under ordinary coverage-run suspension.

xUnit runs collections in parallel and tests inside a collection serially:

| Collection | Purpose |
|------------|---------|
| default | Pure logic; no shared process state. |
| `Grimoire` | SQLCipher template database with per-test file copies. |
| `ApiHost` | Shared `ArcanumWebApplicationFactory`, isolated persistent root, PID file disabled; collection parallelization disabled. |
| `ProcessEnvironment` | Process environment/global `ArcanumPaths` mutation (including built-in Spell fixtures) and Grimoire tests that mutate it; parallelization disabled. |
| `OutboundUrlGuardDns` | Process-global DNS resolver seam; parallelization disabled. |
| `WorkspacePathPolicy` | Static path-comparison seams; parallelization disabled. |

### 13.3 SQLCipher and API-host fixtures

Grimoire tests use `[SkippableFact]` and skip when `e_sqlcipher` is unavailable
(`GrimoireFixture.SqlCipherAvailable`). The probe disables SQLite pooling and reports available only
after its temporary DB can be deleted. Cached-template validation/remediation and main DB/sidecar
copying share an in-process lifecycle lock plus a named cross-process mutex, so concurrent
test/coverage processes cannot observe or delete a partial template.

Shutdown-checkpoint tests create their WAL precondition explicitly: a pooling-disabled SQLCipher
connection disables automatic checkpoints, writes a probe row, and stays open but transaction-free
while `CheckpointOnShutdownAsync` runs. Tests must not assume schema initialization will leave a
nonempty WAL after its final connection closes; SQLite may checkpoint or remove that WAL.

`ArcanumWebApplicationFactory` references `Api.DevHost`, seeds an encrypted copy under a unique
testing root, disables the production PID file, replaces `ISecretStore` and
`IArcanumIntelligenceProvider`, and exposes `CreateAuthenticatedClient()`. It sets
`ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `ARCANUM_TEST_HOME`, and
`ARCANUM_SKIP_KEY_BOOTSTRAP=1` **before** top-level `Program` reaches `CreateSlimBuilder`.
`ArcanumPaths` honors the test-home override only in `Testing`; global MCP, Grimoire, secret, and log
paths all remain inside that root.

Each factory registers `ArcanumDbContext` with an explicit SQLCipher connection rooted at its own
`TempHome`; later process-environment changes cannot redirect scoped repositories into another
factory's DB. Every factory-creating class belongs to `ApiHost` (a reflection guard covers the
performance baseline). The factory retains the exact `IHost` returned by `CreateHost` and
idempotently awaits its stop before delegating framework disposal, so delayed hosted-service
shutdown still observes the factory's test-home paths. Only after host stop and Grimoire checkpoint
does disposal restore the process environment, clear pooled SQLite connections, and delete the
isolated root. Sync disposal routes through the same async lifecycle.

`AddArcanumSerilog` must not resolve options or the ring-buffer sink through
`GetRequiredService` inside the `AddSerilog` configure callback during host `Build()`; that re-enters
logging DI and deadlocks while `HostFactoryResolver` waits for `HostBuilt`. The sink is registered on
first emit. Testing resolves the log directory inside the isolated root before any directory/ACL
work and skips the rolling-file sink.

### 13.4 First-class reasoning coverage matrix

Reasoning tests are organized by the production boundary they protect, not duplicated merely to
increase counts:

- **Contracts/configuration:** `ReasoningContractsJsonTests`, `ModelEntryJsonConverterTests`,
  `PricingSettingsTests`, `ConfigurationValidatorTests`, `PingRequestBoundsValidatorTests`, model
  endpoint/source-context tests, and Compendium descriptor/parity/preservation tests cover the
  retained configuration graph, unknown/removed-path rejection, credential references, editable-key
  parity, strict enum wire names, pricing fallback, control-support × wire-dialect combinations,
  legacy bare model strings, capability metadata, and AOT JSON registration.
- **Provider/engine:** `ReasoningChatOptionsAdapterTests`, `ModelCallExecutorTests`,
  `ProviderAttemptCommitTrackerTests`, and reasoning cases in `WizardIntelligenceProvider*Tests`
  cover typed/default/no-op JSON, all closed dialects, ignored controls, buffered/streaming/
  interleaved/protected reasoning, fallback commitment, no-tools restart, same-provider
  continuation, guardrail buffering, context/reservation boundaries, and strict replacement order.
- **Projection/usage:** `ReasoningProjectionEndpointTests`,
  `TurnEngineProjectionCharacterizationTests`, `OpenAiChatUsageJsonTests`, and OpenAI endpoint tests
  cover native buffered/NDJSON and OpenAI buffered/SSE fields, shared reasoning/error rules, real
  HTTP semantic validation, answer isolation, legacy result data, total-token authority,
  `cached_tokens`, missing/inconsistent usage, and reasoning-token details.
  `OpenAiV1ParityTests` directly owns choice-only terminal chunks, `include_usage` false/true, the
  separate choices-empty usage chunk, and 40-character tool-argument fragmentation/reassembly;
  exact parity with the helper projection is not assumed. A real Master → TurnEngine → native
  projection → Apprentice test guards answer-only handoff.
- **Accounting/persistence:** `CostCalculatorTests`, `BudgetReservationEstimateTests`,
  `TurnAccountingHandleTests`, `InferenceAccountingStoreTests`,
  `GrimoireSqlSchemaMigratorTests`, metrics tests, and audit tests cover cached/reasoning subset
  pricing, nullable-vs-zero rates, reservation headroom, nested ambient restoration, reconciliation,
  raw-SQL token columns, fresh install/idempotent script reapply, the no-EF-entity guard, and
  count-only telemetry.
- **Clients:** CLI API/rendering/command tests, Command Center tests, The Forge NDJSON/Tome/trace
  tests, Compendium notifications, and `ApprenticeStreamFramePolicyTests` cover known/unknown/
  malformed frames, discriminator preflight, one-byte and multibyte fragmentation, bounded
  ephemeral rendering, no-op notification suppression, spinner/viewport/cancellation cleanup,
  trace redaction, and no reasoning handoff. First-frame UI transitions observe emitted updates
  instead of racing wall-clock delays.
- **Concurrency:** daemon overlap tests start one signal-gated execution, await its actual start,
  then invoke the competitor directly; scheduler delay is never mistaken for production locking.

Focused examples:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --configuration Release --filter "FullyQualifiedName~ReasoningContractsJsonTests|FullyQualifiedName~ReasoningChatOptionsAdapterTests|FullyQualifiedName~ReasoningProjectionEndpointTests"
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj --configuration Release --filter "FullyQualifiedName~ArcanumApiClientNdjsonTests|FullyQualifiedName~TomeViewModelTests|FullyQualifiedName~InferenceTraceViewModelTests"
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --configuration Release --filter "FullyQualifiedName~SettingDescriptorParityTests|FullyQualifiedName~GenericSettingsPreservationTests"
```

### 13.5 Database, CLI, process, and workspace safety

SQLCipher/schema/accounting tests use `GrimoireFixture` scratch/template databases only. Never point
tests at `~/.config/arcanum/arcanum.db`. Scratch contexts disable pooling and keep one encrypted
connection open until disposal releases the Windows file handle. Tests must not migrate, reset, or
inspect a real Grimoire. A developer DB created before the current reasoning-accounting install
script must be handled outside tests: stop Arcanum, back up if necessary, delete the DB plus
`-wal`/`-shm`, then restart to reinstall (§5.4.5).

`Cli/Infrastructure/CliApplicationFactory` builds a `CommandApp` from a test `ServiceCollection`;
use `Spectre.Console.Testing.TestConsole` for output assertions. Test C# follows the repository's
blank-line convention.

Windows process-boundary behavior lives in `ProcessResourceLimiterWindowsBehaviorTests`,
`WindowsJobObjectSessionTests`, and `ChildProcessBoundaryBehaviorTests`: Job Object failures use
hand-written API fakes; stream failures use custom `Encoding`/`Decoder` implementations; cleanup
uses unique owned roots; process-tree tests use immediate-exit or bounded 30-second children with
prompt termination assertions and unconditional cleanup. `SpellVersionPathPolicyTests` covers
labels/sidecars without filesystem I/O. `WorkspacePathPolicyTests` and symlink variants separately
cover lexical, symlink-component, and handle-revalidation boundaries independently of Sanctum.

### 13.6 Reliable editing-loop contract matrix

Focused normal-CLR run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~WorkspaceSearchToolTests|FullyQualifiedName~ApplyPatchToolTests|FullyQualifiedName~MultiFileCommitCoordinatorTests|FullyQualifiedName~MandatoryGrimoireRepositoryTests|FullyQualifiedName~WorkspaceCheckToolTests|FullyQualifiedName~WorkspacePathPolicy|FullyQualifiedName~ToolRiskClassifierTests|FullyQualifiedName~ToolAttunementTests|FullyQualifiedName~ArcanumInternalToolServerTests|FullyQualifiedName~McpToolMergerTests|FullyQualifiedName~DiagnosticMcpInvocationServiceTests|FullyQualifiedName~SessionEventHubTests|FullyQualifiedName~InferenceAuditLoggerTests|FullyQualifiedName~WizardIntelligenceProviderTests|FullyQualifiedName~ArcanumMetricsTests|FullyQualifiedName~PrometheusMetricsExporterTests|FullyQualifiedName~MetricsEndpointTests|FullyQualifiedName~AuditEndpointTests"
```

- `WorkspaceSearchToolTests`: strict line scope/mixed newlines/no cross-line regex, ordinal literal
  case, AOT-safe non-backtracking → interpreted fallback without `Compiled`, deterministic order,
  strict UTF-8/binary/symlink policy, every cap/status, and cancellation checkpoints. No Weave.
- `ApplyPatchToolTests`: parser vs planner, all-files-before-mutation, strict text/newline/mode,
  bounded unique relocation, alias/cycle/topology/metadata rejection, dry-run, result ordering/caps,
  bound receipts, all four persistence outcomes, independent calls, cancellation classification,
  rollback, relative recovery, and the non-isolated/crash boundary.
- `MultiFileCommitCoordinatorTests`: all-stage-before-mutation, sequential visibility,
  destination/artifact revalidation, concurrent edits, cancellation, reverse rollback, incomplete
  cleanup, identity-safe retention, and deepest-first directory cleanup. Passing proves reversible
  rollback, not multi-file isolation or crash atomicity.
- `MandatoryGrimoireRepositoryTests`: deterministic receipt/Entry IDs, exact payload readback,
  assistant-call before system-result, recovered idempotence, failed/partial/mismatched/ambiguous
  classification, bounded cancellation, and no generic duplicate append.
- `WorkspaceCheckToolTests`: closed/custom profiles, `--no-restore`, reserved tokens, sanitized env,
  trusted executable/SDK/global.json/package identities, read-only roots, owner-only outputs,
  seeding caps (128 projects / 640 files / 64 MiB per run; 64 inputs / 8 MiB per project), deadline
  admission/cleanup, diagnostic caps, timeout/cancel cleanup, macOS advertisement, Linux/Windows
  unavailability, open-network disclosure, and the unproved malicious detached-descendant boundary.
  Real process cases are macOS-capability-gated.
- Tool-risk/attunement/internal-server/merger/diagnostic tests cover catalog presence, option refresh,
  intrinsic-name collisions, eligibility-based `workspace_check` advertisement, intrinsic Wards,
  and diagnostic blocking of `apply_patch` / `workspace_check` even on colliding external names.
- `SessionEventHubTests` proves bounded process-local fan-out and slow-reader drops; persisted Entries
  remain replay authority.
- Audit/metrics tests prove successful-turn-only rows, default argument redaction/no result bodies,
  closed tool outcome values, and `arcanum_apply_patch_artifact_cleanup_total` outcomes. They do not
  claim globally bounded `tool_name` cardinality; unknown direct invocation names remain distinct
  within per-value/request limits.

All filesystem/process tests use unique owned temporary roots; receipt tests use scratch Grimoire
fixtures. The reliable editing loop changes no schema and requires no real-database migration or
reinstall.

### 13.7 Regression coverage catalog

| Test area | Contract locked down |
|-----------|----------------------|
| `BudgetMonitorTests` | `IOptionsMonitor` + scope-factory singleton shape; record-before-dispatch ordering; duplicate suppression when `RecordAlertAsync` returns false. |
| `GuardrailsPipelineTests` / `GuardrailAuditLoggerTests` | Awaited (not fire-and-forget) audit writes, multiple violations, balanced-parentheses phone regex, bounded topic-regex cache. |
| `JsonSchemaHelperTests` / `StructuredOutputValidatorTests` | Nullable type arrays; enum short-circuit only for string/absent type; decimal numeric-enum equality. |
| `ArcanumErrorMapperTests` | Prompt invalid request, Session invalid status, query/embedding/structured-output codes, and preservation of all explicit 500 mappings. |
| `GrimoireRepositoryTests` | Sargable half-open UTC spend range with decimal sum in C#; hard-delete unsummarized-count decrement; negative token/cost clamp. |
| `EmbeddingsResetScopeTests` / `EmbeddingsResetServiceTests` | Typo scopes cannot silently escalate to `all`; `confirm=true` is mandatory. |
| `ArcanumBrowseWebToolTests` | `maxLinks` clamp, SSRF surfacing, response charset, timeout via `OperationCanceledException` + inner `TimeoutException`, and nav/header/footer filtering. |
| `RequestAugmentingHandlerTests` | Replaced `HttpContent` disposal, content-header restoration on retry, and non-object JSON guard. |
| `ClientToolForwardingTests` | Duplicate names, named-choice membership, auto/none with forwarding disabled, and per-tool `strict` preservation. |
| `OpenAiV1EndpointTests` / `OpenAiV1BatchesEndpointTests` | Structured-output maps to `validation_failed`/`invalid_schema`, not generic inference failure; batch reset removes orphan output/error files. |
| `EncryptedBlobStoreTests` / `FileEncryptionKeyProviderTests` / attachment-file-batch tests | Empty/boundary/large streaming round trips; random nonces; purpose/key separation; bit flips, truncation, trailing data, cancellation cleanup, and concurrent readers; OS/DP key persistence and missing/corrupt fail-closed behavior; no plaintext attachment/upload/batch artifact at rest. |
| `EncryptedBlobCompatibilityTests` / `BlobEncryptionFileProcessorTests` / `BlobEncryptionOperationPolicyTests` | Metadata-led legacy reads; no encrypted-to-plaintext downgrade; crash retry after atomic replace but before metadata commit; reconciliation classifications; retained-key rotation; durable migration/rotation policy registration. |
| `SessionEndpointTests` | `since` 404 emits no leaked SSE headers; stable `Session.EntryNotFound` / `Session.InvalidStatus` constants. |
| `CostCalculatorTests` | Cached tokens clamp to the prompt subset and use `CachedPer1M` (zero or nonzero); potential/actual savings use the nonnegative input-minus-cached rate delta. |
| `PromptCachingChatOptionsAdapterTests` / `PromptCachePlannerTests` | Golden buffered/streaming root fields (`prompt_cache_key`, exact `in_memory`/`24h` retention), reasoning composition, unchanged ineligible bodies, contiguous-prefix planning, deterministic tool digests, stable keys, and plaintext exclusion. |
| `LexiconServiceTests` | Case-insensitive create/upsert, nonduplicate fact append, `General`/keep/refresh type rules, per-upsert cap, delete/FTS removal, exact-before-FTS ranking with `bm25(...,3.0,2.0,1.0)`, fact-text hits, special-char sanitization, index refresh, and missing-name null. |
| Lexicon internal-tool tests | Enabled tools-list advertisement, disabled omission of Lexicon and legacy Lore tools, service-backed create/delete, and disabled tool error. |
| `SemanticRouterTests` / `LexiconEntityExtractor` cases | Spell+entities result, entities surviving `NONE`, missing→empty, fenced/malformed JSON, no-call empty prompt, and cap/deduplication. |
| `CliContractTests` / `DoctorCommandJsonTests` | Recursive global flag placement; stdout/stderr separation; ANSI stripping; one-document JSON wrapping; fail-closed redirected confirmation and `--yes`; closed exit codes; redacted network/unhandled failures; typed doctor JSON. |
| `SystemPromptBuilderTests` / untrusted-fence tests | Lexicon DATA inclusion/omission, control/newline hardening, byte truncation, adaptive fences for Codex/Spell/instructions/Chronosync/summary/attachments, and sanitized Data Stream ids. |
| `UnseenServantDaemonJobTests` | Deterministic bounded daemon-state name, enabled Lexicon state/instruction, disabled omission, and missing-state fail-open kickoff. |

### 13.8 Exclusions, runtime budget, and CI

Types excluded through `[ExcludeFromCodeCoverage]` carry an inline reason. Source-generated JSON
contexts are excluded by `coverage.runsettings`; generated files, migrations, and framework
artifacts are likewise excluded there. Full-suite duration is host-dependent; the expensive fixed
costs are Grimoire template creation and ApiHost startup. Use the serial Windows command when
validating shared process state.

`.github/workflows/ci.yml` runs on pull requests, pushes to `main`, and manual dispatch:

1. `build-test` restores/builds Arcanum + Compendium, tests Compendium, then runs Arcanum once through
   the coverage threshold script and uploads the report. The Forge is temporarily excluded.
2. `aot-il` runs `./scripts/verify-aot-il-warnings.sh` for the hosted Linux RID.

Packaging workflows are separate manual workflows: Windows Native AOT Arcanum + Compendium,
Windows/Linux private beta including The Forge, and signed/notarized macOS arm64. SQLCipher tests
retain their normal skip behavior when the native asset is absent.

---

## 14. Extension guidelines for future contributors

1. **New HTTP routes:** Add in `MapArcanumEndpoints`. Return `ApiResponse<T>` via `FromResult`. Extend `ArcanumJsonContext` for new payload types. Use `.WithName(...)` for OpenAPI.
2. **New domain operations:** Return `Result` / `Result<T>`; rely on implicit conversions.
3. **New CLI verbs:** Add the handler under `Cli/Commands` and wire it in `CliCommandTree`; register constructor dependencies in `ConfigureCliServices`. Route requested payloads through `IConsoleDispatcher`, diagnostics through its stderr path, confirmations through `IConfirmationPrompt`, and structured values through an explicit source-generated `JsonTypeInfo`. Return only `CliExitCode` values. Lightweight verbs should use `AddArcanumEyeOfTheWorld()` rather than `AddArcanumInfrastructure`.
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

## 16. Known limitations and operator constraints

### 16.1 Inference

- **Single user prompt per HTTP request.** Multi-turn is via `sessionId` + Grimoire history reload.
- **Single-model routing only.** No cross-model routing or load balancing.
- **Provider-level fallback is automatic** — the hub retries the next healthy provider advertising the same model after a pre-commit connectivity failure.
- **Models without tool support** are retried once without tools after detecting rejection.
- **OpenAI compatibility is intentionally partial.** Chat Completions, models, embeddings, files,
  and batches are implemented; moderation, image-generation/editing, and audio routes return
  `501 not_supported`. Batch processing supports `/v1/chat/completions` only and forces all tools
  off for every line.
- **Ollama context window size:** When using Ollama via its OpenAI-compatible `/v1` endpoint, Arcanum can no longer inject `num_ctx` to control the context window size (the OpenAI Chat Completions API has no such parameter). Operators must configure Ollama's context size on the Ollama side (e.g. the `OLLAMA_NUM_CTX` environment variable). `ContextWindowLimit` in provider config still feeds Arcanum's read-time compression threshold and the CLI mana bar — set it to match Ollama's effective context size for accurate compression.
- **Tokenizer coverage:** exact local text counting ships for built-in verified `o200k_base` profiles. Other provider/model combinations use a visibly estimated, safety-margined fallback; operator tokenization profiles are not part of the public configuration contract. Iterative history compression beyond one summary swap plus complete-tool-exchange trimming is not implemented.
- **Tool-child confinement is platform-dependent.** macOS uses deprecated
  `/usr/bin/sandbox-exec` Seatbelt for a filesystem-only jail. Linux Landlock support is inactive and
  command tools fail closed unless the unsandboxed escape hatch is acknowledged. Windows uses a
  per-invocation AppContainer filesystem jail and a Job Object process-tree/resource boundary.
  `workspace_check` is unavailable on Linux and Windows. No platform provides child-process network
  isolation. Inspect `arcanum doctor` and the `ToolChildSandbox` / `WorkspaceCheck` health
  components before approving execution.
- **The Weave's vector search has a managed fallback.** No sqlite-vec native asset ships by default.
  When `vec0` is unavailable, `DivinationService` uses SIMD-accelerated managed cosine search over
  BLOB rows with a 50,000-row scan budget. Results can be incomplete after that budget; health,
  `/api/meta`, and `arcanum doctor` report the active vector mode and budget.

### 16.2 Persistence

- **Schema ownership:** EF design-time migrations live under `Data/Migrations/`; the AOT host applies
  companion embedded SQL from `Data/SqlMigrations/` through `GrimoireSqlSchemaMigrator` and
  `__EFMigrationsHistory`, never `Database.MigrateAsync`. Incompatible local schemas are recreated
  under §5.4.5 rather than data-migrated.
- **Installation atomicity:** `GrimoireSqlSchemaMigrator` wraps each embedded script and its matching
  history-row insert in one `SqliteTransaction`. Scripts contain no `BEGIN`, `COMMIT`, or history
  insert. FTS backfills are idempotent, baseline `CREATE TABLE` / `CREATE INDEX` statements are
  guarded, and a failure rolls back both schema work and the history row so startup can retry.
- **SQLite pragmas** (applied on every connection via **`SqliteConnectionPragmas`**): `journal_mode=WAL`, `busy_timeout=5000`, `foreign_keys=ON`, `synchronous=NORMAL`. WAL provides automatic crash recovery; write contention is retried via **`SqliteBusyRetry`** (bounded backoff on SQLITE_BUSY/locked). Its total-delay guard accumulates scheduled backoff durations instead of wall-clock elapsed time, so coverage profilers and runtime suspension cannot suppress a valid retry.
- **`Arcanum:Features:Conclave`** gates **The Conclave** cross-Apprentice delegation (the **`cast_sending`** tool and **`POST /api/apprentices/{id}/cast`**). Apprentice lineage (**`ParentApprenticeId`**) is persisted inside the existing **`CheckpointData`** JSON column — deliberately **no** EF migration or compiled-model regeneration, and no top-level SQL index.
- **`cli-context.json`** is the owner-only, versioned local CLI preference document for active
  Campaign, Workspace, Model, and Session. It stores no secrets and has no server authority.
  `cli-session.txt` remains a temporary compatibility mirror of the last Session id; neither file
  is multi-user or cloud-synchronized.
- **`UnseenServantWatermarks`** (§5.5.5) is deliberately **not** part of the compiled EF model — it is accessed entirely via raw SQL through the scoped **`ArcanumDbContext`**'s connection (`GetDbConnection()`), following the FTS query pattern (**`ResolveFtsSessionIdsAsync`**/**`SearchArchivesAsync`**), so adding it required no `dotnet ef dbcontext optimize` regeneration.
- **Schema-install safety and configuration impact:** `UnseenServantWatermarks` and `SanctumBreaches` are folded into the `InitialCreate.sql` baseline (no production databases in the wild); neither adds a public configuration element. Installation/reinstall policy is §5.4.5.
- **`SanctumBreaches`** (§11.15): raw SQL via `SanctumBreachRepository` (not in the compiled EF model); FK to `Campaigns` (`ON DELETE CASCADE`); retention enforced on every insert (`SanctumConfig.MaxBreachCount`, clamp 100 – 100,000).
- **`LongRunningOperations`** (§10.8): raw SQL via `LongRunningOperationStore`, encrypted by the
  same SQLCipher Grimoire, with self-referencing root/parent foreign keys and indexes on
  state/lease, kind/state, parent, session, run, and reservation. Its checkpoint blob/reference is
  recovery-private; the wire DTO intentionally omits both. This is the durable framework for
  operation lifecycle, not a promise that live streams, Wards, process handles, or in-memory Tasks
  can resume.
- **External encrypted blobs:** `attachments/` and `files/` are not standalone backups. Preserve
  them with `arcanum.db` plus WAL/SHM/KDF sidecars and the file-encryption key from the same backup
  generation. The primary key is an OS credential; the portable recovery set is
  `file-encryption-key.dat` plus the matching Data Protection `keys/` directory. A restored
  database without blobs loses attachment/file content; restored ciphertext without the matching
  key is intentionally unrecoverable; restoring only blobs without matching database metadata
  leaves unreferenced ciphertext. During migration or rotation the portable
  `file-encryption-key.dat` value is a wrapped multi-key ring containing the active write key and
  every retained read key; copying only one extracted key is not a valid backup. Restore installs
  that mirror plus its matching Data Protection ring before startup, and the provider accepts
  archives with multiple active key ids.

### 16.3 Security and identity

- No user identity, sessions, or OAuth. Loopback + API key only.
- **Grimoire KDF:** New databases derive the SQLCipher passphrase via `GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret` using **PBKDF2-HMAC-SHA256** with **600,000 iterations** and a unique 16-byte salt stored in `{grimoire.db}.kdf`. Legacy databases (created before this change) are opened with the prior HKDF path and transparently re-encrypted to PBKDF2 on unlock. The dedicated encryption secret is stored alongside the master API key; rotating the API key alone does not break the Grimoire.
- **API key rotation:** For **legacy** databases that were still encrypted with the master API key, rotating the key was destructive. For **new** databases, the Grimoire is independent of the API key, so rotating the key only invalidates API authentication. To rotate the key on a new database, run `arcanum key set` (or replace the OS credential + `security.dat` mirror) and restart; the Grimoire `.db` and `.kdf` files can stay in place. If the Grimoire encryption secret itself is lost, the database is unrecoverable — there is no automatic key recovery or backdoor. When `grimoire-key.dat` exists but Data Protection cannot decrypt it (missing `key-*.xml` under `~/.config/arcanum/keys/`), bootstrap **FailFast**s with an explicit recovery message and does **not** fall back to the API key (that path previously produced a misleading “key verification failed”). Recovery is restore the matching DP key from backup, or delete `arcanum.db` + `arcanum.db.kdf` + `grimoire-key.dat` and start fresh.
- **`arcanum key show`** / **`arcanum key set`** read/write the master key via CLI DI (`ISecretStore` → OS keychain with `security.dat` fallback); no HTTP endpoint. Shared identity: `arcanum` / `master-api-key`. Linux requires `libsecret` and a running Secret Service for the primary path.
- **Attachment/upload/batch key:** a separate random 256-bit master key lives primarily in OS key
  storage at `arcanum` / `file-encryption-master-key`; it is never derived from, displayed by, or
  rotated with the API key. A DP-sealed best-effort mirror lives at
  `file-encryption-key.dat`. Purpose-specific HKDF keys prevent an attachment envelope from being
  accepted as an upload or batch artifact. First install creates this key only after a successful OS
  store write. If encrypted blobs exist and the credential/mirror is missing, corrupt, or has the
  wrong key id, startup and reads fail closed and never generate a replacement. Restore the OS
  credential, or restore both `file-encryption-key.dat` and its matching `keys/key-*.xml` ring.
  Rotation changes this value to a versioned multi-key ring: one active write key plus old read keys
  that remain until no metadata row references them and a complete verification passes. Deleting
  the ciphertext is the only start-fresh option and permanently loses those blob bytes.
- **Diagnostics:** `/api/health` component `FileEncryption` and `arcanum doctor` report key
  availability and bounded counts of valid encrypted, legacy plaintext, and corrupt blobs. They
  never expose key material, authenticated metadata, plaintext hashes, filenames, or content.
  `arcanum data encryption status` adds complete Grimoire-backed counts; `verify` reports only
  bounded issue categories and aggregate file/byte progress.

### 16.4 Testing

- `tests/RetroDownfall.Arcanum.Tests` covers API, CLI, Infrastructure, Configuration, Intelligence,
  MCP, Weave/RAG, and Security; `tests/RetroDownfall.Compendium.Tests` (assembly
  `RetroDownfall.Compendium.Ux.Tests`) covers Compendium settings/converters; and
  `tests/RetroDownfall.TheForge.Tests` covers the desktop client. Integration and coverage behavior
  is defined in §13.

### 16.5 CLI

- **Line-counter for swap is naive.** Multi-cell glyphs and ANSI escapes are not measured; the swap may erase extra rows or leave stray lines. The renderer never throws.
- **Status/tool diagnostics share the TTY.** Intermixed stderr/stdout lines can desynchronize the cursor count during tool-heavy turns.
- **Direct MCP/tool administration is API-only.** `mcp` and `tool` commands share the bounded resource selector and tool-argument reader; direct commands never read MCP configuration or invoke an MCP transport locally.

### 16.6 CLI UX surface (Spectre.Console + Command Center)

- **Command Center** (bare interactive `arcanum`): Terminal.Gui fixed viewport; hard-modal arbitration (Wards > HumanPrompt); attachment `[Snapshot]`/`[Live]`/`[Stale]` badges, loaded/disk version metadata, watcher-driven backend revalidation, and `/attachments refresh <name>` when enabled (§10.2.5); `ARCANUM_NO_COMMAND_CENTER=1` escapes to usage.
- **Frameless `ask`/`chat`:** Spectre banner, effective-context header, mana bar, `@file`/`@image` staging (chat ephemeral; CC host-persists), TTY/`NO_COLOR` theme gating, atomic owner-only `cli-context.json` plus the temporary `cli-session.txt` mirror.
- **doctor:** themed panels + optional `--json` `DoctorReport`.

### 16.7 Reliability & Performance Hardening

`EmbeddingBlobCodec` uses SIMD cosine; `/v1` SSE classifies disconnects; RAG chunking/truncation is
surrogate-safe; SQLCipher contention uses `SqliteBusyRetry`; and
`scripts/verify-aot-il-warnings.sh` gates first-party AOT purity.

### 16.8 Platform distribution

- Windows and Linux archives are unsigned by default. Windows SmartScreen may warn; Authenticode is
  available only when the Windows packager runs with `-Sign` and `WINDOWS_CERT_PATH` /
  `WINDOWS_CERT_PASSWORD`.
- The Windows/Linux `arcanum` executable is Native AOT. The current macOS arm64 CLI release is a
  folder-based self-contained publish because the supported macOS linker/toolchain cannot reliably
  link this Native AOT closure. Compendium and The Forge are self-contained, multi-file .NET 10
  Avalonia applications on every platform and are not Native AOT.
- Linux shared key discovery requires `libsecret` plus a running Secret Service. Without it, The
  Forge prompts for the API key or accepts process-only `THEFORGE_ARCANUM_KEY`; that value is never
  written to `the-forge.json`.

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
| **Saga** | Auto-extracted associative memory (§21.9); operator-delete only. |
| **Arcane Resonance** | Spell `dependencies` injection (§10.2.2). |
| **Spell Routing** | Pre-flight spell selection (`FullGrimoire` / `DirectResonance` / `FilteredDivination`). |
| **`vec0`** | Optional sqlite-vec KNN index; managed cosine fallback when unavailable (§21.2). |
| **Output Formatting Directive** | Terminal-safe Markdown subset for CLI (§10.5). |

## 18. Document maintenance

The repository maintains exactly the five-document contract. Any contradiction across them is resolved in
`Arcanum.DESIGN.md` (see §18). Update the owning canonical file in every change set.

- `Arcanum.DESIGN.md` for architecture, contracts, APIs, persistence, runtime behavior, testing, and
  packaging;
- `Compendium.README.md` for the complete public configuration surface and editor behavior;
- `Arcanum.README.md` for concise agent/operator orientation and runnable commands; and
- `Arcanum.Design.Human.md` for human navigation without duplicating technical or configuration
  contracts.

Architecture, contract, configuration, persistence, MCP, CLI, desktop, or distribution changes are
incomplete until the owning canonical documents are updated in the same change set.

---

## 19. The Forge — server registry and desktop Inference IDE

The name intentionally covers two related but distinct surfaces:

1. the server-side Campaign/Spell/Prompt registry; and
2. the cross-platform Avalonia **Inference IDE** that consumes Arcanum exclusively over HTTP.

The desktop app does not run inference, open the Grimoire, or duplicate server business logic.

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
- Prompt `/test` = assemble only (`SkipSpellRouting`) and return the model-aware `ContextTokenBreakdown` for the assembled system/user messages, current MCP tool schemas, provider framing, safety margin, and reserved answer; `/execute(-stream)` = live inference.
- Schema is installed by the embedded SQL-script/bootstrap path on host start; there is no supported
  user-data migration program (§5.4.5).

### 19.4 Disk layout

`{campaign}/.arcanum/` on register; prompts export under `.arcanum/prompts/`; workspace spells at `{workspace}/spells/{name}/SPELL.md` + optional `SPELL.json`.

### 19.5 Error codes

Forge codes on `ErrorCodes` + `ArcanumErrorMapper` (§8.23). A few endpoint-local literals remain at call sites.

### 19.6 Apprentice orchestration

Persistent agents + Chronicle SSE — behaviour in §5.7. Table `Apprentices`; Chronicle in-memory only. Conclave / Simulacrum / Second Wind / Shifting Fate / Divine Intervention: §5.7.

### 19.7 Desktop project model and architecture

The desktop Inference IDE is part of `RetroDownfall.Arcanum.slnx`:

| Project | Responsibility | Dependency |
|---------|----------------|------------|
| `RetroDownfall.TheForge.Core` | Forge-local models, mirrored API DTOs, settings, source-generated JSON contexts, API-key resolution; no Avalonia | `RetroDownfall.Arcanum.Core` |
| `RetroDownfall.TheForge.Ux` | Avalonia desktop application | `RetroDownfall.TheForge.Core` |
| `RetroDownfall.TheForge.Tests` | xUnit tests | `RetroDownfall.TheForge.Ux` |

All inherit `0.1.0-beta` from `Directory.Build.props`. Core references only the portable Arcanum Core
leaf. DTOs that live in the ASP.NET-heavy Api assembly are re-declared locally rather than adding an
Api project reference.

The Ux follows MVVM with `CommunityToolkit.Mvvm`; every ViewModel derives from `ViewModelBase`.
`ServiceCollectionConfigurator` composes `Microsoft.Extensions.DependencyInjection` without a
service locator. Only the named-HTTP `ArcanumApiClient` calls `HttpClient` directly; per-route
services wrap it. `ArcanumSseClient` handles server-sent events. `ArcanumConnectionService` polls
`GET /api/health` every five seconds while `AutoConnect` is true and exposes connection state to The
Anvil.

The client supports both stream families:

- NDJSON: `POST /api/intelligence/ping-stream`,
  `POST /api/spells/{name}/execute-stream`, and
  `POST /api/prompts/{id}/execute-stream`.
- SSE: Session live streams, Apprentice Chronicles, and `/api/events/logs|mcp|daemon`. The parser
  recognizes `data: ...\n\n`, terminal `[DONE]`, and ignores `: keep-alive`.

The desktop bundles as self-contained Avalonia on .NET 10 and is **not Native AOT**. This does not
relax the Arcanum host's source-generated wire contracts or Native AOT requirements (§9).

### 19.8 Desktop wire contracts

- Native `/api` JSON uses `ApiResponse<T>`:
  `{data?, isSuccess, error?, traceId?}` in camelCase. Failure/default data is omitted.
  `Error` is `{code,message,details?}`.
- Forge-local mirrors for Api-owned types include `HealthReportDto`, `HealthComponentDto`,
  integer-valued `HealthStatus`, `InstanceMetadataDto`, `GrimoireStatsDto`, `BudgetSummaryDto`,
  `OptionalWorkspaceRequest`, and diagnostic `ToolInvokeRequest`/`ToolInvokeResponse` whose
  arguments/result are `JsonElement`. There is no blanket `JsonStringEnumConverter`;
  per-type converters preserve the server wire, including integer `HealthStatus`.
- `POST /api/providers/test` accepts `AiProviderKind.OpenAICompatible` only.
  `POST /api/intelligence/arsenal` accepts an optional `OptionalWorkspaceRequest` and returns
  `WorkspaceArsenalDto`.
- `WardDto.WardId` is a string and expiry is `ExpiresAt`. Allow/deny uses one
  `POST /api/wards/{id}` with `ResolveWardRequest(bool Allow, string? Reason)`.
- `IntelligenceEvent` token text is `data`, not `message`. Terminal `result.message` is the complete
  answer; legacy `result.data` is the decimal total-token string. Native usage may also include
  cached/reasoning counts. The string discriminator is camelCase:
  `token`, `reasoning`, `toolCall`, `toolResult`, `toolError`, `warded`, `wardResolved`, `status`,
  `sessionBound`, deprecated `conversationBound`, `context`, `result`, or `error`.
  Reasoning is typed `{text,output}` and never carries provider-protected data.
- The NDJSON reader preflights the discriminator before strict source-generated deserialization.
  Unknown nonblank future values are skipped; malformed JSON and missing/non-string/blank/padded
  types are logged and skipped. A bounded line reader over `StreamReader` reassembles multibyte
  UTF-8 split across transport reads, caps each protocol line at 1 MiB, discards an over-cap frame,
  and resumes at the next line. The SSE parser applies the same line cap plus an 8 MiB aggregate
  event cap.
- The Tome renders reasoning in a separate live role and never appends it to the answer. Mutable
  live buffers publish coalesced snapshots with a final flush and explicit truncation: 64 KiB for
  reasoning and 200,000 characters for other live messages.
- `ChatCompletionUsage` retains OpenAI snake_case members inside the otherwise camelCase native
  envelope.
- `ApprenticeDetailDto.Status` is a PascalCase string (`"Running"`, etc.); Plan/step statuses are
  free-form lowercase strings compared case-insensitively. Lineage is a client-side walk of
  `ParentApprenticeId`.
- Chronicle frames are flattened: pass-through Master fields are top-level, not nested under the
  legacy `wizardEvent`. Forge deserializes a local `ChronicleFrame` with raw `Type`.
  `CastSent`, `SimulacrumStarted`, and `SimulacrumCompleted` are PascalCase on the wire; other event
  types are camelCase.
- OpenAI `/v1/files` and `/v1/batches` use `OpenAiCompatApiClient` and bare OpenAI DTO/error
  envelopes, never `ApiResponse<T>`.

Transport failures do not escape ViewModels as exceptions: route services synthesize failed
`ApiResponse<T>` values with `Connection.Failed` or `Connection.Timeout`. Buffered `/api` and
OpenAI-shaped JSON/error bodies are read with `ResponseHeadersRead` and a 64 MiB hard ceiling;
over-cap bodies become `Api.ResponseTooLarge`; status-only deletes complete after headers and do
not buffer an ignored body. JSONL previews stop at their byte ceiling even when the input contains
no newline. File downloads stream to a same-directory staging file, flush, and atomically replace
the destination only after a complete transfer; cancellation or I/O failure removes the staging
file and preserves any existing destination. The Hearth's local terminal reader caps each
stdout/stderr line at 64 Ki characters, retains a bounded prefix with an explicit truncation marker,
and continues with later lines.

### 19.9 Connection, authentication, and desktop settings

`TheForgeSettings.BaseUrl` defaults to `http://localhost:5001`. When Arcanum uses ListenAny /
`ARCANUM_HOST_ANY`, the host is HTTPS-only; use `https://localhost:{HttpsPort}` or the remote HTTPS
authority. The Forge never disables certificate validation. It sends `X-Arcanum-Key` on every
`/api` request (the server also supports Bearer for other clients).

Settings live at `~/.config/arcanum/the-forge.json` with `reloadOnChange:true`; an old `forge.json`
is renamed on first launch when the canonical file is absent. The persisted desktop state includes
base URL, theme, last Campaign, dock layout, auto-connect, and active Session. Fresh installs use the
light theme; existing dark selections remain dark. Dock layout resets through
**View → Reset Window Layout**.

| Desktop setting | Meaning |
|-----------------|---------|
| `baseUrl` | Arcanum authority; default `http://localhost:5001`. |
| `apiKey` | Obsolete migrate-and-strip field; keep null in new files. |
| `theme` | `light` by default; existing `dark` remains honored and changes live through **View → Theme**. |
| `lastCampaignId` | Active Campaign for menus and The Anvil. |
| `layoutState` | Persisted dock layout; null selects the default shell. |
| `autoConnect` | Enables the health polling/automatic connection flow. |
| `activeSessionId` | Last active Tome Session. |

The Master API key is never retained as active plaintext in that file. Resolution order is:

1. shared OS credential store (`service=arcanum`, `account=master-api-key`);
2. migrate-and-strip a legacy `apiKey` from `the-forge.json`;
3. trimmed `THEFORGE_ARCANUM_KEY` for process-only private-beta/automation use (never logged or
   persisted);
4. `arcanum key show` from stderr, persisted to the OS store when possible; then
5. an operator paste dialog, persisted to the OS store when available or held process-only with a
   warning.

Declining paste suppresses repeated prompts until **The Anvil → Enter API key…**, which also clears a
cached bad environment key. Rotate with `arcanum key set` (or the shared OS credential) and restart
The Forge. Linux requires libsecret and a running Secret Service for shared keychain access; The
Forge cannot decrypt Arcanum's `security.dat` fallback.

Operators connect or disconnect through **View → Connect to Arcanum** or The Anvil connection chip.
`arcanum doctor` reports whether the shared Master-key identity is present without exposing it.

### 19.10 Desktop vocabulary and implemented surfaces

| Surface | UI name | Responsibility |
|---------|---------|----------------|
| Center editor tabs | The Workbench | Spell editor, Tome, Scriptorium, Codex, Proving Grounds, comparisons, markdown |
| Campaign/workspace tree | The Atelier | Campaigns, workspaces, Spells, Prompts, Sessions, import/export |
| Session viewer | The Tome | Answer-only transcript plus separate ephemeral live reasoning |
| Apprentice orchestration | The War Table | Apprentice state, lineage, flattened Chronicle |
| Ward approvals | The Gatehouse | Approve/deny active Wards |
| MCP/tools/models | The Arsenal | MCP lifecycle, Scrying Pool diagnostics, read-only providers/models |
| Budget | The Treasury | Read-only budget/spend snapshot |
| Status bar | The Anvil | Connection, Campaign, spend, Wards, Apprentices, MCP |
| Output/logs | The Foundry Floor | Detailed output behind short Whispers toasts |
| Local shell | The Hearth | Local shell panel; not a PTY and not Arcanum HTTP |
| Git | The Ledger | Desktop-local Git status/diff/commit; no push/pull/reset/rebase |
| Command palette | Incantation | Search and execute IDE commands |
| Trials | The Proving Grounds | Build/run Trials and local suites |
| Search/context | Divination / The Eye of the World | Semantic and global search |
| Memory | Lore Browser / The Archive | Operator Lore and Saga |
| Markdown | The Illumination | Source, split, preview with Markdig |
| Spell dependencies | The Resonance Map | Arcane Resonance graph |
| Session tabs | The Council Chamber | Multi-Session tab management |
| Entry inspection | The Loupe | Inspect one Session Entry and its metadata |
| Context help | The Codex | In-product help/documentation |
| Notifications | Whispers | Short success/error toasts; details stay on the Foundry Floor |
| Settings handoff | Compendium | Launch the standalone `arcanum.json` editor |

Other implemented operator surfaces include Campaign CRUD, Spell metadata/version diff (**The
Mirror**), Prompt designer (**The Scriptorium**), Session memory, Workspace explorer/index inspector,
Audit/guardrail browsers, OpenAI Files/Batches, MCP diagnostic fixtures, comparison/inference traces,
Mana visualization, Servants' Quarters, Comm Link alerts, and Sanctum breach monitoring.

Campaigns remain server-side solution containers. All create/open/edit/unregister/import/export
operations use HTTP; no client project-file discovery or direct Grimoire access occurs. A loopback
client may choose a local folder, while a remote connection must submit a path meaningful on the
Arcanum host.

The Forge does not edit `arcanum.json`. **Open Compendium** launches
`RetroDownfall.Compendium.Ux`; `SettingDescriptors.cs` remains Compendium's editable-key authority.
Disabled-state banners show retained setting paths and offer copy/open actions without reproducing a
complete configuration inventory. Comparison reads pricing from the server configuration surface.
War Table and Chronicle text retain canonical **Master/Apprentice** terminology.

The Proving Grounds is a singleton Workbench tab opened from **Trial → Proving Grounds**, Spell
**Create Trial**, or Scriptorium **Open in Proving Grounds**. It targets a Spell, Prompt, or
Apprentice Goal; supports Regex, JsonSchema, and Semantic Inquisitors; and runs through
`POST /api/proving-grounds/trials/run`. Local suites are versioned JSON under
`~/.config/arcanum/the-forge-trial-suites.json`.

Other desktop-local histories (suites, comparisons, traces, fixtures) are versioned JSON under
`~/.config/arcanum/`, not Grimoire tables. Inference traces may retain reasoning event type, output
mode, and token count, but replace the message with a fixed redaction and null `data` before local
persistence or export. Reload/export remains answer-only.

### 19.11 Current desktop limitations

- No true PTY Hearth or OS-level floating tool windows.
- No provider, pricing, budget, or model-metadata editor; no per-model Session token/cost breakdown.
- Guardrail policy editing remains in Compendium.
- No advanced import-conflict wizard, full Campaign settings editor, or advanced file merge.
- Illumination does not support relative workspace binary images, Mermaid, or native math.
- Local Trial suites cannot yet be created from completed batches.
- Ledger has no push, pull, reset, or rebase.
- Diagnostic MCP remains external-tool-only; Forbidden Arts and internal handlers require the Master
  pipeline (§11.28).

### 19.12 Build, packaging, and maintenance

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet run --project src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
```

`App.axaml` uses `Name="The Forge"` so the macOS menu bar is correct during development; packaged
apps set matching `CFBundleName` / `CFBundleDisplayName`.

**Windows and Linux distribution:**

- `scripts/packaging/windows/package-windows.ps1` produces `arcanum-win-x64.zip`,
  `compendium-win-x64.zip`, optional `the-forge-win-x64.zip`, and `SHA256SUMS`. `-SkipForge`
  produces the Arcanum + Compendium set. `-Sign` enables Authenticode and requires
  `WINDOWS_CERT_PATH` plus `WINDOWS_CERT_PASSWORD`; otherwise outputs are unsigned.
- `scripts/packaging/linux/package-linux.sh` produces matching `linux-x64` or `linux-arm64`
  `.tar.gz` archives plus `SHA256SUMS`. It runs on Linux; cross-OS packaging uses GitHub Actions.
- `.github/workflows/private-beta-release.yml` builds the complete Windows x64 and Linux x64 set.
  `.github/workflows/build-windows-x64.yml` builds Windows x64 Arcanum + Compendium only.
- The Windows/Linux CLI is one self-contained Native AOT executable. The Forge and Compendium are
  self-contained multi-file Avalonia folders and are not Native AOT. These channels are unsigned by
  default.

**macOS Apple Silicon distribution:**

- `.github/workflows/release-macos-arm64.yml` is a manual **Release macOS arm64** workflow on the
  Apple Silicon `macos-15-xlarge` runner. Distribution is through workflow artifacts and a draft
  GitHub Release, not the Mac App Store.
- Release signing requires an Apple Developer Program **Developer ID Application** certificate and
  six repository secrets: `APPLE_CERTIFICATE`, `APPLE_CERTIFICATE_PASSWORD`,
  `APPLE_SIGNING_IDENTITY`, `APPLE_ID`, `APPLE_TEAM_ID`, and
  `APPLE_APP_SPECIFIC_PASSWORD`. CI imports the P12 into an ephemeral keychain and requires the
  signing identity to start with `Developer ID Application:`.
- Version input accepts `MAJOR.MINOR.PATCH` with an optional prerelease suffix and rejects build
  metadata. The full value is the .NET version and `v{version}` Git tag;
  `CFBundleShortVersionString` is the numeric `MAJOR.MINOR.PATCH`, and `CFBundleVersion` is the
  GitHub run number.
- `arcanum-osx-arm64.zip` contains the signed, folder-based self-contained CLI publish plus
  `docs/Arcanum.README.md` packaged as `README.md`. The zip is notarized but cannot be stapled; the extracted executable is checked with `codesign` and
  Gatekeeper. macOS currently does not use Native AOT because the supported linker/toolchain cannot
  reliably link the full CLI closure.
- `compendium-osx-arm64.dmg` and `the-forge-osx-arm64.dmg` contain signed, notarized, stapled
  `.app` bundles. Desktop publish is multi-file by default so native libraries can be signed
  individually.
- Signing and notarization are mandatory for CI release outputs. `--skip-sign` exists only for local
  package-structure smoke tests. A successful workflow uploads all three artifacts and creates or
  updates draft release `v{version}`; rerunning the same version replaces assets with
  `gh release upload --clobber`. An operator spot-checks a clean Mac before publishing the draft.

Changes to desktop architecture, wire contracts, project structure, settings/auth behavior, UI
scope, packaging, or release workflows update this section in the same change set.

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

## 21. The Weave, Divination, and Saga (RAG)

**Purpose:** Six independently feature-flagged, gracefully-degrading RAG capabilities. **The Weave** imprints text as vectors; **Divination** is cosine semantic search; **Saga** is auto-extracted long-term associative memory (distinct from operator Lore / Lexicon).

All six capabilities are implemented (§21.1–§21.2 foundation; §21.6–§21.10 features). The durable table inventory and raw-SQL initialization boundary are in §5.4.4; behavioral and reset invariants stay here.

### 21.1 Embedding infrastructure (shared foundation)

**Layering:** `IWeaveService` / `WeaveService` (**Api** — depends on `IEmbeddingGeneratorFactory` / OpenAI SDK packages, mirroring `ChatClientFactory`). `IDivinationService` / `DivinationService` + `WeaveSchemaInitializer` + `SqliteVecExtensionLoader` + `WeaveIndexAvailability` (**Infrastructure**). `EmbeddingBlobCodec` (**Core**).

**`IWeaveService`:** `IsAvailable` from live `IOptionsMonitor` (`Enabled` + Provider + Model). Disabled → `Embeddings.FeatureDisabled` (no HTTP). Provider/timeout → sanitized `Embeddings.ProviderUnavailable`. `EmbedBatchAsync` sequential by `BatchSize`. General embedding inputs retain `ChunkAsync` sliding windows; workspace files use the deterministic line-preserving `WorkspaceCodeChunker` described in §21.7.

**Factory:** resolves `Arcanum:Integrations:Embeddings:Provider`/`Model` as `OpenAICompatible` (including Ollama `/v1`); leases are cached for the process lifetime.

**`IDivinationService.SearchAsync`:** callers pass vec0 table name + PK/embedding columns. If `WeaveIndexAvailability.IsVecAvailable`, vec0 KNN; else strip `_vec` and managed cosine over BLOB companion (`EmbeddingBlobCodec`, top-K heap, row budget). Never throws — sanitized `Result` failure.

### 21.2 Vector storage — vec0 acceleration with a managed fallback (always safe)

Per feature: durable **BLOB** table (always) + optional **`vec0`** virtual table (`distance_metric=cosine`) when extension loads. Schema created by `WeaveSchemaInitializer` after migrations (not a static embedded migration — dimensions come from `Arcanum:Integrations:Embeddings:Dimensions`). Extension load failure → managed-only; never fails startup.

**Default: managed-only** (no sqlite-vec NuGet in-tree). vec0 is performance-only.

**Dimension mismatch:** bootstrap warns when `Arcanum:Integrations:Embeddings:Dimensions` differs from stored `Dim`; it **does not** auto-truncate — the operator must `POST /api/embeddings/reset?confirm=true` (+ optional `scope`) and then re-index.

### 21.3 Configuration

Public opt-ins are `Arcanum:Features:Embeddings`, `Arcanum:Features:SessionSearch`,
`Arcanum:Features:CodebaseRetrieval`, `Arcanum:Features:AttachmentRetrieval`, `Arcanum:Features:Saga`,
`Arcanum:Features:SagaExtraction`, and `Arcanum:Features:SemanticSpellRouting`; provider/model/dimensions are under
`Arcanum:Integrations:Embeddings` (§3.4). The same section exposes
`CodebaseIndexing:WatcherDebounceMilliseconds` (default 300, clamp 50–5,000),
`CodebaseIndexing:MaxWatchers` (default 32, clamp 0–128; zero disables watchers), and
`CodebaseIndexing:ReconciliationIntervalMinutes` (default 60, clamp 1–1,440). File eligibility,
traversal limits, and extraction/routing mechanics remain code-owned. `AttachmentIndexing` exposes
hard limits for eligible bytes (1 KiB–20 MiB), extracted characters (1,000–1,000,000), chunk size
(128–8,192), overlap (0–8,191 and always below effective chunk size), chunks per version (1–2,048),
attachments per batch (1–100), queue capacity (1–10,000), retries (0–10), retry delay (1–300s),
processing timeout (5–600s), and retrieved chunks (1–50). Validation requires provider/model facts
whenever an embedding-backed feature is enabled.

### 21.4 Graceful degradation matrix

| Condition | Behavior |
|-----------|----------|
| `Arcanum:Features:Embeddings` and all embedding-backed feature flags are `false` | Pre-RAG behavior; embed APIs return `Embeddings.FeatureDisabled` immediately |
| Provider unreachable / timeout | Sanitized `Embeddings.ProviderUnavailable`; callers skip retrieval and continue |
| sqlite-vec unavailable (default) | `Mode=managed`; Divination uses BLOB cosine (`ManagedSearchRowBudget` = 50,000). Surfaced via health/`/api/meta`/`doctor` |
| vec0 claimed but unusable | `SearchAsync` returns sanitized failure (no throw) |
| `Dimensions` changed after data | Warning only; no auto-truncate |
| `Arcanum:Features:SessionSearch` = `false` | `EntryWeavingService` idles; `POST /api/sessions/divine` → **503** `Embeddings.FeatureDisabled` |
| `Arcanum:Features:CodebaseRetrieval` = `false` | `WorkspaceIndexingService` idles; no prompt injection; divine/index → **503** |
| `Arcanum:Features:AttachmentRetrieval` = `false` | Attachment creation/listing/tools remain unchanged; no extraction, embedding, or semantic prompt injection; DTO status is `NotEligible` |
| Attachment queue full / transient embedding failure | Creation and inference still succeed; bounded reconciliation recovers dropped work and retries up to the configured cap |
| Unsupported/binary/PDF/Office/image attachment | Attachment remains valid and is marked `NotEligible`; PDF/Office extraction, OCR, and image captions are not attempted |
| Watcher unavailable or watcher cap reached | Workspace status is `Watching=false`, `Degraded=true`; bounded periodic reconciliation remains active |
| Watcher overflow/error or pending-event cap reached | Mark potentially stale, expose `Overflowed` for real/cap overflow, discard the lossy event set, and schedule a bounded full reconciliation; polling never stops |
| No indexed chunks / empty WorkingDirectory | Empty results / skip retrieval — inference continues with `[None]` |
| `Arcanum:Features:Saga` = `false` | Saga retrieval/divine/read_saga are gated; **browse/delete/stats are not gated** |
| `Arcanum:Features:SagaExtraction` = `false` | Extraction drops; retrieval/API reads are unaffected |
| Extraction LLM failure | Watermark **not** advanced (retry later) |
| Malformed/empty extraction JSON | Watermark **is** advanced (“nothing this tick”) |
| Saga caps reached | Skip tick; watermark **not** advanced |
| `Arcanum:Features:SemanticSpellRouting` = `false` | `FullGrimoire` → existing LLM `SemanticRouter` unchanged |
| Spell Weave cache / prompt embed failure | Fall back to `FullGrimoire` (Debug log; no regression) |

### 21.5 Known limitations

No auto re-index on model/dimension change (use `/api/embeddings/reset`); managed scan budgeted at 50k rows; watcher and attachment-queue delivery are advisory and periodic reconciliation remains required; Session Divination has no cursor pagination; workspace and attachment index work are sequential per service instance; HTML extraction is a bounded visible-text projection rather than a browser DOM; PDF/Office/OCR remain disabled; Saga extraction is naive (no dedupe); pure spell-routing ties break by stable sort only.

**Reset scopes:** `POST /api/embeddings/reset?confirm=true` with optional `scope=all|entry|workspaceFile|saga|sessionAttachment` (snake-case aliases accepted; default `all`); unknown scope → **400** `Validation.InvalidBody`.

### 21.6 Session Divination

**Service:** `EntryWeavingService` (`BackgroundService`) — idle unless `Arcanum:Features:SessionSearch` is enabled; uses a code-owned cadence and embeds not-yet-imprinted non-empty `Entries` (SQL `LEFT JOIN`, empty filtered in SQL, batch upsert BLOB ± vec0). It is idempotent; failures retry next tick.

**API/CLI:** `POST /api/sessions/divine` + `arcanum session divine` — gates/errors in §4.3 / §8.23. Filters `CampaignId` / `Status` (default `"active"`; invalid status → **400**). `HasMore`/`NextCursor` always false/null.

### 21.7 Semantic Codebase Retrieval

**Service:** `WorkspaceIndexingService` — idle unless `Arcanum:Features:CodebaseRetrieval` is enabled. Workspace API registration and inference `WorkingDirectory` registration each request one recursive `FileSystemWatcher`; the registry is clamped by `MaxWatchers` and never creates one watcher per directory. Workspace update/unregister and host shutdown dispose obsolete watchers. Create/change/delete/rename callbacks enter a per-workspace, 4,096-path bounded coalescer. After the clamped debounce window, final filesystem state wins: editor temporary-file rename replacement and out-of-order delete notifications become one target upsert, storms on one path become one action, deleted/renamed-away paths remove chunks and embeddings, and directory events request reconciliation.

Watchers are latency hints, never a security or correctness boundary. Every incremental upsert repeats lexical containment, symlink-component resolution, stable path identity capture, opened-handle identity comparison, extension/ignored-folder checks, and byte/character size caps before reading. A symlink replacement removes previously indexed content without embedding the outside target. Watcher error, native buffer overflow, or pending-path overflow marks the volatile status degraded/potentially stale, discards the lossy action set, and schedules a bounded full scan. `ReconciliationIntervalMinutes` keeps full polling active even with healthy watchers; when watchers cannot be created or the cap is reached, reconciliation is the complete fallback. `IndexNowAsync` and `POST .../files/index` also run the same bounded reconciliation. Full walks retain `MaxFilesToIndex`, `MaxFileSizeChars`, extension, 200,000-step traversal, ignored-directory, cancellation, and symlink limits; orphan cleanup runs only after a non-truncated walk.

**Stable chunks:** `WorkspaceCodeChunker` preserves exact source slices, character offsets, and one-based line ranges; it prefers Markdown headings and common code declaration shapes without a reflection-heavy parser, falls back to bounded line-aware splits, and never separates a UTF-16 surrogate pair. `ChunkId` is deterministic from normalized workspace/path, chunk content, and repeated-content occurrence. A small edit embeds only newly identified chunks; unchanged IDs retain their BLOB/vec0 embeddings while positional/file-time metadata is refreshed. Rename to a different path rebuilds under the new identity and deletes the old path. Tables: canonical `workspace_file_chunks` includes `StartLine`/`EndLine`, with embedding BLOB/vec0 companions (§5.4.4).

**Inference:** `RetrieveSemanticContextAsync` injects `### Semantic Context (Retrieved Codebase)` (DATA); failures → `null` (never fail turn).

**API:** `.../files/divine`, `.../files/index`, read-only inspector `.../index/status` + `.../chunks` (no mutate; preview capped). Status merges durable counts with process-local `Watching`, `Degraded`, `Overflowed`, `Reconciling`, `LastEventAt`, and `LastSuccessfulIndexAt`; chunk previews include line ranges. These workspace-scoped diagnostics intentionally stay on the index-status route rather than changing global health/`doctor`. Errors §4.3.

### 21.8 Session attachment retrieval

**Extraction:** `SessionAttachmentTextExtractor` accepts strict UTF-8 plain text, Markdown, source code, JSON, YAML, XML, CSV, and logs; HTML uses bounded visible-text extraction that removes script/style content and decodes entities. Newlines are normalized deterministically. Invalid UTF-8 fails, NUL-bearing/binary content is ineligible, and character/chunk boundaries never split UTF-16 surrogate pairs. PDF, Office, images, and unknown formats remain unindexed.

**Queue and lifecycle:** `SessionAttachmentIndexingService` is a bounded single-reader channel. `SessionAttachmentStore` enqueues newly created Bound versions, refreshed versions through the same persistence path, and promoted pending rows; `SessionRepository` enqueues fork copies only after commit. Attempts have configured timeout/retry bounds. Queue overflow is logged and recovered by periodic `ReconcileAndFindPendingAsync`. Purge deletes attachment index rows in the same ambient transaction; foreign keys and reconciliation remove orphan state/chunks/embeddings, including optional vec0 rows. Prior versions remain indexed while their attachment rows remain valid. The authenticated attachment-list projection exposes only `NotEligible`, `Pending`, `Indexed`, `Failed`, or `Stale`; Command Center aggregates those states in its footer and polls only while Pending work remains, so background completion/failure becomes visible without exposing queue items or content.

**Storage and retrieval:** `session_attachment_chunks` contains Session/Attachment IDs, logical key, version, original filename, MIME, content hash, chunk index, character/line ranges, extracted content, dimensions, timestamps, and latest-version `RetrievalScope`. BLOB embeddings are authoritative; optional vec0 mirrors them. `SearchScopedAsync` ranks only the owning session's latest scopes by default, retaining managed cosine fallback when vec0 is unavailable. `WizardIntelligenceProvider` shares one query embedding across codebase, Saga, and attachment retrieval, then passes results through the per-turn ledger's session, dedupe, version, attachment-count, chunk-count, byte, and estimated-token gates. Indexing/retrieval failure remains empty context; an explicit-materialization failure remains fail-closed before provider I/O.

### 21.9 Saga (long-term associative memory)

**Contrast:** Lore/Lexicon are explicit; Saga is auto-extracted, operator-delete-only (no `scribe_saga`/`delete_saga`).

**Store:** `ISagaMemoryStore` / `SagaMemoryStore` — `saga_memories` plus embedding companions, `saga_extraction_watermarks`, and typed `saga_memory_attachment_provenance` (§5.4.4). Provenance remains after source deletion and is surfaced as unavailable.

**Service:** `SagaExtractionService` — event-driven bounded channel (`EnqueueExtraction`, DropOldest); performs headless LLM extraction after successful turns when `Arcanum:Features:SagaExtraction` is enabled. Each request snapshots the source turn's materialized attachment allowlist. The extractor returns concise typed candidates; invalid/unmaterialized attachment claims are discarded before embedding, and ephemeral attachment content without durable provenance fails closed. Caps, watermark rules, and degradation are code-owned (§21.4).

**Retrieval:** `RetrieveSagaMemoriesAsync` → `### Saga (Associative Memory)` DATA. The query embedding is shared with semantic codebase retrieval per turn.

**Surfaces:** `/api/saga*` (§4.3), MCP `read_saga` (gated), CLI `arcanum saga …`.

### 21.10 Semantic spell routing

**Modes:** `Arcanum:Features:SemanticSpellRouting` disabled → `FullGrimoire` (LLM full catalog); enabled uses the code-owned pure/hybrid routing policy (`DirectResonance` or `FilteredDivination`). Failures → `FullGrimoire`.

**`SpellWeaveCache`:** singleton description imprints; re-embed catalog on change under lock. **`SemanticSpellRouter`** is the sole hub entry (`ResolveRoutedSpellAsync`). `SemanticRouter` optional `candidates` param; name resolve still searches full catalog. `SkipSpellRouting` skips scanner + router (no embed cost).

## 22. Structured output, cost tracking, and prompt caching

These intelligence-pipeline capabilities share the same turn infrastructure.

### 22.1 Structured output enforcement (`response_format: json_schema`)

- **Validation.** `JsonSchemaHelper` (Core `Primitives`) is an AOT-safe, reflection-free JSON Schema parser/validator built on `JsonDocument`. It supports a pragmatic subset: `object` (with `properties`, `required`, `additionalProperties:false`), `string`, `number`, `integer`, `boolean`, `array` (with `items`), `enum`. Unsupported features (`anyOf`, `oneOf`, `allOf`, `$ref`, `pattern`, `format`, `minimum`/`maximum`, `minLength`/`maxLength`, `uniqueItems`, `multipleOf`) are ignored. `Parse` and `Validate` each take a `maxDepth` parameter (default 10, clamped 1–50 by `ArcanumSettingClamps.JsonSchemaMaxDepth`); schemas or payloads exceeding the depth are rejected with `StructuredOutput.SchemaInvalid` (HTTP 400).
- **Correction.** `StructuredOutputValidator.ValidateAndRetryAsync` validates the buffered candidate and, on failure, appends a corrective system message naming the errors and re-invokes the model while the invalid output/correction state changes. Repeating a previously seen state stops deterministically. Before another call it estimates the error-message token count (`InferenceTokenizerResolver` first, else `length/4`) and compares against the provider's `ContextWindowLimit`; if the correction would not fit, it stops and returns the best-effort result with a `context window too small for retry` warning. Strict streaming buffers answer/reasoning and uses the same buffered replacement calls; rejected streamed content is never released, and only replacement answer/reasoning survives. `PromptTurnResult.Warnings` (an `init` property defaulting to `[]`) carries warnings out to the endpoint.
- **Failure behavior.** Best-effort by default: after correction stops making progress the last response is returned with an `X-Arcanum-Structured-Output-Warning` response header and a `system_fingerprint` suffixed with `:arcanum:structured-output-warning`. A request whose `json_schema` wrapper sets `strict:true` flips this to a hard `400 StructuredOutput.ValidationFailed` on the buffered path and an `Error` event that terminates the stream on the streaming path (no `Result` or buffered answer/reasoning frame is emitted). Best-effort streaming remains post-hoc and does not request correction because output has already been released.
- **Provider-side constrained decoding.** `OpenAiRequestAugmentingHandler` augments outgoing `application/json` request bodies (streaming `text/event-stream` requests pass through unchanged): it injects `strict: true` into the `json_schema` wrapper; if the provider 400s mentioning `strict`, it retries once without the flag.
- **Wiring.** `StructuredOutputValidator` is a DI singleton; `WizardIntelligenceProvider.ExecutePromptAsync` invokes it for `response_format: json_schema` requests after the tool loop terminates.

### 22.2 Cost tracking and budget enforcement (`Arcanum:Cost`)

Authoritative composition is **current-call reservation → per-call context budget → reconcile**.
`ITurnRunWriter`, `IBudgetReservationService`, and the count-free provider-I/O boundary
`IModelCallExecutor` are the enforcement seams. Reservations are never multiplied by an assumed
model-call or tool-round count, and their database transaction is never held across provider I/O.

- **Pricing.** `ModelPricingEntry` (`InputPer1M`, `OutputPer1M`, `CachedPer1M`, nullable `ReasoningPer1M` USD) is keyed by model name in `Arcanum:Cost:Pricing:ModelPricing`, with `DefaultPricing` (default free) as the fallback. `CostCalculator` clamps cached input to the prompt-token count, prices non-cached input at `InputPer1M` and cached input at `CachedPer1M`; the default cached rate is zero, but a configured nonzero rate has always been charged. Reasoning tokens are a completion subset priced at `ReasoningPer1M` (falling back to output) without double billing. Configuration rejects rates outside 0–1,000,000 USD per million tokens; runtime arithmetic also clamps rates and saturates accumulated cost. Each `BillableOperations` row snapshots the applicable rates and token counts (ledger keys include provider/model/operation).
- **Billable boundary.** Every operation that invokes a token-producing provider is billable:
  chat/model rounds, embeddings, Spell routing, Lexicon/Saga extraction, and structured-output
  corrections. Model listings, non-inference health probes, `POST /api/providers/test`, and
  `POST /api/intelligence/mana` are the closed non-billable set.
- **Usage authority.** Each provider call maps `InputTokenCount`, `OutputTokenCount`, `CachedInputTokenCount`, and `ReasoningTokenCount` independently. Cached tokens remain a prompt subset; reasoning remains a completion subset. If `TotalTokenCount` is present, that provider value is authoritative even when it disagrees with the subsets (including zero). Only a missing total is derived as clamped prompt + completion. Missing usage leaves the pre-call estimate intact; reported input is retained as the original signed `long` with validity and signed `reported - estimated` variance. Invalid negative input is exposed as inconsistent and omitted from the nonnegative reported-token histogram rather than rewritten. Neither reconciliation nor later telemetry rewrites the historical estimate or provider-reported value. Multi-round/tool/correction usage is accumulated call by call without adding either subset again.
- **Missing usage.** A provider call with no usage payload contributes no `BillableOperations` row
  and no calculated cost. Arcanum does not fabricate billable token counts from the admission
  estimate.
- **Durable operation ordering.** Every completed provider call with reported usage is persisted as its own `BillableOperations` row before its cost enters the in-memory accumulator and before guardrails, structured-output checks, tool-loop finalization, or other post-processing can fail. Corrections and tool continuations therefore remain billable without a duplicate final aggregate row. Routing and extraction provider I/O remains request-cancelable, but once either call completes with usage, its ledger write uses `CancellationToken.None` so a cancellation at the provider/accounting boundary cannot release the reservation as unspent. A durable-write failure marks accounting failed, propagates the failure, and leaves the reservation conservatively outstanding rather than releasing or reconciling unverifiable spend. Session projections, success metrics, and final success audit records remain success-only.
- **Reservation scope.** Context-window admission tracks reserved answer and reserved reasoning as separate rows and conservatively adds both to materialized input. **Dollar completion headroom is deliberately different:** per call it is `max(answer/output limit, reasoning budget)`, because reasoning is a subset of completion rather than additional completion tokens. When reasoning is priced above output, the reasoning portion inside that same headroom is priced at the reasoning rate and the remainder at the output rate; otherwise the higher output rate covers the whole headroom. Supplying materialized `ContextTokenBreakdown.InputTokens` changes only the input side of the estimate and never this completion formula. An owning turn acquires a reservation for the current provider call, then `TurnAccountingHandle.EnsureReservationForContextAsync` atomically raises (never lowers) the same reservation from the latest pre-call `ContextTokenBreakdown.InputTokens` before each main/tool-continuation call. `IBudgetReservationService.AdjustAsync` excludes the reservation's old amount and rechecks committed + outstanding spend inside `BEGIN IMMEDIATE`; a failed raise blocks provider I/O. No assumed model-call multiplier is used. Actual reconciliation always uses provider-reported counts and never rewrites the estimate. Batch reservations preparse valid JSONL lines and sum each line's resolved model pricing, output limit, and reasoning budget; nested concurrent lines do not independently replace that shared aggregate reservation. Batch lines remain single-call and no-tools. Concurrent lines share only the run, reservation, and thread-safe cost accumulator; provider work remains parallel while writes through the shared scoped `TurnRunWriter` are serialized by the accounting root. Embedding input is sanitized/truncated before reservation; each successful provider batch is recorded immediately so earlier spend survives a later batch failure, and the owning operation reconciles on every exit.
- **Raw-SQL accounting boundary.** `BillableOperations.ReasoningTokens` is
  `INTEGER NOT NULL DEFAULT 0` in
  `20260721010000_AddInferenceAccountingAndIdempotencyClaims.sql`. `BillableOperations` has no EF
  entity and remains outside `ArcanumDbContext`'s compiled model; `TurnRunWriter` inserts it with
  parameterized raw SQL. Do not add an EF migration or regenerate the compiled model for this
  column. The count-only `arcanum_inference_reasoning_tokens_total` metric and
  `InferenceAuditRecord.ReasoningTokens` contain no reasoning body.
- **Local reinstall policy.** A Grimoire created before the current inference-accounting schema must
  be recreated. Stop every Arcanum host/daemon, back up anything needed, delete the database plus
  its `-wal`/`-shm` sidecars, then restart. There is intentionally no data migration. Copy-pastable
  commands are in [Arcanum.README, “Local Grimoire reinstall”](Arcanum.README.md#local-grimoire-reinstall).
- **Spend authority.** Daily spend = **`BillableOperations.CompletedAt` (UTC day) + outstanding `BudgetReservations`**. `Sessions.TotalCostUsd` / `TotalTokensUsed` remain a **projection/cache** updated via `IncrementSessionTokensAndCostAsync` for UI convenience — not admission authority. When a durable run exists, the session cost projection uses the accounting root's accumulated reconciled per-call cost; compatibility paths without a run retain the equivalent usage-based calculation.
- **Budget gate.** `BudgetMonitor.CheckAsync` prefers `IBudgetReservationService` (committed + outstanding). It falls back to summing session `TotalCostUsd` for today only when the reservation service is unavailable. At 100% of `Arcanum:Cost:Budget:DailyLimitUsd` it returns `Budget.Exceeded` (HTTP 429 on the buffered path). The code-owned 80% alert threshold dispatches a Comm Link warning and records a `BudgetAlerts` row.
- **Alert deduplication.** The `BudgetAlerts` table (migration `20260706040100_AddBudgetAlerts`) has a unique index `IX_BudgetAlerts_Threshold_Date` on `(Threshold, date(AlertedAt))`; `BudgetAlertRepository.RecordAlertAsync` swallows the resulting `SQLITE_CONSTRAINT` and returns `false` for duplicate inserts. `BudgetMonitor.TryDispatchAlertAsync` **inserts the alert row before dispatching the Comm Link notification**, so the unique index is the dedup authority under concurrent turns — the previous check-then-dispatch race that sent duplicate notifications is eliminated. `HasAlertedTodayAsync` is retained as a cheap pre-check but is no longer the sole dedup gate. Decimal columns (`SpendUsd`, `DailyLimitUsd`) are bound as `decimal`, not strings.
- **Endpoint.** `GET /api/budget` returns `BudgetSummaryDto` (enabled, daily limit, today's spend, remaining, spent percent, alert threshold). When budget is disabled, `TodaySpendUsd` is reported as `0` to avoid a Grimoire read.

### 22.3 Prompt caching (built-in capability catalog)

- **Not a response cache.** Arcanum always invokes the selected model. It never stores/replays inference responses, tool results, attachment bytes, session secrets, or untrusted streams.
- **Conservative default.** Unknown providers, endpoints, and models receive no request directive and do not claim cached-input reporting. Provider-internal automatic caching may still occur, but Arcanum does not infer it.
- **Resolution.** `ModelCapabilityCatalog` requires an exact configured model entry, `OpenAICompatible`, HTTPS `api.openai.com`, and a known family boundary (`gpt-4o`, `chatgpt-4o`, `gpt-4.1`, `gpt-5`, `o1`, `o3`, `o4`). There are no provider/model cache overrides or compatibility flags.
- **Shipped wire contract.** Known rows use the golden-tested `openAiPromptCacheRetention` contract in key-only/provider-default-retention mode. Arcanum may emit root `prompt_cache_key`; it does not emit configured retention, breakpoints, `prompt_cache_options`, or tool-level cache controls.
- **Privacy-safe plans.** Keys use a versioned length-prefixed SHA-256 construction over semantic namespace, provider/model identity, stable-segment digests, and exact finalized tool definitions when enabled. Metadata contains no raw prompt/Codex/Spell text, history, tool results, attachment bytes, paths, IDs, PII, or secrets. `main` is shared by initial/tool-continuation/structured-retry calls when the stable key is unchanged; routing/other auxiliary calls remain non-cacheable with the current single-user-message shape.
- **Per-call application.** After context admission validates the original payload, `ModelCallExecutor` clones messages/options and composes prompt caching with existing reasoning raw options. Reusable turn state is not mutated. Plans are rebuilt after compression, tool filtering/no-tools restart, trimming, structured correction, and fallback selection. The baseline root-only contract does not split content or change message count.
- **Usage and accounting.** Provider-reported `CachedInputTokenCount` is mapped, accumulated as a prompt subset, persisted per completed call, and priced from the row's `CachedPer1M` snapshot. Cache-hit metrics are emitted only when the catalog profile declares reportable cached usage. Budget reservation assumes zero cache hits.
- **Schema impact.** `CachedTokens` and `PricingSnapshotJson` (including `CachedPer1M`) are part of
  the inference-accounting schema, so prompt caching itself requires no schema reinstall. The local
  accounting-schema reinstall rule remains §5.4.5.
- **Metrics.** Per completed provider call, `arcanum_prompt_cache_calls_total` records bounded mode/eligibility/reason labels. Cached tokens and hits are observed only from provider-reported cached usage; a sent key is not a hit. `arcanum_prompt_cache_potential_savings_usd_total` prices the eligible prefix estimate and `arcanum_prompt_cache_actual_savings_usd_total` prices reported cached tokens using `max(0, InputPer1M - CachedPer1M)`. Labels are bounded to provider/model/purpose/mode/eligibility/reason—never keys, sessions, workspaces, environment names, or prompt fragments.

**Per-turn budget semantics (not loop/session).** `ReasoningRequestOptions.BudgetTokens` limits reasoning spend on **one inference turn** (one `PingRequest`); `ReasoningCapabilities.MaxBudgetTokens` caps it per model. There is **no loop/session-level reasoning cap** — an agentic turn of N rounds spends the sum of each round's budget independently. The design states this explicitly (§22.2, `EstimateWorstCaseCallsUsd`, `SaturatingMultiply`, reservation reconciliation per turn) and treats it as docs-only clarification rather than a feature change.

---

*End of design document.*
