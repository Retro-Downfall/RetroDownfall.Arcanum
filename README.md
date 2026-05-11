# Retro Downfall Arcanum

A .NET 10 local-first AI assistant with an encrypted conversation store, MCP tool integration, and a rich terminal REPL — with a **multi-provider** inference hub (Ollama and OpenAI-compatible HTTP APIs).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- At least one configured inference provider under `Arcanum:Providers` in `arcanum.json` (see **Configuration**). For local [Ollama](https://ollama.com/), install it, ensure it is reachable at the `Endpoint` you configure, and list each model id you use under that provider’s `models` array. Pull models with `ollama pull <id>` as needed — Arcanum does not hard-code model names.

## Build

```bash
dotnet build RetroDownfall.Arcanum.slnx
```

Transitive NuGet dependencies are lifted to a patched **Microsoft.Bcl.Memory** (currently **10.0.7**, declared in [`Directory.Build.props`](Directory.Build.props)) to address **CVE-2026-26127** (DoS in Base64Url decoding). After bumping major packages, run `dotnet list package --vulnerable` and a **Native AOT publish** of the CLI (see below) to confirm nothing regressed.

## First-run setup

Arcanum requires an API key for all `/api` and `/v1` routes. Send `X-Arcanum-Key`, or `Authorization: Bearer <KEY>` for OpenAI-compatible clients. On first startup the key is generated automatically:

```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- serve
```

The raw Base64 key prints to stdout once. It is encrypted via ASP.NET Core Data Protection and stored at `{ApplicationData}/arcanum/security.dat`. To regenerate, delete `security.dat` and restart.

For F5 debugging without Spectre, use the DevHost instead:

```bash
dotnet run --project src/RetroDownfall.Arcanum.Api.DevHost/RetroDownfall.Arcanum.Api.DevHost.csproj
```

## Configuration

Settings live in a per-user directory (created on first run):

| OS | Path |
|----|------|
| Windows | `%USERPROFILE%\.config\arcanum\` |
| macOS | `~/.config/arcanum/` |
| Linux | `~/.config/arcanum/` |

Place an optional `arcanum.json` in that directory. Environment variable override prefix: `ARCANUM_` (double underscores for nesting, e.g. `ARCANUM_Arcanum__Providers__0__Endpoint` or `ARCANUM_Arcanum__Providers__1__ApiKey` for secrets).

| Setting | Purpose |
|---------|---------|
| `Arcanum:Host:CorsAllowedOrigins` | JSON array of CORS origins (default: localhost loopback). Use `["*"]` for permissive (browser callers from any origin can read responses — operator-equivalent risk if the API key leaks). |
| `Arcanum:Host:EnableScalarUi` | When **`true`**, mounts the Scalar interactive API reference at **`/api/scalar`**. Default **`false`** (Scalar's inline JS/CSS bootstrap conflicts with Arcanum's strict CSP posture). The OpenAPI JSON document at `/api/openapi/v1.json` is always available. |
| `Arcanum:Host:SystemFingerprint` | Optional stable identifier surfaced as **`system_fingerprint`** on OpenAI-shaped `/v1/chat/completions` responses. When unset (default), Arcanum derives one from the host assembly's informational version (for example `arcanum-0.1.0-beta`). |
| `Arcanum:Host:ListenAny` | When **`true`**, Kestrel binds to all network interfaces (`ListenAnyIP`) instead of loopback. Default **`false`**. The environment variable **`ARCANUM_HOST_ANY=1`** is still honored as an override. |
| `Arcanum:Host:MaxRequestBodyBytes` | Kestrel `MaxRequestBodySize` (bytes). Default **10 MiB**; clamp 256 KiB – 1 GiB. |
| `Arcanum:Host:RateLimit:Enabled` | When **`true`**, mounts a fixed-window rate limiter on `/api` and `/v1` (default **`false`**). Partition key = `X-Arcanum-Key` header value when present, else `Authorization`, else remote IP. Excess requests return **HTTP 429** unless `QueueLimit > 0`. |
| `Arcanum:Host:RateLimit:PermitLimit` | Requests permitted per partition per window. Default **120**; clamp 1 – 1,000,000. |
| `Arcanum:Host:RateLimit:WindowSeconds` | Fixed-window length (seconds). Default **60**; clamp 1 – 86,400. |
| `Arcanum:Host:RateLimit:QueueLimit` | Maximum queued requests per partition (served once the window resets). Default **0** (no queueing). Clamp 0 – 1,000,000. |
| `Arcanum:CommLink:WebhookTimeoutSeconds` | Timeout (seconds) for the named **`HttpClient("CommLinkWebhook")`**. Default **15**; clamp 1 – 120. |
| `Arcanum:CommLink:AllowedSchemes` | JSON array of URI schemes the webhook dispatcher is permitted to call. Default **`["https","http"]`**. Use **`["https"]`** to require TLS. URLs whose scheme is not in this list are skipped with a warning. |
| `Arcanum:Intelligence:MaxToolInferenceRounds` | Hard cap on agentic tool rounds per inference turn. Beyond this the hub fails the turn with **`Hub.ToolLoop`**. Default **8**; clamp 1 – 64. |
| `Arcanum:Intelligence:CompressionPreflightMinMessages` | Minimum assembled-message count before context-compression preflight runs. Default **6**; clamp 0 – 100. |
| `Arcanum:Intelligence:PerMessageTemplateOverheadTokens` | Per-message overhead (tokens) added to the pre-flight count to approximate chat-template framing. Default **4**; clamp 0 – 32. |
| `Arcanum:Intelligence:TokenizerEncoding` | Tiktoken encoding name used by `InferenceTokenizerResolver`. Default **`o200k_base`**. Unknown encodings log a warning and fall back to the default so the hub never throws on misconfig. |
| `Arcanum:Grimoire:MaxMessagesPerConversationLoad` | Maximum messages loaded into memory by `GetConversationAsync` (most recent N, in chronological order). Default **1000**; clamp 50 – 100,000. |
| `Arcanum:Cli:DoctorHealthTimeoutSeconds` | Timeout (seconds) for the `arcanum doctor` `/api/health` probe. Default **2**; clamp 1 – 60. Raise it on slow startups (cold containers, hardware-accelerated provider warmup). |
| `Arcanum:Bureau:Enabled` | **Reserved** for the future Bureau integration (cross-host coordination layer; not yet implemented). Setting to `true` is a no-op today — the property is kept on the binding surface so operator configs don't break across upgrades and so the Bureau feature can light up without a configuration migration. |
| `Arcanum:Host:EnableEnterpriseTelemetry` | When `true`, Serilog also writes structured JSON logs to the console (for log shippers). Rolling JSON file logs are always enabled. |
| `Arcanum:Security:ApiKeyCacheTtlSeconds` | TTL (seconds) for the in-memory SHA-256 digest of the expected API key. Lower TTL picks up rotation faster; default **`30`** (clamp 1–3600). |
| `Arcanum:Perception:AllowedWorkspaceRoots` | Optional JSON array of absolute directory roots that **`GET /api/perception/look`** is permitted to scan. Empty (default) keeps the historical behaviour (any directory the process can read, API key still required); when non-empty, `directory` must resolve under one of these roots or the endpoint returns **`403`**. |
| `Arcanum:Intelligence:ToolOutputCapBytes` | Maximum bytes captured for **`execute_command`** and **`run_spell_script`** combined `stdout`/`stderr` (split evenly per stream). Output beyond the cap is truncated with a `[truncated: …]` marker. Default **1 MiB** (clamp 64 KiB – 64 MiB). |
| `Arcanum:Daemon:MaxConcurrentJobs` | Hard cap on Unseen Servant jobs that may run concurrently. Default **`8`** (clamp 1–1024). Excess due jobs are deferred to the next tick. |
| `Arcanum:Daemon:ShutdownDrainTimeoutSeconds` | Wait (seconds) for in-flight Unseen Servant jobs during host shutdown before logging that they did not drain. Default **`10`** (clamp 0–600; `0` disables waiting). |
| `Arcanum:Daemon:Jobs` | **Unseen Servant**: JSON array of scheduled headless inference jobs when the API host is running. Each object supports `name` (string), `intervalMinutes` (int, clamped 1–10080), `targetSpell` (string: matches a spell’s YAML `name` or the parent folder of `SPELL.md`), and `enabled` (bool, default `true`). Jobs use an empty `WorkingDirectory` so spells resolve from the global spell tree under your Arcanum config directory (`spells/`). **Phase 2 (Adaptive initiative):** while a job runs headlessly, the in-process MCP tool `adjust_initiative` (`job_name`, `interval_minutes`) can change that job’s polling interval for the lifetime of the process; values are clamped to 1–10080 minutes and are not persisted across restarts. The kickoff prompt includes the current effective interval. **Phase 4 (Stateful lore, gated):** when `Arcanum:Intelligence:EnableLoreSystem` is `true`, each run pre-fetches Grimoire lore at `daemon_state_{job.Name}`, injects it into the kickoff, and instructs the model to persist cross-cycle state with `scribe_lore` on that key; if `GetLoreAsync` fails, a warning is logged and the job runs with no prior state. When `EnableLoreSystem` is `false`, the kickoff stays the Phase 1 stateless prompt (no lore fetch, no `scribe_lore` instructions). Headless kickoffs also instruct the model to call in-process MCP **`use_commlink`** for high-alpha / critical operator alerts (**Comm Link**). |
| `Arcanum:CommLink:WebhookUrl` | Optional absolute URL for **Comm Link** outbound JSON `POST` alerts (Discord/Slack/custom). When unset, `use_commlink` and **`POST /api/commlink/send`** succeed but only log a warning—no HTTP is performed. |
| `Arcanum:Intelligence:ContextWindowCompressionThreshold` | Integer percentage (**default `85`**, clamped **50–100**) of each provider’s `contextWindowLimit` used as the pre-flight budget before read-time compression swaps older Grimoire turns for **`Conversation.Summary`**. Leaves headroom for chat-template framing and tool schemas not counted in the estimate. |
| `Arcanum:Intelligence:EnableContextCompression` | When **`true`** (**default**), the API hub estimates tokens and may compress context for inference (see **Context compression** below). When **`false`**, no tokenizer work runs and full Grimoire history is always sent. |
| `Arcanum:Intelligence:EnableTokenTracking` | When **`true`** (**default**), each successful inference turn with a bound conversation increments **`Conversation.TotalTokensUsed`** in the Grimoire from the model-reported **`total_tokens`**. When **`false`**, NDJSON and OpenAI **`usage`** fields are still returned; only persistence of cumulative totals is skipped. |
| `Arcanum:Intelligence:ExecuteCommandTimeoutSeconds` | Hard wall-clock cap (seconds, clamped 1–600) for MCP **`execute_command`** and **`run_spell_script`**. Operator cancel (e.g. Ctrl+C during a streaming turn) still kills spawned process trees immediately; this setting is the unattended backstop. |
| `Arcanum:FastModel` | Optional model id for **internal background inference** (Campaign Logger summarization). When non-empty, must match a `models` entry on some provider. When unset, the Campaign Logger uses **`Arcanum:DefaultModel`**, then the first configured model (same rules as an explicit `PingRequest.Model`). Use a smaller/cheaper model here to save compute vs interactive chat. |
| `Arcanum:Cli:ShowManaBar` | When **`true`** (**default**), the **`chat`** REPL prints the mana bar (session `total_tokens` vs resolved provider context window) before each prompt. Set **`false`** to hide it. |


- The Grimoire (`WorkspaceContexts` table) stores JSON `PatternSnapshot` baselines to power Chronosync Reporting.

The `serve` host registers a **configurable** CORS policy. By default only localhost loopback origins are allowed (`http://localhost:5001`, `http://127.0.0.1:5001`, `http://localhost:3000`, `http://127.0.0.1:3000`). Override via **`Arcanum:Host:CorsAllowedOrigins`** (JSON array of origin strings). Use **`["*"]`** to allow any origin — useful for browser UIs like LibreChat, **with the explicit understanding that a leaked API key could then be exfiltrated from any web page the operator visits**.

The interactive Scalar API reference UI at **`/api/scalar`** is **disabled by default** (it bootstraps with inline JavaScript and CSS, which would violate the Arcanum CSP posture). Set **`Arcanum:Host:EnableScalarUi: true`** to mount it; when enabled, the route is served with a `Content-Security-Policy` header that only permits `self` resources plus the `'unsafe-inline'` directives Scalar requires to render, and forbids framing (`frame-ancestors 'none'`). `/api/openapi/v1.json` is always available regardless of this setting.

Minimal example (local Ollama + OpenAI-compatible DeepSeek; **put API keys in environment variables**, not in `arcanum.json`):

```json
{
  "Arcanum": {
    "Host": {
      "Port": 5001,
      "EnableEnterpriseTelemetry": false
    },
    "DefaultModel": "deepseek-chat",
    "FastModel": "mistral:latest",
    "Providers": [
      {
        "name": "Local Ollama",
        "type": "Ollama",
        "endpoint": "http://localhost:11434",
        "models": ["mistral:latest"],
        "contextWindowLimit": 8192
      },
      {
        "name": "DeepSeek",
        "type": "OpenAICompatible",
        "endpoint": "https://api.deepseek.com/v1",
        "apiKey": null,
        "models": ["deepseek-chat"],
        "contextWindowLimit": 8192
      }
    ],
    "Cli": {
      "Theme": "SystemDefault"
    },
    "Daemon": {
      "Jobs": [
        {
          "name": "Example sweep",
          "intervalMinutes": 60,
          "targetSpell": "MySpellFolderName",
          "enabled": false
        }
      ]
    }
  }
}
```

Set the DeepSeek key without committing it, for example:

`export ARCANUM_Arcanum__Providers__1__ApiKey='your-key-here'`

`Arcanum:DefaultModel` must match a `models` entry on some provider (case-insensitive; Ollama-style `:latest` tag matching is supported). If omitted, the first model of the first provider is used. **`Arcanum:FastModel`** follows the same matching rules when set; it is optional and only affects Campaign Logger headless summarization (see **Context compression**). OpenAI-compatible `endpoint` values must include the path prefix expected by that host (often `/v1`). The same wire shape covers DeepSeek, Groq, GitHub Models, LM Studio, and similar servers. Keyless local OpenAI-compatible servers can omit `apiKey` (the host sends a placeholder credential understood by those servers).

Provider `type` is `Ollama` or `OpenAICompatible` (JSON enum name).

## Database migrations (EF Core)

The Grimoire (encrypted SQLite via SQLCipher) uses EF Core 10 with a compiled model under `src/RetroDownfall.Arcanum.Infrastructure/Generated/`. Beyond conversations and lore, the Grimoire persists workspace state for Chronosync (see Configuration above).

After changing entities or `OnModelCreating`:

```bash
dotnet tool restore
```

```bash
ARCANUM_GRIMOIRE_DEV_KEY=dev-key-placeholder dotnet ef migrations add YourMigrationName \
  --project src/RetroDownfall.Arcanum.Infrastructure \
  --startup-project src/RetroDownfall.Arcanum.Infrastructure \
  --output-dir Data/Migrations \
  --context ArcanumDbContext
```

```bash
ARCANUM_GRIMOIRE_DEV_KEY=dev-key-placeholder dotnet ef dbcontext optimize \
  --project src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj \
  --startup-project src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj \
  --output-dir Generated \
  --namespace RetroDownfall.Arcanum.Infrastructure.Generated \
  --context ArcanumDbContext
```

Commit both the new migration and the regenerated `Generated/` sources.

## MCP configuration

Wire external MCP servers via `~/.config/arcanum/mcp.json` using the standard `mcpServers` schema (`command`, `args`, optional `env`). Workspace-local `mcp.json` is merged when present.

## Context compression (pre-flight tokens)

Spell pre-flight routing (`SemanticRouter`) asks the model for structured JSON (`spellName` / `NONE`) using JSON response format when the provider supports it; see **DESIGN.md** §10.2.2.

When **`Arcanum:Intelligence:EnableContextCompression`** is enabled, the API estimates whether the assembled prompt (dynamic system block, workspace context, spell, attachments, and Grimoire replay) is likely to exceed a **percentage** of the resolved provider’s **`contextWindowLimit`** (`Arcanum:Intelligence:ContextWindowCompressionThreshold`, default **85%**). If so—and **`Conversation.Summary`** plus **`LastSummarizedMessageAt`** are both present—the hub **filters out** older persisted messages at read time (messages at or before the watermark), injects the summary into the system prompt under `### Campaign Summary (compressed context)`, and **never deletes** `ChatMessage` rows.

- **Campaign Logger:** while **`serve`** (or DevHost) is running, a background sweep enqueues conversations that exceed the message-count or idle thresholds. The consumer runs **headless** summarization (`SkipSpellRouting`, `DisableMcpTools`, stateless `PingRequest`, no `ConversationId` — so no new `ChatMessage` row) using **`Arcanum:FastModel`** when set, else **`Arcanum:DefaultModel`**. On success it writes **`Conversation.Summary`** and advances **`LastSummarizedMessageAt`**; on failure the watermark is left unchanged so the session can retry.

- **NDJSON event shape:** Each line is an `IntelligenceEvent` whose **`type`** is a **camelCase string** discriminator: **`"status"`**, **`"conversationBound"`**, **`"token"`**, **`"result"`**, **`"error"`**, **`"toolCall"`**, or **`"toolResult"`**. The terminal **`"result"`** line includes structured **`usage`** (OpenAI shape: `prompt_tokens` / `completion_tokens` / `total_tokens`) when the provider reports token counts.
- **NDJSON:** `POST /api/intelligence/ping-stream` emits a **`status`** line with the exact message *“Context window near limit. Swapping older messages for Campaign Summary.”* before token streaming begins when compression runs.
- **NDJSON `result`:** the terminal **`result`** line includes OpenAI-shaped **`usage`** (`prompt_tokens`, `completion_tokens`, `total_tokens`) on the event object; the legacy **`data`** field still carries **`total_tokens`** as a decimal string for older clients.
- **`chat` REPL:** matching that status sets a persistent **Memory Compressed** hint on the Mana bar (cleared on **`/new`**). Use **`/log`**, **`/memory`**, or **`/summary`** to print the current session’s stored summary from the API (same data; different panel titles). Use **`/mana`** for session vs Grimoire lifetime token totals; on **`/exit`** / **`/quit`** (or EOF) a short **Session mana** panel summarizes the REPL when any usage was recorded.
- **Accuracy:** counting uses **`Microsoft.ML.Tokenizers`** Tiktoken **`o200k_base`** for all providers in v1 (best-effort for Ollama models). Tool definitions are not tokenized; lower the threshold if you hit real context errors.
- **No summary yet:** if the estimate is over budget but **`Summary`** is empty, the server logs a warning and sends the **full** history unchanged.

## CLI reference

All commands use `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj --` as the prefix. After a Native AOT publish, substitute the binary name (e.g. `arcanum`).

**Graceful interruptibility (`chat` / `ask`):** During a streaming inference turn, Ctrl+C cancels only the **current** turn (the REPL keeps running). The cancellation token propagates through `POST /api/intelligence/ping-stream` into tool execution. In-process MCP **`execute_command`** and hub **`run_spell_script`** kill spawned OS process trees immediately (`Kill(entireProcessTree: true)`), **in addition** to the hard wall-clock cap from **`Arcanum:Intelligence:ExecuteCommandTimeoutSeconds`** (see **Configuration** in [DESIGN.md](docs/DESIGN.md) §3.4). External MCP servers started as subprocesses (`mcp.json`) are not sent cooperative cancel over JSON-RPC; cancel stops the host wait and may leave remote work running until that server’s own policies apply.

| Command | Description |
|---------|-------------|
| `serve` | Host the API on localhost:5001 |
| `ask <prompt>` | Single-turn query (streams response via NDJSON) |
| `ask <prompt> -n` | Start a new conversation thread |
| `ask <prompt> -m <model>` | Override the model for this request |
| `ask <prompt> --unattended` | Auto-reply to `ask_human` so the Mage proceeds without an operator |
| `chat` | Interactive multi-turn REPL with Markdig rendering |
| `chat --new` | Start REPL with a fresh conversation |
| `chat -m <model>` | Pin the active model for the REPL session |
| `chat --no-tools` | Disable MCP tools for the session |
| `chat --unattended` | Auto-reply to `ask_human` for the REPL session |
| `look` | Print Eye of the World workspace snapshot (domain + TOC) |
| `doctor` | Run environment diagnostics (version, paths, API health) |
| `lore list` | List all operator memory entries |
| `lore get <key>` | Show a specific lore value |
| `lore set <key> <value>` | Create or update a lore entry |
| `lore delete <key>` | Remove a lore entry |
| `daemon install` | Install as background service (Windows/macOS/Linux) |
| `daemon status` | Check daemon status |
| `daemon uninstall` | Remove background service |
| `daemon jobs` | List Unseen Servant jobs (intervals, enabled); requires **`serve`** on `Arcanum:Host:Port` and a stored API key |
| `daemon initiative <job> <minutes>` | Override a job’s polling interval for this process; same API requirements as `daemon jobs` |
| `daemon alert <message>` | Send a Comm Link test alert (`--title`, `--severity`, `--source`); **`POST /api/commlink/send`**; same API requirements as `daemon jobs` |

**Chat slash commands:** `/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model <name>`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/memory`, `/summary`, `/mana`, `/attach`.

**Inference flags** (both `ask` and `chat`):

| Flag | Forwarded to | Notes |
|------|--------------|-------|
| `--temperature <0..2>` | `ChatOptions.Temperature` | Sampling temperature. |
| `--top-p <0..1>` | `ChatOptions.TopP` | Nucleus sampling cutoff. |
| `--max-tokens <N>` | `ChatOptions.MaxOutputTokens` | Maximum output tokens per turn. |
| `--seed <N>` | `ChatOptions.Seed` | Provider support varies. |
| `--stop <sequence>` | `ChatOptions.StopSequences` | Repeat the flag for several sequences. |
| `--response-format <kind>` | `ChatOptions.ResponseFormat` | `text` / `json_object` / `json_schema`. |
| `--presence-penalty <-2..2>` | `ChatOptions.PresencePenalty` | Positive discourages repetition. |
| `--frequency-penalty <-2..2>` | `ChatOptions.FrequencyPenalty` | Positive penalizes frequent tokens. |

Flags applied to `chat` set the values for **every turn** in the REPL session; `/help` shows the active overrides via the startup banner panel.

**Output detection (NO_COLOR / TTY):** The CLI auto-disables ANSI colors, interactive prompts, and the mana bar when stdout is redirected (`arcanum ask ... | tee out.txt`) or when **`NO_COLOR`** / **`ARCANUM_NO_COLOR`** is set to any non-empty value (see [no-color.org](https://no-color.org)). The detected state is shown in `arcanum doctor` under **System**.

## API reference

All endpoints require the API key via header `X-Arcanum-Key` or `Authorization: Bearer <KEY>`. Default base: `http://localhost:5001`.

### Wire contract (JSON)

Most `/api` JSON responses use the **`ApiResponse<T>`** envelope: `data`, `isSuccess`, `error` (code + message when failed), `traceId`. **Exceptions:** `POST /api/intelligence/ping-stream` returns **NDJSON** lines (`IntelligenceEvent`), not `ApiResponse`; `POST /v1/chat/completions` uses **OpenAI-shaped** JSON or **SSE**; **`GET /v1/models`** returns **OpenAI-shaped** JSON (models list for auto-discovery); OpenAPI (`/api/openapi/v1.json`) and Scalar (`/api/scalar`) are framework/UI, not `ApiResponse` payloads. **`POST /api/intelligence/ping`** wraps a **`PromptResponseDto`** payload (`text`, `usage`, optional `toolCalls`, `finishReason`) so usage and observed tool calls are visible without falling back to NDJSON.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/health` | Health check (`ApiResponse<string>`) |
| POST | `/v1/chat/completions` | OpenAI-compatible chat. JSON body fully parses: **`model`** (required), **`messages`** (required), **`stream`**, **`temperature`**, **`top_p`**, **`max_tokens`** / **`max_completion_tokens`**, **`presence_penalty`**, **`frequency_penalty`**, **`seed`**, **`n`**, **`user`**, **`stop`** (string or string[]), **`response_format`**, **`stream_options.include_usage`**, **`tools`**, **`tool_choice`**, **`parallel_tool_calls`**, **`logprobs`**, **`top_logprobs`**. Messages support **`name`**, **`tool_call_id`**, **`tool_calls`**, and **multimodal `content`** as either a string or an array of `{type:"text"\|"image_url", ...}` parts. **Forwarded into inference today:** `temperature`, `top_p`, `max_(completion_)?tokens`, `presence_penalty`, `frequency_penalty`, `seed`, `stop`, `response_format` (`json_object`/`json_schema`/`text`). Responses include **`usage`**, **`system_fingerprint`** (configurable via `Arcanum:Host:SystemFingerprint`), **`tool_calls`** on assistant messages, and **`logprobs: null`** / **`refusal: null`** for client compatibility. **Stream usage** is gated by `stream_options.include_usage = true` (per OpenAI spec). **Streaming errors** are emitted as an SSE chunk with the OpenAI error shape (`{"error":{"message":...,"type":...,"code":...,"param":...}}`), followed by `data: [DONE]`. **Streaming `delta.tool_calls`** chunks expose server-side tool execution for observability. **Not** `ApiResponse`-wrapped. |
| GET | `/v1/models` | Auto-discovery endpoint returning a flattened list of all models configured across all providers (OpenAI `list models` JSON shape). Per-model `created` is a stable per-process timestamp. **Not** `ApiResponse`-wrapped. |
| POST | `/api/intelligence/ping` | Buffered inference (`prompt` or `statelessMessages` for multi-turn without Grimoire). Body accepts the same inference parameters as `/v1/chat/completions` (`temperature`, `topP`, `maxOutputTokens`, `stop`, `seed`, `responseFormat`, `presencePenalty`, `frequencyPenalty`). Response envelope wraps a **`PromptResponseDto`** (`text`, `usage`, optional `toolCalls`, `finishReason`). **400** validation, **200** success, **500** + envelope on inference failure. |
| POST | `/api/intelligence/ping-stream` | Streaming inference (**NDJSON**, not `ApiResponse`) |
| POST | `/api/intelligence/human-response` | Complete an `ask_human` tool call. **400** validation; **404** + envelope if `promptId` is unknown/expired; **200** + envelope with `data: true` when accepted. |
| POST | `/api/intelligence/arsenal` | List active tools and MCP servers. Optional JSON body: `{ "workingDirectory": "..." }` (`OptionalWorkspaceRequest`). |
| POST | `/api/mcp/reload` | Reload MCP server connections. Optional JSON body: `{ "workingDirectory": "..." }` (`OptionalWorkspaceRequest`). |
| GET | `/api/conversations` | List conversations |
| GET | `/api/conversations/{id}` | Get conversation detail |
| GET | `/api/conversations/{id}/messages` | Get ordered message history |
| DELETE | `/api/conversations/{id}` | Delete a conversation (**200** + `ApiResponse<bool>` on success; **404** if missing) |
| POST | `/api/conversations/{id}/rest` | Enqueue Campaign Log consolidation (**202** + `ApiResponse<bool>` when queued; **404** if conversation missing) |
| GET | `/api/lore` | List all lore entries |
| GET | `/api/lore/{key}` | Get a lore entry |
| POST | `/api/lore` | Create/update a lore entry |
| DELETE | `/api/lore/{key}` | Delete a lore entry (**200** + envelope on success; **404** if key did not exist; **400** invalid key) |
| GET | `/api/daemon/jobs` | List Unseen Servant daemon jobs (base vs effective interval, enabled flag) |
| POST | `/api/daemon/jobs/{name}/initiative` | Set dynamic polling interval for a job (`intervalMinutes` in JSON body); returns updated job status |
| POST | `/api/commlink/send` | Send a Comm Link alert; JSON body `CommLinkMessageRequestDto` (`title`, `body`, `severity` as `Info` \| `Warning` \| `Critical`, `source`). **200** + `ApiResponse<bool>` with `data: true` on success; **400** validation; **502** + envelope when the configured webhook returns a non-success HTTP status or throws during the outbound POST. |
| GET | `/api/perception/look?directory={path}` | Remote Eye of the World snapshot |
| GET | `/api/openapi/v1.json` | OpenAPI specification |
| GET | `/api/scalar` | Scalar interactive API docs |

## Security and hardening notes

Arcanum is **single-user local-first** by default. Hardening choices currently in effect:

- **Loopback-only Kestrel** unless `ARCANUM_HOST_ANY=1` is set.
- **Localhost-only CORS** by default (see `Arcanum:Host:CorsAllowedOrigins` above).
- **API key SHA-256 cached digest compare** in `ApiKeyEndpointFilter` (`CryptographicOperations.FixedTimeEquals` over fixed-size 32-byte hash; multi-valued headers rejected; cache reload via `Arcanum:Security:ApiKeyCacheTtlSeconds`).
- **Path containment + symlink resolution** on every in-process MCP file/dir tool (`read_file_chunk`, `replace_text_block`, `write_file`, `list_directory`, `execute_command`, `run_spell_script`).
- **`execute_command` uses `ArgumentList`** (no shell, no OS string re-tokenization). Callers may pass either pre-tokenized `argumentList: ["foo","bar"]` (preferred) or a single `arguments` string the host tokenizes (quoted substrings stay together; whitespace separates tokens).
- **Tool output caps** (`Arcanum:Intelligence:ToolOutputCapBytes`) prevent runaway `execute_command` / `run_spell_script` calls from exhausting host memory.
- **Scalar UI is opt-in** with a tight `Content-Security-Policy` header (`Arcanum:Host:EnableScalarUi`).
- **Sanitized error envelopes**: hub model-resolution failures, OpenAI v1 inference failures, and Comm Link webhook failures return generic public strings; exception detail stays in server logs only.
- **Perception path allowlist** (`Arcanum:Perception:AllowedWorkspaceRoots`).
- **Unseen Servant concurrency cap** (`Arcanum:Daemon:MaxConcurrentJobs`) and shutdown drain.
- **Comm Link webhook scheme allowlist** (`Arcanum:CommLink:AllowedSchemes`, default `["https","http"]`) and **`AllowAutoRedirect = false`** on the named HTTP client (mitigates SSRF amplification via 302 to internal targets).
- **Kestrel request-body cap** (`Arcanum:Host:MaxRequestBodyBytes`, default 10 MiB).
- **Optional fixed-window rate limiter** on `/api` and `/v1` partitioned by API key or IP (`Arcanum:Host:RateLimit:*`).
- **Bounded conversation hydration** (`Arcanum:Grimoire:MaxMessagesPerConversationLoad`) prevents long threads from blowing host RAM.
- **`McpRequestCancellationBroker` auto-cleanup**: every registration installs a `CancellationToken.Register` callback so caller-token cancellation removes the entry and disposes the linked `CancellationTokenSource` even when `Unregister` is never called (caller crash or unhandled exception).
- **Configurable agentic loop bound** (`Arcanum:Intelligence:MaxToolInferenceRounds`).

## Native AOT publish

Self-contained binary (example for Apple Silicon):

```bash
dotnet publish src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -c Release -r osx-arm64
```

Other RIDs: `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`. Framework-dependent (no RID):

```bash
dotnet publish src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -c Release
```

## Further reading

- [DESIGN.md](docs/DESIGN.md) — architecture, encryption, MCP internals, tool-call lifecycle, and extension guidelines.
