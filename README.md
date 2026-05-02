# Retro Downfall Arcanum

Enterprise-grade .NET 10 solution: shared **Core** library, **Api** class library (Minimal API endpoint mapping), and **Cli** console that runs Spectre commands or hosts a Native AOT–friendly slim Minimal API.

## Documentation

- [DESIGN.md](docs/DESIGN.md) — architecture, design decisions, tradeoffs, and extension guidelines for senior engineers.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build

```bash
dotnet build RetroDownfall.Arcanum.slnx
```

**C# formatting:** follow the blank-line conventions in [`ApiBootstrapper.cs`](src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs) (contiguous `using` directives, no blank line immediately after `{`, fluent `.` continuations without blank lines between parts, no blank lines before a lone closing `}` or `)` line, no blank lines inside a single multiline ternary or between comma-separated arguments, and at most one blank line between logical sections). For attributes on the same member or type, do not insert a blank line between consecutive attribute lines; do insert one blank line between the last attribute in that block and the following declaration (see `AskCommand.Settings` in [`AskCommand.cs`](src/RetroDownfall.Arcanum.Cli/Commands/AskCommand.cs)).

## Configuration

Both **`serve`** (CLI) and **Api.DevHost** call `ConfigurationBootstrapper.AddArcanumConfiguration()` before `AddArcanumApiServices`, so settings load from a **central user directory**, not the process working directory.

**Directory:** `{ApplicationData}/arcanum` (created on startup if missing).

- **Windows:** `%APPDATA%\arcanum\` (roaming application data).
- **macOS:** `~/Library/Application Support/arcanum/`.
- **Linux:** `~/.config/arcanum/` (per `Environment.SpecialFolder.ApplicationData`).

**File:** `arcanum.json` in that folder (optional). Reload on change is enabled. Root JSON property **`Arcanum`** maps to `IOptions<ArcanumSettings>` (Ollama endpoint/model, Bureau enabled flag).

**Logs:** `{ApplicationData}/arcanum/logs/` (created on startup). The API registers **Serilog** programmatically (no `appsettings` Serilog binding): one **compact JSON** object per line via **`Serilog.Formatting.Compact.CompactJsonFormatter`**, written to rolling files named like **`arcanum-api-YYYYMMDD.json`**. Files roll **daily**; **only the last seven** day files are kept (`retainedFileCountLimit: 7`). **`serve`** and **Api.DevHost** call **`WebApplicationBuilder.Logging.ClearProviders()`** before service registration so the default ASP.NET Core console loggers are not used, and **`Serilog.Log.CloseAndFlush()`** runs when the host exits.

Example `arcanum.json`:

```json
{
  "Arcanum": {
    "Ollama": {
      "Endpoint": "http://localhost:11434",
      "DefaultModel": "phi4"
    },
    "Bureau": {
      "Enabled": false
    }
  }
}
```

**Environment variables:** prefix `ARCANUM_`. Use double underscores for nesting after the prefix (for example, `ARCANUM_Arcanum__Ollama__Endpoint` overrides the Ollama endpoint in the `Arcanum` section).

Strongly typed binding uses the **configuration binding source generator** (`EnableConfigurationBindingGenerator` on the Api project) for AOT-friendly options registration. **Do not** add an explicit NuGet `PackageReference` to `Microsoft.Extensions.Configuration.Binder` on the Api project: `Microsoft.AspNetCore.App` already brings it in, and a duplicate reference triggers NuGet package-pruning warnings (for example NU1510).

## Grimoire (encrypted local SQLite)

The **Mage’s Grimoire** is a local-first persistence layer (conversation history and future RAG metadata) backed by **EF Core 10** and **SQLCipher**-enabled SQLite.

**Database file:** `{UserProfile}/.config/arcanum/arcanum.db` (see `ArcanumPaths.GrimoireDatabaseFile` in Core). The directory is created on first use.

**Encryption:** The file is encrypted with **SQLCipher** via **`SQLitePCLRaw.bundle_e_sqlcipher`** (aligned with **SQLitePCLRaw 2.1.x** required by **Microsoft.Data.Sqlite** in EF 10). At host startup, **`SQLitePCL.Batteries_V2.Init()`** runs before any connection opens.

**Passphrase:** The SQLCipher password is **not** the raw master API key. It is derived with **HKDF-SHA256** from the UTF-8 master key using fixed salt/info strings (`GrimoireKeyDerivation` in Infrastructure). **Existing** Grimoire files created with the previous HMAC-based derivation must be recreated or re-encrypted after this change.

**Startup:** An **`IHostedService`** derives the passphrase from **`ISecretStore`**, then:

- If the database file **already exists** but cannot be opened (wrong key, corruption, or tampering), the process logs a **fatal** Serilog event and terminates (**`Environment.FailFast`**).
- If the file **does not** exist, **`EnsureCreatedAsync`** creates the schema (no runtime migrations; local tooling model).

**Inference persistence:** `OllamaIntelligenceProvider` injects **`IGrimoireRepository`**. Both **`POST /api/intelligence/ping-stream`** and **`POST /api/intelligence/ping`** begin a Grimoire turn with **`BeginAssistantReplyAsync`** (new **`Conversation`** or append to an existing one), send chat history plus the new user prompt to **`IChatClient`**, then finalize the assistant row. **`ping-stream`** also **appends each streamed token** during generation and emits a **`conversationBound`** event (see Streaming endpoint) so clients can store the canonical **`conversationId`**. Optional **`conversationId`** on **`PingRequest`** continues a thread: prior **`ChatMessage`** rows are loaded into the model context. Stale or unknown ids start a **new** conversation. The **`ask`** CLI persists that id in **`~/.config/arcanum/cli-session.txt`** (same directory as **`arcanum.db`**) and sends it on the next request; **`arcanum ask -n`** / **`--new`** deletes the file and starts a fresh thread. When **`workingDirectory`** is set, the API merges a **global** codex from **`~/.config/arcanum/CODEX.md`** with an optional **local** **`./CODEX.md`** under **`workingDirectory`** and builds a **dynamic `ChatRole.System`** message (base persona, **`contextSnapshot`** workspace summary, and the merged **`### Master Codex (CODEX.md)`** section). When both files are present, the local file is appended after the global content under a **`### Local Workspace Spells`** sub-header so operator-defined rules cascade Global → Local. Modular spell directories (**`[spell-name]/SPELL.md`**, e.g. **`spells/kalshi-trade/SPELL.md`**) are discovered under that directory (**`SPELL.md`** filename is case-insensitive); YAML frontmatter may include **`name:`** and **`description:`** (if **`name:`** is missing or empty, the spell’s routing name defaults to the **parent folder** name). A **fast pre-flight** model call picks at most one spell whose full markdown is appended under **`### Active Operational Spell`** (see **`docs/DESIGN.md`** §10.2.2). That system message is **prepended only to the in-memory** message list sent to **`IChatClient`**; it is **not** persisted as a Grimoire row. Persistence failures are logged as warnings and do not cancel inference.

**Tool calls:** When the model requests a registered **`Microsoft.Extensions.AI`** tool, the provider runs a bounded multi-round loop, emits **`toolCall`** / **`toolResult`** NDJSON events, appends **`ChatRole.Assistant`** / **`ChatRole.Tool`** turns to the in-memory chat, and persists a bracketed pair of rows via **`AppendToolInteractionAsync`**: an assistant line **`[ToolCall: name(args)]`** and a system line **`[ToolResult: …]`** so the next Grimoire-backed turn reloads tool context as plain text. Built-in tools (Native AOT–friendly, hand-authored JSON schemas, no reflection factories): **`GetLocalSystemTime`**; **`seek_workspace_lore`** (read a UTF-8 text/markdown file under **`workingDirectory`** with path traversal blocked); **`invoke_rune`** (run **`command`** + **`arguments`** with cwd defaulting to **`workingDirectory`**, optional tool argument **`spellDirectory`** to run inside a resolved spell subfolder — same path sandbox as **`seek_workspace_lore`** — **30s** timeout and **`Kill(entireProcessTree: true)`** on overrun). If **`workingDirectory`** is empty, the workspace tools return a clear error string instead of using the API process cwd.

**MCP servers (Cursor-style `mcp.json`):** **`McpConnectionManager`** always loads **`~/.config/arcanum/mcp.json`** (global) once per process and merges in **`mcp.json`** under the normalized **`workingDirectory`** when that file exists. The merged tool list is **cached per workspace root** (including a cache entry when there is no local file, so the inference loop does not rescan disk every request). The JSON shape matches common desktop hosts: top-level **`mcpServers`** map, each entry with **`command`**, **`args`** (array), and optional **`env`** (string map). Duplicate tool names use the **local** registration; if a duplicate exists in both files, **`tools/call`** may **fall back** to the global MCP client once after a local failure **only** when the global and local **server launch recipes** differ (same command/args/env skips the redundant retry). Servers are started lazily with stdio JSON-RPC; if one server fails to start (bad binary, missing dependency, etc.), it is logged and the rest still load. **`DisposeAsync`** shuts down every spawned MCP process across all workspaces. If Ollama rejects tool use (for example **`phi4`** with **`…does not support tools`**), **`OllamaIntelligenceProvider`** retries once **without** registering tools so chat still completes; streaming emits a **`status`** line when that downgrade happens.

**Master API key before host:** [`ArcanumMasterKeyBootstrapper`](src/RetroDownfall.Arcanum.Infrastructure/Security/ArcanumMasterKeyBootstrapper.cs) runs in **`serve`** and **Api.DevHost** *before* **`WebApplicationBuilder.Build()`** so the Grimoire hosted service always sees a stored key when the host starts.

**Infrastructure entry point:** [`AddArcanumInfrastructure(IConfiguration)`](src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs) registers Serilog file logging ([`LoggingBootstrapper`](src/RetroDownfall.Arcanum.Infrastructure/Logging/LoggingBootstrapper.cs)), Data Protection, [`DataProtectionSecretStore`](src/RetroDownfall.Arcanum.Infrastructure/Security/DataProtectionSecretStore.cs), the Grimoire database, [`IWorkspaceScanner`](src/RetroDownfall.Arcanum.Core/Workspace/IWorkspaceScanner.cs) ([`PhysicalWorkspaceScanner`](src/RetroDownfall.Arcanum.Infrastructure/Workspace/PhysicalWorkspaceScanner.cs)), and **[`IEyeOfTheWorld`](src/RetroDownfall.Arcanum.Core/Pattern/IEyeOfTheWorld.cs)** ([`EyeOfTheWorldService`](src/RetroDownfall.Arcanum.Infrastructure/Pattern/EyeOfTheWorldService.cs)) via **`AddArcanumEyeOfTheWorld`**. For **Windows Service**, **launchd**, or **systemd user** lifecycle only (no EF/Serilog/Grimoire), the CLI uses narrow **[`AddArcanumDaemonManagement`](src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs)** to register **[`IDaemonManager`](src/RetroDownfall.Arcanum.Core/Hosting/IDaemonManager.cs)** ([`WindowsDaemonManager`](src/RetroDownfall.Arcanum.Infrastructure/Hosting/WindowsDaemonManager.cs) on Windows, [`MacOsDaemonManager`](src/RetroDownfall.Arcanum.Infrastructure/Hosting/MacOsDaemonManager.cs) on macOS, [`LinuxDaemonManager`](src/RetroDownfall.Arcanum.Infrastructure/Hosting/LinuxDaemonManager.cs) on Linux; other OSes throw **`PlatformNotSupportedException`** from **`AddArcanumDaemonManagement`**). **`Microsoft.Extensions.Hosting.WindowsServices`** and **`Microsoft.Extensions.Hosting.Systemd`** are referenced on **Infrastructure**, **Api**, and **Cli** so **`serve`** can call **`UseWindowsService`** / **`UseSystemd`** with aligned package versions for the target framework. **`Microsoft.EntityFrameworkCore.Tasks`** is referenced for tooling alignment; **`EFOptimizeContext`** / precompiled queries stay **off** so CI and **PublishAot** do not run conflicting MSBuild passes (the compiled model is produced with **`dotnet ef`** below).

**Design-time / compiled model:** The repo includes a source-generated **compiled EF model** under `src/RetroDownfall.Arcanum.Infrastructure/Generated/` (AOT-friendly **`UseModel`**). After changing entities or `OnModelCreating`, regenerate:

```bash
dotnet tool restore
ARCANUM_GRIMOIRE_DEV_KEY='your-local-dev-secret' dotnet ef dbcontext optimize \
  --project src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj \
  --output-dir Generated \
  --namespace RetroDownfall.Arcanum.Infrastructure.Generated \
  --context ArcanumDbContext
```

(`ARCANUM_GRIMOIRE_DEV_KEY` is recommended for **`dotnet ef`** / **`IDesignTimeDbContextFactory`** so your design-time DB uses a known key. MSBuild uses a compile-time placeholder when the variable is unset.)

## Intelligence (Ollama + Microsoft.Extensions.AI)

The API hosts an **Ollama**-backed pipeline via **OllamaSharp** and **`IChatClient`** from **Microsoft.Extensions.AI**. The Core library defines **`IArcanumIntelligenceProvider`**; the Api project implements **`OllamaIntelligenceProvider`**, which lists local models, **pulls** a missing model when needed, then runs inference using the official chat abstraction. Model name matching is **case-insensitive** and handles Ollama's `:latest` tag convention (e.g. requesting `phi4` matches `phi4:latest` locally, avoiding an unnecessary re-download).

**Packages:** `RetroDownfall.Arcanum.Core` references **Microsoft.Extensions.AI.Abstractions**. `RetroDownfall.Arcanum.Api` references **OllamaSharp** and **Microsoft.Extensions.AI** (for `ChatClientExtensions` chat message lists).

**Runtime:** [Ollama](https://ollama.com/) must be running and reachable at `Arcanum:Ollama:Endpoint` (default `http://localhost:11434`). The first request for a model that is not yet local can **download** it and may take a long time.

**Test endpoint:** `POST /api/intelligence/ping` (same **`X-Arcanum-Key`** requirement as other `/api` routes). JSON body uses **`PingRequest`** (camelCase): required **`prompt`**; optional **`model`**; optional **`workingDirectory`** (operator cwd anchor; when set, the server merges the **global** **`~/.config/arcanum/CODEX.md`** with an optional **local** **`./CODEX.md`** under that directory into the dynamic system prompt as **`### Master Codex (CODEX.md)`** — when both exist, the local content is appended after the global content under a **`### Local Workspace Spells`** sub-header; defaults to empty if omitted; the same value scopes **`seek_workspace_lore`** and the default cwd for **`invoke_rune`** when tools are enabled; **`invoke_rune`** also accepts optional **`spellDirectory`** relative to that root); **`SPELL.md`** spells are always discovered under **`~/.config/arcanum/spells/`** and merged with spells under a normalized **`workingDirectory`** when present; if the same spell **`name`** exists in both trees (**case-insensitive**), the **workspace** spell wins; optional **`contextSnapshot`** (Eye of the World **`PatternSnapshot`**: **`domain`**, **`rootPath`**, **`threads`** — included in that system prompt when present); optional **`conversationId`** (GUID string) to continue a Grimoire thread. Example: `{ "prompt": "Say hello in one sentence.", "model": "phi4" }`. When **`model`** is omitted, the server uses `Arcanum:Ollama:DefaultModel`. Response envelope: `ApiResponse<string>` where `data` is the model text on success. HTTP status: **200** on success, **400** for invalid/missing prompt, **500** when inference or model retrieval fails.

**Streaming endpoint:** `POST /api/intelligence/ping-stream` uses the same **`PingRequest`** body shape and API key. The response is **NDJSON** (`Content-Type: application/x-ndjson`): one JSON object per line, each a source-generated **`IntelligenceEvent`** (`type`: `status` \| `conversationBound` \| `token` \| `toolCall` \| `toolResult` \| `result` \| `error`, `message`, optional `data`). After Grimoire begins the turn, a **`conversationBound`** line carries the canonical conversation id in **`data`** (message text is `"Conversation started"`). Status lines report local model checks, download progress, and generation; during inference the server emits many **`token`** lines with incremental assistant text in **`data`** (and an empty `message`); **`toolCall`** / **`toolResult`** lines carry diagnostic text in **`data`** (and often the tool name in **`message`**) when local tools run between model rounds; a final **`result`** line carries the full assistant text in **`data`** for consumers that only read the completion; **`error`** lines describe failures without requiring a buffered `ApiResponse` envelope. Closing the HTTP connection or canceling the request aborts **`HttpContext.RequestAborted`**, which is linked into **`StreamPromptAsync`** and **`IChatClient.GetStreamingResponseAsync`**, so Ollama generation stops promptly and frees GPU work.

Example (replace `YOUR_KEY`):

```bash
curl -sS -X POST "http://localhost:5001/api/intelligence/ping" \
  -H "X-Arcanum-Key: YOUR_KEY" \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Reply with only: pong"}'
```

Streaming (NDJSON lines to stdout):

```bash
curl -sS -N -X POST "http://localhost:5001/api/intelligence/ping-stream" \
  -H "X-Arcanum-Key: YOUR_KEY" \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Reply with only: pong"}'
```

`OllamaApiClient`, `IOllamaApiClient`, `IChatClient`, and `IArcanumIntelligenceProvider` are registered as **scoped** so concurrent requests do not share mutable `SelectedModel` on one client instance. **`IArcanumIntelligenceProvider`** exposes **`ExecutePromptAsync`** / **`StreamPromptAsync`** taking a full **`PingRequest`** for the buffered and NDJSON streaming pipelines used by **`ping`**, **`ping-stream`**, and the CLI **`ask`** command.

## Local API security

The HTTP surface is **loopback-only** and **keyed** for zero-trust local use.

**Listen address:** By default, **`serve`** ([`ServeCommand`](src/RetroDownfall.Arcanum.Cli/Commands/ServeCommand.cs)) and **Api.DevHost** ([`Program.cs`](src/RetroDownfall.Arcanum.Api.DevHost/Program.cs)) use `ListenLocalhost(5001)` so the host is not reachable from LAN interfaces. For container images, set **`ARCANUM_HOST_ANY`** to **`1`** or **`true`** so **`serve`** uses `ListenAnyIP(5001)` instead.

**API key:** Routes under **`/api`** (for example `GET /api/health`, `POST /api/intelligence/ping`, `POST /api/intelligence/ping-stream`) require header **`X-Arcanum-Key`** with the current master key. Requests without a valid key receive **401** with a source-generated JSON body: `ApiResponse<string>` and `Error` code **`Unauthorized`** (serialized via [`ArcanumJsonContext`](src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs)).

**Storage:** The key is encrypted with **ASP.NET Core Data Protection** (`SetApplicationName("ArcanumCore")`, protector purpose **`Arcanum.Core.ApiKey`**) and persisted as **`security.dat`** next to your Arcanum config:

`{ApplicationData}/arcanum/security.dat`

On **first** startup, if no key exists, **`serve`** generates a cryptographically random 32-byte key (Base64), saves it through [`ISecretStore`](src/RetroDownfall.Arcanum.Core/Security/ISecretStore.cs) / [`DataProtectionSecretStore`](src/RetroDownfall.Arcanum.Infrastructure/Security/DataProtectionSecretStore.cs), and prints a green confirmation via Spectre Console. **Api.DevHost** performs the same bootstrap and also prints the raw key once to the console so you can copy it for tools like `curl` during F5 debugging. Integrated clients should resolve **`ISecretStore`** from DI and call **`GetApiKeyAsync()`** instead of persisting secrets in user scripts.

OpenAPI (`/openapi/v1.json`) and Scalar (`/scalar`, `/scalar/v1`) remain on the root pipeline and are **not** covered by the API-key route group.

## CLI

Show help when no arguments are passed:

```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj
```

Host the API on `http://localhost:5001`:

```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- serve
```

Ask the Mage a question (requires **`serve`** on `http://localhost:5001` so the API key exists under the same Data Protection app name). Each HTTP request still carries one user **`prompt`**; the CLI can **continue the last Grimoire conversation** by reading **`~/.config/arcanum/cli-session.txt`** and sending optional **`conversationId`** on **`PingRequest`**. After **`PerceivePatternAsync`**, the stream may include a silent **`conversationBound`** line: the CLI parses the GUID from **`data`** and overwrites the session file (no console output). Use **`-n` / `--new`** to clear that file and omit **`conversationId`** so the next reply starts a new thread. Before each request the CLI resolves **`Environment.CurrentDirectory`**, runs **`IEyeOfTheWorld.PerceivePatternAsync`**, and sends **`workingDirectory`** and **`contextSnapshot`** so the daemon-hosted API receives the operator’s spatial context (the API process cwd is not the shell cwd). The CLI then calls **`POST /api/intelligence/ping-stream`** via **`ArcanumApiClient.AskStreamAsync(PingRequest, …)`**, reading the response body as **NDJSON** (one UTF-8 JSON object per line) and deserializing each line with [`ArcanumJsonContext`](src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs) to match the server’s **`application/x-ndjson`** writer. **`status`** lines are printed to **stderr** (dim markup); assistant **`token`** chunks are written to **stdout** with **`AnsiConsole.Write`** so output matches the model (no extra newlines per chunk). A linked **`CancellationTokenSource`** hooks **`Console.CancelKeyPress`** (graceful cancel) and is passed to **`HttpClient`**, so Ctrl+C aborts the stream, propagates cancellation to the API and Ollama, and exits with code **130** (POSIX interrupt). Exit **0** is success, **1** is an error. Model defaults to `Arcanum:Ollama:DefaultModel` unless you pass **`-m` / `--model`**.

The **`ask`** command takes one logical prompt built from **all** positional words after **`ask`**, and also appends tokens from **`--`** onward (Spectre’s **raw remaining arguments**), so unquoted multi-word prompts work (for example **`arcanum ask local time`**) and the common shell pattern **`arcanum ask -- local time`** passes **`local time`** as the prompt instead of failing with “missing required argument”.

```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- ask "Summarize arcana in one line."
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- ask local time
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- ask -- local time
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- ask "Hello" -m phi4
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- ask "Unrelated question; start a new thread" --new
```

**Eye of the World (`look`):** classifies the **current working directory** into a **`DomainType`** (for example software engineering, administration, research, or unknown) and prints a **bounded table of contents** (up to 20 lines) so agents get concrete filenames without a deep parse. **`IEyeOfTheWorld`** / **`EyeOfTheWorldService`** live in Core / Infrastructure; the CLI registers only **`AddArcanumEyeOfTheWorld()`** (not the full Grimoire stack). When the domain is **Unknown**, the TOC ranks files by **last write time (newest first)**, then **creation time**, so recently touched files surface first.

```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- look
```

**Background agent (`daemon`):** On **Windows** (elevated shell), **`daemon install`** runs **`sc create ArcanumDaemon`** with **`binPath=`** set to **`Environment.ProcessPath`** and arguments **`serve`**, **`start= auto`**, then **`sc start ArcanumDaemon`**. **`serve`** registers **`UseWindowsService`** with service name **`ArcanumDaemon`** so the same binary honors SCM lifetime when running as a service. **`daemon uninstall`** runs **`sc stop`** then **`sc delete`** ( **`sc stop`** when the service is already inactive is treated as success). **`daemon status`** uses **`sc query`**; **`GetStatusAsync`** reads only the numeric **`STATE`** code from **`sc`** output ( **`4`** = running, **`1`** = stopped) because the human-readable state text is **localized** by Windows. Missing service (**1060**) returns success with **`ArcanumDaemon is not installed.`** so **`daemon status`** exits **0**. On **macOS**, the same verbs drive the per-user LaunchAgent at **`~/Library/LaunchAgents/com.retrodownfall.arcanum.plist`**: install writes the plist ( **`Environment.ProcessPath`** + **`serve`**, **`RunAtLoad`**, **`KeepAlive`**) then runs **`launchctl bootstrap gui/<UID> <plist>`** (UID from **`/usr/bin/id -u`**). Uninstall runs **`launchctl bootout gui/<UID> <plist>`** then deletes the plist on success. **`daemon status`** exits **0** when the job is absent, printing **`Daemon is not currently loaded`**. On **Linux**, **`LinuxDaemonManager`** installs a **systemd user** unit at **`~/.config/systemd/user/arcanum.service`** (**`ExecStart=`** **`Environment.ProcessPath`** **`serve`**, **`Restart=always`**, **`WantedBy=default.target`**), runs **`systemctl --user daemon-reload`**, then **`systemctl --user enable --now arcanum.service`**. Uninstall runs **`systemctl --user disable --now arcanum.service`** (ignores errors such as a missing unit), deletes the unit file, then **`daemon-reload`**. **`daemon status`** maps **`ActiveState=active`** to **`Arcanum daemon is running.`** and otherwise returns **`Daemon is not currently loaded.`** (including when the unit file is absent). **`serve`** also calls **`UseSystemd()`** so the generic host receives **systemd** notifications when appropriate (no-op off Linux). In **containers** (**`/.dockerenv`** or **`DOTNET_RUNNING_IN_CONTAINER=true`**), **`daemon install` / `uninstall` / `status`** fail with **`ContainerUnsupported`** and a message to run **`arcanum serve`** as the entrypoint. On OSes outside the Windows / macOS / Linux triad, **`AddArcanumDaemonManagement`** throws **`PlatformNotSupportedException`** at startup.

```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- daemon install
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- daemon status
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- daemon uninstall
```

Health check (replace `YOUR_KEY` with the value from `ISecretStore` / DevHost first-run output, or delete `security.dat` and restart DevHost once to regenerate and print a new key):

```bash
curl -H "X-Arcanum-Key: YOUR_KEY" http://localhost:5001/api/health
```

OpenAPI document (Microsoft.AspNetCore.OpenApi) and Scalar UI (served from the same `serve` host):

- Specification: `http://localhost:5001/openapi/v1.json`
- Interactive reference: `http://localhost:5001/scalar` or `http://localhost:5001/scalar/v1`

Response (camelCase, source-generated `ApiResponse<string>` envelope):

```json
{
  "data": "Arcanum API is online",
  "isSuccess": true,
  "error": null,
  "traceId": "0HMVH2Q7..."
}
```

## Debugging (VS Code)

Open the repo in VS Code with the [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) or [C#](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp) extension. Use **Run and Debug**:

Workspace [`.vscode/settings.json`](.vscode/settings.json) sets **`csharp.debug.justMyCode`** to **`false`** and **`csharp.debug.suppressJITOptimizations`** to **`true`**. **Just My Code** is what triggers the **“You are debugging a Release build of Spectre.Console… (Release)”** guidance for NuGet **Spectre** binaries; turning it off for this repo stops that message. **`suppressJITOptimizations`** still asks the JIT to prefer non-optimized code for **Release** dependencies. If you prefer the default **Just My Code** behavior and accept the Spectre warnings, delete or override those keys (or set **`"justMyCode": true`** in a launch configuration to override the workspace for that profile only).

All [`.vscode/launch.json`](.vscode/launch.json) **`coreclr`** configurations also set **`"justMyCode": false`**, **`"suppressJITOptimizations": true`**, and **`"logging": { "moduleLoad": false }`** so F5 matches the workspace and hides **Loaded …** module lines in the Debug Console. To hide module loads globally, set **`csharp.debug.logging.moduleLoad`** to **`false`** in user `settings.json`.

| Configuration | Purpose |
|----------------|---------|
| **Cli: serve (API on :5001)** | Spectre **`serve`** — same HTTP stack as production CLI; blocks in the integrated terminal (Kestrel loop + tool loop on the server). |
| **Api.DevHost: slim API** | Minimal host for the same routes without Spectre; prints a new master key on first run. When Kestrel logs **Now listening on**, VS Code opens **`/scalar/v1`** in your default browser (`serverReadyAction` in [`.vscode/launch.json`](.vscode/launch.json)). Do not run **serve** and **Api.DevHost** at the same time (both bind **:5001**). |
| **Cli: ask gets local time (needs API :5001)** | One-shot **`ask`** with prompt **`local time`** (exercises multi-word args and the buffered tool loop, including **`GetLocalSystemTime`**). The same host registers **`seek_workspace_lore`** and **`invoke_rune`** (including optional **`spellDirectory`**) for models that support tools. **`preLaunchTask`** runs **`build-cli`** then **`.vscode/wait-arcanum-5001.*`**, which polls **`http://127.0.0.1:5001/`** for up to **45s** so the CLI is not launched against a closed port (avoids refused-connection noise in the debugger). Start **Cli: serve** or **Api.DevHost** in another session first, or the wait step fails with a clear message. |
| **Cli: daemon status** | **`daemon status`** — launchd / `sc` / `systemctl --user` per platform. |
| **Cli: custom verb (prompt)** | Debugger prompts for one argv token (default **`look`**). Use **`--help`** for usage (same as passing no args to **`dotnet run … --`**). For **`ask …`**, **`daemon status`**, or other multi-argument commands, use the **ask** / **daemon status** profiles above or **Arcanum: CLI workspace shell** (below). |

**`ask` and the API:** The CLI’s HTTP client targets **`http://localhost:5001/`** (see `Program.cs`). Inference runs in the **API process** (`serve` or DevHost), including the bounded tool loop; the CLI only sends **`ping-stream`** and prints NDJSON. So **`ask`** needs an API host already listening (or you will see connection errors).

**Arbitrary CLI commands:** A debug launch always passes fixed `args` to the target process; there is no built-in “blank REPL” that stays attached to the debugger. Practical workflow: start **Cli: serve (API on :5001)** in one debug session (one integrated terminal, host loop). Then either (1) start a **second** debug session from the dropdown (e.g. **Cli: ask gets local time** or **Cli: custom verb**) so breakpoints work in the CLI process, or (2) run **Tasks: Run Task** → **Arcanum: CLI workspace shell** to open another terminal in the repo and type `dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- …` for full freedom (breakpoints only if you attach the debugger to that `dotnet` process, or use a second launch configuration).

Build tasks live in [`.vscode/tasks.json`](.vscode/tasks.json); the default **build** task builds the whole solution (`RetroDownfall.Arcanum.slnx`).

## Native AOT publish (CLI)

```bash
dotnet publish src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -c Release
```

First-party **EF Core** trim/AOT diagnostics are addressed with **`[UnconditionalSuppressMessage]`** on **`ArcanumDbContext`** (**`IL3050`**, **`IL2026`**) and **`GrimoireDatabaseHostedService.StartAsync`** (**`IL3050`** for **`EnsureCreatedAsync`**), plus **`[UnconditionalSuppressMessage]`** on **`AddArcanumApiServices`** (**`IL2026`** for OpenAPI/Mvc metadata). **`Program.Main`** keeps **`[UnconditionalSuppressMessage("AOT", "IL3050")]`** for **Spectre** **`CommandApp`** construction, and **`DynamicDependency`** covers **`ServeCommand`**, **`AskCommand`** (including **`AskCommand.Settings`** with **`DynamicallyAccessedMemberTypes.All`**), **`LookCommand`**, daemon commands, **`ArcanumApiClient`**, and **`CliTypeRegistrar`**. **`TrimmerRootAssembly`** still roots **`Spectre.Console.Cli`**. Grouped third-party ILC warnings (**`IL2104`**, **`IL3053`**, **`IL3002`**, **`IL3000`**, **`IL2026`**) that have no valid first-party suppression site are filtered **only for the ILC step** via **`<IlcArg Include="--nowarn:…" />`** in the **Cli** project (Roslyn/IDE still report trim issues in your code). On **macOS**, **`dotnet publish`** may still print **`EXEC : warning`** lines from **Apple clang** about missing **`.pcm`** module cache paths embedded in static runtime libraries; those are **not** IL diagnostics—the publish succeeds and the native binary is unaffected.

## Layout

| Project | Role |
|--------|------|
| `src/RetroDownfall.Arcanum.Core` | Shared domain primitives under `Primitives/`; strongly typed settings and `ConfigurationBootstrapper` under `Configuration/` (`ArcanumSettings`, centralized JSON + `ARCANUM_` env); **`Security/`** (`ISecretStore` contract only); **`Hosting/`** (**`IDaemonManager`**); **`Intelligence/`** (`IArcanumIntelligenceProvider`, `PingRequest`, streaming DTOs under **`Intelligence/Models/`** such as **`IntelligenceEvent`**); **`Storage/`** (Grimoire POCOs, **`ArcanumPaths`**, **`IGrimoireRepository`**); **`Workspace/`** (`IWorkspaceScanner`); **`Pattern/`** (`IEyeOfTheWorld`, **`DomainType`**, **`PatternSnapshot`**). |
| `src/RetroDownfall.Arcanum.Infrastructure` | OS / persistence: **`AddArcanumInfrastructure`**, **`AddArcanumEyeOfTheWorld`**, **`AddArcanumDaemonManagement`** ( **`IDaemonManager`** → Windows **[`WindowsDaemonManager`](src/RetroDownfall.Arcanum.Infrastructure/Hosting/WindowsDaemonManager.cs)** / macOS **[`MacOsDaemonManager`](src/RetroDownfall.Arcanum.Infrastructure/Hosting/MacOsDaemonManager.cs)** / Linux **[`LinuxDaemonManager`](src/RetroDownfall.Arcanum.Infrastructure/Hosting/LinuxDaemonManager.cs)** ), Serilog rolling JSON ([`Logging/`](src/RetroDownfall.Arcanum.Infrastructure/Logging/)), Data Protection + [`DataProtectionSecretStore`](src/RetroDownfall.Arcanum.Infrastructure/Security/DataProtectionSecretStore.cs), [`ArcanumMasterKeyBootstrapper`](src/RetroDownfall.Arcanum.Infrastructure/Security/ArcanumMasterKeyBootstrapper.cs), EF Core **SQLCipher** (`ArcanumDbContext`, `GrimoireRepository`, `GrimoireDatabaseHostedService`, HKDF passphrase), [`PhysicalWorkspaceScanner`](src/RetroDownfall.Arcanum.Infrastructure/Workspace/PhysicalWorkspaceScanner.cs), **[`Pattern/EyeOfTheWorldService`](src/RetroDownfall.Arcanum.Infrastructure/Pattern/EyeOfTheWorldService.cs)**, and generated compiled model under **`Generated/`**. **MCP client foundation (Native AOT):** JSON-RPC 2.0 DTOs, MCP wire DTOs (**[`McpWireDtos.cs`](src/RetroDownfall.Arcanum.Infrastructure/Mcp/Protocol/McpWireDtos.cs)**), and source-generated **[`McpJsonSerializerContext`](src/RetroDownfall.Arcanum.Infrastructure/Mcp/Protocol/JsonRpcModels.cs)**; internal stdio **[`McpProcessTransport`](src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpProcessTransport.cs)** (line-delimited messages, UTF-8 stdin/stdout/stderr, bounded inbound channel); internal **[`McpClient`](src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpClient.cs)** (JSON-RPC **id** correlation, **`initialize`** / **`notifications/initialized`**, **`tools/list`**, **`SendRequestAsync`** with default **60s** timeout); internal **[`McpBridgeTool`](src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs)** (**`Microsoft.Extensions.AI`** **`AIFunction`** → **`tools/call`**). Packages include **`Microsoft.EntityFrameworkCore.Sqlite.Core`**, **`SQLitePCLRaw.bundle_e_sqlcipher`**, **`Microsoft.EntityFrameworkCore.Tasks`** (with **`EFOptimizeContext`** disabled—see Grimoire section), **`Microsoft.EntityFrameworkCore.Design`** (private), **`Serilog.AspNetCore`**, **`Microsoft.Extensions.Hosting.WindowsServices`**, **`Microsoft.Extensions.Hosting.Systemd`**, **`Microsoft.Extensions.AI`**. Class library is **`IsTrimmable`** / **`PublishAot`**-marked for Native AOT alignment. |
| `src/RetroDownfall.Arcanum.Api` | HTTP surface: `AddArcanumApiServices(IConfiguration)` calls **`AddArcanumInfrastructure`** (Serilog, secrets, Grimoire, workspace scanner) then registers Ollama, OpenAPI, JSON options, and `MapArcanumEndpoints` (OpenAPI/Scalar + **`/api`** route group with `ApiKeyEndpointFilter`, **`POST /api/intelligence/ping`**, NDJSON **`POST /api/intelligence/ping-stream`**), `Intelligence/OllamaIntelligenceProvider`, and `ArcanumJsonContext` under `Serialization/`. Hosts must **`ClearProviders()`** before service registration so Serilog replaces default logging. References **`Microsoft.Extensions.Hosting.WindowsServices`** and **`Microsoft.Extensions.Hosting.Systemd`** for version alignment with **Cli** / **Infrastructure** ( **`UseWindowsService`** / **`UseSystemd`** are invoked from **`ServeCommand`**, not from this class library). |
| `src/RetroDownfall.Arcanum.Api.DevHost` | Debug-only console host (no Spectre) for F5 on the API stack from VS Code; references **Api**, **Core**, and **Infrastructure** (for [`ArcanumMasterKeyBootstrapper`](src/RetroDownfall.Arcanum.Infrastructure/Security/ArcanumMasterKeyBootstrapper.cs)); same configuration and **loopback + API key** bootstrap as `serve`, printing the generated key once on first run for developer convenience. |
| `src/RetroDownfall.Arcanum.Cli` | Entry point: Spectre `CommandApp` with MS DI (**DataProtection** + **`ISecretStore`** via [`DataProtectionSecretStore`](src/RetroDownfall.Arcanum.Infrastructure/Security/DataProtectionSecretStore.cs) from **Infrastructure** so pre-`serve` resolution matches the API host, **`AddArcanumEyeOfTheWorld`**, **`AddArcanumDaemonManagement`**, named **`HttpClient` "ArcanumApi"`**, **`ArcanumApiClient`**). **`serve`** hosts the slim API (`AsyncCommand`) and calls **`UseWindowsService`** (**`Microsoft.Extensions.Hosting.WindowsServices`**, service name **`ArcanumDaemon`**) and **`UseSystemd()`** (**`Microsoft.Extensions.Hosting.Systemd`**). Kestrel uses **`ARCANUM_HOST_ANY`** (**`1`** / **`true`**) to choose **`ListenAnyIP(5001)`** vs loopback. **`ask`** injects **`IEyeOfTheWorld`**, builds **`PingRequest`** (spatial fields + optional **`conversationId`** from **`CliSessionManager`** / **`~/.config/arcanum/cli-session.txt`**; **`--new`** clears it), persists ids from **`conversationBound`** on the NDJSON stream, then streams **`/api/intelligence/ping-stream`** with **`X-Arcanum-Key`** (`HttpCompletionOption.ResponseHeadersRead` + line-delimited NDJSON via **`JsonSerializer.Deserialize`** per line), prints **`status`** on stderr and live **`token`** text on stdout, then a trailing newline after streamed output (errors on stderr; exit **0** / **1** / **130** on Ctrl+C). **`look`** runs **`IEyeOfTheWorld`** on **`Environment.CurrentDirectory`** and prints domain + TOC (Spectre markup: silver labels, sky-blue values). **`daemon install` / `uninstall` / `status`** resolve **`IDaemonManager`** (Windows **`sc`** / macOS launchd / Linux **`systemctl --user`**; see CLI section). Project references **Core**, **Api**, and **Infrastructure**. |

Solution file: `RetroDownfall.Arcanum.slnx` (XML SLNX format).
