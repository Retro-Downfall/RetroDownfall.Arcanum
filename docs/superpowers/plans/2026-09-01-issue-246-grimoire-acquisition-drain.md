# Issue #246 Grimoire Acquisition and Physical-Drain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every serving EF and raw live-Grimoire acquisition admission-safe and physically drainable, add capability-bound journal, legacy V3, and stopped-host acquisition routes, and prove the complete authored acquisition surface with an exact bidirectional Roslyn inventory.

**Architecture:** Keep the existing gate and EF interceptor as the process-local enforcement spine. Extract their physical-connection provenance into one singleton lifecycle shared by a new raw ordinary factory, make stage two own the singleton drain, and split closed/stopped-host access into three non-interchangeable authorities: journal-era gate/lane capabilities, temporary V3 capabilities minted from the exact #124 lease, and live maintenance-lock-derived stopped-host capabilities. A syntax-only Roslyn catalog then proves each authored provider construction/open and each marked indirect route in both directions.

**Tech Stack:** .NET 10, C# 13, EF Core SQLite/SQLCipher, Microsoft.Data.Sqlite, Microsoft.CodeAnalysis.CSharp 5.9.0 as a private test-only dependency, xUnit, deterministic `TaskCompletionSource` barriers, Git, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-01-issue-246-grimoire-acquisition-drain-design.md`

## Global Constraints

- Work on `codex/issue-246-grimoire-acquisition-drain`, based on `grimoire-fixes` commit `a1160e88fde6970d0940cb02872e1061158169f9`, and merge only into `grimoire-fixes`.
- Preserve the two unrelated untracked issue-221 duplicate documents exactly as found; never stage, edit, delete, or move them.
- Treat `CovenantConnectionEnrolmentInterceptor` as inherited partial baseline. Retain already-proven behavior; add a focused RED before every new production behavior.
- The process-wide instances of `IGrimoireConnectionAdmissionGate`, `ICovenantConnectionDrain`, and `IGrimoireOrdinaryConnectionLifecycle` are singletons in every serving composition.
- Bind production `ISqliteNativeRuntime` to the exact `SqliteNativeRuntime.Instance`, and call that dependency before every factory-owned provider construction or native open.
- A post-open refusal or failure closes the exact connection, clears its exact pool, observes physical closure, and only then terminalizes the open ticket.
- `CloseConnectionAdmissionAsync` is the sole new-transition stage-two drain path. The temporary V3 direct-drain call sites remain individually catalogued with removal owner #248.
- Journal-era maintenance supports only the injected canonical live path and fixed-purpose methods. Do not add a caller-supplied path, connection string, passphrase, mode, or purpose.
- Legacy V3 authority is one-shot, phase-specific, and mintable only from the exact live `ICovenantExclusiveOperationLease`; do not grant it to family reinitialize or any call chain that does not already hold that lease.
- Stopped-host authority is one-shot and mintable only from the exact live `ArcanumMaintenanceLock` after `AssertHeldFor(ArcanumPaths.GrimoireDirectory)` succeeds.
- Keep public `IGrimoireCliInitialization`, `IInstallationResetService`, HTTP contracts, CLI verbs, configuration, schema, and migrations unchanged.
- Fresh Global/All installation reset uses the local stopped-host plan and under-lock data path. It may use the existing explicit quit/host-absence probe, but it performs no plan/validator/bind/handoff/factory-reset host API or HTTP call.
- Existing active installation-reset records and historical handoffs retain their current authenticated recovery semantics.
- The acquisition inventory is syntax-only. It uses no semantic compilation, reflection, broad directory/type exemptions, wildcards, line-number identities, or production runtime inspection.
- `NotGrimoire` and `NonGrimoireCandidate` apply to one exact fingerprint with one named negative proof, never to a receiver name, namespace, directory, or wildcard.
- Follow RED → GREEN → REFACTOR in each task. Ordering tests use barriers/manual time and no sleeps. SQLite pool tests remain in the existing serialized collection.
- Preserve Native-AOT compatibility, source-generated JSON rules, C# vertical-whitespace style, and zero Release build warnings/errors.
- Run only child-scoped focused tests during development. Coverage, the complete suites, Native AOT/IL, benchmark, native SQLCipher provenance, packaging, full-host, cross-platform, and parent-wide qualification remain assigned to #257.
- Do not edit, transition, reparent, close, or make a resolution claim for issue #242. Leave #239 open and in progress after #246 delivery.

---

### Task 1: Build the exact syntax inventory and capture its first RED

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj`
- Create: `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`

**Interfaces:**
- Consumes: `ProductionSourceInventory`, `NativeSqlCipherTestPaths.RepositoryRoot()`, authored `src/**/*.cs`, and the approved closed path/kind/proof sets from spec section 8.
- Produces: `GrimoireConnectionAcquisitionScanner.Discover`, `Validate`, and `ValidateMarkerCoverage`, plus a finite direct-acquisition production `Catalog()`. In-memory source fixtures exercise marker syntax here; Task 3 introduces the production attribute alongside the first real, repository-unique marked routes.

- [ ] **Step 1: Add the exact private Roslyn dependency and closed catalog model**

Add this package reference to the test project:

```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.9.0">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

Restore the changed test project once before the first RED command:

```bash
dotnet restore tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --disable-build-servers
```

In the test helper, define the closed model exactly as follows:

```csharp
internal enum GrimoirePathAuthority : byte
{

    LiveGrimoire = 1,

    StoppedHostGrimoire = 2,

    PreReadinessGrimoire = 3,

    ShutdownGrimoire = 4,

    ArchiveOrSnapshot = 5,

    RestoreOrCompactionStaging = 6,

    DesignTimeScratch = 7,

    NativeRuntimeValidation = 8,

    NotGrimoire = 9,

}

internal enum GrimoireAcquisitionKind : byte
{

    ServingEfOrdinary = 1,

    ServingRawOrdinary = 2,

    JournalMaintenance = 3,

    LegacyV3Maintenance = 4,

    BootstrapOrShutdown = 5,

    StoppedHostRecovery = 6,

    StagingOrArchive = 7,

    DesignTimeOrNativeValidation = 8,

    NonGrimoireCandidate = 9,

}

internal enum AcquisitionConstructKind : byte
{

    UseSqlite = 1,

    AddDbContext = 2,

    ProviderOpen = 3,

    ProviderObjectCreation = 4,

    MarkedRouteDeclaration = 5,

    MarkedRouteInvocation = 6,

}

internal readonly record struct AcquisitionIdentity(
    string RelativePath,
    string EnclosingType,
    string EnclosingMember,
    AcquisitionConstructKind ConstructKind,
    string CalleeOrConstructedType,
    int Arity,
    string Fingerprint);

internal sealed record ExactNonServingProof(
    ExactNonServingProofKind Kind,
    string EvidenceMember,
    int RemovalIssue = 0);

internal sealed record GrimoireAcquisitionCatalogEntry(
    AcquisitionIdentity Identity,
    GrimoirePathAuthority PathAuthority,
    GrimoireAcquisitionKind AcquisitionKind,
    GrimoireRuntimeAdmissionRoute RuntimeRoute,
    ExactNonServingProof? NonServingProof);
```

Define the remaining closed values exactly:

```csharp
internal enum GrimoireRuntimeAdmissionRoute : byte
{

    SharedEfInterceptor = 1,

    OrdinaryConnectionFactory = 2,

    MaintenanceConnectionFactory = 3,

    StoppedHostConnectionFactory = 4,

    ExactNonServingProof = 5,

}

internal enum ExactNonServingProofKind : byte
{

    StoppedHostAuthority = 1,

    PreReadinessHeldLock = 2,

    ShutdownHeldLock = 3,

    TypedStagingOrSnapshot = 4,

    DesignTimeScratch = 5,

    NativeRuntimeValidation = 6,

    NegativeNonDatabaseProof = 7,

    LegacyV3ExclusiveLease = 8,

}

internal enum InventoryFailureCode : byte
{

    UncataloguedDiscovery = 1,

    StaleCatalogEntry = 2,

    DuplicateCatalogEntry = 3,

    DuplicateDiscovery = 4,

    MissingRequiredRouteMarker = 5,

    DuplicateMarkedRouteName = 6,

    InvalidClassification = 7,

    MissingNonServingProof = 8,

}
```

- [ ] **Step 2: Write scanner mutation tests before scanner code**

Add facts named:

```csharp
[Fact]
public void Injected_unlisted_acquisition_fails_independently();

[Fact]
public void Stale_catalog_entry_fails_independently();

[Fact]
public void Misclassified_canonical_live_acquisition_fails_independently();

[Fact]
public void Nested_Task_ValueTask_and_Result_returns_require_a_marker();

[Fact]
public void Duplicate_marked_route_names_fail_independently();

[Fact]
public void Exact_non_database_candidate_requires_one_negative_proof();
```

Use in-memory sources. The first and third fixtures must contain these exact constructs so the test cannot pass through production-source mutation:

```csharp
AcquisitionSource unlisted = Source("""
    using Microsoft.Data.Sqlite;
    sealed class Fixture
    {
        void Open()
        {
            _ = new SqliteConnection("Data Source=fixture.db");
        }
    }
    """);

AcquisitionSource live = Source("""
    using Microsoft.Data.Sqlite;
    sealed class Fixture
    {
        void Open()
        {
            _ = new SqliteConnection(ArcanumPaths.GrimoireDatabaseFile);
        }
    }
    """);
```

The recursive-return fixture must include an unmarked `Task<Result<IGrimoireOrdinaryConnectionLease>>` and a marked `ValueTask<Result<IStoppedHostGrimoireConnectionLease>>`; assert exactly one `MissingRequiredRouteMarker` failure.

- [ ] **Step 3: Run the scanner tests and observe RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests"
```

Expected: build FAIL because `GrimoireConnectionAcquisitionScanner` and its closed model do not exist.

- [ ] **Step 4: Implement syntax-only discovery, normalization, and validation**

Parse each source with `CSharpSyntaxTree.ParseText` and `LanguageVersion.Preview`. Discover:

```csharp
private static readonly HashSet<string> ProviderOpenNames =
[
    "Open",
    "OpenAsync",
    "OpenConnection",
    "OpenConnectionAsync",
];

private static readonly HashSet<string> ConnectionOwningReturnNames =
[
    "DbConnection",
    "SqliteConnection",
    "IGrimoireOrdinaryConnectionLease",
    "IGrimoireMaintenanceConnectionLease",
    "ICovenantV3MaintenanceConnectionLease",
    "IStoppedHostGrimoireConnectionLease",
];

private static readonly HashSet<string> RecursiveReturnWrappers =
[
    "Task",
    "ValueTask",
    "Result",
];
```

Walk `InvocationExpressionSyntax` for `UseSqlite`, every terminal name beginning `AddDbContext`, and the four terminal open names. Walk `ObjectCreationExpressionSyntax` for terminal `DbConnection` or `SqliteConnection`. Walk marked concrete `MethodDeclarationSyntax` and `LocalFunctionStatementSyntax`, require repository-unique marked method names, and discover every same-name/same-arity invocation. Recursively unwrap nullable types plus the three approved generic wrappers when enforcing marker coverage. A method is concrete for this syntax-only rule only when it has a block body or expression body; declaration-only interface/abstract/partial contracts are not acquisition implementations and are excluded. A default interface method with a body remains concrete and must be marked when its return/open shape qualifies.

Build identity with repository-relative slash-normalized path, syntax-derived nested enclosing type, enclosing member plus parameter count, construct kind, normalized callee/type text, argument arity, and `string.Concat(node.DescendantTokens().Select(token => token.Text))`. Never use line numbers or a semantic model.

Validation must report independent failures for both set differences, both duplicate directions, missing/duplicate markers, a live serving route with non-live path authority, every non-live path without a proof, every `LegacyV3Maintenance` proof whose `RemovalIssue` is not `248`, and every exact negative without `NegativeNonDatabaseProof`.

Recognize the exact attribute terminal name `GrimoireConnectionAcquisitionRoute` in parsed source, but do not add an otherwise-inert production attribute or mark a generic existing helper in this foundation task. The in-memory fixtures declare the attribute text they need. Task 3 adds the production attribute with the first actual factory routes.

- [ ] **Step 5: Turn the current direct production surface RED, then catalog it exactly**

Add `Production_inventory_is_bijective`. `ProductionSources()` must enumerate authored production C# under `src`, exclude `bin` and `obj`, and use raw source text. Begin with `Catalog()` returning an empty array and run the Step 3 command. Keep `ValidateMarkerCoverage` exercised by the in-memory tests here. Task 3 starts production marker coverage with the first repository-unique factory routes; each later factory task extends it, and Task 11 adds the final whole-surface assertion.

Expected: test FAIL with one `UncataloguedDiscovery` per current authored direct acquisition. Preserve this output in the task review as the required inventory-first RED.

Create one explicit `GrimoireAcquisitionCatalogEntry` per reported identity. Classify the known positive migration set from spec section 5.2 as `LiveGrimoire` plus `ServingRawOrdinary`; classify serving EF options as `ServingEfOrdinary`; classify the existing V3 same-database opens/drains as `LegacyV3Maintenance` plus `LegacyV3ExclusiveLease` and removal issue `248`; classify installation-reset database opens as `StoppedHostRecovery`; and give each bootstrap, shutdown, staging/archive, design-time/native validation, or exact negative candidate its named proof member. Do not add an unclassified value or broad exemption. Do not mark or rename current generic route methods merely to make this foundation task green; their operation-specific replacements travel with the behavior tasks that define them.

At this task boundary the catalog states each call site's required destination; later tasks add runtime-route assertions and make production conform.

- [ ] **Step 6: Run GREEN and commit the inventory foundation**

Run the Step 3 command again.

Expected: all inventory parser, mutation, direct-production bijection, and classification tests PASS.

Commit only the three task files:

```bash
git add tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs
git commit -m "test: inventory Grimoire acquisitions exactly"
```

---

### Task 2: Extract the singleton ordinary lifecycle and audit EF composition

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionLifecycle.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryConnectionLifecycleTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantConnectionDrainTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs`

**Interfaces:**
- Consumes: `IGrimoireConnectionAdmissionGate.AcquireOrdinaryOpen`, `ICovenantConnectionDrain.Register`, `IGrimoireConnectionOpenTicket`, and the inherited interceptor tests.
- Produces: singleton `IGrimoireOrdinaryConnectionLifecycle`; reference-counted `IGrimoireOrdinaryConnectionRegistration`; and one shared provenance state used by every serving EF options path and Task 3's factory.

- [ ] **Step 1: Add lifecycle RED for current-generation borrow and reference counting**

Define tests around this exact contract:

```csharp
internal interface IGrimoireOrdinaryConnectionLifecycle
{

    IGrimoireOrdinaryConnectionRegistration BeginOpen(DbConnection connection);

    Result<IGrimoireOrdinaryConnectionRegistration> BorrowCurrentOpen(DbConnection connection);

}

internal interface IGrimoireOrdinaryConnectionRegistration : IDisposable
{

    DbConnection Connection { get; }

    long Generation { get; }

    Result RevalidateAfterNativeOpen();

    Result MarkOpened();

    void MarkFailed();

    void MarkRefusedAfterOpen();

}
```

Add facts proving: a closed connection begins one ticket; an already-open unproven connection cannot be borrowed; a proven current-generation open can be borrowed; stale-generation provenance cannot be borrowed; disposing one of two logical registrations does not unregister the drain; the last registration unregisters exactly once; and a refused registration cannot terminalize until the physical connection is closed.

- [ ] **Step 2: Add serving-composition RED**

Extend composition tests to resolve the host `AddDbContextPool`, CLI `AddDbContext`, and `ArcanumWebApplicationFactory` options and assert:

```csharp
Assert.Same(
    provider.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>(),
    GetLifecycle(GetOnlyInterceptor(options)));

Assert.Same(
    provider.GetRequiredService<ICovenantConnectionDrain>(),
    GetDrain(provider.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>()));
```

Also assert exactly one `CovenantConnectionEnrolmentInterceptor` in each serving options set and retain explicit non-serving proofs for `ArcanumDbContextFactory`, `OnConfiguring`, installation reset, and bootstrap.

- [ ] **Step 3: Run RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireOrdinaryConnectionLifecycleTests|FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests|FullyQualifiedName~CovenantConnectionDrainTests|FullyQualifiedName~GrimoireDbContextCompositionTests"
```

Expected: build FAIL because the lifecycle contracts and shared constructor parameter do not exist.

- [ ] **Step 4: Move weak provenance ownership out of the interceptor**

Move the interceptor's `ConditionalWeakTable<DbConnection, ConnectionLifecycleState>`, lock, open ticket, native-open/refusal flags, and drain enrollment into `GrimoireOrdinaryConnectionLifecycle`. Add `HolderCount` and keep exactly one drain registration per physical open. `BeginOpen` returns the first registration; `BorrowCurrentOpen` succeeds only when state is native-open, ticket-terminal, current-generation, and `ConnectionState.Open`; each returned registration increments `HolderCount`; the final registration disposal removes drain enrollment exactly once.

`MarkOpened` must register the SQLite handle before terminal `MarkOpened`, store the ticket generation as provenance, and roll back enrollment if the final gate check loses. `MarkFailed` is legal only before native-open revalidation. `MarkRefusedAfterOpen` requires physical `Closed` state and disposes the ticket after its terminal callback.

Make the interceptor a thin EF adapter that retains its registration in a weak table until EF close/dispose, initializes between revalidation and `MarkOpened`, and performs the inherited close-before-refusal order. Do not broaden synchronous blocking beyond the existing synchronous EF callback.

- [ ] **Step 5: Register and inject one singleton lifecycle**

Register:

```csharp
services.AddSingleton<IGrimoireOrdinaryConnectionLifecycle>(sp =>
    new GrimoireOrdinaryConnectionLifecycle(
        sp.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
        sp.GetRequiredService<ICovenantConnectionDrain>()));
```

Change `ArcanumDbContextOptionsConfigurator.Configure` to accept the lifecycle and initializer, construct the interceptor from those two services, and update the CLI, host pool, web-test replacement, and the direct interceptor construction in `CovenantConnectionDrainTests`. Retain `SqliteNativeRuntime.Instance.Initialize()` before provider composition in each existing serving path.

- [ ] **Step 6: Run GREEN and commit**

Run the Step 3 command.

Expected: all focused lifecycle/interceptor/composition tests PASS with the inherited race tests unchanged.

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionLifecycle.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryConnectionLifecycleTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantConnectionDrainTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs
git commit -m "refactor: share ordinary Grimoire connection lifecycles"
```

---

### Task 3: Add the ordinary raw factory protocol

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAcquisitionRouteAttribute.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryConnectionFactoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`

**Interfaces:**
- Consumes: Task 2's lifecycle/registration singleton, `IGrimoireDbPassphraseSource`, `ICovenantSqliteConnectionInitializer`, `ISqliteNativeRuntime` bound to `SqliteNativeRuntime.Instance`, and `ArcanumPaths.GrimoireDatabaseFile`.
- Produces: singleton `IGrimoireOrdinaryConnectionFactory`, `IGrimoireOrdinaryConnectionLease`, and closed `GrimoireOrdinaryFreshConnectionKind` used by every serving raw migration.

- [ ] **Step 1: Write factory contract and protocol RED tests**

Use this closed surface:

```csharp
internal interface IGrimoireOrdinaryConnectionFactory
{

    Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken);

    Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
        GrimoireOrdinaryFreshConnectionKind kind,
        CancellationToken cancellationToken);

}

internal interface IGrimoireOrdinaryConnectionLease : IDisposable, IAsyncDisposable
{

    SqliteConnection Connection { get; }

}

internal enum GrimoireOrdinaryFreshConnectionKind : byte
{

    ReadOnly = 1,

    ReadWrite = 2,

    IsolatedHeartbeat = 3,

}
```

Both disposal paths must preserve close-before-unregister without sync-over-async: `Dispose` uses the provider's synchronous `Close`/`Dispose` path before releasing the registration, while `DisposeAsync` uses `CloseAsync`/`DisposeAsync` before releasing it. A borrow lease performs no physical close in either path. Add RED facts for both disposal surfaces and idempotent cross-calls (`Dispose` followed by `DisposeAsync`, and the reverse).

Add the production marker at the same time as these first real acquisition routes:

```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class GrimoireConnectionAcquisitionRouteAttribute : Attribute
{
}
```

Apply it once to each concrete `GrimoireOrdinaryConnectionFactory` implementation route, `AcquireScopedAsync` and `OpenFreshAsync`; leave the declaration-only interface contracts unmarked. Extend the inventory RED to require both unique concrete declarations and all same-name/same-arity invocations. This resolves the approved spec's otherwise contradictory combination of “every owning production method” and “repository-unique marked method name”; Task 11 clarifies that “method” means a concrete acquisition implementation, not its declaration-only contract duplicate.

Add facts for: refusal before provider construction; fresh native-runtime failure with zero provider construction/open; closed-scoped native-runtime failure after canonical-target validation but before ticket acquisition with zero ticket/native open; closed scoped ticket-before-open; rejection before native open of a closed scoped connection whose normalized data source is not `ArcanumPaths.GrimoireDatabaseFile`; already-admitted current-generation borrow without a second open; rejection of an already-open unproven/stale scoped connection; generation-race close then exact `SqliteConnection.ClearPool(connection)` then terminal refusal; initializer failure and cancellation with the same order; successful enrollment before ticket terminality; fresh unpooled read-only/read-write/heartbeat connection strings; scoped-open lease close without context disposal; fresh lease physical dispose; and exact singleton lifecycle/drain composition.

Expose an internal test seam only for provider construction/open ordering:

```csharp
internal interface IGrimoireOrdinaryConnectionFactoryTestSeam
{

    void BeforeProviderConstruction();

    ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken);

    void AfterExactPoolClear(SqliteConnection connection);

}
```

Production registers a no-op singleton. Tests count calls and block with `TaskCompletionSource`, never sleeps.

- [ ] **Step 2: Run RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireOrdinaryConnectionFactoryTests|FullyQualifiedName~GrimoireDbContextCompositionTests"
```

Expected: build FAIL because the ordinary factory contracts do not exist.

- [ ] **Step 3: Implement the minimal factory**

For every factory-owned physical open, call the injected `ISqliteNativeRuntime.Initialize()` after validating the internal/canonical request but before provider construction, `BeginOpen`, ticket acquisition, or native open. Register its production identity as `SqliteNativeRuntime.Instance`; tests inject a refusing fake to prove zero provider construction/open for fresh opens and zero ticket/native open for closed scoped connections. Build fresh connection strings only inside the factory from the canonical path and passphrase; set `Pooling = false`, add `Mode = ReadOnly` plus `Cache = Private` for `ReadOnly`, and retain read-write for the heartbeat.

For a scoped connection, parse its existing `ConnectionString` with `SqliteConnectionStringBuilder`, normalize `DataSource` and `ArcanumPaths.GrimoireDatabaseFile` through `Path.GetFullPath`, and compare with `StringComparison.OrdinalIgnoreCase` on Windows and `StringComparison.Ordinal` elsewhere. Reject an empty, malformed, foreign, archive, side-file, or staging target before a ticket or native open. Then attempt `BorrowCurrentOpen`. If it succeeds, return a borrow lease that disposes only the extra registration. If the connection is open but cannot be borrowed, return `ErrorCodes.Covenant.Unavailable`. If it is closed, begin a registration before `OpenAsync` and use the full protocol.

Implement one shared post-open failure method with this fixed order:

```csharp
private static async Task CloseClearAndRefuseAsync(
    SqliteConnection connection,
    IGrimoireOrdinaryConnectionRegistration registration,
    IGrimoireOrdinaryConnectionFactoryTestSeam testSeam)
{

    await connection.CloseAsync().ConfigureAwait(false);

    SqliteConnection.ClearPool(connection);

    testSeam.AfterExactPoolClear(connection);

    if (connection.State != ConnectionState.Closed)
    {

        throw new InvalidOperationException(
            "A refused ordinary Grimoire open remained physically open.");

    }

    registration.MarkRefusedAfterOpen();

}
```

On pre-open failure call `MarkFailed`. On post-open initializer failure/cancellation call the close/clear/refuse helper. A successful lease owns the registration and closes only when it performed the physical open; a borrow lease releases only its extra registration.

- [ ] **Step 4: Register the singleton factory and prove identity**

Register the native runtime and factory after the singleton lifecycle:

```csharp
services.AddSingleton<ISqliteNativeRuntime>(SqliteNativeRuntime.Instance);

services.AddSingleton<IGrimoireOrdinaryConnectionFactory, GrimoireOrdinaryConnectionFactory>();
```

Assert the runtime is `SqliteNativeRuntime.Instance` and the factory's lifecycle and drain references are the same objects used by the EF interceptor and gate composition.

- [ ] **Step 5: Run GREEN and commit**

Run the Step 2 command.

Expected: all ordinary factory and composition tests PASS.

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAcquisitionRouteAttribute.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryConnectionFactoryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs
git commit -m "feat: admit ordinary raw Grimoire connections"
```

---

### Task 4: Make stage two own physical draining and harden lane disposition

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantConnectionDrainTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryConnectionFactoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs`

**Interfaces:**
- Consumes: singleton `ICovenantConnectionDrain`, ticket terminal callbacks, the gate's maintenance/adoption interlock, and existing closed-lease/lane contracts.
- Produces: the exact stage-two ordering `tickets terminal → registered handles closed → pools clear → lock-linearized closed lease`; disposition refusal while any lane is active or disposing.

- [ ] **Step 1: Add deterministic stage-two RED**

Add a recording drain fake whose `DrainAsync` blocks on a `TaskCompletionSource`. Add facts proving:

```csharp
[Fact]
public async Task Closed_lease_waits_for_ticket_terminalization_then_the_singleton_drain();

[Fact]
public async Task Drain_failure_issues_no_closed_lease_and_keeps_admission_closed();

[Fact]
public async Task Drain_cancellation_issues_no_closed_lease_and_keeps_admission_closed();

[Fact]
public async Task Adoption_interlock_cannot_enter_between_drain_and_closed_lease_reservation();

[Fact]
public async Task Ef_open_attempt_while_stage_two_drain_is_blocked_is_refused_before_native_open();

[Fact]
public async Task Raw_open_attempt_while_stage_two_drain_is_blocked_is_refused_before_native_open();
```

For the ordering fact: begin native open, start closure, assert drain not called; terminalize the ticket, assert drain called but close incomplete; release the drain, assert the closed lease issues. For the interlock fact, block immediately after drain and race `AcquireExpiredLeaseAdoptionInterlockAsync`; exactly the closed-lease reservation wins under the gate lock. For each late-open fact, wait until the recording drain has entered, invoke the EF interceptor or ordinary factory through a provider-open counting seam, assert `GrimoireMaintenanceUnavailableException` or `ErrorCodes.Covenant.Unavailable` and zero native opens, then release the drain and require the closed lease to issue.

- [ ] **Step 2: Add zero-handle live-lane RED**

Add:

```csharp
[Fact]
public async Task Closed_lease_disposition_refuses_an_active_zero_handle_lane();

[Fact]
public async Task Lane_disposal_waits_for_real_physical_close_before_disposition();
```

Acquire a lane without issuing a handle and assert `CompleteAsync(Reopen)` fails. In the second fact, report open started, begin lane disposal, assert it is incomplete, report physical closure, await lane disposal, then assert disposition succeeds.

- [ ] **Step 3: Run RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAdmissionGateTests|FullyQualifiedName~CovenantConnectionDrainTests|FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests|FullyQualifiedName~GrimoireOrdinaryConnectionFactoryTests|FullyQualifiedName~GrimoireDbContextCompositionTests"
```

Expected: stage-two ordering tests FAIL because the gate does not invoke the drain; zero-handle lane disposition FAILS because the current guard checks handles/authorities but not the live lane.

- [ ] **Step 4: Compose the drain and final reservation atomically**

Inject `ICovenantConnectionDrain` into the gate. After all open tickets explicitly terminalize, call the singleton drain once and require success. On failure or cancellation, keep the exact owner and generation closed and return no lease.

After drain success, enter the same `_sync` critical section that owns maintenance/adoption interlock acquisition; revalidate owner, generation, empty unresolved opens, successful stage-one state, no interlock, no lane, no permit/authority/handle, reserve the closed lease, and only then exit the lock. Do not create a second drain abstraction or call `ClearAllPools` from the gate.

Add lane state to `CompleteClosedLease`'s conflict predicate. A lane remains active through its disposing wait and becomes absent only after its final handle reports terminal physical state and `ReleaseMaintenanceIoLaneAsync` completes.

- [ ] **Step 5: Prove the existing drain order and singleton identity**

Retain and extend `CovenantConnectionDrainTests` to assert each enrolled handle's physical close observation precedes `SqliteConnection.ClearAllPools()`. Extend composition tests so gate, lifecycle/interceptor, and ordinary factory all reference the exact same drain singleton.

- [ ] **Step 6: Run GREEN and commit**

Run the Step 3 command.

Expected: all focused gate/drain/composition tests PASS.

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantConnectionDrainTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryConnectionFactoryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs
git commit -m "feat: compose the Grimoire physical drain"
```

---

### Task 5: Migrate scoped serving raw acquisitions

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Api/Health/GrimoireLivenessProbe.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Workspaces/WorkspaceDivinationEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCampaignScopeProbe.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantConnectionSource.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryScopedConsumerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SessionDivinationEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Api/WorkspaceDivinationEndpointTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Repositories/GrimoireRepositoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Weave/EmbeddingsResetServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/SagaStoreHarness.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantLabeledArtifactGuardTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`

**Interfaces:**
- Consumes: `IGrimoireOrdinaryConnectionFactory.AcquireScopedAsync`, `IGrimoireOrdinaryConnectionLease`, and existing scoped `ArcanumDbContext` lifetimes.
- Produces: no direct scoped `DbConnection.Open/OpenAsync` in serving production; every command holds the returned lease through its last database use.

- [ ] **Step 1: Add consumer-lifetime RED tests**

For each consumer, inject a recording ordinary factory and assert its lease is not disposed before the final command/reader completes. Use one blocked command seam for endpoint/probe providers and the existing repository seams for transaction code. Add a focused `CovenantConnectionSource` fact that calls `GetOpenCoreConnectionAsync`, disposes an independent borrow, and proves the source's retained lease remains live until source disposal.

Add this source-shape assertion to the inventory suite before migration:

```csharp
Assert.DoesNotContain(
    ProductionServingRawDiscoveries(),
    discovery => discovery.Identity.ConstructKind
        is AcquisitionConstructKind.ProviderOpen
        && ScopedMigrationMembers.Contains(
            (discovery.Identity.RelativePath, discovery.Identity.EnclosingMember)));
```

- [ ] **Step 2: Run the scoped slice RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireLivenessProbeTests|FullyQualifiedName~WizardIntelligenceProviderTests|FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~SessionDivinationEndpointTests|FullyQualifiedName~WorkspaceDivinationEndpointTests|FullyQualifiedName~CovenantCampaignScopeProbeTests|FullyQualifiedName~CovenantConnectionSourceTests|FullyQualifiedName~GrimoireRepositoryTests|FullyQualifiedName~EmbeddingsResetServiceTests|FullyQualifiedName~CovenantLabeledArtifactGuardTests|FullyQualifiedName~CovenantProtectedArtifactErasureContentTests"
```

Expected: source-shape and lifetime tests FAIL because the current methods open directly or discard independent admission lifetime.

- [ ] **Step 3: Replace each scoped open with an acquired lease**

At each method, borrow the context connection and retain the lease across all work:

```csharp
SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

Result<IGrimoireOrdinaryConnectionLease> acquired = await connections
    .AcquireScopedAsync(
        connection,
        CovenantSqliteConnectionMode.ReadWrite,
        cancellationToken)
    .ConfigureAwait(false);

if (acquired.IsFailure)
{

    return Result.Failure(acquired.Error);

}

await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

connection = lease.Connection;
```

Use `ReadOnly` only for methods that issue no mutation. Thread the factory through endpoint handler parameters and DI constructors. Change `MemoryEndpoints.OpenConnectionAsync` to return the lease and require both callers to `await using` it. In `CovenantConnectionSource`, retain the single returned lease in a field, return `lease.Connection` from the existing bare-connection contract, and release it through the lease's safe synchronous `Dispose` from the source's existing synchronous disposal; never block on `DisposeAsync`. Update every direct `CovenantConnectionSource` construction in `SagaStoreHarness`, `CovenantLabeledArtifactGuardTests`, and `CovenantProtectedArtifactErasureContentTests` to pass a recording/real ordinary factory. Thread the factory from both `GrimoireRepository` compositions into `SessionEntryPersistence` and the turn-commit partial.

- [ ] **Step 4: Refresh exact catalog identities and run GREEN**

Remove the superseded direct-open catalog entries and add marked ordinary-factory invocation entries for every migrated member. Run the Step 2 command.

Expected: all scoped consumer and inventory tests PASS.

- [ ] **Step 5: Commit the scoped migration**

Stage only the files changed by this task, including their focused tests and the exact catalog update:

```bash
git add src/RetroDownfall.Arcanum.Api/Health/GrimoireLivenessProbe.cs src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs src/RetroDownfall.Arcanum.Api/Workspaces/WorkspaceDivinationEndpoints.cs src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCampaignScopeProbe.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantConnectionSource.cs src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryScopedConsumerTests.cs tests/RetroDownfall.Arcanum.Tests/Api/MemoryEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/Tower/SessionDivinationEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Api/WorkspaceDivinationEndpointTests.cs tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.cs tests/RetroDownfall.Arcanum.Tests/Repositories/GrimoireRepositoryTests.cs tests/RetroDownfall.Arcanum.Tests/Weave/EmbeddingsResetServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/SagaStoreHarness.cs tests/RetroDownfall.Arcanum.Tests/Data/CovenantLabeledArtifactGuardTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantProtectedArtifactErasureContentTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs
git commit -m "refactor: route scoped Grimoire opens through admission"
```

Before committing, inspect `git diff --cached --name-only` and unstage any file outside this task, especially both issue-221 duplicates.

---

### Task 6: Migrate fresh, ambient-read, heartbeat, and long-lived ordinary acquisitions

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantHealthyCatalogErasureGuard.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryFreshConsumerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureWriterTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureInventorySourceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantHealthyCatalogErasureGuardTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionDefaultLogRootTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionWorkspaceResetTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionApplyBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/SagaMemoryMidUpgradeWriteTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/A2A/A2ASendingAccountingTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/A2A/A2ASendingLedgerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingGrimoireTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/SagaStoreHarness.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`

**Interfaces:**
- Consumes: `OpenFreshAsync(ReadOnly|ReadWrite|IsolatedHeartbeat)` and long-lived `IGrimoireOrdinaryConnectionLease` ownership.
- Produces: no ordinary use of the ambient maintenance factory; heartbeat isolation preserved; writer drain enrollment retained until writer close/disposal.

- [ ] **Step 1: Add fresh and long-lived RED tests**

Add facts proving: `RenewLeaseAsync` uses an unpooled isolated-heartbeat lease; each fresh receipt/probe read uses a read-only lease; inventory and healthy-catalog reads are ordinary; `CovenantDisclosureWriter` holds its ordinary lease while warm, releases it on close/disposal, and a drain/close race never unregisters before physical closure.

Add source-shape assertions that these exact members contain only marked ordinary-factory invocations and no direct provider construction/open or `ICovenantMaintenanceConnectionFactory` reference.

- [ ] **Step 2: Run RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~LongRunningOperationStoreTests|FullyQualifiedName~SessionEntryPersistenceTests|FullyQualifiedName~CovenantDisclosureWriterTests|FullyQualifiedName~CovenantErasureInventorySourceTests|FullyQualifiedName~CovenantHealthyCatalogErasureGuardTests|FullyQualifiedName~DataRetentionDefaultLogRootTests|FullyQualifiedName~DataRetentionServiceTests|FullyQualifiedName~CovenantRetentionTests|FullyQualifiedName~DataRetentionWorkspaceResetTests|FullyQualifiedName~DataRetentionApplyBoundaryTests|FullyQualifiedName~SagaMemoryMidUpgradeWriteTests|FullyQualifiedName~A2ASendingAccountingTests|FullyQualifiedName~A2ASendingLedgerTests|FullyQualifiedName~InstallationResetExistingGrimoireTests"
```

Expected: FAIL because current code constructs fresh connections directly or opens through the ambient maintenance factory.

- [ ] **Step 3: Route each fresh lifetime through the ordinary factory**

Make `IGrimoireOrdinaryConnectionFactory` a required `LongRunningOperationStore` constructor dependency; do not retain an optional/null/default constructor path that can bypass admission. Update every direct construction in the files listed above, including `SagaStoreHarness` and installation-reset fixtures, with a focused recording factory. Use `OpenFreshAsync(IsolatedHeartbeat)` in `RenewLeaseAsync`; use `ReadOnly` for the two `SessionEntryPersistence` probes and both pre-close Covenant readers. Remove caller-owned connection-string construction.

Replace the disclosure writer's connection field with a lease field:

```csharp
private IGrimoireOrdinaryConnectionLease? _connectionLease;

private SqliteConnection? CoreConnection => _connectionLease?.Connection;
```

Acquire `ReadWrite` in `OpenVerifiedAsync`, keep the lease through the writer lifecycle, and dispose the lease only after the writer has stopped using the connection. Do not separately enroll or unregister the same handle; the shared lifecycle owns reference counting.

- [ ] **Step 4: Remove ambient read dependencies, update catalog, and run GREEN**

Change DI constructors, fakes, and catalog identities. Run the Step 2 command.

Expected: all fresh/long-lived consumer and inventory tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantHealthyCatalogErasureGuard.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireOrdinaryFreshConsumerTests.cs tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDisclosureWriterTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureInventorySourceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantHealthyCatalogErasureGuardTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionDefaultLogRootTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Data/CovenantRetentionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionWorkspaceResetTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionApplyBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema/SagaMemoryMidUpgradeWriteTests.cs tests/RetroDownfall.Arcanum.Tests/A2A/A2ASendingAccountingTests.cs tests/RetroDownfall.Arcanum.Tests/A2A/A2ASendingLedgerTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingGrimoireTests.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/SagaStoreHarness.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs
git commit -m "refactor: admit fresh ordinary Grimoire opens"
```

---

### Task 7: Add the journal-era capability-bound maintenance factory

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireMaintenanceConnectionFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireMaintenanceConnectionFactoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`

**Interfaces:**
- Consumes: gate-minted `IGrimoireMaintenanceConnectionCapability`, exact `IGrimoireMaintenanceIoLane`, canonical path/passphrase, initializer, and tracked maintenance handle.
- Produces: `IGrimoireMaintenanceConnectionFactory.OpenJournalCanonicalErasureAsync` and a lease that owns physical closure reporting; no V3/staging methods.

- [ ] **Step 1: Add the narrow contract and mismatch RED matrix**

Define only:

```csharp
internal interface IGrimoireMaintenanceConnectionFactory
{

    Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCanonicalErasureAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken);

}

internal interface IGrimoireMaintenanceConnectionLease : IAsyncDisposable
{

    SqliteConnection Connection { get; }

}
```

Mark the single concrete `GrimoireMaintenanceConnectionFactory.OpenJournalCanonicalErasureAsync` implementation with `[GrimoireConnectionAcquisitionRoute]`, leave its declaration-only interface contract unmarked, and add the exact concrete declaration/invocation identities to the production marker catalog in this task.

Add theories for foreign owner, foreign generation, wrong lane instance, wrong canonical path, wrong mode, wrong purpose, reused capability, disposed capability, and adopter-owned interlock. Each must return failure with zero provider construction/open.

Add facts for native runtime failure before construction, construction failure before open-start with one `ReportNotOpened`, cancellation/failure after `ReportOpenStarted` with physical dispose before one `ReportPhysicallyClosed`, successful unpooled initialized open, lease disposal ordering, and lane disposal waiting for the lease.

- [ ] **Step 2: Run RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireMaintenanceConnectionFactoryTests|FullyQualifiedName~GrimoireConnectionAdmissionGateTests|FullyQualifiedName~GrimoireDbContextCompositionTests"
```

Expected: build FAIL because the journal maintenance factory and lease do not exist.

- [ ] **Step 3: Implement fixed canonical acquisition**

Before provider construction, consume the capability exactly as follows:

```csharp
Result<IGrimoireTrackedMaintenanceHandle> consumed = capability.Consume(
    lane.Owner,
    lane.Generation,
    ArcanumPaths.GrimoireDatabaseFile,
    CovenantMaintenanceConnectionMode.ReadWrite,
    CovenantMaintenanceConnectionPurpose.CanonicalErasure,
    lane);
```

On failure return `ErrorCodes.Covenant.Unavailable` without constructing a provider. On success call the injected production-identity `ISqliteNativeRuntime.Initialize()`, construct one `Pooling=false` canonical connection, call `ReportOpenStarted` immediately before `OpenAsync`, initialize `ExclusiveMaintenance`, and return the lease. Before open-start failure call `ReportNotOpened` once. After open-start failure/cancellation and successful lease disposal, close/dispose the connection before `ReportPhysicallyClosed` once.

Do not add `OpenAsync`, a path/mode/purpose parameter, read-only/staging/compaction/reopen methods, or a passphrase/connection-string property.

- [ ] **Step 4: Register without activating journal handlers**

Register the factory singleton. Add composition proof that it receives canonical path policy and the same gate capability family, but do not call it from current V3 runtime. #248 activates journal-driven handlers.

- [ ] **Step 5: Run GREEN and commit**

Run the Step 2 command.

Expected: all journal maintenance, gate, and composition tests PASS.

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireMaintenanceConnectionFactory.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireMaintenanceConnectionFactoryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireDbContextCompositionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs
git commit -m "feat: bind journal maintenance Grimoire opens"
```

---

### Task 8: Replace ambient V3 maintenance with exact #124 capabilities

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceCapability.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs`
- Retain temporarily: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMaintenanceConnectionFactory.cs`, isolated after this task to the stopped-host HostTools path and test scratch helpers that Task 9 migrates.
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureTransition.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantV3MaintenanceCapabilityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureTransactionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantLocalErasureStorageHealthTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureTransitionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureFreshProcessRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantResetCheckpointInitiatorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCovenantResetRecoveryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`

**Interfaces:**
- Consumes: exact live `ICovenantExclusiveOperationLease`, its `Snapshot.RecoveryOwner`, `RevalidateAsync`, and `ExecuteWhileHeld`; current V3 phase machine and storage algorithms.
- Produces: one-shot `CovenantV3MaintenanceCapability`, purpose-specific V3 factory methods and tracked leases, no V3 call through ambient `ICovenantMaintenanceConnectionFactory.Open*`, and explicit #248 removal entries. The ambient interface survives this intermediate commit only for the Task 9 stopped-host/test-scratch migration, so every commit remains buildable.

- [ ] **Step 1: Write capability provenance RED**

Use this exact closed purpose set and surface:

```csharp
internal enum CovenantV3MaintenancePurpose : byte
{

    CanonicalErasure = 1,

    WalTruncation = 2,

    CompactionVacuum = 3,

    CompactionExport = 4,

    CompactionExportVerification = 5,

    CompactionPostReplaceJournalRestore = 6,

    AcceleratorInitialization = 7,

    CandidateReopenVerification = 8,

}

internal sealed class CovenantV3MaintenanceCapability : IAsyncDisposable
{

    internal static ValueTask<Result<CovenantV3MaintenanceCapability>> MintAsync(
        ICovenantExclusiveOperationLease exactLease,
        CovenantV3MaintenancePurpose purpose,
        CancellationToken cancellationToken);

    internal CovenantExclusiveOperation Operation { get; }

    internal ValueTask<Result> ConsumeAsync(
        CovenantV3MaintenancePurpose expectedPurpose,
        CancellationToken cancellationToken);

}
```

Add RED facts for missing recovery owner, wrong operation, stale/revoked/disposed exact lease, purpose mismatch, duplicate consume, and unused capability disposal. `MintAsync` accepts only `CovenantReset` or `HealthyCatalogFactoryErasure`; private construction retains the exact lease; mint and consume both revalidate it; consume is atomic.

- [ ] **Step 2: Add narrow V3 adapter RED**

Define `ICovenantV3MaintenanceConnectionFactory` with globally unique fixed methods `OpenV3CanonicalErasureAsync`, `OpenV3WalTruncationAsync`, `OpenV3VacuumAsync`, `OpenV3ExportSourceAsync`, `OpenV3ExportVerificationAsync`, `OpenV3PostReplaceJournalRestoreAsync`, `OpenV3AcceleratorInitializationAsync`, and `OpenV3CandidateReopenVerificationAsync`, plus:

```csharp
Task<Result> AttachV3ExportStagingAsync(
    ICovenantV3MaintenanceConnectionLease exportLease,
    CancellationToken cancellationToken);
```

Mark each concrete `CovenantV3MaintenanceConnectionFactory` implementation route with `[GrimoireConnectionAcquisitionRoute]`, leave its declaration-only interface contracts unmarked, and extend production marker coverage with its exact same-name/same-arity invocations in this task.

Every open method takes only its matching capability and cancellation token and returns `Task<Result<ICovenantV3MaintenanceConnectionLease>>`. The lease exposes a borrowed `SqliteConnection` and owns physical disposal. `AttachV3ExportStagingAsync` accepts no path or alias and only accepts the exact export-source lease. Add tests for wrong purpose, reuse, native-init-before-provider, unpooled mode, fixed canonical/staging target derivation, attach rejection for another lease kind, and physical disposal.

- [ ] **Step 3: Run RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantV3MaintenanceCapabilityTests|FullyQualifiedName~CovenantCanonicalErasureTransactionTests|FullyQualifiedName~CovenantLocalErasureStorageHealthTests|FullyQualifiedName~CovenantErasureTransitionTests|FullyQualifiedName~CovenantErasureCoordinatorTests"
```

Expected: build FAIL because the capability and narrow V3 factory do not exist.

- [ ] **Step 4: Implement the capability and V3 factory**

`MintAsync` reads the exact lease recovery owner, validates the two allowed operations, calls `RevalidateAsync`, and constructs the capability only inside `exactLease.ExecuteWhileHeld`. `ConsumeAsync` first reserves one-shot state with `Interlocked.CompareExchange`, repeats `RevalidateAsync`, and runs its terminal purpose/lease check inside `exactLease.ExecuteWhileHeld`; a failed validation is terminal and cannot be replayed as success. Disposal atomically revokes an unused token.

Move the old connection-string, immutable read, keyed staging, and attach policies behind the narrow V3 factory. Each method calls the injected production-identity `ISqliteNativeRuntime.Initialize()` before provider construction, fixes purpose, mode, canonical/staging derivation, initializer mode, and `Pooling=false`. The compaction staging filename and attach alias remain factory-owned; no public/internal caller-supplied path, mode, purpose, passphrase, or connection string remains. Return a lease and remove bare-connection returns.

- [ ] **Step 5: Mint capabilities only inside entered coordinator phases**

Change `ICovenantErasureTransition` to this exact capability-bearing shape, and change the corresponding storage methods on `ICovenantLocalErasureStorageHealth` to match (the storage interface does not own `ApplyCanonicalErasureAsync`):

```csharp
Task<Result<Guid>> ApplyCanonicalErasureAsync(
    CovenantExclusiveOperation operation,
    CovenantV3MaintenanceCapability capability,
    CancellationToken cancellationToken);

Task<Result> TruncateWalAsync(
    CovenantV3MaintenanceCapability capability,
    CancellationToken cancellationToken);

Task<Result> CompactAsync(
    CovenantV3CompactionCapabilities capabilities,
    CancellationToken cancellationToken);

Task<Result> InitializeAcceleratorAsync(
    CovenantV3MaintenanceCapability capability,
    CancellationToken cancellationToken);

Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
    CovenantV3MaintenanceCapability capability,
    CancellationToken cancellationToken);
```

`CloseHandlesAsync` and `VerifySidecarAbsenceAsync` remain capability-free. Define `internal sealed class CovenantV3CompactionCapabilities` in `CovenantV3MaintenanceCapability.cs`, not as a private coordinator type, so the cross-file transition and storage contracts can carry vacuum, export, export-verification, and post-replace-restore capabilities. Its disposal must dispose every unused member.

Inside each `AdvanceAsync` callback, mint only the capability(s) for that entered phase from the exact live `lease`; then pass them through `CovenantErasureTransition` to the canonical/storage owner. Keep `PublishCommittedAsync` lease-only. Keep `CloseHandlesAsync` and `VerifySidecarAbsenceAsync` capability-free and individually catalog their current direct drains as `LegacyV3Maintenance`, `LegacyV3ExclusiveLease`, removal issue `248`.

Change canonical/storage owners to `await using` each V3 lease through its last command. For compaction, dispose unused export capabilities when vacuum proves sufficient. Do not grant this adapter to `CovenantFamilyReinitializeCoordinator`.

- [ ] **Step 6: Isolate the ambient factory and update all V3 fakes/catalog entries**

Migrate every #124 V3 caller away from `ICovenantMaintenanceConnectionFactory`, `DatabasePath`, generic `OpenAsync/OpenReadOnlyAsync/OpenSidecarFreeReadOnlyAsync/OpenSideFileAsync`, and caller-path `AttachSideFileAsync`. Update DI lifetimes and every transition fake signature, including `CovenantCanonicalErasureFixture`, fresh-process recovery, reset-checkpoint initiator, same-process, and coordinator/recovery fakes. Assert `ICovenantV3MaintenanceConnectionFactory` has exactly the approved methods and no generic opener/path property.

Do not delete the old ambient interface/file in this task: after the V3 migration, assert that its only remaining production consumer is `HostToolsMarkerPairResetDatabase` and its only remaining test consumers are the explicitly catalogued maintenance-factory/scratch helpers assigned to Task 9. This deliberate one-task bridge prevents Task 8 from breaking Task 9's not-yet-migrated stopped-host code.

Refresh inventory identities so every V3 acquisition and direct drain names its exact consuming member, exact path authority, #124 proof, and removal owner `248`.

- [ ] **Step 7: Run GREEN and commit**

Run the Step 3 command, followed by:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantErasureSameProcessTests|FullyQualifiedName~DataRetentionCovenantResetRecoveryTests|FullyQualifiedName~CovenantArchitectureBoundaryTests|FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests"
```

Expected: both focused groups PASS.

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceCapability.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantV3MaintenanceConnectionFactory.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureTransition.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantV3MaintenanceCapabilityTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureFixture.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantCanonicalErasureTransactionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantLocalErasureStorageHealthTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureTransitionTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureFreshProcessRecoveryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantResetCheckpointInitiatorTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCovenantResetRecoveryTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs
git commit -m "refactor: bind legacy Covenant maintenance authority"
```

---

### Task 9: Add exact stopped-host connection authority and migrate raw/EF openers

**Files:**
- Delete: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMaintenanceConnectionFactory.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionContracts.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireAuthorityIssuer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetHostProcessToolsPairReader.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabaseContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/StoppedHostGrimoireConnectionAuthorityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingGrimoireTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetHostProcessToolsPairReaderTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetDatabaseTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCleanupContinuationTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetCleanupAuthorityTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetCleanupTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetMarkerPairRoutingTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs`
- Delete: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMaintenanceConnectionFactoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantSchemaScratchDatabase.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/MaintenanceLockTypedCallSiteTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`

**Interfaces:**
- Consumes: exact live `ArcanumMaintenanceLock`, `AssertHeldFor`, canonical directory/path, existing installation-reset local EF operations, and marker-pair database session.
- Produces: one-shot `IStoppedHostGrimoireConnectionAuthority`, operation-specific issuer/factory methods, and `IStoppedHostGrimoireConnectionLease` retained through each session/open.

- [ ] **Step 1: Add wrong-authority and zero-open RED matrix**

Define opaque contracts in `StoppedHostGrimoireConnectionContracts.cs`:

```csharp
internal interface IStoppedHostGrimoireConnectionAuthority : IAsyncDisposable
{
}

internal interface IStoppedHostGrimoireConnectionLease : IAsyncDisposable
{

    SqliteConnection Connection { get; }

}
```

Give `IStoppedHostGrimoireAuthorityIssuer` these repository-unique methods: `IssueStoppedHostInstallationResetPlanReadAuthority`, `IssueStoppedHostInstallationResetWorkspaceResolutionAuthority`, `IssueStoppedHostInstallationResetIdentityReadAuthority`, `IssueStoppedHostInstallationResetHostToolsEvidenceReadAuthority`, `IssueStoppedHostInstallationResetApplyAuthority`, and `IssueStoppedHostMarkerPairResetAuthority`. Each has no caller-selected path/mode/purpose input and returns `Result<IStoppedHostGrimoireConnectionAuthority>`. Give `IStoppedHostGrimoireConnectionFactory` the matching `OpenStoppedHostInstallationResetPlanReadAsync`, `OpenStoppedHostInstallationResetWorkspaceResolutionAsync`, `OpenStoppedHostInstallationResetIdentityReadAsync`, `OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync`, `OpenStoppedHostInstallationResetApplyAsync`, and `OpenStoppedHostMarkerPairResetAsync` methods. Each takes only its matching authority and a cancellation token and returns `Task<Result<IStoppedHostGrimoireConnectionLease>>`. Mark every concrete factory implementation route as an acquisition route and leave declaration-only interface contracts unmarked.

Use these exact signatures:

```csharp
internal interface IStoppedHostGrimoireAuthorityIssuer
{

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetPlanReadAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetWorkspaceResolutionAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetIdentityReadAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetHostToolsEvidenceReadAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetApplyAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostMarkerPairResetAuthority();

}

internal interface IStoppedHostGrimoireConnectionFactory
{

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetPlanReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetWorkspaceResolutionAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetIdentityReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetApplyAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostMarkerPairResetAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

}
```

Add, without changing any existing public port signature:

```csharp
internal interface IInstallationResetStoppedHostDataService
{

    Task<Result<DataRetentionPlan>> PlanUnderStoppedHostAuthorityAsync(
        InstallationResetDataPlanRequest request,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken);

    Task<Result<InstallationResetWorkspaceResolution>>
        ResolveWorkspaceUnderStoppedHostAuthorityAsync(
            string invocationDirectory,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken);

    Task<Result<Guid>> ReadIdentityUnderStoppedHostAuthorityAsync(
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken);

    Task<Result<HostProcessToolsDatabaseMarkerEvidence>>
        ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken);

    Task<Result<DataRetentionApplyResult>> ApplyUnderStoppedHostAuthorityAsync(
        DataRetentionApplyRequest request,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken);

}
```

`InstallationResetExistingGrimoire` implements this internal port in addition to its existing public interfaces. Do not add issuer parameters to `IInstallationResetDataService`, `IInstallationResetWorkspaceResolver`, `IInstallationResetDatabaseIdentityReader`, or `IInstallationResetHostProcessToolsDatabaseEvidenceReader`. Register the internal port separately. The authority-aware port is the only route used by Task 10's fresh planner/apply path; the existing public data-service branch remains only for authenticated active-record/handoff recovery and never becomes an authority substitute.

Add theories for wrong lock instance, wrong guarded root, wrong canonical path, wrong mode/purpose method, reused authority, disposed authority, disposed original lock followed by a replacement lock, native runtime failure, and cancellation. Every refusal must record zero provider construction/open.

- [ ] **Step 2: Run RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~StoppedHostGrimoireConnectionAuthorityTests|FullyQualifiedName~InstallationResetExistingGrimoireTests|FullyQualifiedName~InstallationResetHostProcessToolsPairReaderTests|FullyQualifiedName~InstallationResetServiceTests|FullyQualifiedName~HostToolsMarkerPairResetDatabaseTests|FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests|FullyQualifiedName~HostToolsMarkerPairResetCleanupContinuationTests|FullyQualifiedName~FullInstallationResetCleanupAuthorityTests|FullyQualifiedName~CampaignPathFullInstallationResetCleanupTests|FullyQualifiedName~InstallationResetMarkerPairRoutingTests|FullyQualifiedName~MaintenanceLockTypedCallSiteTests|FullyQualifiedName~CovenantArchitectureBoundaryTests"
```

Expected: build FAIL because stopped-host issuer/factory contracts do not exist.

- [ ] **Step 3: Implement exact live-lock issuance and factory consumption**

Construct the issuer only as:

```csharp
internal StoppedHostGrimoireAuthorityIssuer(
    ArcanumMaintenanceLock heldInstallationLock,
    string guardedGrimoireDirectory,
    string canonicalDatabasePath)
```

The constructor and every `Issue*` repeat `heldInstallationLock.AssertHeldFor(guardedGrimoireDirectory)`. Each private authority retains that exact lock reference, canonical path, read-only/read-write mode, installation-reset operation, fixed purpose, and an atomic consumed/disposed state. Factory consumption repeats the live-lock assertion immediately before provider construction and immediately before native open.

The factory calls the injected production-identity `ISqliteNativeRuntime.Initialize()` first, constructs one unpooled canonical connection, initializes the fixed mode, and returns a lease. Its lease physically closes/disposes before becoming terminal. It has no admission generation or maintenance lane because the retained OS lock is its mutually exclusive proof.

- [ ] **Step 4: Thread exact authority through both stopped-host acquisition families**

Implement the five uniquely named `IInstallationResetStoppedHostDataService` methods in `InstallationResetExistingGrimoire`. Each requires `IStoppedHostGrimoireAuthorityIssuer`, mints one fresh capability per EF open, and acquires the matching stopped-host lease before EF provider construction. Build its unpooled `ArcanumDbContext` options with `UseSqlite(lease.Connection, contextOwnsConnection: false)`, retain the lease until the context operation and context disposal complete, and never construct an arbitrary connection string in this class. Keep every existing public interface signature unchanged and make the issuer-free host-safe paths incapable of using this local provider-open implementation.

Split pair reads explicitly. Preserve `IInstallationResetHostProcessToolsPairReader.ReadAsync` as a host-safe route that cannot resolve or invoke the local stopped-host evidence opener. Its conservative local-production result is content-free `ExternalRemediationRequired` with zero provider open; a non-local injected host-safe evidence source may retain its existing behavior. Add `IInstallationResetStoppedHostProcessToolsPairReader.ReadUnderStoppedHostAuthorityAsync(IStoppedHostGrimoireAuthorityIssuer, CancellationToken)`; it reads the OS marker first, calls `ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync`, and then joins. Extend `InstallationResetHostProcessToolsPairReaderTests` to prove the host-safe local route performs zero local database acquisition, the authority-bearing route uses exactly one fresh evidence capability, malformed OS evidence fails before issuance/open, and wrong/disposed issuer performs zero provider construction.

In `InstallationResetService`, replace every lock-held active/handoff local read, not only the later fresh path. `ApplyFullUnderMaintenanceLockAsync` and `ApplyUnderMaintenanceLockAsync` derive one operation-scoped issuer from their exact `heldInstallationLock`; both use `ReadUnderStoppedHostAuthorityAsync` for pair evidence and `ReadIdentityUnderStoppedHostAuthorityAsync` for local database identity. Change `ReadCurrentTaintedMatchedPairAsync` to require that issuer and thread it from both lock-held callers. Preserve authenticated active-record/handoff state-machine decisions exactly; only their local acquisition route changes. Public `PlanAsync` remains host-safe: when its injected public data/pair source is the local `InstallationResetExistingGrimoire`, it returns the existing conservative inventory/external-remediation outcome without a provider open. Task 10's command stops using this public local-plan route and calls the authority-bearing planner instead.

Change marker database open to:

```csharp
Task<Result<HostToolsMarkerPairResetDatabaseSession>> OpenHostToolsMarkerPairResetDatabaseSessionAsync(
    IStoppedHostGrimoireConnectionAuthority authority,
    CancellationToken cancellationToken);
```

Make `HostToolsMarkerPairResetDatabaseSession` retain the stopped-host lease rather than a bare connection. At every current coordinator open site, derive an issuer from the already-held exact `ArcanumMaintenanceLock`, issue a fresh marker-pair reset authority, and pass it explicitly. Do not read a lock from `InstallationResetMaintenanceLockAccessor` or ambient state.

- [ ] **Step 5: Remove the final ambient factory, update typed inventory, run GREEN, and commit**

After `InstallationResetExistingGrimoire`, the authority-bearing pair reader, and `HostToolsMarkerPairResetDatabase` no longer use the ambient factory, delete `CovenantMaintenanceConnectionFactory.cs` and its old test suite. Migrate `CovenantSchemaScratchDatabase` to its explicit design-time scratch opener, update all remaining HostTools/architecture fakes, and assert no production or test reference to `ICovenantMaintenanceConnectionFactory` remains. Update the acquisition and maintenance-lock inventories with exact stopped-host entries. Run the Step 2 command.

Expected: all authority, existing-Grimoire, marker-pair, and typed-lock tests PASS.

```bash
git add -u -- src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMaintenanceConnectionFactory.cs tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantMaintenanceConnectionFactoryTests.cs
git add src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionContracts.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireAuthorityIssuer.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireConnectionFactory.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetHostProcessToolsPairReader.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabaseContracts.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetDatabase.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/StoppedHostGrimoireConnectionAuthorityTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetExistingGrimoireTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetHostProcessToolsPairReaderTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetDatabaseTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCoordinatorTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/HostToolsMarkerPairResetCleanupContinuationTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/FullInstallationResetCleanupAuthorityTests.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CampaignPathFullInstallationResetCleanupTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetMarkerPairRoutingTests.cs tests/RetroDownfall.Arcanum.Tests/Fixtures/CovenantSchemaScratchDatabase.cs tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantArchitectureBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Backup/MaintenanceLockTypedCallSiteTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs
git commit -m "feat: bind stopped-host Grimoire authority"
```

---

### Task 10: Route installation-reset planning and fresh Global/All apply through the stopped-host boundary

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireCliInitialization.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/IGrimoireCliStoppedHostInitialization.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostInstallationResetPlan.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationFactoryResetCommand.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/InstallationResetApplyBoundary.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireCliInitializationTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetCommandTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/InstallationResetApplyBoundaryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`

**Interfaces:**
- Consumes: Task 9 issuer, existing CLI maintenance/client-mutation locks, public `InstallationResetPlan`, local `DataRetentionPlan.Covenant`, `ApplyOfflineAsync`, and `ApplyUnderMaintenanceLockAsync`.
- Produces: internal stopped-host initialization/planner; authoritative local plan plus disclosure; fresh Global/All no-online path; exact under-lock replan before active publication/local apply.

- [ ] **Step 1: Add stopped-host CLI boundary RED**

Define:

```csharp
internal interface IGrimoireCliStoppedHostInitialization
{

    Task<T> RunAsync<T>(
        Func<IServiceProvider,
            IStoppedHostGrimoireAuthorityIssuer,
            CancellationToken,
            Task<T>> operation,
        CancellationToken cancellationToken);

}
```

Extend `GrimoireCliInitializationTests` to prove running-host lock contention never invokes the callback or provider seam; successful execution passes an issuer derived from the exact held lock; the client-mutation lease and maintenance lock remain held through the callback; and disposal makes later issuance fail. Keep public `IGrimoireCliInitialization` unchanged.

- [ ] **Step 2: Add locked-plan and no-online RED**

Define:

```csharp
internal sealed record StoppedHostInstallationResetPlan(
    InstallationResetPlan Plan,
    DataRetentionCovenantInventory? CovenantDisclosure);

internal interface IInstallationResetStoppedHostPlanner
{

    Task<Result<StoppedHostInstallationResetPlan>> PlanUnderStoppedHostLockAsync(
        InstallationResetPlanRequest request,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken);

}
```

Change the fresh boundary contract so the internal wrapper, not only its public plan, travels intact:

```csharp
Task<Result<InstallationResetResult>> ApplyFreshAsync(
    InstallationResetPlanRequest request,
    StoppedHostInstallationResetPlan confirmedPlan,
    CancellationToken cancellationToken);
```

Add this distinct locked-service method and leave the existing `ApplyUnderMaintenanceLockAsync` signature for active-record/handoff recovery unchanged:

```csharp
Task<Result<InstallationResetResult>> ApplyFreshUnderMaintenanceLockAsync(
    InstallationResetPlanRequest request,
    StoppedHostInstallationResetPlan confirmedPlan,
    ArcanumMaintenanceLock heldInstallationLock,
    CancellationToken cancellationToken = default);
```

Add command/service facts proving: local dry-run/plan/apply planning fails on lock contention before provider open; successful plan contacts no `IInstallationResetOnlinePlanValidator`, bind, handoff, plan/factory-reset API, or HTTP seam; the existing quit/host-absence attempt remains allowed; Global/All without local Covenant disclosure fails; Workspace permits a null disclosure; confirmation writes the exact local disclosure when present; changed under-lock replan or disclosure fails before active publication; marker-pair revalidation uses the reacquired exact lock; client coordination spans local apply; and active records/historical handoffs cannot enter or be reinterpreted by the fresh path.

- [ ] **Step 3: Run RED**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireCliInitializationTests|FullyQualifiedName~InstallationFactoryResetCommandTests|FullyQualifiedName~InstallationResetServiceTests|FullyQualifiedName~InstallationResetApplyBoundaryTests"
```

Expected: build/test FAIL because planning still enters public/online routes and the internal stopped-host interfaces do not exist.

- [ ] **Step 4: Implement stopped-host initialization and locked planning**

Refactor `GrimoireCliInitialization`'s private lock body so public exclusive methods retain their existing callback shape while `IGrimoireCliStoppedHostInitialization.RunAsync` constructs `StoppedHostGrimoireAuthorityIssuer` from the exact acquired lock and passes it only to its operation-scoped callback.

Implement `PlanUnderStoppedHostLockAsync` in `InstallationResetService` against `IInstallationResetStoppedHostDataService` and `IInstallationResetStoppedHostProcessToolsPairReader`. Thread the issuer through workspace resolution, local data planning, identity, and host-tools evidence reads, minting one capability per open. Return the public plan plus the exact local Covenant disclosure for Global/All and permit a null disclosure for Workspace. Public `PlanAsync` remains host-safe and cannot perform a local database read without an issuer.

- [ ] **Step 5: Replace fresh Global/All online planning and apply**

In `InstallationFactoryResetCommand`, run local planning inside the stopped-host boundary before dry-run output or confirmation. For a fresh Global/All plan, do not call the online validator or bind. Retain the entire `StoppedHostInstallationResetPlan` through confirmation and pass it to `ApplyFreshAsync`; persist the local disclosure at confirmation. Workspace retains the same wrapper with a permitted null disclosure.

In `InstallationResetApplyBoundary.ApplyFreshAsync`, for fresh Global/All skip pre-lock database pair read, `_createHostHandoff`, and online factory reset. Use the existing `_quitServer`/unreachable host-absence attempt, retain existing client coordination, reacquire the exact maintenance lock, and call `ApplyOfflineAsync` with `handoff: null` and the confirmed wrapper. The allowed quit/absence probe is not authority and cannot be used for plan, validation, bind, handoff, or factory-reset API work.

Inside the new `ApplyFreshUnderMaintenanceLockAsync`, derive an issuer from that exact lock, revalidate the marker pair through the authority-bearing pair reader, replan through the stopped-host planner, compare the complete confirmed plan ID and local disclosure, then publish the active record and call `IInstallationResetStoppedHostDataService.ApplyUnderStoppedHostAuthorityAsync`. Global/All requires exact disclosure equality; Workspace requires exact public-plan equality and permits both disclosures to be null. Do not call the issuer-free public `IInstallationResetDataService.ApplyAsync` from this fresh branch. Do not alter `ApplyUnderMaintenanceLockAsync` resume handling for an existing active record or authenticated handoff.

- [ ] **Step 6: Run GREEN and commit**

Run the Step 3 command, then the stopped-host focused command from Task 9 Step 2.

Expected: both groups PASS with zero online seam calls on the fresh stopped-host path.

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireCliInitialization.cs src/RetroDownfall.Arcanum.Infrastructure/Hosting/IGrimoireCliStoppedHostInitialization.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostInstallationResetPlan.cs src/RetroDownfall.Arcanum.Cli/Commands/InstallationFactoryResetCommand.cs src/RetroDownfall.Arcanum.Cli/Commands/InstallationResetApplyBoundary.cs src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireCliInitializationTests.cs tests/RetroDownfall.Arcanum.Tests/InstallationReset/InstallationResetServiceTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/InstallationFactoryResetCommandTests.cs tests/RetroDownfall.Arcanum.Tests/Cli/InstallationResetApplyBoundaryTests.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs
git commit -m "feat: plan installation reset under stopped-host authority"
```

---

### Task 11: Turn the bidirectional route proof green and document the delivered boundary

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs`
- Modify: `README.md`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`
- Modify: `docs/superpowers/specs/2026-09-01-issue-246-grimoire-acquisition-drain-design.md`

**Interfaces:**
- Consumes: all production routes from Tasks 2–10 and every exact non-serving proof.
- Produces: complete two-way source equality plus runtime-route proof; delivered #246 documentation with #247/#248/#257 boundaries unchanged.

- [ ] **Step 1: Add final route assertions and prove the bounded inventory GREEN**

Add `Connection_owning_return_routes_are_all_marked`, then make the production suite assert all eight directions from spec section 8. In addition to set equality, require:

```csharp
Assert.All(
    catalog.Where(entry => entry.AcquisitionKind
        is GrimoireAcquisitionKind.ServingEfOrdinary),
    entry => Assert.Equal(
        GrimoireRuntimeAdmissionRoute.SharedEfInterceptor,
        entry.RuntimeRoute));

Assert.All(
    catalog.Where(entry => entry.AcquisitionKind
        is GrimoireAcquisitionKind.ServingRawOrdinary),
    entry => Assert.Equal(
        GrimoireRuntimeAdmissionRoute.OrdinaryConnectionFactory,
        entry.RuntimeRoute));

Assert.All(
    catalog.Where(entry => entry.AcquisitionKind
        is GrimoireAcquisitionKind.JournalMaintenance),
    entry => Assert.Equal(
        GrimoireRuntimeAdmissionRoute.MaintenanceConnectionFactory,
        entry.RuntimeRoute));
```

Add equivalent exact assertions for stopped-host factory, V3 #124 proof/removal owner, pre-readiness/shutdown/staging/design-time/native proofs, and per-fingerprint negatives. Assert the catalog contains no broad prefix/wildcard identity and every marker declaration/invocation resolves exactly once.

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests"
```

Expected: PASS. Tasks 1–10 already enumerate, migrate, mark, and prove every direct or indirect acquisition. This task adds only the final whole-surface assertions; it is not a residual implementation bucket.

- [ ] **Step 2: Stop on any ownership mismatch**

If Step 1 fails, do not patch production opportunistically here. Identify which earlier task owns the stale/unlisted/misrouted acquisition, return to that task, add or retain its focused RED, fix it there, and rerun that task's GREEN before repeating Step 1. Never add an exemption to make this task pass.

- [ ] **Step 3: Update owning documentation**

Do not update the root README: it is the curated public GitHub front page, is not maintained with every implementation, and the user's active `main`-branch rewrite owns it. In `docs/Arcanum.DESIGN.md` section 10.20.3, document singleton lifecycle/ordinary factory admission, stage-two drain order, journal capability/lane ownership, temporary exact V3 boundary, and stopped-host reset planning/apply. Clarify in the child spec's inventory section that marker coverage applies to concrete method/local-function acquisition implementations with a body, while declaration-only interface/abstract/partial contracts are excluded; this is the syntax-only resolution of the approved text's duplicate-name contradiction and does not exempt any executable route.

Update the umbrella and child specs with the implemented architecture and the exact remaining #247/#248/#257 ownership, but keep #246 explicitly `in progress — final child qualification pending` in this task. Do not add delivered status or final build evidence yet; Task 12 owns that only after its branch review and Release gate pass. Do not claim parent qualification or edit #242.

- [ ] **Step 4: Run final focused child tests before the build gate**

Run each command once:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests|FullyQualifiedName~GrimoireConnectionAdmissionGateTests|FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests|FullyQualifiedName~GrimoireOrdinaryConnectionLifecycleTests|FullyQualifiedName~GrimoireOrdinaryConnectionFactoryTests|FullyQualifiedName~GrimoireMaintenanceConnectionFactoryTests|FullyQualifiedName~CovenantConnectionDrainTests|FullyQualifiedName~GrimoireDbContextCompositionTests"

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~CovenantV3MaintenanceCapabilityTests|FullyQualifiedName~CovenantCanonicalErasureTransactionTests|FullyQualifiedName~CovenantLocalErasureStorageHealthTests|FullyQualifiedName~CovenantErasureTransitionTests|FullyQualifiedName~CovenantErasureCoordinatorTests|FullyQualifiedName~CovenantErasureSameProcessTests|FullyQualifiedName~DataRetentionCovenantResetRecoveryTests"

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~InstallationFactoryResetCommandTests|FullyQualifiedName~InstallationResetApplyBoundaryTests|FullyQualifiedName~InstallationResetServiceTests|FullyQualifiedName~InstallationResetExistingGrimoireTests|FullyQualifiedName~InstallationResetHostProcessToolsPairReaderTests|FullyQualifiedName~GrimoireCliInitializationTests|FullyQualifiedName~HostToolsMarkerPairResetDatabaseTests|FullyQualifiedName~HostToolsMarkerPairResetCoordinatorTests|FullyQualifiedName~InstallationResetMarkerPairRoutingTests|FullyQualifiedName~MaintenanceLockTypedCallSiteTests|FullyQualifiedName~StoppedHostGrimoireConnectionAuthorityTests"
```

Expected: all three focused groups PASS.

- [ ] **Step 5: Commit inventory and docs**

```bash
git add tests/RetroDownfall.Arcanum.Tests/Support/GrimoireConnectionAcquisitionInventory.cs tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAcquisitionInventoryTests.cs README.md docs/Arcanum.DESIGN.md docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md docs/superpowers/specs/2026-09-01-issue-246-grimoire-acquisition-drain-design.md
git diff --cached --name-only
git commit -m "docs: describe issue 246 acquisition guarantees"
```

Inspect the staged list before committing and exclude unrelated files. There should be no production routing correction in this task; if one was necessary, it must already have been committed with its owning Task 1–10 behavior slice.

---

### Task 12: Review, qualify, integrate, push, and close issue #246

**Files:**
- Inspect: every file changed from `grimoire-fixes...codex/issue-246-grimoire-acquisition-drain`
- Modify after qualification: `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`
- Modify after qualification: `docs/superpowers/specs/2026-09-01-issue-246-grimoire-acquisition-drain-design.md`
- Modify only if review or verification finds a child-scoped defect.

**Interfaces:**
- Consumes: the complete reviewed feature branch and issue #246's live acceptance criteria/project item.
- Produces: one green child tree merged to `grimoire-fixes`, no implementation feature branches, pushed integration ref, #246 closed/Done, #239 still open/In progress, #242 unchanged.

- [ ] **Step 1: Run per-task and one bounded branch review**

Use `superpowers:requesting-code-review` after every implementation task and fix only evidenced issues with RED/GREEN proof. At the end, dispatch one read-only review against:

```bash
git diff --stat grimoire-fixes...codex/issue-246-grimoire-acquisition-drain
git diff --check grimoire-fixes...codex/issue-246-grimoire-acquisition-drain
```

Audit every acceptance criterion in issue #246 and every section of the approved child spec. Confirm no #247 launch binding, #248 handler activation/compaction reconciliation, #257 qualification, or #242 state change entered the branch.

Immediately before any GitHub write, refresh the three live issues and retain #242's no-touch baseline (`OPEN`, tracker `Ready`, `updatedAt` `2026-08-31T14:01:55Z` at plan-authoring time):

```bash
gh issue view 239 --json number,title,state,stateReason,updatedAt,projectItems,url
gh issue view 246 --json number,title,state,stateReason,updatedAt,projectItems,url
gh issue view 242 --json number,title,state,stateReason,updatedAt,projectItems,labels,assignees,milestone,url
```

If #242's live baseline has changed externally by execution time, record the newly observed value before our writes; do not modify it to match this planning snapshot.

- [ ] **Step 2: Commit any review-only fix**

If review changed code, run only the directly affected focused test, then commit:

```bash
git status --short
git add -p
git diff --cached --name-only
git commit -m "fix: address issue 246 review"
```

Stage only the reviewed hunks shown by `git add -p`; never use `git add -A`. If no review fix exists, do not create an empty commit.

- [ ] **Step 3: Run the one final child qualification set**

Do not repeat the three focused groups from Task 11 unless code changed after them. Run:

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --disable-build-servers -m:1
git diff --name-only --diff-filter=ACM -z grimoire-fixes...HEAD -- '*.cs' | xargs -0 ./scripts/align-csharp-blanklines.sh --check
git diff --check grimoire-fixes...HEAD
```

Expected: Release build exits 0 with zero warnings and zero errors; changed-file blank-line verification and `git diff --check` exit 0.

Do not run threshold coverage, the complete suites, Native AOT/IL, benchmark, native SQLCipher provenance, packaging, full-host, or cross-platform matrices; record them as intentionally deferred to #257.

- [ ] **Step 4: Record delivered status only after qualification**

After and only after Step 3 passes, change the umbrella #239 status table so #246 is delivered while #247, #248, and #257 retain their original pending ownership. Change the child spec status to delivered and record the exact focused-test groups, Release build result, blank-line check, and diff-check evidence. Do not claim parent qualification or edit #242.

Run and commit the documentation-only status transition:

```bash
git diff --check -- docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md docs/superpowers/specs/2026-09-01-issue-246-grimoire-acquisition-drain-design.md
git add docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md docs/superpowers/specs/2026-09-01-issue-246-grimoire-acquisition-drain-design.md
git diff --cached --name-only
git commit -m "docs: qualify issue 246 delivery"
```

Do not rerun the build or focused tests for this documentation-only evidence commit.

- [ ] **Step 5: Merge to the authorized integration branch**

Use `superpowers:finishing-a-development-branch`. Fetch the current remote, require `grimoire-fixes` to be at the expected reviewed base or reconcile only its authorized issue sequence, switch to `grimoire-fixes`, and fast-forward merge the feature branch:

```bash
git fetch origin grimoire-fixes
git rev-parse grimoire-fixes
git rev-parse origin/grimoire-fixes
git switch grimoire-fixes
git merge --ff-only origin/grimoire-fixes
git merge --ff-only codex/issue-246-grimoire-acquisition-drain
```

If the two pre-merge `rev-parse` outputs differ because the remote advanced, run `git rebase origin/grimoire-fixes` while still on the feature branch and rerun the directly affected review/verification before switching. Then fast-forward the local integration ref from the fetched remote as shown and merge the feature; never overwrite or force-push the integration branch.

Verify exact tree identity:

```bash
git rev-parse grimoire-fixes^{tree}
git rev-parse codex/issue-246-grimoire-acquisition-drain^{tree}
```

Expected: both tree IDs are identical. Do not rerun the already-green suite merely because the ref name changed.

- [ ] **Step 6: Push, clean feature branches, and reconcile GitHub**

Push only the integration branch:

```bash
git push origin grimoire-fixes
git rev-parse grimoire-fixes
git ls-remote --heads origin refs/heads/grimoire-fixes
```

Require the local SHA and the SHA in the `ls-remote` output to be identical before cleanup. Then delete every `codex/` feature branch created for #246 locally and remotely if it was ever pushed. No subagent branch is planned; the complete feature-branch manifest is `codex/issue-246-grimoire-acquisition-drain`. Check and remove its remote ref only when present, then remove the merged local ref:

```bash
if git ls-remote --exit-code --heads origin refs/heads/codex/issue-246-grimoire-acquisition-drain >/dev/null
then
    git push origin --delete codex/issue-246-grimoire-acquisition-drain
fi

git branch -d codex/issue-246-grimoire-acquisition-drain
git ls-remote --heads origin refs/heads/codex/issue-246-grimoire-acquisition-drain
git branch --list codex/issue-246-grimoire-acquisition-drain
```

Require both final branch-list commands to print no ref before claiming cleanup complete.

Close the issue with concrete evidence, then set its exact Arcanum Feature Tracker item to `Done`:

```bash
gh issue close 246 --comment "Delivered the approved Grimoire acquisition and physical-drain boundary on grimoire-fixes. The issue-scoped focused groups and Release build passed with zero warnings/errors; changed-file blank-line and diff checks also passed. Parent-wide coverage, Native AOT/IL, native SQLCipher, packaging, full-host, and cross-platform qualification remain assigned to #257."

gh project item-edit --id PVTI_lADOElfBBM4BeEbAzg4zKJA --project-id PVT_kwDOElfBBM4BeEbA --field-id PVTSSF_lADOElfBBM4BeEbAzhYhXCo --single-select-option-id 98236657
```

Finally re-read live state:

```bash
gh issue view 246 --json number,state,stateReason,updatedAt,projectItems,url
gh issue view 239 --json number,state,stateReason,updatedAt,projectItems,url
gh issue view 242 --json number,title,state,stateReason,updatedAt,projectItems,labels,assignees,milestone,url
```

Verify #246 is `CLOSED`/`Done`, #239 remains `OPEN`/`In progress`, and every captured #242 field including `updatedAt` remains unchanged across our write window. Report the pushed integration SHA and the intentionally deferred #257 gates.
