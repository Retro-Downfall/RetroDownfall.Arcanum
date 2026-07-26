# RetroDownfall.Arcanum.Tests

xUnit test suite for **Core**, **Infrastructure**, **Api**, and **Cli** shipping assemblies. Tests run on the normal CLR (not Native AOT). Hand-written fakes only — no Moq.

## Quick commands

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
./scripts/coverage.sh
./scripts/coverage.sh --threshold
```

Windows serial verification uses the host PowerShell and the normal user NuGet cache:

```powershell
$env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --configuration Release -- xUnit.ParallelizeTestCollections=false
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj --configuration Release
```

Run `scripts/coverage.sh --threshold` from Git Bash for the normal parallel coverage gate. Threshold evaluation uses Python when available and falls back to Windows PowerShell on Windows; the test runner itself remains parallel.

**Coverage gates** (post-exclusions, see `coverage.runsettings`):

| Metric | Target |
|--------|--------|
| Line | ≥ 85% |
| Branch | ≥ 75% |
| Security-critical types | 100% branch (`ApiKeyEndpointFilter`, `ApiKeyDigestCache`, `DataProtectionSecretStore`, `GrimoireKeyDerivation`, `McpSecurityLimits`, `SandboxedFileIo`, `SanctumGuard`, `ToolHelpers`, `OutboundUrlGuard`, `HostProcessToolPolicy`, `IdempotencyClaimStore`, `BudgetReservationService`, `WardGate`) |

Assemblies under gate: **Core**, **Infrastructure**, **Api** (Cli interactive surfaces are exercised by scenario tests but excluded from coverlet Include filters — Terminal.Gui Command Center / UX is not line-covered).

Reports are written to `.tmp/coverage/report/index.html`.

### CI

See [`.github/workflows/ci.yml`](../.github/workflows/ci.yml). Authoritative Arcanum coverage collection:

```yaml
- run: ./scripts/coverage.sh --threshold
```

Coverage thresholds are enforced in CI. The latest Windows Git Bash parallel run passed **3,448 tests** (6 platform skips) at **86.37% line / 76.05% branch**, with every gated security type at 100% branch coverage. Run the same hard gate locally with `./scripts/coverage.sh --threshold`. Coverage HTML + Cobertura upload as the `arcanum-coverage-report` workflow artifact.

Compendium runs as a separate `dotnet test` step in the same job (coverage filters remain Arcanum-only). **The Forge remains excluded from CI build and test**; use the Windows verification command above until `tests/RetroDownfall.TheForge.Tests` and the `RetroDownfall.TheForge.Ux` solution build are re-enabled in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml).

## Conventions

### Test data

- **Checked-in static inputs** live under `TestData/<Feature>/` (e.g. `TestData/Spells/`, `TestData/Configuration/`). Marked `CopyToOutputDirectory=PreserveNewest` in the `.csproj`. Treat as **read-only**; copy into a temp dir before mutating.
- **Mutable scenarios** (workspace trees, Grimoire DB copies, CODEX writes) use `Support/TempWorkspace` or fixture helpers. They retain and delete only their exact uniquely named owned root; cleanup must never infer an ancestor by walking parent directories. API-host tests additionally set guarded `ARCANUM_TEST_HOME` while the environment is `Testing`; this is required on Windows because changing `HOME`, `USERPROFILE`, or `APPDATA` does not redirect .NET known-folder paths after process start.

### Collections & parallelization

xUnit runs **collections in parallel**; tests inside a collection run **serially**.

| Collection | Purpose |
|------------|---------|
| *(default)* | Pure-logic tests — parallel, no shared process state |
| `[Collection("Grimoire")]` | SQLCipher template DB; per-test file copies |
| `[Collection("ApiHost")]` | `ArcanumWebApplicationFactory` — shared WAF, isolated persistent root, PID file disabled, `DisableParallelization` |
| `[Collection("ProcessEnvironment")]` | Process environment/global Arcanum path mutation (including built-in spell fixtures) plus Grimoire-backed tests that mutate it — `DisableParallelization` |
| `[Collection("OutboundUrlGuardDns")]` | Process-global `OutboundUrlGuard.DnsResolver` replacement — `DisableParallelization` |
| `[Collection("WorkspacePathPolicy")]` | Static path-comparison test seams — `DisableParallelization` |

### SQLCipher

Grimoire DB tests use `[SkippableFact]` and skip when `e_sqlcipher` is unavailable (`GrimoireFixture.SqlCipherAvailable`). The availability probe disables SQLite pooling and requires its temporary database to be deletable before reporting SQLCipher available. Cached-template validation, remediation, and main-database/sidecar copying share both an in-process lifecycle lock and a named cross-process mutex, so concurrent test/coverage processes cannot delete or expose a partial template while another process copies it.

`ArcanumWebApplicationFactory` still redirects process-global testing paths for host bootstrap, but each factory now registers `ArcanumDbContext` with an explicit SQLCipher connection string rooted under that factory's own `TempHome`. Later environment changes therefore cannot redirect scoped repositories to another factory's database. Every test class that creates a factory participates in the non-parallel `ApiHost` collection; a reflection guard covers the performance baseline that previously escaped serialization.

### API host integration

`Fixtures/ArcanumWebApplicationFactory.cs` references `Api.DevHost`, seeds an encrypted Grimoire copy under its temporary testing root, disables the production PID file, swaps `ISecretStore` / `IArcanumIntelligenceProvider` fakes, and provides `CreateAuthenticatedClient()`.

The factory sets `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` to `Testing`, `ARCANUM_TEST_HOME` to its temporary root, and `ARCANUM_SKIP_KEY_BOOTSTRAP=1` **before** the entry point runs (not only via `UseEnvironment`), because top-level `Program` reads the environment at `CreateSlimBuilder` time. `ArcanumPaths` honors `ARCANUM_TEST_HOME` only in a Testing environment, so production path behavior is unchanged. Global `mcp.json`, Grimoire, secret-store, and log paths all resolve beneath that root. Both synchronous and asynchronous factory disposal stop the host and run the Grimoire shutdown checkpoint before restoring process environment variables; pooled test-host SQLite connections are then cleared before deleting the isolated tree.

**Serilog / WAF hang:** `AddArcanumSerilog` must not call `GetRequiredService` for options or the ring-buffer sink inside the `AddSerilog` configure callback during host `Build()` — that re-enters logging DI and deadlocks, and HostFactoryResolver then times out waiting for `HostBuilt`. The ring-buffer sink is deferred until first emit; Testing resolves the log directory through the isolated Arcanum root before any directory creation or ACL hardening, then skips the rolling file sink.

### First-class reasoning matrix

Reasoning coverage is distributed by the production boundary it protects; do not create duplicate "reasoning" fixtures merely to increase test count.

- **Contracts/configuration:** `ReasoningContractsJsonTests`, `ModelEntryJsonConverterTests`, `PricingSettingsTests`, `ConfigurationValidatorTests`, `PingRequestBoundsValidatorTests`, model endpoint tests, source-generated-context completeness tests, and Compendium descriptor/parity/preservation tests cover all enum values, unchanged string wire names through the shared closed-generic string-only converter, defined/undefined integer rejection through direct AOT contexts and nested native/configuration requests, null/blank/unmapped pricing fallback, the full control-support × wire-dialect matrix, invalid/unsupported controls, legacy model strings, capability metadata, and AOT JSON registration.
- **Provider/engine:** `ReasoningChatOptionsAdapterTests`, `ModelCallExecutorTests`, `ProviderAttemptCommitTrackerTests`, and the reasoning cases in `WizardIntelligenceProvider*Tests` cover default no-op JSON, output as a typed best-effort hint without an invented provider field, all closed dialects, provider-ignored controls, buffered/streaming/interleaved/protected reasoning, fallback commitment, no-tools restart, same-provider tool continuation, guardrail buffering, context reservation boundaries, and strict replacement retries whose answer/reasoning runs retain response-content order through safety inspection and release.
- **Projections/usage:** `ReasoningProjectionEndpointTests`, `TurnEngineProjectionCharacterizationTests`, `OpenAiChatUsageJsonTests`, and OpenAI endpoint tests cover native buffered/NDJSON and OpenAI buffered/SSE fields, shared production-writer/`OpenAiSseProjection` reasoning and typed-error rules, real buffered and `stream:true` HTTP semantic validation, answer isolation, legacy result usage data, provider-total authority, native `cached_tokens`, missing/inconsistent usage, and `completion_tokens_details.reasoning_tokens`. `OpenAiV1ParityTests` exercises the authoritative production mapper directly for choice-only terminal chunks, `include_usage:false`, the separate choices-empty usage chunk for `include_usage:true`, and 40-character tool-argument fragmentation/reassembly; no exact parity with the semantic helper is assumed. A real Wizard → TurnEngine → native projection → Apprentice test guards final-answer handoff.
- **Accounting/persistence:** `CostCalculatorTests`, `BudgetReservationEstimateTests`, `TurnAccountingHandleTests`, `InferenceAccountingStoreTests`, `GrimoireSqlSchemaMigratorTests`, metrics tests, and audit tests cover ordinary/cached/reasoning subset pricing, explicit-zero versus nullable-rate fallback (including snapshots), reservation headroom, nested ambient-accounting restoration, reconciliation, raw-SQL `CachedTokens`/`ReasoningTokens`, fresh schema install and idempotent script reapply, a guard that `BillableOperations` stays outside the compiled EF model, count-only metrics/audit, and absence of reasoning bodies.
- **Clients:** CLI API/rendering/command tests, Command Center reasoning tests, The Forge NDJSON/Tome/trace tests, Compendium field-notification tests, and `ApprenticeStreamFramePolicyTests` cover known/unknown/malformed frames, AOT discriminator preflight, one-byte fragmentation through multibyte UTF-8 content, ephemeral rendering, bounded streaming-buffer completion, no-op binding notification suppression, spinner/viewport/cancellation cleanup, trace redaction, and Master/Apprentice non-handoff. First-reasoning/token UI transitions use a finite response plus an observing channel writer, so assertions follow emitted updates instead of racing wall-clock timeouts.
- **Concurrency:** daemon overlap tests start one signal-gated execution, await its actual start, then invoke the competing run directly. This preserves atomic single-flight coverage without blocking ThreadPool workers on a `Barrier` or treating scheduler delay as a production failure.

Focused runs can combine class filters, for example:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --configuration Release --filter "FullyQualifiedName~ReasoningContractsJsonTests|FullyQualifiedName~ReasoningChatOptionsAdapterTests|FullyQualifiedName~ReasoningProjectionEndpointTests"
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj --configuration Release --filter "FullyQualifiedName~ArcanumApiClientNdjsonTests|FullyQualifiedName~TomeViewModelTests|FullyQualifiedName~InferenceTraceViewModelTests"
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --configuration Release --filter "FullyQualifiedName~SettingDescriptorParityTests|FullyQualifiedName~GenericSettingsPreservationTests"
```

**Database safety:** SQLCipher/schema/accounting tests use `GrimoireFixture` scratch/template databases only; never point them at `~/.config/arcanum/arcanum.db`. Scratch contexts disable SQLite connection pooling and keep their single encrypted connection open for the context lifetime, preserving test speed while ensuring disposal releases the Windows file handle before cleanup. The application install script changed in place for `BillableOperations.ReasoningTokens`, so a developer database created by the older script must be handled outside tests: stop Arcanum, back up if needed, delete the local `arcanum.db` plus `-wal`/`-shm`, and restart to install a fresh database. Tests must not migrate, reset, or inspect a real Grimoire.

### CLI harness

`Cli/Infrastructure/CliApplicationFactory` builds a `CommandApp` with a test `ServiceCollection`. Use `Spectre.Console.Testing.TestConsole` for command output assertions.

### Code style

One blank line after each C# statement in test code (matches production style).

### Process and workspace boundaries

Windows process-boundary coverage lives in `ProcessResourceLimiterWindowsBehaviorTests`, `WindowsJobObjectSessionTests`, and `ChildProcessBoundaryBehaviorTests`: Job Object create/configure/assign errors use hand-written API fakes, stream setup/read failures use custom `Encoding`/`Decoder` implementations, filesystem cleanup uses uniquely owned temp paths, and process-tree termination uses immediate-exit or 30-second bounded children with prompt-termination assertions and unconditional cleanup. `SpellVersionPathPolicyTests` covers the complete label and sidecar-filename policy without touching the filesystem.

## Bug-squash coverage (changeset review)

The following test classes gained cases directly from the bug-squash plan. Each is named for the fix it locks down; see the plan in `docs/` for the per-bug rationale.

| Test class | Coverage added |
|------------|----------------|
| `BudgetMonitorTests` | Singleton captive-dependency fix (`IOptionsMonitor` + `IServiceScopeFactory`); alert-record-before-dispatch ordering; duplicate-alert suppression via `RecordAlertAsync` returning `false`. |
| `GuardrailsPipelineTests` / `GuardrailAuditLoggerTests` | Async audit-log observation (no fire-and-forget); multiple-violation auditing; phone regex balanced-parens enforcement; topic-regex cache bound. |
| `JsonSchemaHelperTests` / `StructuredOutputValidatorTests` | Nullable type-array validation; enum short-circuit now requires `type: "string"` or absent; numeric enum equality via `decimal`. |
| `ArcanumErrorMapperTests` | New codes (`Prompt.InvalidRequest`, `Session.InvalidStatus`, `Validation.InvalidQuery`, `Embeddings.ConfirmationRequired`, `StructuredOutput.*`); `ResolveStatusCodeDefaultBadRequest` preserves all explicit 500 mappings. |
| `GrimoireRepositoryTests` | `GetTodaySpendAsync` sargable half-open range + decimal sum in C#; `DeleteEntryAsync` decrements `UnsummarizedEntryCount`; `IncrementSessionTokensAndCostAsync` clamps negatives. |
| `EmbeddingsResetScopeTests` / `EmbeddingsResetServiceTests` | `ParseScope` rejects typos (no silent `All` escalation); `?confirm=true` gates with `Embeddings.ConfirmationRequired`. |
| `ArcanumBrowseWebToolTests` | `MaxLinks` clamp; SSRF error surfacing; response charset; timeout via `OperationCanceledException` inner `TimeoutException`; nav/header/footer link filter. |
| `RequestAugmentingHandlerTests` | Replaced `HttpContent` disposed; retry re-applies content headers; non-object JSON guarded. |
| `ClientToolForwardingTests` | Duplicate tool names; `tool_choice.function.name` verified against supplied tools; `tool_choice: "auto"`/`"none"` accepted when forwarding disabled; per-tool `strict` forwarded. |
| `OpenAiV1EndpointTests` / `OpenAiV1BatchesEndpointTests` | Structured-output failure maps to `validation_failed`/`invalid_schema` (not generic `inference_failed`); batch reset cleans orphan output/error files. |
| `SessionEndpointTests` | SSE `since` 404 returned with clean headers (no SSE headers leaked); `ErrorCodes.Session.EntryNotFound`/`InvalidStatus` constants used. |
| `CostCalculatorTests` | Cached prompt tokens are clamped to the prompt subset and priced separately at `CachedPer1M` (default zero; configured nonzero rates are charged); potential/actual savings use the nonnegative input-minus-cached rate delta. |
| `PromptCachingChatOptionsAdapterTests` / `PromptCachePlannerTests` | Golden buffered/streaming root fields (`prompt_cache_key`, exact `in_memory` / `24h` retention), reasoning-option composition, unchanged ineligible bodies, contiguous-prefix planning, deterministic tool digests, stable-key behavior, and plaintext exclusion. |
| `LexiconServiceTests` | The Lexicon raw-SQL store: create + case-insensitive upsert, append non-duplicate facts, type `General`/keep/refresh, fact-per-upsert cap, `delete` removes the FTS hit, exact-name match before column-weighted FTS (`bm25(lexicon_fts, 3.0, 2.0, 1.0)`), FTS-by-fact-text, empty-entity no-op, FTS special-char sanitization, update retires old fact / indexes new fact, `GetByNameAsync` missing → null. |
| `ArcanumInternalToolServerTests` (Lexicon) | `tools/list` advertises `scribe_lexicon`/`delete_lexicon` when enabled and omits them (and all lore tools) when disabled; `scribe_lexicon` creates via `ILexiconService`; `delete_lexicon` removes; disabled gate returns a tool error. |
| `SemanticRouterTests` | Router returns `SemanticSpellRoutingResult(Spell, Entities)`; entities survive `NONE`; missing entities → empty; fenced JSON; malformed JSON → null; cap/dedupe; `LexiconEntityExtractor` extracts from JSON, returns empty on invalid JSON / empty prompt (no LLM call). |
| `SystemPromptBuilderTests` (Lexicon) | `### Lexicon (Known Context)` injected inside DATA; omitted when no entries; newline/control-char hardening; `LexiconMaxInjectedBytes` truncation. |
| `SystemPromptBuilderUntrustedFenceTests` | Adaptive markdown fences for Codex/Spell/Additional Instructions/Chronosync/Campaign Summary/Attached Files; Data Streams fence payload + sanitize `StreamId` (no heading/newline breakout) + adaptive fence breakout prevention. |
| `UnseenServantDaemonJobTests` | `BuildDaemonStateName` deterministic/bounded; enabled → kickoff instructs `scribe_lexicon` and injects Previous State; disabled → no `scribe_lexicon` instruction; missing state does not fail kickoff. |

## Budget

Full-suite duration is host-dependent; use the serial Windows verification command above when validating shared process state. Largest costs are the one-time Grimoire template build and ApiHost WAF boot.

## Exclusions

Types marked `[ExcludeFromCodeCoverage] // Reason: ...` are documented in source. JSON source-gen contexts are excluded via `coverage.runsettings` only. See `DESIGN.md` §13 for the policy summary.
