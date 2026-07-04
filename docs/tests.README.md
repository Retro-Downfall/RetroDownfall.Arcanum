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
| Security-critical types | 100% branch (`ApiKeyEndpointFilter`, `ApiKeyDigestCache`, `DataProtectionSecretStore`, `GrimoireKeyDerivation`, `McpSecurityLimits`, `SandboxedFileIo`, `SanctumGuard`, `WorkspacePathPolicy`, `OutboundUrlGuard`, `WardGate`) |

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

## Budget

Full `dotnet test` should complete in **under 60 seconds** locally. Largest costs: one-time Grimoire template build and ApiHost WAF boot.

## Exclusions

Types marked `[ExcludeFromCodeCoverage] // Reason: ...` are documented in source. JSON source-gen contexts are excluded via `coverage.runsettings` only. See `DESIGN.md` §13 for the policy summary.
