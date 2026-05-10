# Retro Downfall Arcanum

A .NET 10 local-first AI assistant with an encrypted conversation store, MCP tool integration, and a rich terminal REPL — with a **multi-provider** inference hub (Ollama and OpenAI-compatible HTTP APIs).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- At least one configured inference provider under `Arcanum:Providers` in `arcanum.json` (see **Configuration**). For local [Ollama](https://ollama.com/), install it, ensure it is reachable at the `Endpoint` you configure, and list each model id you use under that provider’s `models` array. Pull models with `ollama pull <id>` as needed — Arcanum does not hard-code model names.

## Build

```bash
dotnet build RetroDownfall.Arcanum.slnx
```

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
| `Arcanum:Host:EnableEnterpriseTelemetry` | When `true`, Serilog also writes structured JSON logs to the console (for log shippers). Rolling JSON file logs are always enabled. |
| `Arcanum:Daemon:Jobs` | **Unseen Servant**: JSON array of scheduled headless inference jobs when the API host is running. Each object supports `name` (string), `intervalMinutes` (int, clamped 1–10080), `targetSpell` (string: matches a spell’s YAML `name` or the parent folder of `SPELL.md`), and `enabled` (bool, default `true`). Jobs use an empty `WorkingDirectory` so spells resolve from the global spell tree under your Arcanum config directory (`spells/`). **Phase 2 (Adaptive initiative):** while a job runs headlessly, the in-process MCP tool `adjust_initiative` (`job_name`, `interval_minutes`) can change that job’s polling interval for the lifetime of the process; values are clamped to 1–10080 minutes and are not persisted across restarts. The kickoff prompt includes the current effective interval. **Phase 4 (Stateful lore, gated):** when `Arcanum:Intelligence:EnableLoreSystem` is `true`, each run pre-fetches Grimoire lore at `daemon_state_{job.Name}`, injects it into the kickoff, and instructs the model to persist cross-cycle state with `scribe_lore` on that key; if `GetLoreAsync` fails, a warning is logged and the job runs with no prior state. When `EnableLoreSystem` is `false`, the kickoff stays the Phase 1 stateless prompt (no lore fetch, no `scribe_lore` instructions). Headless kickoffs also instruct the model to call in-process MCP **`use_commlink`** for high-alpha / critical operator alerts (**Comm Link**). |
| `Arcanum:CommLink:WebhookUrl` | Optional absolute URL for **Comm Link** outbound JSON `POST` alerts (Discord/Slack/custom). When unset, `use_commlink` and **`POST /api/commlink/send`** succeed but only log a warning—no HTTP is performed. |


- The Grimoire (`WorkspaceContexts` table) stores JSON `PatternSnapshot` baselines to power Chronosync Reporting.

The `serve` host registers a permissive CORS policy (`AllowAnyOrigin` / `AllowAnyHeader` / `AllowAnyMethod`) so browser UIs (for example LibreChat) can call the API without preflight failures.

Minimal example (local Ollama + OpenAI-compatible DeepSeek; **put API keys in environment variables**, not in `arcanum.json`):

```json
{
  "Arcanum": {
    "Host": {
      "Port": 5001,
      "EnableEnterpriseTelemetry": false
    },
    "DefaultModel": "deepseek-chat",
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

`Arcanum:DefaultModel` must match a `models` entry on some provider (case-insensitive; Ollama-style `:latest` tag matching is supported). If omitted, the first model of the first provider is used. OpenAI-compatible `endpoint` values must include the path prefix expected by that host (often `/v1`). The same wire shape covers DeepSeek, Groq, GitHub Models, LM Studio, and similar servers. Keyless local OpenAI-compatible servers can omit `apiKey` (the host sends a placeholder credential understood by those servers).

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

## CLI reference

All commands use `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj --` as the prefix. After a Native AOT publish, substitute the binary name (e.g. `arcanum`).

| Command | Description |
|---------|-------------|
| `serve` | Host the API on localhost:5001 |
| `ask <prompt>` | Single-turn query (streams response via NDJSON) |
| `ask <prompt> -n` | Start a new conversation thread |
| `ask <prompt> -m <model>` | Override the model for this request |
| `chat` | Interactive multi-turn REPL with Markdig rendering |
| `chat --new` | Start REPL with a fresh conversation |
| `chat --no-tools` | Disable MCP tools for the session |
| `look` | Print Eye of the World workspace snapshot (domain + TOC) |
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

**Chat slash commands:** `/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model <name>`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/attach`.

## API reference

All endpoints require the API key via header `X-Arcanum-Key` or `Authorization: Bearer <KEY>`. Default base: `http://localhost:5001`.

### Wire contract (JSON)

Most `/api` JSON responses use the **`ApiResponse<T>`** envelope: `data`, `isSuccess`, `error` (code + message when failed), `traceId`. **Exceptions:** `POST /api/intelligence/ping-stream` returns **NDJSON** lines (`IntelligenceEvent`), not `ApiResponse`; `POST /v1/chat/completions` uses **OpenAI-shaped** JSON or **SSE**; **`GET /v1/models`** returns **OpenAI-shaped** JSON (models list for auto-discovery); OpenAPI (`/api/openapi/v1.json`) and Scalar (`/api/scalar`) are framework/UI, not `ApiResponse` payloads.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/health` | Health check (`ApiResponse<string>`) |
| POST | `/v1/chat/completions` | OpenAI-compatible chat (JSON body: `model`, `messages`, `stream`, optional `temperature` / `max_tokens`; `temperature` and `max_tokens` are accepted but not yet applied to inference). Stateless transcript only (no Grimoire thread). Response: `text/event-stream` when `stream` is true. **Not** `ApiResponse`-wrapped. |
| GET | `/v1/models` | Auto-discovery endpoint returning a flattened list of all models configured across all providers (OpenAI `list models` JSON shape). **Not** `ApiResponse`-wrapped. |
| POST | `/api/intelligence/ping` | Buffered inference (`prompt` or `statelessMessages` for multi-turn without Grimoire). **400** validation, **200** success, **500** + envelope on inference failure. |
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
