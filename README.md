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

The raw Base64 key prints to stdout once. It is encrypted via ASP.NET Core Data Protection and stored at `{ApplicationData}/arcanum/security.dat`.

> **Rotation is destructive.** The Grimoire SQLCipher passphrase is derived from this API key via HKDF-SHA256, so a rotated key cannot decrypt the existing conversation database. To rotate: stop the host, move/delete **both** `security.dat` and the Grimoire `.db` under `~/.config/arcanum/`, then restart. The host will generate a new 32-byte key, print it once, and provision a fresh Grimoire. See [DESIGN.md §16.3](docs/DESIGN.md#163-security-and-identity) for the full rotation playbook.

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

The table below mirrors **DESIGN.md §3.4** row-for-row, grouped by section. Every numeric setting has a runtime clamp in `ArcanumSettingClamps` and is read through that clamp at the consumer site. When in doubt, the in-code XML doc on each property is authoritative.

**Host (Kestrel, CORS, OpenAPI/Scalar, rate limiter):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:Host:Port` | `int` | `5001` | Kestrel listen port (clamp 1 – 65535). |
| `Arcanum:Host:RetainedLogFileCount` | `int` | `7` | Serilog rolling file retention (days; clamp 1 – 366). |
| `Arcanum:Host:EnableEnterpriseTelemetry` | `bool` | `false` | When `true`, Serilog adds a console sink with `CompactJsonFormatter` (structured JSON for log shippers). Rolling JSON file logs are always enabled regardless. |
| `Arcanum:Host:ListenAny` | `bool` | `false` | When `true`, Kestrel binds to all interfaces (`ListenAnyIP`). The env var **`ARCANUM_HOST_ANY=1`** always wins. |
| `Arcanum:Host:MaxRequestBodyBytes` | `long` | `10 MiB` | Kestrel `MaxRequestBodySize`. Clamp 256 KiB – 1 GiB. |
| `Arcanum:Host:CorsAllowedOrigins` | `string[]` | localhost loopback | Origins for the `ArcanumCors` policy. Use `["*"]` for any-origin (browser callers can read responses; risk = key exfiltration on a compromised page). |
| `Arcanum:Host:EnableScalarUi` | `bool` | `false` | Mounts `MapScalarApiReference` under `/api/scalar`. Off by default (Scalar's inline JS/CSS conflicts with the project's strict CSP posture). `/api/openapi/v1.json` is always available. |
| `Arcanum:Host:SystemFingerprint` | `string?` | `null` | Optional override for the `system_fingerprint` field on `/v1/chat/completions` responses. When `null`, derived from `AssemblyInformationalVersionAttribute` (e.g. `arcanum-0.1.0-beta`). |
| `Arcanum:Host:RateLimit:Enabled` | `bool` | `false` | Mounts a fixed-window rate limiter on `/api` and `/v1`. Partition key = `X-Arcanum-Key` → else `Authorization` → else remote IP. Excess requests return **HTTP 429** unless `QueueLimit > 0`. |
| `Arcanum:Host:RateLimit:PermitLimit` | `int` | `120` | Requests permitted per partition per window. Clamp 1 – 1,000,000. |
| `Arcanum:Host:RateLimit:WindowSeconds` | `int` | `60` | Window length. Clamp 1 – 86,400. |
| `Arcanum:Host:RateLimit:QueueLimit` | `int` | `0` | Queued requests per partition. `0` rejects immediately; positive values serve queued requests when the window replenishes. Clamp 0 – 1,000,000. |

**Security (API key filter):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:Security:MaxApiKeyHeaderUtf16Chars` | `int` | `512` | Rejects oversized API key headers before UTF-8 conversion. Clamp 128 – 8192. |
| `Arcanum:Security:ApiKeyCacheTtlSeconds` | `int` | `30` | TTL for the cached SHA-256 digest of the expected API key in `ApiKeyEndpointFilter`. After the TTL the filter re-reads `ISecretStore` so on-disk rotation propagates without a restart. Clamp 1 – 3600. |

**Providers / model resolution:**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:DefaultModel` | `string?` | `null` | Used when `PingRequest.Model` is omitted. Must match a `models` entry on some provider; case-insensitive with Ollama-style `:latest` tag matching. If null/empty, the first model of the first provider is used. |
| `Arcanum:FastModel` | `string?` | `null` | Optional model id for **internal background inference** (Campaign Logger summarization). Falls back to `DefaultModel` then first configured model. |
| `Arcanum:Providers` | `array` | `[]` | Multi-provider hub. Each element: `name`, `type` (`Ollama` or `OpenAICompatible`), `endpoint`, `apiKey` (optional; use `ARCANUM_` env vars for secrets), `models` (`string[]`), `contextWindowLimit` (default **8192**; clamp 256 – 2,097,152). |

**CommLink (outbound webhook):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:CommLink:WebhookUrl` | `string?` | `null` | Optional absolute URL for outbound JSON `POST` alerts (Discord/Slack/custom). When unset, `use_commlink` and **`POST /api/commlink/send`** succeed but only log a warning. |
| `Arcanum:CommLink:WebhookTimeoutSeconds` | `int` | `15` | Timeout for the named `HttpClient("CommLinkWebhook")`. Clamp 1 – 120. |
| `Arcanum:CommLink:AllowedSchemes` | `string[]` | `["https","http"]` | URI schemes the webhook dispatcher may call. Use `["https"]` to require TLS. Non-matching URLs are skipped with a warning (no HTTP call). The handler also has `AllowAutoRedirect = false`. |

**Intelligence (inference hub, MCP, compression, tools):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:Intelligence:ExecuteCommandTimeoutSeconds` | `int` | `30` | Hard wall-clock cap for MCP `execute_command` and `run_spell_script`. Clamp 1 – 600. Cooperative cancel kills the process tree immediately, independent of this. |
| `Arcanum:Intelligence:ToolOutputCapBytes` | `long` | `1 MiB` | Combined cap on stdout + stderr captured from `execute_command` / `run_spell_script` (split evenly per stream). Output beyond the cap is truncated with `[truncated: …]`. Clamp 64 KiB – 64 MiB. |
| `Arcanum:Intelligence:MaxToolInferenceRounds` | `int` | `8` | Hard cap on agentic tool rounds per turn. Beyond this the hub fails the turn with `Hub.ToolLoop`. Clamp 1 – 64. |
| `Arcanum:Intelligence:SemanticRouterPreflightTimeoutSeconds` | `int` | `15` | Max wait for the spell-router preflight call. Clamp 1 – 600. |
| `Arcanum:Intelligence:SemanticRouterMaxTokens` | `int` | `50` | Spell-router preflight `MaxOutputTokens`. Clamp 1 – 4096. |
| `Arcanum:Intelligence:SemanticRouterTemperature` | `float` | `0` | Spell-router preflight temperature. Clamp 0 – 2. |
| `Arcanum:Intelligence:McpRequestTimeoutSeconds` | `int` | `60` | Default per-request timeout for `McpClient` JSON-RPC. Clamp 1 – 600. |
| `Arcanum:Intelligence:McpMaxPaginationPages` | `int` | `32` | Max `tools/list` pagination iterations. Clamp 1 – 256. |
| `Arcanum:Intelligence:ListDirectoryMaxPaths` | `int` | `500` | Max paths from in-process `list_directory`. Clamp 1 – 100,000. |
| `Arcanum:Intelligence:EnableLoreSystem` | `bool` | `true` | Gates `read_lore` / `scribe_lore` / `delete_lore` MCP tools and Unseen Servant lore injection. |
| `Arcanum:Intelligence:EnableArchiveSearch` | `bool` | `true` | Gates `search_archives` MCP tool. |
| `Arcanum:Intelligence:ArchiveSearchMaxResults` | `int` | `5` | Max rows per `search_archives` call. Clamp 1 – 100. |
| `Arcanum:Intelligence:ArchiveSearchMaxQueryLength` | `int` | `512` | Max query length before FTS sanitization. Clamp 32 – 4096. |
| `Arcanum:Intelligence:CampaignLogThreshold` | `int` | `25` | Message-count safety valve for Campaign Log consolidation. Clamp 1 – 10,000. |
| `Arcanum:Intelligence:CampaignLogIdleTimeoutMinutes` | `int` | `240` | Idle minutes before a conversation becomes eligible for consolidation. Clamp 1 – 43,200. |
| `Arcanum:Intelligence:CampaignLogSweepIntervalMinutes` | `int` | `15` | Background sweep interval for Campaign Log enqueue. Clamp 1 – 1440. |
| `Arcanum:Intelligence:ContextWindowCompressionThreshold` | `int` | `85` | Percentage of the resolved provider's `contextWindowLimit` at which read-time context compression is considered. Clamp 50 – 100. |
| `Arcanum:Intelligence:EnableContextCompression` | `bool` | `true` | When `true`, the hub runs pre-flight token counting and may swap older Grimoire messages for `Conversation.Summary` in the assembled system prompt without deleting rows. |
| `Arcanum:Intelligence:CompressionPreflightMinMessages` | `int` | `6` | Minimum assembled-message count before context-compression preflight runs (short threads skip tokenizer cost). Clamp 0 – 100. |
| `Arcanum:Intelligence:PerMessageTemplateOverheadTokens` | `int` | `4` | Per-message overhead (tokens) added to the pre-flight count to approximate chat-template framing. Clamp 0 – 32. |
| `Arcanum:Intelligence:TokenizerEncoding` | `string` | `"o200k_base"` | Tiktoken encoding name used by `InferenceTokenizerResolver`. Unknown names log a warning and fall back to `o200k_base`. |
| `Arcanum:Intelligence:EnableTokenTracking` | `bool` | `true` | When `true`, each successful inference turn with a bound conversation increments `Conversation.TotalTokensUsed` in the Grimoire. Wire responses still emit `usage` when `false`; only persistence is skipped. |

**Grimoire (encrypted SQLite store):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:Grimoire:MaxMessagesPerConversationLoad` | `int` | `1000` | Maximum messages loaded into memory by `GetConversationAsync` (most recent N, returned in chronological order). Clamp 50 – 100,000. |

**Perception (`/api/perception/look`, Eye of the World):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:Perception:MaxEnumerationSteps` | `int` | `50,000` | File-walk budget for Eye of the World. Clamp 1 – 10,000,000. |
| `Arcanum:Perception:MaxTableOfContentsLines` | `int` | `20` | TOC line budget for `PatternSnapshot`. Clamp 1 – 500. |
| `Arcanum:Perception:AllowedWorkspaceRoots` | `string[]` | `[]` | Optional allowlist of absolute roots that `GET /api/perception/look` may scan. Empty (default) keeps historical behaviour; when non-empty, `directory` must resolve under one of these roots or the endpoint returns **`403`**. |

**Daemon (Unseen Servant scheduler):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:Daemon:Jobs` | `array` | `[]` | JSON array of scheduled headless inference jobs. Each entry: `name`, `intervalMinutes` (clamp 1 – 10,080), `targetSpell` (matches `SPELL.md` frontmatter name or parent folder), `enabled` (default `true`). Runtime overrides via MCP `adjust_initiative` and **`POST /api/daemon/jobs/{name}/initiative`**. Per-job lore key `daemon_state_{job.Name}` is injected into the kickoff when `Intelligence:EnableLoreSystem` is `true`. Kickoffs also instruct the model to call `use_commlink` for high-alpha alerts. |
| `Arcanum:Daemon:MaxConcurrentJobs` | `int` | `8` | Hard concurrency cap on jobs the scheduler dispatches per minute; excess jobs defer. Clamp 1 – 1024. |
| `Arcanum:Daemon:ShutdownDrainTimeoutSeconds` | `int` | `10` | Wait (seconds) `StopAsync` gives in-flight jobs to drain. `0` disables waiting. Clamp 0 – 600. |

**CLI (`ask` / `chat` / `doctor` / theming):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:Cli:MaxAttachFileSizeBytes` | `long` | `1 MiB` | Per-file staging limit for `chat /attach`. Clamp 1 KiB – 100 MiB. |
| `Arcanum:Cli:MaxAttachedFilesPerRequest` | `int` | `32` | Max attached files per inference request. Clamp 1 – 256. |
| `Arcanum:Cli:MaxAttachedFileRelativePathChars` | `int` | `4096` | Max `RelativePath` length per attachment. Clamp 256 – 8192. |
| `Arcanum:Cli:Theme` | `ArcanumTheme` | `SystemDefault` | CLI appearance: `Light`, `Dark`, or `SystemDefault` (uses `IThemeDetector` once at process start). |
| `Arcanum:Cli:ThemeColors` | object | core defaults | Nested `Light` / `Dark`, each with `Text`, `Heading`, `Highlight`, `Error`, `Muted` as `#RRGGBB` strings. |
| `Arcanum:Cli:ShowManaBar` | `bool` | `true` | When `true`, the `chat` REPL prints the mana bar before each prompt. Auto-suppressed when stdout is redirected or `NO_COLOR` / `ARCANUM_NO_COLOR` is set. |
| `Arcanum:Cli:DoctorHealthTimeoutSeconds` | `int` | `2` | Timeout for the `arcanum doctor` `/api/health` probe. Clamp 1 – 60. Raise it on slow startups (cold containers, hardware-accelerated provider warmup). |

**Bureau (reserved):**

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `Arcanum:Bureau:Enabled` | `bool` | `false` | **Reserved** for the future Bureau integration. No-op today — the property is kept on the binding surface so operator configs stay valid across upgrades and Bureau wiring can land without a configuration migration. |

In addition to conversations and lore, the Grimoire's `WorkspaceContexts` table stores JSON `PatternSnapshot` baselines that power Chronosync reporting (see [DESIGN.md §5.4.2](docs/DESIGN.md)).

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
- **Accuracy:** counting uses **`Microsoft.ML.Tokenizers`** Tiktoken with the encoding from **`Arcanum:Intelligence:TokenizerEncoding`** (default **`o200k_base`**, applied to every provider; counts are best-effort for SentencePiece-based local models). Unknown encoding names log a warning and fall back to `o200k_base`. Tool definitions are not tokenized; lower the threshold if you hit real context errors.
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

> **`/mcp reload`** is parsed as the verb **`/mcp`** with the required argument **`reload`** (other arguments are rejected). It disposes every MCP partition, clears the merged-tool cache, and re-bootstraps the global `mcp.json`. The CLI prints a usage hint when the verb is invoked without `reload`.

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
| GET | `/api/conversations?take={N}` | List most-recent conversations. Optional `take` query parameter (default **50**, clamped 1 – 200) — values outside the range are clamped, not rejected. |
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

**Threat model.** Arcanum is a **single-user, local-first** assistant. The default deployment binds Kestrel to **loopback only**, requires a 32-byte master API key on every `/api` and `/v1` request, encrypts the conversation store at rest with SQLCipher (passphrase HKDF-derived from the master key), and confines the model's filesystem reach to the operator's workspace root through path containment + symlink resolution. The model can still issue `execute_command` — once an operator hands out the API key, the holder has **operator-equivalent power within the workspace tree** (and within whatever external services the configured MCP servers expose). Threats that are explicitly out of scope: multi-tenant isolation, network-level authentication beyond the static key, exfiltration via attacker-controlled MCP servers the operator chose to install, and physical-host attacks against `security.dat` or the Grimoire `.db` file. Hardening choices currently in effect:

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
