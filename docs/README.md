# Retro Downfall Arcanum

> **Agent orientation document.** This README is written first and foremost for an **AI coding agent** (e.g. Cursor) that needs to understand Arcanum well enough to write effective prompts and code changes. It summarizes *what Arcanum is*, *the standards every change must uphold*, *how the system is shaped*, and *the patterns to follow*. For exhaustive, authoritative detail (every config key, clamp, and endpoint contract), defer to **[`DESIGN.md`](DESIGN.md)** — this file links into it throughout.

**Arcanum** is a **.NET 10, single-binary, Native AOT, local-first AI assistant and inference hub.** It ships as one self-contained native executable (`arcanum`) that runs two ways: a long-running **HTTP host** exposing an API-first surface (`arcanum serve`), and a set of **terminal commands** (`ask`, `chat`, `look`, `lore`, `daemon`, `llama`) that are thin clients over that same API. It speaks the **OpenAI API** for drop-in client compatibility, routes inference across a **multi-provider native engine** (Ollama, any OpenAI-compatible HTTP API, and local **GGUF** models via `llama.cpp`'s `llama-server`), and persists everything in an **encrypted local store** (SQLCipher).

- **Stack:** .NET 10 · ASP.NET Core Minimal API · Native AOT · `Microsoft.Extensions.AI` · EF Core 10 + SQLCipher · Spectre.Console.Cli
- **Version:** `0.1.0-beta` (see [`Directory.Build.props`](../Directory.Build.props))
- **Audience for the code:** senior C#/.NET engineers and coding agents extending an AOT-constrained, API-first system.

---

## The standards (read this first)

These are **non-negotiable** and define what "correct" means in this repo. Every prompt you write and every change you make must hold the line on all of them. They are the reason many "obvious" approaches (reflection-based JSON, `AIFunctionFactory.Create`, anonymous DTOs, inline `<script>`) are **wrong here**.

### 1. Native AOT compatibility (hard constraint)

The shipping artifact is a **Native AOT** binary with **zero runtime prerequisite**. No JIT, minimal reflection. This dictates almost every serialization and binding decision. See [DESIGN.md §9](DESIGN.md#9-native-aot-and-trimming).

- **Source-generated JSON only.** Every HTTP payload type must have a `[JsonSerializable]` registration on **`ArcanumJsonContext`** (Api). Other contexts are scoped: `GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext`, `LlamaCppJsonContext` (Core — Grimoire blobs, `arcanum.json`, campaign/skill metadata, GGUF cache manifest), `McpJsonSerializerContext` / `McpConfigJsonSerializerContext` (Infrastructure, JSON-RPC + `mcp.json`), `CommLinkInfrastructureJsonContext` (outbound webhooks). **Never** use reflection-based `JsonSerializer` overloads, `PostAsJsonAsync` with anonymous types, or `Results.Json` without an explicit `JsonTypeInfo`.
- **Source-generated request delegates.** `Api` sets `EnableRequestDelegateGenerator`; handlers must be RDG-compatible (no unbounded reflection model binding, no anonymous return DTOs).
- **Hand-authored tool schemas.** New `AIFunction` tools use explicit `JsonDocument` schemas, **not** `AIFunctionFactory.Create`.
- **Config binding** uses `EnableConfigurationBindingGenerator`.
- **Verification gate:** a clean `dotnet publish` AOT run with zero first-party IL trim/AOT warnings. Use `./scripts/verify-aot-il-warnings.sh` (see [Build & verify](#build-test--verify)).

### 2. API-first design

**The HTTP API is the product.** The CLI, the future Studio UI, LibreChat, and any sidecar are all just clients of `/api` and `/v1`. Business logic lives behind the API, never in a client.

- Add behavior as **endpoints in `MapArcanumEndpoints`**, returning the **`ApiResponse<T>`** envelope via `ApiResponse<T>.FromResult`.
- Put **domain logic in `Core`**; keep `Api` to composition/orchestration and `Cli` to thin HTTP calls (`ArcanumApiClient`).
- CLI verbs that need server state (`lore`, `daemon jobs`, `llama …`) **call the running host's API** rather than reaching into infrastructure directly.

### 3. OpenAI API compatibility

Arcanum exposes a maximum-parity **OpenAI Chat Completions** surface so existing OpenAI clients work unchanged. See [DESIGN.md §8.8](DESIGN.md#88-openai-v1-parity-surface).

- **`POST /v1/chat/completions`** (JSON or SSE) and **`GET /v1/models`** (auto-discovery across all configured providers).
- Full request parsing including multimodal `content` parts, `tool`/`assistant` tool-call replay, `stream_options.include_usage`, `response_format`, etc.
- Responses carry `usage`, `system_fingerprint`, and OpenAI-shaped error envelopes. **Auth** accepts `Authorization: Bearer <KEY>` for OpenAI clients (as well as `X-Arcanum-Key`).
- Arcanum runs **its own server-side MCP toolset**, so client-supplied `tools`/`tool_choice` are rejected with `400 unsupported_parameter` (by design — do not "fix" this by forwarding client schemas).

### 4. Top-of-the-line, all-native multi-provider inference engine

Inference flows through one hub behind a single `IChatClient` abstraction. See [DESIGN.md §10](DESIGN.md#10-intelligence-pipeline).

- **`WizardIntelligenceProvider`** (Api) implements **`IArcanumIntelligenceProvider`** (Core); **`IChatClientFactory`** builds a per-turn `IChatClient` per provider kind.
- **Providers (`AiProviderKind`):** `Ollama` (OllamaSharp), `OpenAICompatible` (`Microsoft.Extensions.AI.OpenAI` — DeepSeek, Groq, GitHub Models, LM Studio, …), and `LlamaCppServer` (local GGUF via spawned `llama-server`, fully managed lifecycle + GGUF cache).
- **No hard-coded model names.** `ProviderResolver` maps a requested/default model to a provider+model. Everything is configured under `Arcanum:Providers`.
- The engine adds agentic **MCP tool loops**, **semantic spell routing**, **read-time context compression**, **wards** (approval gates), and **Sanctum** (sandboxing) on top of raw inference.

### The Proving Grounds

**The Proving Grounds** is Arcanum's validation subsystem for spell outcomes, prompt accuracy, and Apprentice plan structure. Submit a **Trial** (target + variables + **Inquisitors**) via `POST /api/proving-grounds/trials/run` and receive a `TrialResult` with per-Inquisitor verdicts. Phase 1 is ephemeral (in-memory only; no Grimoire persistence). Inquisitor kinds: `regex`, `jsonSchema` (lightweight subset), and `semantic` (FastModel yes/no judge). The legacy industry term for LLM testing is intentionally **not** used anywhere in this project — use *Proving Grounds*, *Trial*, and *Inquisitor* instead. See [DESIGN.md §20](DESIGN.md#20-the-proving-grounds--trials-and-inquisitors).

### 5. Local-first security posture

Single-user, loopback-by-default, secret-minimizing. See [DESIGN.md §11](DESIGN.md#11-local-api-security).

- Kestrel binds **loopback only** unless explicitly opened; a **32-byte master API key** guards every `/api` and `/v1` route; the **Grimoire** is encrypted at rest (SQLCipher passphrase HKDF-derived from the key).
- Sensitive files (`arcanum.json`, Grimoire `.db`, `cli-session.txt`, logs) are created **owner-only** (`chmod 600/700` on Unix; owner ACL on Windows). Startup warns if group/other can read them.
- `Arcanum:Host:ListenAny` requires **first-run acknowledgement** in interactive `serve` (or `ARCANUM_LISTEN_ANY_ACK=1` / `ARCANUM_HOST_ANY` for automation) and emits a **security banner** when binding all interfaces over plaintext HTTP.
- In-process file/dir tools enforce **path containment + symlink resolution** and **handle-based revalidation** (pre-open path identity vs opened fd dev/ino) for read/write tools; MCP `RequestTimeoutSeconds` must be ≥ `ExecuteCommandTimeoutSeconds`; workspace `mcp.json` servers are registered only after operator trust; `execute_command` uses `ArgumentList` (no shell); outbound URLs pass an SSRF guard with **DNS-rebind IP pinning** on untrusted egress (`LlamaModelDownload`, `CommLinkWebhook`); errors return **sanitized public envelopes** (detail stays in logs).

### 6. Strict Content Security Policy on every web surface

No inline code, ever. **JavaScript belongs in `.js` files and CSS in `.css` files.** This is why the Scalar UI is opt-in and served under a tight CSP, and why any future browser UI must externalize all scripts/styles.

### 7. C# house style

- **One blank line after each line of C# code** (visual breathing room) — applied throughout the codebase.
- File-scoped namespaces; positional records for DTOs/contracts; **no `[JsonPropertyName]`** on `/api` wire types (casing comes from `[JsonSourceGenerationOptions]`); OpenAI `/v1` and MCP JSON-RPC types are explicit exceptions (§8.2); primary constructors for DI; `IDisposable` where a service owns a `SemaphoreSlim`/`ServiceProvider`. See [DESIGN.md §12](DESIGN.md#12-c-language-and-coding-conventions).

> **Note on org-wide rules:** Corp-wide standards scoped to `Corp.Solution.*` solutions (Dapper + SQL Server stored procedures, the `Corp.Lib.*` NuGet stack, Refit "Service Libraries") **do not apply to Arcanum** — it is local-first over its own EF Core + SQLCipher Grimoire and ships as one Native AOT binary. The always-on house rules (blank lines, strict CSP, docs-in-same-change-set) still hold.

### 8. Thematic naming metaphor (D&D)

Arcanum uses a Dungeons & Dragons metaphor for domain concepts. New features **must** follow it. See [Naming metaphor](#naming-metaphor).

### 9. Docs travel with code

Any change to architecture, contracts, configuration, persistence, MCP surfaces, or CLI **updates `docs/DESIGN.md` in the same change set**, and operator-visible behavior changes update this `README.md` too. Do not close work with only code changes. See [DESIGN.md §18](DESIGN.md#18-document-maintenance).

---

## Architecture at a glance

**One binary, hybrid process model.** A Spectre.Console.Cli verb selects the role: `serve` (long-running Kestrel host) vs. short-lived commands. See [DESIGN.md §5](DESIGN.md#5-hybrid-hosting-model).

**Dependency chain:** `Cli → Api → Infrastructure → Core` (`Cli` also references `Core`/`Infrastructure` directly for lightweight DI). Strict project boundaries are a deliberate goal.

| Project | Role | Owns | AOT |
|---------|------|------|-----|
| **`Core`** | Domain primitives, contracts, configuration | `Result`/`Result<T>`, `Error`, `ApiResponse<T>`, `ArcanumSettings`, `IArcanumIntelligenceProvider`, `PingRequest`, `IGrimoireRepository`, `IEyeOfTheWorld`, events, source-gen contexts (`GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext`, `LlamaCppJsonContext`) | `IsAotCompatible` |
| **`Infrastructure`** | OS-adjacent services | Serilog, Data Protection, encrypted Grimoire (EF Core 10 + SQLCipher, compiled model), workspace scanning, Eye of the World, the **MCP client layer** (subprocess + in-process transports, `ArcanumInternalToolServer`), Comm Link, GGUF cache + `llama-server` manager | `IsTrimmable` + `PublishAot` (analysis signal) |
| **`Api`** | HTTP surface composition (class library, **not** executable) | `MapArcanumEndpoints`, `ApiBootstrapper`, `WizardIntelligenceProvider`, `IChatClientFactory`, `SemanticRouter`, built-in `AIFunction` tools, `ApiKeyEndpointFilter`, `ArcanumJsonContext`, `/v1` OpenAI endpoints | `IsAotCompatible` + `EnableRequestDelegateGenerator` |
| **`Cli`** | Single shipping executable | Spectre commands, `ArcanumApiClient`, theming, AOT-safe Markdown rendering (`MarkdigSpectreRenderer`) | `PublishAot` (the native image) |
| **`Api.DevHost`** | Debug-only F5 host (not shipped) | Mirrors `serve` wiring without Spectre | — |
| **`tests/RetroDownfall.Arcanum.Tests`** | xUnit test suite (not shipped) | MCP, security, config, workspace policy, SQLCipher Grimoire, and API-host integration tests | — |

**Key entry points to know:** `ApiBootstrapper.AddArcanumApiServices` / `MapArcanumEndpoints` (wire everything), `AddArcanumInfrastructure` (Infrastructure DI), `WizardIntelligenceProvider.StreamPromptAsync` (the inference loop), `Cli/Program.cs` (command registration).

### Repository map

```
src/
  RetroDownfall.Arcanum.Core/            # domain, contracts, config, source-gen JSON contexts
    ProvingGrounds/                      # Trial / Inquisitor models and IProvingGroundsArbiter
  RetroDownfall.Arcanum.Infrastructure/  # Grimoire, MCP, perception, llama, Comm Link, Serilog
    Generated/                           # EF Core compiled model (commit regenerations)
    Data/Migrations/                     # EF Core migrations
  RetroDownfall.Arcanum.Api/             # endpoints, intelligence hub, /v1, security filter
  RetroDownfall.Arcanum.Cli/             # the `arcanum` executable (Spectre commands)
  RetroDownfall.Arcanum.Api.DevHost/     # debug-only host
tests/
  RetroDownfall.Arcanum.Tests/           # xUnit tests (MCP, security, config, workspace policy, SQLCipher Grimoire)
docs/                                    # all project documentation lives here
  README.md                              # this agent orientation document
  DESIGN.md                              # authoritative deep reference
  tests.README.md                        # test suite conventions and coverage gates
  CODEX.template.md                      # CODEX scaffold template
scripts/verify-aot-il-warnings.sh        # AOT IL-warning gate
Directory.Build.props                    # shared MSBuild props + CVE pin (Microsoft.Bcl.Memory)
```

### Patterns to follow when writing code

These are the recurring shapes. Matching them is what makes a change "fit."

- **Wire envelope.** JSON under `/api` returns `ApiResponse<T>` (`Data`, `IsSuccess`, `Error`, `TraceId`). Map from domain with `ApiResponse<T>.FromResult`. Exceptions: streaming (NDJSON), SSE event buses, and OpenAI `/v1` (raw OpenAI shape). See [DESIGN.md §8.1](DESIGN.md#81-wire-contract-the-apiresponset-envelope).
- **Result flow.** Domain ops return `Result` / `Result<T>` and rely on implicit conversions; the endpoint is the single place that turns a `Result` into an envelope + status code.
- **New endpoint checklist:** add to `MapArcanumEndpoints` → return `ApiResponse<T>` (or documented streaming shape) → register every new payload type on `ArcanumJsonContext` → `.WithName(...)` for OpenAPI → use explicit `JsonTypeInfo` on failable `Results.Json` → update DESIGN.md §4.3 + this README's API map.
- **New CLI verb:** add an `AsyncCommand` under `Cli/Commands`, register in `Program.Configure`, add `[DynamicDependency]`; prefer `AddArcanumEyeOfTheWorld()` over full infrastructure for lightweight verbs.
- **New inference provider:** add an `AiProviderKind` and extend `IChatClientFactory`; keep the `WizardIntelligenceProvider` contract intact.
- **New MCP tool:** implement on `ArcanumInternalToolServer` with a hand-authored JSON schema via `McpJsonSerializerContext`; honor workspace path containment and `ToolOutputCapBytes`; decide whether it's a **Forbidden Art** (ward-gated).
- **Treat all wire types as versioned contracts.** Casing is fixed at the context level; don't add `[JsonPropertyName]` except on OpenAI `/v1` and MCP JSON-RPC types (see [DESIGN.md §8.2](DESIGN.md#82-arcanumjsoncontext--source-generated-public)).

---

## Naming metaphor

Arcanum maps domain concepts onto a D&D fantasy metaphor. Universal terms with no clean fantasy equivalent (Prompt, Goal, Plan, Session, Entry, **Workspaces**) stay as-is. Prefer terms **well-known in pop culture**.

| Concept | Name | API / surface |
|---------|------|---------------|
| Persistent workspace | **Campaign** | `/api/campaigns` |
| Skill / capability (versioned markdown) | **Spell** | `/api/spells` (`SPELL.md` + optional `SKILL.json`) |
| Parameterized prompt template | **Prompt** | `/api/prompts` |
| Approval gate for high-risk tools | **Ward** | `/api/wards` (DM resolves allow/deny) |
| Per-campaign execution sandbox | **Sanctum** | `/api/campaigns/{id}/sanctum` |
| High-risk gated tools | **Forbidden Arts** | `Arcanum:Ward:ForbiddenArts` |
| Autonomous sub-agent | **Apprentice** | `/api/apprentices` |
| Multi-agent coordination network | **The Conclave** | `cast_sending` tool · `/api/apprentices/{id}/cast` |
| Agent event stream | **Chronicle** | `/api/apprentices/{id}/chronicle` (SSE) |
| Human operator | **Dungeon Master (DM)** | — |
| Encrypted persistence store | **Grimoire** | (internal: EF Core + SQLCipher) |
| Background job runner | **Unseen Servant** | `/api/unseen-servant/*` (canonical; `/api/daemon/*` deprecated alias) |
| Situational directory perception | **Eye of the World** | `/api/perception/look` |
| Operator key-value memory | **Lore** | `/api/lore` |
| Operator alert channel | **Comm Link** | `/api/commlink/send` |
| Inference orchestrator | **Wizard** | **`WizardIntelligenceProvider`** (implements **`IArcanumIntelligenceProvider`**) |
| Scratchpad / instructions | **Codex** | `CODEX.md`, `/api/codex` |
| Multi-turn chat thread | **Session** (rows = **Entry**) | `/api/sessions` |
| Spell/prompt/plan validation | **The Proving Grounds** (Trials, Inquisitors) | `POST /api/proving-grounds/trials/run` |

**Rejected:** Dispel, Glyph, Invocation (too obscure). The placeholder **Bureau** was retired in favor of **The Conclave** (the multi-agent coordination network; see above).

**Naming rules:** thematic API routes (`/api/spells`); error codes `{Noun}.{Verb}` (`Ward.NotFound`, `Campaign.DuplicateName`); config paths `Arcanum:{Noun}:{Setting}`. Propose any new concept name to the DM before implementing. Full rationale in this section's source and DESIGN.md §2.1.

---

## API surface map

Default base `http://localhost:5001`. **All `/api` and `/v1` routes require the API key** (`X-Arcanum-Key: <KEY>` or `Authorization: Bearer <KEY>`). This is a grouped overview — the exhaustive per-endpoint table (verbs, status codes, payload DTOs) lives in [DESIGN.md §4.3](DESIGN.md#43-retrodownfallarcanumapi-class-library-not-executable).

| Area | Routes | Notes |
|------|--------|-------|
| Health & meta | `/api/health`, `/api/meta`, `/api/grimoire/stats` | `health` returns `ApiResponse<HealthReportDto>` with Grimoire/MCP/llama/provider components (HTTP 200 for healthy/degraded). `meta` adds `uptime` and `nativeAot`. `grimoire/stats` returns db/WAL sizes and row counts. |
| Configuration | `/api/config` (GET/PUT), `/api/config/validate` | Reads redact secrets and URLs to `"***"`; PUT preserves unchanged `"***"` placeholders (apiKey, endpoint, webhook, model-map URLs). |
| Inference (native) | `/api/intelligence/ping`, `…/ping-stream`, `…/human-response`, `…/arsenal` | `ping` buffered (`PromptResponseDto`); `ping-stream` is **NDJSON** `IntelligenceEvent`. |
| Inference (OpenAI) | `POST /v1/chat/completions`, `GET /v1/models` | OpenAI-shaped JSON/SSE; **not** envelope-wrapped. |
| Sessions (Grimoire) | `/api/sessions/*` (CRUD, `/entries`, `/export`, `/rest`, `/stream`, `/analytics`) | Single source of truth for threads; FTS5 search; SSE live stream. |
| Lore | `/api/lore/*` | Operator key-value memory. |
| Spells | `/api/spells/*` (CRUD, `/search`, `/validate`, `/export`, `/import`, `/execute(-stream)`, `/versions`) | Built-in spells are read-only (`source: builtin`). `SKILL.json` `dependencies` and `declaredTools` affect **execution** (Arcane Resonance + Artifact Attunement), not just validation. List responses include optional `isValid` and `unresolvedDependencies` when Arcane Resonance deps are missing from the catalog. |
| The Forge — campaigns | `/api/campaigns/*` (+ `/codex`, `/export`, `/import`), `/api/codex` | Registers workspace roots; creates `.arcanum/`. |
| The Forge — prompts | `/api/prompts/*` (`/render`, `/test`, `/execute(-stream)`, versions) | Versioned templates with parameter schemas; `/execute(-stream)` renders and runs session-backed inference (NDJSON stream). |
| The Forge — apprentices | `/api/apprentices/*` (`/start`, `/pause`, `/resume`, `/cancel`, `/reweave`, `/intervene`, `/cast`, `/chronicle`) | Goal-driven autonomous agents with **Second Wind** (exponential retry/backoff with full jitter), **Shifting Fate** (plan re-weave), **Divine Intervention** (`Escalated` → `/intervene`), **The Conclave** cross-Apprentice delegation (`/cast` + `cast_sending`), and **Simulacrum** parallel steps; Chronicle is SSE. On host restart, `Running` and empty-plan `Planning` apprentices resume automatically; `Planning` apprentices that already have a plan are escalated for Divine Intervention. |
| Wards & Sanctum | `/api/wards/*`, `/api/campaigns/{id}/sanctum(/breaches)` | Forbidden Arts gating + per-campaign sandbox. |
| MCP | `/api/mcp` (list), `/api/mcp/{name}` (status), `/api/mcp/*` (`/start`, `/stop`, `/restart`, `/reload`, `/trust-workspace`) | Manage external + in-process MCP servers. |
| LlamaCpp | `/api/llama/models(/pull)`, `/api/llama/servers/*` | GGUF cache + `llama-server` lifecycle; pull is **NDJSON**. |
| Workspaces | `/api/workspaces/*` (+ `/files`, `/files/info`, `/files/contents`) | Registry + read-only file browser. |
| Unseen Servant | `/api/unseen-servant/*` (config/intervals; canonical), `/api/daemon/*` (deprecated alias), `/api/daemons/*` + `/api/executions/*` (registry/history) | Three route families — **unseen-servant** = interval control, **daemons** = registry. `GET /api/unseen-servant/jobs` includes `lastRunAt`, `nextDueAt`, and `lastResult` (in-memory per host process; cleared on restart — jobs may re-run once after restart). |
| Events (SSE) | `/api/events/daemon`, `…/mcp`, `…/logs` | `text/event-stream`; **not** envelope-wrapped. |
| Comm Link | `/api/commlink/send` | Outbound webhook alerts; `502` on webhook failure. |
| Perception | `/api/perception/look` | Eye of the World snapshot; requires at least one `Arcanum:Perception:AllowedWorkspaceRoots` entry (**403** when unset). |
| Providers | `/api/providers/test` | Read-only connectivity probe; does not persist. |
| The Proving Grounds | `POST /api/proving-grounds/trials/run` | Ephemeral **Trial** runner: targets a Spell, Prompt, or Apprentice Goal and adjudicates output with **Inquisitors** (`regex`, `jsonSchema`, `semantic`). Returns `ApiResponse<TrialResult>`. |
| Logs | `/api/logs` | Paginated in-memory ring buffer query. |
| Docs | `/api/openapi/v1.json`, `/api/scalar` | OpenAPI always on; Scalar opt-in + strict CSP. |

**Wire contracts:** `ApiResponse<T>` for `/api` JSON; **NDJSON** for `ping-stream`, spell/prompt `execute-stream`, and `llama/models/pull`; **SSE** for `/api/events/*`, `/api/sessions/{id}/stream`, and Chronicle; **OpenAI JSON/SSE** for `/v1/*`. See [DESIGN.md §8](DESIGN.md#8-http-json-and-minimal-api-design-api-project).

### Wire contract changes

Breaking or client-visible HTTP contract fixes (document here when no `CHANGELOG.md` exists):

| Change | Before | After |
|--------|--------|-------|
| `/api` **404** responses | Bare **404** with empty body on some routes | **`ApiResponse<T>`** envelope with `isSuccess: false`, `error`, and `traceId` |
| OpenAI **`model_not_found`** | **400** `invalid_request_error` | **404** `invalid_request_error` with `code: "model_not_found"` |
| OpenAI **`tool_calls` on `/v1`** | Observability-only `tool_calls` on completions | **`tool_calls` omitted** — server-executed MCP tools stay on native `/api` routes; see [DESIGN.md §8.8.1](DESIGN.md#881-server-executed-tools-vs-v1) |
| OpenAI **`finish_reason`** | Hard-coded `"stop"` | Mapped from provider (`length`, `content_filter`, …) |
| **Config key rename** | `Arcanum:Bureau:Enabled` (reserved no-op) | `Arcanum:Conclave:Enabled` (gates Cast Sending). Operator configs that set `Arcanum:Bureau` no longer bind and should be renamed. |
| **`GET /api/health` body** | Plain string `"Arcanum API is online"` in `ApiResponse<string>` | `ApiResponse<HealthReportDto>` with per-component status (HTTP 200 for healthy/degraded; 503 when Grimoire is unhealthy). |

---

## Inference engine details

- **Provider hub:** configure one or more entries under `Arcanum:Providers`; each has `name`, `type` (`Ollama` | `OpenAICompatible` | `LlamaCppServer`), `endpoint`, optional `apiKey`, `models[]`, and `contextWindowLimit` (default 8192). `Arcanum:DefaultModel` selects the default; `Arcanum:FastModel` is used for internal background summarization.
- **Local GGUF (`LlamaCppServer`):** Arcanum spawns and health-manages `llama-server` child processes and downloads/caches GGUF files under `~/.config/arcanum/models/`. `endpoint`/`apiKey` are ignored (the hub talks to the spawned local port). Pull models with `arcanum llama pull <url>` while `serve` runs. See [DESIGN.md §8.20](DESIGN.md#820-llamacpp-management-api-apillama).
- **Agentic features layered on inference:** semantic **spell routing** (frontmatter-only preflight → lazy body load), **Arcane Resonance** (spells declare `dependencies` in `SKILL.json`; at execution they are resolved recursively with a hard depth limit of 3, cycle-safe, their markdown bodies are concatenated into the system prompt, and `run_spell_script` is unified across the primary spell and resonant dependencies), **Artifact Attunement** (when a spell's `SKILL.json` `declaredTools` is populated, the Wizard restricts its MCP toolset — internal + external servers — to that allowlist; built-in native tools stay exempt and an empty/absent list leaves all tools available), **MCP tool loops** (bounded by `MaxToolInferenceRounds`), **read-time context compression** (swaps old entries for `Session.Summary` near the context limit; never deletes rows), **Wards** (operator approval for Forbidden Arts), **Sanctum** (per-campaign path/network/tool sandbox). Token counting uses `Microsoft.ML.Tokenizers` Tiktoken (`o200k_base`).

---

## Configuration

Settings bind under the `Arcanum` object in **`arcanum.json`**, living in the per-user config dir (created on first run): `~/.config/arcanum/` on macOS/Linux, `%USERPROFILE%\.config\arcanum\` on Windows. Override any key with env vars using the **`ARCANUM_`** prefix and `__` for nesting (use env vars for secrets — e.g. `ARCANUM_Arcanum__Providers__1__ApiKey`). Every numeric setting has a runtime clamp in `ArcanumSettingClamps`.

**The full key reference (types, defaults, clamps) is [DESIGN.md §3.4](DESIGN.md#34-configuration-reference-arcanumsettings).** Sections at a glance:

| Section | Controls |
|---------|----------|
| `Arcanum:Host` | Kestrel port, CORS, body cap, rate limiter, Scalar UI toggle, system fingerprint, default workspace, loopback vs `ListenAny`. |
| `Arcanum:Security` | API key header sizing + cache TTL (on-disk rotation propagation). |
| `Arcanum:DefaultModel` / `FastModel` / `Providers` | Multi-provider hub + model resolution. |
| `Arcanum:Intelligence` | Tool timeouts/caps, **`InferenceTimeoutSeconds`** (default 600; wall-clock cap per inference turn), agentic round cap, lore/archive gates, context compression (`ManaPreflight` LRU), optional `UseFastModelForSpellRouting`, tokenizer encoding, token tracking. **Injection bounds (enforced):** `MaxPingPromptChars`, `MaxStatelessMessages`, `MaxOpenApiMessages`, `MaxPlanSteps`, `ArchiveSearchMaxQueryLength`. |
| `Arcanum:Mcp` | MCP client timeouts, `tools/list` bounds (`MaxToolsPerServer`, `MaxToolsPerListPage`, `MaxToolsTotalBytes`), `MaxServers`, JSON-RPC line cap (`MaxJsonRpcLineBytes`), and bootstrap behavior (`BootstrapBlocksStartup`). Startup requires `MaxJsonRpcLineBytes` ≥ `Intelligence:ToolOutputCapBytes`. |
| `Arcanum:Ward` / Sanctum | Forbidden Arts list, ward timeout, `MaxActiveWards` (default 50), unattended auto-deny; per-campaign Sanctum config. |
| `Arcanum:Apprentices` | Concurrency, step timeout, Chronicle channel capacity, **Second Wind** retry/backoff (`MaxStepRetries`, `RetryBackoffSeconds`, `RetryBackoffMaxSeconds`), **Shifting Fate** / **Divine Intervention** toggles, **Simulacrum** parallel-step bound (`MaxSimulacra`, default 3, clamp 1–10). |
| `Arcanum:LlamaCpp` | `llama-server` path, GPU layers, context size, ports, cache cap, SHA-256 verification (`RequireModelHash`, default `true`; set `false` to allow unverified pulls with `verified:false` in the cache manifest). |
| `Arcanum:Grimoire` / `Sessions` | Load/query caps, snapshot retention, page sizes, SSE replay caps, `MaxEntriesPerSession` / `MaxEntryContentBytes` entry bounds (also caps stateless `/v1` and ping message content). |
| `Arcanum:CommLink` | Webhook URL, timeout, scheme allowlist; webhook responses are drained (bounded) after POST. |
| `Arcanum:Perception` / `Spells` / `Campaigns` | Path allowlists (**empty = deny by default**), campaign caps. `Arcanum:Spells:MaxFileSizeBytes` (default 256 KiB) caps spell/frontmatter reads; `Arcanum:Spells:MetadataScanCacheTtlSeconds` (default 5s, `0` disables) caches routing metadata scans. **`MaxDependencies`**, **`MaxDeclaredTools`**, **`MaxResonantDependencies`**, **`MaxResonantBytes`** enforced at API and scan. |
| `Arcanum:Prompts` | **`MaxParameterValueChars`** (default 4096) enforced on prompt render/execute parameter values. |
| `Arcanum:Daemon` / `EventBus` / `Logs` / `Workspaces` / `Codex` / `Cli` | Unseen Servant scheduling, SSE channel capacity, global `MaxSseConnections` cap (503 `Api.TooManyConnections`), log ring buffer, file-read caps, `Arcanum:Codex:MaxSizeBytes` (default 256 KiB) for CODEX reads/writes, CLI theming/attachments, **`ApiRequestTimeoutSeconds`** (default 60; non-streaming CLI API calls such as `lore` / `daemon jobs` / `llama status`; streaming `ask` / `chat` / `llama pull` stay unbounded). |
| `Arcanum:Conclave` | **The Conclave** toggle (`Enabled`, default `false`): gates cross-Apprentice delegation (`cast_sending` tool + `POST /api/apprentices/{id}/cast`). |
| `Arcanum:ProvingGrounds` | **The Proving Grounds** bounds: `MaxInquisitorsPerTrial` (default 20, clamp 1–200), `SemanticJudgeMaxTokens` (default 8), `SemanticJudgeTimeoutSeconds` (default 60). |

**Minimal example** (local Ollama + OpenAI-compatible DeepSeek; keep API keys in env vars):

```json
{
  "Arcanum": {
    "Host": { "Port": 5001 },
    "DefaultModel": "deepseek-chat",
    "FastModel": "mistral:latest",
    "Providers": [
      { "name": "Local Ollama", "type": "Ollama", "endpoint": "http://localhost:11434", "models": ["mistral:latest"], "contextWindowLimit": 8192 },
      { "name": "DeepSeek", "type": "OpenAICompatible", "endpoint": "https://api.deepseek.com/v1", "apiKey": null, "models": ["deepseek-chat"], "contextWindowLimit": 8192 }
    ]
  }
}
```

```bash
export ARCANUM_Arcanum__Providers__1__ApiKey='your-key-here'
```

`DefaultModel`/`FastModel` must match a `models` entry on some provider (case-insensitive, Ollama-style `:latest` matching). OpenAI-compatible `endpoint`s usually include `/v1`. **MCP servers** are wired via `~/.config/arcanum/mcp.json` (`mcpServers` schema); workspace-local `mcp.json` is merged only after `POST /api/mcp/trust-workspace`. See [DESIGN.md §3.4](DESIGN.md#34-configuration-reference-arcanumsettings) and the MCP host limits there.

---

## CLI quick reference

All commands run as `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- <cmd>` in development, or `arcanum <cmd>` after an AOT publish.

| Command | Purpose |
|---------|---------|
| `serve` | Run the HTTP host on `localhost:5001` (writes a PID file). Prints and logs the bound `http://` address before accepting traffic. Binding all interfaces (`Arcanum:Host:ListenAny`) requires a **first-run interactive acknowledgement** (or `ARCANUM_LISTEN_ANY_ACK=1` / `ARCANUM_HOST_ANY` for automation). |
| `ask <prompt>` | Single-turn inference (NDJSON stream). Flags: `-n` / `--new` (new session), `-m <model>`, `--unattended`, plus inference flags (below). Use `--` to pass a prompt that starts with a flag. Ctrl+C cancels the in-flight turn (exit 130). |
| `chat` | Interactive multi-turn REPL (Markdig rendering, mana bar). Flags: `-n` / `--new`, `-m`, `--no-tools`, `--unattended`, plus inference flags. **Slash commands:** `/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model [name]`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/memory`, `/summary`, `/mana`, `/attach`. Stage files inline with `@path`; the mana bar shows a persistent **(Memory Compressed)** suffix after read-time compression until `/new`. |
| `look` | Print the Eye of the World workspace snapshot (no HTTP). |
| `doctor` | Environment diagnostics (System / Paths / Configuration / MCP / Tokenizer panels) + API health probe. Timeout via `Arcanum:Cli:DoctorHealthTimeoutSeconds` (default 2s); an unreachable API is a non-fatal warning (still exits 0). |
| `key show` | Print the stored master API key from the local secret store (CLI-only; no HTTP). |
| `lore list\|get\|set\|delete` | Operator key-value memory via `/api/lore` (needs `serve`). Args: `get <KEY>`, `set <KEY> <VALUE>`, `delete <KEY>`. |
| `daemon install\|uninstall\|status` | OS background-service lifecycle. |
| `daemon jobs\|initiative\|alert` | Unseen Servant inspection + Comm Link smoke test (needs `serve`). `daemon jobs` shows **Last run**, **Next due**, **Last result** (process-local; cleared on restart). `daemon initiative <JOB_NAME> <MINUTES>` sets adaptive interval. `daemon alert <MESSAGE>` options: `--title`/`-t` (default `"Arcanum alert"`), `--severity`/`-s` (`Info`\|`Warning`\|`Critical`, default `Warning`), `--source`. |
| `campaign` | **(Route-table stub; makes no HTTP call.)** Prints the `/api/campaigns` route table (The Forge). |
| `spell search` | **(Route-table stub; makes no HTTP call.)** Prints the `/api/spells/search` route table. |
| `prompt render` | **(Route-table stub; makes no HTTP call.)** Prints the `/api/prompts/{id}/render` route table. |
| `apprentice list\|create\|start\|chronicle` | **(Route-table stubs; make no HTTP call.)** Print `/api/apprentices` route tables (The Forge). |
| `llama pull\|start\|stop\|status` | Manage local GGUF models + `llama-server` (needs `serve`). Use `--help` on `llama` subcommands for option descriptions. Abandoned `.download.tmp` partials older than 24h are swept automatically. |

**Inference flags** (both `ask`/`chat`): `--temperature` (0–2), `--top-p` (0–1), `--max-tokens` (≥1), `--seed` (int64), repeatable `--stop`, `--response-format` (`text` \| `json_object` \| `json_schema`; `json` aliases `json_object`), `--presence-penalty` / `--frequency-penalty` (−2..2). Out-of-range values are rejected by the CLI before the request is sent. The full **chat slash-command** suite is listed in the `chat` row above. At the `Mage >` prompt, **Ctrl+C** cancels the current input line; during an in-flight turn, **Ctrl+C** cancels the turn (exit code 130 for `ask`; `chat` cancels the turn and returns to the prompt). The CLI auto-disables ANSI/prompts/mana bar when stdout is redirected or `NO_COLOR`/`ARCANUM_NO_COLOR` is set. CLI failures from the API print **`[ErrorCode] message`** (matching `{Noun}.{Verb}` codes in `ApiResponse` envelopes, e.g. `Auth.Unauthorized`). Full detail: [DESIGN.md §4.4](DESIGN.md#44-retrodownfallarcanumcli-console-executable).

---

## First-run setup

On first `serve`, Arcanum generates a 32-byte master API key, **prints the raw Base64 once to stdout**, then encrypts it via Data Protection at `{ApplicationData}/arcanum/security.dat`.

```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- serve
```

For F5 debugging without Spectre, run the DevHost:

```bash
dotnet run --project src/RetroDownfall.Arcanum.Api.DevHost/RetroDownfall.Arcanum.Api.DevHost.csproj
```

> **Key rotation is destructive.** The Grimoire passphrase is HKDF-derived from the API key, so a rotated key cannot decrypt the existing store. To rotate: stop the host, remove **both** `security.dat` and the Grimoire `.db` under `~/.config/arcanum/`, then restart. To retrieve the key later (same machine), run **`arcanum key show`**. See [DESIGN.md §16.3](DESIGN.md#163-security-and-identity).

---

## Build, test & verify

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download); at least one configured provider; for local GGUF, `llama.cpp`'s `llama-server` on `PATH` (or set `Arcanum:LlamaCpp:ServerExecutablePath`).

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
```

**Code coverage** (Core + Infrastructure + Api + Cli; `Api.DevHost` is wiring-only and excluded from the denominator):

```bash
dotnet tool restore
./scripts/coverage.sh              # HTML report under .tmp/coverage/report/
./scripts/coverage.sh --threshold  # fails if line < 85%, branch < 75%, or security types < 100% branch
```

See [tests.README.md](tests.README.md) for fixtures, collections, and conventions.

SQLCipher-backed Grimoire integration tests (`[Collection("Grimoire")]`) copy an encrypted template database per test and are marked `[SkippableFact]` when the `e_sqlcipher` native runtime is unavailable on the host RID.

API host integration tests (`[Collection("ApiHost")]`) spin up `ArcanumWebApplicationFactory` against the DevHost entry point with a seeded Grimoire database, authenticated `HttpClient`, and `[SkippableFact]` when SQLCipher is unavailable. They cover lore, wards, sessions, apprentices, spell search, workspaces, meta, MCP status, logs, and OpenAI `/v1/models`.

**Native AOT publish** (self-contained binary; example for Apple Silicon — other RIDs: `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`):

```bash
dotnet publish src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -c Release -r osx-arm64
```

**AOT IL-warning gate** (first-party trim/AOT diagnostics only — run after dependency or serialization changes):

```bash
./scripts/verify-aot-il-warnings.sh            # current host RID
./scripts/verify-aot-il-warnings.sh all        # osx-arm64, osx-x64, linux-x64, win-x64
./scripts/verify-aot-il-warnings.sh all --strict   # fail if any RID is skipped (CI)
```

`dotnet build` is warning-clean in Debug/Release. `dotnet publish` may emit clang `.pcm`/`ld` toolchain notices (not IL diagnostics); on Homebrew `dotnet`, the CLI adds conditional linker paths for keg-only OpenSSL/Brotli. See [DESIGN.md §9.3](DESIGN.md#93-tradeoffs-and-constraints).

> **CVE note:** `Microsoft.Bcl.Memory` is pinned to a patched build in [`Directory.Build.props`](../Directory.Build.props) to mitigate **CVE-2026-26127** (a DoS in Base64Url decoding pulled in transitively by `Microsoft.ML.Tokenizers.Data.O200kBase`). After bumping major packages, run `dotnet list package --vulnerable` and an AOT publish to confirm no regressions.

### Database migrations (EF Core)

> **Migrations are not required yet.** Arcanum has no production Grimoire databases in the wild, so schema changes do not need a shipped migration or backfill step until real deployments exist. On first start, `GrimoireDatabaseBootstrapper` applies whatever schema migrations are bundled with the build; when new migrations become necessary, follow the workflow below.

The Grimoire uses EF Core 10 with a **compiled model** under `src/RetroDownfall.Arcanum.Infrastructure/Generated/`. After changing entities or `OnModelCreating`, regenerate and **commit both the migration and the generated sources**:

```bash
dotnet tool restore
ARCANUM_GRIMOIRE_DEV_KEY=dev-key-placeholder dotnet ef migrations add YourMigrationName \
  --project src/RetroDownfall.Arcanum.Infrastructure \
  --startup-project src/RetroDownfall.Arcanum.Infrastructure \
  --output-dir Data/Migrations --context ArcanumDbContext
ARCANUM_GRIMOIRE_DEV_KEY=dev-key-placeholder dotnet ef dbcontext optimize \
  --project src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj \
  --startup-project src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj \
  --output-dir Generated --namespace RetroDownfall.Arcanum.Infrastructure.Generated --context ArcanumDbContext
```

---

## Writing effective prompts for Arcanum

When you (an AI agent) draft a prompt or make a change in this repo, bake in the standards above. A good Arcanum prompt typically:

1. **States the AOT constraint up front** — "register the new DTO on `ArcanumJsonContext`; no reflection-based serialization; hand-author any `AIFunction` schema."
2. **Routes work through the API** — new behavior is an endpoint returning `ApiResponse<T>`; domain logic in `Core`; the CLI just calls the API.
3. **Names the right project and types** — e.g. "implement in `WizardIntelligenceProvider` (Api), contract in `IArcanumIntelligenceProvider` (Core)." Use the [repository map](#repository-map) and [naming metaphor](#naming-metaphor).
4. **Respects the metaphor** — Campaign/Spell/Ward/Apprentice/Grimoire, error codes `{Noun}.{Verb}`, config `Arcanum:{Noun}:{Setting}`.
5. **Preserves OpenAI parity** — don't change `/v1` shapes casually; remember client `tools` are intentionally rejected.
6. **Keeps the security posture** — loopback default, API key on every route, path containment, SSRF guard, sanitized errors, strict CSP (external JS/CSS only).
7. **Specifies the verification gates** — `dotnet build` clean, `dotnet test`, and `./scripts/verify-aot-il-warnings.sh` for serialization/dependency changes.
8. **Requires docs in the same change** — update `docs/DESIGN.md` (and this README for operator-visible changes).
9. **Follows C# house style** — one blank line after each line of C#; file-scoped namespaces; positional records without `[JsonPropertyName]`.

When unsure about a contract, clamp, or lifecycle detail, **read the linked DESIGN.md section** rather than guessing — it is the source of truth.

---

## Further reading

- **[`DESIGN.md`](DESIGN.md)** — the authoritative deep reference. Quick links: [§3.4 Configuration](DESIGN.md#34-configuration-reference-arcanumsettings) · [§4 Projects](DESIGN.md#4-project-model-and-dependency-graph) · [§8 HTTP/JSON design](DESIGN.md#8-http-json-and-minimal-api-design-api-project) · [§9 Native AOT](DESIGN.md#9-native-aot-and-trimming) · [§10 Intelligence pipeline](DESIGN.md#10-intelligence-pipeline) · [§11 Security](DESIGN.md#11-local-api-security) · [§17 Glossary](DESIGN.md#17-glossary) · [§19 The Forge](DESIGN.md#19-the-forge--campaign-spell-metadata-and-prompt-registry)
