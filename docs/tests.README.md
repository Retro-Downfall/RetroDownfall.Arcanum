# RetroDownfall.Arcanum.Tests

xUnit test suite for **Core**, **Infrastructure**, **Api**, and **Cli** shipping assemblies. Tests run on the normal CLR (not Native AOT). Hand-written fakes only — no Moq.

## Quick commands

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
./scripts/coverage.sh
./scripts/coverage.sh --threshold
```

**Coverage gates** (post-exclusions, see `coverage.runsettings`):

| Metric | Target |
|--------|--------|
| Line | ≥ 85% |
| Branch | ≥ 75% |
| Security-critical types | 100% branch (`ApiKeyEndpointFilter`, `ApiKeyDigestCache`, `DataProtectionSecretStore`, `GrimoireKeyDerivation`, `McpSecurityLimits`, `SandboxedFileIo`, `SanctumGuard`, `ToolHelpers`, `OutboundUrlGuard`, `WardGate`) |

Reports are written to `.tmp/coverage/report/index.html`.

### CI drop-in (when CI is introduced)

```yaml
- run: dotnet tool restore
- run: ./scripts/coverage.sh --threshold
```

## Conventions

### Test data

- **Checked-in static inputs** live under `TestData/<Feature>/` (e.g. `TestData/Spells/`, `TestData/Configuration/`). Marked `CopyToOutputDirectory=PreserveNewest` in the `.csproj`. Treat as **read-only**; copy into a temp dir before mutating.
- **Mutable scenarios** (workspace trees, Grimoire DB copies, CODEX writes) use `Support/TempWorkspace` or fixture helpers. Each fixture creates `Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid())` and deletes it on dispose.

### Collections & parallelization

xUnit runs **collections in parallel**; tests inside a collection run **serially**.

| Collection | Purpose |
|------------|---------|
| *(default)* | Pure-logic tests — parallel, no shared process state |
| `[Collection("Grimoire")]` | SQLCipher template DB; per-test file copies |
| `[Collection("ApiHost")]` | `ArcanumWebApplicationFactory` — shared WAF, isolated `HOME`, `DisableParallelization` |
| `[Collection("WorkspacePathPolicy")]` | Static path-comparison test seams — `DisableParallelization` |

### SQLCipher

Grimoire DB tests use `[SkippableFact]` and skip when `e_sqlcipher` is unavailable (`GrimoireFixture.SqlCipherAvailable`).

### API host integration

`Fixtures/ArcanumWebApplicationFactory.cs` references `Api.DevHost`, seeds an encrypted Grimoire copy, swaps `ISecretStore` / `IArcanumIntelligenceProvider` fakes, and provides `CreateAuthenticatedClient()`.

### CLI harness

`Cli/Infrastructure/CliApplicationFactory` builds a `CommandApp` with a test `ServiceCollection`. Use `Spectre.Console.Testing.TestConsole` for command output assertions.

### Code style

One blank line after each C# statement in test code (matches production style).

## Bug-squash coverage (changeset review)

The following test classes gained cases directly from the bug-squash plan. Each is named for the fix it locks down; see the plan in `docs/` for the per-bug rationale.

| Test class | Coverage added |
|------------|----------------|
| `BudgetMonitorTests` | Singleton captive-dependency fix (`IOptionsMonitor` + `IServiceScopeFactory`); alert-record-before-dispatch ordering; duplicate-alert suppression via `RecordAlertAsync` returning `false`. |
| `GuardrailsPipelineTests` / `GuardrailAuditLoggerTests` | Async audit-log observation (no fire-and-forget); multiple-violation auditing; phone regex balanced-parens enforcement; topic-regex cache bound. |
| `JsonSchemaHelperTests` / `StructuredOutputValidatorTests` | Nullable type-array validation; GBNF rule-name collision; enum short-circuit now requires `type: "string"` or absent; numeric enum equality via `decimal`. |
| `ArcanumErrorMapperTests` | New codes (`Prompt.InvalidRequest`, `Session.InvalidStatus`, `Validation.InvalidQuery`, `Embeddings.ConfirmationRequired`, `StructuredOutput.*`); `ResolveStatusCodeDefaultBadRequest` preserves all explicit 500 mappings. |
| `GrimoireRepositoryTests` | `GetTodaySpendAsync` sargable half-open range + decimal sum in C#; `DeleteEntryAsync` decrements `UnsummarizedEntryCount`; `IncrementSessionTokensAndCostAsync` clamps negatives. |
| `EmbeddingsResetScopeTests` / `EmbeddingsResetServiceTests` | `ParseScope` rejects typos (no silent `All` escalation); `?confirm=true` gates with `Embeddings.ConfirmationRequired`. |
| `ArcanumBrowseWebToolTests` | `MaxLinks` clamp; SSRF error surfacing; response charset; timeout via `OperationCanceledException` inner `TimeoutException`; nav/header/footer link filter. |
| `RequestAugmentingHandlerTests` | Replaced `HttpContent` disposed; retry re-applies content headers; non-object JSON guarded. |
| `ClientToolForwardingTests` | Duplicate tool names; `tool_choice.function.name` verified against supplied tools; `tool_choice: "auto"`/`"none"` accepted when forwarding disabled; per-tool `strict` forwarded. |
| `OpenAiV1EndpointTests` / `OpenAiV1BatchesEndpointTests` | Structured-output failure maps to `validation_failed`/`invalid_schema` (not generic `inference_failed`); batch reset cleans orphan output/error files. |
| `SessionEndpointTests` | SSE `since` 404 returned with clean headers (no SSE headers leaked); `ErrorCodes.Session.EntryNotFound`/`InvalidStatus` constants used. |
| `CostCalculatorTests` | Cached prompt tokens billed at zero (only non-cached portion priced). |

## Budget

Full `dotnet test` should complete in **under 60 seconds** locally. Largest costs: one-time Grimoire template build and ApiHost WAF boot.

## Exclusions

Types marked `[ExcludeFromCodeCoverage] // Reason: ...` are documented in source. JSON source-gen contexts are excluded via `coverage.runsettings` only. See `DESIGN.md` §13 for the policy summary.
