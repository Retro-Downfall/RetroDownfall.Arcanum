# Retro Downfall Arcanum

> **Agent orientation document.** This README is written first and foremost for an **AI coding agent** (e.g. Cursor) that needs to understand Arcanum well enough to write effective prompts and code changes. It summarizes *what Arcanum is*, *the standards every change must uphold*, *how the system is shaped*, and *the patterns to follow*. For exhaustive, authoritative detail (every config key, clamp, and endpoint contract), defer to **[`DESIGN.md`](DESIGN.md)** — this file links into it throughout.

**Arcanum** is a **.NET 10, single-binary, Native AOT, local-first AI assistant and inference hub.** It ships as one self-contained native executable (`arcanum`) that runs two ways: a long-running **HTTP host** exposing an API-first surface (`arcanum serve`), and a set of **terminal commands** (`ask`, `chat`, `look`, `lore`, `daemon`, `llama`, `campaign`, `session`, `saga`, `spell`, `prompt`, `ward`, `trial`, `apprentice`, `model`, `provider`) that are thin clients over that same API — see the [CLI quick reference](#cli-quick-reference) for the full list. It speaks the **OpenAI API** for drop-in client compatibility, routes inference across a **multi-provider native engine** (any OpenAI-compatible HTTP API, including Ollama via its `/v1` endpoint, and local **GGUF** models via `llama.cpp`'s `llama-server`), and persists everything in an **encrypted local store** (SQLCipher).

- **Stack:** .NET 10 · ASP.NET Core Minimal API · Native AOT · `Microsoft.Extensions.AI` · EF Core 10 + SQLCipher · ConsoleAppFramework + Spectre.Console
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
- Arcanum runs **its own server-side MCP toolset** by default, so client-supplied `tools`/`tool_choice` are rejected with `400 unsupported_parameter`. Operators may opt in to **client tool forwarding** via `Arcanum:ClientToolForwarding:Enabled`; when enabled, client schemas are forwarded to the resolved provider and the returned `tool_calls` are surfaced for the client to round-trip (bypasses Arcanum's server-side tool loop, Sanctum, Wards, and tool audit logging).

### 4. Top-of-the-line, all-native multi-provider inference engine

Inference flows through one hub behind a single `IChatClient` abstraction. See [DESIGN.md §10](DESIGN.md#10-intelligence-pipeline).

- **`WizardIntelligenceProvider`** (Api) implements **`IArcanumIntelligenceProvider`** (Core) and delegates ward/Sanctum tool invocation to **`ToolExecutionPipeline`**; **`IChatClientFactory`** builds a per-turn `IChatClient` per provider kind.
- **Providers (`AiProviderKind`):** `OpenAICompatible` (`Microsoft.Extensions.AI.OpenAI` — DeepSeek, Groq, GitHub Models, LM Studio, Ollama via its `/v1` endpoint, …) and `LlamaCppServer` (local GGUF via spawned `llama-server`, fully managed lifecycle + GGUF cache).
- **No hard-coded model names.** `ProviderResolver` maps a requested/default model to a provider+model. Everything is configured under `Arcanum:Providers`.
- The engine adds agentic **MCP tool loops**, **semantic spell routing**, **read-time context compression**, **wards** (approval gates), and **Sanctum** (sandboxing) on top of raw inference.
- **Structured output** (`Arcanum:StructuredOutput`): JSON Schema responses are validated against the supplied schema (`JsonSchemaHelper`, AOT-safe, max-depth guarded) and retried on failure with a corrective system message; a context-window guard skips the retry when the appended error would not fit. Best-effort by default (last response returned with an `X-Arcanum-Structured-Output-Warning` header and a `:arcanum:structured-output-warning` `system_fingerprint` marker). Set `StrictMode: true` for a hard `400 StructuredOutput.ValidationFailed` on the buffered path, or an `Error` event that terminates the stream on the streaming path. Provider-side constrained decoding is injected via `DelegatingHandler`s — llama.cpp gets a hand-rolled JSON Schema → GBNF grammar (`grammar`), OpenAI-compatible gets `strict: true` (with a one-shot retry without `strict` if the provider 400s mentioning it). Only `application/json` request bodies are modified; `text/event-stream` passes through unchanged.
- **Cost tracking & budgets** (`Arcanum:Pricing`, `Arcanum:Budget`): per-model USD/1M-token pricing feeds `CostCalculator` (decimal arithmetic — no precision loss), accumulated per session via an atomic `IncrementSessionTokensAndCostAsync` UPDATE, and summed daily via `GetTodaySpendAsync`. `BudgetMonitor.CheckAsync` runs before every inference turn (buffered and streaming): at 100% of `DailyLimitUsd` it rejects with `Budget.Exceeded` (HTTP 429 on the buffered path); at `AlertThresholdPercent` (default 80%) it dispatches a Comm Link warning once per threshold per UTC day (deduplicated by a unique `BudgetAlerts` index). `GET /api/budget` surfaces the snapshot.
- **Prompt caching** (`Arcanum:Cache`): for llama.cpp, `LlamaCppRequestAugmentingHandler` injects `cache_prompt: true` when the estimated prompt token count (tokenizer-first, else `length/4`) meets `MinCacheableTokens`. For OpenAI-compatible providers, caching is automatic; Arcanum reads `UsageDetails.CachedInputTokenCount` and records `arcanum_prompt_cache_tokens_total` / `arcanum_prompt_cache_hits_total` metrics with strictly low-cardinality `provider`+`model` labels. `ProviderSettings.SupportsPromptCaching` (default true for `OpenAICompatible`) gates the metric recording.
- **Content guardrails** (`Arcanum:Guardrails`): an opt-in `GuardrailsPipeline` (singleton) scans inbound messages *before* inference and the model's completed text *after* inference. `DetectPii` (default `true`) rejects email/phone/SSN/credit-card input with `Guardrails.PiiDetected` (HTTP 400) via `[GeneratedRegex]` source generators (AOT-clean) before the chat client is called; `BlockToxicity` + `ToxicityBlocklist`, and `AllowedTopics`/`BlockedTopics` (regex) reject with `Guardrails.Blocked`. Matched text is redacted before it leaves the pipeline (`***@***.***`, `***-**-****`, …). A blocked output is not persisted as the assistant reply. A complete pass-through when `Enabled` is `false` (the default). The persisted, disabled-by-default **guardrails audit log** (`Arcanum:Guardrails:AuditLog`, one dated JSONL file per UTC day, matched text always redacted) records only rejected turns and is queryable via `GET /api/guardrails/audit`. See [DESIGN.md §8.27](DESIGN.md#827-content-guardrails-pii--toxicity--topics).

### The Proving Grounds

**The Proving Grounds** is Arcanum's validation subsystem for spell outcomes, prompt accuracy, and Apprentice plan structure. Submit a **Trial** (target + variables + **Inquisitors**) via `POST /api/proving-grounds/trials/run` and receive a `TrialResult` with per-Inquisitor verdicts. Phase 1 is ephemeral (in-memory only; no Grimoire persistence). Inquisitor kinds: `regex`, `jsonSchema` (lightweight subset), and `semantic` (FastModel yes/no judge). The legacy industry term for LLM testing is intentionally **not** used anywhere in this project — use *Proving Grounds*, *Trial*, and *Inquisitor* instead. See [DESIGN.md §20](DESIGN.md#20-the-proving-grounds--trials-and-inquisitors).

### 5. Local-first security posture

Single-user, loopback-by-default, secret-minimizing. See [DESIGN.md §11](DESIGN.md#11-local-api-security).

- Kestrel binds **loopback only** unless explicitly opened; a **32-byte master API key** guards every `/api` and `/v1` route; the **Grimoire** is encrypted at rest (SQLCipher passphrase derived via PBKDF2-HMAC-SHA256 with a unique 16-byte salt stored in `{grimoire.db}.kdf`).
- Sensitive files (`arcanum.json`, Grimoire `.db`, `cli-session.txt`, logs) are created **owner-only** (`chmod 600/700` on Unix; owner ACL on Windows). Startup warns if group/other can read them.
- `Arcanum:Host:ListenAny` requires **first-run acknowledgement** in interactive `serve` (or `ARCANUM_LISTEN_ANY_ACK=1` / `ARCANUM_HOST_ANY` for automation) and emits a **security banner** when binding all interfaces over plaintext HTTP.
- In-process file/dir tools enforce **path containment + symlink resolution** and **handle-based revalidation** (pre-open path identity vs opened fd dev/ino) for read/write tools; MCP `RequestTimeoutSeconds` must be ≥ `ExecuteCommandTimeoutSeconds`; workspace `mcp.json` servers are registered only after operator trust; `execute_command` uses `ArgumentList` (no shell); outbound URLs pass an SSRF guard with **DNS-rebind IP pinning** on untrusted egress (`LlamaModelDownload`, `CommLinkWebhook`, `McpHttp`); errors return **sanitized public envelopes** (detail stays in logs).

### 6. Strict Content Security Policy on every web surface

No inline code, ever. **JavaScript belongs in `.js` files and CSS in `.css` files.** This is why the Scalar UI is opt-in and served under a tight CSP, and why any future browser UI must externalize all scripts/styles.

### 7. C# house style

- **One blank line after each line of C# code** (visual breathing room) — applied throughout the codebase. Within reason. Curly braces do not require blank lines around them. Neither do control statements like if and loops, etc. Also, long-running Linq statements do not require blank lines either.
- File-scoped namespaces; positional records for DTOs/contracts; **no `[JsonPropertyName]`** on `/api` wire types (casing comes from `[JsonSourceGenerationOptions]`); OpenAI `/v1` and MCP JSON-RPC types are explicit exceptions (§8.2); primary constructors for DI; `IDisposable` where a service owns a `SemaphoreSlim`/`ServiceProvider`. See [DESIGN.md §12](DESIGN.md#12-c-language-and-coding-conventions).

> **Note on org-wide rules:** Corp-wide standards scoped to `Corp.Solution.*` solutions (Dapper + SQL Server stored procedures, the `Corp.Lib.*` NuGet stack, Refit "Service Libraries") **do not apply to Arcanum** — it is local-first over its own EF Core + SQLCipher Grimoire and ships as one Native AOT binary. The always-on house rules (blank lines, strict CSP, docs-in-same-change-set) still hold.

### 8. Thematic naming metaphor (D&D)

Arcanum uses Dungeons & Dragons and/or fantasy metaphors for domain concepts. New features **must** follow it if possible. Current exceptions include "prompt" and "workspace". See [Naming metaphor](#naming-metaphor).

### 9. Docs travel with code

Any change to architecture, contracts, configuration, persistence, MCP surfaces, or CLI **updates `docs/DESIGN.md` in the same change set**, and operator-visible behavior changes update this `README.md` too. Do not close work with only code changes. See [DESIGN.md §18](DESIGN.md#18-document-maintenance).

---

## Architecture at a glance

**One binary, hybrid process model.** A ConsoleAppFramework verb selects the role: `serve` (long-running Kestrel host) vs. short-lived commands. See [DESIGN.md §5](DESIGN.md#5-hybrid-hosting-model).

**Dependency chain:** `Cli → Api → Infrastructure → Core` (`Cli` also references `Core`/`Infrastructure` directly for lightweight DI). Strict project boundaries are a deliberate goal.

| Project | Role | Owns | AOT |
|---------|------|------|-----|
| **`Core`** | Domain primitives, contracts, configuration | `Result`/`Result<T>`, `Error`, `ApiResponse<T>`, `ArcanumSettings`, `IArcanumIntelligenceProvider`, `PingRequest`, `IGrimoireRepository`, `IEyeOfTheWorld`, events, source-gen contexts (`GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext`, `LlamaCppJsonContext`) | `IsAotCompatible` |
| **`Infrastructure`** | OS-adjacent services | Serilog, Data Protection, encrypted Grimoire (EF Core 10 + SQLCipher, compiled model), workspace scanning, Eye of the World, the **MCP client layer** (subprocess + in-process transports, `ArcanumInternalToolServer`), Comm Link, GGUF cache + `llama-server` manager | `IsTrimmable` + `PublishAot` (analysis signal) |
| **`Api`** | HTTP surface composition (class library, **not** executable) | `MapArcanumEndpoints`, `ApiBootstrapper`, `WizardIntelligenceProvider`, `ToolExecutionPipeline`, `IChatClientFactory`, `SemanticRouter`, built-in `AIFunction` tools, `ApiKeyEndpointFilter`, `ArcanumJsonContext`, `/v1` OpenAI endpoints | `IsAotCompatible` + `EnableRequestDelegateGenerator` |
| **`Cli`** | Single shipping executable | Spectre commands, `ArcanumApiClient`, theming, AOT-safe Markdown rendering (`MarkdigSpectreRenderer`) | `PublishAot` (the native image) |
| **`Api.DevHost`** | Debug-only F5 host (not shipped) | Mirrors `serve` wiring without Spectre | `PublishAot` + `IsAotCompatible` (analysis signal; not shipped) |
| **`tests/RetroDownfall.Arcanum.Tests`** | xUnit test suite (not shipped) | MCP, security, config, workspace policy, SQLCipher Grimoire, and API-host integration tests | — |
| **`tests/RetroDownfall.Compendium.Tests`** (assembly `RetroDownfall.Compendium.Ux.Tests`) | Compendium smoke tests (not shipped) | Round-trip read/write of `arcanum.json` with DataProtection key interop | — |
| **`Compendium.Ux`** | Desktop configuration editor (MAUI) | Visual editor for `arcanum.json`; metadata-driven (`SettingDescriptor` table) so every setting has a description, validated range, and correct control (dropdown for enums, live swatch for CLI theme colors); reuses Core models; dynamic system light/dark theming; `dp:v1:` secret interop | — |

**Key entry points to know:** `ApiBootstrapper.AddArcanumApiServices` / `MapArcanumEndpoints` (wire everything), `AddArcanumInfrastructure` (Infrastructure DI), `WizardIntelligenceProvider.StreamPromptAsync` (the inference loop), `Cli/Program.cs` (command registration).

### Repository map

```
src/
  RetroDownfall.Arcanum.Core/            # domain, contracts, config, source-gen JSON contexts
    ProvingGrounds/                      # Trial / Inquisitor models and IProvingGroundsArbiter
  RetroDownfall.Arcanum.Infrastructure/  # Grimoire, MCP, perception, llama, Comm Link, Serilog
    Generated/                           # EF Core compiled model (commit regenerations)
    Data/Migrations/                     # EF Core migrations
    Data/SqlMigrations/                  # SQL scripts run at startup
  RetroDownfall.Arcanum.Api/             # endpoints, intelligence hub, /v1, security filter
    ProvingGrounds/                      # trial/inquisitor endpoint wiring
  RetroDownfall.Arcanum.Cli/             # the `arcanum` executable (Spectre commands)
  RetroDownfall.Compendium.Ux/            # desktop `arcanum.json` editor (MAUI)
  RetroDownfall.Arcanum.Api.DevHost/     # debug-only host
tests/
  RetroDownfall.Arcanum.Tests/           # xUnit tests (MCP, security, config, workspace policy, SQLCipher Grimoire)
  RetroDownfall.Compendium.Tests/        # Compendium round-trip smoke tests (assembly: RetroDownfall.Compendium.Ux.Tests)
docs/                                    # all project documentation lives here
  README.md                              # this agent orientation document
  DESIGN.md                              # authoritative deep reference
  tests.README.md                        # test suite conventions and coverage gates
  CODEX.template.md                      # CODEX scaffold template
  DESIGN-KDF-UPGRADE.md                  # Grimoire key-derivation upgrade notes
scripts/coverage.sh                      # run tests, generate Cobertura + HTML coverage; pass --threshold to enforce gates
scripts/coverage_threshold.py            # tiered coverage threshold enforcement
scripts/coverage_threshold_test.py       # coverage threshold script tests
scripts/align-csharp-blanklines.sh       # C# blank-line formatter entrypoint
scripts/align_csharp_blanklines.py       # C# blank-line formatter logic
scripts/verify-aot-il-warnings.sh        # AOT IL-warning gate
Directory.Build.props                    # shared MSBuild props + CVE pin (Microsoft.Bcl.Memory)
```

### Patterns to follow when writing code

These are the recurring shapes. Matching them is what makes a change "fit."

- **Wire envelope.** JSON under `/api` returns `ApiResponse<T>` (`Data`, `IsSuccess`, `Error`, `TraceId`). Map from domain with `ApiResponse<T>.FromResult`. Exceptions: streaming (NDJSON), SSE event buses, and OpenAI `/v1` (raw OpenAI shape). See [DESIGN.md §8.1](DESIGN.md#81-wire-contract-the-apiresponset-envelope).
- **Result flow.** Domain ops return `Result` / `Result<T>` and rely on implicit conversions; the endpoint is the single place that turns a `Result` into an envelope + status code.
- **New endpoint checklist:** add to `MapArcanumEndpoints` → return `ApiResponse<T>` (or documented streaming shape) → register every new payload type on `ArcanumJsonContext` → `.WithName(...)` for OpenAPI → use explicit `JsonTypeInfo` on failable `Results.Json` → update DESIGN.md §4.3 + this README's API map.
- **New CLI verb:** add a public method (XML doc `<summary>`/`<param>` comments drive `--help` text and aliases) to a grouped command class under `Cli/Commands`, registered via `app.Add<T>("path")` in `CliApplicationFactory.RunAsync`; prefer `AddArcanumEyeOfTheWorld()` over full infrastructure for lightweight verbs.
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
| Per-campaign execution sandbox | **Sanctum** | `/api/campaigns/{campaignId}/sanctum` |
| High-risk gated tools | **Forbidden Arts** | `Arcanum:Ward:ForbiddenArts` |
| Autonomous sub-agent | **Apprentice** | `/api/apprentices` |
| Multi-agent coordination network | **The Conclave** | `cast_sending` tool · `/api/apprentices/{id}/cast` |
| Agent event stream | **Chronicle** | `/api/apprentices/{id}/chronicle` (SSE) |
| A2A Agent Card | **Heraldry** | `GET /api/conclave/a2a/agent-card` |
| A2A Task (inbound or outbound) | **Sending** (a.k.a. Delegated Quest) | `/api/conclave/a2a/*` · `dispatch_sending` tool |
| The Conclave's outward-facing A2A delegate | **Archmage Client** | `IA2AClientService`/`A2AClientService`, invoked via `dispatch_sending` |
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
| Embedding & vector substrate | **The Weave** | `Arcanum:Embeddings:*`; see [DESIGN.md §21](DESIGN.md#21-the-weave-divination-and-saga-rag) |
| Semantic search over The Weave | **Divination** | `IDivinationService`; `POST /api/sessions/divine`, `POST /api/workspaces/{id}/files/divine`, `POST /api/saga/divine` (§21) |
| Vector representation of text | **Imprint** | `IWeaveService.EmbedAsync`/`EmbedBatchAsync` ("imprints" text into The Weave; §21) |
| Long-term associative memory | **Saga** | `/api/saga/*`, `read_saga`, `arcanum saga` (§21.8) |
| Recursive Spell dependency injection | **Arcane Resonance** | `SpellDependencyResolver`; `Arcanum:Spells:MaxResonantDependencies`/`MaxResonantBytes` (DESIGN.md §10.2.2) |
| Pre-flight active-Spell selection | **Spell Routing** | `SemanticRouter` (LLM-based) + `SemanticSpellRouter` (Phase 5 embedding pre-filter); `Arcanum:Embeddings:SemanticSpellRoutingEnabled` (DESIGN.md §10.2.2, §21.9) |

**Rejected:** Dispel, Glyph, Invocation (too obscure). The placeholder **Bureau** was retired in favor of **The Conclave** (the multi-agent coordination network; see above).

**Naming rules:** thematic API routes (`/api/spells`); error codes `{Noun}.{Verb}` (`Ward.NotFound`, `Campaign.DuplicateName`) — cross-layer wire codes are centralized as `public const string` in `Core/Primitives/ErrorCodes.cs` (grouped by Validation / Hub / NotFound / etc.); HTTP status mapping for `Result.Error.Code` is centralized in `Api/TheForge/ArcanumErrorMapper.cs`; config paths `Arcanum:{Noun}:{Setting}`. Propose any new concept name to the DM before implementing. Full rationale in this section's source and DESIGN.md §2.1.

---

## API surface map

Default base `http://localhost:5001`. **All `/api` and `/v1` routes require the API key** (`X-Arcanum-Key: <KEY>` or `Authorization: Bearer <KEY>`). This is a grouped overview — the exhaustive per-endpoint table (verbs, status codes, payload DTOs) lives in [DESIGN.md §4.3](DESIGN.md#43-retrodownfallarcanumapi-class-library-not-executable).

| Area | Routes | Notes |
|------|--------|-------|
| Metrics | `GET /metrics` | Prometheus text format (`text/plain; version=0.0.4`). Outside `/api`/`/v1` and **unauthenticated by default** (safe on the default loopback bind); gate with `Arcanum:Metrics:RequireApiKey` — forced on automatically when the host binds all interfaces. See [DESIGN.md §8.22](DESIGN.md#822-metrics-endpoint-get-metrics). |
| Health & meta | `/api/health`, `/api/meta`, `/api/grimoire/stats`, `/api/budget` | `health` returns `ApiResponse<HealthReportDto>` with Grimoire/MCP/llama/provider components (HTTP 200 for healthy/degraded). `meta` adds `uptime` and `nativeAot`. `grimoire/stats` returns db/WAL sizes and row counts. `budget` returns `ApiResponse<BudgetSummaryDto>` with enabled flag, daily limit, today's spend, remaining, spent percent, and alert threshold. |
| Configuration | `/api/config` (GET/PUT), `/api/config/validate` | Reads redact secrets and URLs to `"***"`; PUT preserves unchanged `"***"` placeholders (apiKey, endpoint, webhook, model-map URLs). |
| Models & providers | `GET /api/models`, `GET /api/providers` | Read-only listings across configured providers (`ModelInfoDto[]` / `ProviderInfoDto[]`); `endpoint`/`apiKey` redacted to `"***"`; no connectivity checks. |
| Inference (native) | `/api/intelligence/ping`, `…/ping-stream`, `…/human-response`, `…/arsenal`, `…/mana` | `ping` buffered (`PromptResponseDto`); `ping-stream` is **NDJSON** `IntelligenceEvent`; `mana` is a read-only Mana (token) counter — no Grimoire writes, no inference. |
| Inference (OpenAI) | `POST /v1/chat/completions`, `GET /v1/models`, `POST /v1/embeddings` | OpenAI-shaped JSON/SSE; **not** envelope-wrapped. Images (`content[].type: "image_url"`) are gated by **Scrying** — sent to a model whose `models` entry lacks `supportsVision: true` return `400 invalid_request_error` (`code: "vision_not_supported"`); disabled Scrying returns `403` (`code: "feature_disabled"`). `GET /v1/models` is enriched with `context_window`, `supports_vision`, `provider_name`, `provider_type`, `supports_tools`, `supports_streaming` (additive; mirrors native `GET /api/models`). `POST /v1/embeddings` reuses The Weave (`IWeaveService`); `model` must match `Arcanum:Embeddings:Model` or be omitted (else `404 model_not_found`); supports `float`/`base64` `encoding_format` and pre-tokenized `int[]`/`int[][]` input. Server-executed tool calls are surfaced as `message.tool_calls`/`delta.tool_calls` for observability and transcript replay (Arcanum still runs them server-side). Client-supplied `tools`/`tool_choice` are rejected by default (`400 unsupported_parameter`); enable `Arcanum:ClientToolForwarding:Enabled` to forward them to the provider instead. |
| OpenAI compat surface | `POST /v1/moderations`, `POST /v1/images/*`, `POST /v1/audio/*` | `/moderations` is a disabled-by-default (`Arcanum:Moderations:Enabled`) pass-through stub — always unflagged when enabled, `404 feature_disabled` when not. `/images/*` and `/audio/*` always return `501 not_supported` — not implemented yet, no config toggle. See [DESIGN.md §11.18](DESIGN.md#1118-openai-moderations-post-v1moderations) / [§11.19](DESIGN.md#1119-openai-images-and-audio-stubs-post-v1images-post-v1audio). |
| Files | `POST/GET /v1/files`, `GET/DELETE /v1/files/{id}`, `GET /v1/files/{id}/content` | Standalone upload storage (multipart), feeding `/v1/batches`. Bytes on disk under a fresh GUID name (never the client filename); download always `Content-Disposition: attachment`. See [DESIGN.md §11.20](DESIGN.md#1120-openai-files-v1files). |
| Batches | `POST/GET /v1/batches`, `GET /v1/batches/{id}`, `POST /v1/batches/{id}/cancel` | Async bulk chat-completion over an uploaded JSONL file (Phase 1: `/v1/chat/completions` only). Processed out-of-band by `BatchProcessingService`; per-line output/error JSONL uploaded back through the files API. See [DESIGN.md §11.21](DESIGN.md#1121-openai-batches-v1batches). |
| Sessions (Grimoire) | `/api/sessions/*` (CRUD, `/entries`, `/export`, `/rest`, `/stream`, `/analytics`, `/divine`, `/fork`), plus memory-management routes: `DELETE /entries/{entryId}`, `POST/DELETE /entries/{entryId}/pin`, `POST /compact` | Single source of truth for threads; FTS5 search; SSE live stream. `GET /entries` returns entry history with keyset pagination and optional `?countOnly=true` for an efficient count. Memory-management endpoints are gated by `Arcanum:Sessions:AllowMemoryManagement` (default `false`); pinning is bounded by `Arcanum:Sessions:MaxPinnedEntries`; pinned entries survive read-time context compression. `POST /divine` is RAG Phase 2 semantic search over embedded entries (disabled by default; see [DESIGN.md §21.6](DESIGN.md#216-phase-2--session-divination)). `POST /fork` branches a session (optionally truncated at an entry) into a new independent session, tracked via `ForkedFromSessionId` and bounded by `Arcanum:Sessions:MaxForkDepth` (see [DESIGN.md §11.16.1](DESIGN.md#11161-session-forking-post-apisessionsidfork)). |
| Lore | `/api/lore/*` | Operator key-value memory. |
| Saga | `/api/saga/*` (list, `/divine`, delete single/all, `/stats`) | RAG Phase 4 — auto-extracted long-term associative memory (disabled by default; see [DESIGN.md §21.8](DESIGN.md#218-phase-4--saga-long-term-associative-memory)). `GET`/`DELETE`/`/stats` are not gated on `SagaEnabled`; `POST /divine` requires `Arcanum:Embeddings:Enabled` + `SagaEnabled`. |
| Spells | `/api/spells/*` (CRUD, `/search`, `/validate`, `/export`, `/import`, `/execute(-stream)`, `/versions` (list/create/update/activate), `/clone`, `/cast`) | Built-in spells are read-only (`source: builtin`). `SKILL.json` `dependencies` and `declaredTools` affect **execution** (Arcane Resonance + Artifact Attunement), not just validation. List responses include optional `isValid` and `unresolvedDependencies` when Arcane Resonance deps are missing from the catalog. `/versions` uses **string** labels (`SPELL.v{label}.md`); `/clone` copies a spell to a new name; `/cast` is a dry-run system-prompt preview (no inference). |
| The Forge — campaigns | `/api/campaigns/*` (+ `/codex`, `/export`, `/import`, `/spells`, `/prompts`, `/sessions`), `/api/codex` | Registers workspace roots; creates `.arcanum/`. `/spells`, `/prompts`, `/sessions` list resources scoped to the campaign. |
| The Forge — prompts | `/api/prompts/*` (`/render`, `/test`, `/execute(-stream)`, versions, `/clone`) | Versioned templates with parameter schemas; `/execute(-stream)` renders and runs session-backed inference (NDJSON stream); `/clone` copies to a new name/version, optionally overriding the campaign. |
| The Forge — apprentices | `/api/apprentices/*` (`/start`, `/pause`, `/resume`, `/cancel`, `/reweave`, `/intervene`, `/cast`, `/chronicle`) | Goal-driven autonomous agents with **Second Wind** (exponential retry/backoff with full jitter), **Shifting Fate** (plan re-weave), **Divine Intervention** (`Escalated` → `/intervene`), **The Conclave** cross-Apprentice delegation (`/cast` + `cast_sending`), and **Simulacrum** parallel steps; Chronicle is SSE. On host restart, `Running` and empty-plan `Planning` apprentices resume automatically; `Planning` apprentices that already have a plan are escalated for Divine Intervention. |
| The Conclave — A2A | `/api/conclave/a2a/*` (JSON-RPC + `/agent-card`) · `dispatch_sending` tool | A2A (Agent-to-Agent) protocol surface, additive to The Conclave. **Disabled by default** — requires `Arcanum:Conclave:Enabled` **and** `Arcanum:Conclave:A2A:Enabled`, plus `ServerEnabled`/`ClientEnabled` respectively. Inbound A2A messages spawn headless Apprentices (server side); `dispatch_sending` delegates outward to a remote A2A agent (client side, blocking). See [DESIGN.md §5.7.1](DESIGN.md#571-a2a-and-the-conclave). |
| Wards & Sanctum | `/api/wards/*`, `/api/campaigns/{campaignId}/sanctum(/breaches)` | Forbidden Arts gating + per-campaign sandbox: path/network/tool policy plus OS-enforced CPU time, memory, and open-file-descriptor limits (setrlimit on macOS, cgroups v2 with setrlimit fallback on Linux) on the child processes spawned by `execute_command` and `run_spell_script`. Breach history — including resource-limit breaches — is Grimoire-backed (`SanctumBreaches` table, survives host restart) with paginated `GET .../breaches` (`limit`, `before`, `tool`) and per-campaign retention (`SanctumConfig.MaxBreachCount`). |
| MCP | `/api/mcp` (list), `/api/mcp/{name}` (status), `/api/mcp/*` (`/start`, `/stop`, `/restart`, `/reload`, `/trust-workspace`) | Manage external + in-process MCP servers. |
| LlamaCpp | `/api/llama/models(/pull)`, `/api/llama/servers/*` | GGUF cache + `llama-server` lifecycle; pull is **NDJSON**. `POST /api/llama/servers/{cacheKey}/warmup` sends a minimal dummy completion to an already-running server to prime its KV-cache (`400 Llama.ServerNotRunning` if none is running — it does not start one). |
| Workspaces | `/api/workspaces/*` (+ `/files`, `/files/info`, `/files/contents`, `/files/directory`, `/files/divine`, `/files/index`) | Registry + file browser (read) plus write/modify/delete (`PUT`/`PATCH .../files/contents`, `DELETE .../files`, `POST .../files/directory`) gated by `Arcanum:Workspaces:EnableFileWrite` (default `false` — **403** `Workspace.FileWriteDisabled` when off). `POST .../files/divine` and `.../files/index` are RAG Phase 3 semantic codebase retrieval and manual re-index (disabled by default; see [DESIGN.md §21.7](DESIGN.md#217-phase-3--semantic-codebase-retrieval)). `HEAD .../files/contents` returns `Content-Length` and `Last-Modified` with an empty body for size/freshness checks. |
| Unseen Servant | `/api/unseen-servant/*` (config/intervals; canonical), `/api/daemon/*` (deprecated alias), `/api/daemons/*` + `/api/executions/*` (registry/history) | Three route families — **unseen-servant** = interval control, **daemons** = registry. `GET /api/unseen-servant/jobs` includes `lastRunAt`, `nextDueAt`, and `lastResult` (in-memory per host process; cleared on restart — jobs may re-run once after restart). |
| Events (SSE) | `/api/events/daemon`, `…/mcp`, `…/logs` | `text/event-stream`; **not** envelope-wrapped. Global (`MaxSseConnections`, default 50) and per-event-type (`MaxSseConnectionsPerType`, default 20) connection caps apply to every SSE route, including session stream and Apprentice Chronicle. |
| Comm Link | `/api/commlink/send` | Outbound webhook alerts; `502` on webhook failure. |
| Perception | `/api/perception/look` | Eye of the World snapshot; requires at least one `Arcanum:Perception:AllowedWorkspaceRoots` entry (**403** when unset). |
| Providers | `/api/providers/test` | Read-only connectivity probe; does not persist. |
| The Proving Grounds | `POST /api/proving-grounds/trials/run` | Ephemeral **Trial** runner: targets a Spell, Prompt, or Apprentice Goal and adjudicates output with **Inquisitors** (`regex`, `jsonSchema`, `semantic`). Returns `ApiResponse<TrialResult>`. |
| Logs | `/api/logs` | Paginated in-memory ring buffer query. |
| Audit | `GET /api/audit` | Persisted, disabled-by-default inference audit log (`Arcanum:Host:AuditLog`) — one dated JSONL file per UTC day, independent of the Grimoire. Optional `from`/`to`/`model`/`sessionId`/`limit` query filters; empty array when disabled. Currently populated only by `/api/intelligence/ping(-stream)` and `/v1/chat/completions`. |
| Guardrails | `GET /api/guardrails/audit` | Persisted, disabled-by-default **guardrails violation** audit log (`Arcanum:Guardrails:AuditLog`) — one dated JSONL file per UTC day, recording only turns rejected by the `GuardrailsPipeline` (PII / toxicity / topic). Optional `from`/`to`/`stage`/`violationType`/`sessionId`/`limit` query filters; empty array when disabled. Matched text is always redacted in the stored record. See [DESIGN.md §8.27](DESIGN.md#827-content-guardrails-pii--toxicity--topics). |
| Built-in tools | `POST /api/tools/invoke` | Diagnostic endpoint for directly invoking a built-in tool by name. Currently exposes `get_local_system_time`, `get_arcanum_system_info`, and `browse_web` (the last only when `Arcanum:WebBrowsing:Enabled`). Body: `{ "toolName", "arguments" }`; returns `ApiResponse<ToolInvokeResponse>` with `result` as the raw tool-output JSON. See [DESIGN.md §11.27](DESIGN.md#1127-built-in-web-browsing-tool-browse_web). |
| Docs | `/api/openapi/v1.json`, `/api/scalar` | OpenAPI always on; Scalar opt-in + strict CSP. |

**Wire contracts:** `ApiResponse<T>` for `/api` JSON; **NDJSON** for `ping-stream`, spell/prompt `execute-stream`, and `llama/models/pull`; **SSE** for `/api/events/*`, `/api/sessions/{id}/stream`, and Chronicle; **OpenAI JSON/SSE** for `/v1/*`. See [DESIGN.md §8](DESIGN.md#8-http-json-and-minimal-api-design-api-project).

**Response compression:** Gzip/Brotli compression is active for JSON responses when the client sends a matching `Accept-Encoding` (opt-in, non-breaking). NDJSON and SSE streams are always excluded so their anti-buffering headers keep working. See [DESIGN.md §8.25](DESIGN.md#825-http-response-compression).

**Idempotency-Key replay:** an optional `Idempotency-Key` request header on `POST /api/intelligence/ping(-stream)`, `POST /v1/chat/completions`, `POST /v1/embeddings`, `POST /api/spells/{name}/execute(-stream)`, and `POST /api/prompts/{id}/execute(-stream)` caches the full response (buffered or streamed) and replays it verbatim on a retry with the same key + body, instead of re-executing inference. See [DESIGN.md §11.17](DESIGN.md#1117-idempotency-key-request-replay).

### Wire contract changes

Breaking or client-visible HTTP contract fixes (document here when no `CHANGELOG.md` exists):

| Change | Before | After |
|--------|--------|-------|
| `/api` **404** responses | Bare **404** with empty body on some routes | **`ApiResponse<T>`** envelope with `isSuccess: false`, `error`, and `traceId` |
| OpenAI **`model_not_found`** | **400** `invalid_request_error` | **404** `invalid_request_error` with `code: "model_not_found"` |
| OpenAI **`tool_loop` / `timeout` failures** | **500** `inference_failed` | **503** `server_error` (mirrors native `/api` spell/prompt execute status-code mapping) |
| OpenAI **`tool_calls` on `/v1`** | `tool_calls` omitted — server-executed MCP tools stayed on native `/api` routes only | **`tool_calls` surfaced** on both buffered `message.tool_calls` and streaming `delta.tool_calls` (chunked arguments, fresh `call_...` ids per call) for observability and transcript replay — Arcanum still executes the tools server-side; see [DESIGN.md §8.8.1](DESIGN.md#881-server-executed-tools-on-v1-buffered--streaming-tool_calls) |
| OpenAI **`finish_reason`** | Hard-coded `"stop"` | Mapped from provider (`length`, `content_filter`, …) |
| **Config key rename** | `Arcanum:Bureau:Enabled` (reserved no-op) | `Arcanum:Conclave:Enabled` (gates Cast Sending). Operator configs that set `Arcanum:Bureau` no longer bind and should be renamed. |
| **`GET /api/health` body** | Plain string `"Arcanum API is online"` in `ApiResponse<string>` | `ApiResponse<HealthReportDto>` with per-component status (HTTP 200 for healthy/degraded; 503 when Grimoire is unhealthy). |
| **Spell version model** | `SpellVersionDto.Version` was `int` (file index); `execute` `?version=` was an integer | `SpellVersionDto.Version` is a **string label** with an `isActive` flag; `execute` `?version=` takes the same string label (`^[A-Za-z0-9.]+$`). Existing `SPELL.v1.md`-style files remain valid labels since integers are a subset of the charset. |
| **Provider type `Ollama` removed** | `type: "Ollama"` with Ollama-native API access via `OllamaSharp`; `:tag`-symmetric model matching (`llama3` ↔ `llama3:8b`); `Ollama.Error`/`Ollama.Pull`/`Ollama.ListModels` error codes | `type: "OpenAICompatible"` with Ollama's `/v1` endpoint (e.g. `http://localhost:11434/v1`). There is no deprecated alias — `type: "Ollama"` is no longer a valid enum value and configs must be updated. Model matching is case-insensitive **exact** match only. `Ollama.Error` is removed; inference failures now use `Hub.Error`. |
| **New built-in `browse_web` tool** | No built-in web-fetch tool — agents could not reach the web without an external MCP server | New `browse_web` built-in tool (gated by `Arcanum:WebBrowsing:Enabled`, default `false`). When enabled, the model can fetch a URL (subject to `OutboundUrlGuard` SSRF + Sanctum network policy) and receive `{ title, content, links }`. Also reachable via the new `POST /api/tools/invoke` diagnostic endpoint and the `arcanum browse <url>` CLI command. Client-supplied `tools`/`tool_choice` on `/v1` are still rejected by default with `400 unsupported_parameter` (Arcanum runs its own server-side toolset). See [DESIGN.md §11.27](DESIGN.md#1127-built-in-web-browsing-tool-browse_web). |
| **Client tool forwarding on `/v1/chat/completions`** | Client-supplied `tools`/`tool_choice` always rejected with `400 unsupported_parameter` | New opt-in `Arcanum:ClientToolForwarding:Enabled` (default `false`). When enabled, validated client `tools`/`tool_choice` are forwarded to the resolved provider, the returned `tool_calls` are surfaced with provider-minted ids preserved, and the client must round-trip `role: "tool"` replies. Bypasses Arcanum's server-side tool loop, Sanctum, Wards, and tool audit logging. See [DESIGN.md §8.8.3](DESIGN.md#883-client-tool-security-forwarding-mode). |

---

## Inference engine details

- **Provider hub:** configure one or more entries under `Arcanum:Providers`; each has `name`, `type` (`OpenAICompatible` | `LlamaCppServer`), `endpoint`, optional `apiKey`, `models[]`, and `contextWindowLimit` (default 8192). `OpenAICompatible` covers any OpenAI-shaped HTTP API, including Ollama via its own `/v1` endpoint. `Arcanum:DefaultModel` selects the default; `Arcanum:FastModel` is used for internal background summarization.
- **Local GGUF (`LlamaCppServer`):** Arcanum spawns and health-manages `llama-server` child processes and downloads/caches GGUF files under `~/.config/arcanum/models/`. `endpoint`/`apiKey` are ignored (the hub talks to the spawned local port). Pull models with `arcanum llama pull <url>` while `serve` runs. On `serve` startup, a pid registry under `~/.config/arcanum/models/.pids/` is swept for `llama-server` processes orphaned by a previous crash/`SIGKILL` and, once verified live and matching (pid + start time + process name), terminated to reclaim VRAM/RAM before new servers start — see [DESIGN.md §16.7](DESIGN.md#167-reliability--performance-hardening). See [DESIGN.md §8.20](DESIGN.md#820-llamacpp-management-api-apillama).
- **Agentic features layered on inference:** semantic **spell routing** (frontmatter-only preflight → lazy body load; optionally embedding-based pre-filter — see RAG Phase 5 below), **Arcane Resonance** (spells declare `dependencies` in `SKILL.json`; at execution they are resolved recursively with a hard depth limit of 3, cycle-safe, their markdown bodies are concatenated into the system prompt, and `run_spell_script` is unified across the primary spell and resonant dependencies), **Artifact Attunement** (when a spell's `SKILL.json` `declaredTools` is populated, the Wizard restricts its MCP toolset — internal + external servers — to that allowlist; built-in native tools stay exempt and an empty/absent list leaves all tools available), **MCP tool loops** (bounded by `MaxToolInferenceRounds`; an unexpected tool exception is tolerated and synthesized into a tool result by default — `Arcanum:Intelligence:TolerateToolFailures`, set `false` to fail the whole buffered turn instead), **read-time context compression** (swaps old entries for `Session.Summary` near the context limit; never deletes rows), **Wards** (operator approval for Forbidden Arts), **Sanctum** (per-campaign path/network/tool sandbox). Token counting uses `Microsoft.ML.Tokenizers` Tiktoken (`o200k_base`).
- **Scrying (vision/multimodality):** each `Providers[].models` entry may declare `supportsVision` (bare string form defaults to `false`); `Arcanum:Scrying` gates image content (native `ContentParts`/`ScryingFoci` and `/v1` `image_url`) with a master kill-switch (`Enabled`, default `true`), a per-image size cap for `data:`-URI images (`MaxImageBytes`), a per-request image count cap (`MaxImagesPerRequest`), and a MIME allow-list (`AllowedMimeTypes`). The gate runs before any inference token is consumed and rejects images to a model that does not declare `supportsVision` (`400 Scrying.VisionNotSupported`). CLI `ask --image <path>` (repeatable) and `chat`'s inline `@path` (when the extension is an image type) attach ephemeral Scrying foci — never persisted to the Grimoire. See [DESIGN.md §10.2.4](DESIGN.md#1024-scrying--the-visionmultimodality-capability-gate).
- **A2A (Agent-to-Agent) interoperability (disabled by default):** `Arcanum:Conclave:A2A` layers an external door onto The Conclave using the `A2A`/`A2A.AspNetCore` SDK (`1.0.0-preview2`) — no Protocol Buffers involved, and it ships its own Native AOT-safe `System.Text.Json` source-generated context. Server side, an external A2A client's message spawns a headless Apprentice via the same `IConclaveArchmage` `cast_sending` uses, and the Apprentice's own Chronicle drives the A2A task lifecycle (`Submitted → Working → Completed/Failed/Canceled`, `Escalated → InputRequired`). Client side, the `dispatch_sending` MCP tool blocks on a remote A2A agent and returns its response text, governed by an in-memory concurrency semaphore (`MaxExternalTasks`), the `AllowedRemoteAgents` allowlist, and the same `OutboundUrlGuard` SSRF hardening used everywhere else outbound. See [DESIGN.md §5.7.1](DESIGN.md#571-a2a-and-the-conclave).
- **RAG and Semantic Retrieval (disabled by default, all five phases implemented):** `Arcanum:Embeddings:Enabled` gates **The Weave**, Arcanum's embedding and vector substrate, and **Divination**, semantic search over it. `IWeaveService` imprints text into vectors via a per-provider `IEmbeddingGeneratorFactory` (all `OpenAICompatible` providers, including Ollama via `/v1`, share one OpenAI-compatible embeddings path; `LlamaCppServer` uses its dedicated local-server lifecycle); `IDivinationService` runs cosine-similarity KNN search, accelerating to a sqlite-vec `vec0` index when available and otherwise falling back to a managed brute-force scan (SIMD-vectorized via `Vector<float>`, zero functional difference from the `vec0` path). **Phase 2** (`Arcanum:Embeddings:SessionSearchEnabled`) imprints Grimoire entries in the background (`EntryWeavingService`) for `POST /api/sessions/divine` / `arcanum session divine`. **Phase 3** (`Arcanum:Embeddings:CodebaseRetrievalEnabled`) indexes workspace files in the background (`WorkspaceIndexingService`) and, when enabled, `WizardIntelligenceProvider` retrieves semantically relevant chunks for the current prompt and injects them into the system prompt as a `### Semantic Context (Retrieved Codebase)` DATA section before every inference turn with a non-empty working directory. **Phase 4 — Saga** (`Arcanum:Embeddings:SagaEnabled`) auto-extracts long-term associative memories from inference conversations in the background (`SagaExtractionService`, event-driven) and, when enabled, injects semantically relevant memories as a `### Saga (Associative Memory)` DATA section; browse/search/delete via `/api/saga/*`, `read_saga` (MCP, read-only), and `arcanum saga`. **Phase 5 — semantic spell routing** (`Arcanum:Embeddings:SemanticSpellRoutingEnabled`) adds an embedding-based pre-filter in front of the existing LLM-based spell router: pure mode picks the highest-similarity spell with no LLM call, hybrid mode (`SpellRoutingHybridMode`) narrows the catalog to the top `SpellRoutingHybridTopK` candidates before the LLM router picks. Every phase has zero behavior change when disabled (the default). See [DESIGN.md §21](DESIGN.md#21-the-weave-divination-and-saga-rag).

---

## Configuration

Settings bind under the `Arcanum` object in **`arcanum.json`**, living in the per-user config dir (created on first run): `~/.config/arcanum/` on macOS/Linux, `%USERPROFILE%\.config\arcanum\` on Windows. Override any key with env vars using the **`ARCANUM_`** prefix and `__` for nesting (use env vars for secrets — e.g. `ARCANUM_Arcanum__Providers__1__ApiKey`). Every numeric setting has a runtime clamp in `ArcanumSettingClamps`. On `arcanum serve` startup the configuration is validated **before serving**, and the host aborts with a clear logged message (not a crash) when settings are semantically invalid — an unknown default/fast model, an MCP timeout / JSON-RPC ordering conflict, a llama port range that overflows 65535, or a missing/relative allow-list root.

> **Compendium** — a .NET 10 MAUI desktop editor for `arcanum.json` is available at `src/RetroDownfall.Compendium.Ux`. It reuses Core models, supports System Light/Dark modes, and interoperates with Arcanum's DataProtection-encrypted provider keys. See [`docs/COMPENDIUM.md`](COMPENDIUM.md).

**The full key reference (types, defaults, clamps) is [DESIGN.md §3.4](DESIGN.md#34-configuration-reference-arcanumsettings).** Sections at a glance:

| Section | Controls |
|---------|----------|
| `Arcanum:Host` | Kestrel port, CORS, body cap, rate limiter, Scalar UI toggle, system fingerprint, default workspace, loopback vs `ListenAny`. |
| `Arcanum:Security` | API key header sizing + cache TTL (on-disk rotation propagation); `IdempotencyTtlHours` / `IdempotencyMaxResponseBytes` for `Idempotency-Key` request replay caching. |
| `Arcanum:DefaultModel` / `FastModel` / `Providers` | Multi-provider hub + model resolution. |
| `Arcanum:Intelligence` | Tool timeouts/caps, **`InferenceTimeoutSeconds`** (default 600; wall-clock cap per inference turn), agentic round cap, lore/archive gates, context compression (`ManaPreflight` LRU), optional `UseFastModelForSpellRouting`, tokenizer encoding, token tracking. **Injection bounds (enforced):** `MaxPingPromptChars`, `MaxStatelessMessages`, `MaxOpenApiMessages`, `MaxPlanSteps`, `ArchiveSearchMaxQueryLength`. |
| `Arcanum:Mcp` | MCP client timeouts, `tools/list` bounds (`MaxToolsPerServer`, `MaxToolsPerListPage`, `MaxToolsTotalBytes`), `MaxServers`, JSON-RPC line cap (`MaxJsonRpcLineBytes`), and bootstrap behavior (`BootstrapBlocksStartup`). Startup requires `MaxJsonRpcLineBytes` ≥ `Intelligence:ToolOutputCapBytes`. |
| `Arcanum:Ward` / Sanctum | Forbidden Arts list, ward timeout, `MaxActiveWards` (default 50), unattended auto-deny; per-campaign Sanctum config, including OS-enforced CPU/memory/file-descriptor limits (`SanctumConfig.ResourceLimits`, not bound from `Arcanum:*` — set per campaign via the Sanctum API). |
| `Arcanum:Apprentices` | Concurrency, step timeout, Chronicle channel capacity, **Second Wind** retry/backoff (`MaxStepRetries`, `RetryBackoffSeconds`, `RetryBackoffMaxSeconds`), **Shifting Fate** / **Divine Intervention** toggles, **Simulacrum** parallel-step bound (`MaxSimulacra`, default 3, clamp 1–10). |
| `Arcanum:LlamaCpp` | `llama-server` path, GPU layers, context size, ports, cache cap, SHA-256 verification (`RequireModelHash`, default `true`; set `false` to allow unverified pulls with `verified:false` in the cache manifest). |
| `Arcanum:Grimoire` / `Sessions` | Load/query caps, snapshot retention, page sizes, SSE replay caps, `MaxEntriesPerSession` / `MaxEntryContentBytes` entry bounds (also caps stateless `/v1` and ping message content), `MaxForkDepth` lineage cap for `POST /api/sessions/{id}/fork`, `AllowMemoryManagement` gate for entry delete/pin/compact, `MaxPinnedEntries` cap. |
| `Arcanum:CommLink` | Webhook URL, timeout, scheme allowlist (defaults to `["https"]`; add `"http"` to allow plaintext), optional host allowlist; webhook responses are drained (bounded) after POST. |
| `Arcanum:Perception` / `Spells` / `Campaigns` | Path allowlists (**empty = deny by default**), campaign caps. `Arcanum:Spells:MaxFileSizeBytes` (default 256 KiB) caps spell/frontmatter reads; `Arcanum:Spells:MetadataScanCacheTtlSeconds` (default 5s, `0` disables) caches routing metadata scans. **`MaxDependencies`**, **`MaxDeclaredTools`**, **`MaxResonantDependencies`**, **`MaxResonantBytes`** enforced at API and scan. |
| `Arcanum:Prompts` | **`MaxParameterValueChars`** (default 4096) enforced on prompt render/execute parameter values. |
| `Arcanum:Daemon` / `EventBus` / `Logs` / `Workspaces` / `Codex` / `Cli` | Unseen Servant scheduling, SSE channel capacity, global `MaxSseConnections` cap (default 50) plus a per-event-type `MaxSseConnectionsPerType` cap (default 20, clamp 1–50) so one stream family — for example a log watcher — cannot starve the others (both return 503 `Api.TooManyConnections`; the per-type response names the saturated event type), log ring buffer, file-read caps, `Arcanum:Codex:MaxSizeBytes` (default 256 KiB) for CODEX reads/writes, CLI theming/attachments, **`ApiRequestTimeoutSeconds`** (default 60; non-streaming CLI API calls such as `lore` / `daemon jobs` / `llama status`; streaming `ask` / `chat` / `llama pull` stay unbounded). **`Workspaces:EnableFileWrite`** (default `false`) master-gates the write/modify/delete surface; **`MaxFileWriteSizeBytes`** (default 1 MiB, clamp 1 KiB–10 MiB) caps `PUT` content and the `PATCH` `newString`; **`MaxReplaceTextBlockBytes`** (default 512 KiB, clamp 1 KiB–4 MiB) caps the combined `oldString` + `newString` on `PATCH`. |
| `Arcanum:Conclave` | **The Conclave** toggle (`Enabled`, default `false`): gates cross-Apprentice delegation (`cast_sending` tool + `POST /api/apprentices/{id}/cast`). |
| `Arcanum:ProvingGrounds` | **The Proving Grounds** bounds: `MaxInquisitorsPerTrial` (default 20, clamp 1–200), `SemanticJudgeMaxTokens` (default 8), `SemanticJudgeTimeoutSeconds` (default 60). |
| `Arcanum:Resilience` | Provider health probing and fallback (**disabled by default**). `Enabled` (default `false`) turns on periodic probing (`HealthProbeIntervalSeconds`, default 30s; `HealthRecoveryProbeIntervalSeconds` for unhealthy providers, default 60s) and fallback resolution. `HealthFailureThreshold` (default 3) consecutive failures marks a provider Unhealthy; `MaxFallbackAttempts` (default 3) bounds candidates tried per turn; `HealthProbeTimeoutSeconds` (default 5) bounds each probe call. When disabled, behavior is unchanged — exactly one provider is resolved per turn. |
| `Arcanum:Metrics` | `GET /metrics` Prometheus endpoint. `Enabled` (default `true`) toggles the route (`404` when `false`). `RequireApiKey` (default `false`) moves the route behind the `/api` key filter instead of standalone; forced to effectively `true` whenever the host binds all interfaces, regardless of this setting. |
| `Arcanum:Scrying` | Vision/multimodality image gate. `Enabled` (default `true`) is a master kill-switch — `false` rejects images at the API boundary even for vision-capable models. `MaxImageBytes` (default 1 MiB, clamp 1 KiB–20 MiB) caps decoded `data:`-URI image size (`http(s)` URLs are not size-checked; the provider fetches them). `MaxImagesPerRequest` (default 10, clamp 1–100) caps images per turn. `AllowedMimeTypes` (default `image/png`, `image/jpeg`, `image/gif`, `image/webp`, `image/bmp`) is enforced for `data:`-URI images only. Model vision capability is declared per-model on `Arcanum:Providers[].models` (`supportsVision`), not here. |
| `Arcanum:Embeddings` | **The Weave** and **Divination** (RAG, all five phases implemented; disabled by default). `Enabled` (default `false`) is the master toggle; `Provider`/`Model` select the embedding provider/model (required when `Enabled`); `Dimensions` (default 768), `BatchSize`, `ChunkSizeChars`/`ChunkOverlapChars`, `SimilarityThreshold` (default 0.70), `MaxResults`, `RequestTimeoutSeconds` tune imprinting and Divination. `SessionSearchEnabled` (Phase 2) gates `EntryWeavingService` + `EmbeddingQueueIntervalSeconds` (default 10s). `CodebaseRetrievalEnabled` (Phase 3) gates `WorkspaceIndexingService` + the nested **`Codebase`** sub-record (`MaxFilesToIndex`, `MaxFileSizeChars`, `FileExtensions`, `IndexingIntervalMinutes`, `MaxRetrievedChunks`). `SagaEnabled` (Phase 4) gates `SagaExtractionService` + `/api/saga/*` + `read_saga` and the nested **`Saga`** sub-record (`ExtractionEnabled`, `MaxMemoriesPerSession`, `MaxMemoriesTotal`, `ExtractionModel`, `ExtractionMaxTokens`, `ExtractionIntervalMinutes`, `ExtractionWindowEntries`). `SemanticSpellRoutingEnabled` (Phase 5) gates the embedding-based spell-routing pre-filter, tuned by `SpellRoutingHybridMode` (default `false`) and `SpellRoutingHybridTopK` (default 3). Every feature flag requires `Enabled` to also be `true`. See [DESIGN.md §21](DESIGN.md#21-the-weave-divination-and-saga-rag). |
| `Arcanum:Guardrails` | **Content guardrails** (Tier 3 Phase 4; disabled by default). `Enabled` (default `false`) is the master toggle for the `GuardrailsPipeline`; `DetectPii` (default `true`) rejects PII-bearing input before inference; `BlockToxicity` (default `false`) + `ToxicityBlocklist` reject toxicity in input or output; `AllowedTopics`/`BlockedTopics` (regex) enforce topic policy. The nested **`AuditLog`** sub-record (`Enabled`, `FilePath`, `MaxSizeMb`, `RetentionDays`) gates the persisted guardrails violation audit log + `GET /api/guardrails/audit`. Matched text is always redacted. See [DESIGN.md §8.27](DESIGN.md#827-content-guardrails-pii--toxicity--topics). |
| `Arcanum:Pricing` | Per-model USD-per-1M-token pricing (`ModelPricing` dictionary of `ModelPricingEntry` { `InputPer1M`, `OutputPer1M` }) and `DefaultPricing` fallback (default free). Feeds `CostCalculator` (decimal arithmetic — no precision loss). See [DESIGN.md §22.2](DESIGN.md#222-cost-tracking-and-budget-enforcement-arcanumpricing-arcanumbudget). |
| `Arcanum:Budget` | Daily spend enforcement (disabled by default). `Enabled` (default `false`) is the master toggle; `DailyLimitUsd` (default `0`, clamp 0–1,000,000) rejects inference at 100% (HTTP 429); `AlertThresholdPercent` (default `80`, clamp 1–100) dispatches a Comm Link warning once per threshold per UTC day (deduplicated by a unique `BudgetAlerts` index). `GET /api/budget` surfaces the snapshot. See [DESIGN.md §22.2](DESIGN.md#222-cost-tracking-and-budget-enforcement-arcanumpricing-arcanumbudget). |
| `Arcanum:Cache` | Prompt caching (disabled by default). `Enabled` (default `false`) is the master toggle; `MinCacheableTokens` (default `256`, clamp 1–131,072) gates llama.cpp `cache_prompt: true` injection. OpenAI-compatible caching is automatic; Arcanum reads `UsageDetails.CachedInputTokenCount` and records `arcanum_prompt_cache_tokens_total` / `arcanum_prompt_cache_hits_total` metrics with strictly low-cardinality `provider`+`model` labels. See [DESIGN.md §22.3](DESIGN.md#223-prompt-caching-arcanumcache). |
| `Arcanum:StructuredOutput` | JSON Schema response validation, retry, and provider-side constrained decoding. `Enabled` (default `true`) is the master toggle; `MaxValidationRetries` (default `2`, clamp 0–10); `UseProviderConstrainedDecoding` (default `true`) injects GBNF grammar (llama.cpp) or `strict: true` (OpenAI-compatible); `StrictMode` (default `false`) flips best-effort to a hard `400 StructuredOutput.ValidationFailed`; `SchemaMaxDepth` (default `10`, clamp 1–50). See [DESIGN.md §22.1](DESIGN.md#221-structured-output-enforcement-arcanumstructuredoutput). |

**Minimal example** (local Ollama via its OpenAI-compatible endpoint + OpenAI-compatible DeepSeek; keep API keys in env vars):

```json
{
  "Arcanum": {
    "Host": { "Port": 5001 },
    "DefaultModel": "deepseek-chat",
    "FastModel": "mistral:latest",
    "Providers": [
      { "name": "Local Ollama", "type": "OpenAICompatible", "endpoint": "http://localhost:11434/v1", "models": ["mistral:latest"], "contextWindowLimit": 8192 },
      { "name": "DeepSeek", "type": "OpenAICompatible", "endpoint": "https://api.deepseek.com/v1", "apiKey": null, "models": ["deepseek-chat"], "contextWindowLimit": 8192 },
      { "name": "OpenAI", "type": "OpenAICompatible", "endpoint": "https://api.openai.com/v1", "apiKey": null, "models": [{ "name": "gpt-4o", "supportsVision": true }, "gpt-4o-mini"], "contextWindowLimit": 128000 }
    ]
  }
}
```

The `"Local Ollama"` entry targets Ollama's own OpenAI-compatible `/v1` endpoint (not its native `11434` root) — this is the only way Arcanum talks to Ollama. The `"OpenAI"` entry shows both `models` forms: `"gpt-4o-mini"` (bare string, `supportsVision` defaults `false`) and `{ "name": "gpt-4o", "supportsVision": true }` (object form, declaring Scrying/vision support — see `Arcanum:Scrying` above).

```bash
export ARCANUM_Arcanum__Providers__1__ApiKey='your-key-here'
```

`DefaultModel`/`FastModel` must match a `models` entry on some provider — matching is a case-insensitive **exact** match, with no bare-name or tag-stripping fallback. OpenAI-compatible `endpoint`s usually include `/v1`. **MCP servers** are wired via `~/.config/arcanum/mcp.json` (`mcpServers` schema) over **stdio** (`command`/`args`, with an optional `inheritEnv` allowlist for `npx`-style launches) or **Streamable HTTP** (`type: "http"` or a bare `url`, SSRF-guarded and `https`-by-default); workspace-local `mcp.json` is merged only after `POST /api/mcp/trust-workspace`. See [DESIGN.md §3.4](DESIGN.md#34-configuration-reference-arcanumsettings) and the MCP host limits there.

---

## CLI quick reference

All commands run as `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- <cmd>` in development, or `arcanum <cmd>` after an AOT publish.

| Command | Purpose |
|---------|---------|
| `serve` | Run the HTTP host on `localhost:5001` (writes a PID file). Prints and logs the bound `http://` address before accepting traffic. Binding all interfaces (`Arcanum:Host:ListenAny`) requires a **first-run interactive acknowledgement** (or `ARCANUM_LISTEN_ANY_ACK=1` / `ARCANUM_HOST_ANY` for automation). |
| `ask <prompt>` | Single-turn inference (NDJSON stream). Flags: `-n` / `--new` (new session), `-m <model>`, `-c` / `--campaign <id>`, `--unattended`, `--image <path>` (repeatable — attach a Scrying focus; requires a vision-capable model), plus inference flags (below). Use `--` to pass a prompt that starts with a flag. Ctrl+C cancels the in-flight turn (exit 130). Running `ask` before a key is stored exits **1** with a friendly "run `arcanum serve` once" message (no crash). |
| `chat` | Interactive multi-turn REPL (Markdig rendering, mana bar). Flags: `-n` / `--new`, `-m`, `-c` / `--campaign <id>` (shown in the startup banner when set), `--no-tools`, `--unattended`, plus inference flags. **Slash commands:** `/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model [name]`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/memory`, `/summary`, `/mana`, `/attach`. Stage text files inline with `@path`; an `@path` whose extension is an image type (`.png`/`.jpg`/`.jpeg`/`.gif`/`.webp`/`.bmp`) stages a **Scrying focus** instead (prints `Scrying focus: <name> (<size>)`; requires a vision-capable model). The mana bar shows a persistent **(Memory Compressed)** suffix after read-time compression until `/new`. |
| `look` | Print the Eye of the World workspace snapshot (no HTTP). |
| `doctor` | Environment diagnostics (System / Paths / Configuration / MCP / Tokenizer panels) + API health probe. Timeout via `Arcanum:Cli:DoctorHealthTimeoutSeconds` (default 2s); an unreachable API is a non-fatal warning (still exits 0). Use `--fix-permissions` to apply owner-only permissions to the Grimoire database, `arcanum.json`, and secret store. Use `--json` to emit a structured `DoctorReport` to stdout for programmatic consumption (exit code 0 if healthy, 1 otherwise). |
| `key show` | Print the stored master API key from the local secret store (CLI-only; no HTTP). |
| `lore list\|get\|set\|delete` | Operator key-value memory via `/api/lore` (needs `serve`). Args: `get <KEY>`, `set <KEY> <VALUE>`, `delete <KEY>`. |
| `daemon install\|uninstall\|status` | OS background-service lifecycle. |
| `daemon jobs\|initiative\|alert` | Unseen Servant inspection + Comm Link smoke test (needs `serve`). `daemon jobs` shows **Last run**, **Next due**, **Last result** (process-local; cleared on restart). `daemon initiative <JOB_NAME> <MINUTES>` sets adaptive interval. `daemon alert <MESSAGE>` options: `--title`/`-t` (default `"Arcanum alert"`), `--severity`/`-s` (`Info`\|`Warning`\|`Critical`, default `Warning`), `--source`. |
| `campaign list\|get\|create\|update\|delete\|export\|import\|codex\|spells\|prompts\|sessions` | The Forge campaign registry via `/api/campaigns` (needs `serve`). `create --name <n> --path <p> [--type <t>]`; `export`/`import <id>` round-trip JSON (stdout/`--output` or `--file`); `codex get\|put\|delete <id>` manages `CODEX.md`; `spells\|prompts\|sessions <id>` list campaign-scoped resources (campaign spells shadow built-ins of the same name). |
| `session divine <QUERY>` | RAG Phase 2 — semantic search over Grimoire entries via `POST /api/sessions/divine` (needs `serve`; disabled by default — requires `Arcanum:Embeddings:Enabled` + `SessionSearchEnabled`). Options: `--limit <n>`, `--campaign <id>`, `--status <status>`. See [DESIGN.md §21.6](DESIGN.md#216-phase-2--session-divination). |
| `saga list\|divine\|delete\|stats` | RAG Phase 4 — Saga long-term associative memory via `/api/saga/*` (needs `serve`). `list` (options `--query`, `--session`, `--limit`, `--offset`) and `stats` are always available; `divine <QUERY>` (option `--limit`) requires `Arcanum:Embeddings:Enabled` + `SagaEnabled`; `delete <ID>` removes a single memory. See [DESIGN.md §21.8](DESIGN.md#218-phase-4--saga-long-term-associative-memory). |
| `spell list\|get\|create\|update\|delete\|search\|validate\|execute\|versions\|export\|import\|cast\|clone` | The Forge spell CRUD + execution via `/api/spells` (needs `serve`). `create`/`update` require `--workspace`; `--body`/`--goal`/`--template`/`--plan`/`--inquisitor` accept inline text or `@filename`; `execute` prints the response text plus a tool-call summary (stderr) when tools ran (`--version` takes a **string label**); `cast <name>` is a dry-run system-prompt preview — no inference tokens consumed; `clone <name> --new-name <n>` clones a spell into the workspace. |
| `spell version create\|update\|activate` | Named spell version files (`SPELL.v{label}.md`) via `/api/spells/{name}/versions` (needs `serve`). `create`/`update <name> --version <label> --body <text\|@file>`; `activate <name> --version <label>` swaps the version into `SPELL.md`, printing where the previous content was preserved. |
| `prompt list\|get\|versions\|create\|update\|delete\|render\|test\|execute\|export\|import\|clone` | The Forge prompt CRUD + rendering via `/api/prompts` (needs `serve`). `render`/`execute` accept repeatable `--param key=value`; `test` assembles the system prompt at no LLM cost; `clone <id> --new-name <n> --new-version <v> [--campaign <id>]` copies to a new name/version. |
| `ward list\|get\|resolve` | Ward approval gates via `/api/wards` (needs `serve`). `resolve <id>` requires exactly one of `--allow`/`--deny` plus optional `--reason`. |
| `trial run` | The Proving Grounds via `POST /api/proving-grounds/trials/run` (needs `serve`). `--target spell\|prompt\|apprenticeGoal` + `--target-value`, repeatable `--inquisitor` (JSON or `@file`) and `--var key=value`; exits `1` when the Trial fails. |
| `apprentice list\|get\|create\|delete\|start\|pause\|resume\|cancel\|reweave\|intervene\|cast\|chronicle` | The Forge Apprentice orchestration via `/api/apprentices` (needs `serve`). `create --goal <text\|@file>`; `reweave --plan <json\|@file>`; `cast` reports 409 `Apprentice.ConclaveDisabled` when `Arcanum:Conclave:Enabled` is off; `chronicle <id>` streams live SSE events (Ctrl+C exits 130). |
| `llama pull\|start\|stop\|status` | Manage local GGUF models + `llama-server` (needs `serve`). Use `--help` on `llama` subcommands for option descriptions. Abandoned `.download.tmp` partials older than 24h are swept automatically. |
| `model list` | List configured models across all providers via `GET /api/models` (needs `serve`); endpoint redacted. |
| `provider list` | List configured providers via `GET /api/providers` (needs `serve`); `apiKey`/`endpoint` redacted. |
| `browse <url>` | Fetch a web page via the built-in `browse_web` tool (requires `Arcanum:WebBrowsing:Enabled`; needs `serve`). Renders title, content preview, and link list. |

**Inference flags** (both `ask`/`chat`): `--temperature` (0–2), `--top-p` (0–1), `--max-tokens` (≥1), `--seed` (int64), repeatable `--stop`, `--response-format` (`text` \| `json_object` \| `json_schema`; `json` aliases `json_object`), `--presence-penalty` / `--frequency-penalty` (−2..2), `-c` / `--campaign <id>` (sets `PingRequest.CampaignId`; server resolves the workspace from the Grimoire campaign path, 400 `Campaign.NotFound` if unknown). Out-of-range values are rejected by the CLI before the request is sent. **Scrying image attachment:** `ask --image <path>` (repeatable) or `chat`'s inline `@path` (image extension) stage a Scrying focus for the current turn only — capped by `Arcanum:Scrying:MaxImageBytes`/`MaxImagesPerRequest`/`AllowedMimeTypes` and rejected with `Scrying.VisionNotSupported` if the active model does not declare `supportsVision`. The full **chat slash-command** suite is listed in the `chat` row above. At the `Mage >` prompt, **Ctrl+C** cancels the current input line; during an in-flight turn, **Ctrl+C** cancels the turn (exit code 130 for `ask`; `chat` cancels the turn and returns to the prompt). The CLI auto-disables ANSI/prompts/mana bar when stdout is redirected or `NO_COLOR`/`ARCANUM_NO_COLOR` is set. CLI failures from the API print **`[ErrorCode] message`** (matching `{Noun}.{Verb}` codes in `ApiResponse` envelopes, e.g. `Auth.Unauthorized`). The `--body`/`--template`/`--goal`/`--plan`/`--inquisitor` flags across `campaign`/`spell`/`prompt`/`apprentice`/`trial` accept either inline text or `@filename` to read from a file. Full detail: [DESIGN.md §4.4](DESIGN.md#44-retrodownfallarcanumcli-console-executable).

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

> **Key rotation.** New Grimoire databases use a dedicated encryption secret, so rotating the master API key only invalidates API authentication; the Grimoire `.db` and `.kdf` files can stay in place. Legacy databases encrypted directly from the API key remain destructive to rotate. To retrieve the key later (same machine), run **`arcanum key show`**. See [DESIGN.md §16.3](DESIGN.md#163-security-and-identity).

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

`dotnet build` is warning-clean in Debug/Release. `dotnet publish` may emit clang `.pcm`/`ld` toolchain notices (not IL diagnostics); on Homebrew `dotnet`, the CLI adds conditional linker paths for keg-only OpenSSL/Brotli, and forces the classic `ld_classic` linker on macOS to work around a confirmed Xcode 15+ `ld64` crash (`"too many large addends"`, [dotnet/runtime#119380](https://github.com/dotnet/runtime/issues/119380)) that large Native AOT binaries can trigger. See [DESIGN.md §9.3](DESIGN.md#93-tradeoffs-and-constraints).

> **CVE note:** `Microsoft.Bcl.Memory` is pinned to a patched build in [`Directory.Build.props`](../Directory.Build.props) to mitigate **CVE-2026-26127** (a DoS in Base64Url decoding pulled in transitively by `Microsoft.ML.Tokenizers.Data.O200kBase`). After bumping major packages, run `dotnet list package --vulnerable` and an AOT publish to confirm no regressions.

### Database migrations (EF Core)

> **Migrations are not required yet.** Arcanum has no production Grimoire databases in the wild, so schema changes do not need a shipped migration or backfill step until real deployments exist. Because of this, the migration history is squashed back down to a single `InitialCreate` baseline (both the EF Core migration and its hand-authored SQL twin) whenever it grows unwieldy, rather than being carried forward forever — there is no installed base to upgrade, so there is nothing to preserve by keeping old incremental scripts around. On first start, `GrimoireDatabaseBootstrapper` applies whatever schema migrations are bundled with the build; when new migrations become necessary, follow the workflow below.

> **Purely additive tables** that are accessed only via raw SQL (not added to `ArcanumDbContext`'s `DbSet`s) skip the `dotnet ef migrations add` / `dotnet ef dbcontext optimize` workflow below entirely — hand-author the `.sql` file, register it in `GrimoireSqlSchemaMigrator`'s `MigrationOrder`, and stop there. `SanctumBreaches` and `UnseenServantWatermarks` are both folded into the current `InitialCreate.sql` baseline this way: new table, no backfill, no config element changes, no Compendium (`arcanum.json` editor) updates needed — see [`persistence.md`](persistence.md) §9. The migration applies on first start with zero risk to existing data.

> **Squashing the history:** when the incremental migration list gets long enough to be more archaeology than documentation, collapse it back to one `InitialCreate` migration id (EF Core `.cs`/`.Designer.cs` pair regenerated from the current model via `dotnet ef migrations add InitialCreate` after deleting the old ones, plus a hand-authored `.sql` twin with the same migration id under `Data/SqlMigrations/`) and reset `GrimoireSqlSchemaMigrator.MigrationOrder` to that single id. The resulting schema must be byte-for-byte identical to what the old chain produced — verify by replaying the old scripts against a scratch SQLite file and diffing `pragma_table_info`/index/trigger output against the new single script before deleting anything. Keep every `CREATE TABLE`/`CREATE INDEX` in the squashed script guarded with `IF NOT EXISTS` so a lost `__EFMigrationsHistory` row is still safely recoverable by re-running the (now monolithic) migration.

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
- **[`persistence.md`](persistence.md)** — which operational state lives in the Grimoire vs. in memory, serialization/migration/retention conventions, and what is intentionally never persisted (Wards, SSE subscriber state, live token streams)
- **[`COMPENDIUM.md`](COMPENDIUM.md)** — desktop `arcanum.json` editor: project layout, theming, secret interop, and build/run instructions
