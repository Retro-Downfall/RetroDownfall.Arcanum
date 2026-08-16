# Retro Downfall Arcanum

> **Agent orientation document.** This README gives an AI coding agent or operator the shortest useful context for Arcanum. **[`Arcanum.DESIGN.md`](docs/Arcanum.DESIGN.md)** is authoritative for architecture, persistence, runtime behavior, packaging, and testing. Exact HTTP contracts belong to [`Arcanum.API.md`](docs/Arcanum.API.md). Complete CLI syntax, options, aliases, interactive commands, output modes, and exit behavior belong to [`Arcanum.Command.Reference.md`](docs/Arcanum.Command.Reference.md). **[`Compendium.README.md`](docs/Compendium.README.md#complete-configuration-reference)** is the only complete configuration reference, and **[`Arcanum.Design.Human.md`](docs/Arcanum.Design.Human.md)** is the human-readable navigation companion.

**Arcanum** is a **.NET 10, local-first AI assistant and inference hub.** The `arcanum` executable runs either as the long-lived HTTP host (`arcanum serve`) or as thin terminal clients (`run`, `watch`, `look`, `lore`, `daemon`, `campaign`, `session`, `memory`, `saga`, `spell`, `prompt`, `ward`, `trial`, `apprentice`, `model`, `provider`) over the same API. Windows and Linux ship the CLI/host as one self-contained Native AOT executable, and so does macOS arm64 when LLVM `lld` is installed (`brew install lld`) — Apple's own linker asserts on an object file this large, so the AOT link is routed through `ld64.lld`; without it the signed, notarized release degrades to a folder-based self-contained publish. Arcanum exposes an **OpenAI Chat Completions compatibility subset**, routes inference across OpenAI-compatible HTTP providers (including Ollama through `/v1`) and — opt in — across the Claude Code and Codex CLIs you already have installed, and persists state in an encrypted SQLCipher store.

Arcanum's default product posture is an unrestricted coding harness: let the agent keep working, using tools, and reporting progress until the task completes or the operator cancels. Arcanum does not stop ordinary work because an expected duration, turn count, hop count, retry count, or total repository size was exceeded. Provider/model facts, explicit operator budgets, authentication, containment, SSRF defenses, Wards/Sanctum, integrity/protocol checks, single-allocation protection, concurrency admission, and post-cancellation cleanup remain authoritative. Ctrl+C cancels the current CLI turn; Command Center returns to its composer after cleanup, while non-interactive streams exit 130. Durable work uses its explicit cancel command and reports saved/checkpointed state.

- **Stack:** .NET 10 · ASP.NET Core Minimal API · Native AOT on Windows/Linux · `Microsoft.Extensions.AI` · EF Core 10 + hermetic SQLCipher 4.17.0 on SQLite 3.53.3, built from pinned sources with statically linked OpenSSL 3.5.7 · System.CommandLine 2.0.10 + Spectre.Console
- **Version:** `0.1.0-beta` (see [`Directory.Build.props`](Directory.Build.props))
- **Audience for the code:** senior C#/.NET engineers and coding agents extending an AOT-constrained, API-first system.

---

## The standards (read this first)

These are **non-negotiable** and define what "correct" means in this repo. Every prompt you write and every change you make must hold the line on all of them. They are the reason many "obvious" approaches (reflection-based JSON, `AIFunctionFactory.Create`, anonymous DTOs, inline `<script>`) are **wrong here**.

### 1. Native AOT compatibility (hard constraint)

Windows/Linux ship a **Native AOT** binary with **zero runtime prerequisite**. macOS ships the same Native AOT host when LLVM `lld` is installed and uses a folder-based self-contained fallback without it. The shared host remains AOT-constrained: minimal reflection, source generation, and an AOT warning gate still dictate serialization and binding. See [DESIGN.md §9](docs/Arcanum.DESIGN.md#9-native-aot-and-trimming).

- **Source-generated JSON only.** Every HTTP payload type must have a `[JsonSerializable]` registration on **`ArcanumJsonContext`** (Api). Other contexts are scoped: `GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext` (Core — Grimoire blobs, `arcanum.json`, campaign/skill metadata), `McpJsonSerializerContext` / `McpConfigJsonSerializerContext` (Infrastructure, JSON-RPC + `mcp.json`), `CommLinkInfrastructureJsonContext` (outbound webhooks), and `CliJsonContext` (CLI process envelopes). **Never** use reflection-based `JsonSerializer` overloads, `PostAsJsonAsync` with anonymous types, or `Results.Json` without an explicit `JsonTypeInfo`.
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

Arcanum exposes a **Chat Completions compatibility subset** so common OpenAI clients work for chat. See [API §8.8](docs/Arcanum.API.md#88-openai-v1-chat-completions-compatibility-subset). Moderations/images/audio remain `501 not_supported`.

- **`POST /v1/chat/completions`** (JSON or SSE) and **`GET /v1/models`** (auto-discovery across all configured providers).
- Request parsing including multimodal `content` parts, `tool`/`assistant` tool-call replay, `stream_options.include_usage`, `response_format`, etc.
- Responses carry `usage`, `system_fingerprint`, and OpenAI-shaped error envelopes. **Auth** accepts `Authorization: Bearer <KEY>` for OpenAI clients (as well as `X-Arcanum-Key`).
- Arcanum runs **its own server-side MCP toolset** by default, so client-supplied `tools`/`tool_choice` are rejected with `400 unsupported_parameter` (except `tool_choice: "auto"`/`"none"`, which are always accepted as OpenAI defaults). Operators may opt in to **client tool forwarding** via `Arcanum:Features:ClientTools`; when enabled, client schemas are forwarded to the resolved provider (per-tool `strict` flag preserved via `AIFunction.AdditionalProperties`), `tool_choice.function.name` is verified against the supplied `tools`, and the returned `tool_calls` are surfaced for the client to round-trip (bypasses Arcanum's server-side tool loop, Sanctum, Wards, and tool audit logging).

### 4. Top-of-the-line, all-native multi-provider inference engine

Inference flows through one hub behind a single `IChatClient` abstraction. See [DESIGN.md §10](docs/Arcanum.DESIGN.md#10-intelligence-pipeline); the exact turn order is [§10.7](docs/Arcanum.DESIGN.md#107-end-to-end-turn-lifecycle-and-chat-loop).

- **`WizardIntelligenceProvider`** + **`ToolExecutionPipeline`** + **`IChatClientFactory`**; providers are **`OpenAICompatible`** (including Ollama via `/v1`) or a **Familiar** — `ClaudeCodeCli` / `CodexCli`, your own installed CLI on your own subscription. Still no managed local inference: a Familiar is a transport Arcanum invokes, never a runtime it installs, configures, or signs into.
- **`TurnEngine` is a progress-driven semantic shell** over Wizard's `ITurnPipelineRunner`; Wizard still owns the one mode-parameterized model/tool loop. There is no Arcanum-owned total turn duration or fixed model-call, tool-round, correction, or retry count — with one exception: a Familiar is a spawned process rather than a socket, so its runner enforces a code-owned wall-clock ceiling and kills the process tree instead of holding a request open forever. Work continues while evidence changes and stops for completion, cancellation, explicit token/cost policy, a provider/model boundary, a required safety/integrity denial, or deterministic repeated no-progress. The primary loop can call native `delegate_task` to start a fresh buffered child TurnEngine with a sterile stateless context, any number of explicit file values that fit the retained per-file/parent/provider boundaries, and a delegated token or cost ceiling. Child tools are disabled, so recursive delegation is unavailable by construction. Only the child summary or structured failure returns to the parent.
- **`ProviderResolver`** maps model → provider from `Arcanum:Providers` (no hard-coded model names).
- Agentic layers: MCP tool loops, semantic spell routing, read-time context compression, Wards, Sanctum.
- **Session attachment retrieval:** when `Arcanum:Features:AttachmentRetrieval` is enabled, supported UTF-8 text/Markdown/source/JSON/YAML/XML/CSV/log attachments and allocation-safe visible HTML are indexed per version and retrieved only inside their owning session. One per-turn materialization ledger deduplicates current attachments, references, pins, model attach/refresh calls, attachment/workspace RAG, Saga, and The Tapestry; explicit whole files suppress equivalent semantic chunks, and refreshes replace stale versions before continuation. Provider context remains the final per-request authority; indexing uses internal slices and reconciliation rather than public retry/timeout/count knobs. Latest Bound versions are preferred; historical provenance is retained; PDFs, Office files, binaries, and images remain unindexed. Queue/provider failures never fail the turn.
- **The Tapestry (hierarchical memory):** when `Arcanum:Features:Tapestry` is enabled, a background sweep weaves RAPTOR-style summary trees over the corpora the other RAG features already index — workspace chunks, session attachment chunks, and session history. Leaf chunks are embedded, clustered with a deterministic pure-managed spherical K-Means, summarized by the fast model, and recursed into higher layers, so retrieval can answer corpus-level and multi-hop questions that flat top-K chunk search cannot. Trees are derived data published as immutable generations behind one atomic switch: a failed or interrupted build never replaces the last good tree, and a scope with no published generation simply contributes nothing. Retrieved nodes are injected as untrusted DATA under `### Hierarchical Context (The Tapestry)`, with lineage-aware suppression so a summary and one of its own descendants never both occupy the turn's budget. Summary spend goes through normal cost accounting. See [DESIGN.md §21.11](docs/Arcanum.DESIGN.md#2111-the-tapestry-hierarchical-memory).
- **Structured output / pricing / budgets / capability-driven provider prompt caching / guardrails** — see [DESIGN.md §22](docs/Arcanum.DESIGN.md#22-structured-output-cost-tracking-and-prompt-caching) and [API §8.27](docs/Arcanum.API.md#827-content-guardrails-pii--toxicity--topics). Arcanum never caches or replays inference responses.

### The Proving Grounds

Ephemeral Trials via `POST /api/proving-grounds/trials/run` (regex / jsonSchema / semantic Inquisitors). Desktop UI: [DESIGN.md §19.10](docs/Arcanum.DESIGN.md#1910-desktop-vocabulary-and-implemented-surfaces). Server behavior: [§20](docs/Arcanum.DESIGN.md#20-the-proving-grounds--trials-and-inquisitors).

### 5. Local-first security posture

Single-user, loopback-by-default, secret-minimizing. See [DESIGN.md §11](docs/Arcanum.DESIGN.md#11-local-api-security).

- Kestrel binds **loopback only** unless explicitly opened; a **32-byte master API key** guards every `/api` and `/v1` route; the **Grimoire** is encrypted at rest (SQLCipher passphrase derived via PBKDF2-HMAC-SHA256 with a unique 16-byte salt stored in `{grimoire.db}.kdf`). Session attachments, uploaded files, and batch artifacts outside SQLCipher are independently protected by versioned, chunk-authenticated AES-256-GCM envelopes.
- Sensitive files (`arcanum.json`, Grimoire `.db`, `cli-context.json`, `cli-session.txt`, logs) are created **owner-only** (`chmod 600/700` on Unix; owner ACL on Windows). Startup warns if group/other can read them.
- `Arcanum:Host:ListenAny` requires **first-run acknowledgement** in interactive `serve` (or `ARCANUM_LISTEN_ANY_ACK=1` / `ARCANUM_HOST_ANY` for automation) and emits a **security banner** when binding all interfaces over **HTTPS only** (plaintext any-IP HTTP is refused; `Arcanum:Host:Https:Enabled` + cert required).
- `WorkspacePathPolicy` containment, symlink walking, and handle-identity revalidation are the primary boundary for file/search/patch tools; campaign Sanctum is an additional conditional allowlist. Shared `SecureFileReader` opens no-follow/nonblocking, accepts only regular single-link files, reads through cleared capped pools, and revalidates identity; FIFO/device/hardlink/symlink inputs fail closed. Host-process tools (`execute_command` / `run_spell_script`) use `ArgumentList` (no shell) with child-env scrubbing and are **gated by Local edition** unless Development + `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1`; workspace MCP requires trust bound to the exact approved bytes. Oversized `execute_command` output keeps only a bounded response preview in memory while streaming complete stdout/stderr to private connection-scoped artifacts; attuned `read_command_output` pages them through opaque handles, and cancellation/failure/connection close removes them. **Tool-child FS jail** is filesystem-only: macOS uses Seatbelt, Linux remains inactive/fail-closed, and Windows uses a per-invocation AppContainer identity with explicit allowed-root ACLs plus Job Object process-tree/resource enforcement. The Windows broker confirms Job membership before resuming the suspended untrusted target; capability or setup failure fails closed, and health/doctor are Healthy only when AppContainer is genuinely available. Owner-only temp artifacts are deleted only after identity-safe quarantine checks. `workspace_check` is stricter and separate: advertised only on an eligible macOS Seatbelt host, never enabled by `AllowUnsandboxedToolChildren`, and unavailable on Linux/Windows. Its child additionally needs the two shared `/private/tmp/.dotnet` PAL inter-process roots, because macOS no longer allows a private per-run group container ([DESIGN §11.7.1](docs/Arcanum.DESIGN.md#1171-shared-net-inter-process-roots-for-workspace_check-macos)); eligibility now probes them, so an unusable host reports the reason instead of failing at invocation. SSRF guard + DNS-rebind pinning on untrusted egress; sanitized public error envelopes. Details: [DESIGN §11](docs/Arcanum.DESIGN.md#11-local-api-security).

### 6. Strict Content Security Policy on every web surface

**First-party browser UI must externalize scripts and styles** (JS in `.js` files, CSS in `.css` files — no inline first-party code). The opt-in **Scalar** UI (`Arcanum:Features:ScalarUi`) is a third-party exception served under the same-origin CSP documented in [DESIGN.md §11.5](docs/Arcanum.DESIGN.md#115-openapi-and-scalar).

### 7. C# house style

- **One blank line after each line of C# code** (visual breathing room) — applied throughout the codebase. Within reason. Curly braces do not require blank lines around them. Neither do control statements like if and loops, etc. Also, long-running Linq statements do not require blank lines either.
- File-scoped namespaces; positional records for DTOs/contracts; **no `[JsonPropertyName]`** on `/api` wire types (casing comes from `[JsonSourceGenerationOptions]`); OpenAI `/v1` and MCP JSON-RPC types are explicit exceptions (API §8.2); primary constructors for DI; `IDisposable` where a service owns a `SemaphoreSlim`/`ServiceProvider`. See [DESIGN.md §12](docs/Arcanum.DESIGN.md#12-c-language-and-coding-conventions).

> **Note on org-wide rules:** Corp-wide standards scoped to `Corp.Solution.*` solutions (Dapper + SQL Server stored procedures, the `Corp.Lib.*` NuGet stack, Refit "Service Libraries") **do not apply to Arcanum** — it is local-first over its own EF Core + SQLCipher Grimoire and retains AOT-safe contracts across Native AOT Windows/Linux and self-contained macOS packaging. The always-on house rules (blank lines, strict CSP, docs-in-same-change-set) still hold.

### 8. Thematic naming metaphor (D&D)

Arcanum uses Dungeons & Dragons and/or fantasy metaphors for domain concepts. New features **must** follow it if possible. Current exceptions include "prompt" and "workspace". See [Naming metaphor](#naming-metaphor).

### 9. Docs travel with code

The repository maintains seven canonical docs (`README.md` at the repository root, plus `Arcanum.DESIGN.md`, `Arcanum.API.md`, `Arcanum.Command.Reference.md`, `Arcanum.Design.Human.md`, `Compendium.README.md`, and `Arcanum.DEBUGGING.Human.md` under `docs/`) plus the focused `Arcanum.CHAT-LOOP.md` companion. Architecture, persistence, runtime behavior, testing, and packaging update `Arcanum.DESIGN.md`; API contracts update `Arcanum.API.md`; the complete public configuration contract updates `Compendium.README.md`; CLI command-surface changes update `Arcanum.Command.Reference.md`; agent/operator orientation updates this file; human navigation updates `Arcanum.Design.Human.md`; debugging guides update `Arcanum.DEBUGGING.Human.md`; shared model/tool-loop and attachment continuation changes also update `Arcanum.CHAT-LOOP.md`. Keep the owning documents current in the same change set. See [DESIGN.md §18](docs/Arcanum.DESIGN.md#18-document-maintenance).

---

## Architecture at a glance

**One CLI/host entry point, hybrid process model.** A System.CommandLine 2.0.10 verb selects the role: `serve` (long-running Kestrel host) vs. short-lived commands. See [DESIGN.md §5](docs/Arcanum.DESIGN.md#5-hybrid-hosting-model).

**Explicit application entry.** `arcanum center` and `arcanum open center` reuse the in-process Command Center host. `arcanum open theforge|compendium|session|campaign|spell|prompt|apprentice` resolves any friendly selector first, then launches through a versioned one-argument deep link. Only canonical server-owned identifiers cross the process boundary; API keys, endpoints, prompt/file content, attachments, and server paths do not. Missing desktop applications report every safe candidate plus repository-relative `dotnet run` and current-CLI fallbacks. Windows and Linux release archives extracted beneath one parent are discovered by their shipped sibling folder names and active architecture. See [the command reference](docs/Arcanum.Command.Reference.md#arcanum-open) and [DESIGN §4.4](docs/Arcanum.DESIGN.md#44-retrodownfallarcanumcli-console-executable).

**Primary dependency chain:** `Cli → Api → Infrastructure → Core` (`Cli` also references `Core`/`Infrastructure` directly for lightweight DI). `Infrastructure` also references the isolated `Secrets` project. Strict project boundaries are a deliberate goal.

| Project | Role | Owns | AOT |
|---------|------|------|-----|
| **`Core`** | Domain primitives, contracts, configuration | `Result`/`Result<T>`, `Error`, `ApiResponse<T>`, `ArcanumSettings`, Covenant compiler/digests/linker/admission contracts, `IArcanumIntelligenceProvider`, `PingRequest`, `IGrimoireRepository`, `IEyeOfTheWorld`, events, source-gen contexts (`GrimoireJsonContext`, `ConfigurationJsonContext`, `TheForgeJsonContext`) | `IsAotCompatible` |
| **`Secrets`** | Native credential boundary | macOS Keychain, Windows Credential Manager, Linux Secret Service, fixed Arcanum credential identity | `IsAotCompatible` + `IsTrimmable` |
| **`Infrastructure`** | OS-adjacent services | Serilog, Data Protection, encrypted Grimoire (EF Core 10 + SQLCipher, compiled model), authenticated encrypted blob storage + OS-backed file key, workspace scanning, reliable `search_workspace` / `apply_patch` / `workspace_check` engines, Eye of the World, the **MCP client layer** (subprocess + in-process transports, `ArcanumInternalToolServer`), Comm Link | `IsTrimmable` + `PublishAot` (analysis signal) |
| **`NativeSqlCipher`** | Hermetic SQLCipher delivery (assets only, no code) | One verified, reproducibly built SQLCipher library per shipping RID (`osx-arm64`, `win-x64`, `win-arm64`), the `native-source-manifest.json` provenance it is checked against, upstream licenses and SBOMs, and the MSBuild target that delivers exactly one asset with no fallback | packable; `IncludeBuildOutput=false` |
| **`Api`** | HTTP surface composition (class library, **not** executable) | `MapArcanumEndpoints`, `ApiBootstrapper`, `WizardIntelligenceProvider`, `TurnExecutionCoordinator`/`TurnEngine`, `ToolExecutionPipeline`, `IChatClientFactory`, `SemanticRouter`, built-in `AIFunction` tools, `ApiKeyEndpointFilter`, `ArcanumJsonContext`, `/v1` OpenAI endpoints | `IsAotCompatible` + `EnableRequestDelegateGenerator` |
| **`Cli`** | Shipping CLI/host entry point | Spectre commands, `ArcanumApiClient`, theming, AOT-safe Markdown rendering (`MarkdigSpectreRenderer`) | `PublishAot` on Windows/Linux and macOS with LLVM `lld`; folder-based self-contained macOS fallback without lld |
| **`Api.DevHost`** | Debug-only F5 host (not shipped) | Mirrors `serve` wiring without Spectre | `PublishAot` + `IsAotCompatible` (analysis signal; not shipped) |
| **`tests/RetroDownfall.Arcanum.Tests`** | xUnit test suite (not shipped) | MCP, security, config, workspace policy, SQLCipher Grimoire, and API-host integration tests | — |
| **`tests/RetroDownfall.Compendium.Tests`** (assembly `RetroDownfall.Compendium.Ux.Tests`) | Compendium smoke tests (not shipped) | Round-trip read/write of factual configuration and credential references | — |
| **`Compendium.Ux`** | Desktop configuration editor (Avalonia) | Visual editor for the 13 retained configuration sections; polished Host/Providers/Daemon/CLI/Presets pages plus descriptor-driven pages that refresh after asynchronous loads without rebuild-time file I/O; reuses Core models and edits credential references, never secret values | — |
| **`TheForge.Core` / `TheForge.Ux`** | Desktop Inference IDE (Avalonia) | HTTP-only Arcanum client with bounded buffered/NDJSON/SSE reads and atomic downloads; Campaign/Spell/Prompt/Session workbench, Wards, MCP, Trials, diagnostics | — |
| **`tests/RetroDownfall.TheForge.Tests`** | Forge desktop tests (not shipped) | Client contracts, settings, view models, and source-generated JSON | — |

**Covenant status.** Issue #79 supplies the pure-Core protocol foundation tracked by umbrella issue #74.  Issue #80 supplies the hermetic SQLCipher runtime and central connection authorization foundation. Issue #81 adds the persistence schema: the always-present core support tables, the Covenant canonical and FTS5 accelerator tiers under `Data/Schema/Capabilities/Covenant/`, closed installed-catalog manifests with index-shape validation, a three-transaction installer, and `ICovenantAvailability` health publication. **Issue #82 adds canonical persistence and inspection search**: the generation-bound operation gate that closes and drains admission before any destructive operation, the bounded canonical store, the transactional mutation kernel with its replay ledger, canonical and turn-capacity quotas, owner-deletion cleanup, and the degradable FTS5 accelerator with its bounded canonical fallback. Those components are registered in both host compositions but reached by nothing yet: there is still no public endpoint, CLI command, MCP tool, configuration key, or provider-call wiring, and the feature is not enabled. **Issue #83 adds invocation authority and Campaign binding**: the non-serializable `ArcanumInvocationContext` required at every inference seam, the six-purpose AES-256-GCM envelope protocol and its keyed diagnostic tag, `OperatorAuthorityContextIssuer` as the one place operator authority is minted, canonical Campaign resolution by keyed physical directory identity (replacing `PingRequestResolver`), and an assistant-begin path that honors the immutable Session binding and writes nothing when it cannot. Still no Covenant content is loaded or rendered, no MCP tool is exposed, no configuration key is introduced, and the feature is not enabled. **Issue #84 adds prompt planning and atomic turn publication**: deterministic Confirmed and fenced Proposed placement in `SystemPromptBuilder`, the Core-owned `SystemPromptAttributionMap` that replaces heading-and-fence parsing for Covenant token attribution, the two new `ContextTokenSource` lanes, `ICovenantContextProvider` (one immutable plan and one lease per logical turn), `CovenantAdmissionPlanner` (Confirmed all-or-fail, Proposed longest-prefix pressure), `ICovenantDisclosureJournal` (durable acknowledgement before any dispatch), `ICovenantMutationCollector` (provisional per-branch staging that writes nothing), and `IGrimoireTurnCommitter` (assistant content, the one-shot finalization guard, and any staged batch in one immediate transaction). With no Covenant content admitted, prompt bytes, the DATA `[None]` placeholder, and the cache plan are byte-for-byte identical to before. **Issue #85 adds the MCP mutation and sensitive-egress controls**: `ProviderToolCallBuffer` (streamed tool-name and argument fragments stay private under code-owned bounds until one call is frozen and classified), `CovenantToolInvocationContext` and `CovenantToolCapabilityRegistry` (a single-use, nonce-bound capability carried across the in-process MCP task on the exact connection-and-request id), `CovenantEgressWardPolicy` (attended approval for retirement, resolved against the live invocation, and denied outright when Wards are off), `CovenantToolEgressGuard` (a disclosure receipt commits before every physical attempt, with retries and reconnects counted separately), and the two hand-authored `propose_covenant` / `retire_covenant` tools with typed `structuredContent` and a compact text fallback. Both tools are registered inert and advertised only while the feature and canonical tier are healthy; no turn mints a capability yet, so every call is still refused. **Issue #86 adds derived-output protection and host-process taint**: `ArtifactSensitivityLedger` (the one writer of `artifact_sensitivity` and `session_sensitivity_state`, always inside the caller's own transaction, append-only and refusing every downgrade), `DerivedArtifactWrite` (sensitivity is a required argument, so a new sink cannot be untainted by omission), the assistant-entry label written in the same transaction as the response and its finalization guard, `SessionDerivedArtifactStore` (immutable summary and title artifacts with composite-keyed current pointers, so a mutable column can never outlive the label describing it), `ProtectedAssistantArtifactReader` (artifact and label in one snapshot under a revalidated lease, failing closed on mismatched evidence), `CovenantProtectedLogScope` (a type logs, metrics, and progress cannot smuggle content through), `CovenantDerivedOutputInventory` with the architecture suite that fails when a new sink has no declared policy, and the two-marker host-process-tools taint: `HostProcessToolsTransitionService`, the pure `HostProcessToolsMarkerPairJoiner`, and `HostProcessToolsStartupGate`, which classifies the installation before any pool, key, or Covenant service exists and blocks startup with `Covenant.HostToolsTransitionRequired` on anything it cannot prove. **Issue #88 freezes the public and recovery contracts**: every operator request and response shape with its own `Validate()` and bounded UTF-8 limits (`Covenant*Contracts.cs`), the complete Covenant error vocabulary and its exact HTTP status mapping — which also fixes three shipped codes that were reaching operators as untyped 500s — the five service ports #87 and #89 implement (`ICovenantManagementService` and peers), `CovenantPublicContractInventory` with an architecture suite that fails in both directions and forces every naming-convention exception to be declared with a reason, `CovenantProtectedJsonResult<T>` and `CovenantProtectedStreamResult` (the lease is revalidated before the first byte, the response is `no-store` with its validators stripped, and the lease is released only after serialization), the two durable recovery checkpoints under the Infrastructure-owned `CovenantRecoveryJsonContext`, and the caller-named durable operation identity that finally writes the `long_running_operation_request_identities` table shipped by issue #81. No route is mapped, no command is registered, no port has an implementation, and no configuration key exists. **Issue #87 adds maintenance and protected-erasure recovery**: `CovenantSensitiveArtifactPurgePolicy` (one exhaustive thirteen-kind table that every erasure path resolves through, with `external_disclosure_receipts` and the folded disclosure aggregates deliberately outside it), `CovenantArtifactErasureAuthority` (a closed, nonserializable two-arm capability that is borrowed and revalidated but never acquired, completed, or disposed), `CovenantProtectedArtifactErasureKernel` (every artifact and its exact label reread inside the transaction that deletes them, with the owner scope derived from those rows and never from the request), `CovenantManagedFileErasureKernel` and its shared state machine (durable work item before the first syscall, no-follow open, same-handle ownership verification, compare-delete, parent fsync, and label completion, with the database's own triggers enforcing the label-then-producer-then-work-item ordering), `CovenantLocalErasureStartupRecovery` (the sole pre-readiness adopter of unfinished deletions, under the caller's already-held installation lock and without reconstructing any lost capability), `CovenantSchemaRepairJournal` and `CovenantSchemaRepairStartupRecovery` (committed before the first repair statement, resumed only against the exact catalog digest it recorded), `CovenantExclusiveDisposition` (one commit, rollback, or keep-closed decision per evidence combination, with the one-shot journal finalizer running only after a disposition actually succeeded), and the two durable operation kinds `covenant-index-rebuild` and `covenant-family-reinitialize`, each with exactly one descriptor, handler, checkpoint version, and transition owner. The operator API, CLI, Compendium surfaces, and `Arcanum:Features:Covenant` are issue #89 and later.

**Key entry points to know:** `ApiBootstrapper.AddArcanumApiServices` / `MapArcanumEndpoints` (wire everything), `AddArcanumInfrastructure` (Infrastructure DI), `WizardIntelligenceProvider` (existing inference orchestration and `ITurnPipelineRunner`), `TurnEngine` (bounded semantic shell), and `Cli/Program.cs` (command registration).

### Repository map

```
README.md                                # this agent orientation document (repo + GitHub landing page)
src/
  RetroDownfall.Arcanum.Core/            # domain, contracts, config, source-gen JSON contexts
    ProvingGrounds/                      # Trial / Inquisitor models and IProvingGroundsArbiter
  RetroDownfall.Arcanum.Secrets/         # native OS credential-store implementations
  RetroDownfall.Arcanum.Infrastructure/  # Grimoire, MCP, perception, Comm Link, Serilog
    Generated/                           # EF Core compiled model (commit regenerations)
    Data/Schema/                         # the schema: one object per .sql file, installed at startup
      Tables/                            #   one file per table, its indexes co-located
      FullTextSearch/                    #   one file per FTS5 virtual table
      Triggers/                          #   one file per trigger
      Capabilities/Covenant/Canonical/   #   Covenant's own transaction tier
      Capabilities/Covenant/Accelerator/ #   the FTS5 inspection index, its own tier again
    Data/Migrations/                     # EF design-time scaffolding only — never applied
  RetroDownfall.Arcanum.Api/             # endpoints, intelligence hub, /v1, security filter
    ProvingGrounds/                      # trial/inquisitor endpoint wiring
  RetroDownfall.Arcanum.Cli/             # the `arcanum` executable (Spectre commands)
    Services/Setup/                      # guided-setup state machine, planner, committer, probe
  RetroDownfall.Compendium.Ux/           # desktop `arcanum.json` editor (Avalonia)
  RetroDownfall.TheForge.Core/           # portable Forge client contracts/services
  RetroDownfall.TheForge.Ux/             # desktop Inference IDE (Avalonia)
  RetroDownfall.Arcanum.Api.DevHost/     # debug-only host
tests/
  RetroDownfall.Arcanum.Tests/           # xUnit tests (MCP, security, config, workspace policy, SQLCipher Grimoire)
  RetroDownfall.Compendium.Tests/        # Compendium round-trip smoke tests (assembly: RetroDownfall.Compendium.Ux.Tests)
  RetroDownfall.TheForge.Tests/          # Forge client/UI tests
docs/                                    # canonical documentation and focused companions
  Arcanum.DESIGN.md                      # authoritative technical reference
  Arcanum.API.md                         # native and OpenAI-compatible API reference
  Arcanum.Command.Reference.md           # complete CLI commands, options, aliases, and exits
  Arcanum.Design.Human.md                # non-authoritative human reading companion
  Arcanum.DEBUGGING.Human.md             # verified breakpoint map and debugging recipes
  Arcanum.CHAT-LOOP.md                    # focused model/tool-loop and attachment ordering guide
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

- **Wire envelope.** JSON under `/api` returns `ApiResponse<T>` (`Data`, `IsSuccess`, `Error`, `TraceId`). Map from domain with `ApiResponse<T>.FromResult`. Exceptions: streaming (NDJSON), SSE event buses, and OpenAI `/v1` (raw OpenAI shape). See [API §8.1](docs/Arcanum.API.md#81-wire-contract-the-apiresponset-envelope).
- **Result flow.** Domain ops return `Result` / `Result<T>` and rely on implicit conversions; the endpoint is the single place that turns a `Result` into an envelope + status code.
- **New endpoint checklist:** add to `MapArcanumEndpoints` → return `ApiResponse<T>` (or documented streaming shape) → register every new payload type on `ArcanumJsonContext` → `.WithName(...)` for OpenAPI → use explicit `JsonTypeInfo` on failable `Results.Json` → update DESIGN.md §4.3 + this README's API map.
- **New CLI verb:** add the handler under `Cli/Commands` and wire it in `CliCommandTree`; use `IConsoleDispatcher` for stdout payloads/stderr diagnostics, `IConfirmationPrompt` for destructive approval, an explicit source-generated `JsonTypeInfo` for structured output, and a defined `CliExitCode`. Prefer `AddArcanumEyeOfTheWorld()` over full infrastructure for lightweight verbs.
- **New inference provider:** add an `AiProviderKind` and extend `IChatClientFactory`; keep the `WizardIntelligenceProvider` contract intact. This holds even for a transport that is not OpenAI-compatible HTTP — the Familiar kinds are exactly this pattern and change nothing above the factory. External CLI wire types may carry vendor-chosen names through a source-generated naming policy, the same exception `/v1` and MCP JSON-RPC types get.
- **New MCP tool:** implement on `ArcanumInternalToolServer` with a hand-authored JSON schema via `McpJsonSerializerContext`; honor unconditional `WorkspacePathPolicy` containment and treat `ToolOutputCapBytes` as one response/page allocation, adding an attuned continuation when complete useful output can exceed it; decide whether it belongs in `ToolRiskClassifier.IntrinsicWardToolNames`. Do not treat campaign Sanctum as the primary filesystem boundary.
- **Treat all wire types as versioned contracts.** Casing is fixed at the context level; don't add `[JsonPropertyName]` except on OpenAI `/v1` and MCP JSON-RPC types (see [API §8.2](docs/Arcanum.API.md#82-arcanumjsoncontext--source-generated-public)).
- **Register long-running work.** Use the scoped `ILongRunningOperationCoordinator`; add the kind and exactly one `LongRunningOperationRecoveryDescriptor` to `LongRunningOperationRecoveryRegistry` (`LongRunningOperationPolicyCatalog` projects its policy column, so there is nothing to update there), implement an idempotent recovery handler, register it, store only minimum encrypted checkpoint state, and expose only a bounded safe summary. Never persist a live Task/token/enumerator/process/DI object. `RecoveryHandlerCoverageTests` fails if a kind has no owning handler, and `LongRunningOperationCrashRecoveryTests` replays a crash at every durable step of every kind — so a new kind must be repeat-safe before it can go green. Be explicit about what recovery cannot know: never aggregate a cost, re-bill a provider, or claim a child process or peer task survived. See [DESIGN §10.8](docs/Arcanum.DESIGN.md#108-durable-operation-ledger-and-restart-reconciliation).
- **Change the schema by editing its object file.** The Grimoire schema is a declarative tree under `Infrastructure/Data/Schema/`: one `.sql` file per table (with its indexes co-located), per FTS5 virtual table, per trigger, and per view, embedded by glob and installed fresh. Adding an object is adding a file — no list to update, no numbered script, no `__EFMigrationsHistory`. Arcanum has no users yet, so edit the canonical definition in place and recreate local/test databases; do not add compatibility migrations or in-place upgrade paths. Revisit this policy before durable user data exists. **The file's path picks its install transaction**: directly under a category folder is the startup-blocking core tier, while `Capabilities/Covenant/{Canonical,Accelerator}/<Category>/` is a capability tier that fails on its own without taking startup down. The declared object name must equal the file name, and every statement stays `CREATE ... IF NOT EXISTS`. See [DESIGN §5.4.5](docs/Arcanum.DESIGN.md#545-schema-installation-serialization-and-crash-consistency).

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
| Multi-agent coordination network | **The Conclave** | `cast_sending` tool · `/api/apprentices/{id}/cast` · `arcanum conclave` |
| Agent event stream | **Chronicle** | `/api/apprentices/{id}/chronicle` (SSE) |
| A2A Agent Card | **Heraldry** | `GET /api/conclave/a2a/agent-card` |
| A2A Task (inbound or outbound) | **Sending** (a.k.a. Delegated Quest) | `/api/conclave/a2a/*` · `POST /api/conclave/sendings` · `POST /api/conclave/sendings/{taskId}/continue` · `dispatch_sending` / `continue_sending` tools · `arcanum conclave dispatch|continue` |
| The Conclave's outward-facing A2A delegate | **Archmage Client** | `IA2AClientService`/`A2AClientService`, invoked via `dispatch_sending` / `continue_sending` |
| Human operator | **Dungeon Master (DM)** | — |
| Encrypted persistence store | **Grimoire** | (internal: EF Core + SQLCipher) |
| Durable operator-and-agent standing agreement | **The Covenant** | (public contract frozen in issue #88, maintenance and protected-erasure kernels in issue #87, no operator surface until issue #89; `propose_covenant` / `retire_covenant` MCP tools, `Data/Schema/Capabilities/Covenant/`, `Core/Covenant/`, `Core/DataLifecycle/`, `Infrastructure/Covenant/`) |
| The frozen public shapes, ports, and recovery payloads | **Covenant contract inventory** | (internal: `CovenantPublicContractInventory`, `Covenant*Contracts.cs`, `CovenantRecoveryJsonContext`) |
| One inference call's authority classification | **Invocation context** | (internal: `ArcanumInvocationContext`, required at every inference seam) |
| A Campaign's keyed physical-directory identity | **Canonical Campaign binding** | (internal: `ICanonicalCampaignContextResolver`, `campaign_path_identities`) |
| Background job runner | **Unseen Servant** | `/api/unseen-servant/*` |
| Situational directory perception | **Eye of the World** | `/api/perception/look` |
| Operator key-value memory | **Lore** (legacy) | `/api/lore` |
| Agent-directed entity memory | **The Lexicon** | `scribe_lexicon` / `delete_lexicon` MCP tools; see [DESIGN.md §10.6](docs/Arcanum.DESIGN.md#106-the-lexicon--agent-directed-entity-memory) |
| Operator alert channel | **Comm Link** | `/api/commlink/send` |
| Primary agent / inference orchestrator | **Master** | **`WizardIntelligenceProvider`** (implementation class; implements **`IArcanumIntelligenceProvider`**) |
| An installed, already-authenticated vendor CLI Arcanum calls on for inference | **Familiar** | `AiProviderKind.ClaudeCodeCli` / `CodexCli`; `IFamiliarProcessRunner`, `IFamiliarProbe`; `GET /api/providers/{name}/familiar-probe` (see [DESIGN.md §10.9](docs/Arcanum.DESIGN.md#109-familiars--subscription-backed-cli-transports)) |
| Scratchpad / instructions | **Codex** | `CODEX.md`, `/api/codex` |
| Multi-turn chat thread | **Session** (rows = **Entry**) | `/api/sessions` |
| Spell/prompt/plan validation | **The Proving Grounds** (Trials, Inquisitors) | `POST /api/proving-grounds/trials/run` |
| Embedding & vector substrate | **The Weave** | `Arcanum:Features:Embeddings` plus `Arcanum:Integrations:Embeddings`; see [DESIGN.md §21](docs/Arcanum.DESIGN.md#21-the-weave-divination-and-saga-rag) |
| Semantic search over The Weave | **Divination** | `IDivinationService`; `POST /api/sessions/divine`, `POST /api/workspaces/{id}/files/divine`, `POST /api/saga/divine` (§21) |
| Vector representation of text | **Imprint** | `IWeaveService.EmbedAsync`/`EmbedBatchAsync` ("imprints" text into The Weave; §21) |
| Long-term associative memory | **Saga** | `/api/saga/*`, `read_saga`, `arcanum saga` (§21.9) |
| Hierarchical summary tree over The Weave | **The Tapestry** | `Arcanum:Features:Tapestry`; `### Hierarchical Context (The Tapestry)`; `POST /api/embeddings/reset?scope=tapestry` (§21.11) |
| Recursive Spell dependency injection | **Arcane Resonance** | `SpellDependencyResolver`; dependency and byte envelopes are internal invariants (Arcanum.DESIGN.md §10.2.2) |
| Pre-flight active-Spell selection | **Spell Routing** | `SemanticRouter` (LLM-based) + `SemanticSpellRouter` (embedding pre-filter); `Arcanum:Features:SemanticSpellRouting` (Arcanum.DESIGN.md §10.2.2, §21.10) |

**Rejected:** Dispel, Glyph, Invocation (too obscure). The placeholder **Bureau** was retired in favor of **The Conclave** (the multi-agent coordination network; see above).

**Naming rules:** thematic API routes (`/api/spells`); error codes `{Noun}.{Verb}` (`Ward.NotFound`, `Campaign.DuplicateName`) — cross-layer wire codes are centralized as `public const string` in `Core/Primitives/ErrorCodes.cs` (grouped by Validation / Hub / NotFound / etc.); HTTP status mapping for `Result.Error.Code` is centralized in `Api/TheForge/ArcanumErrorMapper.cs`; config paths `Arcanum:{Noun}:{Setting}`. Propose any new concept name to the DM before implementing. Full rationale in this section's source and DESIGN.md §2.1.

---

## API surface map

Default base `http://localhost:5001`. **All `/api` and `/v1` routes require the API key** (`X-Arcanum-Key` or `Authorization: Bearer`). This is a grouped overview; the exhaustive inventory is in [Arcanum.API.md §1](docs/Arcanum.API.md#1-complete-api-surface).

| Area | Routes | Contract / purpose |
|------|--------|-------------------|
| Metrics | `GET /metrics` | Prometheus text; API key on by default (forced on ListenAny). [API §8.22](docs/Arcanum.API.md#822-metrics-endpoint-get-metrics) |
| Health & meta | `/api/health`, `/meta`, `/grimoire/stats`, `/budget` | Readiness + spend snapshot; a valid Unhealthy health envelope returns 503 with component detail |
| Durable operations | `/api/operations*` | Safe list/show plus CAS cancel/retry and bounded manual reconciliation; checkpoint bytes/references never leave SQLCipher |
| Data lifecycle | `/api/data/*` | Authenticated retained-data status, typed retention settings, dry-run plans, durable apply, explicit session/attachment deletion, scoped memory reset, and factory reset |
| Config | `/api/config`, `/config/validate` | GET redacts secrets; PUT preserves `"***"` placeholders |
| Models / providers | `GET /api/models`, `/providers`, `/providers/test`, `/providers/{name}/familiar-probe` | Listings, HTTP connectivity probe, and Familiar readiness (all read-only, no persist) |
| Inference (native) | `/api/intelligence/ping(-stream)`, `/human-response`, `/arsenal`, `/mana`, `/context/inspect` | Buffered / NDJSON `IntelligenceEvent`; model-aware Mana/source breakdown and read-only effective-turn preview |
| Inference (OpenAI) | `POST /v1/chat/completions`, `GET /v1/models`, `POST /v1/embeddings` | OpenAI JSON/SSE; Scrying gates images; client tools opt-in |
| OpenAI stubs | `/v1/moderations`, `/images/*`, `/audio/*` | Always 501 `not_supported` |
| Files / Batches | `/v1/files*`, `/v1/batches*` | Upload + async JSONL chat batches |
| Sessions | `/api/sessions/*` (+ entries/stream/attachments/divine/fork/pin/compact) | Grimoire threads; standalone attachment snapshot/reference/content/refresh routes; memory-mgmt gated; RAG divine off by default |
| Memory inspection | `/api/memory/*` | Cross-store read model with explicit provenance/retention; stores remain separate |
| Lore / Saga | `/api/lore/*`, `/api/saga/*` | Legacy KV lore; Saga auto-memory (divine gated) |
| Spells / Prompts / Campaigns | `/api/spells/*`, `/prompts/*`, `/campaigns/*`, `/codex` | Forge registry + execute/stream/versions |
| Apprentices / A2A | `/api/apprentices/*`, `/conclave/a2a/*`, `/conclave/status`, `/conclave/sendings`, `/conclave/sendings/{taskId}/continue`, `/conclave/a2a/callbacks/{configId}` | Goal agents + optional A2A (off by default, no edition gate). The callback route is the single A2A path outside the API-key filter — peers cannot hold an operator key, so it authenticates on a per-Sending secret (DESIGN §5.7.1.4). |
| Wards / Sanctum | `/api/wards/*`, `/campaigns/{id}/sanctum*` | Forbidden Arts + sandbox / FS-jail |
| MCP | `/api/mcp*`, `/mcp/tools/invoke` | Lifecycle + diagnostic external invoke |
| Workspaces | `/api/workspaces/*` | File browser/write gate + Weave index/divine |
| Unseen Servant | `/api/unseen-servant/*` | Interval control; watermarks persist; `lastResult` process-local |
| Events / Comm / Perception | `/api/events/*`, `/commlink/send`, `/perception/look` | SSE; webhook; Eye of the World |
| Trials / Logs / Audit | `/proving-grounds/trials/run`, `/logs`, `/audit`, `/guardrails/audit` | Ephemeral trials; ring buffer; JSONL audits |
| Tools / Docs | `POST /api/tools/invoke`, `/openapi/v1.json`, `/scalar` | Built-in invoke; OpenAPI; Scalar opt-in |

**Wire shapes:** `ApiResponse<T>` for `/api` JSON; NDJSON for streams and machine-readable watch output; SSE for events/session/Chronicle; OpenAI shapes for `/v1`. Native NDJSON includes additive `context` frames with the pre-call estimate and optional post-call provider variance; OpenAI SSE intentionally filters those Arcanum diagnostics. Native clients preflight `type`: unknown nonblank future strings are silently skipped, while malformed JSON or missing/non-string/blank/whitespace-padded discriminators retain diagnostics and the stream continues. The Forge caps buffered JSON/error bodies at 64 MiB, protocol lines at 1 MiB, aggregate SSE events at 8 MiB, and resumes after an over-cap frame; JSONL previews enforce their byte ceiling even without newlines, downloads replace the destination only after the staged transfer completes, and The Hearth truncates individual local-terminal lines after 64 Ki characters while continuing the stream. Direct source-generated enum deserialization stays strict. Compression + Idempotency-Key: [API §8.25](docs/Arcanum.API.md#825-http-response-compression) / [DESIGN §11.17](docs/Arcanum.DESIGN.md#1117-idempotency-key-request-replay).


## Inference engine details

Summaries only — full contracts live in DESIGN.

- **Providers:** `Arcanum:Providers[]` keeps provider name/type/endpoint, optional credential environment-variable reference, factual model inventory/capabilities, and context capacity. A **Familiar** row (`ClaudeCodeCli` / `CodexCli`) instead keeps an optional `command` override and a subtractive `hiddenModels` list: it has no endpoint and no credential, its `models[]` is optional because the vendor owns the catalogue, and a model name nothing declares passes through to the CLI verbatim — so a newly released model works with no edit. Hidden is not blocked: a hidden model is left out of listings and pickers but still resolves when named. Tokenization and prompt-cache behavior are code-owned: the built-in catalog selects verified behavior, and unknown endpoints/models emit no cache directives or cached-usage claim.
- **Model-aware context accounting:** `IModelTokenEstimator` resolves the built-in verified official-OpenAI exact `o200k_base` families or a conservative fallback (at least UTF-8 bytes plus margin). Every provider call accounts for messages, complete tool schemas, structured-output schema, RAG/memory/attachments, provider framing, and separate answer/reasoning reserves. `/api/intelligence/mana`, the read-only `/api/intelligence/context/inspect` preview, native `context` frames, successful audit records, Command Center `/mana`, the Command Center Context pane, and Prometheus expose quality/source/variance plus direct history, explicit-attachment, refreshed-file, attachment-RAG, and workspace-RAG token fields; the metadata-only attachment index does not inflate retrieved-RAG totals. The pane switches its total from `estimated` to valid provider-reported input labeled `billed`. Admission drops Tapestry, Saga, workspace RAG, then attachment RAG before complete tool exchanges, records attachment/workspace/Tapestry semantic drop counts for the pane warning, and never silently drops accepted explicit files. The footer aggregates attachment indexing as pending, completed, or failed and refreshes while pending work runs.
- **First-class reasoning:** native requests use `reasoning:{effort?,budgetTokens?,output?}` where effort is `none|minimal|low|medium|high|extraHigh`, output is `none|summary|full`, and effort/budget are mutually exclusive. OpenAI requests use `reasoning_effort` (`xhigh` maps to native `extraHigh`), additive `reasoning_budget`, and `reasoning_output`. `reasoning_output` is an Arcanum-local exposure preference plus a Microsoft.Extensions.AI best-effort hint, not a guaranteed provider wire control; Arcanum never invents an unsupported provider field. When output is omitted, a full-capable model defaults to `full`, otherwise a summary-only model defaults to `summary` (subject to `allowsClientOutput`, and `supportsStreaming` on streams). Reasoning and capability/dialect enums are string-only; numeric or unknown enum JSON fails strict binding. Model objects opt in with `reasoning:{controlSupport,supportsSummary,supportsFull,supportsStreaming,reportsReasoningTokens,allowsClientOutput,wireDialect,maxBudgetTokens?}`; control support is `none|effort|budget|effortAndBudget`, and the closed dialects are `standard|openRouter|topLevelReasoningBudget|anthropicThinking`. No dialect is inferred from provider/model names.
- **OpenAI reasoning errors:** semantic validation is identical for buffered and `stream:true` requests and returns HTTP 400, `type:"invalid_request_error"`, `param:"reasoning"`, with `invalid_reasoning_options`, `invalid_reasoning_budget`, `unsupported_reasoning_control`, `reasoning_budget_exceeds_model_limit`, or `unsupported_reasoning_output`. Unknown enum strings and defined/undefined integer enum values fail earlier as strict JSON binding: HTTP 400, code `invalid_json`, no `param`.
- **Reasoning separation:** native buffered responses expose an ordered `reasoning` array; NDJSON uses typed `reasoning` frames; OpenAI buffered/SSE uses additive `reasoning_summary` / `reasoning_content`; native usage exposes additive `cached_tokens` and `reasoning_tokens`, while OpenAI usage uses `prompt_tokens_details.cached_tokens` and `completion_tokens_details.reasoning_tokens`. Answer fields remain answer-only. Visible reasoning is ephemeral, provider `ProtectedData` stays in memory only for same-provider tool continuation, and no reasoning body enters Grimoire, logs/audit, trace export, Master/Apprentice handoff, checkpoints, or Chronicles. The Forge Tome renders a live reasoning role and traces retain only redacted type/output/count metadata.
- **Agentic layers:** spell routing (+ optional embedding pre-filter), Arcane Resonance, Artifact Attunement, MCP tool loops, read-time compression, Wards, Sanctum. Artifact Attunement applies to MCP tools plus native `web_search` / `read_url`; exactly local time, system info, and spell-script tools are exempt. Legacy spell declarations of `browse_web` canonicalize to `read_url`. Spell validation and dry-run preview use the same web-tool decision. See [DESIGN §10](docs/Arcanum.DESIGN.md#10-intelligence-pipeline), especially the canonical [turn lifecycle in §10.7](docs/Arcanum.DESIGN.md#107-end-to-end-turn-lifecycle-and-chat-loop).
- **Reliable workspace tools:** `search_workspace` performs strict-UTF-8, deterministic, line-scoped literal or bounded runtime-regex search directly over the complete contained workspace and returns 256-match pages with opaque query-bound match-identity cursors (non-backtracking first, interpreted fallback, no `RegexOptions.Compiled`, no Weave). It accepts every normalized include/exclude selector that fits the request/per-pattern boundaries instead of stopping at 64. A vanished checkpoint requests a cursor-free restart, and response trimming advances only after the last retained match. `list_directory` likewise uses opaque scope-bound last-entry continuations without a total path/depth ceiling and canonical visited-directory identities; it yields a contained directory symlink once without following a cycle. The workspace-files API returns 500 entries from a bounded 501-candidate heap, and `workspace tree` follows every `nextCursor`. `apply_patch` separates byte-bounded unified-diff parsing from per-file filesystem planning, removes incidental elapsed/file/hunk/line/input/result totals, then uses one allocation-protected reversible **sequential, observable, non-isolated** transaction per call; it requires a persisted assistant turn and deterministically persists the exact arguments/result before the result reaches the model. It offers rollback and relative recovery artifacts, not process-wide isolation or crash atomicity. `workspace_check` runs closed `.NET` build/test/lint profiles with `--no-restore`, read-only source/package/SDK roots, owner-only per-run outputs, and complete streamed top-level TRX aggregation. Restore seeding lazily visits every project/artifact/input, streams DTD-disabled XML beyond the former 8 MiB parser cap, and writes an owner-only fingerprint manifest under the measured Sanctum `MaxFileWriteMb` policy rather than project/file/input count totals. Repository tasks/generators/analyzers/tests still execute arbitrary code, so it always Wards while Wards are on. It is advertised only with eligible macOS Seatbelt + trusted `dotnet`/SDK/launch chain; Linux/Windows are unavailable. Network remains open and intentionally detached-descendant cleanup is best effort. Full status/recovery contract: [DESIGN §10.2.1](docs/Arcanum.DESIGN.md#1021-built-in-tools-and-mcp-workspace-tools).
- **Bounded tool results / Apprentice denials:** result materialization normalizes malformed UTF-16 and bounds retained text plus its marker with shared surrogate-safe UTF-8 helpers. Ward/Sanctum denial is carried to Apprentice orchestration through an internal non-wire `ToolDenied` bit, never phrase matching; reasoning frames never count as denial evidence.
- **Ward auto-approval (opt-in, off by default):** rather than choosing between confirming every gated call and disabling Wards wholesale, an operator may pre-authorize named tools with `Arcanum:Security:Ward:AutoApprove:Enabled` plus an exact-name `AutoApprove:Tools` allowlist. Listed tools skip the prompt; everything else still prompts (attended) or denies (unattended) exactly as before, and an empty list is a no-op. This supplies **operator consent only** — Sanctum, `WorkspacePathPolicy`, edition and host-process gates, Artifact Attunement, and `workspace_check` eligibility still run on every auto-approved call, and unattended auto-deny is evaluated first so **deny always wins**. Auto-approved calls still emit `warded`/`wardResolved` with an `origin` field (`autoApproved` vs `human`), so Command Center records them in the transcript instead of opening a modal and `arcanum_ward_decisions_total{origin=…}` counts them. It is unrelated to the CLI `--yes` direct-command flag, which never becomes authority for a model-proposed action. [DESIGN §11.14](docs/Arcanum.DESIGN.md#1114-wards-forbidden-arts).
- **Idempotency:** same-process requests coordinate locally before durable acquire; live foreign-process ownership returns 409 `Security.IdempotencyInProgress` (OpenAI `idempotency_in_progress`). The current renewable lease is five minutes. Only terminal in-cap responses replay; explicitly terminal empty bodies replay empty, while partial/over-cap responses do not. [DESIGN §11.17](docs/Arcanum.DESIGN.md#1117-idempotency-key-request-replay).
- **Inference audit:** the opt-in JSONL log records successful completed turns only. Tool names/counts are retained; raw argument JSON is omitted by default (`Arcanum:Host:AuditLog:RedactToolArguments=true`); tool results and prompt/answer/reasoning bodies are not audit fields.
- **Scrying / attachments:** persisted bytes are durable snapshots stored as authenticated encrypted envelopes; plaintext hashes and lifecycle metadata remain inside SQLCipher. Optional live-file provenance is accepted only from a host-trusted path beneath the active workspace after canonical, symlink, and file-handle identity checks; API-supplied paths remain snapshot-only. Attachment responses expose sanitized relative provenance/status/hash/time metadata and never absolute host paths. Missing or unsafe sources do not delete snapshots. This schema revision is folded into the canonical database creation script, so upgrading installations must recreate the database. Full contract: [§10.2.4](docs/Arcanum.DESIGN.md#1024-scrying--the-visionmultimodality-capability-gate) / [§10.2.5](docs/Arcanum.DESIGN.md#1025-session-attachments-disk--grimoire-pointers). The attunement-aware `refresh_session_file` tool accepts an attachment id or logical key—not a path—securely rereads verified workspace provenance through an identity-checked handle, reuses an unchanged version or persists the next encrypted version with its currently detected MIME-derived kind, and queues it after the complete tool round for the next request in the same logical turn. It shares attachment byte/version/reference budgets, inject-once behavior, MIME/Scrying/vision checks, and Sanctum enforcement. Native NDJSON exposes sanitized `attachmentRefreshed` observability; OpenAI projections ignore it. Command Center `/attachments` renders `[Snapshot]`, `[Live]`, or `[Stale]` with the loaded version hash and last backend-observed disk hash/time. Its filesystem watcher only triggers an asynchronous metadata re-read; the host revalidates provenance before the UI changes state. Use `/attachments refresh <logicalName>` to run the same secure refresh core manually; `[Live]` is printed only after the backend confirms the persisted/reused version. Manual refresh applies the content policy without requiring the configured default model to support vision because no model content is queued. Semantic retrieval reads only through the encrypted attachment store, exposes bounded `indexingStatus` metadata, and fences retrieved excerpts as untrusted DATA. Durable memory promotion is fail-closed: Lexicon and Saga accept attachment-derived facts only from the current turn's materialized attachment allowlist and retain typed source provenance. Campaign summaries persist metadata-only consultation references; prompt-cache stable prefixes, audit logs, and subagent context never absorb attachment bytes, excerpts, host paths, or hashes. Source deletion preserves provenance but reports it as unavailable. See [the chat-loop ordering guide](docs/Arcanum.CHAT-LOOP.md).
- **A2A:** [§5.7.1](docs/Arcanum.DESIGN.md#571-a2a-and-the-conclave) (disabled by default; enabled purely by `Arcanum:Features:A2AServer` / `A2AClient`, with no edition gate). The end-to-end operator workflow — enable, identify, verify, dispatch, continue, observe, accept, account — is §5.7.1.1; what survives a restart and what is reconciled is §5.7.1.2; continuing a Sending the remote parked at `input-required` is §5.7.1.3; push notifications and callback-mode Sendings are §5.7.1.4; answering a parked Sending **after a restart** is §5.7.1.5. Delegation loops are broken by node-id cycle detection rather than a hop ceiling, and the chain propagates through an Apprentice's own Sendings so multi-hop cycles are caught too. A peer that advertises streaming is subscribed to rather than polled, degrading to polling rather than failing when a stream drops. An outbound Sending states what it will accept back and checks it against the peer's Agent Card **before** the remote task exists, so a modality or skill mismatch is a named local failure rather than a remote task left running for an answer nothing here can read. Remote cost is reported as known or **explicitly unknown**, never as a silent zero — and now counts against the daily budget, with unpriced delegated work surfaced as a count rather than folded in at zero (§22.2, `arcanum budget`). Operators may declare Agent Card skills and modalities; declaring none serves the original card unchanged. Reach an authenticated peer (including another Arcanum) with `Arcanum:Integrations:A2A:OutboundCredentialEnvironmentVariable`.
- **RAG (Weave / Divination / Saga / Tapestry):** [§21](docs/Arcanum.DESIGN.md#21-the-weave-divination-saga-and-the-tapestry-rag) — capabilities are gated under `Arcanum:Features`; only embedding provider/model/dimensions are public integration facts. Watcher, queue, retry, batch, checkpoint, and retrieval-slice mechanics are code-owned and no longer exposed as user restrictions. Semantic workspace indexing reacts to recursive watcher events, revalidates paths and opened file identities before every read, lazily completes cancellable full walks without a total-entry ceiling, streams large files through pooled embedding pages, reconciles lost/unavailable events, and exposes watcher/reconciliation health through `/api/workspaces/{id}/files/index/status`. Saga extraction likewise drains a deduplicated queue through oldest-first timestamp-group checkpoint pages, retries failed pages without advancing the watermark, and keeps every eligible memory without a per-session or installation count cap; provider capability, paged retrieval, explicit deletion, retention, and cancellation remain authoritative. The Tapestry adds hierarchical summary trees over those same corpora: its tree-shaping bounds (depth, children per summary, clusters per layer, summary tokens, rebuild cadence) are code-owned mechanics rather than user restrictions, and only the retrieval mode and summary model are operator policy. Its clustering is deterministic and versioned, but the design deliberately does not claim reproducible model prose — only reproducible membership and summary identity.
- **Lexicon:** agent memory via `scribe_lexicon` / `delete_lexicon`; gated by `Arcanum:Features:Lexicon`. Every non-empty distinct fact is persisted at full length—there is no per-upsert, per-fact, retained-fact, or extracted-entity product total. Provider-visible matching/injection remains context-bounded without deleting durable facts. Attachment-derived facts require a current-turn materialized attachment id and retain typed provenance. [§10.6](docs/Arcanum.DESIGN.md#106-the-lexicon--agent-directed-entity-memory).

---

## Configuration

Settings bind under the required `Arcanum` object in **`arcanum.json`** (`~/.config/arcanum/` on macOS/Linux, `%USERPROFILE%\.config\arcanum\` on Windows). General environment overrides keep the wrapper after the prefix, for example `ARCANUM_Arcanum__Host__Port`; `ARCANUM_EDITION` and `ARCANUM_HOST_ANY` are explicit overrides. Before binding, the source-generated configuration schema walks the complete tree and reports every unknown/obsolete path together; dynamic array indices and documented dictionary keys remain valid. Serve then runs semantic validation before listening.

Use `arcanum config path`, `show`, `get <dot.path>`, `set <dot.path> [value]`, `validate`, `edit`, or `open` for routine configuration work. `arcanum open compendium` is the explicit application-launch spelling; `arcanum config open` remains the configuration-family entry. These commands prefer the running host's authenticated `/api/config` endpoints. When the host cannot be reached (or a first-run key is not initialized), stderr clearly identifies local bootstrap mode; that path still uses the canonical loader, full validator, outbound URL guard, and atomic writer. `show`/`get` mask provider endpoints, environment overrides are named without revealing their values, and sensitive endpoint values must be supplied through redirected stdin or the hidden prompt—not argv. `edit` uses an owner-only temporary redacted copy and applies it only after full validation. Both open forms launch Compendium or print the attempted locations, repository-relative development command, exact file path where applicable, and `arcanum config edit` fallback.

Public settings are limited to deployment choices, provider/model facts, credential references, security and permission policy, integration endpoints/allowlists, feature opt-ins, schedules, host-capacity choices, pricing facts, and user preferences. Retry, fallback, workflow-count, and other implementation mechanics are code-owned. Unknown or obsolete paths fail together before binding; there are no compatibility aliases or silent ignores.

> **Compendium** edits the same file visually — [`Compendium.README.md`](docs/Compendium.README.md). Provider rows edit factual fields and credential environment-variable references, never credential values or tokenization/prompt-cache algorithms.

**Full retained-key reference (types, defaults, clamps):** [Compendium's complete configuration reference](docs/Compendium.README.md#complete-configuration-reference). `SettingDescriptors.cs` is Compendium's editable-key source of truth. DESIGN §3.4 documents only the architectural contract. The public roots are summarized here:

| Section | Controls |
|---------|----------|
| `Edition` | Runtime hardening mode. |
| `Host` | Port, CORS, external HTTPS binding, inference-audit policy, and buffered-log level. |
| `DefaultModel` / `FastModel` / `Providers` | Provider endpoint and credential reference, model inventory, vision/reasoning facts, and context capacity. |
| `Security` | Ward/guardrail policy, metrics authentication, path authority, MIME allowlists, and the unsandboxed-child acknowledgement. |
| `Workspaces` | Default root and explicit write permission. |
| `Features` | Capability opt-ins including Conclave/A2A, Apprentices, The Weave, session attachment retrieval, Scrying, attachments, browsing, guardrails, workspace checks, and memory management. |
| `Integrations` | A2A identity/allowlist, CommLink reference/allowlists, embedding facts, native web-research provider facts, MCP plaintext-host exceptions, and workspace-check profiles. |
| `Execution` | Host concurrency/backpressure for Apprentices, SSE, and batches. |
| `Cost` | Default/per-model pricing and daily budget policy. |
| `Retention` | Opt-in sweep scheduling, bounded/checkpointed execution, typed per-class rules, accounting floor, and protected-session holds. |
| `Daemon` | Unseen Servant schedules and concurrency. |
| `Cli` | Theme and mana-bar preference. |

### Safe onboarding presets

`arcanum preset` offers six immutable, versioned **partial overlays**. A preset owns only the listed paths; every provider, secret reference, integration, budget, schedule, and other setting outside that list remains operator-owned. Presets are transparent configuration changes, not hidden runtime modes or a second configuration model.

| v1 preset | Owned configuration |
|-----------|---------------------|
| **General Assistant** (`general-assistant`) | Attachments on; Saga, Saga extraction, and memory management off; Ward and unattended auto-deny on; unsandboxed tool children off. |
| **Coding Workspace** (`coding-workspace`) | Workspace checks and workspace file writes on; Ward and unattended auto-deny on; unsandboxed tool children off. A default workspace root is a prerequisite; semantic indexing remains optional. |
| **Research** (`research`) | Native web browsing on only when its credential prerequisite is satisfied; Ward and unattended auto-deny on; unsandboxed tool children off. |
| **Private/Offline** (`private-offline`) | Loopback host binding; web browsing and enterprise telemetry off; Ward and unattended auto-deny on; unsandboxed tool children off. A loopback inference provider is required, and authored MCP/integration configuration is not silently erased. |
| **Automation** (`automation`) | Ward, unattended mode, and unattended auto-deny on; unsandboxed tool children off. A configured provider/model and an already enabled positive daily budget are required; the preset never invents or enlarges a budget. |
| **Advanced/Custom** (`advanced-custom`) | No owned paths and no automatic changes; hands control directly to `arcanum config` or Compendium. |

Use the same catalog and planner from either CLI or Compendium:

```bash
arcanum preset list
arcanum preset show coding-workspace
arcanum preset diff coding-workspace
arcanum preset apply coding-workspace
arcanum preset reset
```

`show` explains the exact owned settings, security/network disclosures, prerequisites, first essential choice, deferred advanced features, and next command. Recommendations are executable; for example, Coding Workspace uses `arcanum run --workspace . "Inspect this workspace and summarize it."`, including the required prompt. `diff` is read-only and reports persisted, effective, and proposed persisted values separately, including the current source, environment-variable name (never its value), ownership, prerequisite status, and restart impact. An environment override can therefore make a persisted value change while leaving the effective value unchanged; the output says so instead of claiming the preset changed live behavior. Only an effective override that contradicts a preset-owned safety or privacy boundary blocks Apply. A benign feature mask remains authoritative, stays visible as effective drift, and does not make the plan inapplicable. The secure research-credential store is probed only while diffing or applying the Research preset; other preset plans, listing/state inspection, and reset do not touch it.

Application first builds and validates one complete candidate, rejects missing required prerequisites, checks for a concurrent configuration change, and enters the same current-user cross-process transaction coordinator used by every canonical configuration write, including CLI and Compendium writers. It creates owner-only provenance and rollback sidecars and atomically replaces `arcanum.json`. Provenance records the preset ID/version, timestamp, owned-value hash, baseline, and applied values separately from effective configuration. Sidecar reads are bounded and no-follow; provenance must exactly match a catalog preset's version, owned paths, canonical values, hashes, and paired rollback state before it is trusted.

No provenance means **Custom**; matching owned persisted and effective values mean **Active**; any later owned-value difference means **Drifted**. Reapplying the same version and owned values is idempotent. `reset` restores a baseline value only when it still equals the preset-applied value, preserves user drift and every unrelated setting, then reports both counts. The prepared journal contains only preset-owned before/after values and hashes plus previous/next provenance—not a full configuration copy. Recovery conditionally reverses only values that still match the interrupted write, so unrelated or later manual edits win instead of being overwritten.

Every successful plan ends with provider/model, workspace/campaign, enabled memory sources, tool policy, privacy state, and the next recommended command. Compendium keeps that selected-preset projection separate from the latest inspected current summary; an inspection failure is `Unavailable`, never a stale active/drifted label. The five workflow presets explicitly retain Ward, unattended auto-deny, and the unsandboxed-child safe default; Advanced/Custom owns no paths and changes nothing. Sanctum continues to enforce the operator's configured workspace boundaries. No preset weakens these boundaries or silently enables `ListenAny`, unsandboxed tool children, untrusted workspace MCP, destructive memory operations, Forbidden Arts bypasses, or changes to explicit token/cost/security policy. Presets do not add retry, timeout, loop-count, or other arbitrary tuning knobs.

This command family is the reusable preset service for guided onboarding. The interactive `arcanum setup` wizard composes the same service for its preset step, so the two surfaces apply exactly the same overlay; `preset apply` never simulates the wizard's other steps.

Progress/no-progress mechanics, retries/fallback, structured-output correction, transport connection/idle behavior, filesystem and storage envelopes, heartbeats, and other implementation mechanics are not configuration sections. The public surface retains only meaningful operator policy, security choices, provider/model facts, explicit budgets/retention, and host-capacity choices. Required containment, protocol, integrity, and single-allocation safeguards remain code-owned invariants and are classified in [`Arcanum.ConstraintInventory.json`](docs/Arcanum.ConstraintInventory.json); the removals and rationale are summarized in [`Arcanum.ConstraintReduction.20260803.md`](docs/Arcanum.ConstraintReduction.20260803.md).

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
      },
      {
        "name": "ClaudeCode-subscription",
        "type": "ClaudeCodeCli",
        "contextWindowLimit": 200000,
        "hiddenModels": []
      }
    ]
  }
}
```

Set `OPENAI_API_KEY` in the host environment. `models` entries may be bare strings or objects. Ollama, when used, must use its `/v1` endpoint. A reasoning-capable model entry is explicit:

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

Provider credentials never live in `arcanum.json`. They resolve in a fixed order: the environment reference first, then the OS-backed secure store. An explicit `credentialEnvironmentVariable` is the exact reference and replaces the default. When omitted, Arcanum derives `ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY`: ASCII letters/digits are retained, letters are upper-cased, runs of other characters become one underscore, and an empty result becomes `UNNAMED`. Explicit references use portable `[A-Za-z_][A-Za-z0-9_]*` names. For the minimal example:

```bash
export OPENAI_API_KEY='your-key-here'
```

PowerShell: `$env:OPENAI_API_KEY = "your-key-here"`.

To skip environment variables entirely, store the credential in the OS credential manager — macOS Keychain, Windows Credential Manager, or Linux Secret Service, each with an owner-only Data Protection mirror for headless hosts:

```bash
arcanum key provider set openai   # reads redirected stdin or a hidden prompt; never echoed back
```

`arcanum setup` does the same thing as part of guided onboarding. Because the environment reference is checked first, exporting the variable still overrides the stored credential for that process without changing stored state. `arcanum key list` reports every credential identity Arcanum owns with presence and status only; a credential value is never printed back, including under `--json`.

### Native web research

Native web tools are off by default. Enable the family and select the synthesized-search model in `arcanum.json`:

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

The secure prompt stores the key in the OS credential manager with an owner-only, Data Protection-encrypted fallback. For unattended hosts, set `ARCANUM_PERPLEXITY_API_KEY`, or set `integrations.webResearch.credentialEnvironmentVariable` to another exact environment-variable name. The environment reference takes precedence at invocation time; key values are never returned by status, provider-list, logs, or telemetry. Remove local copies with `arcanum key provider delete perplexity`.

When enabled, models receive `web_search` for current, synthesized answers with ordered citations and `read_url` for allocation-safe static HTTP/HTTPS pages converted to Markdown. `read_url` does not launch or embed a browser and does not execute JavaScript; bot-protected and empty JavaScript shells return a structured error suggesting `web_search`. Connection and idle-I/O deadlines protect stalled transports without assigning a total wall-clock duration to progressing work. Per-read body/frame limits, SSRF protection, untrusted-content framing, and aggregate-only usage/token/cost/latency telemetry remain. The old `browse_web` direct-invoke alias remains for compatibility but is not advertised in new model toolsets.

The same capabilities are first-class CLI workflows, so no chat prompt or raw tool JSON is needed:

```bash
arcanum search "current .NET support policy" --count 5 --freshness month
arcanum search "release notes" --include-domain dotnet.microsoft.com --json
arcanum browse https://example.com/article --render static --save article.md
arcanum research "Compare the current proposals" --sources 8 \
  --token-budget 2500 --cost-budget 0.25 --format markdown
cat local-notes.txt | arcanum run --research "Reconcile these notes with current sources" \
  --with @requirements.unusual --sources 8
```

`search` also accepts repeatable `--include-domain` / `--exclude-domain`. `browse --render javascript` reports a clear unavailable-renderer error and recommends `static`; it never silently pretends static HTML is rendered JavaScript. `research` performs another server-side pass while at least one new unique source is discovered. Optional `--sources` is a positive target, not a default ceiling; omitting it continues to source exhaustion or deterministic no-progress. The command prints the target/no-progress reason, unique-source count, explicit token/cost policy, and `Searching` / `Fetching` / `Rendering` / `Synthesizing` progress to stderr, while the final cited terminal, Markdown, or single JSON payload remains on stdout. All orchestration and model accounting stay in the server. `--save <path>` atomically exports Markdown; `--attach-to-session <session>` stores it as an encrypted session attachment; and research `--continue-session <session>` continues the server-side synthesis turn. Session values accept a GUID, exact title, or unique title prefix.

`arcanum run --research` reaches the same server-owned workflow through the unified execution surface. It preserves the resolved Campaign, Workspace, Session, and Model, combines an optional instruction with piped text, accepts repeatable current-turn `--with @path` text/images, and forwards the common sampling controls into synthesis. The host validates the prospective synthesis request and resolves Campaign/Session context before provider search. Research's existing untrusted-source instruction and tool-disabled final synthesis remain server-owned; selecting the route does not impose new restrictions on ordinary Agent or named-Spell execution. Live synthesis uses the normal attachment pipeline. Use `--dry-run` for a spend-free static pre-inference plan without search, synthesis, tool execution, or persistence.

Use only keys in [Compendium's retained reference](docs/Compendium.README.md#complete-configuration-reference). After changing `arcanum.json`, restart Arcanum. Configuration-only changes do not require deleting or reinstalling the Grimoire.

Known official OpenAI `gpt-4o`, `chatgpt-4o`, `gpt-4.1`, `gpt-5`, `o1`, `o3`, and `o4` families use the built-in exact `o200k_base`/key-only prompt-cache profile. Unknown providers, endpoints, or models use conservative estimated accounting and no prompt-cache directive.

`DefaultModel`/`FastModel` must match a `models` entry on some provider — matching is a case-insensitive **exact** match, with no bare-name or tag-stripping fallback. OpenAI-compatible `endpoint`s usually include `/v1`. **MCP servers** are wired via `~/.config/arcanum/mcp.json` (`mcpServers` schema) over **stdio** (`command`/`args`, with an optional `inheritEnv` allowlist for `npx`-style launches) or **Streamable HTTP** (`type: "http"` or a bare `url`, SSRF-guarded and `https`-by-default); workspace-local `mcp.json` is merged only after explicit `arcanum mcp trust [workspace]` approval, which calls `POST /api/mcp/trust-workspace`. Routine listing, lifecycle, reload, tool discovery, and diagnostics are available through `arcanum mcp ...` and `arcanum tool ...`; raw HTTP is not required. See [Compendium's complete configuration reference](docs/Compendium.README.md#complete-configuration-reference); MCP transport limits are code-owned.

### Local Grimoire reinstall

Arcanum has no migration chain and no supported user-data migration path between incompatible local schemas — the declarative schema tree installs fresh and every statement is `CREATE ... IF NOT EXISTS`, so re-opening a database with an older shape adds what is missing and leaves incompatible objects exactly as they are. It does not upgrade them. A developer database created before the current schema must be recreated. Before replacing the binary that can still read it, create and verify a supported `.arcbackup` for anything that must be preserved. Then stop every Arcanum host and daemon, delete the database and its WAL/SHM sidecars, and restart. A database created by the current schema needs no reinstall.

If the dedicated Grimoire secret is corrupt or cannot decrypt the current database, startup fails closed with a sanitized database-unavailable error and never falls back to the API key. The failure is controlled, so host/CLI cleanup completes. Restore the matching secret and Data Protection key ring, or follow the destructive reinstall procedure only after preserving anything recoverable. The same controlled rule applies when the master API-key credential/mirror is corrupt and a Grimoire database exists: Arcanum does not log the underlying decryption message or generate a replacement key. Safe key regeneration is limited to the no-database case. All Data Protection credential mirrors are accepted only as no-follow, single-link regular files of at most 64 KiB; linked, oversized, or undecryptable ciphertext fails closed as corrupt. The Grimoire `.kdf` sidecar follows the same identity rule with a 4 KiB ceiling and owner-only, durable atomic publication.

The current canonical schema requires recreation of any database whose `Entries` table lacks the `Sequence` column and unique `(SessionId, Sequence)` index. Those fields give a session's transcript an explicit append order instead of inferring one from timestamps that a prompt and its answer share. Existing rows never recorded that order, so there is nothing to backfill and the database is recreated instead ([DESIGN §5.4.1](docs/Arcanum.DESIGN.md#541-grimoire-data-model), [§5.4.5](docs/Arcanum.DESIGN.md#545-schema-installation-serialization-and-crash-consistency)).

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

There is intentionally no data migration or EF-model regeneration for the raw-SQL accounting tables. See [DESIGN §5.4.5](docs/Arcanum.DESIGN.md#545-schema-installation-serialization-and-crash-consistency) and [§22.2](docs/Arcanum.DESIGN.md#222-cost-tracking-and-budget-enforcement-arcanumcost).

### Encrypted blob key, backup, and recovery

Session attachments, `/v1/files` uploads, and batch input/output/error files are never stored as plaintext under `attachments/` or `files/`. Arcanum streams them through a versioned `ARCABLOB` AES-256-GCM envelope with independently authenticated bounded chunks. Downloads and batch readers authenticate each chunk before returning plaintext, and batch output uses encrypted staging—there is no plaintext JSONL temp.

The independent 256-bit file-encryption master key is stored primarily in the operating system's secret storage:

- service `arcanum`, account `file-encryption-master-key`;
- macOS Keychain on macOS;
- Windows Credential Manager on Windows; and
- Secret Service/libsecret on Linux.

A second, unrelated installation secret lives beside it at service `arcanum`, account `campaign-root-identity-key`. It keys the opaque identity Arcanum derives for a Campaign's physical workspace directory, has no encrypted mirror and no environment reference, survives API-key rotation and Covenant reset, and is regenerated only by a full installation reset. Losing it leaves every Campaign path identity unresolved until authenticated repair rather than silently orphaning registered roots ([DESIGN §10.12](docs/Arcanum.DESIGN.md#1012-covenant-invocation-authority-and-campaign-binding)).

First startup creates the key only after the OS store accepts it. Arcanum also attempts to write `~/.config/arcanum/file-encryption-key.dat`, sealed with the local Data Protection key ring, as a recovery mirror. The mirror is not a substitute for OS key storage during normal writes. The file-encryption key is separate from both `master-api-key` and the Grimoire encryption secret; `arcanum key show` never displays it and API-key rotation does not rotate it.

Existing version-zero attachment/upload rows can be migrated in place without a startup rewrite:

```text
arcanum data encryption status
arcanum data encryption migrate
arcanum data encryption verify
arcanum data encryption rotate-key
```

Migration and rotation are resumable durable operations. They use bounded concurrency (default 2, maximum 8), an aggregate 64 MiB/s default throttle, and observe cancellation between files. Every file is length/hash checked before encryption, the temporary encrypted copy is authenticated before atomic replacement, and the replacement is decrypted and checked before metadata commits. A crash between replacement and metadata commit is reconciled on retry. `verify` reports aggregate missing/corrupt/unknown-key/metadata-mismatch/hash-mismatch categories and never prints filenames. New writes remain encrypted throughout the mixed-mode window.

Do not copy a live database or assemble a backup from its WAL sidecars. Use the supported portable backup workflow instead:

```text
arcanum backup create --dry-run
arcanum backup create
arcanum backup inspect <archive.arcbackup>
arcanum backup inspect <archive.arcbackup> --decrypt
arcanum backup verify <archive.arcbackup>
arcanum backup list
arcanum backup restore <archive.arcbackup> --dry-run
arcanum backup restore <archive.arcbackup>
arcanum backup migrate <archive.arcbackup> --output <new-archive.arcbackup>
```

Creation uses the same typed inventory planner for dry-run and execution. Dry-run reports selected components, estimated files and bytes, missing required files, nonportable paths, and security warnings without asking for the recovery passphrase or publishing an archive. The scopes are `full`, `configuration-and-authored-assets`, `sessions-and-memory`, `specific-session`, and `metadata-only`. `--include` and `--exclude` accept only the documented component catalog; they are not an escape hatch for arbitrary host paths, and exclusion wins if a component is named in both. `compendium-settings` and `configuration` share `arcanum.json`: selecting only `compendium-settings` still captures it as Compendium-owned state even when `configuration` is excluded, while selecting both stores one configuration entry and records Compendium as a complete zero-entry alias. The same configuration inventory includes committed `arcanum.preset.json`/`arcanum.preset.rollback.json` sidecars only as an exact matching pair and only when no preset recovery journal is present. An incomplete/mismatched pair makes the component incomplete; a pending journal prevents capture of the possibly mid-transaction configuration until preset recovery completes. The transient `arcanum.preset.journal.json` is never backed up. Verification accepts the paired sidecars as authenticated configuration entries; a coordinated recovery installs them beside their matching `arcanum.json`, never by recreating the journal. `specific-session` requires `--session-id`; other scopes may record it as provenance without narrowing their inventory. `trusted-mcp-workspace-metadata`, `audit-logs`, `guardrail-logs`, and `master-api-key` are excluded by default and require explicit inclusion. A full backup includes global MCP configuration and warns that it may contain literal environment values.

The default full scope captures a live, consistent SQLCipher snapshot through SQLite's online backup facility, the KDF metadata, configuration, attachment/upload/batch ciphertext, global Codex and Spells, CLI and The Forge local state, Compendium settings and certificates, and only the portable Grimoire secret plus active/referenced file keys needed by the selected data. The recovery material is re-exported inside the encrypted payload; the archive does not depend on the original OS credential store or raw Data Protection key ring. Environment-referenced secret values, OS credential-store internals, raw Data Protection keys, external workspace trees, daemon registration, and ephemeral process state are not portable components. A `specific-session` archive includes only matching Session attachments by default; uploaded and batch files are omitted because they have no Session ownership unless their global typed components are explicitly included. The version-1 physical Grimoire snapshot remains indivisible, so its manifest discloses collateral global/accounting rows rather than pretending to be a privacy-scoped logical export.

Every `.arcbackup` uses a versioned `ARCABACK` envelope. The small outer header contains only safe format, KDF, encryption, size, and creation-time facts. The canonical manifest, checksums, files, and portable recovery keys are inside a PBKDF2-HMAC-SHA256/AES-256-GCM authenticated encrypted payload streamed in bounded chunks. The current version records its salt and 600,000-iteration KDF parameters and uses one-MiB authenticated chunks. The passphrase comes from a hidden interactive prompt, the environment variable named by `--passphrase-env`, or the inherited descriptor named by `--passphrase-fd`; it is never accepted as a literal argv value. Interactive creation confirms the entry. Empty input is rejected without adding an arbitrary character-composition rule. Automation should prefer an inherited descriptor when practical and must protect any chosen environment variable from unrelated child processes.

Creation uses an identity-owned, owner-only staging root directly beneath the destination parent. The encrypted archive temporary file, decrypted self-verification payload, database verification extraction, and generated snapshot material all remain inside that root. Arcanum durable-flushes the staged archive, performs a complete authentication/checksum self-verification plus database validation when included, and revalidates that the staged pathname still names the captured file before atomically publishing it to the sibling destination on the same filesystem. A replacement staging path is neither published nor deleted as though Arcanum owned it. Existing destinations are not replaced without the explicit overwrite flow. Missing or unreadable required files, a changed or linked source identity, cancellation, corruption, or failed verification leaves no published archive and never reports success. Inventory fingerprints every source with bounded streaming SHA-256; creation rejects a changed fingerprint even when an in-place rewrite preserves the file identity and byte count. Optional state that does not exist is recorded as `unavailable`; policy exclusions remain explicit in the encrypted manifest.

For each database-backed blob, inventory validates the version, bounds, owning purpose, and key id from the `ARCABLOB` envelope on the same no-follow file handle used for its SHA-256 fingerprint, and confirms that descriptor again after hashing. The captured ciphertext's key id is authoritative for portable recovery export when a live key rotation has replaced bytes before committing their database metadata. Encrypted metadata paired with plaintext, a malformed or purpose-mismatched envelope, a descriptor changed during hashing, or an unavailable captured key makes creation incomplete before publication; it does not add an irrelevant snapshot key or block normal live rotation.

`backup inspect` without `--decrypt` or an explicit passphrase source shows only the safe outer header. `--decrypt` prompts when needed; it and the explicit sources authenticate one bounded encrypted chunk at a time, skip entry content, and retain only bounded path metadata plus the capped final manifest in memory. Inspection creates no plaintext staging file. `backup verify` authenticates the complete structure and every chunk, compares each SHA-256 and size, and checks the decrypted database and schema in an owner-only temporary root before removing it. Wrong passphrases and modified authenticated bytes share a sanitized authentication failure. `backup list` reads outer headers in the selected backup directory and does not decrypt archives.

#### Restoring a backup

`backup restore` is the supported recovery path. It never restores half a generation: the database, encrypted blobs, configuration and authored assets, and the archive's portable recovery material move together, or nothing moves at all.

Run `--dry-run` first. It authenticates the archive, validates every checksum and the Grimoire snapshot, validates your path mappings, and reports how much free space the restore needs — all without touching the destination. The plan it prints is exactly what a real restore would execute.

A real restore refuses before staging when the archive's format is newer than this build supports (with upgrade guidance), when the archive carries no portable recovery material, when the destination volume cannot hold the restored generation alongside the current installation, or when a running host or another restore holds the maintenance lock. Stop the host and try again.

Replacing an installation is destructive, so it asks for confirmation and writes a pre-restore safety backup first; `--no-safety-backup` records that you declined rather than skipping silently. The commit itself is two directory renames guarded by a journal, so an interrupted restore resolves on the next start to a complete commit, a complete rollback, or an explicit reconciliation request — never a half-swapped tree. Your Data Protection key ring and existing archives are carried across the swap; they belong to this machine, not to the archive.

Three conflict modes:

| Mode | What it does |
|------|--------------|
| `replace-installation` (default) | Displaces the current installation and commits the archive in its place, rebuilding local secret protection from the portable recovery material. This is the clean-machine recovery path and needs no access to the source machine's credential store. |
| `new-profile-root` | Materializes the archive into an empty directory and leaves the current installation and its secrets untouched. Data only: adopt it with a `replace-installation` restore before using it. |
| `import-selected-sessions` | Merges named Sessions into the live installation. Colliding ids are remapped, attachment payloads that already match are deduplicated, and the archive's file-encryption keys are added to your ring without changing its active key. |

Paths recorded on the source machine are rewritten with typed `--map <kind>=<from>=<to>` mappings covering campaign roots, workspace roots, Codex and Spell roots, and attachment source provenance. Windows and Unix roots interoperate: separators are converted, drive and UNC roots match case-insensitively while Unix roots do not, and mappings that are ambiguous, that escape containment, that collide on one destination, or that name something invalid on the target platform are rejected before anything is staged. Anything no mapping claims is reported rather than guessed at.

Restored attachment snapshots remain readable even when the originating workspace does not exist here — but their live source is marked `WorkspaceUnavailable` and stays unrefreshable until you rebind that workspace and it passes the normal containment, identity, Sanctum, and MCP trust checks. Two things are deliberately not inherited: trusted MCP workspace metadata is withheld rather than installed, and `Host:ListenAny` is reset to `false`. Both are authorization decisions made on the source machine, and neither transfers. The archived master API key is likewise left alone unless you pass `--restore-master-api-key`.

`backup migrate` rewrites a supported archive at the current container format through the authoritative codec, writing a new file and never modifying the source.

If encrypted blobs exist but the restored key set is missing, corrupt, or lacks a referenced key id, Arcanum fails closed and never generates a replacement. `/api/health` and `arcanum doctor` expose a `FileEncryption` check with key availability plus bounded encrypted/legacy-plaintext/corrupt counts, but never key or content data. Legacy plaintext blobs are never silently served; migrate and verify them before relying on a portable backup. Full format, rotation, and atomicity details are in [DESIGN §5.4.6](docs/Arcanum.DESIGN.md#546-versioned-authenticated-blob-storage) and [§5.4.8](docs/Arcanum.DESIGN.md#548-versioned-encrypted-portable-backups).

### Unified data retention and deletion

`Arcanum:Retention` is the single policy surface for the Grimoire, encrypted blob trees, dated JSONL logs, and derived indexes. Automatic sweeps are off by default. Active/archived sessions, Entries, attachments, Saga, and Lexicon rules also default disabled so an upgrade does not begin deleting user conversation or durable-memory content. Enabled defaults cover shorter-lived or derived classes; the exact rules, days, and clamps are in [Compendium's configuration reference](docs/Compendium.README.md#integrations-execution-cost-retention-daemon-and-cli). Inference and guardrail audit writers only append to the current dated JSONL file; they never scan or delete older files. Those files age out only through the same bounded, durable plan/apply path used for every other retention class, whether a sweep is started manually or by the scheduler.

Use the API-backed CLI rather than editing storage directly:

```text
arcanum data status
arcanum data retention show
arcanum data retention set archived-sessions 180
arcanum data prune --dry-run
arcanum --yes data prune --apply
arcanum data delete-session <session-guid>
arcanum data delete-attachment <attachment-guid>
arcanum data reset-memory --scope entry|attachments|workspace|saga|lexicon
arcanum data factory-reset --workspace --dry-run
arcanum data factory-reset --global --dry-run
arcanum data factory-reset --all --apply
```

Dry-run and apply call the same server-owned planner. A plan identifies candidates and reports rows, files, estimated bytes, derived records, blockers, and conflicts by typed data class. Apply rebuilds the plan immediately before mutation; API callers may supply the preview's `planId` as `expectedPlanId` to fail if the candidate graph changed. CLI `--dry-run` never mutates. CLI `--apply` fetches and displays the exact current plan, confirms its id and totals, and binds that id to the apply request without another user step. Under `--json --yes`, the preview remains silent and stdout contains exactly one final apply result. Human mode prints concise operator summaries for status, settings, plan, and apply results; `--json` preserves the exact API payload. Every mutation requires interactive approval or the recursive `--yes` switch and still uses normal API-key authentication.

Deletion follows explicit dependencies. An attachment deletion removes its metadata, encrypted bytes, chunks, embeddings, and index state; a session deletion additionally removes its Entries and Entry embeddings. Workspace chunks and their embeddings move together. Uploaded files are retained while any batch still references them, and in-progress batches are conflicts. Pinned Entries, pinned context/attachments, `retention.protectedSessionIds`, active durable operations, active inference/idempotency work, and outstanding budget reservations block rather than disappear. Accounting uses `InferenceRuns`, `BillableOperations`, `BudgetReservations`, and `CostAdjustments`, plus standalone adjustments and `BudgetAlerts`; it never uses `Sessions.TotalCostUsd`, and its effective age is at least `accountingMinimumDays`. Managed files are captured and revalidated by no-follow identity at deletion time, so a substituted path leaves both bytes and metadata intact and fails closed.

Saga and Lexicon remain separate durable stores. Deleting a source attachment does not silently delete independently retained facts; their typed provenance remains and changes to `Unavailable`/unresolved when the source no longer exists. Use an explicit `reset-memory --scope saga` or `--scope lexicon` only when those stores themselves are the intended target.

Sweeps are bounded by `maxItemsPerSweep`, skip stable blocked rows without spending that candidate quota, checkpoint at the effective checkpoint interval, run as restart-idempotent durable operations, and verify the selected rows, derived records, and owned files after each planned candidate. Prune and explicit mutations start atomically under one retention single-flight lease; elapsed-time heartbeats retain ownership even during one slow candidate. This is candidate-local reconciliation, not a global orphan vacuum. A missing selected row or already-unlinked owned file is a converged state, so repeating an interrupted sweep is safe. Inspect or repair durable state with `arcanum operation`.

Deletion is logical database deletion plus owned-file unlinking, **not a physical secure-erasure guarantee**. SSD wear leveling, copy-on-write filesystems, filesystem snapshots, SQLCipher free pages/WAL copies, operating-system caches, and independent backups can retain recoverable copies. Encryption protects retained bytes but does not make per-record unlinking equivalent to destroying all copies.

Installation reset requires an explicit scope and mode. Workspace scope removes exact-root derived rows and the selected registered Campaign's `.arcanum` tree while preserving global daemon state. Global scope removes installation-wide state, configured credential identities, and daemon registration. All scope captures the current Campaign before running both phases. Dry-run performs no writes. Apply prompts for the exact text `RESET`; automation requires both `--yes` and `--force`. Recognized `.arcbackup` files and nested registered Campaign roots remain in place. An owner-only active record makes interruption resumable, blocks host startup until recovery completes, and is retired only after final file, backup, and credential verification succeeds.

This feature uses the existing canonical tables and file layouts. It adds no schema object and requires no local/test database recreation. If a later retention change alters a canonical schema, the pre-user-data reinstall policy in [DESIGN §5.4.5](docs/Arcanum.DESIGN.md#545-schema-installation-serialization-and-crash-consistency) applies at that time.

### Optional HTTPS

HTTP remains the default on **loopback**. `Arcanum:Host:Https:Enabled` adds a TLS listener; with `Arcanum:Host:ListenAny` / `ARCANUM_HOST_ANY`, HTTPS is **required and exclusive**. A PFX password comes from the exact `CertificatePasswordEnvironmentVariable`, or `ARCANUM_HTTPS_CERTIFICATE_PASSWORD` when that reference is omitted; PEM ignores it. Values never enter configuration or API/Compendium responses. Clients do not bypass TLS validation. PFX vs PEM shapes and Compendium self-signed generation: [Compendium's complete configuration reference](docs/Compendium.README.md#complete-configuration-reference) / [secrets and HTTPS](docs/Compendium.README.md#secrets-and-https).

---

## Distribution and first run

Windows and Linux packages contain separate archives for Arcanum, Compendium, and The Forge plus `SHA256SUMS`. The `arcanum` executable is Native AOT; desktop apps are self-contained multi-file Avalonia folders. These archives are unsigned by default. Windows SmartScreen can warn; optional Authenticode requires the Windows packager's `-Sign` flag and `WINDOWS_CERT_PATH` / `WINDOWS_CERT_PASSWORD`.

Linux:

```bash
tar -xzf arcanum-linux-x64.tar.gz
chmod +x arcanum-linux-x64/arcanum
./arcanum-linux-x64/arcanum setup
./arcanum-linux-x64/arcanum serve
./arcanum-linux-x64/arcanum key show
```

Windows:

```powershell
Expand-Archive .\arcanum-win-x64.zip -DestinationPath .
.\arcanum-win-x64\arcanum.exe setup
.\arcanum-win-x64\arcanum.exe serve
.\arcanum-win-x64\arcanum.exe key show
```

### Guided setup

`arcanum setup` is the guided first run. It walks eight explicit steps — runtime edition and privacy posture, provider endpoint and model, provider credential, optional Perplexity web-research credential, live provider validation, workspace and Campaign, onboarding preset, and the final diff — and then commits. Nothing is written until you accept the plan, so Ctrl+C or end of input at any step leaves configuration, credentials, CLI context, and the workspace registry unchanged. Re-running it and accepting the current values is a no-op, not a reset: the wizard owns only `edition`, `host.listenAny`, `defaultModel`, `workspaces.defaultRoot`, and the selected provider entry.

The wizard authors OpenAI-compatible endpoints, including Ollama and other local model servers through their own `/v1` endpoint. A Familiar has no endpoint and no credential to collect, so add one in Compendium or by hand instead. Validation is one guarded `GET {endpoint}/models` with a strict five-second timeout: non-billable, in-process, and usable before `arcanum serve` has ever started. It tells you which dependency failed — endpoint rejected, TLS failure, authentication failure, model absent, malformed response, timeout, or unreachable.

Credentials go into the OS credential manager (macOS Keychain, Windows Credential Manager, Linux Secret Service) with an owner-only Data Protection mirror for headless hosts, so a finished run is ready for `arcanum run` without exporting anything. Provider credentials still resolve from `ARCANUM_PROVIDER_<NAME>_API_KEY` first when it is set, so a per-process override always wins.

For automation, `--plan` prints the plan and writes nothing, and `--apply` commits without prompting:

```bash
printf '%s\n' "$OPENAI_KEY" | arcanum setup --apply \
  --provider openai --endpoint https://api.openai.com/v1 --model gpt-4o-mini \
  --preset general-assistant --workspace . --provider-key-stdin
```

Secrets are never accepted as arguments. A credential may only arrive on redirected stdin (`--provider-key-stdin`, `--research-key-stdin`) or as an environment reference (`--provider-key-env`, `--research-key-env`), so nothing secret reaches argv, the process table, or shell history. Use `arcanum key list` afterwards to see every credential identity Arcanum owns with presence and status only.

Run as a normal user; elevation is not required. Extract the Arcanum, The Forge, and Compendium archives beneath the same parent directory. `arcanum open ...` discovers the shipped sibling folders (`the-forge-win-x64` / `compendium-win-x64`, or the matching `the-forge-linux-x64|arm64` / `compendium-linux-x64|arm64` folders for the active architecture). The applications can also be launched directly from those extracted archives. Linux shared key discovery requires `libsecret` and a running Secret Service; otherwise The Forge prompts for a key or accepts process-only `THEFORGE_ARCANUM_KEY`.

Local package creation:

```bash
./scripts/packaging/linux/package-linux.sh --version 0.1.0-beta.1 --output-dir ./dist
```

```powershell
.\scripts\packaging\windows\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist
```

Use `-SkipForge` for Windows Arcanum + Compendium only. Cross-OS builds are manual GitHub workflows: `Private beta release (Windows / Linux)` builds all three products; `Build Windows x64 (Arcanum + Compendium)` omits The Forge.

The manual **Release macOS arm64** workflow builds on `macos-15-xlarge`, signs with a Developer ID Application certificate, notarizes all outputs, and creates or updates a draft GitHub Release. Required repository secrets are `APPLE_CERTIFICATE`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_IDENTITY`, `APPLE_ID`, `APPLE_TEAM_ID`, and `APPLE_APP_SPECIFIC_PASSWORD`. Enter a version such as `0.1.0-beta.1`; build metadata is rejected. Outputs are:

- `arcanum-osx-arm64.zip` — signed, notarized folder-based self-contained CLI plus this document as `README.md`; zip is not stapled;
- `compendium-osx-arm64.dmg` — signed, notarized, stapled `Compendium.app`; and
- `the-forge-osx-arm64.dmg` — signed, notarized, stapled `The Forge.app`.

Signing is mandatory in CI; `--skip-sign` is only for local package-structure smoke tests. Spot-check the draft on a clean Mac, then publish it. Rerunning the same version replaces its release assets. Full distribution contracts are in [DESIGN §19.12](docs/Arcanum.DESIGN.md#1912-build-packaging-and-maintenance).

## Current operator limitations

- Tool-child filesystem confinement uses deprecated Seatbelt on macOS and AppContainer plus Job Objects on Windows; Linux fails closed unless unsandboxed process tools are explicitly acknowledged. No platform provides child-process network isolation.
- `workspace_check` is advertised only on an eligible macOS host and remains unavailable on Linux/Windows.
- sqlite-vec is not shipped by default. Managed SIMD Divination streams every matching BLOB row with caller cancellation and bounded top-K memory; runtime grows linearly with corpus size. `/api/meta`, health, and `arcanum doctor` report the active mode and compatibility budget `0` (no total row budget).
- OpenAI support is a compatibility subset. Moderation, image-generation/editing, and audio routes return `501 not_supported`; batch processing supports `/v1/chat/completions` and forces tools off.
- Durable recovery is single-host and handler-driven, not a distributed workflow engine. Live streams and Wards remain ephemeral. A deferred or unsupported/corrupt checkpoint is explicit `ReconciliationRequired`/Degraded health and is repaired with `arcanum operation ...`.
- Subagents are intentionally model-only and cannot recursively delegate because child tools are disabled; there is no depth counter. They inherit no parent transcript, session memory, workspace/Codex/RAG context, or tools; the parent must pass self-contained instructions and any file content explicitly. Attachment files additionally require an opaque id from the parent's current-turn materialized allowlist. A crashed `subagent` durable operation is abandoned safely rather than replayed.

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

Reliable-editing-loop focused filters and platform notes are in [DESIGN §13.6](docs/Arcanum.DESIGN.md#136-reliable-editing-loop-contract-matrix). Do not use `workspace_check` as the bootstrap verifier for an untrusted repository: it executes repository-authored code and itself requires an eligible macOS runtime plus an operator Ward.

---

## CLI quick reference

This section is intentionally condensed. See [`Arcanum.Command.Reference.md`](docs/Arcanum.Command.Reference.md) for the complete command tree and option-by-option behavior.

### First run

```bash
arcanum setup                       # guided wizard; nothing is written until you accept the plan
arcanum setup --plan --json         # machine-readable plan, writes nothing
arcanum key list                    # credential inventory: presence and status only
arcanum run "Hello"                 # the wizard prints the exact next command for your preset
```

### Shell completion

Completion is generated from the canonical command tree, so it offers exactly the commands and options the binary accepts. Generation reads no state and produces identical bytes on any machine.

```bash
arcanum completion install zsh      # names the target, confirms, then writes atomically
```

Install shows the exact destination before asking and prints the sourcing step for that shell. It is a mutation, so a redirected invocation without `--yes` fails closed rather than editing shell configuration unattended. To place the script yourself instead:

```bash
arcanum completion bash > ~/.local/share/bash-completion/completions/arcanum
```

| Shell | Default target | Extra step |
|---|---|---|
| bash | `~/.local/share/bash-completion/completions/arcanum` | `source` it from `~/.bashrc` if bash-completion does not load the directory |
| zsh | `~/.zfunc/_arcanum` | `fpath+=(~/.zfunc)` before `compinit` in `~/.zshrc` |
| fish | `~/.config/fish/completions/arcanum.fish` | none; loaded on next shell start |
| powershell | `~/.config/powershell/arcanum.completion.ps1` | dot-source it from `$PROFILE` |

Completing a live resource — a model, Campaign, Session, Spell, and so on — asks the running host. That path never starts the host, gives up on a short budget rather than making the shell wait, and prints nothing when the host is unavailable, so completion silently falls back to the static tree.

### Discovering commands

```bash
arcanum help                        # list task-oriented topics
arcanum help sessions               # plain-language guide with runnable commands
arcanum run --help                  # options plus runnable examples for one command
```

Every runnable command's `--help` ends with an `Examples:` section. Those examples are parse-tested against the live tree, so they cannot go stale; a command with no safe example says why instead.

A mistyped or removed command exits `2` naming the canonical replacement or the nearest command. It is only printed — Arcanum never runs a suggestion for you. The failing verb is found by the parser rather than by position, so `arcanum campain list` still names `campaign`, and a global option typed before the verb does not suppress the diagnostic.

### Safe resource selection

Commands that target a session, campaign, workspace, prompt, spell, Apprentice, model, provider, MCP server, or diagnostic tool accept an exact ID, an exact case-insensitive name, or a unique name prefix. In an interactive terminal, omit the selector to open a searchable picker; press Escape to cancel before any mutation. MCP server/tool ambiguity may also open that picker in a real interactive terminal. Redirected stdin/stdout and `--json` never prompt or guess: ambiguity and missing selectors exit with candidate summaries so scripts can provide an exact value. Exact IDs retain deterministic scripting behavior.

Pickers page through large collections and show only safe resource-specific columns. Recent choices are stored locally as an owner-only ordering hint, never as tie-breaking authority. Picker output does not include provider endpoints/credential references or MCP URL/command/argument details.

```bash
arcanum campaign show campaign-alpha  # exact name or unique prefix
arcanum prompt render                 # interactive picker when attached to a TTY
arcanum prompt render <exact-guid> --param topic=dragons  # deterministic script
arcanum session show                  # title/campaign/updated picker
arcanum workspace show
arcanum mcp show
```

### Portable backup

Use the typed encrypted workflow for a consistent recovery generation; do not copy a live database or its WAL files:

```bash
arcanum backup create --dry-run
arcanum backup create --scope full
arcanum backup inspect ~/.config/arcanum/backups/example.arcbackup
arcanum backup inspect ~/.config/arcanum/backups/example.arcbackup --decrypt
arcanum backup verify ~/.config/arcanum/backups/example.arcbackup
arcanum backup list
arcanum backup restore ~/.config/arcanum/backups/example.arcbackup --dry-run
arcanum backup restore ~/.config/arcanum/backups/example.arcbackup --yes   # global --yes
arcanum backup migrate ~/.config/arcanum/backups/example.arcbackup -o ~/migrated.arcbackup
```

Create and verify prompt securely when no automation source is supplied; interactive create asks twice. Automation supplies either the *name* of an environment variable through `--passphrase-env` or an inherited descriptor through `--passphrase-fd`, never a literal passphrase argument. Outer inspect and list do not decrypt manifests or prompt. Creation is no-clobber unless `--overwrite` is explicit, and `--dry-run` uses the creation inventory without asking for a recovery passphrase or publishing an archive. Because dry-run never consumes a passphrase source, a parsed negative descriptor or simultaneous `--passphrase-env` and `--passphrase-fd` flags do not restrict the inventory preview; live creation still validates the source it will read. Scope/component and recovery limitations are in [the operator backup section](#encrypted-blob-key-backup-and-recovery); exact flags are in the [command reference](docs/Arcanum.Command.Reference.md#arcanum-backup).

### Unified prompt execution

`arcanum run` is the primary flexible one-turn entry point. The default route is the ordinary Agent Loop; `--research` selects progress-driven server-side research, and `--spell <name-or-unique-prefix>` forces a named Spell through the same production Spell/dependency/tool policy. Only `--research` plus `--spell` conflicts. All routes resolve explicit Campaign, Workspace, Session, and Model first, then active context, current-directory detection, and server defaults. Recursive `--no-context`, `--plain`, and `--json` keep their normal meanings.

```bash
arcanum run "Fix this bug"
cat error.log | arcanum run "Explain this failure"
arcanum run --spell code-review "Review this change" --with @src/Feature.cs
arcanum run --research "What changed upstream?" --sources 8
arcanum run --dry-run --show-content "Show the planned static turn"
```

Positional words form the instruction. Redirected stdin remains a separate untrusted text source, so supplying both never drops or replaces either value. With neither and a real TTY, the command prompts once for one line. Stdin is counted while reading and capped at exactly 10 MiB (10,485,760 UTF-8 bytes); oversized or unreadable input fails before dispatch, with no silent truncation, positional-only fallback, or partial model context.

Repeat `--with @path` to stage current-turn context. Relative paths resolve from the effective working directory; an explicitly supplied absolute path is also accepted. Any strict-UTF-8 text file is eligible regardless of extension, while recognized images pass through the existing Scrying MIME, size, and vision checks. Text diagnostics report UTF-8 bytes, part count, and SHA-256; image diagnostics report decoded bytes and SHA-256. The CLI splits text and stdin into UTF-8-safe 1 MiB parts without a file/part-count ceiling and enforces the existing 32 MiB aggregate request authority; stdin retains its separate 10 MiB reader ceiling, but `--with` files do not inherit it. Typed content is sent for server-side materialization. The client path is a display/resolution input, not server filesystem authority. On a live route, an Attachments-enabled host persists and Session-binds these sources before inference; an Attachments-disabled host keeps them in memory for the current turn. A dry-run never persists them.

`--dry-run` uses the authenticated context-preview path with the resolved route, Spell, preview-only sources, context, output reserve, and common inference flags. It always skips retrieval, automatic semantic Spell routing, search, and provider inference, while an explicit named Spell still resolves. The result is a spend-free static pre-inference plan, not an exact live request: live Agent dispatch may still add local `PatternSnapshot` and `ChronosyncDelta` context. It does not start the selected Agent/research synthesis turn, execute tools, persist an assistant Entry, or persist the staged sources. `--show-content` explicitly reveals model-visible preview content. All live routes accept `--new`, `--unattended`, model/context options, temperature, top-p, seed, repeatable stops, response format (`json` aliases `json_object`), and penalties; Agent and Spell also use `--max-tokens`, while research uses its synthesis token budget. On this permissive unified surface, `--new` suppresses Session continuation even when `--session` is also present. Research additionally accepts optional `--sources`, `--token-budget`, and `--cost-budget`; its positive token budget is the explicit synthesis output policy. With no source target, new-evidence passes continue until source exhaustion/no-progress or cancellation.

### Unified live watch

Use one command family for the existing Session, Apprentice Chronicle, log, MCP, daemon, and host health sources:

```bash
arcanum watch session [session] [--since <entry-guid>]
arcanum watch apprentice [apprentice]
arcanum watch logs [--level information] [--category Api] [--search needle]
arcanum watch mcp
arcanum watch daemons
arcanum watch health [--interval 5]
```

`watch session` and `watch apprentice` are the only spellings for those streams. Session and Apprentice selectors accept the same GUID/name/prefix and interactive-picker forms as their lifecycle commands. All six sources authenticate against the running host; the SSE sources continue to count against the existing global and per-type connection caps.

Terminal mode prints UTC timestamps and event-type colors. SSE comments/keep-alives produce stderr liveness diagnostics and are never printed as data; `[DONE]` ends successfully. Ctrl+C cancels cleanly with exit code `130`. Add recursive `--json` for automation: stdout then contains only one compact source JSON object per line, with no ANSI, heartbeat, `[DONE]`, label, or diagnostic text; diagnostics remain on stderr.

Repeat `--event-type <value>` or `--tool <value>` (`--tool-name` alias) for case-insensitive free-form filters; blank values are ignored and the CLI does not restrict values to a hard-coded enum. Log tool matching includes structured `properties.ToolName` metadata. Log category/search remain free-form, while `--level` is one of `trace`, `debug`, `information`, `warning`, `error`, or `critical` and is validated by the API. `--reconnect` is deliberately opt-in and keeps retrying unexpected disconnects with capped exponential backoff until completion or cancellation. Each attempt warns on stderr that a gap may exist. Session reconnect advances from the last received valid Entry id where possible, but the server replay window is bounded; the other live sources have no cursor. Arcanum never claims that missed events were replayed.

`watch health` polls every five seconds by default and accepts any positive whole-second `--interval`. A well-formed HTTP 503 health response is still rendered as a valid Unhealthy snapshot, including component detail. Filters, reconnect, and polling cadence are invocation-only choices; there are no new configuration keys or persistent user restrictions.

### MCP and diagnostic tools

The MCP family is a safe API client for the existing lifecycle and diagnostic endpoints. Status output includes scope, transport, trust, lifecycle, tool count, and last error. It deliberately omits subprocess commands/arguments, URLs, environment variables, and secrets. `--workspace` selects a workspace-local scope; an omitted selector can open the interactive picker.

```bash
arcanum mcp list
arcanum mcp show [server] [--workspace /server/path]
arcanum mcp start|stop|restart [server] [--workspace /server/path]
arcanum mcp reload [--workspace /server/path]
arcanum mcp trust [/server/path]
arcanum mcp tools [server] [--workspace /server/path]
arcanum mcp invoke <tool> ['{"query":"dragons"}'] [--server name] [--workspace /server/path]

arcanum tool list [--workspace /server/path]
arcanum tool show <tool> [--workspace /server/path]
arcanum tool invoke <tool> ['{"timezone":"UTC"}'] [--workspace /server/path]
```

Both invoke commands accept one JSON object inline, as `@file`, or from redirected stdin; omitted interactive arguments default to `{}`. Response-file expansion is disabled so `@file` reaches the Arcanum argument reader unchanged. Input is capped at 1 MiB and JSON depth 64 before any invocation. MCP results retain the server-owned output cap, report the server/tool, duration, and truncation flag. MCP initialization and HTTP connection establishment retain local deadlines, but an established diagnostic or inference tool call has no Arcanum-owned total request duration; it runs until completion, terminal protocol/provider failure, or caller cancellation.

`mcp invoke` is strictly external-only. `arcanum-internal` is not a diagnostic MCP target, and the Forbidden Art names `execute_command`, `write_file`, `replace_text_block`, `delete_lexicon`, `run_spell_script`, `apply_patch`, and `workspace_check`, plus the two Covenant mutation tools `propose_covenant` and `retire_covenant`, cannot use this path, including when an external server reuses a blocked name. Eligible built-ins use `arcanum tool invoke`; internal and high-risk execution otherwise remains in the Master pipeline with its Ward and Sanctum policy.

Inside that pipeline, `execute_command` returns a bounded stdout/stderr preview. When either stream is larger, it also returns an opaque connection-lifetime handle and the available stream names. The automatically attuned `read_command_output` tool accepts that handle, `stdout` or `stderr`, a byte offset, and an optional per-page byte size; continue with each returned `nextOffset`. Pages use strict UTF-8 and `RandomAccess` and are bounded to one JSON-RPC-safe allocation. Artifacts are owner-only and never exposed by path. A stream's final page closes its `FileOptions.DeleteOnClose` handle immediately; the opaque handle expires after all available streams finish, with caller cancellation, preservation failure, connection disposal, and abrupt exit as cleanup backstops. There is no product-owned output total, but complete stdout and stderr share the existing explicit Sanctum `MaxFileWriteMb` operator policy. Crossing it terminates the process tree, deletes partial artifacts, and reports the measured bytes, limit, saved state, and exact next action instead of silently losing diagnostics.

### Files and asynchronous batches

The native CLI exposes the existing OpenAI-compatible `/v1/files` and `/v1/batches` APIs without opening server storage. A batch can start from local JSONL in one command; the CLI checks the obvious wrapper shape first, uploads it as a batch file, and then creates the server-owned job. Pass an existing `file-*` id instead to skip the upload. The server still owns full request validation, cancellation, recovery, endpoint restrictions, MIME policy, size limits, and status. There is no total requests-per-batch ceiling. The server streams JSONL through internal 64-line processing pages, keeps only a pooled 256 KiB prefix of one physical record before owner-only spill, and retains a measured 64 MiB one-request DTO materialization boundary while later records continue. It reserves explicit token/cost policy per page, durably marks each line before provider dispatch, atomically stores its terminal output or error, and advances exact 64-bit request counters without reopening artifacts during metadata reads. Completed checkpoints publish in input order and are skipped on resume. Cancellation seals every already-claimed unresolved line before publication; after an ambiguous host failure or unexpected pre-publication exception, durable recovery converts a dispatched line without a result into explicit `batch_interrupted_after_dispatch` output instead of deleting or replaying the provider call. Metadata list responses default to 20 rows, retain a 100-row one-response maximum, and continue through an opaque status-bound keyset cursor; worker pickup selects only the oldest rows needed for current free concurrency slots. If the next page is rejected by operator policy, prior output remains downloadable and the error identifies the first remaining line and continuation action. `completion_window` is accepted for OpenAI compatibility but does not expire or delete queued/progressing work; explicit cancellation, terminal retention policy, and startup reconciliation own that lifecycle.

```bash
arcanum file upload ./batch-input.jsonl
arcanum file list --purpose batch
arcanum file show file-0123456789abcdef0123456789abcdef
arcanum file download file-0123456789abcdef0123456789abcdef [--output ./input.jsonl]
arcanum file delete file-0123456789abcdef0123456789abcdef

arcanum batch create ./batch-input.jsonl
arcanum batch create file-0123456789abcdef0123456789abcdef
arcanum batch list [--status in_progress] [--cursor opaque-next-cursor]
arcanum batch show batch_0123456789abcdef0123456789abcdef
arcanum batch wait batch_0123456789abcdef0123456789abcdef
arcanum batch cancel|reset batch_0123456789abcdef0123456789abcdef
arcanum batch output|errors batch_0123456789abcdef0123456789abcdef [--output ./result.jsonl]
```

Lists and detail views include total/completed/failed request counts. `batch wait` uses bounded exponential polling and exits at the first terminal state. Downloads stream through a same-directory temporary file and atomically replace only after success. Default names discard server path components and sanitize the leaf; an existing destination requires interactive confirmation or explicit `--yes`. `file delete` uses the same confirmation boundary. Recursive `--json` emits one source-generated JSON document on stdout and keeps progress/diagnostics on stderr; `/v1` successes are never reinterpreted as `ApiResponse<T>`. A file referenced by any batch input/output/error role—including a terminal batch—is preserved and returns an OpenAI-shaped 409 conflict; deletion can succeed only after no batch role references that file. Batch inserts and artifact-reference updates resolve every non-null file role in the same conditional database write, so a concurrent deletion cannot leave a new batch pointing at absent metadata or bytes. Uploads and generated batch artifacts likewise capture their new encrypted file identity before waiting for the database writer, then revalidate that exact owned file inside the immediate metadata-insert transaction. A reset/delete that commits first makes publication fail without a metadata row; publication that commits first makes both metadata and the same bytes visible to the next reset/delete. Upload failures remain sanitized and cleanup never blindly deletes a replacement.

### Session attachments

The standalone attachment family manages encrypted, versioned snapshots without starting an inference turn. Selectors accept an attachment GUID, exact logical key, or unique logical-key prefix; omit one in an interactive terminal for the bounded picker. `--session` accepts the shared session selector and falls back through active context. Metadata commands never print file bytes.

```bash
arcanum attachment list [session] [--session <session>]
arcanum attachment add <local-path|-> [--mime <type>|--content-type <type>] [--name <filename>] [--session <session>]
arcanum attachment reference <workspace-path> [--workspace <workspace>] [--name <logical-key>] [--session <session>]
arcanum attachment show [attachment] [--session <session>]
arcanum attachment show --privacy
arcanum attachment versions [attachment] [--session <session>]
arcanum attachment refresh [attachment] [--session <session>]
arcanum attachment pin|unpin [attachment] [--session <session>]
arcanum attachment export [attachment] [--output <file>|-o <file>] [--session <session>]
arcanum attachment reveal [attachment] [--session <session>]

arcanum run --session <session> --attachment <bound-guid> "use this snapshot"
arcanum run -c --attachment <bound-guid> "and again"
```

`attachment add` reads a client-local file or `-` for stdin and uploads bytes; it may snapshot a file outside any Workspace because the originating path is neither sent nor retained. Stdin with no MIME/name defaults to `stdin.txt` and `text/plain`. `--mime` is only a hint: the server still validates detected content, strict text encoding/image policy, size, and shared Session budgets. Unsupported binary/PDF/Office content remains a valid `Binary` snapshot with `NotEligible` indexing status; it can be managed and exported even though it cannot be injected as text model context. A maximum-size file remains valid with normal multipart overhead; aggregate declared or chunked bodies beyond the bounded 64 KiB protocol allowance fail before persistence.

`attachment reference` is different: its path belongs to the server host and is resolved only inside the selected registered Workspace or server default. The CLI does not open that path. The server canonicalizes it, checks containment/file identity and Campaign Sanctum, reads it stably, and persists those verified bytes as a refreshable version. A snapshot is always immutable; refresh never rereads a client path or accepts a new path. Changed verified bytes create the next version with its newly detected Text/Image/Binary kind, while an unchanged hash reuses the existing row. Standalone refresh has no model-capability gate because it does not inject content. `versions` exposes that history and `list` shows the latest version of each logical key.

`pin` stores a selected version as durable Session context; `unpin` is harmless when no matching pin exists. Text pins may be admitted implicitly under the existing pin/turn budgets. An image pin remains selected but reports `Unsupported` for implicit materialization; pass its bound GUID with repeatable `ask --attachment` or `chat --attachment` for an explicit vision-capable turn. Direct IDs must belong to the effective Session and share the normal attachment-reference budget.

`export` is the only command that writes plaintext attachment bytes. It streams to a same-directory temporary file and atomically replaces the destination only after a complete authenticated download; an existing file requires interactive confirmation or global `--yes`. `--output -` is refused, so attachment bytes never reach stdout. `reveal` opens the encrypted stored snapshot artifact only when that local path contains an `ARCABLOB` envelope; a remote or mismatched client is directed to export. It does not reveal the live source or a decrypted copy. `show --privacy` prints these rules as disclosure and exits without an acknowledgement prompt. `--json` remains metadata-only with diagnostics on stderr.

### Workspace versus Campaign

A **Workspace** is a registered filesystem access and indexing boundary. A **Campaign** is a persistent project container for sessions, spells, prompts, Codex, and Sanctum policy. Campaigns are exposed as workspaces to filesystem consumers, but the server models remain separate and the CLI does not copy or merge them.

```bash
arcanum workspace register                 # current directory, bundled local server
arcanum workspace current                  # explain Campaign and Workspace containment
arcanum workspace tree                     # saved/current Workspace, server-side listing
arcanum workspace info src/Program.cs
arcanum workspace read README.md
arcanum workspace search "where is startup configured"
arcanum workspace index
arcanum workspace index-status
arcanum workspace chunks --path src/Program.cs
arcanum workspace unregister
```

Workspace file, search, and index commands always call the authenticated server API; they never substitute a direct client filesystem read. Explicit registration paths and paths printed by the CLI belong to the **server host**. Omitting `register [path]` uses the client current directory only because the shipping CLI connects to its bundled loopback server. A future remote client must pass an explicit server path. File-write routes and `Arcanum:Workspaces:EnableFileWrite` are unchanged. When `workspace current` finds a Workspace but no Campaign, it suggests the exact `campaign create` shape for operations that need persistent project state.

### Persistent active context

Use local active context to avoid repeating Campaign, Workspace, Model, and Session options:

```bash
arcanum use campaign campaign-alpha
arcanum use campaign campaign-alpha   # the only active-context selector
arcanum use workspace workspace-alpha
arcanum use model provider/model
arcanum use session 11111111-1111-1111-1111-111111111111
arcanum context current
arcanum context inspect "explain this repository"
arcanum context tools --no-retrieval
arcanum context sources --show-content
arcanum context cost "explain this repository"
arcanum use clear workspace   # one scope
arcanum use clear             # every scope
```

Precedence is explicit option, active context, current-directory resource detection, then server default. Campaign and Workspace containment are detected independently. `--no-context` bypasses saved values for one invocation without disabling directory detection. `context current` explains the source of each effective value. `run` validates saved references before inference, report confirmed stale references before clearing them, warn when the current directory is outside an inherited workspace, and refuse a Session/Campaign mismatch. Explicit options always win and are never persisted merely by use.

The preview commands show the effective provider/model/window, Spell and resonances, tool availability reasons, classified source-token allocation, output reserve, compression decision, and explicit auxiliary work without invoking the main model. Preview content is hidden unless `--show-content`; `--no-retrieval` skips embedding/RAG work.

The state file is owner-only `~/.config/arcanum/cli-context.json` (platform-equivalent Grimoire directory), schema version `1`. It contains resource IDs, safe names/paths, and a model name only; it contains no credentials, prompts, or transcript content and has no server authority. `cli-session.txt` is retained temporarily as a last-session mirror for older flows. The shipping CLI talks to its local loopback host; a future remote-host client must not compare its local current directory with server paths.

All commands run as `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- <cmd>` in development, or `arcanum <cmd>` after an AOT publish.

**Default command:** bare interactive `arcanum` (no arguments) opens the **Command Center** (Terminal.Gui fixed-viewport TUI). Bare non-interactive `arcanum`, or `ARCANUM_NO_COMMAND_CENTER=1`, prints usage and exits **0**. Explicit commands (`serve`, `run`, `--help`, …) stay frameless Spectre/CAF as before.

Command Center list views load 50-line terminal pages and expose the exact next offset when more rows remain; navigation continues until exhaustion instead of silently truncating a resource list. The Sessions and Transcript panes likewise keep only one bounded view page (40 sessions or 200 entries): use `Ctrl+PgDn`/`Ctrl+PgUp` for older/newer session pages and `Ctrl+PgUp`/`Ctrl+PgDn` for older/newer transcript pages. Exact server cursors/offsets preserve the complete durable history; repeated or missing checkpoints fail with restart guidance.

**Global automation contract:** every direct command accepts these flags before or after its verb:

| Flag | Contract |
|---|---|
| `--json` | Write exactly one valid JSON document to stdout and disable terminal control sequences. Typed commands keep their documented shape (for example `doctor`); text commands use `{ "output": "...", "exitCode": 0 }`. Diagnostics remain on stderr. |
| `--plain` | Disable ANSI colors, animations, and the mana bar for this invocation. This does not change `arcanum.json`. |
| `--yes` | Auto-approve command confirmation prompts. Without it, a confirmation required while stdout is redirected fails immediately instead of reading stdin or hanging CI. |
| `--no-context` | Bypass saved Campaign, Workspace, Model, and Session defaults for one invocation; explicit options and independent current-directory Campaign/Workspace detection still apply. |

The closed exit-code set is `0` success, `1` generic/runtime error, `2` invalid command line or configuration/confirmation error, `3` network error, and `130` cancellation. Unexpected failures print fixed redacted copy only: no raw exception message, stack trace, path, PII, or credential.

Examples:

```bash
arcanum doctor --json | jq .
arcanum --json operation list | jq -r '.output'
arcanum operation list --plain
```

**Command Center:** interactive Terminal.Gui workbench (sessions sidebar, transcript, composer, HITL/Ward hard modals). Bare interactive `arcanum` opens it; non-interactive / `ARCANUM_NO_COMMAND_CENTER=1` → usage. Slash allowlist and attach flows: [complete command reference](docs/Arcanum.Command.Reference.md#bare-arcanum-command-center).

Attachment status is authoritative and versioned: `/attachments` shows `[Snapshot]`, `[Live]`, or `[Stale]`, the snapshot hash loaded into context, and tracked-source observations. External workspace edits are debounced through `FileSystemWatcher` and rechecked by the host; use `/attachments refresh <name>` to securely load the current version after backend confirmation.

Session branching is first-class in Command Center. `/fork` copies the complete active session; select a transcript entry and use `/fork at` for an inclusive cutoff branch. Select an assistant answer and use `/fork alternative` to branch before it and regenerate from the preceding user prompt; generation starts only after the new branch opens. `/branch parent` and `/branch child` move through visible lineage. A compact `⑂` marker identifies branches in the header and session pane without changing its newest-updated-first order. Large attachment-bearing forks require `/fork confirm`. The new branch is opened only after its transcript and attachment metadata reload; any fork failure leaves the original session unchanged. Durable sessions have no total entry-count or fork-depth ceiling. The pre-existing `sessions.maxPinnedEntries` admission setting is unchanged outside issue #55; long transcripts remain pageable, Campaign Logger advances timestamp-group-safe checkpoint pages, and only one provider turn's actual context window is finite.

Persistent session context is managed with `/context`, `/context pin <kind> <target>`, and `/context unpin <pin-id>`. Kinds are `file`, `directorySnapshot`, `symbolRange` (`path:start-end`), `sessionEntry`, `attachment`, `url`, and `diagnostic`. Pins survive host and session restarts. File and symbol-range pins open a no-follow single-link regular-file handle and retain only bounded output; total source length is streamed rather than used as an admission gate. File pins hash the complete accepted handle incrementally on every turn; modified, deleted, inaccessible, linked, or workspace-escaping targets are shown to the model with an explicit stale/error status rather than silently reusing bytes. Symbol ranges stream their selected lines and normalize CRLF output. Directory snapshots retain a per-materialization listing bound. Materialization adds no second per-turn pin-count ceiling: it considers every pin already accepted by the existing session-management contract. One pin retains at most 64 KiB and one turn at most 256 KiB, with byte-excess pins reported as deferred. Materialized values are source-labeled untrusted data, participate in normal context/mana estimates, and do not change transcript `Entries.IsPinned` compression behavior. Existing `@path` text/image staging remains unchanged and turn-scoped.

**Ephemeral reasoning:** live `run` Agent/Spell turns render client-safe reasoning in a dimmed, labeled block separate from the Mage answer; their reasoning buffer has a 64 KiB default cap with an explicit truncation marker, and reasoning is coalesced on the same refresh cadence as answer tokens. Command Center stops its synthetic Thinking indicator and refreshes the header exactly once on the first token or reasoning frame, coalesces reasoning into one separately bounded in-memory `Reasoning (ephemeral)` entry, and preserves both the source entry and exact line offset of a scrolled multiline viewport. Reasoning is never appended to stdout answer text, mana totals, structured output, or reloaded session history.



**Operator communication tools (canonical catalog):** `ask_human` (attended streaming only — wait for operator), `petition_dungeon_master` (async Apprentice escalation; may send Critical Comm Link), `send_commlink_alert` (one-way external notification; no replies). Comm Link webhooks receive generic JSON (`title`/`body`/`severity`/`source`/`timestampUtc`) — Telegram/WhatsApp need a relay.

**Auto-start serve:** interactive Command Center and `run` spawn `arcanum serve` on definite no-listener (refused). Each health probe gets 2 seconds; an already-listening unhealthy host is retried for 3 seconds; post-spawn readiness is observed for 20 seconds; eight consecutive unauthorized responses after a key exists classify an authentication mismatch. These foreground budgets never terminate the spawned host. Disabled via `ARCANUM_NO_AUTO_SERVE=1`. Never auto-acks ListenAny. On failure, retry, run `arcanum doctor`, verify `arcanum key show`, and inspect `~/.config/arcanum/logs/auto-serve-bootstrap.log`.



| Command | Purpose |
|---------|---------|
| *(bare)* | Open Command Center (interactive TTY). Non-interactive / `ARCANUM_NO_COMMAND_CENTER=1` → usage, exit 0. |
| `serve [quit]` | Run the host (default loopback :5001), or ask the authenticated running host to shut down with `serve quit`. ListenAny is HTTPS-only + first-run ack. Auto-launched suppresses key print. Details: [DESIGN §5](docs/Arcanum.DESIGN.md#5-hybrid-hosting-model). |
| `run [prompt...]` | The one-shot turn entry (the `claude -p` analog): unified Agent (default), `--research`, named `--spell`, or spend-free static `--dry-run` planning. `-c`/`--continue` resumes the most recent Session and `-r`/`--resume [<id>]` a named one; `-p`/`--print` forces headless behavior. Positional input composes with bounded 10 MiB stdin; repeat `--with @path` for current-turn arbitrary-extension UTF-8 text or Scrying images. Live sources use normal attachment persistence when enabled; dry-run sources never persist. Resolves Campaign/Workspace/Session/Model context and supports the common inference flags plus research bounds. Only `--research` + `--spell` conflicts, and supplying more than one of `--session`/`--continue`/`--resume` exits 2; recursive `--plain`/`--output-format`/`--print`/`--verbose` apply normally. |
| `use campaign\|workspace\|model\|session <value>` | Validate and save an active local default without modifying server rows. |
| `use clear [scope]`, `context current` | Clear saved context or show effective values, sources, warnings, and the state-file path. |
| `context inspect [prompt]`, `context tools`, `context sources`, `context cost [prompt]` | Read-only production context preview. `context cost` absorbs the former top-level `mana`. All accept `--show-content`, `--no-retrieval`, `--campaign`, `--workspace`, `--model`, `--session`, recursive `--no-context`, and `--json`. |
| `look` | Print the Eye of the World workspace snapshot (no HTTP). |
| `doctor` | Subsystem diagnostics across paths, permissions, configuration syntax **and semantics**, credential stores and the Data Protection key ring, SQLCipher key material / `quick_check` / `foreign_key_check` / WAL, encrypted blob inventory, durable operations read locally so a crashed host cannot hide them, PID/maintenance-lock/disk residue, MCP config, the tokenizer, and the running host's own per-subsystem verdicts. Each finding carries a stable `subsystem.snake_case` id, a typed outcome (`Skipped`/`Healthy`/`Unavailable`/`Degraded`/`Unhealthy`), and the exact next command. A bare run is strictly read-only; `--only`/`--skip` scope it, `--include-network` adds one non-billable `/models` probe per provider, `--strict` promotes degraded to a nonzero exit, and `--repair <id>` plans a narrow idempotent repair that changes nothing without `--apply`. `--fix-permissions` is the no-prompt alias for the owner-only permission repair. `arcanum doctor list` / `explain <id>` make every id discoverable. An unreachable API stays a non-fatal warning. `--json` emits the structured `DoctorReport` — the pre-existing `healthy`/`name`/`status`/`detail` shape, extended with `id`, `subsystem`, `outcome`, `remedies`, and `repairs` (exit code 0 if healthy, 1 otherwise). Architecture: [DESIGN §4.4.2](docs/Arcanum.DESIGN.md#442-subsystem-diagnostics-and-safe-repairs-arcanum-doctor). |
| `watch session\|apprentice\|logs\|mcp\|daemons\|health` | Follow the six authenticated live sources with shared UTC/color/heartbeat/`[DONE]`/Ctrl+C/NDJSON behavior. Repeat free-form `--event-type` and `--tool` filters; `watch logs` adds `--level`/`--category`/`--search`; `watch session` adds `--since`; `watch health` adds `--interval` (default 5). `--reconnect` is opt-in, indefinitely retries unexpected SSE disconnects with capped backoff, and always warns of possible gaps/no replay guarantee. |
| `backup create\|inspect\|verify\|list\|restore\|migrate` | Plan/create an owner-only encrypted `.arcbackup`, read its safe outer header or authenticated manifest, verify every entry and included Grimoire snapshot, or list archive headers without decryption. Create uses typed scopes/components, online SQLite backup, hidden/environment-reference/inherited-descriptor passphrase input, dry-run, and explicit no-clobber/overwrite behavior. `restore` verifies completely, stages a whole generation, migrates schema through the authoritative installer, remaps typed machine-specific roots, rebuilds local secret protection, and commits atomically or rolls the prior installation back; `--dry-run`, three conflict modes, confirmation, and a pre-restore safety backup are part of the contract. `migrate` rewrites a supported archive at the current format into a new file. |
| `data status\|retention show\|retention set\|prune\|delete-session\|delete-attachment\|reset-memory\|factory-reset` | Inspect typed retained stores, configure policy, preview deletion plans, or perform confirmed durable deletion. Installation factory reset requires one of `--workspace`/`--global`/`--all` and one of `--dry-run`/`--apply`; noninteractive apply also requires `--yes --force`. Recognized backups and excluded nested Campaign roots are preserved. |
| `data encryption status\|migrate\|verify\|rotate-key` | Inspect mixed-mode state; resumably encrypt legacy blobs; authenticate/decrypt/hash-check every blob; or create a new key and incrementally rotate before retiring unreferenced old keys. Worker commands accept `--max-concurrency` and `--max-bytes-per-second`; output contains aggregate files/bytes and issue categories, never names or paths. |
| `key show` | Print the stored master API key from the OS credential store (with `security.dat` fallback) to **stderr**. CLI-only; no HTTP. |
| `key set` | Store a master API key into the OS credential store (mirrors to `security.dat`). Argument or stdin / interactive secret prompt. |
| `key provider set\|status\|delete perplexity` | Manage the Perplexity key used by native `web_search`. Status never prints the secret; all operations are CLI-only and perform no HTTP. |
| `search <query>` | Search without a chat prompt. Options: `--count`, `--freshness day\|week\|month\|year`, repeatable `--include-domain` / `--exclude-domain`, `--save`, `--attach-to-session`, and recursive `--json`. Final citations stay on stdout. |
| `browse <url>` | Read bounded page Markdown through the typed server workflow. `--render static\|javascript` is explicit; unavailable JavaScript rendering degrades with a static retry hint. Supports `--save`, `--attach-to-session`, and `--json`. |
| `research <question>` | Progress-driven server-side research with citations. Options: optional positive target `--sources`, `--model`, explicit positive `--token-budget`, optional `--cost-budget`, `--continue-session`, `--format terminal\|markdown\|json`, `--save`, and `--attach-to-session`. New-source passes continue until the target, source exhaustion/no-progress, cancellation, explicit policy, or provider/safety failure. Progress/terminal reason use stderr; final content uses stdout. |
| `config path\|show\|get\|set\|validate\|edit\|open` | Inspect or change `arcanum.json` without manual file discovery. Host API first; explicit canonical local bootstrap on unavailability; redacted reads, typed dot paths, full-snapshot validation, secure sensitive input, and atomic writes. |
| `lore list\|get\|set\|delete` | Operator key-value memory via `/api/lore` (needs `serve`). `list` follows all advancing API pages without a client-owned total-page ceiling and fails explicitly on invalid/no-progress offsets. Args: `get <KEY>`, `set <KEY> <VALUE>`, `delete <KEY>`. |
| `daemon install\|uninstall\|status` | OS background-service lifecycle. |
| `daemon jobs\|initiative\|alert` | Unseen Servant inspection + Comm Link smoke test (needs `serve`). `daemon jobs` shows **Last run** / interval from persisted watermarks (survive restart), **Next due** reconstructed from watermark + interval, and **Last result** (process-local diagnostic text). `daemon initiative <JOB_NAME> <MINUTES>` sets adaptive interval. `daemon alert <MESSAGE>` options: `--title`/`-t` (default `"Arcanum alert"`), `--severity`/`-s` (`Info`\|`Warning`\|`Critical`, default `Warning`), `--source`. |
| `campaign list\|show\|create\|update\|delete\|export\|import\|codex\|spells\|prompts\|sessions` | The Forge campaign registry via `/api/campaigns` (needs `serve`). Active Campaign context is selected with `use campaign`, which is the only selector. Resource-taking verbs accept optional ID/name/prefix selection. |
| `session list\|show\|entries\|fork\|rename\|archive\|export\|rest\|attachments\|delete-entry\|pin-entry\|unpin-entry\|compact\|divine` | Manage the complete session lifecycle through the API (needs `serve`). Management only: continuation is `run -c` / `run -r [<id>]` / `run --session <id>` (and the same flags on Command Center), and live streaming is `watch session`. Session arguments accept a GUID/title/prefix or open the interactive picker when omitted. `list` supports `--campaign`, `--status`, `--search`, `--model`, `--from`, and `--to`. `show` reports status, campaign, entry/attachment counts, token/cost telemetry, and fork parent. `fork` supports `--title`, `--up-to-entry`, and destination `--campaign`; `export` supports `json`/`markdown`. Delete-entry requires confirmation (`--yes` for redirected use). Memory commands do not bypass `Arcanum:Features:MemoryManagement`. Read commands support `--json`. Archived sessions can still be shown, exported, and forked. |
| `memory status\|sources\|search\|explain\|lexicon` | Inspect what Arcanum retains without conflating stores (needs `serve`). `status [session]`, `sources [session]`, and `explain [session]` distinguish persisted counts, feature gates, provenance, retention, and conditional next-turn eligibility. `search <query>` accepts `--scope session\|attachments\|workspace\|saga\|lexicon\|all` (default `all`, always displayed), plus optional `--session`/`--workspace`; every hit reports provenance and retention and no hit is promoted. `lexicon list\|show\|search\|delete` exposes the named entity store; delete is item-scoped and confirmed. There is no generic `memory delete`. |
| `workspace list\|current\|register\|show\|tree\|info\|read\|search\|index\|index-status\|chunks\|unregister` | Register, resolve, inspect, search, index, and unregister server-host Workspace boundaries through `/api/workspaces` (needs `serve`). `show` accepts ID/name/path; there is no `get` spelling. Omitted selectors use saved Workspace context, then current-directory containment. |
| `saga list\|divine\|delete\|stats` | Saga long-term associative memory via `/api/saga/*` (needs `serve`). `list` (options `--query`, `--session`, `--limit`, `--offset`) and `stats` are always available; `divine <QUERY>` (option `--limit`) requires `Arcanum:Features:Embeddings` + `Arcanum:Features:Saga`; `delete <ID>` removes a single memory. See [Arcanum.DESIGN.md §21.9](docs/Arcanum.DESIGN.md#219-saga-long-term-associative-memory). |
| `spell list\|show\|create\|update\|delete\|search\|validate\|execute\|versions\|export\|import\|cast\|clone` | Spell CRUD + execution via `/api/spells` (needs `serve`). Legacy direct listing stays array-shaped; resource selection follows 50-item opaque-cursor pages, and Command Center `/spell list [opaque-cursor]` prints the exact continuation without changing Forge callers. `create`/`update` require `--workspace`; `--body`/`--goal`/`--template`/`--plan`/`--inquisitor` accept inline text or `@filename`; `execute` prints the response text plus a tool-call summary (stderr) when tools ran (`--version` takes a **string label**); `cast <name>` is a dry-run system-prompt preview — no inference tokens consumed; `clone <name> --new-name <n>` clones a spell into the workspace. |
| `spell version create\|update\|activate` | Named spell version files (`SPELL.v{label}.md`) via `/api/spells/{name}/versions` (needs `serve`). `create`/`update <name> --version <label> --body <text\|@file>`; `activate <name> --version <label>` swaps the version into `SPELL.md`, printing where the previous content was preserved. |
| `prompt list\|show\|versions\|create\|update\|delete\|render\|test\|execute\|export\|import\|clone` | Prompt CRUD + rendering. Resource-taking verbs accept optional ID/name/prefix selection; `render`/`execute` accept repeatable `--param key=value`. |
| `ward list\|show\|resolve` | Ward approval gates via `/api/wards` (needs `serve`). `resolve <id>` requires exactly one of `--allow`/`--deny` plus optional `--reason`. |
| `trial run` | The Proving Grounds via `POST /api/proving-grounds/trials/run` (needs `serve`). `--target spell\|prompt\|apprenticeGoal` + `--target-value`, repeatable `--inquisitor` (JSON or `@file`) and `--var key=value`; exits `1` when the Trial fails. |
| `apprentice list\|show\|create\|delete\|start\|pause\|resume\|cancel\|reweave\|intervene\|cast` | Apprentice orchestration. Resource-taking verbs accept optional ID/name/prefix selection and picker cancellation never mutates. Live events are `watch apprentice`. |
| `model list\|show`, `provider list\|show` | List/select configured inference resources. Detail output omits endpoints and credential references. |
| `mcp list\|show` | List/select safe MCP server status without command, URL, arguments, or working-directory details. |
| `model list` | List models from the latest successfully persisted provider snapshot via `GET /api/models` (needs `serve`); endpoint redacted. Non-retention runtime consumers still require restart to adopt the change. |
| `provider list` | List providers from the latest successfully persisted snapshot via `GET /api/providers` (needs `serve`); endpoint redacted and only the credential environment-variable reference returned. Non-retention runtime consumers still require restart to adopt the change. |
| `operation list\|show\|cancel\|retry\|reconcile` | Inspect and repair the durable operation ledger via authenticated `/api/operations*` routes (needs `serve`). `list` accepts `--kind` / `--state`; `show <id>` returns only safe checkpoint presence/version/summary; `cancel <id>` requests `Cancelling`; `retry <id>` resets failed/abandoned/repair-required work; `reconcile` processes every recoverable operation through bounded pages/concurrency and exits 2 when operator repair remains. Startup waits at most 10 seconds for readiness, then continues periodic background recovery until completion or shutdown. |

**Inference flags** (`run`): `--temperature`, `--top-p`, `--max-tokens`, `--seed`, `--stop`, `--response-format` (`json` aliases `json_object`), penalties, `-C`/`--campaign`, `-w`/`--workspace`, and `-s`/`--session`. Scrying: `run --with @path`, or `@path` inside Command Center. Full option ranges, slash commands, context precedence, and exit behavior: [complete command reference](docs/Arcanum.Command.Reference.md).
