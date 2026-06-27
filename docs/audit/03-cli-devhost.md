# 03 — Cli + Api.DevHost (`RetroDownfall.Arcanum.Cli`, `RetroDownfall.Arcanum.Api.DevHost`)

**Scope:** the shipping `arcanum` executable — the `ArcanumApiClient` (HTTP/streaming), Spectre commands, UX rendering, session/secret handling — plus the debug-only DevHost. Cli: 42 files, ~8.4k lines; DevHost: 2 files.

**Method:** two parallel read-only deep-read passes ([API client + services](1a1b086b-9e90-4177-884a-070cf5ed99a3), [commands + UX + DevHost](667fffc2-cb4c-46a1-a01a-9a97220c33b3)); the P1 and a behavioral P2 were re-verified against source. Severities reflect that this is a local, single-user CLI.

Severity counts: **P1 ×1 · P2 ×7 · P3 ×9.** DevHost parity is faithful (intentional drift only).

---

## P1 findings

### [P1][reliability] `ask`/`chat` `Environment.FailFast` (crash) when no master API key is stored
- **Location:** `src/RetroDownfall.Arcanum.Cli/Commands/ChatCommand.cs:81` and `src/RetroDownfall.Arcanum.Cli/Commands/AskCommand.cs:79` → `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs:45-54`
- **Observation:** Both commands call `grimoireBootstrapper.EnsureInitializedAsync(...)` before any API call. The bootstrapper does:
  ```csharp
  string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);
  if (string.IsNullOrWhiteSpace(apiKey))
  {
      Log.Fatal("Grimoire startup aborted: master API key is not present...");
      Environment.FailFast("Arcanum Grimoire requires the master API key.");
  }
  ```
- **Impact:** Running `arcanum ask`/`chat` before a key exists (e.g. before the first `serve`) **crashes the process via `FailFast`** — abnormal termination, a crash dump / Watson report, and a non-deterministic exit code — instead of the clean `Security.MissingApiKey` error + exit 1 that `ArcanumApiClient` returns for the same condition. This breaks scripting and is a poor first-run experience for a recoverable, normal condition.
- **Recommendation:** Detect the missing-key case in the CLI bootstrap path (or have the bootstrapper return a `Result`) and print the same friendly error before returning exit 1. Reserve `FailFast` for genuine host/DB integrity failures.

---

## P2 findings

### [P2][reliability] Non-streaming responses are deserialized with no `JsonException` guard
- **Location:** `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs:133` (and the other non-streaming `JsonSerializer.Deserialize` sites, e.g. `:221,:306,:1485`)
- **Observation:** `ApiResponse<T>? envelope = JsonSerializer.Deserialize(responseBytes, responseTypeInfo);` is not wrapped; `InvalidResponseError` is only returned when `envelope is null` (empty body), not when the body is syntactically invalid JSON.
- **Impact:** A non-JSON/corrupt body (a proxy error page, a truncated response) throws `JsonException` out of the client; callers expect a `Result` failure and don't catch it, so the CLI can terminate with an unhandled exception instead of a formatted `[Api.InvalidResponse]` message.
- **Recommendation:** Catch `JsonException` around every non-streaming deserialize and return a parse-failure `Result`.

### [P2][reliability] Turn bodies catch only `OperationCanceledException`
- **Location:** `src/RetroDownfall.Arcanum.Cli/Commands/ChatCommand.cs:1501-1504`, `src/RetroDownfall.Arcanum.Cli/Commands/AskCommand.cs:195-198`
- **Observation:** Neither turn loop has a general `catch (Exception)`; only cancellation is handled. Other faults (e.g. from `IChronosyncEngine.AnalyzeAndSyncAsync` at `ChatCommand.cs:1351`, or an unexpected client throw per the finding above) propagate to Spectre.
- **Impact:** Unexpected failures surface as raw unhandled exceptions/stack traces to the user rather than a formatted error + exit 1.
- **Recommendation:** Wrap the turn in `catch (Exception)`, render an error panel, and return exit 1 (chat: show error and continue the REPL).

### [P2][correctness] Pre-stream HTTP failures drop the error `Code`
- **Location:** `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs:1323-1325,2086-2088`
- **Observation:** When a stream request returns a non-success status, only `envelope.Error.Value.Message` is forwarded into the `Error` event / `LlamaPullProgress.Error`; `Error.Code` is discarded.
- **Impact:** Pre-stream failures (401/429/…) in `ask`/`chat`/`llama pull` show as bare text, losing the `[ErrorCode] message` format used everywhere else.
- **Recommendation:** Format with the shared `FormatError(error)` helper when `Code` is present.

### [P2][correctness] `ask_human` submit failure replaces the API error with a fixed string
- **Location:** `src/RetroDownfall.Arcanum.Cli/Services/AskHumanToolCallStreamHandler.cs:99-104`
- **Observation:** On `submitResult.IsFailure`, emits a fixed `"Failed to submit response to Daemon..."` and never reads `submitResult.Error`.
- **Impact:** Structured errors (`Intelligence.HumanPromptNotFound`, timeouts) are hidden from the operator.
- **Recommendation:** Render `submitResult.Error` via the palette error markup.

### [P2][performance] Unbounded markdown render of the full response
- **Location:** `src/RetroDownfall.Arcanum.Cli/UX/MarkdigSpectreRenderer.cs:15-31` + `src/RetroDownfall.Arcanum.Cli/Commands/ChatCommand.cs:1319-1544`
- **Observation:** `chat` accumulates the entire response in a `StringBuilder` and then `markdig.Render(body)` parses it whole; the renderer has parse fallbacks but no maximum input length.
- **Impact:** A very large model response causes high memory (buffer + Markdown AST + renderables) and can stall the REPL.
- **Recommendation:** Cap the renderable body length (truncate with a notice) or skip the Markdig pass above a threshold.

### [P2][aot] `DoctorCommand.Settings` is not rooted for trimming/AOT
- **Location:** `src/RetroDownfall.Arcanum.Cli/Program.cs:30` (roots `DoctorCommand` but not its `Settings`) vs the `AskCommand.Settings`/`ChatCommand.Settings` roots; option defined at `DoctorCommand.cs:76-84`.
- **Observation:** Other command `Settings` types get `[DynamicDependency(... typeof(X.Settings))]`; `DoctorCommand.Settings` (which declares `--fix-permissions`) does not.
- **Impact:** Under aggressive trimming, binding `--fix-permissions` may fail at runtime in the Native AOT build (partially mitigated by `<TrimmerRootAssembly Include="Spectre.Console.Cli" />`).
- **Recommendation:** Add the `[DynamicDependency]` for `DoctorCommand.Settings` for parity.

### [P2][correctness] Some subcommands skip client-side flag validation
- **Location:** `src/RetroDownfall.Arcanum.Cli/Commands/Daemon/DaemonInitiativeCommand.cs:48-49` (`Minutes` int, no range check); `src/RetroDownfall.Arcanum.Cli/Commands/Llama/LlamaStartCommand.cs:21-27,41-43` (`--gpu-layers`, `--port` passed through unchecked).
- **Observation:** Unlike `ask`/`chat` (which validate inference flags via `InferenceFlagBinder` before sending), these send unvalidated values (negative/zero/out-of-range) to the API.
- **Impact:** Inconsistent UX; invalid values rely entirely on server rejection.
- **Recommendation:** Validate client-side (e.g. `Minutes >= 1`, port `1..65535`) before the HTTP call.

## P3 findings

- **[P3][security/design] Attachments are not jailed to the workspace** — `@path` resolves `Path.GetFullPath(Path.Combine(cwd, token))` with no containment check (`ChatCommand.cs:177-196`), and `/attach` can browse up to filesystem root (`:959-1094`). For a single-user local CLI sending to the user's own localhost API this is largely by-design (the user is attaching their own files), but it should be a conscious decision; consider clamping to a workspace root if attachments are meant to be scoped. Also, an oversized `@path` leaves the literal token in the prompt (`:220-232`).
- **[P3][reliability] `ask` streams raw model tokens** — `AnsiConsole.Write(chunk)` (`AskCommand.cs:136`) writes raw text (it does **not** interpret Spectre markup, so `[` is safe), but ANSI escape sequences in model output pass through to the terminal. This is reasonable for piped raw output; if terminal hygiene matters, strip control sequences when interactive.
- **[P3][concurrency] `cli-session.txt` read-modify-write isn't cross-process serialized** — `CliSessionManager.cs:55-76` writes atomically (temp + move + owner-only perms) but two concurrent `chat` sessions can race; last writer wins on the session id. Document single-writer expectation or add an advisory lock.
- **[P3][resource-safety] `ServiceProvider` disposal depends on Spectre disposing the resolver** — `CliTypeRegistrar.cs:10`/`CliTypeResolver.Dispose` build/dispose the provider, but `Program.Main` doesn't dispose `CommandApp` explicitly. Benign for a short-lived CLI; confirm Spectre's disposal contract.
- **[P3][maintainability] `ArcanumApiClient` duplicates non-streaming HTTP boilerplate** across ~20 methods instead of routing through `SendRequestAsync`/`GetApiAsync` (drift risk); some methods omit `HttpCompletionOption.ResponseHeadersRead` (`:2256,2319,2381`); a streaming send `OperationCanceledException` can mislabel a disconnect as a timeout (`:1293-1295,2060-2062`).
- **[P3][reliability] Outer `chat` loop cancellation** — host-token cancellation could surface an uncaught OCE rather than exit 130 (`ChatCommand.cs:97`); fine for interactive TTY use, worth a top-level catch if host cancellation should map to 130.
- **[P3][docs/style] House-style blank-line drift** in several commands (`Lore/LoreListCommand.cs`, `LookCommand.cs`, install commands).

## DevHost ↔ `serve` parity (verified)

The DevHost is a **faithful F5 surface for API behavior**: it calls the same `AddArcanumConfiguration()` / `AddArcanumApiServices()` DI (including the JSON source-gen contexts) and the same middleware pipeline (`UseArcanumExceptionHandler` → `UseArcanumCors` → `UseArcanumRateLimiter` → `MapArcanumEndpoints`) with the same Kestrel body-size clamp. All differences are **intentional** and safe:

| Aspect | `serve` (`ServeCommand.cs`) | `DevHost/Program.cs` |
|--------|---------------------------|----------------------|
| Bind address | `ListenAnyIP`/`ListenLocalhost` per config | **always `ListenLocalhost`** (safer for debug) |
| `ListenAny` confirmation + banner | yes | n/a (never binds any) |
| Windows service / systemd integration | yes | no |
| Graceful-stop registration | yes | no |
| Master-key bootstrap | always | skipped in `Testing` env |
| New-key output / startup log | themed `AnsiConsole` + Serilog | `Console` + Serilog |

No API endpoint, DI service, or JSON-context wiring is missing from DevHost — only host packaging, binding policy, and dev ergonomics differ. **No findings.**

## Verified strengths

- **HttpClient is factory-managed and correctly split:** streaming calls (`ask`/`chat`/`llama pull`) use the `"ArcanumApi"` client with `Timeout.InfiniteTimeSpan`; non-streaming calls use the bounded client (`ApiRequestTimeoutSeconds`, default 60s). No `new HttpClient()` per call; the API key is set per-request, not on shared default headers. (Test-confirmed.)
- **Streaming is incremental:** `HttpCompletionOption.ResponseHeadersRead` + `ReadAsStreamAsync` + `StreamReader.ReadLineAsync` for NDJSON, with `using`/`await using` disposal; user cancellation is rethrown while `IOException`/`HttpRequestException` map to friendly messages.
- **Cancellation/exit codes are correct:** `Console.CancelKeyPress` → linked CTS; `ask` returns **130** on in-flight Ctrl+C; `chat` cancels the turn and returns to the prompt; handlers are removed in `finally`; per-turn CTS and Chronosync scope are disposed.
- **Inference flags are validated before sending** (`InferenceFlagBinder`: temperature 0–2, top-p 0–1, max-tokens ≥1, penalties −2..2, response-format enum).
- **Secrets:** `key show` writes to **stderr** with a note; the API key is read from the secret store and never logged; `cli-session.txt` is written owner-only.
- **AOT:** all client serialization uses `ArcanumJsonContext` (no reflection); `Program.Main` carries extensive `[DynamicDependency]` command roots; `Spectre.Console.Cli` is a trimmer root. `MarkdigSpectreRenderer` is a stateless singleton with defensive parse fallbacks and `Markup.Escape` on inline text.
