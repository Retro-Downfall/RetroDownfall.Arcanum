# Retro Downfall Arcanum

> **Agent orientation document.** This README gives an AI coding agent or operator the shortest
> useful context for Arcanum. **[`Arcanum.DESIGN.md`](Arcanum.DESIGN.md)** is authoritative for
> architecture, APIs, persistence, runtime behavior, packaging, and testing.
> **[`Compendium.README.md`](Compendium.README.md#complete-configuration-reference)** is the only
> complete configuration reference, and **[`Arcanum.Design.Human.md`](Arcanum.Design.Human.md)** is
> the human-readable navigation companion.

**Arcanum** is a **.NET 10, local-first AI assistant and inference hub.** The `arcanum` executable
runs either as the long-lived HTTP host (`arcanum serve`) or as thin terminal clients (`ask`,
`chat`, `look`, `lore`, `daemon`, `campaign`, `session`, `saga`, `spell`, `prompt`, `ward`, `trial`,
`apprentice`, `model`, `provider`) over the same API. Windows and Linux ship the CLI/host as one
self-contained Native AOT executable; the current macOS arm64 release is a signed, notarized,
folder-based self-contained publish because of the supported linker/toolchain limitation. Arcanum
exposes an **OpenAI Chat Completions compatibility subset**, routes inference across OpenAI-compatible
HTTP providers (including Ollama through `/v1`), and persists state in an encrypted SQLCipher store.

- **Stack:** .NET 10 · ASP.NET Core Minimal API · Native AOT on Windows/Linux · `Microsoft.Extensions.AI` · EF Core 10 + SQLCipher · System.CommandLine 2.0.10 + Spectre.Console
- **Version:** `0.1.0-beta` (see [`Directory.Build.props`](../Directory.Build.props))
- **Audience for the code:** senior C#/.NET engineers and coding agents extending an AOT-constrained, API-first system.

---

## The standards (read this first)

These are **non-negotiable** and define what "correct" means in this repo. Every prompt you write and every change you make must hold the line on all of them. They are the reason many "obvious" approaches (reflection-based JSON, `AIFunctionFactory.Create`, anonymous DTOs, inline `<script>`) are **wrong here**.

### 1. Native AOT compatibility (hard constraint)

Windows/Linux ship a **Native AOT** binary with **zero runtime prerequisite**. macOS currently uses
a folder-based self-contained fallback, but the shared host remains AOT-constrained: minimal
reflection, source generation, and an AOT warning gate still dictate serialization and binding.
See [DESIGN.md §9](Arcanum.DESIGN.md#9-native-aot-and-trimming).

- **Source-generated JSON only.** Every HTTP payload type must have a `[JsonSerializable]` registration on **`ArcanumJsonContext`** (Api). Other contexts are scoped: `GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext` (Core — Grimoire blobs, `arcanum.json`, campaign/skill metadata), `McpJsonSerializerContext` / `McpConfigJsonSerializerContext` (Infrastructure, JSON-RPC + `mcp.json`), `CommLinkInfrastructureJsonContext` (outbound webhooks). **Never** use reflection-based `JsonSerializer` overloads, `PostAsJsonAsync` with anonymous types, or `Results.Json` without an explicit `JsonTypeInfo`.
- **Source-generated request delegates.** `Api` sets `EnableRequestDelegateGenerator`; handlers must be RDG-compatible (no unbounded reflection model binding, no anonymous return DTOs).
- **Hand-authored tool schemas.** New `AIFunction` tools use explicit `JsonDocument` schemas, **not** `AIFunctionFactory.Create`.
- **Config binding** uses `EnableConfigurationBindingGenerator`. Settings POCOs under `Arcanum:…` must use `{ get; set; }` (not `init`) — the generator silently skips `init`-only properties (dotnet/runtime#107856), which previously left `Providers` / `DefaultModel` empty at runtime while `arcanum.json` still looked correct.
- **Verification gate:** a clean `dotnet publish` AOT run with zero first-party IL trim/AOT warnings. Use `./scripts/verify-aot-il-warnings.sh` (see [Build, test & verify](#build-test--verify)).

### 2. API-first design

**The HTTP API is the product.** The CLI, Studio UI, LibreChat, and any sidecar are all just clients of `/api` and `/v1`. Business logic lives behind the API, never in a client.

- Add behavior as **endpoints in `MapArcanumEndpoints`**, returning the **`ApiResponse<T>`** envelope via `ApiResponse<T>.FromResult`.
- Put **domain logic in `Core`**; keep `Api` to composition/orchestration and `Cli` to thin HTTP calls (`ArcanumApiClient`).
- CLI verbs that need server state (`lore`, `daemon jobs`, …) **call the running host's API** rather than reaching into infrastructure directly.

### 3. OpenAI Chat Completions compatibility subset

Arcanum exposes a **Chat Completions compatibility subset** so common OpenAI clients work for chat. See [DESIGN.md §8.8](Arcanum.DESIGN.md#88-openai-v1-chat-completions-compatibility-subset). Moderations/images/audio remain `501 not_supported`.

- **`POST /v1/chat/completions`** (JSON or SSE) and **`GET /v1/models`** (auto-discovery across all configured providers).
- Request parsing including multimodal `content` parts, `tool`/`assistant` tool-call replay, `stream_options.include_usage`, `response_format`, etc.
- Responses carry `usage`, `system_fingerprint`, and OpenAI-shaped error envelopes. **Auth** accepts `Authorization: Bearer <KEY>` for OpenAI clients (as well as `X-Arcanum-Key`).
- Arcanum runs **its own server-side MCP toolset** by default, so client-supplied `tools`/`tool_choice` are rejected with `400 unsupported_parameter` (except `tool_choice: "auto"`/`"none"`, which are always accepted as OpenAI defaults). Operators may opt in to **client tool forwarding** via `Arcanum:Features:ClientTools`; when enabled, client schemas are forwarded to the resolved provider (per-tool `strict` flag preserved via `AIFunction.AdditionalProperties`), `tool_choice.function.name` is verified against the supplied `tools`, and the returned `tool_calls` are surfaced for the client to round-trip (bypasses Arcanum's server-side tool loop, Sanctum, Wards, and tool audit logging).

### 4. Top-of-the-line, all-native multi-provider inference engine

Inference flows through one hub behind a single `IChatClient` abstraction. See [DESIGN.md §10](Arcanum.DESIGN.md#10-intelligence-pipeline); the exact turn order is [§10.7](Arcanum.DESIGN.md#107-end-to-end-turn-lifecycle-and-chat-loop).

- **`WizardIntelligenceProvider`** + **`ToolExecutionPipeline`** + **`IChatClientFactory`**; providers are **`OpenAICompatible` only** (including Ollama via `/v1`). No managed local inference.
- **`TurnEngine` is a bounded semantic shell** over Wizard's `ITurnPipelineRunner`; Wizard still owns the one mode-parameterized model/tool loop. The primary loop can call native `delegate_task` to start exactly one fresh buffered child TurnEngine with a sterile stateless context, explicit file values, and a delegated token/cost/turn ceiling. Only the child summary or structured failure returns to the parent.
- **`ProviderResolver`** maps model → provider from `Arcanum:Providers` (no hard-coded model names).
- Agentic layers: MCP tool loops, semantic spell routing, read-time context compression, Wards, Sanctum.
- **Structured output / pricing / budgets / capability-driven provider prompt caching / guardrails** — see [DESIGN.md §22](Arcanum.DESIGN.md#22-structured-output-cost-tracking-and-prompt-caching) and [§8.27](Arcanum.DESIGN.md#827-content-guardrails-pii--toxicity--topics). Arcanum never caches or replays inference responses.

### The Proving Grounds

Ephemeral Trials via `POST /api/proving-grounds/trials/run` (regex / jsonSchema / semantic Inquisitors). Desktop UI: [DESIGN.md §19.10](Arcanum.DESIGN.md#1910-desktop-vocabulary-and-implemented-surfaces). Server behavior: [§20](Arcanum.DESIGN.md#20-the-proving-grounds--trials-and-inquisitors).

### 5. Local-first security posture

Single-user, loopback-by-default, secret-minimizing. See [DESIGN.md §11](Arcanum.DESIGN.md#11-local-api-security).

- Kestrel binds **loopback only** unless explicitly opened; a **32-byte master API key** guards
  every `/api` and `/v1` route; the **Grimoire** is encrypted at rest (SQLCipher passphrase derived
  via PBKDF2-HMAC-SHA256 with a unique 16-byte salt stored in `{grimoire.db}.kdf`). Session
  attachments, uploaded files, and batch artifacts outside SQLCipher are independently protected
  by versioned, chunk-authenticated AES-256-GCM envelopes.
- Sensitive files (`arcanum.json`, Grimoire `.db`, `cli-session.txt`, logs) are created **owner-only** (`chmod 600/700` on Unix; owner ACL on Windows). Startup warns if group/other can read them.
- `Arcanum:Host:ListenAny` requires **first-run acknowledgement** in interactive `serve` (or `ARCANUM_LISTEN_ANY_ACK=1` / `ARCANUM_HOST_ANY` for automation) and emits a **security banner** when binding all interfaces over **HTTPS only** (plaintext any-IP HTTP is refused; `Arcanum:Host:Https:Enabled` + cert required).
- `WorkspacePathPolicy` containment, symlink walking, and handle-identity revalidation are the primary boundary for file/search/patch tools; campaign Sanctum is an additional conditional allowlist. Shared `SecureFileReader` opens no-follow/nonblocking, accepts only regular single-link files, reads through cleared capped pools, and revalidates identity; FIFO/device/hardlink/symlink inputs fail closed. Host-process tools (`execute_command` / `run_spell_script`) use `ArgumentList` (no shell) with child-env scrubbing and are **gated by Local edition** unless Development + `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1`; workspace MCP requires trust bound to the exact approved bytes. **Tool-child FS jail** is filesystem-only: macOS uses Seatbelt, Linux remains inactive/fail-closed, and Windows uses a per-invocation AppContainer identity with explicit allowed-root ACLs plus Job Object process-tree/resource enforcement. The Windows broker confirms Job membership before resuming the suspended untrusted target; capability or setup failure fails closed, and health/doctor are Healthy only when AppContainer is genuinely available. Owner-only temp artifacts are deleted only after identity-safe quarantine checks. `workspace_check` is stricter and separate: advertised only on an eligible macOS Seatbelt host, never enabled by `AllowUnsandboxedToolChildren`, and unavailable on Linux/Windows. SSRF guard + DNS-rebind pinning on untrusted egress; sanitized public error envelopes. Details: [DESIGN §11](Arcanum.DESIGN.md#11-local-api-security).

### 6. Strict Content Security Policy on every web surface

**First-party browser UI must externalize scripts and styles** (JS in `.js` files, CSS in `.css` files — no inline first-party code). The opt-in **Scalar** UI (`Arcanum:Features:ScalarUi`) is a third-party exception served under the same-origin CSP documented in [DESIGN.md §11.5](Arcanum.DESIGN.md#115-openapi-and-scalar).

### 7. C# house style

- **One blank line after each line of C# code** (visual breathing room) — applied throughout the codebase. Within reason. Curly braces do not require blank lines around them. Neither do control statements like if and loops, etc. Also, long-running Linq statements do not require blank lines either.
- File-scoped namespaces; positional records for DTOs/contracts; **no `[JsonPropertyName]`** on `/api` wire types (casing comes from `[JsonSourceGenerationOptions]`); OpenAI `/v1` and MCP JSON-RPC types are explicit exceptions (§8.2); primary constructors for DI; `IDisposable` where a service owns a `SemaphoreSlim`/`ServiceProvider`. See [DESIGN.md §12](Arcanum.DESIGN.md#12-c-language-and-coding-conventions).

> **Note on org-wide rules:** Corp-wide standards scoped to `Corp.Solution.*` solutions (Dapper + SQL Server stored procedures, the `Corp.Lib.*` NuGet stack, Refit "Service Libraries") **do not apply to Arcanum** — it is local-first over its own EF Core + SQLCipher Grimoire and retains AOT-safe contracts across Native AOT Windows/Linux and self-contained macOS packaging. The always-on house rules (blank lines, strict CSP, docs-in-same-change-set) still hold.

### 8. Thematic naming metaphor (D&D)

Arcanum uses Dungeons & Dragons and/or fantasy metaphors for domain concepts. New features **must** follow it if possible. Current exceptions include "prompt" and "workspace". See [Naming metaphor](#naming-metaphor).

### 9. Docs travel with code

The repository maintains exactly five docs (`Arcanum.DESIGN.md`, `Arcanum.README.md`, `Arcanum.Design.Human.md`, `Compendium.README.md`, `Arcanum.DEBUGGING.Human.md`). Architecture, APIs, persistence, runtime behavior,
testing, and packaging update `Arcanum.DESIGN.md`; the complete public configuration contract updates
`Compendium.README.md`; agent/operator orientation updates this file; human navigation updates
`Arcanum.Design.Human.md`; debugging guides update `Arcanum.DEBUGGING.Human.md`. Keep the owning documents current in the same change set. See
[DESIGN.md §18](Arcanum.DESIGN.md#18-document-maintenance).

---

## Architecture at a glance

**One CLI/host entry point, hybrid process model.** A System.CommandLine 2.0.10 verb selects the role:
`serve` (long-running Kestrel host) vs. short-lived commands. See
[DESIGN.md §5](Arcanum.DESIGN.md#5-hybrid-hosting-model).

**Dependency chain:** `Cli → Api → Infrastructure → Core` (`Cli` also references `Core`/`Infrastructure` directly for lightweight DI). Strict project boundaries are a deliberate goal.

| Project | Role | Owns | AOT |
|---------|------|------|-----|
| **`Core`** | Domain primitives, contracts, configuration | `Result`/`Result<T>`, `Error`, `ApiResponse<T>`, `ArcanumSettings`, `IArcanumIntelligenceProvider`, `PingRequest`, `IGrimoireRepository`, `IEyeOfTheWorld`, events, source-gen contexts (`GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext`) | `IsAotCompatible` |
| **`Infrastructure`** | OS-adjacent services | Serilog, Data Protection, encrypted Grimoire (EF Core 10 + SQLCipher, compiled model), authenticated encrypted blob storage + OS-backed file key, workspace scanning, reliable `search_workspace` / `apply_patch` / `workspace_check` engines, Eye of the World, the **MCP client layer** (subprocess + in-process transports, `ArcanumInternalToolServer`), Comm Link | `IsTrimmable` + `PublishAot` (analysis signal) |
| **`Api`** | HTTP surface composition (class library, **not** executable) | `MapArcanumEndpoints`, `ApiBootstrapper`, `WizardIntelligenceProvider`, `TurnExecutionCoordinator`/`TurnEngine`, `ToolExecutionPipeline`, `IChatClientFactory`, `SemanticRouter`, built-in `AIFunction` tools, `ApiKeyEndpointFilter`, `ArcanumJsonContext`, `/v1` OpenAI endpoints | `IsAotCompatible` + `EnableRequestDelegateGenerator` |
| **`Cli`** | Shipping CLI/host entry point | Spectre commands, `ArcanumApiClient`, theming, AOT-safe Markdown rendering (`MarkdigSpectreRenderer`) | `PublishAot` on Windows/Linux; self-contained folder on macOS |
| **`Api.DevHost`** | Debug-only F5 host (not shipped) | Mirrors `serve` wiring without Spectre | `PublishAot` + `IsAotCompatible` (analysis signal; not shipped) |
| **`tests/RetroDownfall.Arcanum.Tests`** | xUnit test suite (not shipped) | MCP, security, config, workspace policy, SQLCipher Grimoire, and API-host integration tests | — |
| **`tests/RetroDownfall.Compendium.Tests`** (assembly `RetroDownfall.Compendium.Ux.Tests`) | Compendium smoke tests (not shipped) | Round-trip read/write of factual configuration and credential references | — |
| **`Compendium.Ux`** | Desktop configuration editor (Avalonia) | Visual editor for the 11 retained configuration sections; polished Host/Providers/Daemon/CLI pages plus descriptor-driven pages; reuses Core models and edits credential references, never secret values | — |
| **`TheForge.Core` / `TheForge.Ux`** | Desktop Inference IDE (Avalonia) | HTTP-only Arcanum client, Campaign/Spell/Prompt/Session workbench, Wards, MCP, Trials, diagnostics | — |
| **`tests/RetroDownfall.TheForge.Tests`** | Forge desktop tests (not shipped) | Client contracts, settings, view models, and source-generated JSON | — |

**Key entry points to know:** `ApiBootstrapper.AddArcanumApiServices` / `MapArcanumEndpoints` (wire everything), `AddArcanumInfrastructure` (Infrastructure DI), `WizardIntelligenceProvider` (existing inference orchestration and `ITurnPipelineRunner`), `TurnEngine` (bounded semantic shell), and `Cli/Program.cs` (command registration).

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
  RetroDownfall.Compendium.Ux/           # desktop `arcanum.json` editor (Avalonia)
  RetroDownfall.TheForge.Core/           # portable Forge client contracts/services
  RetroDownfall.TheForge.Ux/             # desktop Inference IDE (Avalonia)
  RetroDownfall.Arcanum.Api.DevHost/     # debug-only host
tests/
  RetroDownfall.Arcanum.Tests/           # xUnit tests (MCP, security, config, workspace policy, SQLCipher Grimoire)
  RetroDownfall.Compendium.Tests/        # Compendium round-trip smoke tests (assembly: RetroDownfall.Compendium.Ux.Tests)
  RetroDownfall.TheForge.Tests/          # Forge client/UI tests
docs/                                    # the complete four-document contract
  Arcanum.README.md                      # this agent orientation document
  Arcanum.DESIGN.md                      # authoritative technical reference
  Arcanum.Design.Human.md                # non-authoritative human reading companion
  Compendium.README.md                   # sole complete arcanum.json reference
scripts/coverage.sh                      # run tests, generate Cobertura + HTML coverage; pass --threshold to enforce gates
scripts/coverage_threshold.py            # tiered coverage threshold enforcement
scripts/coverage_threshold_test.py       # coverage threshold script tests
scripts/align-csharp-blanklines.sh       # C# blank-line formatter entrypoint
scripts/align_csharp_blanklines.py       # C# blank-line formatter logic
scripts/verify-aot-il-warnings.sh        # AOT IL-warning gate
scripts/packaging/macos/                 # signed/notarized macOS arm64 packaging
scripts/packaging/linux/                 # unsigned Linux private-beta tarballs (CLI AOT + Forge/Compendium)
scripts/packaging/windows/               # unsigned Windows zips (CLI AOT + Compendium; Forge optional via package-windows.ps1)
                                         # workflow: build-windows-x64.yml (Arcanum + Compendium); private-beta-release.yml (all three)
Directory.Build.props                    # shared MSBuild props + CVE pin (Microsoft.Bcl.Memory)
```

### Patterns to follow when writing code

These are the recurring shapes. Matching them is what makes a change "fit."

- **Wire envelope.** JSON under `/api` returns `ApiResponse<T>` (`Data`, `IsSuccess`, `Error`, `TraceId`). Map from domain with `ApiResponse<T>.FromResult`. Exceptions: streaming (NDJSON), SSE event buses, and OpenAI `/v1` (raw OpenAI shape). See [DESIGN.md §8.1](Arcanum.DESIGN.md#81-wire-contract-the-apiresponset-envelope).
- **Result flow.** Domain ops return `Result` / `Result<T>` and rely on implicit conversions; the endpoint is the single place that turns a `Result` into an envelope + status code.
- **New endpoint checklist:** add to `MapArcanumEndpoints` → return `ApiResponse<T>` (or documented streaming shape) → register every new payload type on `ArcanumJsonContext` → `.WithName(...)` for OpenAPI → use explicit `JsonTypeInfo` on failable `Results.Json` → update DESIGN.md §4.3 + this README's API map.
- **New CLI verb:** add a public method (XML doc `<summary>`/`<param>` comments drive `--help` text and aliases) to a grouped command class under `Cli/Commands`, registered via `app.Add<T>("path")` in `CliApplicationFactory.RunAsync`; prefer `AddArcanumEyeOfTheWorld()` over full infrastructure for lightweight verbs.
- **New inference provider:** add an `AiProviderKind` and extend `IChatClientFactory`; keep the `WizardIntelligenceProvider` contract intact.
- **New MCP tool:** implement on `ArcanumInternalToolServer` with a hand-authored JSON schema via `McpJsonSerializerContext`; honor unconditional `WorkspacePathPolicy` containment and `ToolOutputCapBytes`; decide whether it belongs in `ToolRiskClassifier.IntrinsicWardToolNames`. Do not treat campaign Sanctum as the primary filesystem boundary.
- **Treat all wire types as versioned contracts.** Casing is fixed at the context level; don't add `[JsonPropertyName]` except on OpenAI `/v1` and MCP JSON-RPC types (see [DESIGN.md §8.2](Arcanum.DESIGN.md#82-arcanumjsoncontext--source-generated-public)).
- **Register long-running work.** Use the scoped `ILongRunningOperationCoordinator`; add the kind
  and exactly one recovery policy to `LongRunningOperationPolicyCatalog`, implement an idempotent
  recovery handler, store only minimum encrypted checkpoint state, and expose only a bounded safe
  summary. Never persist a live Task/token/enumerator/process/DI object. See
  [DESIGN §10.8](Arcanum.DESIGN.md#108-durable-operation-ledger-and-restart-reconciliation).
- **Change pre-user raw-SQL schemas directly.** Arcanum has no users yet, so update canonical
  initializer definitions and recreate local/test databases; do not add compatibility migrations
  or in-place upgrade paths. Revisit this policy before durable user data exists.

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
| High-risk gated tools | **Forbidden Arts** | `Arcanum:Security:Ward:ForbiddenArts` |
| Autonomous sub-agent | **Apprentice** | `/api/apprentices` |
| Multi-agent coordination network | **The Conclave** | `cast_sending` tool · `/api/apprentices/{id}/cast` |
| Agent event stream | **Chronicle** | `/api/apprentices/{id}/chronicle` (SSE) |
| A2A Agent Card | **Heraldry** | `GET /api/conclave/a2a/agent-card` |
| A2A Task (inbound or outbound) | **Sending** (a.k.a. Delegated Quest) | `/api/conclave/a2a/*` · `dispatch_sending` tool |
| The Conclave's outward-facing A2A delegate | **Archmage Client** | `IA2AClientService`/`A2AClientService`, invoked via `dispatch_sending` |
| Human operator | **Dungeon Master (DM)** | — |
| Encrypted persistence store | **Grimoire** | (internal: EF Core + SQLCipher) |
| Background job runner | **Unseen Servant** | `/api/unseen-servant/*` |
| Situational directory perception | **Eye of the World** | `/api/perception/look` |
| Operator key-value memory | **Lore** (legacy) | `/api/lore` |
| Agent-directed entity memory | **The Lexicon** | `scribe_lexicon` / `delete_lexicon` MCP tools; see [DESIGN.md §10.6](Arcanum.DESIGN.md#106-the-lexicon--agent-directed-entity-memory) |
| Operator alert channel | **Comm Link** | `/api/commlink/send` |
| Primary agent / inference orchestrator | **Master** | **`WizardIntelligenceProvider`** (implementation class; implements **`IArcanumIntelligenceProvider`**) |
| Scratchpad / instructions | **Codex** | `CODEX.md`, `/api/codex` |
| Multi-turn chat thread | **Session** (rows = **Entry**) | `/api/sessions` |
| Spell/prompt/plan validation | **The Proving Grounds** (Trials, Inquisitors) | `POST /api/proving-grounds/trials/run` |
| Embedding & vector substrate | **The Weave** | `Arcanum:Features:Embeddings` plus `Arcanum:Integrations:Embeddings`; see [DESIGN.md §21](Arcanum.DESIGN.md#21-the-weave-divination-and-saga-rag) |
| Semantic search over The Weave | **Divination** | `IDivinationService`; `POST /api/sessions/divine`, `POST /api/workspaces/{id}/files/divine`, `POST /api/saga/divine` (§21) |
| Vector representation of text | **Imprint** | `IWeaveService.EmbedAsync`/`EmbedBatchAsync` ("imprints" text into The Weave; §21) |
| Long-term associative memory | **Saga** | `/api/saga/*`, `read_saga`, `arcanum saga` (§21.8) |
| Recursive Spell dependency injection | **Arcane Resonance** | `SpellDependencyResolver`; dependency and byte envelopes are internal invariants (Arcanum.DESIGN.md §10.2.2) |
| Pre-flight active-Spell selection | **Spell Routing** | `SemanticRouter` (LLM-based) + `SemanticSpellRouter` (embedding pre-filter); `Arcanum:Features:SemanticSpellRouting` (Arcanum.DESIGN.md §10.2.2, §21.9) |

**Rejected:** Dispel, Glyph, Invocation (too obscure). The placeholder **Bureau** was retired in favor of **The Conclave** (the multi-agent coordination network; see above).

**Naming rules:** thematic API routes (`/api/spells`); error codes `{Noun}.{Verb}` (`Ward.NotFound`, `Campaign.DuplicateName`) — cross-layer wire codes are centralized as `public const string` in `Core/Primitives/ErrorCodes.cs` (grouped by Validation / Hub / NotFound / etc.); HTTP status mapping for `Result.Error.Code` is centralized in `Api/TheForge/ArcanumErrorMapper.cs`; config paths `Arcanum:{Noun}:{Setting}`. Propose any new concept name to the DM before implementing. Full rationale in this section's source and DESIGN.md §2.1.

---

## API surface map

Default base `http://localhost:5001`. **All `/api` and `/v1` routes require the API key** (`X-Arcanum-Key` or `Authorization: Bearer`). Grouped overview — exhaustive inventory: [DESIGN §4.3](Arcanum.DESIGN.md#43-retrodownfallarcanumapi-class-library-not-executable).

| Area | Routes | Contract / purpose |
|------|--------|-------------------|
| Metrics | `GET /metrics` | Prometheus text; API key on by default (forced on ListenAny). [§8.22](Arcanum.DESIGN.md#822-metrics-endpoint-get-metrics) |
| Health & meta | `/api/health`, `/meta`, `/grimoire/stats`, `/budget` | Readiness + spend snapshot; 503 mainly when Grimoire Unhealthy |
| Durable operations | `/api/operations*` | Safe list/show plus CAS cancel/retry and bounded manual reconciliation; checkpoint bytes/references never leave SQLCipher |
| Config | `/api/config`, `/config/validate` | GET redacts secrets; PUT preserves `"***"` placeholders |
| Models / providers | `GET /api/models`, `/providers`, `/providers/test` | Listings + connectivity probe (no persist) |
| Inference (native) | `/api/intelligence/ping(-stream)`, `/human-response`, `/arsenal`, `/mana` | Buffered / NDJSON `IntelligenceEvent`; model-aware Mana/source breakdown |
| Inference (OpenAI) | `POST /v1/chat/completions`, `GET /v1/models`, `POST /v1/embeddings` | OpenAI JSON/SSE; Scrying gates images; client tools opt-in |
| OpenAI stubs | `/v1/moderations`, `/images/*`, `/audio/*` | Always 501 `not_supported` |
| Files / Batches | `/v1/files*`, `/v1/batches*` | Upload + async JSONL chat batches |
| Sessions | `/api/sessions/*` (+ entries/stream/attachments/divine/fork/pin/compact) | Grimoire threads; memory-mgmt gated; RAG divine off by default |
| Lore / Saga | `/api/lore/*`, `/api/saga/*` | Legacy KV lore; Saga auto-memory (divine gated) |
| Spells / Prompts / Campaigns | `/api/spells/*`, `/prompts/*`, `/campaigns/*`, `/codex` | Forge registry + execute/stream/versions |
| Apprentices / A2A | `/api/apprentices/*`, `/conclave/a2a/*` | Goal agents + optional A2A (off by default) |
| Wards / Sanctum | `/api/wards/*`, `/campaigns/{id}/sanctum*` | Forbidden Arts + sandbox / FS-jail |
| MCP | `/api/mcp*`, `/mcp/tools/invoke` | Lifecycle + diagnostic external invoke |
| Workspaces | `/api/workspaces/*` | File browser/write gate + Weave index/divine |
| Unseen Servant | `/api/unseen-servant/*` | Interval control; watermarks persist; `lastResult` process-local |
| Events / Comm / Perception | `/api/events/*`, `/commlink/send`, `/perception/look` | SSE; webhook; Eye of the World |
| Trials / Logs / Audit | `/proving-grounds/trials/run`, `/logs`, `/audit`, `/guardrails/audit` | Ephemeral trials; ring buffer; JSONL audits |
| Tools / Docs | `POST /api/tools/invoke`, `/openapi/v1.json`, `/scalar` | Built-in invoke; OpenAPI; Scalar opt-in |

**Wire shapes:** `ApiResponse<T>` for `/api` JSON; NDJSON for streams; SSE for events/session/Chronicle; OpenAI shapes for `/v1`. Native NDJSON includes additive `context` frames with the pre-call estimate and optional post-call provider variance; OpenAI SSE intentionally filters those Arcanum diagnostics. Native clients preflight `type`: unknown nonblank future strings are silently skipped, while malformed JSON or missing/non-string/blank/whitespace-padded discriminators retain diagnostics and the stream continues. Direct source-generated enum deserialization stays strict. Compression + Idempotency-Key: [§8.25](Arcanum.DESIGN.md#825-http-response-compression) / [§11.17](Arcanum.DESIGN.md#1117-idempotency-key-request-replay).


## Inference engine details

Summaries only — full contracts live in DESIGN.

- **Providers:** `Arcanum:Providers[]` keeps provider name/type/endpoint, optional credential environment-variable reference, factual model inventory/capabilities, and context capacity. Tokenization and prompt-cache behavior are code-owned: the built-in catalog selects verified behavior, and unknown endpoints/models emit no cache directives or cached-usage claim.
- **Model-aware context accounting:** `IModelTokenEstimator` resolves the built-in verified official-OpenAI exact `o200k_base` families or a conservative fallback (at least UTF-8 bytes plus margin). Every provider call accounts for messages, complete tool schemas, structured-output schema, RAG/memory/attachments, provider framing, and separate answer/reasoning reserves. `/api/intelligence/mana`, native `context` frames, successful audit records, Command Center `/mana`, and Prometheus expose quality/source/variance; provider-reported usage remains the post-call authority.
- **First-class reasoning:** native requests use `reasoning:{effort?,budgetTokens?,output?}` where effort is `none|minimal|low|medium|high|extraHigh`, output is `none|summary|full`, and effort/budget are mutually exclusive. OpenAI requests use `reasoning_effort` (`xhigh` maps to native `extraHigh`), additive `reasoning_budget`, and `reasoning_output`. `reasoning_output` is an Arcanum-local exposure preference plus a Microsoft.Extensions.AI best-effort hint, not a guaranteed provider wire control; Arcanum never invents an unsupported provider field. When output is omitted, a full-capable model defaults to `full`, otherwise a summary-only model defaults to `summary` (subject to `allowsClientOutput`, and `supportsStreaming` on streams). Reasoning and capability/dialect enums are string-only; numeric or unknown enum JSON fails strict binding. Model objects opt in with `reasoning:{controlSupport,supportsSummary,supportsFull,supportsStreaming,reportsReasoningTokens,allowsClientOutput,wireDialect,maxBudgetTokens?}`; control support is `none|effort|budget|effortAndBudget`, and the closed dialects are `standard|openRouter|topLevelReasoningBudget|anthropicThinking`. No dialect is inferred from provider/model names.
- **OpenAI reasoning errors:** semantic validation is identical for buffered and `stream:true` requests and returns HTTP 400, `type:"invalid_request_error"`, `param:"reasoning"`, with `invalid_reasoning_options`, `invalid_reasoning_budget`, `unsupported_reasoning_control`, `reasoning_budget_exceeds_model_limit`, or `unsupported_reasoning_output`. Unknown enum strings and defined/undefined integer enum values fail earlier as strict JSON binding: HTTP 400, code `invalid_json`, no `param`.
- **Reasoning separation:** native buffered responses expose an ordered `reasoning` array; NDJSON uses typed `reasoning` frames; OpenAI buffered/SSE uses additive `reasoning_summary` / `reasoning_content`; native usage exposes additive `cached_tokens` and `reasoning_tokens`, while OpenAI usage uses `prompt_tokens_details.cached_tokens` and `completion_tokens_details.reasoning_tokens`. Answer fields remain answer-only. Visible reasoning is ephemeral, provider `ProtectedData` stays in memory only for same-provider tool continuation, and no reasoning body enters Grimoire, logs/audit, trace export, Master/Apprentice handoff, checkpoints, or Chronicles. The Forge Tome renders a live reasoning role and traces retain only redacted type/output/count metadata.
- **Agentic layers:** spell routing (+ optional embedding pre-filter), Arcane Resonance, Artifact Attunement, MCP tool loops, read-time compression, Wards, Sanctum. Artifact Attunement applies to MCP tools plus native `web_search` / `read_url`; exactly local time, system info, and spell-script tools are exempt. Legacy spell declarations of `browse_web` canonicalize to `read_url`. Spell validation and dry-run preview use the same web-tool decision. See [DESIGN §10](Arcanum.DESIGN.md#10-intelligence-pipeline), especially the canonical [turn lifecycle in §10.7](Arcanum.DESIGN.md#107-end-to-end-turn-lifecycle-and-chat-loop).
- **Reliable workspace tools:** `search_workspace` performs strict-UTF-8, deterministic, line-scoped literal or bounded runtime-regex search directly over the workspace (non-backtracking first, interpreted fallback, no `RegexOptions.Compiled`, no Weave). `apply_patch` separates pure unified-diff parsing from all-file filesystem planning, then uses one reversible **sequential, observable, non-isolated** transaction per call; it requires a persisted assistant turn and deterministically persists the exact arguments/result before the result reaches the model. It offers rollback and relative recovery artifacts, not process-wide isolation or crash atomicity. `workspace_check` runs closed `.NET` build/test/lint profiles with `--no-restore`, read-only source/package/SDK roots, and owner-only per-run outputs. Repository tasks/generators/analyzers/tests still execute arbitrary code, so it always Wards while Wards are on. It is advertised only with eligible macOS Seatbelt + trusted `dotnet`/SDK/launch chain; Linux/Windows are unavailable. Network remains open and intentionally detached-descendant cleanup is best effort. Full status/recovery contract: [DESIGN §10.2.1](Arcanum.DESIGN.md#1021-built-in-tools-and-mcp-workspace-tools).
- **Bounded tool results / Apprentice denials:** result materialization normalizes malformed UTF-16 and bounds retained text plus its marker with shared surrogate-safe UTF-8 helpers. Ward/Sanctum denial is carried to Apprentice orchestration through an internal non-wire `ToolDenied` bit, never phrase matching; reasoning frames never count as denial evidence.
- **Idempotency:** same-process requests coordinate locally before durable acquire; live foreign-process ownership returns 409 `Security.IdempotencyInProgress` (OpenAI `idempotency_in_progress`). The current renewable lease is five minutes. Only terminal in-cap responses replay; explicitly terminal empty bodies replay empty, while partial/over-cap responses do not. [DESIGN §11.17](Arcanum.DESIGN.md#1117-idempotency-key-request-replay).
- **Inference audit:** the opt-in JSONL log records successful completed turns only. Tool names/counts are retained; raw argument JSON is omitted by default (`Arcanum:Host:AuditLog:RedactToolArguments=true`); tool results and prompt/answer/reasoning bodies are not audit fields.
- **Scrying / attachments:** persisted bytes are durable snapshots stored as authenticated encrypted
  envelopes; plaintext hashes and lifecycle metadata remain inside SQLCipher. Optional live-file
  provenance is accepted only from a host-trusted path beneath the active workspace after
  canonical, symlink, and file-handle identity checks; API-supplied paths remain snapshot-only.
  Attachment responses expose sanitized relative provenance/status/hash/time metadata and never
  absolute host paths. Missing or unsafe sources do not delete snapshots. This schema revision is
  folded into the canonical database creation script, so upgrading installations must recreate the
  database. Full contract: [§10.2.4](Arcanum.DESIGN.md#1024-scrying--the-visionmultimodality-capability-gate) /
  [§10.2.5](Arcanum.DESIGN.md#1025-session-attachments-disk--grimoire-pointers).
- **A2A:** [§5.7.1](Arcanum.DESIGN.md#571-a2a-and-the-conclave) (disabled by default).
- **RAG (Weave / Divination / Saga):** [§21](Arcanum.DESIGN.md#21-the-weave-divination-and-saga-rag) — capabilities are gated under `Arcanum:Features`; embedding provider/model/dimensions and the codebase watcher debounce/count/reconciliation controls live under `Arcanum:Integrations:Embeddings`. Semantic workspace indexing reacts to debounced recursive watcher events, revalidates paths and opened file identities before every read, retains bounded periodic reconciliation when events are lost/unavailable, and exposes watcher/reconciliation health through `/api/workspaces/{id}/files/index/status`.
- **Lexicon:** agent memory via `scribe_lexicon` / `delete_lexicon`; gated by `Arcanum:Features:Lexicon`. [§10.6](Arcanum.DESIGN.md#106-the-lexicon--agent-directed-entity-memory).

---

## Configuration

Settings bind under the required `Arcanum` object in **`arcanum.json`** (`~/.config/arcanum/` on macOS/Linux, `%USERPROFILE%\.config\arcanum\` on Windows). General environment overrides keep the wrapper after the prefix, for example `ARCANUM_Arcanum__Host__Port`; `ARCANUM_EDITION` and `ARCANUM_HOST_ANY` are explicit overrides. Before binding, the source-generated configuration schema walks the complete tree and reports every unknown/obsolete path together; dynamic array indices and documented dictionary keys remain valid. Serve then runs semantic validation before listening.

Public settings are limited to deployment choices, provider/model facts, credential references,
security and permission policy, integration endpoints/allowlists, feature opt-ins, schedules,
host-capacity choices, pricing facts, and user preferences. Retry, fallback, workflow-count, and
other implementation mechanics are code-owned. Unknown or obsolete paths fail together before
binding; there are no compatibility aliases or silent ignores.

> **Compendium** edits the same file visually — [`Compendium.README.md`](Compendium.README.md). Provider rows edit factual fields and credential environment-variable references, never credential values or tokenization/prompt-cache algorithms.

**Full retained-key reference (types, defaults, clamps):** [Compendium's complete configuration reference](Compendium.README.md#complete-configuration-reference). `SettingDescriptors.cs` is Compendium's editable-key source of truth. DESIGN §3.4 documents only the architectural contract. The public roots are summarized here:

| Section | Controls |
|---------|----------|
| `Edition` | Runtime hardening mode. |
| `Host` | Port, CORS, external HTTPS binding, inference-audit policy, and buffered-log level. |
| `DefaultModel` / `FastModel` / `Providers` | Provider endpoint and credential reference, model inventory, vision/reasoning facts, and context capacity. |
| `Security` | Ward/guardrail policy, metrics authentication, path authority, MIME allowlists, and the unsandboxed-child acknowledgement. |
| `Workspaces` | Default root and explicit write permission. |
| `Features` | Capability opt-ins including Conclave/A2A, Apprentices, The Weave, Scrying, attachments, browsing, guardrails, workspace checks, and memory management. |
| `Integrations` | A2A identity/allowlist, CommLink reference/allowlists, embedding facts, native web-research provider facts, MCP plaintext-host exceptions, and workspace-check profiles. |
| `Execution` | Host concurrency/backpressure for Apprentices, SSE, and batches. |
| `Cost` | Default/per-model pricing and daily budget policy. |
| `Daemon` | Unseen Servant schedules and concurrency. |
| `Cli` | Theme and mana-bar preference. |

Turn mechanics, retries/fallback, structured-output correction, MCP transport limits, filesystem and
storage envelopes, session/fork limits, heartbeats, and other physical safeguards are code-owned
invariants—not configuration sections.

**Minimal complete example** (one provider, one model, no secret values):

```json
{
  "Arcanum": {
    "edition": "local",
    "defaultModel": "gpt-4o-mini",
    "providers": [
      {
        "name": "OpenAI",
        "type": "OpenAICompatible",
        "endpoint": "https://api.openai.com/v1",
        "credentialEnvironmentVariable": "OPENAI_API_KEY",
        "models": [
          {
            "name": "gpt-4o-mini",
            "supportsVision": false
          }
        ],
        "contextWindowLimit": 128000
      }
    ]
  }
}
```

Set `OPENAI_API_KEY` in the host environment. `models` entries may be bare strings or objects.
Ollama, when used, must use its `/v1` endpoint. A reasoning-capable model entry is explicit:

```json
{
  "name": "example-reasoner",
  "supportsVision": false,
  "reasoning": {
    "controlSupport": "effortAndBudget",
    "supportsSummary": true,
    "supportsFull": false,
    "supportsStreaming": true,
    "reportsReasoningTokens": true,
    "allowsClientOutput": true,
    "wireDialect": "openRouter",
    "maxBudgetTokens": 32768
  }
}
```

`standard` uses typed Microsoft.Extensions.AI/OpenAI controls and does not accept a numeric budget. Numeric budgets require exactly one explicit nonstandard shape: `openRouter` → `reasoning.max_tokens`, `topLevelReasoningBudget` → top-level `reasoning_budget`, or `anthropicThinking` → `thinking.budget_tokens`.

Provider credentials are environment-backed. An explicit
`credentialEnvironmentVariable` is the exact reference and replaces the default. When omitted,
Arcanum derives `ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY`: ASCII letters/digits are retained,
letters are upper-cased, runs of other characters become one underscore, and an empty result becomes
`UNNAMED`. Explicit references use portable `[A-Za-z_][A-Za-z0-9_]*` names. For the minimal
example:

```bash
export OPENAI_API_KEY='your-key-here'
```

PowerShell: `$env:OPENAI_API_KEY = "your-key-here"`.

### Native web research

Native web tools are off by default. Enable the family and select the synthesized-search model in
`arcanum.json`:

```json
{
  "Arcanum": {
    "features": {
      "webBrowsing": true
    },
    "integrations": {
      "webResearch": {
        "searchProvider": "perplexity",
        "perplexityModel": "sonar"
      }
    }
  }
}
```

Store a Perplexity key without putting it in configuration:

```bash
arcanum key provider set perplexity
arcanum key provider status perplexity
```

The secure prompt stores the key in the OS credential manager with an owner-only,
Data Protection-encrypted fallback. For unattended hosts, set `ARCANUM_PERPLEXITY_API_KEY`, or set
`integrations.webResearch.credentialEnvironmentVariable` to another exact environment-variable
name. The environment reference takes precedence at invocation time; key values are never returned
by status, provider-list, logs, or telemetry. Remove local copies with
`arcanum key provider delete perplexity`.

When enabled, models receive `web_search` for current, synthesized answers with ordered citations
and `read_url` for bounded static HTTP/HTTPS pages converted to Markdown. `read_url` does not launch
or embed a browser and does not execute JavaScript; bot-protected and empty JavaScript shells return
a structured error suggesting `web_search`. Both operations have a strict 15-second overall
deadline, bounded bodies/results, SSRF protection, untrusted-content framing, and aggregate-only
usage/token/cost/latency telemetry. The old `browse_web` direct-invoke/CLI behavior remains for
compatibility but is not advertised in new model toolsets.

Use only keys in [Compendium's retained reference](Compendium.README.md#complete-configuration-reference).
After changing `arcanum.json`, restart Arcanum. Configuration-only changes do not require deleting
or reinstalling the Grimoire.

Known official OpenAI `gpt-4o`, `chatgpt-4o`, `gpt-4.1`, `gpt-5`, `o1`, `o3`, and `o4`
families use the built-in exact `o200k_base`/key-only prompt-cache profile. Unknown providers,
endpoints, or models use conservative estimated accounting and no prompt-cache directive.

`DefaultModel`/`FastModel` must match a `models` entry on some provider — matching is a case-insensitive **exact** match, with no bare-name or tag-stripping fallback. OpenAI-compatible `endpoint`s usually include `/v1`. **MCP servers** are wired via `~/.config/arcanum/mcp.json` (`mcpServers` schema) over **stdio** (`command`/`args`, with an optional `inheritEnv` allowlist for `npx`-style launches) or **Streamable HTTP** (`type: "http"` or a bare `url`, SSRF-guarded and `https`-by-default); workspace-local `mcp.json` is merged only after `POST /api/mcp/trust-workspace`. See [Compendium's complete configuration reference](Compendium.README.md#complete-configuration-reference); MCP transport limits are code-owned.

### Local Grimoire reinstall

Arcanum has no supported user-data migration path between incompatible local schemas. A developer
database created before the current schema must be recreated: stop every Arcanum host and daemon,
back up anything needed, delete the database and its WAL/SHM sidecars, and restart. A database
created by the current schema needs no reinstall.

**A reinstall is required now.** `Entries` gained a `Sequence` column and a unique
`(SessionId, Sequence)` index, which give a session's transcript an explicit append order instead of
inferring one from timestamps that a prompt and its answer share. Existing rows never recorded that
order, so there is nothing to backfill and the database is recreated instead
([DESIGN §5.4.1](Arcanum.DESIGN.md#541-grimoire-data-model), [§5.4.5](Arcanum.DESIGN.md#545-schema-installation-serialization-and-crash-consistency)).

macOS/Linux (Bash):

```bash
rm -f -- "$HOME/.config/arcanum/arcanum.db" "$HOME/.config/arcanum/arcanum.db-wal" "$HOME/.config/arcanum/arcanum.db-shm"
```

Windows (PowerShell):

```powershell
Remove-Item -Force -ErrorAction SilentlyContinue `
  "$HOME\.config\arcanum\arcanum.db", `
  "$HOME\.config\arcanum\arcanum.db-wal", `
  "$HOME\.config\arcanum\arcanum.db-shm"
```

There is intentionally no data migration or EF-model regeneration for this raw-SQL accounting
table. See [DESIGN §5.4.5](Arcanum.DESIGN.md#545-schema-installation-serialization-and-crash-consistency)
and [§22.2](Arcanum.DESIGN.md#222-cost-tracking-and-budget-enforcement-arcanumcost).

### Encrypted blob key, backup, and recovery

Session attachments, `/v1/files` uploads, and batch input/output/error files are never stored as
plaintext under `attachments/` or `files/`. Arcanum streams them through a versioned `ARCABLOB`
AES-256-GCM envelope with independently authenticated bounded chunks. Downloads and batch readers
authenticate each chunk before returning plaintext, and batch output uses encrypted staging—there
is no plaintext JSONL temp.

The independent 256-bit file-encryption master key is stored primarily in the operating system's
secret storage:

- service `arcanum`, account `file-encryption-master-key`;
- macOS Keychain on macOS;
- Windows Credential Manager on Windows; and
- Secret Service/libsecret on Linux.

First startup creates the key only after the OS store accepts it. Arcanum also attempts to write
`~/.config/arcanum/file-encryption-key.dat`, sealed with the local Data Protection key ring, as a
recovery mirror. The mirror is not a substitute for OS key storage during normal writes. The
file-encryption key is separate from both `master-api-key` and the Grimoire encryption secret;
`arcanum key show` never displays it and API-key rotation does not rotate it.

Stop every Arcanum host/daemon before copying the persistence tree. A recoverable backup must
capture one consistent generation of:

- `arcanum.db`, its `-wal`/`-shm` files when present, and `arcanum.db.kdf`;
- `attachments/` and `files/`;
- the OS credential `arcanum/file-encryption-master-key`, or
  `file-encryption-key.dat` as its portable recovery copy; and
- the matching `~/.config/arcanum/keys/` Data Protection key ring when relying on that mirror.

Restore the key or mirror+key-ring before starting against restored ciphertext. If encrypted blobs
exist but the key is missing, corrupt, or has the wrong key id, Arcanum fails closed and never
generates a replacement. `/api/health` and `arcanum doctor` expose a `FileEncryption` check with
key availability plus bounded encrypted/legacy-plaintext/corrupt counts, but never key or content
data. Legacy plaintext blobs are detected and never silently served; before upgrading an old
installation, export needed content with the old version and re-upload/re-attach it under the new
version, or remove the legacy files after backing them up. Full format and atomicity details:
[DESIGN §5.4.6](Arcanum.DESIGN.md#546-versioned-authenticated-blob-storage).

### Optional HTTPS

HTTP remains the default on **loopback**. `Arcanum:Host:Https:Enabled` adds a TLS listener; with `Arcanum:Host:ListenAny` / `ARCANUM_HOST_ANY`, HTTPS is **required and exclusive**. A PFX password comes from the exact `CertificatePasswordEnvironmentVariable`, or `ARCANUM_HTTPS_CERTIFICATE_PASSWORD` when that reference is omitted; PEM ignores it. Values never enter configuration or API/Compendium responses. Clients do not bypass TLS validation. PFX vs PEM shapes and Compendium self-signed generation: [Compendium's complete configuration reference](Compendium.README.md#complete-configuration-reference) / [secrets and HTTPS](Compendium.README.md#secrets-and-https).

---

## Distribution and first run

Windows and Linux packages contain separate archives for Arcanum, Compendium, and The Forge plus
`SHA256SUMS`. The `arcanum` executable is Native AOT; desktop apps are self-contained multi-file
Avalonia folders. These archives are unsigned by default. Windows SmartScreen can warn; optional
Authenticode requires the Windows packager's `-Sign` flag and `WINDOWS_CERT_PATH` /
`WINDOWS_CERT_PASSWORD`.

Linux:

```bash
tar -xzf arcanum-linux-x64.tar.gz
chmod +x arcanum-linux-x64/arcanum
./arcanum-linux-x64/arcanum serve
./arcanum-linux-x64/arcanum key show
```

Windows:

```powershell
Expand-Archive .\arcanum-win-x64.zip -DestinationPath .
.\arcanum-win-x64\arcanum.exe serve
.\arcanum-win-x64\arcanum.exe key show
```

Run as a normal user; elevation is not required. Launch The Forge and Compendium from their
extracted archives. Linux shared key discovery requires `libsecret` and a running Secret Service;
otherwise The Forge prompts for a key or accepts process-only `THEFORGE_ARCANUM_KEY`.

Local package creation:

```bash
./scripts/packaging/linux/package-linux.sh --version 0.1.0-beta.1 --output-dir ./dist
```

```powershell
.\scripts\packaging\windows\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist
```

Use `-SkipForge` for Windows Arcanum + Compendium only. Cross-OS builds are manual GitHub workflows:
`Private beta release (Windows / Linux)` builds all three products; `Build Windows x64 (Arcanum +
Compendium)` omits The Forge.

The manual **Release macOS arm64** workflow builds on `macos-15-xlarge`, signs with a Developer ID
Application certificate, notarizes all outputs, and creates or updates a draft GitHub Release.
Required repository secrets are `APPLE_CERTIFICATE`, `APPLE_CERTIFICATE_PASSWORD`,
`APPLE_SIGNING_IDENTITY`, `APPLE_ID`, `APPLE_TEAM_ID`, and `APPLE_APP_SPECIFIC_PASSWORD`. Enter a
version such as `0.1.0-beta.1`; build metadata is rejected. Outputs are:

- `arcanum-osx-arm64.zip` — signed, notarized folder-based self-contained CLI plus this document as
  `README.md`; zip is not stapled;
- `compendium-osx-arm64.dmg` — signed, notarized, stapled `Compendium.app`; and
- `the-forge-osx-arm64.dmg` — signed, notarized, stapled `The Forge.app`.

Signing is mandatory in CI; `--skip-sign` is only for local package-structure smoke tests. Spot-check
the draft on a clean Mac, then publish it. Rerunning the same version replaces its release assets.
Full distribution contracts are in [DESIGN §19.12](Arcanum.DESIGN.md#1912-build-packaging-and-maintenance).

## Current operator limitations

- Tool-child filesystem confinement uses deprecated Seatbelt on macOS and AppContainer plus Job
  Objects on Windows; Linux fails closed unless unsandboxed process tools are explicitly
  acknowledged. No platform provides child-process network isolation.
- `workspace_check` is advertised only on an eligible macOS host and remains unavailable on
  Linux/Windows.
- sqlite-vec is not shipped by default. Managed SIMD Divination is functional but scans at most
  50,000 rows; `/api/meta`, health, and `arcanum doctor` report the active mode and budget.
- OpenAI support is a compatibility subset. Moderation, image-generation/editing, and audio routes
  return `501 not_supported`; batch processing supports `/v1/chat/completions` and forces tools off.
- Durable recovery is single-host and handler-driven, not a distributed workflow engine. Live
  streams and Wards remain ephemeral. A deferred or unsupported/corrupt checkpoint is explicit
  `ReconciliationRequired`/Degraded health and is repaired with `arcanum operation ...`.
- Subagents are intentionally one level deep and model-only. They inherit no parent transcript,
  session memory, workspace/Codex/RAG context, or tools; the parent must pass self-contained
  instructions and any file content explicitly. A crashed `subagent` durable operation is
  abandoned safely rather than replayed.

---

## Build, test & verify

Run from the repository root. The focused test projects run on the normal CLR; the final script checks the Native AOT publish closure for first-party trim/AOT warnings.

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
```

Reliable-editing-loop focused filters and platform notes are in [DESIGN §13.6](Arcanum.DESIGN.md#136-reliable-editing-loop-contract-matrix). Do not use `workspace_check` as the bootstrap verifier for an untrusted repository: it executes repository-authored code and itself requires an eligible macOS runtime plus an operator Ward.

---

## CLI quick reference

All commands run as `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- <cmd>` in development, or `arcanum <cmd>` after an AOT publish.

**Default command:** bare interactive `arcanum` (no arguments) opens the **Command Center** (Terminal.Gui fixed-viewport TUI). Bare non-interactive `arcanum`, or `ARCANUM_NO_COMMAND_CENTER=1`, prints usage and exits **0**. Explicit commands (`serve`, `ask`, `chat`, `--help`, …) stay frameless Spectre/CAF as before.

**Command Center:** interactive Terminal.Gui workbench (sessions sidebar, transcript, composer, HITL/Ward hard modals). Bare interactive `arcanum` opens it; non-interactive / `ARCANUM_NO_COMMAND_CENTER=1` → usage. Slash allowlist and attach flows: [DESIGN §4.4](Arcanum.DESIGN.md#44-retrodownfallarcanumcli-console-executable).

Session branching is first-class in Command Center. `/fork` copies the complete active session;
select a transcript entry and use `/fork at` for an inclusive cutoff branch. Select an assistant
answer and use `/fork alternative` to branch before it and regenerate from the preceding user
prompt; generation starts only after the new branch opens. `/branch parent` and `/branch child`
move through visible lineage. A compact `⑂` marker identifies branches in the header and session
pane without changing its newest-updated-first order. Large attachment-bearing forks require
`/fork confirm`. The new branch is opened only after its transcript and attachment metadata reload;
any fork failure leaves the original session unchanged.

Persistent session context is managed with `/context`, `/context pin <kind> <target>`, and
`/context unpin <pin-id>`. Kinds are `file`, `directorySnapshot`, `symbolRange`
(`path:start-end`), `sessionEntry`, `attachment`, `url`, and `diagnostic`. Pins survive host and
session restarts. File pins retain a SHA-256 version and are re-read on every turn; modified,
deleted, inaccessible, or workspace/symlink-escaping targets are shown to the model with an
explicit stale/error status rather than silently reusing bytes. Directory snapshots and all other
pins have deterministic count/byte limits. Materialized values are source-labeled untrusted data,
participate in normal context/mana estimates, and do not change transcript `Entries.IsPinned`
compression behavior. Existing `@path` text/image staging remains unchanged and turn-scoped.

**Ephemeral reasoning:** `ask` and `chat` render client-safe reasoning in a dimmed, labeled block separate from the Mage answer; their reasoning buffer has a 64 KiB default cap with an explicit truncation marker, and the live `chat` layout coalesces reasoning on the same refresh cadence as answer tokens. Command Center stops its synthetic Thinking indicator and refreshes the header exactly once on the first token or reasoning frame, coalesces reasoning into one separately bounded in-memory `Reasoning (ephemeral)` entry, and preserves both the source entry and exact line offset of a scrolled multiline viewport. Reasoning is never appended to stdout answer text, mana totals, structured output, or reloaded session history.



**Operator communication tools (canonical catalog):** `ask_human` (attended streaming only — wait for operator), `petition_dungeon_master` (async Apprentice escalation; may send Critical Comm Link), `send_commlink_alert` (one-way external notification; no replies). Comm Link webhooks receive generic JSON (`title`/`body`/`severity`/`source`/`timestampUtc`) — Telegram/WhatsApp need a relay.

**Auto-start serve:** interactive Command Center / `chat` / `ask` spawn `arcanum serve` on definite no-listener (refused), wait ~20s for authenticated health. Disabled via `ARCANUM_NO_AUTO_SERVE=1`. Never auto-acks ListenAny. Bootstrap log: `~/.config/arcanum/logs/auto-serve-bootstrap.log`. Key via `arcanum key show`.



| Command | Purpose |
|---------|---------|
| *(bare)* | Open Command Center (interactive TTY). Non-interactive / `ARCANUM_NO_COMMAND_CENTER=1` → usage, exit 0. |
| `serve` | Run the host (default loopback :5001). ListenAny is HTTPS-only + first-run ack. Auto-launched suppresses key print. Details: [DESIGN §5](Arcanum.DESIGN.md#5-hybrid-hosting-model).
| `ask <prompt>` | Single-turn inference (NDJSON stream). Flags: `-n` / `--new` (new session), `-m <model>`, `-c` / `--campaign <id>`, `--unattended`, `--image <path>` (repeatable — attach a Scrying focus; requires a vision-capable model), plus inference flags (below). Use `--` to pass a prompt that starts with a flag. Ctrl+C cancels the in-flight turn (exit 130). Running `ask` before a key is stored exits **1** with a friendly "run `arcanum serve` once" message (no crash). Interactive sessions auto-start `serve` when the API is unreachable (see above). |
| `chat` | Interactive multi-turn REPL (Figlet banner, Markdig rendering, mana bar, live multi-panel layout on wide color terminals). Flags: `-n` / `--new`, `-m`, `-c` / `--campaign <id>` (shown in the startup banner when set), `--no-tools`, `--unattended`, plus inference flags. **Slash commands:** `/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model [name]`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/memory`, `/summary`, `/mana`, `/attach`. Stage text files inline with `@path`; an `@path` whose extension is an image type (`.png`/`.jpg`/`.jpeg`/`.gif`/`.webp`/`.bmp`) stages a **Scrying focus** instead (prints `Scrying focus: <name> (<size>)`; requires a vision-capable model). The mana bar shows a persistent **(Memory Compressed)** suffix after read-time compression until `/new`. Auto-starts `serve` when needed (see above). Narrow / redirected / `NO_COLOR` sessions keep the simple streaming path. |
| `look` | Print the Eye of the World workspace snapshot (no HTTP). |
| `doctor` | Environment diagnostics (System / Paths / Configuration / MCP / Tokenizer / File Encryption panels) + API health probe, including key availability, encrypted/legacy/corrupt blob counts, and the safe `DurableOperations` reconciliation detail. The probe uses a code-owned short timeout; an unreachable API is a non-fatal warning (still exits 0 unless another check fails). Use `--fix-permissions` to apply owner-only permissions to the Grimoire database, `arcanum.json`, and secret store. Use `--json` to emit a structured `DoctorReport` to stdout for programmatic consumption (exit code 0 if healthy, 1 otherwise). |
| `key show` | Print the stored master API key from the OS credential store (with `security.dat` fallback) to **stderr**. CLI-only; no HTTP. |
| `key set` | Store a master API key into the OS credential store (mirrors to `security.dat`). Argument or stdin / interactive secret prompt. |
| `key provider set\|status\|delete perplexity` | Manage the Perplexity key used by native `web_search`. Status never prints the secret; all operations are CLI-only and perform no HTTP. |
| `lore list\|get\|set\|delete` | Operator key-value memory via `/api/lore` (needs `serve`). Args: `get <KEY>`, `set <KEY> <VALUE>`, `delete <KEY>`. |
| `daemon install\|uninstall\|status` | OS background-service lifecycle. |
| `daemon jobs\|initiative\|alert` | Unseen Servant inspection + Comm Link smoke test (needs `serve`). `daemon jobs` shows **Last run** / interval from persisted watermarks (survive restart), **Next due** reconstructed from watermark + interval, and **Last result** (process-local diagnostic text). `daemon initiative <JOB_NAME> <MINUTES>` sets adaptive interval. `daemon alert <MESSAGE>` options: `--title`/`-t` (default `"Arcanum alert"`), `--severity`/`-s` (`Info`\|`Warning`\|`Critical`, default `Warning`), `--source`. |
| `campaign list\|get\|create\|update\|delete\|export\|import\|codex\|spells\|prompts\|sessions` | The Forge campaign registry via `/api/campaigns` (needs `serve`). `create --name <n> --path <p> [--type <t>]`; `export`/`import <id>` round-trip JSON (stdout/`--output` or `--file`); `codex get\|put\|delete <id>` manages `CODEX.md`; `spells\|prompts\|sessions <id>` list campaign-scoped resources (campaign spells shadow built-ins of the same name). |
| `session divine <QUERY>` | Session Divination — semantic search over Grimoire entries via `POST /api/sessions/divine` (needs `serve`; disabled by default — requires `Arcanum:Features:Embeddings` + `Arcanum:Features:SessionSearch`). Options: `--limit <n>`, `--campaign <id>`, `--status <status>`. See [Arcanum.DESIGN.md §21.6](Arcanum.DESIGN.md#216-session-divination). |
| `saga list\|divine\|delete\|stats` | Saga long-term associative memory via `/api/saga/*` (needs `serve`). `list` (options `--query`, `--session`, `--limit`, `--offset`) and `stats` are always available; `divine <QUERY>` (option `--limit`) requires `Arcanum:Features:Embeddings` + `Arcanum:Features:Saga`; `delete <ID>` removes a single memory. See [Arcanum.DESIGN.md §21.8](Arcanum.DESIGN.md#218-saga-long-term-associative-memory). |
| `spell list\|get\|create\|update\|delete\|search\|validate\|execute\|versions\|export\|import\|cast\|clone` | The Forge spell CRUD + execution via `/api/spells` (needs `serve`). `create`/`update` require `--workspace`; `--body`/`--goal`/`--template`/`--plan`/`--inquisitor` accept inline text or `@filename`; `execute` prints the response text plus a tool-call summary (stderr) when tools ran (`--version` takes a **string label**); `cast <name>` is a dry-run system-prompt preview — no inference tokens consumed; `clone <name> --new-name <n>` clones a spell into the workspace. |
| `spell version create\|update\|activate` | Named spell version files (`SPELL.v{label}.md`) via `/api/spells/{name}/versions` (needs `serve`). `create`/`update <name> --version <label> --body <text\|@file>`; `activate <name> --version <label>` swaps the version into `SPELL.md`, printing where the previous content was preserved. |
| `prompt list\|get\|versions\|create\|update\|delete\|render\|test\|execute\|export\|import\|clone` | The Forge prompt CRUD + rendering via `/api/prompts` (needs `serve`). `render`/`execute` accept repeatable `--param key=value`; `test` assembles the system prompt at no LLM cost; `clone <id> --new-name <n> --new-version <v> [--campaign <id>]` copies to a new name/version. |
| `ward list\|get\|resolve` | Ward approval gates via `/api/wards` (needs `serve`). `resolve <id>` requires exactly one of `--allow`/`--deny` plus optional `--reason`. |
| `trial run` | The Proving Grounds via `POST /api/proving-grounds/trials/run` (needs `serve`). `--target spell\|prompt\|apprenticeGoal` + `--target-value`, repeatable `--inquisitor` (JSON or `@file`) and `--var key=value`; exits `1` when the Trial fails. |
| `apprentice list\|get\|create\|delete\|start\|pause\|resume\|cancel\|reweave\|intervene\|cast\|chronicle` | The Forge Apprentice orchestration via `/api/apprentices` (needs `serve`). `create --goal <text\|@file>`; `reweave --plan <json\|@file>`; `cast` reports 409 `Apprentice.ConclaveDisabled` when `Arcanum:Features:Conclave` is off; `chronicle <id>` streams live SSE events (Ctrl+C exits 130). |
| `model list` | List configured models across all providers via `GET /api/models` (needs `serve`); endpoint redacted. |
| `provider list` | List configured providers via `GET /api/providers` (needs `serve`); endpoint redacted and only the credential environment-variable reference returned. |
| `operation list\|show\|cancel\|retry\|reconcile` | Inspect and repair the durable operation ledger via authenticated `/api/operations*` routes (needs `serve`). `list` accepts `--kind` / `--state`; `show <id>` returns only safe checkpoint presence/version/summary; `cancel <id>` requests `Cancelling`; `retry <id>` resets failed/abandoned/repair-required work; `reconcile` runs a bounded pass and exits 2 when operator attention remains. |
| `browse <url>` | Compatibility CLI for the legacy `browse_web` direct-invoke surface (requires `Arcanum:Features:WebBrowsing`; needs `serve`). New inference toolsets use `read_url`. |

**Inference flags** (`ask`/`chat`): `--temperature`, `--top-p`, `--max-tokens`, `--seed`, `--stop`, `--response-format`, penalties, `-c`/`--campaign`. Scrying: `ask --image` / chat `@path`. Full slash-command suite and error formatting: [DESIGN §4.4](Arcanum.DESIGN.md#44-retrodownfallarcanumcli-console-executable).
