# Retro Downfall Arcanum

A .NET 10 local-first AI assistant with an encrypted conversation store, MCP tool integration, and a rich terminal REPL — powered by Ollama.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.com/) installed and running locally (default endpoint `http://localhost:11434`)
- Pull the default model before first use:

```bash
ollama pull llama3.2
```

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
| Windows | `%APPDATA%\arcanum\` |
| macOS | `~/Library/Application Support/arcanum/` |
| Linux | `~/.config/arcanum/` |

Place an optional `arcanum.json` in that directory. Environment variable override prefix: `ARCANUM_` (double underscores for nesting, e.g. `ARCANUM_Arcanum__Ollama__Endpoint`).

| Setting | Purpose |
|---------|---------|
| `Arcanum:Host:EnableEnterpriseTelemetry` | When `true`, Serilog also writes structured JSON logs to the console (for log shippers). Rolling JSON file logs are always enabled. |

- The Grimoire (`WorkspaceContexts` table) stores JSON `PatternSnapshot` baselines to power Chronosync Reporting.

The `serve` host registers a permissive CORS policy (`AllowAnyOrigin` / `AllowAnyHeader` / `AllowAnyMethod`) so browser UIs (for example LibreChat) can call the API without preflight failures.

Minimal example:

```json
{
  "Arcanum": {
    "Host": {
      "Port": 5001,
      "EnableEnterpriseTelemetry": false
    },
    "Ollama": {
      "Endpoint": "http://localhost:11434",
      "DefaultModel": "llama3.2",
      "ContextWindowLimit": 8192
    },
    "Cli": {
      "Theme": "SystemDefault"
    }
  }
}
```

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

**Chat slash commands:** `/exit`, `/quit`, `/clear`, `/help`, `/new`, `/model <name>`, `/look`, `/tools`, `/mcp reload`, `/arsenal`, `/history`, `/resume <id>`, `/delete <id>`, `/rest`, `/log`, `/attach`.

## API reference

All endpoints require the API key via header `X-Arcanum-Key` or `Authorization: Bearer <KEY>`. Default base: `http://localhost:5001`.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/health` | Health check |
| POST | `/v1/chat/completions` | OpenAI-compatible chat (JSON body: `model`, `messages`, `stream`, optional `temperature` / `max_tokens`; `temperature` and `max_tokens` are accepted but not yet applied to inference). Stateless transcript only (no Grimoire thread). Response: `text/event-stream` when `stream` is true. |
| POST | `/api/intelligence/ping` | Buffered inference (`prompt` or `statelessMessages` for multi-turn without Grimoire) |
| POST | `/api/intelligence/ping-stream` | Streaming inference (NDJSON) |
| POST | `/api/intelligence/human-response` | Complete an `ask_human` tool call |
| POST | `/api/intelligence/arsenal` | List active tools and MCP servers |
| POST | `/api/mcp/reload` | Reload MCP server connections |
| GET | `/api/conversations` | List conversations |
| GET | `/api/conversations/{id}` | Get conversation detail |
| GET | `/api/conversations/{id}/messages` | Get ordered message history |
| DELETE | `/api/conversations/{id}` | Delete a conversation |
| POST | `/api/conversations/{id}/rest` | Enqueue Campaign Log consolidation |
| GET | `/api/lore` | List all lore entries |
| GET | `/api/lore/{key}` | Get a lore entry |
| POST | `/api/lore` | Create/update a lore entry |
| DELETE | `/api/lore/{key}` | Delete a lore entry |
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
