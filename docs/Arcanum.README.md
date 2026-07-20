# Retro Downfall Arcanum

> **Agent orientation document.** This README is written first and foremost for an **AI coding agent** (e.g. Cursor) that needs to understand Arcanum well enough to write effective prompts and code changes. It summarizes *what Arcanum is*, *the standards every change must uphold*, *how the system is shaped*, and *the patterns to follow*. For exhaustive, authoritative detail (every config key, clamp, and endpoint contract), defer to **[`Arcanum.DESIGN.md`](Arcanum.DESIGN.md)** — this file links into it throughout.

**Arcanum** is a **.NET 10, single-binary, Native AOT, local-first AI assistant and inference hub.** It ships as one self-contained native executable (`arcanum`) that runs two ways: a long-running **HTTP host** exposing an API-first surface (`arcanum serve`), and a set of **terminal commands** (`ask`, `chat`, `look`, `lore`, `daemon`, `campaign`, `session`, `saga`, `spell`, `prompt`, `ward`, `trial`, `apprentice`, `model`, `provider`) that are thin clients over that same API — see the [CLI quick reference](#cli-quick-reference) for the full list. It speaks the **OpenAI API** for drop-in client compatibility, routes inference across a **multi-provider native engine** (any OpenAI-compatible HTTP API, including Ollama via its `/v1` endpoint), and persists everything in an **encrypted local store** (SQLCipher).

- **Stack:** .NET 10 · ASP.NET Core Minimal API · Native AOT · `Microsoft.Extensions.AI` · EF Core 10 + SQLCipher · ConsoleAppFramework + Spectre.Console
- **Version:** `0.1.0-beta` (see [`Directory.Build.props`](../Directory.Build.props))
- **Audience for the code:** senior C#/.NET engineers and coding agents extending an AOT-constrained, API-first system.

---

## The standards (read this first)

These are **non-negotiable** and define what "correct" means in this repo. Every prompt you write and every change you make must hold the line on all of them. They are the reason many "obvious" approaches (reflection-based JSON, `AIFunctionFactory.Create`, anonymous DTOs, inline `<script>`) are **wrong here**.

### 1. Native AOT compatibility (hard constraint)

The shipping artifact is a **Native AOT** binary with **zero runtime prerequisite**. No JIT, minimal reflection. This dictates almost every serialization and binding decision. See [DESIGN.md §9](Arcanum.DESIGN.md#9-native-aot-and-trimming).

- **Source-generated JSON only.** Every HTTP payload type must have a `[JsonSerializable]` registration on **`ArcanumJsonContext`** (Api). Other contexts are scoped: `GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext` (Core — Grimoire blobs, `arcanum.json`, campaign/skill metadata), `McpJsonSerializerContext` / `McpConfigJsonSerializerContext` (Infrastructure, JSON-RPC + `mcp.json`), `CommLinkInfrastructureJsonContext` (outbound webhooks). **Never** use reflection-based `JsonSerializer` overloads, `PostAsJsonAsync` with anonymous types, or `Results.Json` without an explicit `JsonTypeInfo`.
- **Source-generated request delegates.** `Api` sets `EnableRequestDelegateGenerator`; handlers must be RDG-compatible (no unbounded reflection model binding, no anonymous return DTOs).
- **Hand-authored tool schemas.** New `AIFunction` tools use explicit `JsonDocument` schemas, **not** `AIFunctionFactory.Create`.
- **Config binding** uses `EnableConfigurationBindingGenerator`. Settings POCOs under `Arcanum:…` must use `{ get; set; }` (not `init`) — the generator silently skips `init`-only properties (dotnet/runtime#107856), which previously left `Providers` / `DefaultModel` empty at runtime while `arcanum.json` still looked correct.
- **Verification gate:** a clean `dotnet publish` AOT run with zero first-party IL trim/AOT warnings. Use `./scripts/verify-aot-il-warnings.sh` (see [Build & verify](#build-test--verify)).

### 2. API-first design

**The HTTP API is the product.** The CLI, the future Studio UI, LibreChat, and any sidecar are all just clients of `/api` and `/v1`. Business logic lives behind the API, never in a client.

- Add behavior as **endpoints in `MapArcanumEndpoints`**, returning the **`ApiResponse<T>`** envelope via `ApiResponse<T>.FromResult`.
- Put **domain logic in `Core`**; keep `Api` to composition/orchestration and `Cli` to thin HTTP calls (`ArcanumApiClient`).
- CLI verbs that need server state (`lore`, `daemon jobs`, …) **call the running host's API** rather than reaching into infrastructure directly.

### 3. OpenAI API compatibility

Arcanum exposes a maximum-parity **OpenAI Chat Completions** surface so existing OpenAI clients work unchanged. See [DESIGN.md §8.8](Arcanum.DESIGN.md#88-openai-v1-parity-surface).

- **`POST /v1/chat/completions`** (JSON or SSE) and **`GET /v1/models`** (auto-discovery across all configured providers).
- Full request parsing including multimodal `content` parts, `tool`/`assistant` tool-call replay, `stream_options.include_usage`, `response_format`, etc.
- Responses carry `usage`, `system_fingerprint`, and OpenAI-shaped error envelopes. **Auth** accepts `Authorization: Bearer <KEY>` for OpenAI clients (as well as `X-Arcanum-Key`).
- Arcanum runs **its own server-side MCP toolset** by default, so client-supplied `tools`/`tool_choice` are rejected with `400 unsupported_parameter` (except `tool_choice: "auto"`/`"none"`, which are always accepted as OpenAI defaults). Operators may opt in to **client tool forwarding** via `Arcanum:ClientToolForwarding:Enabled`; when enabled, client schemas are forwarded to the resolved provider (per-tool `strict` flag preserved via `AIFunction.AdditionalProperties`), `tool_choice.function.name` is verified against the supplied `tools`, and the returned `tool_calls` are surfaced for the client to round-trip (bypasses Arcanum's server-side tool loop, Sanctum, Wards, and tool audit logging).

### 4. Top-of-the-line, all-native multi-provider inference engine

Inference flows through one hub behind a single `IChatClient` abstraction. See [DESIGN.md §10](Arcanum.DESIGN.md#10-intelligence-pipeline); turn order is in [Arcanum.CHAT-LOOP.md](Arcanum.CHAT-LOOP.md).

- **`WizardIntelligenceProvider`** + **`ToolExecutionPipeline`** + **`IChatClientFactory`**; providers are **`OpenAICompatible` only** (including Ollama via `/v1`). No managed local inference.
- **`ProviderResolver`** maps model → provider from `Arcanum:Providers` (no hard-coded model names).
- Agentic layers: MCP tool loops, semantic spell routing, read-time context compression, Wards, Sanctum.
- **Structured output / pricing / budgets / prompt-cache metrics / guardrails** — see [DESIGN.md §22](Arcanum.DESIGN.md#22-structured-output-cost-tracking-and-prompt-caching) and [§8.27](Arcanum.DESIGN.md#827-content-guardrails-pii--toxicity--topics).

### The Proving Grounds

Ephemeral Trials via `POST /api/proving-grounds/trials/run` (regex / jsonSchema / semantic Inquisitors). Desktop UI: [TheForge.README.md](TheForge.README.md#the-proving-grounds). See [DESIGN.md §20](Arcanum.DESIGN.md#20-the-proving-grounds--trials-and-inquisitors).

### 5. Local-first security posture

Single-user, loopback-by-default, secret-minimizing. See [DESIGN.md §11](Arcanum.DESIGN.md#11-local-api-security).

- Kestrel binds **loopback only** unless explicitly opened; a **32-byte master API key** guards every `/api` and `/v1` route; the **Grimoire** is encrypted at rest (SQLCipher passphrase derived via PBKDF2-HMAC-SHA256 with a unique 16-byte salt stored in `{grimoire.db}.kdf`).
- Sensitive files (`arcanum.json`, Grimoire `.db`, `cli-session.txt`, logs) are created **owner-only** (`chmod 600/700` on Unix; owner ACL on Windows). Startup warns if group/other can read them.
- `Arcanum:Host:ListenAny` requires **first-run acknowledgement** in interactive `serve` (or `ARCANUM_LISTEN_ANY_ACK=1` / `ARCANUM_HOST_ANY` for automation) and emits a **security banner** when binding all interfaces over **HTTPS only** (plaintext any-IP HTTP is refused; `Host:Https` + cert required).
- Path containment + symlink revalidation for file tools; `execute_command` uses `ArgumentList` (no shell) with child-env scrubbing; workspace MCP requires trust. **Tool-child FS jail** (macOS Seatbelt active; Linux inactive fail-closed; Windows Job Objects only / Degraded) — filesystem-only — unless `AllowUnsandboxedToolChildren`. SSRF guard + DNS-rebind pinning on untrusted egress; sanitized public error envelopes. Details: [DESIGN §11](Arcanum.DESIGN.md#11-local-api-security).

### 6. Strict Content Security Policy on every web surface

**First-party browser UI must externalize scripts and styles** (JS in `.js` files, CSS in `.css` files — no inline first-party code). The opt-in **Scalar** UI (`Arcanum:Host:EnableScalarUi`) is a third-party exception served under the same-origin CSP documented in [DESIGN.md §11.5](Arcanum.DESIGN.md#115-openapi-and-scalar).

### 7. C# house style

- **One blank line after each line of C# code** (visual breathing room) — applied throughout the codebase. Within reason. Curly braces do not require blank lines around them. Neither do control statements like if and loops, etc. Also, long-running Linq statements do not require blank lines either.
- File-scoped namespaces; positional records for DTOs/contracts; **no `[JsonPropertyName]`** on `/api` wire types (casing comes from `[JsonSourceGenerationOptions]`); OpenAI `/v1` and MCP JSON-RPC types are explicit exceptions (§8.2); primary constructors for DI; `IDisposable` where a service owns a `SemaphoreSlim`/`ServiceProvider`. See [DESIGN.md §12](Arcanum.DESIGN.md#12-c-language-and-coding-conventions).

> **Note on org-wide rules:** Corp-wide standards scoped to `Corp.Solution.*` solutions (Dapper + SQL Server stored procedures, the `Corp.Lib.*` NuGet stack, Refit "Service Libraries") **do not apply to Arcanum** — it is local-first over its own EF Core + SQLCipher Grimoire and ships as one Native AOT binary. The always-on house rules (blank lines, strict CSP, docs-in-same-change-set) still hold.

### 8. Thematic naming metaphor (D&D)

Arcanum uses Dungeons & Dragons and/or fantasy metaphors for domain concepts. New features **must** follow it if possible. Current exceptions include "prompt" and "workspace". See [Naming metaphor](#naming-metaphor).

### 9. Docs travel with code

Any change to architecture, contracts, configuration, persistence, MCP surfaces, or CLI **updates `docs/Arcanum.DESIGN.md` in the same change set**, and operator-visible behavior changes update this `README.md` too. Do not close work with only code changes. See [DESIGN.md §18](Arcanum.DESIGN.md#18-document-maintenance).

---

## Architecture at a glance

**One binary, hybrid process model.** A ConsoleAppFramework verb selects the role: `serve` (long-running Kestrel host) vs. short-lived commands. See [DESIGN.md §5](Arcanum.DESIGN.md#5-hybrid-hosting-model).

**Dependency chain:** `Cli → Api → Infrastructure → Core` (`Cli` also references `Core`/`Infrastructure` directly for lightweight DI). Strict project boundaries are a deliberate goal.

| Project | Role | Owns | AOT |
|---------|------|------|-----|
| **`Core`** | Domain primitives, contracts, configuration | `Result`/`Result<T>`, `Error`, `ApiResponse<T>`, `ArcanumSettings`, `IArcanumIntelligenceProvider`, `PingRequest`, `IGrimoireRepository`, `IEyeOfTheWorld`, events, source-gen contexts (`GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext`) | `IsAotCompatible` |
| **`Infrastructure`** | OS-adjacent services | Serilog, Data Protection, encrypted Grimoire (EF Core 10 + SQLCipher, compiled model), workspace scanning, Eye of the World, the **MCP client layer** (subprocess + in-process transports, `ArcanumInternalToolServer`), Comm Link | `IsTrimmable` + `PublishAot` (analysis signal) |
| **`Api`** | HTTP surface composition (class library, **not** executable) | `MapArcanumEndpoints`, `ApiBootstrapper`, `WizardIntelligenceProvider`, `ToolExecutionPipeline`, `IChatClientFactory`, `SemanticRouter`, built-in `AIFunction` tools, `ApiKeyEndpointFilter`, `ArcanumJsonContext`, `/v1` OpenAI endpoints | `IsAotCompatible` + `EnableRequestDelegateGenerator` |
| **`Cli`** | Single shipping executable | Spectre commands, `ArcanumApiClient`, theming, AOT-safe Markdown rendering (`MarkdigSpectreRenderer`) | `PublishAot` (the native image) |
| **`Api.DevHost`** | Debug-only F5 host (not shipped) | Mirrors `serve` wiring without Spectre | `PublishAot` + `IsAotCompatible` (analysis signal; not shipped) |
| **`tests/RetroDownfall.Arcanum.Tests`** | xUnit test suite (not shipped) | MCP, security, config, workspace policy, SQLCipher Grimoire, and API-host integration tests | — |
| **`tests/RetroDownfall.Compendium.Tests`** (assembly `RetroDownfall.Compendium.Ux.Tests`) | Compendium smoke tests (not shipped) | Round-trip read/write of `arcanum.json` with DataProtection key interop | — |
| **`Compendium.Ux`** | Desktop configuration editor (Avalonia) | Visual editor for `arcanum.json`; 14 polished section views plus a grouped descriptor-driven generic editor; reuses Core models; VS Fluent light/dark theming; `dp:v1:` secret interop | — |

**Key entry points to know:** `ApiBootstrapper.AddArcanumApiServices` / `MapArcanumEndpoints` (wire everything), `AddArcanumInfrastructure` (Infrastructure DI), `WizardIntelligenceProvider.StreamPromptAsync` (the inference loop), `Cli/Program.cs` (command registration).

### Repository map

```
src/
  RetroDownfall.Arcanum.Core/            # domain, contracts, config, source-gen JSON contexts
    ProvingGrounds/                      # Trial / Inquisitor models and IProvingGroundsArbiter
  RetroDownfall.Arcanum.Infrastructure/  # Grimoire, MCP, perception, Comm Link, Serilog
    Generated/                           # EF Core compiled model (commit regenerations)
    Data/Migrations/                     # EF Core migrations
    Data/SqlMigrations/                  # SQL scripts run at startup
  RetroDownfall.Arcanum.Api/             # endpoints, intelligence hub, /v1, security filter
    ProvingGrounds/                      # trial/inquisitor endpoint wiring
  RetroDownfall.Arcanum.Cli/             # the `arcanum` executable (Spectre commands)
  RetroDownfall.Compendium.Ux/            # desktop `arcanum.json` editor (Avalonia)
  RetroDownfall.Arcanum.Api.DevHost/     # debug-only host
tests/
  RetroDownfall.Arcanum.Tests/           # xUnit tests (MCP, security, config, workspace policy, SQLCipher Grimoire)
  RetroDownfall.Compendium.Tests/        # Compendium round-trip smoke tests (assembly: RetroDownfall.Compendium.Ux.Tests)
docs/                                    # all project documentation lives here
  README.md                              # this agent orientation document
  DESIGN.md                              # authoritative deep reference
  chat-loop.md → Arcanum.CHAT-LOOP.md  # chat loop workflow (mermaid + walkthrough)
  tests.README.md                        # test suite conventions and coverage gates
  CODEX.template.md                      # CODEX scaffold template
  DESIGN-KDF-UPGRADE.md                  # Grimoire key-derivation upgrade notes
scripts/coverage.sh                      # run tests, generate Cobertura + HTML coverage; pass --threshold to enforce gates
scripts/coverage_threshold.py            # tiered coverage threshold enforcement
scripts/coverage_threshold_test.py       # coverage threshold script tests
scripts/align-csharp-blanklines.sh       # C# blank-line formatter entrypoint
scripts/align_csharp_blanklines.py       # C# blank-line formatter logic
scripts/verify-aot-il-warnings.sh        # AOT IL-warning gate
scripts/packaging/macos/                 # signed macOS arm64 release packaging (see RELEASE-MACOS.md)
scripts/packaging/linux/                 # unsigned Linux private-beta tarballs (CLI AOT + Forge/Compendium)
scripts/packaging/windows/               # unsigned Windows private-beta zips (CLI AOT + Forge/Compendium)
Directory.Build.props                    # shared MSBuild props + CVE pin (Microsoft.Bcl.Memory)
```

### Patterns to follow when writing code

These are the recurring shapes. Matching them is what makes a change "fit."

- **Wire envelope.** JSON under `/api` returns `ApiResponse<T>` (`Data`, `IsSuccess`, `Error`, `TraceId`). Map from domain with `ApiResponse<T>.FromResult`. Exceptions: streaming (NDJSON), SSE event buses, and OpenAI `/v1` (raw OpenAI shape). See [DESIGN.md §8.1](Arcanum.DESIGN.md#81-wire-contract-the-apiresponset-envelope).
- **Result flow.** Domain ops return `Result` / `Result<T>` and rely on implicit conversions; the endpoint is the single place that turns a `Result` into an envelope + status code.
- **New endpoint checklist:** add to `MapArcanumEndpoints` → return `ApiResponse<T>` (or documented streaming shape) → register every new payload type on `ArcanumJsonContext` → `.WithName(...)` for OpenAPI → use explicit `JsonTypeInfo` on failable `Results.Json` → update DESIGN.md §4.3 + this README's API map.
- **New CLI verb:** add a public method (XML doc `<summary>`/`<param>` comments drive `--help` text and aliases) to a grouped command class under `Cli/Commands`, registered via `app.Add<T>("path")` in `CliApplicationFactory.RunAsync`; prefer `AddArcanumEyeOfTheWorld()` over full infrastructure for lightweight verbs.
- **New inference provider:** add an `AiProviderKind` and extend `IChatClientFactory`; keep the `WizardIntelligenceProvider` contract intact.
- **New MCP tool:** implement on `ArcanumInternalToolServer` with a hand-authored JSON schema via `McpJsonSerializerContext`; honor workspace path containment and `ToolOutputCapBytes`; decide whether it's a **Forbidden Art** (ward-gated).
- **Treat all wire types as versioned contracts.** Casing is fixed at the context level; don't add `[JsonPropertyName]` except on OpenAI `/v1` and MCP JSON-RPC types (see [DESIGN.md §8.2](Arcanum.DESIGN.md#82-arcanumjsoncontext--source-generated-public)).

---

## Naming metaphor

Arcanum maps domain concepts onto a D&D fantasy metaphor. Universal terms with no clean fantasy equivalent (Prompt, Goal, Plan, Session, Entry, **Workspaces**) stay as-is. Prefer terms **well-known in pop culture**.

| Concept | Name | API / surface |
|---------|------|---------------|
| Persistent workspace | **Campaign** | `/api/campaigns` |
| Skill / capability (versioned markdown) | **Spell** | `/api/spells` (`SPELL.md` + optional `SPELL.json`; legacy `SKILL.json` still read when present) |
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
| Operator key-value memory | **Lore** (legacy) | `/api/lore` |
| Agent-directed entity memory | **The Lexicon** | `scribe_lexicon` / `delete_lexicon` MCP tools; see [DESIGN.md §10.6](Arcanum.DESIGN.md#106-the-lexicon--agent-directed-entity-memory) |
| Operator alert channel | **Comm Link** | `/api/commlink/send` |
| Inference orchestrator | **Wizard** | **`WizardIntelligenceProvider`** (implements **`IArcanumIntelligenceProvider`**) |
| Scratchpad / instructions | **Codex** | `CODEX.md`, `/api/codex` |
| Multi-turn chat thread | **Session** (rows = **Entry**) | `/api/sessions` |
| Spell/prompt/plan validation | **The Proving Grounds** (Trials, Inquisitors) | `POST /api/proving-grounds/trials/run` |
| Embedding & vector substrate | **The Weave** | `Arcanum:Embeddings:*`; see [DESIGN.md §21](Arcanum.DESIGN.md#21-the-weave-divination-and-saga-rag) |
| Semantic search over The Weave | **Divination** | `IDivinationService`; `POST /api/sessions/divine`, `POST /api/workspaces/{id}/files/divine`, `POST /api/saga/divine` (§21) |
| Vector representation of text | **Imprint** | `IWeaveService.EmbedAsync`/`EmbedBatchAsync` ("imprints" text into The Weave; §21) |
| Long-term associative memory | **Saga** | `/api/saga/*`, `read_saga`, `arcanum saga` (§21.8) |
| Recursive Spell dependency injection | **Arcane Resonance** | `SpellDependencyResolver`; `Arcanum:Spells:MaxResonantDependencies`/`MaxResonantBytes` (Arcanum.DESIGN.md §10.2.2) |
| Pre-flight active-Spell selection | **Spell Routing** | `SemanticRouter` (LLM-based) + `SemanticSpellRouter` (Phase 5 embedding pre-filter); `Arcanum:Embeddings:SemanticSpellRoutingEnabled` (Arcanum.DESIGN.md §10.2.2, §21.9) |

**Rejected:** Dispel, Glyph, Invocation (too obscure). The placeholder **Bureau** was retired in favor of **The Conclave** (the multi-agent coordination network; see above).

**Naming rules:** thematic API routes (`/api/spells`); error codes `{Noun}.{Verb}` (`Ward.NotFound`, `Campaign.DuplicateName`) — cross-layer wire codes are centralized as `public const string` in `Core/Primitives/ErrorCodes.cs` (grouped by Validation / Hub / NotFound / etc.); HTTP status mapping for `Result.Error.Code` is centralized in `Api/TheForge/ArcanumErrorMapper.cs`; config paths `Arcanum:{Noun}:{Setting}`. Propose any new concept name to the DM before implementing. Full rationale in this section's source and DESIGN.md §2.1.

---

## API surface map

Default base `http://localhost:5001`. **All `/api` and `/v1` routes require the API key** (`X-Arcanum-Key` or `Authorization: Bearer`). Grouped overview — exhaustive inventory: [DESIGN §4.3](Arcanum.DESIGN.md#43-retrodownfallarcanumapi-class-library-not-executable).

| Area | Routes | Contract / purpose |
|------|--------|-------------------|
| Metrics | `GET /metrics` | Prometheus text; API key on by default (forced on ListenAny). [§8.22](Arcanum.DESIGN.md#822-metrics-endpoint-get-metrics) |
| Health & meta | `/api/health`, `/meta`, `/grimoire/stats`, `/budget` | Readiness + spend snapshot; 503 mainly when Grimoire Unhealthy |
| Config | `/api/config`, `/config/validate` | GET redacts secrets; PUT preserves `"***"` placeholders |
| Models / providers | `GET /api/models`, `/providers`, `/providers/test` | Listings + connectivity probe (no persist) |
| Inference (native) | `/api/intelligence/ping(-stream)`, `/human-response`, `/arsenal`, `/mana` | Buffered / NDJSON `IntelligenceEvent` |
| Inference (OpenAI) | `POST /v1/chat/completions`, `GET /v1/models`, `POST /v1/embeddings` | OpenAI JSON/SSE; Scrying gates images; client tools opt-in |
| OpenAI stubs | `/v1/moderations`, `/images/*`, `/audio/*` | Moderations toggle; images/audio always 501 |
| Files / Batches | `/v1/files*`, `/v1/batches*` | Upload + async JSONL chat batches |
| Sessions | `/api/sessions/*` (+ entries/stream/attachments/divine/fork/pin/compact) | Grimoire threads; memory-mgmt gated; RAG divine off by default |
| Lore / Saga | `/api/lore/*`, `/api/saga/*` | Legacy KV lore; Saga auto-memory (divine gated) |
| Spells / Prompts / Campaigns | `/api/spells/*`, `/prompts/*`, `/campaigns/*`, `/codex` | Forge registry + execute/stream/versions |
| Apprentices / A2A | `/api/apprentices/*`, `/conclave/a2a/*` | Goal agents + optional A2A (off by default) |
| Wards / Sanctum | `/api/wards/*`, `/campaigns/{id}/sanctum*` | Forbidden Arts + sandbox / FS-jail |
| MCP | `/api/mcp*`, `/mcp/tools/invoke` | Lifecycle + diagnostic external invoke |
| Workspaces | `/api/workspaces/*` | File browser/write gate + Weave index/divine |
| Unseen Servant | `/api/unseen-servant/*` (+ deprecated `/daemon/*`) | Interval control; watermarks persist; `lastResult` process-local |
| Events / Comm / Perception | `/api/events/*`, `/commlink/send`, `/perception/look` | SSE; webhook; Eye of the World |
| Trials / Logs / Audit | `/proving-grounds/trials/run`, `/logs`, `/audit`, `/guardrails/audit` | Ephemeral trials; ring buffer; JSONL audits |
| Tools / Docs | `POST /api/tools/invoke`, `/openapi/v1.json`, `/scalar` | Built-in invoke; OpenAPI; Scalar opt-in |

**Wire shapes:** `ApiResponse<T>` for `/api` JSON; NDJSON for streams; SSE for events/session/Chronicle; OpenAI shapes for `/v1`. Compression + Idempotency-Key: [§8.25](Arcanum.DESIGN.md#825-http-response-compression) / [§11.17](Arcanum.DESIGN.md#1117-idempotency-key-request-replay).


## Inference engine details

Summaries only — full contracts live in DESIGN.

- **Providers:** `Arcanum:Providers[]` with `type: "OpenAICompatible"` (Ollama via `/v1`). Obsolete managed-local / `Ollama` / `Arcanum:Cache` keys are hard-rejected by `ConfigurationValidator`.
- **Agentic layers:** spell routing (+ optional embedding pre-filter), Arcane Resonance, Artifact Attunement, MCP tool loops, read-time compression, Wards, Sanctum. See [DESIGN §10](Arcanum.DESIGN.md#10-intelligence-pipeline) and [CHAT-LOOP](Arcanum.CHAT-LOOP.md).
- **Scrying / attachments:** [§10.2.4](Arcanum.DESIGN.md#1024-scrying--the-visionmultimodality-capability-gate) / [§10.2.5](Arcanum.DESIGN.md#1025-session-attachments-disk--grimoire-pointers).
- **A2A:** [§5.7.1](Arcanum.DESIGN.md#571-a2a-and-the-conclave) (disabled by default).
- **RAG (Weave / Divination / Saga):** [§21](Arcanum.DESIGN.md#21-the-weave-divination-and-saga-rag) — all phases gated by `Arcanum:Embeddings:*`, off by default.
- **Lexicon:** agent memory via `scribe_lexicon` / `delete_lexicon`; gated by `EnableLexiconSystem`. [§10.6](Arcanum.DESIGN.md#106-the-lexicon--agent-directed-entity-memory).

---

## Configuration

Settings bind under the `Arcanum` object in **`arcanum.json`** (`~/.config/arcanum/` on macOS/Linux, `%USERPROFILE%\.config\arcanum\` on Windows). Override with **`ARCANUM_`** + `__` nesting. Clamps live in `ArcanumSettingClamps`; serve validates before listening. Obsolete removed keys are hard-rejected.

> **Compendium** edits the same file visually — [`Compendium.README.md`](Compendium.README.md). Descriptors mirror [DESIGN §3.4](Arcanum.DESIGN.md#34-configuration-reference-arcanumsettings).

**Full key reference (types, defaults, clamps):** [DESIGN.md §3.4](Arcanum.DESIGN.md#34-configuration-reference-arcanumsettings). Sections at a glance:

| Section | Controls |
|---------|----------|
| `Host` | Port, HTTPS, CORS, body cap, rate limit, Scalar, ListenAny |
| `Security` | API-key header/cache; Idempotency-Key TTL/size |
| `DefaultModel` / `FastModel` / `Providers` | Multi-provider hub |
| `Intelligence` | Timeouts, tool rounds, Lexicon, compression, injection bounds |
| `Mcp` | Client timeouts, tools/list bounds, bootstrap |
| `Ward` / Sanctum / `AllowUnsandboxedToolChildren` | Forbidden Arts, sandbox, FS-jail escape hatch |
| `Apprentices` | Concurrency, retries, Simulacra |
| `Grimoire` / `Sessions` | Load/query caps, memory-management gate, fork depth |
| `CommLink` | Webhook URL/schemes/timeout |
| `Perception` / `Spells` / `Campaigns` / `Prompts` | Path allowlists, spell/prompt caps |
| `Daemon` / `EventBus` / `Logs` / `Workspaces` / `Codex` / `Cli` | Unseen Servant, SSE caps, file write gates, CLI |
| `Conclave` / `ProvingGrounds` / `Resilience` / `Metrics` | Delegation, Trials, provider fallback, Prometheus |
| `Scrying` / `Attachments` | Vision gates; session attachment persistence |
| `Embeddings` | Weave/Divination/Saga/semantic routing (all off by default) |
| `Guardrails` / `Pricing` / `Budget` / `StructuredOutput` | Content policy, cost, daily spend, JSON Schema |

**Minimal example** (local Ollama via its OpenAI-compatible endpoint + OpenAI-compatible DeepSeek; keep API keys in env vars):

```json
{
  "Arcanum": {
    "Host": {
      "Port": 5001,
      "Https": {
        "Enabled": false,
        "Port": 5443,
        "CertificatePath": null,
        "PrivateKeyPath": null,
        "CertificatePassword": null
      }
    },
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

Ollama must use its `/v1` endpoint. `models` entries may be bare strings or `{ "name", "supportsVision" }` objects.

```bash
export ARCANUM_Arcanum__Providers__1__ApiKey='your-key-here'
```

`DefaultModel`/`FastModel` must match a `models` entry on some provider — matching is a case-insensitive **exact** match, with no bare-name or tag-stripping fallback. OpenAI-compatible `endpoint`s usually include `/v1`. **MCP servers** are wired via `~/.config/arcanum/mcp.json` (`mcpServers` schema) over **stdio** (`command`/`args`, with an optional `inheritEnv` allowlist for `npx`-style launches) or **Streamable HTTP** (`type: "http"` or a bare `url`, SSRF-guarded and `https`-by-default); workspace-local `mcp.json` is merged only after `POST /api/mcp/trust-workspace`. See [DESIGN.md §3.4](Arcanum.DESIGN.md#34-configuration-reference-arcanumsettings) and the MCP host limits there.

### Optional HTTPS

HTTP remains the default on **loopback**. `Host:Https:Enabled` adds a TLS listener; with `ListenAny` / `ARCANUM_HOST_ANY`, HTTPS is **required and exclusive**. Cert password is `dp:v1:`-encrypted and redacted on `GET /api/config`. Clients do not bypass TLS validation. PFX vs PEM shapes and Compendium self-signed generation: [DESIGN §3.4 Host](Arcanum.DESIGN.md#34-configuration-reference-arcanumsettings) / [Compendium.README](Compendium.README.md#host-https).

---

## CLI quick reference

All commands run as `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- <cmd>` in development, or `arcanum <cmd>` after an AOT publish.

**Default command:** bare interactive `arcanum` (no arguments) opens the **Command Center** (Terminal.Gui fixed-viewport TUI). Bare non-interactive `arcanum`, or `ARCANUM_NO_COMMAND_CENTER=1`, prints usage and exits **0**. Explicit commands (`serve`, `ask`, `chat`, `--help`, …) stay frameless Spectre/CAF as before.

**Command Center:** interactive Terminal.Gui workbench (sessions sidebar, transcript, composer, HITL/Ward hard modals). Bare interactive `arcanum` opens it; non-interactive / `ARCANUM_NO_COMMAND_CENTER=1` → usage. Slash allowlist and attach flows: [DESIGN §4.4](Arcanum.DESIGN.md#44-retrodownfallarcanumcli-console-executable).



**Operator communication tools (canonical catalog):** `ask_human` (attended streaming only — wait for operator), `petition_dungeon_master` (async Apprentice escalation; may send Critical Comm Link), `send_commlink_alert` (one-way external notification; no replies). Legacy `use_commlink` is a tools/call alias only. Comm Link webhooks receive generic JSON (`title`/`body`/`severity`/`source`/`timestampUtc`) — Telegram/WhatsApp need a relay.

**Auto-start serve:** interactive Command Center / `chat` / `ask` spawn `arcanum serve` on definite no-listener (refused), wait ~20s for authenticated health. Disabled via `ARCANUM_NO_AUTO_SERVE=1`. Never auto-acks ListenAny. Bootstrap log: `~/.config/arcanum/logs/auto-serve-bootstrap.log`. Key via `arcanum key show`.



| Command | Purpose |
|---------|---------|
| *(bare)* | Open Command Center (interactive TTY). Non-interactive / `ARCANUM_NO_COMMAND_CENTER=1` → usage, exit 0. |
| `serve` | Run the host (default loopback :5001). ListenAny is HTTPS-only + first-run ack. Auto-launched suppresses key print. Details: [DESIGN §5](Arcanum.DESIGN.md#5-hybrid-hosting-model).
| `ask <prompt>` | Single-turn inference (NDJSON stream). Flags: `-n` / `--new` (new session), `-m <model>`, `-c` / `--campaign <id>`, `--unattended`, `--image <path>` (repeatable — attach a Scrying focus; requires a vision-capable model), plus inference flags (below). Use `--` to pass a prompt that starts with a flag. Ctrl+C cancels the in-flight turn (exit 130). Running `ask` before a key is stored exits **1** with a friendly "run `arcanum serve` once" message (no crash). Interactive sessions auto-start `serve` when the API is unreachable (see above). |
| `chat` | Interactive multi-turn REPL (Figlet banner, Markdig rendering, mana bar, live multi-panel layout on wide color terminals). Flags: `-n` / `--new`, `-m`, `-c` / `--campaign <id>` (shown in the startup banner when set), `--no-tools`, `--unattended`, plus inference flags. **Slash commands:** `/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model [name]`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/memory`, `/summary`, `/mana`, `/attach`. Stage text files inline with `@path`; an `@path` whose extension is an image type (`.png`/`.jpg`/`.jpeg`/`.gif`/`.webp`/`.bmp`) stages a **Scrying focus** instead (prints `Scrying focus: <name> (<size>)`; requires a vision-capable model). The mana bar shows a persistent **(Memory Compressed)** suffix after read-time compression until `/new`. Auto-starts `serve` when needed (see above). Narrow / redirected / `NO_COLOR` sessions keep the simple streaming path. |
| `look` | Print the Eye of the World workspace snapshot (no HTTP). |
| `doctor` | Environment diagnostics (System / Paths / Configuration / MCP / Tokenizer panels) + API health probe. Timeout via `Arcanum:Cli:DoctorHealthTimeoutSeconds` (default 2s); an unreachable API is a non-fatal warning (still exits 0). Use `--fix-permissions` to apply owner-only permissions to the Grimoire database, `arcanum.json`, and secret store. Use `--json` to emit a structured `DoctorReport` to stdout for programmatic consumption (exit code 0 if healthy, 1 otherwise). |
| `key show` | Print the stored master API key from the OS credential store (with `security.dat` fallback) to **stderr**. CLI-only; no HTTP. |
| `key set` | Store a master API key into the OS credential store (mirrors to `security.dat`). Argument or stdin / interactive secret prompt. |
| `lore list\|get\|set\|delete` | Operator key-value memory via `/api/lore` (needs `serve`). Args: `get <KEY>`, `set <KEY> <VALUE>`, `delete <KEY>`. |
| `daemon install\|uninstall\|status` | OS background-service lifecycle. |
| `daemon jobs\|initiative\|alert` | Unseen Servant inspection + Comm Link smoke test (needs `serve`). `daemon jobs` shows **Last run** / interval from persisted watermarks (survive restart), **Next due** reconstructed from watermark + interval, and **Last result** (process-local diagnostic text). `daemon initiative <JOB_NAME> <MINUTES>` sets adaptive interval. `daemon alert <MESSAGE>` options: `--title`/`-t` (default `"Arcanum alert"`), `--severity`/`-s` (`Info`\|`Warning`\|`Critical`, default `Warning`), `--source`. |
| `campaign list\|get\|create\|update\|delete\|export\|import\|codex\|spells\|prompts\|sessions` | The Forge campaign registry via `/api/campaigns` (needs `serve`). `create --name <n> --path <p> [--type <t>]`; `export`/`import <id>` round-trip JSON (stdout/`--output` or `--file`); `codex get\|put\|delete <id>` manages `CODEX.md`; `spells\|prompts\|sessions <id>` list campaign-scoped resources (campaign spells shadow built-ins of the same name). |
| `session divine <QUERY>` | RAG Phase 2 — semantic search over Grimoire entries via `POST /api/sessions/divine` (needs `serve`; disabled by default — requires `Arcanum:Embeddings:Enabled` + `SessionSearchEnabled`). Options: `--limit <n>`, `--campaign <id>`, `--status <status>`. See [DESIGN.md §21.6](Arcanum.DESIGN.md#216-phase-2--session-divination). |
| `saga list\|divine\|delete\|stats` | RAG Phase 4 — Saga long-term associative memory via `/api/saga/*` (needs `serve`). `list` (options `--query`, `--session`, `--limit`, `--offset`) and `stats` are always available; `divine <QUERY>` (option `--limit`) requires `Arcanum:Embeddings:Enabled` + `SagaEnabled`; `delete <ID>` removes a single memory. See [DESIGN.md §21.8](Arcanum.DESIGN.md#218-phase-4--saga-long-term-associative-memory). |
| `spell list\|get\|create\|update\|delete\|search\|validate\|execute\|versions\|export\|import\|cast\|clone` | The Forge spell CRUD + execution via `/api/spells` (needs `serve`). `create`/`update` require `--workspace`; `--body`/`--goal`/`--template`/`--plan`/`--inquisitor` accept inline text or `@filename`; `execute` prints the response text plus a tool-call summary (stderr) when tools ran (`--version` takes a **string label**); `cast <name>` is a dry-run system-prompt preview — no inference tokens consumed; `clone <name> --new-name <n>` clones a spell into the workspace. |
| `spell version create\|update\|activate` | Named spell version files (`SPELL.v{label}.md`) via `/api/spells/{name}/versions` (needs `serve`). `create`/`update <name> --version <label> --body <text\|@file>`; `activate <name> --version <label>` swaps the version into `SPELL.md`, printing where the previous content was preserved. |
| `prompt list\|get\|versions\|create\|update\|delete\|render\|test\|execute\|export\|import\|clone` | The Forge prompt CRUD + rendering via `/api/prompts` (needs `serve`). `render`/`execute` accept repeatable `--param key=value`; `test` assembles the system prompt at no LLM cost; `clone <id> --new-name <n> --new-version <v> [--campaign <id>]` copies to a new name/version. |
| `ward list\|get\|resolve` | Ward approval gates via `/api/wards` (needs `serve`). `resolve <id>` requires exactly one of `--allow`/`--deny` plus optional `--reason`. |
| `trial run` | The Proving Grounds via `POST /api/proving-grounds/trials/run` (needs `serve`). `--target spell\|prompt\|apprenticeGoal` + `--target-value`, repeatable `--inquisitor` (JSON or `@file`) and `--var key=value`; exits `1` when the Trial fails. |
| `apprentice list\|get\|create\|delete\|start\|pause\|resume\|cancel\|reweave\|intervene\|cast\|chronicle` | The Forge Apprentice orchestration via `/api/apprentices` (needs `serve`). `create --goal <text\|@file>`; `reweave --plan <json\|@file>`; `cast` reports 409 `Apprentice.ConclaveDisabled` when `Arcanum:Conclave:Enabled` is off; `chronicle <id>` streams live SSE events (Ctrl+C exits 130). |
| `model list` | List configured models across all providers via `GET /api/models` (needs `serve`); endpoint redacted. |
| `provider list` | List configured providers via `GET /api/providers` (needs `serve`); `apiKey`/`endpoint` redacted. |
| `browse <url>` | Fetch a web page via the built-in `browse_web` tool (requires `Arcanum:WebBrowsing:Enabled`; needs `serve`). Renders title, content preview, and link list. |

**Inference flags** (`ask`/`chat`): `--temperature`, `--top-p`, `--max-tokens`, `--seed`, `--stop`, `--response-format`, penalties, `-c`/`--campaign`. Scrying: `ask --image` / chat `@path`. Full slash-command suite and error formatting: [DESIGN §4.4](Arcanum.DESIGN.md#44-retrodownfallarcanumcli-console-executable).
