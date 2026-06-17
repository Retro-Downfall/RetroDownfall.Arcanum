# Retro Downfall Arcanum

> **Agent orientation document.** This README is written first and foremost for an **AI coding agent** (e.g. Cursor) that needs to understand Arcanum well enough to write effective prompts and code changes. It summarizes *what Arcanum is*, *the standards every change must uphold*, *how the system is shaped*, and *the patterns to follow*. For exhaustive, authoritative detail (every config key, clamp, and endpoint contract), defer to **[`docs/DESIGN.md`](docs/DESIGN.md)** — this file links into it throughout.

**Arcanum** is a **.NET 10, single-binary, Native AOT, local-first AI assistant and inference hub.** It ships as one self-contained native executable (`arcanum`) that runs two ways: a long-running **HTTP host** exposing an API-first surface (`arcanum serve`), and a set of **terminal commands** (`ask`, `chat`, `look`, `lore`, `daemon`, `llama`) that are thin clients over that same API. It speaks the **OpenAI API** for drop-in client compatibility, routes inference across a **multi-provider native engine** (Ollama, any OpenAI-compatible HTTP API, and local **GGUF** models via `llama.cpp`'s `llama-server`), and persists everything in an **encrypted local store** (SQLCipher).

- **Stack:** .NET 10 · ASP.NET Core Minimal API · Native AOT · `Microsoft.Extensions.AI` · EF Core 10 + SQLCipher · Spectre.Console.Cli
- **Version:** `0.1.0-beta` (see [`Directory.Build.props`](Directory.Build.props))
- **Audience for the code:** senior C#/.NET engineers and coding agents extending an AOT-constrained, API-first system.

---

## The standards (read this first)

These are **non-negotiable** and define what "correct" means in this repo. Every prompt you write and every change you make must hold the line on all of them. They are the reason many "obvious" approaches (reflection-based JSON, `AIFunctionFactory.Create`, anonymous DTOs, inline `<script>`) are **wrong here**.

### 1. Native AOT compatibility (hard constraint)

The shipping artifact is a **Native AOT** binary with **zero runtime prerequisite**. No JIT, minimal reflection. This dictates almost every serialization and binding decision. See [DESIGN.md §9](docs/DESIGN.md#9-native-aot-and-trimming).

- **Source-generated JSON only.** Every HTTP payload type must have a `[JsonSerializable]` registration on **`ArcanumJsonContext`** (Api). Other contexts are scoped: `GrimoireJsonContext` (Core, Grimoire blobs), `ConfigurationJsonContext` (Core, `arcanum.json`), `McpJsonSerializerContext` / `McpConfigJsonSerializerContext` (Infrastructure, JSON-RPC + `mcp.json`), `CommLinkInfrastructureJsonContext` (outbound webhooks), `TheForgeJsonContext` (Core, campaign/skill metadata). **Never** use reflection-based `JsonSerializer` overloads, `PostAsJsonAsync` with anonymous types, or `Results.Json` without an explicit `JsonTypeInfo`.
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

Arcanum exposes a maximum-parity **OpenAI Chat Completions** surface so existing OpenAI clients work unchanged. See [DESIGN.md §8.8](docs/DESIGN.md#88-openai-v1-parity-surface).

- **`POST /v1/chat/completions`** (JSON or SSE) and **`GET /v1/models`** (auto-discovery across all configured providers).
- Full request parsing including multimodal `content` parts, `tool`/`assistant` tool-call replay, `stream_options.include_usage`, `response_format`, etc.
- Responses carry `usage`, `system_fingerprint`, and OpenAI-shaped error envelopes. **Auth** accepts `Authorization: Bearer <KEY>` for OpenAI clients (as well as `X-Arcanum-Key`).
- Arcanum runs **its own server-side MCP toolset**, so client-supplied `tools`/`tool_choice` are rejected with `400 unsupported_parameter` (by design — do not "fix" this by forwarding client schemas).

### 4. Top-of-the-line, all-native multi-provider inference engine

Inference flows through one hub behind a single `IChatClient` abstraction. See [DESIGN.md §10](docs/DESIGN.md#10-intelligence-pipeline).

- **`HubIntelligenceProvider`** (Api) implements **`IArcanumIntelligenceProvider`** (Core); **`IChatClientFactory`** builds a per-turn `IChatClient` per provider kind.
- **Providers (`AiProviderKind`):** `Ollama` (OllamaSharp), `OpenAICompatible` (`Microsoft.Extensions.AI.OpenAI` — DeepSeek, Groq, GitHub Models, LM Studio, …), and `LlamaCppServer` (local GGUF via spawned `llama-server`, fully managed lifecycle + GGUF cache).
- **No hard-coded model names.** `ProviderResolver` maps a requested/default model to a provider+model. Everything is configured under `Arcanum:Providers`.
- The engine adds agentic **MCP tool loops**, **semantic spell routing**, **read-time context compression**, **wards** (approval gates), and **Sanctum** (sandboxing) on top of raw inference.

### 5. Local-first security posture

Single-user, loopback-by-default, secret-minimizing. See [DESIGN.md §11](docs/DESIGN.md#11-local-api-security).

- Kestrel binds **loopback only** unless explicitly opened; a **32-byte master API key** guards every `/api` and `/v1` route; the **Grimoire** is encrypted at rest (SQLCipher passphrase HKDF-derived from the key).
- In-process file/dir tools enforce **path containment + symlink resolution**; `execute_command` uses `ArgumentList` (no shell); outbound URLs pass an SSRF guard; errors return **sanitized public envelopes** (detail stays in logs).

### 6. Strict Content Security Policy on every web surface

No inline code, ever. **JavaScript belongs in `.js` files and CSS in `.css` files.** This is why the Scalar UI is opt-in and served under a tight CSP, and why any future browser UI must externalize all scripts/styles.

### 7. C# house style

- **One blank line after each line of C# code** (visual breathing room) — applied throughout the codebase.
- File-scoped namespaces; positional records for DTOs/contracts; **no `[JsonPropertyName]`** on `/api` wire types (casing comes from `[JsonSourceGenerationOptions]`); OpenAI `/v1` and MCP JSON-RPC types are explicit exceptions (§8.2); primary constructors for DI; `IDisposable` where a service owns a `SemaphoreSlim`/`ServiceProvider`. See [DESIGN.md §12](docs/DESIGN.md#12-c-language-and-coding-conventions).

> **Note on org-wide rules:** Corp-wide standards scoped to `Corp.Solution.*` solutions (Dapper + SQL Server stored procedures, the `Corp.Lib.*` NuGet stack, Refit "Service Libraries") **do not apply to Arcanum** — it is local-first over its own EF Core + SQLCipher Grimoire and ships as one Native AOT binary. The always-on house rules (blank lines, strict CSP, docs-in-same-change-set) still hold.

### 8. Thematic naming metaphor (D&D)

Arcanum uses a Dungeons & Dragons metaphor for domain concepts. New features **must** follow it. See [Naming metaphor](#naming-metaphor).

### 9. Docs travel with code

Any change to architecture, contracts, configuration, persistence, MCP surfaces, or CLI **updates `docs/DESIGN.md` in the same change set**, and operator-visible behavior changes update this `README.md` too. Do not close work with only code changes. See [DESIGN.md §18](docs/DESIGN.md#18-document-maintenance).

---

## Architecture at a glance

**One binary, hybrid process model.** A Spectre.Console.Cli verb selects the role: `serve` (long-running Kestrel host) vs. short-lived commands. See [DESIGN.md §5](docs/DESIGN.md#5-hybrid-hosting-model).

**Dependency chain:** `Cli → Api → Infrastructure → Core` (`Cli` also references `Core`/`Infrastructure` directly for lightweight DI). Strict project boundaries are a deliberate goal.

| Project | Role | Owns | AOT |
|---------|------|------|-----|
| **`Core`** | Domain primitives, contracts, configuration | `Result`/`Result<T>`, `Error`, `ApiResponse<T>`, `ArcanumSettings`, `IArcanumIntelligenceProvider`, `PingRequest`, `IGrimoireRepository`, `IEyeOfTheWorld`, events, source-gen contexts (`GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext`) | `IsAotCompatible` |
| **`Infrastructure`** | OS-adjacent services | Serilog, Data Protection, encrypted Grimoire (EF Core 10 + SQLCipher, compiled model), workspace scanning, Eye of the World, the **MCP client layer** (subprocess + in-process transports, `ArcanumInternalToolServer`), Comm Link, GGUF cache + `llama-server` manager | `IsTrimmable` + `PublishAot` (analysis signal) |
| **`Api`** | HTTP surface composition (class library, **not** executable) | `MapArcanumEndpoints`, `ApiBootstrapper`, `HubIntelligenceProvider`, `IChatClientFactory`, `SemanticRouter`, built-in `AIFunction` tools, `ApiKeyEndpointFilter`, `ArcanumJsonContext`, `/v1` OpenAI endpoints | `IsAotCompatible` + `EnableRequestDelegateGenerator` |
| **`Cli`** | Single shipping executable | Spectre commands, `ArcanumApiClient`, theming, AOT-safe Markdown rendering (`MarkdigSpectreRenderer`) | `PublishAot` (the native image) |
| **`Api.DevHost`** | Debug-only F5 host (not shipped) | Mirrors `serve` wiring without Spectre | — |

**Key entry points to know:** `ApiBootstrapper.AddArcanumApiServices` / `MapArcanumEndpoints` (wire everything), `AddArcanumInfrastructure` (Infrastructure DI), `HubIntelligenceProvider.StreamPromptAsync` (the inference loop), `Cli/Program.cs` (command registration).

### Repository map

```
src/
  RetroDownfall.Arcanum.Core/            # domain, contracts, config, source-gen JSON contexts
  RetroDownfall.Arcanum.Infrastructure/  # Grimoire, MCP, perception, llama, Comm Link, Serilog
    Generated/                           # EF Core compiled model (commit regenerations)
    Data/Migrations/                     # EF Core migrations
  RetroDownfall.Arcanum.Api/             # endpoints, intelligence hub, /v1, security filter
  RetroDownfall.Arcanum.Cli/             # the `arcanum` executable (Spectre commands)
  RetroDownfall.Arcanum.Api.DevHost/     # debug-only host
tests/
  RetroDownfall.Arcanum.Tests/           # xUnit tests (MCP, security, config, workspace policy)
docs/DESIGN.md                           # authoritative deep reference
scripts/verify-aot-il-warnings.sh        # AOT IL-warning gate
Directory.Build.props                    # shared MSBuild props + CVE pin (Microsoft.Bcl.Memory)
```

### Patterns to follow when writing code

These are the recurring shapes. Matching them is what makes a change "fit."

- **Wire envelope.** JSON under `/api` returns `ApiResponse<T>` (`Data`, `IsSuccess`, `Error`, `TraceId`). Map from domain with `ApiResponse<T>.FromResult`. Exceptions: streaming (NDJSON), SSE event buses, and OpenAI `/v1` (raw OpenAI shape). See [DESIGN.md §8.1](docs/DESIGN.md#81-wire-contract-the-apiresponset-envelope).
- **Result flow.** Domain ops return `Result` / `Result<T>` and rely on implicit conversions; the endpoint is the single place that turns a `Result` into an envelope + status code.
- **New endpoint checklist:** add to `MapArcanumEndpoints` → return `ApiResponse<T>` (or documented streaming shape) → register every new payload type on `ArcanumJsonContext` → `.WithName(...)` for OpenAPI → use explicit `JsonTypeInfo` on failable `Results.Json` → update DESIGN.md §4.3 + this README's API map.
- **New CLI verb:** add an `AsyncCommand` under `Cli/Commands`, register in `Program.Configure`, add `[DynamicDependency]`; prefer `AddArcanumEyeOfTheWorld()` over full infrastructure for lightweight verbs.
- **New inference provider:** add an `AiProviderKind` and extend `IChatClientFactory`; keep the `HubIntelligenceProvider` contract intact.
- **New MCP tool:** implement on `ArcanumInternalToolServer` with a hand-authored JSON schema via `McpJsonSerializerContext`; honor workspace path containment and `ToolOutputCapBytes`; decide whether it's a **Forbidden Art** (ward-gated).
- **Treat all wire types as versioned contracts.** Casing is fixed at the context level; don't add `[JsonPropertyName]` except on OpenAI `/v1` and MCP JSON-RPC types (see [DESIGN.md §8.2](docs/DESIGN.md#82-arcanumjsoncontext--source-generated-public)).

---

## Naming metaphor

Arcanum maps domain concepts onto a D&D fantasy metaphor. Universal terms with no clean fantasy equivalent (Prompt, Goal, Plan, Session, Entry) stay as-is. Prefer terms **well-known in pop culture**.

| Concept | Name | API / surface |
|---------|------|---------------|
| Persistent workspace | **Campaign** | `/api/campaigns` |
| Skill / capability (versioned markdown) | **Spell** | `/api/spells` (`SPELL.md` + optional `SKILL.json`) |
| Parameterized prompt template | **Prompt** | `/api/prompts` |
| Approval gate for high-risk tools | **Ward** | `/api/wards` (DM resolves allow/deny) |
| Per-campaign execution sandbox | **Sanctum** | `/api/campaigns/{id}/sanctum` |
| High-risk gated tools | **Forbidden Arts** | `Arcanum:Ward:ForbiddenArts` |
| Autonomous sub-agent | **Apprentice** | `/api/apprentices` |
| Agent event stream | **Chronicle** | `/api/apprentices/{id}/chronicle` (SSE) |
| Human operator | **Dungeon Master (DM)** | — |
| Encrypted persistence store | **Grimoire** | (internal: EF Core + SQLCipher) |
| Background job runner | **Unseen Servant** | `/api/daemon/*` |
| Situational directory perception | **Eye of the World** | `/api/perception/look` |
| Operator key-value memory | **Lore** | `/api/lore` |
| Operator alert channel | **Comm Link** | `/api/commlink/send` |
| Scratchpad / instructions | **Codex** | `CODEX.md`, `/api/codex` |
| Multi-turn chat thread | **Session** (rows = **Entry**) | `/api/sessions` |

**Reserved (do not reuse):** **Wizard** (future `HubIntelligenceProvider` rename — spawns Apprentices, wields Spells), **Bureau** (future multi-agent coordination). **Rejected:** Dispel, Glyph, Invocation (too obscure).

**Naming rules:** thematic API routes (`/api/spells`); error codes `{Noun}.{Verb}` (`Ward.NotFound`, `Campaign.DuplicateName`); config paths `Arcanum:{Noun}:{Setting}`. Propose any new concept name to the DM before implementing. Full rationale in this section's source and DESIGN.md §2.1.

---

## API surface map

Default base `http://localhost:5001`. **All `/api` and `/v1` routes require the API key** (`X-Arcanum-Key: <KEY>` or `Authorization: Bearer <KEY>`). This is a grouped overview — the exhaustive per-endpoint table (verbs, status codes, payload DTOs) lives in [DESIGN.md §4.3](docs/DESIGN.md#43-retrodownfallarcanumapi-class-library-not-executable).

| Area | Routes | Notes |
|------|--------|-------|
| Health & meta | `/api/health`, `/api/meta` | `meta` returns version, OS, runtime, paths, feature flags, `llamaCppEnabled`. |
| Configuration | `/api/config` (GET/PUT), `/api/config/validate` | Reads redact secrets to `"***"`; PUT preserves unchanged `"***"` keys. |
| Inference (native) | `/api/intelligence/ping`, `…/ping-stream`, `…/human-response`, `…/arsenal` | `ping` buffered (`PromptResponseDto`); `ping-stream` is **NDJSON** `IntelligenceEvent`. |
| Inference (OpenAI) | `POST /v1/chat/completions`, `GET /v1/models` | OpenAI-shaped JSON/SSE; **not** envelope-wrapped. |
| Sessions (Grimoire) | `/api/sessions/*` (CRUD, `/entries`, `/export`, `/rest`, `/stream`, `/analytics`) | Single source of truth for threads; FTS5 search; SSE live stream. |
| Lore | `/api/lore/*` | Operator key-value memory. |
| Spells | `/api/spells/*` (CRUD, `/search`, `/validate`, `/export`, `/import`, `/execute(-stream)`, `/versions`) | Built-in spells are read-only (`source: builtin`). |
| The Forge — campaigns | `/api/campaigns/*` (+ `/codex`, `/export`, `/import`), `/api/codex` | Registers workspace roots; creates `.arcanum/`. |
| The Forge — prompts | `/api/prompts/*` (`/render`, `/test`, `/execute(-stream)`, versions) | Versioned templates with parameter schemas. |
| The Forge — apprentices | `/api/apprentices/*` (`/start`, `/pause`, `/resume`, `/cancel`, `/chronicle`) | Goal-driven autonomous agents; Chronicle is SSE. |
| Wards & Sanctum | `/api/wards/*`, `/api/campaigns/{id}/sanctum(/breaches)` | Forbidden Arts gating + per-campaign sandbox. |
| MCP | `/api/mcp/*` (`/start`, `/stop`, `/restart`, `/reload`, `/trust-workspace`) | Manage external + in-process MCP servers. |
| LlamaCpp | `/api/llama/models(/pull)`, `/api/llama/servers/*` | GGUF cache + `llama-server` lifecycle; pull is **NDJSON**. |
| Workspaces | `/api/workspaces/*` (+ `/files`, `/files/info`, `/files/contents`) | Registry + read-only file browser. |
| Unseen Servant | `/api/daemon/*` (config/intervals), `/api/daemons/*` + `/api/executions/*` (registry/history) | Two route families — singular = config, plural = registry. |
| Events (SSE) | `/api/events/daemon`, `…/mcp`, `…/logs` | `text/event-stream`; **not** envelope-wrapped. |
| Comm Link | `/api/commlink/send` | Outbound webhook alerts; `502` on webhook failure. |
| Perception | `/api/perception/look` | Eye of the World snapshot (allowlisted roots). |
| Providers | `/api/providers/test` | Read-only connectivity probe; does not persist. |
| Logs | `/api/logs` | Paginated in-memory ring buffer query. |
| Docs | `/api/openapi/v1.json`, `/api/scalar` | OpenAPI always on; Scalar opt-in + strict CSP. |

**Wire contracts:** `ApiResponse<T>` for `/api` JSON; **NDJSON** for `ping-stream`, spell/prompt `execute-stream`, and `llama/models/pull`; **SSE** for `/api/events/*`, `/api/sessions/{id}/stream`, and Chronicle; **OpenAI JSON/SSE** for `/v1/*`. See [DESIGN.md §8](docs/DESIGN.md#8-http-json-and-minimal-api-design-api-project).

### Wire contract changes

Breaking or client-visible HTTP contract fixes (document here when no `CHANGELOG.md` exists):

| Change | Before | After |
|--------|--------|-------|
| `/api` **404** responses | Bare **404** with empty body on some routes | **`ApiResponse<T>`** envelope with `isSuccess: false`, `error`, and `traceId` |
| OpenAI **`model_not_found`** | **400** `invalid_request_error` | **404** `invalid_request_error` with `code: "model_not_found"` |
| OpenAI **`tool_calls` + `finish_reason`** | Clients might retry when `content` empty | **`finish_reason: "stop"`** with observability-only `tool_calls`; see [DESIGN.md §8.8.1](docs/DESIGN.md#881-sdk-client-caveat-tool_calls--finish_reason-stop-option-a) |

---

## Inference engine details

- **Provider hub:** configure one or more entries under `Arcanum:Providers`; each has `name`, `type` (`Ollama` | `OpenAICompatible` | `LlamaCppServer`), `endpoint`, optional `apiKey`, `models[]`, and `contextWindowLimit` (default 8192). `Arcanum:DefaultModel` selects the default; `Arcanum:FastModel` is used for internal background summarization.
- **Local GGUF (`LlamaCppServer`):** Arcanum spawns and health-manages `llama-server` child processes and downloads/caches GGUF files under `~/.config/arcanum/models/`. `endpoint`/`apiKey` are ignored (the hub talks to the spawned local port). Pull models with `arcanum llama pull <url>` while `serve` runs. See [DESIGN.md §8.20](docs/DESIGN.md#820-llamacpp-management-api-apillama).
- **Agentic features layered on inference:** semantic **spell routing** (frontmatter-only preflight → lazy body load), **MCP tool loops** (bounded by `MaxToolInferenceRounds`), **read-time context compression** (swaps old entries for `Session.Summary` near the context limit; never deletes rows), **Wards** (operator approval for Forbidden Arts), **Sanctum** (per-campaign path/network/tool sandbox). Token counting uses `Microsoft.ML.Tokenizers` Tiktoken (`o200k_base`).

---

## Configuration

Settings bind under the `Arcanum` object in **`arcanum.json`**, living in the per-user config dir (created on first run): `~/.config/arcanum/` on macOS/Linux, `%USERPROFILE%\.config\arcanum\` on Windows. Override any key with env vars using the **`ARCANUM_`** prefix and `__` for nesting (use env vars for secrets — e.g. `ARCANUM_Arcanum__Providers__1__ApiKey`). Every numeric setting has a runtime clamp in `ArcanumSettingClamps`.

**The full key reference (types, defaults, clamps) is [DESIGN.md §3.4](docs/DESIGN.md#34-configuration-reference-arcanumsettings).** Sections at a glance:

| Section | Controls |
|---------|----------|
| `Arcanum:Host` | Kestrel port, CORS, body cap, rate limiter, Scalar UI toggle, system fingerprint, default workspace, loopback vs `ListenAny`. |
| `Arcanum:Security` | API key header sizing + cache TTL (on-disk rotation propagation). |
| `Arcanum:DefaultModel` / `FastModel` / `Providers` | Multi-provider hub + model resolution. |
| `Arcanum:Intelligence` | Tool timeouts/caps, agentic round cap, MCP limits, lore/archive gates, context compression, tokenizer encoding, token tracking. |
| `Arcanum:Ward` / Sanctum | Forbidden Arts list, ward timeout, unattended auto-deny; per-campaign Sanctum config. |
| `Arcanum:Apprentices` | Concurrency, step timeout, Chronicle channel capacity. |
| `Arcanum:LlamaCpp` | `llama-server` path, GPU layers, context size, ports, cache cap, SHA-256 verification. |
| `Arcanum:Grimoire` / `Sessions` | Load/query caps, snapshot retention, page sizes, SSE replay caps. |
| `Arcanum:CommLink` | Webhook URL, timeout, scheme allowlist. |
| `Arcanum:Perception` / `Spells` / `Campaigns` | Path allowlists (**empty = deny by default**), campaign caps. `Arcanum:Spells:MaxFileSizeBytes` (default 256 KiB) caps spell/frontmatter reads; clamped against `Arcanum:Workspaces:MaxFileReadSizeBytes`. |
| `Arcanum:Daemon` / `EventBus` / `Logs` / `Workspaces` / `Codex` / `Cli` | Unseen Servant scheduling, SSE channel capacity, log ring buffer, file-read caps, `Arcanum:Codex:MaxSizeBytes` (default 256 KiB) for CODEX reads/writes, CLI theming/attachments. |
| `Arcanum:Bureau` | **Reserved** (no-op today). |

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

`DefaultModel`/`FastModel` must match a `models` entry on some provider (case-insensitive, Ollama-style `:latest` matching). OpenAI-compatible `endpoint`s usually include `/v1`. **MCP servers** are wired via `~/.config/arcanum/mcp.json` (`mcpServers` schema); workspace-local `mcp.json` is merged only after `POST /api/mcp/trust-workspace`. See [DESIGN.md §3.4](docs/DESIGN.md#34-configuration-reference-arcanumsettings) and the MCP host limits there.

---

## CLI quick reference

All commands run as `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- <cmd>` in development, or `arcanum <cmd>` after an AOT publish.

| Command | Purpose |
|---------|---------|
| `serve` | Run the HTTP host on `localhost:5001` (writes a PID file). |
| `ask <prompt>` | Single-turn inference (NDJSON stream). Flags: `-n` new session, `-m <model>`, `--unattended`. |
| `chat` | Interactive multi-turn REPL (Markdig rendering, mana bar, slash commands). Flags: `--new`, `-m`, `--no-tools`, `--unattended`. |
| `look` | Print the Eye of the World workspace snapshot (no HTTP). |
| `doctor` | Environment diagnostics + API health probe. |
| `lore list\|get\|set\|delete` | Operator key-value memory via `/api/lore`. |
| `daemon install\|uninstall\|status` | OS background-service lifecycle. |
| `daemon jobs\|initiative\|alert` | Unseen Servant inspection + Comm Link smoke test (needs `serve`). |
| `llama pull\|start\|stop\|status` | Manage local GGUF models + `llama-server` (needs `serve`). |

**Inference flags** (both `ask`/`chat`): `--temperature`, `--top-p`, `--max-tokens`, `--seed`, `--stop`, `--response-format`, `--presence-penalty`, `--frequency-penalty`. **Chat slash commands** include `/new`, `/model`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume`, `/mana`, `/attach`, `/log`. The CLI auto-disables ANSI/prompts/mana bar when stdout is redirected or `NO_COLOR`/`ARCANUM_NO_COLOR` is set. Full detail: [DESIGN.md §4.4](docs/DESIGN.md#44-retrodownfallarcanumcli-console-executable).

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

> **Key rotation is destructive.** The Grimoire passphrase is HKDF-derived from the API key, so a rotated key cannot decrypt the existing store. To rotate: stop the host, remove **both** `security.dat` and the Grimoire `.db` under `~/.config/arcanum/`, then restart. See [DESIGN.md §16.3](docs/DESIGN.md#163-security-and-identity).

---

## Build, test & verify

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download); at least one configured provider; for local GGUF, `llama.cpp`'s `llama-server` on `PATH` (or set `Arcanum:LlamaCpp:ServerExecutablePath`).

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
```

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

`dotnet build` is warning-clean in Debug/Release. `dotnet publish` may emit clang `.pcm`/`ld` toolchain notices (not IL diagnostics); on Homebrew `dotnet`, the CLI adds conditional linker paths for keg-only OpenSSL/Brotli. See [DESIGN.md §9.3](docs/DESIGN.md#93-tradeoffs-and-constraints).

> **CVE note:** `Microsoft.Bcl.Memory` is pinned to a patched build in [`Directory.Build.props`](Directory.Build.props) to mitigate **CVE-2026-26127** (a DoS in Base64Url decoding pulled in transitively by `Microsoft.ML.Tokenizers.Data.O200kBase`). After bumping major packages, run `dotnet list package --vulnerable` and an AOT publish to confirm no regressions.

### Database migrations (EF Core)

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
3. **Names the right project and types** — e.g. "implement in `HubIntelligenceProvider` (Api), contract in `IArcanumIntelligenceProvider` (Core)." Use the [repository map](#repository-map) and [naming metaphor](#naming-metaphor).
4. **Respects the metaphor** — Campaign/Spell/Ward/Apprentice/Grimoire, error codes `{Noun}.{Verb}`, config `Arcanum:{Noun}:{Setting}`.
5. **Preserves OpenAI parity** — don't change `/v1` shapes casually; remember client `tools` are intentionally rejected.
6. **Keeps the security posture** — loopback default, API key on every route, path containment, SSRF guard, sanitized errors, strict CSP (external JS/CSS only).
7. **Specifies the verification gates** — `dotnet build` clean, `dotnet test`, and `./scripts/verify-aot-il-warnings.sh` for serialization/dependency changes.
8. **Requires docs in the same change** — update `docs/DESIGN.md` (and this README for operator-visible changes).
9. **Follows C# house style** — one blank line after each line of C#; file-scoped namespaces; positional records without `[JsonPropertyName]`.

When unsure about a contract, clamp, or lifecycle detail, **read the linked DESIGN.md section** rather than guessing — it is the source of truth.

---

## Further reading

- **[`docs/DESIGN.md`](docs/DESIGN.md)** — the authoritative deep reference. Quick links: [§3.4 Configuration](docs/DESIGN.md#34-configuration-reference-arcanumsettings) · [§4 Projects](docs/DESIGN.md#4-project-model-and-dependency-graph) · [§8 HTTP/JSON design](docs/DESIGN.md#8-http-json-and-minimal-api-design-api-project) · [§9 Native AOT](docs/DESIGN.md#9-native-aot-and-trimming) · [§10 Intelligence pipeline](docs/DESIGN.md#10-intelligence-pipeline) · [§11 Security](docs/DESIGN.md#11-local-api-security) · [§17 Glossary](docs/DESIGN.md#17-glossary) · [§19 The Forge](docs/DESIGN.md#19-the-forge--campaign-spell-metadata-and-prompt-registry)
