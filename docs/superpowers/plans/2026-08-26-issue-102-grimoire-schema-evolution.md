# Grimoire Schema Evolution and Resumable Backfills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task, and use superpowers:test-driven-development for every behavior change. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an already-installed Grimoire schema tier move from one integer version to the next through a code-owned, ordered chain of transition steps, with resumable bounded backfills that can never advance past uncommitted work, and typed fail-closed health for every condition the chain cannot honor.

**Architecture:** The head object tree stays the single source of truth for the head version. Each tier gains an ordered `GrimoireSchemaVersionChain` whose steps carry DDL statements loaded from a `Transitions/` subtree and an optional backfill. A pure planner decides fresh install, converge, evolve, resume, or refuse from the metadata row, the transition journal row, and the chain. The installer executes DDL steps at bootstrap; a journal-gated coordinator drains backfills in bounded batches after readiness, writing each cursor inside the batch's own transaction.

**Tech Stack:** .NET 10, C# 13, Native AOT, raw SQLite/SQLCipher through `Microsoft.Data.Sqlite`, embedded `.sql` resources, `Microsoft.Extensions.DependencyInjection`, `BackgroundService`, xUnit, FakeItEasy.

**Spec:** [`docs/superpowers/specs/2026-08-26-issue-102-grimoire-schema-evolution-design.md`](../specs/2026-08-26-issue-102-grimoire-schema-evolution-design.md)

## Global Constraints

- Work in the repository at `/Users/mat/Source/apps/RetroDownfall.Arcanum` on branch `codex/issue-102-schema-evolution`, cut from `long-term-memory`. Never commit to `main`.
- Raw SQL through the declarative tree only. No EF entity, no numbered migration, no compiled-model regeneration, no `Database.MigrateAsync`.
- Head objects stay one object per file, named after the object, every statement `CREATE ... IF NOT EXISTS`. Transition statements are one statement per file.
- Native AOT: no reflection-based `JsonSerializer`, no `AIFunctionFactory.Create`, no anonymous DTOs, no dynamic type loading.
- C# house style: file-scoped namespaces, positional records for DTOs, primary constructors for DI, one blank line after each line of code, no `[JsonPropertyName]` on `/api` wire types.
- Every new diagnostic value is closed and content-free. No SQL text, path, exception message, or secret-derived value reaches a health code or a journal column.
- No new public API route, no new CLI verb, no new configuration key. `docs/Arcanum.CommandMap.json` must not change.
- Every build must end with `0 Warning(s)` and `0 Error(s)`. Use `--no-incremental` for any build whose warning count is being claimed.
- No test seeds a `grimoire_feature_schemas` row, a `grimoire_schema_transitions` row, or a catalog state that the same test then asserts. Every test enters through `GrimoireSchemaInstaller.InstallAsync` or `GrimoireSchemaTransitionCoordinator.RunOnceAsync`. **One named exception:** the `MixedCatalogVersions` case in Task 9 detects a database an *external* actor changed, and no production path can produce it. That test therefore plays the external actor with direct SQL, and says so in a comment. It is the only test permitted to write schema metadata directly, and it asserts a refusal rather than the state it wrote.
- Do not create intermediate commits. One commit at the end, after every gate is green, then `--no-ff` merge into `long-term-memory`.
- Documentation travels with the code, in the same change set. No document outside `README.md` and `docs/Arcanum.OATH.md` may reference an issue in direct or inferred form. No document is hard-wrapped to a column width.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChain.cs` | `GrimoireSchemaTransitionStatement`, `GrimoireSchemaVersionStep`, `GrimoireSchemaVersionChain`, `GrimoireSchemaVersionChainSet` and their validation. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs` | The shipped chains, the three per-tier version constants, and the pinned source-fingerprint and backfill tables. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/IGrimoireSchemaBackfill.cs` | The backfill contract and `GrimoireSchemaBackfillBatch`. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTransitionJournal.cs` | Reading, inserting, revision-checked advancing, and deleting the journal row. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaEvolutionPlanner.cs` | The pure decision and `GrimoireSchemaEvolutionDecision`. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaBackfillRunner.cs` | Draining a pending backfill in bounded batches. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/grimoire_schema_transitions.sql` | The journal table. |
| `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionCoordinator.cs` | The connection, retry, one bounded pass, convergence re-entry, health republication. |
| `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs` | Interval scheduling of the coordinator. |
| `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs` | Chain and chain-set construction validation. |
| `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs` | Transition path parsing and fingerprint exclusion. |
| `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaEvolutionPlannerTests.cs` | Every arm of the decision table. |
| `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaEvolutionInstallerTests.cs` | Evolution through the real installer against a real database. |
| `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaBackfillTests.cs` | Bounded, checkpointed, restart-safe backfill behavior. |
| `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireSchemaEvolutionFixture.cs` | The synthetic two-version chain and its test backfill. |

**Modified:**

| File | Change |
|---|---|
| `.../Data/Schema/GrimoireSchemaCatalog.cs` | Transition resource loading; head-only source fingerprints. |
| `.../Data/Schema/GrimoireSchemaTierHealth.cs` | Three new health values. |
| `.../Data/Schema/GrimoireSchemaManifestBuilder.cs` | Replace `CovenantSchemaVersion` with three per-tier constants. |
| `.../Data/Schema/GrimoireSchemaTierOwnershipRegistry.cs` | `GrimoireSchemaManifests` reads the three constants; registry gains a chain-set factory. |
| `.../Data/Schema/GrimoireSchemaInstaller.cs` | Takes the chain set; planner-driven classification; step execution; run finalization. |
| `.../Core/Covenant/CovenantAvailabilitySnapshot.cs` | `CovenantHealthTransition.SchemaEvolution = 11`. |
| `.../DependencyInjection/ServiceCollectionExtensions.cs` | Register the chain set, the journal, the planner, the runner, the coordinator, the hosted service. |
| `tests/.../Fixtures/GrimoireSchemaTestInstaller.cs` | Compose the installer with an explicit chain set. |
| `docs/*`, `README.md` | The documentation sweep in Task 10. |

---

### Task 1: Load transition resources without disturbing any source fingerprint

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaCatalog.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaObject.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs`

**Interfaces:**

- Consumes: `GrimoireSchemaCatalog.ParseResourcePath`, `ParseCapability`, `ComputeSourceFingerprint` (all existing and private except `ParseResourcePath`).
- Produces: `GrimoireSchemaTransitionResourcePath`, `GrimoireSchemaCatalog.TryParseTransitionResourcePath(string, out GrimoireSchemaTransitionResourcePath?)`, `GrimoireSchemaCatalog.TransitionStatements` (`IReadOnlyList<GrimoireSchemaTransitionStatementResource>`), and `GrimoireSchemaCatalog.ComputeSourceFingerprint` widened to `internal`.

Resource names arrive dotted. `Transitions/V2/010_add_x.sql` in the Core tree becomes the relative path `Transitions.V2.010_add_x` — three segments, which collides with neither the two-segment core object shape nor the five-segment capability object shape. The capability form `Capabilities.Covenant.Canonical.Transitions.V2.010_add_x` is six segments.

- [ ] **Step 1: Write the failing test for the two path shapes**

Create `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs`:

```csharp
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Transition resources are decoded from the same embedded tree as head objects, and are excluded
/// from every source-definition fingerprint.
/// </summary>
public sealed class GrimoireSchemaTransitionResourceTests
{

    [Fact]
    public void TryParse_decodes_a_core_transition_path()
    {

        Assert.True(GrimoireSchemaCatalog.TryParseTransitionResourcePath(
            "Transitions.V2.010_add_entries_campaign_id",
            out GrimoireSchemaTransitionResourcePath? path));

        Assert.NotNull(path);

        Assert.Equal(GrimoireSchemaFamily.Core, path.Family);

        Assert.Equal(GrimoireSchemaTransactionTier.Core, path.TransactionTier);

        Assert.Equal(2, path.ToVersion);

        Assert.Equal(10, path.Ordinal);

        Assert.Equal("add_entries_campaign_id", path.Name);

    }

    [Fact]
    public void TryParse_decodes_a_capability_transition_path()
    {

        Assert.True(GrimoireSchemaCatalog.TryParseTransitionResourcePath(
            "Capabilities.Covenant.Canonical.Transitions.V3.020_widen_validity",
            out GrimoireSchemaTransitionResourcePath? path));

        Assert.NotNull(path);

        Assert.Equal(GrimoireSchemaFamily.Covenant, path.Family);

        Assert.Equal(GrimoireSchemaTransactionTier.CovenantCanonical, path.TransactionTier);

        Assert.Equal(3, path.ToVersion);

        Assert.Equal(20, path.Ordinal);

        Assert.Equal("widen_validity", path.Name);

    }

    [Fact]
    public void TryParse_rejects_an_object_path()
    {

        Assert.False(GrimoireSchemaCatalog.TryParseTransitionResourcePath(
            "Tables.grimoire_feature_schemas",
            out GrimoireSchemaTransitionResourcePath? path));

        Assert.Null(path);

    }

    [Theory]
    [InlineData("Transitions.V1.010_impossible")]
    [InlineData("Transitions.V0.010_impossible")]
    [InlineData("Transitions.Two.010_not_a_version")]
    [InlineData("Transitions.V2.add_without_ordinal")]
    [InlineData("Transitions.V2.010_")]
    public void TryParse_throws_on_a_malformed_transition_path(string relative)
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaCatalog.TryParseTransitionResourcePath(
                relative,
                out GrimoireSchemaTransitionResourcePath? _));

    }

}
```

- [ ] **Step 2: Run it and confirm it fails to compile**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaTransitionResourceTests"`
Expected: build failure, `TryParseTransitionResourcePath` and `GrimoireSchemaTransitionResourcePath` do not exist.

- [ ] **Step 3: Add the record and the parser**

Append to `GrimoireSchemaObject.cs`:

```csharp
/// <summary>
/// The family, tier, target version, ordinal, and step name decoded from one embedded transition
/// resource path.
/// </summary>
/// <remarks>
/// A transition resource has no <see cref="GrimoireSchemaCategory"/>: it is one statement in an
/// ordered step, not an object anything converges. Sharing
/// <see cref="GrimoireSchemaResourcePath"/> would let a step be mistaken for an object in the one
/// place that mistake cannot be recovered from — the startup-blocking Core install transaction.
/// </remarks>
internal sealed record GrimoireSchemaTransitionResourcePath(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int ToVersion,
    int Ordinal,
    string Name);
```

In `GrimoireSchemaCatalog.cs`, add the segment constant beside `CapabilitiesSegment`:

```csharp
    private const string TransitionsSegment = "Transitions";
```

and the parser:

```csharp
    /// <summary>
    /// Decodes a transition resource path, or reports that the path names an object instead.
    /// </summary>
    /// <remarks>
    /// Returning <see langword="false"/> means "this is not a transition", which is the ordinary
    /// answer for every object in the tree. A path that <i>is</i> under a <c>Transitions</c> folder
    /// and is malformed throws instead, because silently declining it would hand it to the object
    /// parser and produce a failure naming the wrong mistake.
    /// </remarks>
    internal static bool TryParseTransitionResourcePath(
        string relativePath,
        out GrimoireSchemaTransitionResourcePath? path)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string[] segments = relativePath.Split('.');

        if (segments.Length == 3 && string.Equals(segments[0], TransitionsSegment, StringComparison.Ordinal))
        {

            path = BuildTransitionPath(
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core,
                segments[1],
                segments[2],
                relativePath);

            return true;

        }

        if (segments.Length == 6
            && string.Equals(segments[0], CapabilitiesSegment, StringComparison.Ordinal)
            && string.Equals(segments[3], TransitionsSegment, StringComparison.Ordinal))
        {

            (GrimoireSchemaFamily family, GrimoireSchemaTransactionTier tier) =
                ParseCapability(segments[1], segments[2], relativePath);

            path = BuildTransitionPath(family, tier, segments[4], segments[5], relativePath);

            return true;

        }

        path = null;

        return false;

    }

    private static GrimoireSchemaTransitionResourcePath BuildTransitionPath(
        GrimoireSchemaFamily family,
        GrimoireSchemaTransactionTier tier,
        string versionFolder,
        string fileName,
        string relativePath)
    {

        if (versionFolder.Length < 2
            || versionFolder[0] != 'V'
            || !int.TryParse(
                versionFolder.AsSpan(1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int toVersion)
            || toVersion < 2)
        {

            throw new InvalidOperationException(
                $"Embedded Grimoire transition resource '{relativePath}.sql' is not in a 'V<n>' folder "
                + "with n of 2 or more; version 1 is installed by the head tree and is never a transition target.");

        }

        int underscore = fileName.IndexOf('_', StringComparison.Ordinal);

        if (underscore <= 0
            || underscore == fileName.Length - 1
            || !int.TryParse(
                fileName.AsSpan(0, underscore),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int ordinal))
        {

            throw new InvalidOperationException(
                $"Embedded Grimoire transition resource '{relativePath}.sql' is not named "
                + "'<ordinal>_<name>' with a numeric ordinal and a non-empty name.");

        }

        return new GrimoireSchemaTransitionResourcePath(
            family,
            tier,
            toVersion,
            ordinal,
            fileName[(underscore + 1)..]);

    }
```

- [ ] **Step 4: Run the test and confirm it passes**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaTransitionResourceTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing test for loading and fingerprint exclusion**

Append to the same test class:

```csharp
    [Fact]
    public void The_shipped_catalog_declares_no_transition_today()
    {

        Assert.Empty(GrimoireSchemaCatalog.TransitionStatements);

    }

    [Fact]
    public void No_head_object_is_loaded_from_a_transitions_folder()
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.AllObjects)
        {

            Assert.False(
                definition.ResourcePath.Contains(".Transitions.", StringComparison.Ordinal)
                    || definition.ResourcePath.StartsWith("Transitions.", StringComparison.Ordinal),
                $"{definition.ResourcePath} was loaded as a head object from a transitions folder");

        }

    }

    [Fact]
    public void A_transition_resource_is_outside_every_source_fingerprint()
    {

        // Every fingerprint is computed over head objects only, and no head object comes from a
        // transitions folder. Recomputing each one over the same input therefore has to reproduce
        // the published value, which is what a transition resource joining the input would break.
        Assert.Equal(
            GrimoireSchemaCatalog.CoreSchemaFingerprint,
            GrimoireSchemaCatalog.ComputeSourceFingerprint(GrimoireSchemaCatalog.CoreObjects));

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            GrimoireSchemaCatalog.ComputeSourceFingerprint(GrimoireSchemaCatalog.CovenantCanonicalObjects));

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint,
            GrimoireSchemaCatalog.ComputeSourceFingerprint(GrimoireSchemaCatalog.CovenantAcceleratorObjects));

    }
```

- [ ] **Step 6: Run it and confirm it fails**

Run the same filter. Expected: build failure on `TransitionStatements`, and an accessibility failure on `ComputeSourceFingerprint`.

- [ ] **Step 7: Load transitions separately and expose the fingerprint helper**

In `GrimoireSchemaCatalog.cs`, add the record for a loaded statement resource beside the others in `GrimoireSchemaObject.cs`:

```csharp
/// <summary>
/// One transition statement, loaded from exactly one embedded <c>.sql</c> file under a
/// <c>Transitions/V&lt;n&gt;/</c> folder.
/// </summary>
internal sealed record GrimoireSchemaTransitionStatementResource(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int ToVersion,
    int Ordinal,
    string Name,
    string ResourcePath,
    string Sql);
```

In `GrimoireSchemaCatalog`, add the lazy field beside `LoadedObjects`:

```csharp
    private static readonly Lazy<IReadOnlyList<GrimoireSchemaTransitionStatementResource>> LoadedTransitions =
        new(LoadOrderedTransitions, LazyThreadSafetyMode.ExecutionAndPublication);
```

and the member:

```csharp
    /// <summary>
    /// Every transition statement in the tree, ordered by tier, then target version, then ordinal.
    /// </summary>
    /// <remarks>
    /// Deliberately outside <see cref="AllObjects"/> and outside every source fingerprint. A
    /// transition resource that entered a tier's fingerprint would change the value recorded for the
    /// version it upgrades <i>from</i>, so authoring the very step that leaves version 1 would make
    /// every installation at version 1 refuse with <c>SourceDefinitionMismatch</c> before that step
    /// could run. The feature would break itself on first use.
    /// </remarks>
    public static IReadOnlyList<GrimoireSchemaTransitionStatementResource> TransitionStatements =>
        LoadedTransitions.Value;
```

Change `LoadOrderedObjects`'s loop body so a transition resource is skipped rather than parsed as an object. Immediately after the prefix and suffix check, insert:

```csharp
            string relative = resourceName[ResourcePrefix.Length..^ResourceSuffix.Length];

            if (TryParseTransitionResourcePath(relative, out GrimoireSchemaTransitionResourcePath? _))
            {

                continue;

            }
```

and change `ReadObject(assembly, resourceName)` to `ReadObject(assembly, resourceName, relative)`, with `ReadObject` taking the already-computed relative path instead of recomputing it.

Add the transition loader:

```csharp
    private static IReadOnlyList<GrimoireSchemaTransitionStatementResource> LoadOrderedTransitions()
    {

        Assembly assembly = typeof(GrimoireSchemaCatalog).Assembly;

        List<GrimoireSchemaTransitionStatementResource> statements = [];

        HashSet<(GrimoireSchemaTransactionTier Tier, int ToVersion, int Ordinal)> seen = [];

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {

            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {

                continue;

            }

            string relative = resourceName[ResourcePrefix.Length..^ResourceSuffix.Length];

            if (!TryParseTransitionResourcePath(relative, out GrimoireSchemaTransitionResourcePath? path))
            {

                continue;

            }

            if (!seen.Add((path!.TransactionTier, path.ToVersion, path.Ordinal)))
            {

                throw new InvalidOperationException(
                    $"Embedded Grimoire transition resource '{relative}.sql' duplicates ordinal "
                    + $"{path.Ordinal} already declared for the same tier and target version; two "
                    + "statements with one ordinal have no defined install order.");

            }

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded Grimoire transition resource not found: {resourceName}");

            using StreamReader reader = new(stream, Encoding.UTF8);

            statements.Add(
                new GrimoireSchemaTransitionStatementResource(
                    path.Family,
                    path.TransactionTier,
                    path.ToVersion,
                    path.Ordinal,
                    path.Name,
                    relative,
                    reader.ReadToEnd()));

        }

        return
        [
            .. statements
                .OrderBy(static statement => (int)statement.TransactionTier)
                .ThenBy(static statement => statement.ToVersion)
                .ThenBy(static statement => statement.Ordinal),
        ];

    }
```

Widen `ComputeSourceFingerprint` from `private` to `internal` so a test can prove the published values are computed over head objects alone. A test-only wrapper would be a second name for one algorithm; the test project already has `InternalsVisibleTo`.

- [ ] **Step 8: Run the test and confirm it passes**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaTransitionResourceTests"`
Expected: PASS, all seven tests.

- [ ] **Step 9: Confirm nothing else regressed**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Data.Schema"`
Expected: PASS.

---

### Task 2: The version chain, its validation, and the shipped chains

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChain.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/IGrimoireSchemaBackfill.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaManifestBuilder.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTierOwnershipRegistry.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs`

**Interfaces:**

- Consumes: `GrimoireSchemaCatalog.TransitionStatements`, `GrimoireSchemaManifests.ForTier`, `GrimoireSchemaCatalog.CoreObjects` and siblings.
- Produces:
  - `GrimoireSchemaTransitionStatement(string ResourcePath, int Ordinal, string Name, string Sql)`
  - `GrimoireSchemaVersionStep(GrimoireSchemaFamily Family, GrimoireSchemaTransactionTier TransactionTier, int FromVersion, int ToVersion, string FromSourceDefinitionFingerprint, IReadOnlyList<GrimoireSchemaTransitionStatement> Statements, IGrimoireSchemaBackfill? Backfill)`
  - `GrimoireSchemaVersionChain` with `HeadManifest`, `HeadObjects`, `Steps`, `HeadVersion`, `SourceDefinitionFingerprintFor(int)`, `TryGetStep(int, out GrimoireSchemaVersionStep)`
  - `GrimoireSchemaVersionChainSet.ForTier(GrimoireSchemaTransactionTier)`
  - `GrimoireSchemaVersionChains.Default`
  - `IGrimoireSchemaBackfill` and `GrimoireSchemaBackfillBatch`

- [ ] **Step 1: Write the failing chain-validation test**

Create `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs`:

```csharp
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// A chain is the closed, ordered statement of how a tier reaches its head version. Every way it
/// could be authored wrong is refused at construction, because a chain that installs is a chain the
/// installer trusts completely.
/// </summary>
public sealed class GrimoireSchemaVersionChainTests
{

    [Fact]
    public void A_single_version_chain_has_no_step()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default
            .ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.Equal(1, chain.HeadVersion);

        Assert.Empty(chain.Steps);

    }

    [Fact]
    public void Every_shipped_tier_is_at_version_one_with_no_step()
    {

        foreach (GrimoireSchemaTransactionTier tier in Enum.GetValues<GrimoireSchemaTransactionTier>())
        {

            GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default.ForTier(tier);

            Assert.Equal(1, chain.HeadVersion);

            Assert.Empty(chain.Steps);

        }

    }

    [Fact]
    public void The_head_fingerprint_answers_for_the_head_version()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default
            .ForTier(GrimoireSchemaTransactionTier.CovenantCanonical);

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            chain.SourceDefinitionFingerprintFor(1));

    }

    [Fact]
    public void A_two_version_chain_answers_for_both_versions()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaEvolutionFixture.TwoVersionChain();

        Assert.Equal(2, chain.HeadVersion);

        Assert.Equal(GrimoireSchemaEvolutionFixture.VersionOneFingerprint, chain.SourceDefinitionFingerprintFor(1));

        Assert.Equal(chain.HeadManifest.SourceDefinitionFingerprint, chain.SourceDefinitionFingerprintFor(2));

        Assert.True(chain.TryGetStep(1, out GrimoireSchemaVersionStep step));

        Assert.Equal(2, step.ToVersion);

    }

    [Fact]
    public void A_step_that_skips_a_version_is_refused()
    {

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 3,
                GrimoireSchemaEvolutionFixture.Step(fromVersion: 1, toVersion: 3)));

        Assert.Contains("consecutive", error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void A_gap_between_steps_is_refused()
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 4,
                GrimoireSchemaEvolutionFixture.Step(1, 2),
                GrimoireSchemaEvolutionFixture.Step(3, 4)));

    }

    [Fact]
    public void A_last_step_that_does_not_reach_head_is_refused()
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 3,
                GrimoireSchemaEvolutionFixture.Step(1, 2)));

    }

    [Fact]
    public void A_step_with_no_statement_is_refused()
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 2,
                GrimoireSchemaEvolutionFixture.Step(1, 2, statements: [])));

    }

    [Fact]
    public void Two_steps_naming_one_backfill_are_refused()
    {

        TestBackfill shared = new("shared");

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaEvolutionFixture.ChainWithSteps(
                headVersion: 3,
                GrimoireSchemaEvolutionFixture.Step(1, 2, backfill: shared),
                GrimoireSchemaEvolutionFixture.Step(2, 3, backfill: shared)));

    }

    [Fact]
    public void A_chain_set_missing_a_tier_is_refused()
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => new GrimoireSchemaVersionChainSet(
            [
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
            ]));

    }

}
```

- [ ] **Step 2: Run it and confirm it fails to compile**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaVersionChainTests"`
Expected: build failure — the chain types and the fixture do not exist.

- [ ] **Step 3: Write the backfill contract**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/IGrimoireSchemaBackfill.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// What one bounded backfill batch achieved.
/// </summary>
/// <remarks>
/// <paramref name="NextCursor"/> is opaque to everything except the backfill that produced it. It is
/// written into the transition journal inside the same transaction as the batch's data writes, so
/// there is no ordering between the work and the record of the work to get wrong.
/// </remarks>
internal sealed record GrimoireSchemaBackfillBatch(
    string? NextCursor,
    int RowsProcessed,
    bool IsComplete);

/// <summary>
/// One resumable data sweep a version step depends on before that version may be recorded as
/// installed.
/// </summary>
/// <remarks>
/// The contract below is not expressible in the signature and is therefore the implementer's
/// obligation:
///
/// <list type="bullet">
/// <item>Write only through the supplied transaction. Never commit, roll back, open a second
/// connection, or retry — the coordinator owns all four.</item>
/// <item>Process at most <see cref="MaxRowsPerBatch"/> rows. An unbounded batch is what turns a
/// resumable sweep back into a migration.</item>
/// <item>Be safe to re-run from the last committed cursor and produce the same durable effect. A
/// crash between a batch's work and its commit is indistinguishable from that batch never having
/// run, so the next pass will run it again.</item>
/// <item>Report <see cref="GrimoireSchemaBackfillBatch.IsComplete"/> only when the corpus is
/// drained. A complete batch may still report rows and a cursor; the cursor is discarded with the
/// journal row.</item>
/// </list>
/// </remarks>
internal interface IGrimoireSchemaBackfill
{

    /// <summary>Stable, 1 to 64 characters, recorded in the journal so a resumed run can prove the pending sweep is the one this binary declares.</summary>
    string Name { get; }

    int MaxRowsPerBatch { get; }

    Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken);

}
```

- [ ] **Step 4: Write the chain types**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChain.cs`:

```csharp
namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// One statement in one version step, loaded from exactly one embedded <c>.sql</c> file.
/// </summary>
/// <remarks>
/// Unlike a head object, a step statement need not be <c>CREATE ... IF NOT EXISTS</c>. A step's
/// statements commit in one transaction with the journal write that records the step, so a step
/// either fully applies or leaves nothing behind, and nothing re-runs a committed step.
/// <c>ALTER TABLE ... ADD COLUMN</c>, which has no idempotent form, is therefore legal here and
/// would not be legal in the head tree.
/// </remarks>
internal sealed record GrimoireSchemaTransitionStatement(
    string ResourcePath,
    int Ordinal,
    string Name,
    string Sql);

/// <summary>
/// One ordered move from an installed version to the next, and the sweep it depends on.
/// </summary>
/// <remarks>
/// <paramref name="FromSourceDefinitionFingerprint"/> is the value the tier's head tree produced
/// <i>at</i> <paramref name="FromVersion"/>. It is a pinned literal captured when the step was
/// authored, in the same spirit as the pinned FTS5 shadow DDL: the tree that produced it no longer
/// exists, so nothing can recompute it, and changing it is a reviewed change rather than an absorbed
/// one. It is what lets an installation at an older version be recognized as the older version this
/// binary knows, rather than as an unknown one.
/// </remarks>
internal sealed record GrimoireSchemaVersionStep(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int FromVersion,
    int ToVersion,
    string FromSourceDefinitionFingerprint,
    IReadOnlyList<GrimoireSchemaTransitionStatement> Statements,
    IGrimoireSchemaBackfill? Backfill);

/// <summary>
/// One tier's complete, ordered, closed statement of every version it has had and how to reach the
/// one this binary declares.
/// </summary>
/// <remarks>
/// The constructor is the validating boundary. Everything downstream — the planner, the installer,
/// the backfill runner — treats a constructed chain as trusted, so every authoring mistake has to be
/// refused here rather than discovered against a live database.
/// </remarks>
internal sealed class GrimoireSchemaVersionChain
{

    private readonly Dictionary<int, GrimoireSchemaVersionStep> _byFromVersion;

    internal GrimoireSchemaVersionChain(
        GrimoireSchemaManifest headManifest,
        IReadOnlyList<GrimoireSchemaObject> headObjects,
        IReadOnlyList<GrimoireSchemaVersionStep> steps)
    {

        ArgumentNullException.ThrowIfNull(headManifest);

        ArgumentNullException.ThrowIfNull(headObjects);

        ArgumentNullException.ThrowIfNull(steps);

        HeadManifest = headManifest;

        HeadObjects = headObjects;

        Steps = steps;

        if (steps.Count != headManifest.Version - 1)
        {

            throw new InvalidOperationException(
                $"The {headManifest.TransactionTier} schema chain declares {steps.Count} step(s) for head "
                + $"version {headManifest.Version}; a chain needs exactly one step per version above 1.");

        }

        _byFromVersion = new Dictionary<int, GrimoireSchemaVersionStep>(steps.Count);

        HashSet<string> backfillNames = new(StringComparer.Ordinal);

        int expectedFrom = 1;

        foreach (GrimoireSchemaVersionStep step in steps)
        {

            if (step.TransactionTier != headManifest.TransactionTier || step.Family != headManifest.Family)
            {

                throw new InvalidOperationException(
                    $"A schema step for {step.Family}/{step.TransactionTier} was declared on the "
                    + $"{headManifest.Family}/{headManifest.TransactionTier} chain.");

            }

            if (step.ToVersion != step.FromVersion + 1)
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema step {step.FromVersion} to {step.ToVersion} "
                    + "is not consecutive; a step that skipped a version would make the chain's order unverifiable.");

            }

            if (step.FromVersion != expectedFrom)
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema chain expected a step leaving version "
                    + $"{expectedFrom} and found one leaving version {step.FromVersion}.");

            }

            if (step.Statements.Count == 0)
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema step {step.FromVersion} to {step.ToVersion} "
                    + "declares no statement.");

            }

            if (step.FromSourceDefinitionFingerprint.Length != 64)
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema step {step.FromVersion} to {step.ToVersion} "
                    + "pins a source-definition fingerprint that is not 64 characters.");

            }

            if (step.Backfill is not null && !backfillNames.Add(step.Backfill.Name))
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema chain names backfill "
                    + $"'{step.Backfill.Name}' on more than one step; the journal identifies a pending "
                    + "sweep by name, so two steps sharing one name are indistinguishable after a restart.");

            }

            _byFromVersion[step.FromVersion] = step;

            expectedFrom = step.ToVersion;

        }

        if (steps.Count > 0 && expectedFrom != headManifest.Version)
        {

            throw new InvalidOperationException(
                $"The {headManifest.TransactionTier} schema chain's last step reaches version {expectedFrom}, "
                + $"not head version {headManifest.Version}.");

        }

    }

    internal GrimoireSchemaFamily Family => HeadManifest.Family;

    internal GrimoireSchemaTransactionTier TransactionTier => HeadManifest.TransactionTier;

    internal GrimoireSchemaManifest HeadManifest { get; }

    internal IReadOnlyList<GrimoireSchemaObject> HeadObjects { get; }

    internal IReadOnlyList<GrimoireSchemaVersionStep> Steps { get; }

    internal int HeadVersion => HeadManifest.Version;

    /// <summary>
    /// The source-definition fingerprint this binary expects to find recorded for an installation at
    /// <paramref name="version"/>, or <see langword="null"/> for a version the chain does not cover.
    /// </summary>
    internal string? SourceDefinitionFingerprintFor(int version) =>
        version == HeadVersion
            ? HeadManifest.SourceDefinitionFingerprint
            : _byFromVersion.TryGetValue(version, out GrimoireSchemaVersionStep? step)
                ? step.FromSourceDefinitionFingerprint
                : null;

    internal bool TryGetStep(int fromVersion, out GrimoireSchemaVersionStep step)
    {

        bool found = _byFromVersion.TryGetValue(fromVersion, out GrimoireSchemaVersionStep? resolved);

        step = resolved!;

        return found;

    }

}

/// <summary>
/// Exactly one chain per transaction tier.
/// </summary>
/// <remarks>
/// Injected rather than read statically, which is what makes multi-version behavior reachable from a
/// test through the production entry point: a suite installs one chain, then hands the same installer
/// a longer chain for the same tier. Nothing has to hand-seed a metadata row to describe an older
/// installation, so no test asserts a precondition it wrote itself.
/// </remarks>
internal sealed class GrimoireSchemaVersionChainSet
{

    private readonly Dictionary<GrimoireSchemaTransactionTier, GrimoireSchemaVersionChain> _chains;

    internal GrimoireSchemaVersionChainSet(IReadOnlyList<GrimoireSchemaVersionChain> chains)
    {

        ArgumentNullException.ThrowIfNull(chains);

        _chains = new Dictionary<GrimoireSchemaTransactionTier, GrimoireSchemaVersionChain>(chains.Count);

        foreach (GrimoireSchemaVersionChain chain in chains)
        {

            if (!_chains.TryAdd(chain.TransactionTier, chain))
            {

                throw new InvalidOperationException(
                    $"Two schema chains were declared for the {chain.TransactionTier} transaction tier.");

            }

        }

        foreach (GrimoireSchemaTransactionTier tier in Enum.GetValues<GrimoireSchemaTransactionTier>())
        {

            if (!_chains.ContainsKey(tier))
            {

                throw new InvalidOperationException(
                    $"No schema chain was declared for the {tier} transaction tier.");

            }

        }

    }

    internal GrimoireSchemaVersionChain ForTier(GrimoireSchemaTransactionTier tier) =>
        _chains.TryGetValue(tier, out GrimoireSchemaVersionChain? chain)
            ? chain
            : throw new ArgumentOutOfRangeException(nameof(tier));

}
```

- [ ] **Step 5: Write the shipped chains and split the version constant**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`:

```csharp
namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The three shipped version chains, built once from the catalog.
/// </summary>
/// <remarks>
/// Every tier is at version 1 and declares no step, so the pinned tables below are empty and the
/// transition subtree holds no file. That is the shipped state on purpose: the loader, the planner,
/// the installer's step arm, and the backfill driver all run in production and find nothing to do.
/// A sweep introduced alongside the rows it must drain is a sweep nobody has watched run empty.
/// </remarks>
internal static class GrimoireSchemaVersionChains
{

    /// <summary>The version of the durable schema this binary declares.</summary>
    internal const int CoreSchemaVersion = 1;

    internal const int CovenantCanonicalSchemaVersion = 1;

    internal const int CovenantAcceleratorSchemaVersion = 1;

    /// <summary>
    /// The source-definition fingerprint each tier's head tree produced at the version a step leaves,
    /// keyed by the tier and the version that step targets.
    /// </summary>
    /// <remarks>
    /// Empty because no tier has left version 1. A pin is authored in the same change as the
    /// transition it belongs to, by copying the tier's current published fingerprint before editing
    /// any object file; nothing can recompute it afterwards.
    /// </remarks>
    private static readonly IReadOnlyDictionary<(GrimoireSchemaTransactionTier Tier, int ToVersion), string> SourcePins =
        new Dictionary<(GrimoireSchemaTransactionTier, int), string>();

    /// <summary>The sweep each step depends on, keyed the same way. Empty for the same reason.</summary>
    private static readonly IReadOnlyDictionary<(GrimoireSchemaTransactionTier Tier, int ToVersion), IGrimoireSchemaBackfill> Backfills =
        new Dictionary<(GrimoireSchemaTransactionTier, int), IGrimoireSchemaBackfill>();

    private static readonly Lazy<GrimoireSchemaVersionChainSet> LoadedDefault =
        new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static GrimoireSchemaVersionChainSet Default => LoadedDefault.Value;

    private static GrimoireSchemaVersionChainSet Build() =>
        new(
        [
            BuildChain(GrimoireSchemaManifests.Core, GrimoireSchemaCatalog.CoreObjects),
            BuildChain(GrimoireSchemaManifests.CovenantCanonical, GrimoireSchemaCatalog.CovenantCanonicalObjects),
            BuildChain(GrimoireSchemaManifests.CovenantAccelerator, GrimoireSchemaCatalog.CovenantAcceleratorObjects),
        ]);

    private static GrimoireSchemaVersionChain BuildChain(
        GrimoireSchemaManifest headManifest,
        IReadOnlyList<GrimoireSchemaObject> headObjects)
    {

        List<GrimoireSchemaVersionStep> steps = [];

        for (int toVersion = 2; toVersion <= headManifest.Version; toVersion++)
        {

            List<GrimoireSchemaTransitionStatement> statements =
            [
                .. GrimoireSchemaCatalog.TransitionStatements
                    .Where(statement =>
                        statement.TransactionTier == headManifest.TransactionTier
                        && statement.ToVersion == toVersion)
                    .Select(static statement => new GrimoireSchemaTransitionStatement(
                        statement.ResourcePath,
                        statement.Ordinal,
                        statement.Name,
                        statement.Sql)),
            ];

            if (!SourcePins.TryGetValue((headManifest.TransactionTier, toVersion), out string? pin))
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema step to version {toVersion} has no pinned "
                    + "source-definition fingerprint for the version it leaves. Record the tier's published "
                    + "fingerprint before editing any object file; it cannot be recovered afterwards.");

            }

            _ = Backfills.TryGetValue((headManifest.TransactionTier, toVersion), out IGrimoireSchemaBackfill? backfill);

            steps.Add(
                new GrimoireSchemaVersionStep(
                    headManifest.Family,
                    headManifest.TransactionTier,
                    toVersion - 1,
                    toVersion,
                    pin,
                    statements,
                    backfill));

        }

        return new GrimoireSchemaVersionChain(headManifest, headObjects, steps);

    }

}
```

In `GrimoireSchemaManifestBuilder.cs`, delete `internal const int CovenantSchemaVersion = 1;`. In `GrimoireSchemaTierOwnershipRegistry.cs`, change each `GrimoireSchemaManifests` lazy to use the matching new constant: `GrimoireSchemaVersionChains.CoreSchemaVersion`, `...CovenantCanonicalSchemaVersion`, `...CovenantAcceleratorSchemaVersion`.

Add a chain-set-aware registry factory beside `CreateDefault`:

```csharp
    /// <summary>
    /// Builds the registry over the head manifests of an explicit chain set, so a suite driving a
    /// synthetic chain inspects against the objects that chain declares rather than the shipped ones.
    /// </summary>
    internal static GrimoireSchemaTierOwnershipRegistry ForChains(GrimoireSchemaVersionChainSet chains)
    {

        ArgumentNullException.ThrowIfNull(chains);

        return new GrimoireSchemaTierOwnershipRegistry(
        [
            .. Enum.GetValues<GrimoireSchemaTransactionTier>()
                .Select(tier => chains.ForTier(tier).HeadManifest),
        ]);

    }
```

- [ ] **Step 6: Write the evolution fixture**

Create `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireSchemaEvolutionFixture.cs`. It builds a synthetic chain whose head manifest is produced by the **production** `GrimoireSchemaManifestBuilder` over synthetic objects, so the manifest a test drives is shaped by production code rather than hand-written.

```csharp
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// A bounded test backfill: it copies <c>evolution_source.Value</c> into
/// <c>evolution_target.Value</c> in ordered batches, keyed by the source row id, which is exactly
/// the shape a real backfill has.
/// </summary>
internal sealed class TestBackfill(string name, int maxRowsPerBatch = 2) : IGrimoireSchemaBackfill
{

    private int _batchesRun;

    public string Name { get; } = name;

    public int MaxRowsPerBatch { get; } = maxRowsPerBatch;

    /// <summary>Set to a batch ordinal to make that batch throw, for the interruption tests.</summary>
    internal int? ThrowOnBatch { get; set; }

    internal int BatchesRun => _batchesRun;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {

        _batchesRun++;

        if (ThrowOnBatch == _batchesRun)
        {

            throw new InvalidOperationException("The test backfill was asked to fail this batch.");

        }

        long after = cursor is null ? 0 : long.Parse(cursor, CultureInfo.InvariantCulture);

        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = transaction;

        read.CommandText = """
            SELECT Id, Value FROM evolution_source WHERE Id > $after ORDER BY Id LIMIT $limit;
            """;

        _ = read.Parameters.AddWithValue("$after", after);

        _ = read.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

        List<(long Id, string Value)> rows = [];

        await using (SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add((reader.GetInt64(0), reader.GetString(1)));

            }

        }

        foreach ((long id, string value) in rows)
        {

            await using SqliteCommand write = connection.CreateCommand();

            write.Transaction = transaction;

            // Idempotent on purpose: a batch re-run from the last committed cursor must produce the
            // same durable effect, because a crash between the work and its commit is
            // indistinguishable from the batch never having run.
            write.CommandText = """
                INSERT INTO evolution_target (Id, Value) VALUES ($id, $value)
                ON CONFLICT (Id) DO UPDATE SET Value = excluded.Value;
                """;

            _ = write.Parameters.AddWithValue("$id", id);

            _ = write.Parameters.AddWithValue("$value", value);

            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        return rows.Count == 0
            ? new GrimoireSchemaBackfillBatch(cursor, 0, IsComplete: true)
            : new GrimoireSchemaBackfillBatch(
                rows[^1].Id.ToString(CultureInfo.InvariantCulture),
                rows.Count,
                IsComplete: rows.Count < MaxRowsPerBatch);

    }

}

internal static class GrimoireSchemaEvolutionFixture
{

    /// <summary>A pinned value standing in for what the version-1 tree produced; 64 characters, as a real pin is.</summary>
    internal const string VersionOneFingerprint =
        "1111111111111111111111111111111111111111111111111111111111111111";

    internal static GrimoireSchemaTransitionStatement Statement(
        int ordinal,
        string name,
        string sql) =>
        new($"Transitions.V2.{ordinal:D3}_{name}", ordinal, name, sql);

    internal static GrimoireSchemaVersionStep Step(
        int fromVersion,
        int toVersion,
        IReadOnlyList<GrimoireSchemaTransitionStatement>? statements = null,
        IGrimoireSchemaBackfill? backfill = null) =>
        new(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            fromVersion,
            toVersion,
            VersionOneFingerprint,
            statements ?? [Statement(10, "noop", "CREATE TABLE IF NOT EXISTS evolution_noop (Id INTEGER PRIMARY KEY);")],
            backfill);

    /// <summary>
    /// A chain for constructor-validation tests only. Its head manifest is the shipped Core one, so
    /// it must never be installed: its steps create objects that manifest does not declare.
    /// </summary>
    internal static GrimoireSchemaVersionChain ChainWithSteps(
        int headVersion,
        params GrimoireSchemaVersionStep[] steps) =>
        new(
            GrimoireSchemaManifestBuilder.Build(
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core,
                headVersion,
                GrimoireSchemaCatalog.CoreSchemaFingerprint,
                GrimoireSchemaCatalog.CoreObjects),
            GrimoireSchemaCatalog.CoreObjects,
            steps);

    internal static GrimoireSchemaVersionChain TwoVersionChain() =>
        ChainWithSteps(2, Step(1, 2));

}
```

**The installable chains are separate, and the distinction matters.** `ChainWithSteps` above is for constructor validation and must never reach a database: its head manifest is the shipped Core manifest, which does not declare the objects its steps create, so installing it would fail the finalization inspection with `UnexpectedObject`. The tempting wrong fix at that point is to weaken the inspector — the correct one is that an installable chain's head manifest **includes** every object its steps create.

Add a second family of fixture members for the installer and coordinator tests:

```csharp
    /// <summary>The two synthetic objects the evolution chains own, as head objects at their own version.</summary>
    private static GrimoireSchemaObject SyntheticObject(string name, string sql) =>
        new(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            GrimoireSchemaCategory.Tables,
            name,
            $"Tables.{name}",
            sql);

    private const string SourceTableSql =
        """CREATE TABLE IF NOT EXISTS evolution_source (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);""";

    private const string TargetTableSql =
        """CREATE TABLE IF NOT EXISTS evolution_target (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);""";

    /// <summary>
    /// Version 1 of the evolution tier: the shipped Core objects plus <c>evolution_source</c>. Its
    /// manifest is built through the production builder, so the shape a test drives is the shape
    /// production would produce.
    /// </summary>
    private static IReadOnlyList<GrimoireSchemaObject> VersionOneObjects =>
        [.. GrimoireSchemaCatalog.CoreObjects, SyntheticObject("evolution_source", SourceTableSql)];

    /// <summary>Version 2 adds <c>evolution_target</c>, which the step creates and the backfill fills.</summary>
    private static IReadOnlyList<GrimoireSchemaObject> VersionTwoObjects =>
        [.. VersionOneObjects, SyntheticObject("evolution_target", TargetTableSql)];

    private static GrimoireSchemaVersionChain CoreChain(
        int headVersion,
        IReadOnlyList<GrimoireSchemaObject> headObjects,
        params GrimoireSchemaVersionStep[] steps) =>
        new(
            GrimoireSchemaManifestBuilder.Build(
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core,
                headVersion,
                headVersion == 1
                    ? VersionOneFingerprint
                    : "3333333333333333333333333333333333333333333333333333333333333333",
                headObjects),
            headObjects,
            steps);

    private static GrimoireSchemaVersionChainSet ChainSet(GrimoireSchemaVersionChain core) =>
        new(
        [
            core,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

    /// <summary>An installable version-1 chain: no step, head is <c>evolution_source</c> plus Core.</summary>
    internal static GrimoireSchemaVersionChainSet OneVersionChainSet() =>
        ChainSet(CoreChain(1, VersionOneObjects));

    /// <summary>An installable version-2 chain whose one step creates <c>evolution_target</c>.</summary>
    internal static GrimoireSchemaVersionChainSet TwoVersionChainSet(IGrimoireSchemaBackfill? backfill = null) =>
        ChainSet(
            CoreChain(
                2,
                VersionTwoObjects,
                new GrimoireSchemaVersionStep(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    1,
                    2,
                    VersionOneFingerprint,
                    [Statement(10, "add_evolution_target", TargetTableSql)],
                    backfill)));
```

`VersionOneFingerprint` is therefore the version-1 chain's **own** head fingerprint as well as the version-2 chain's pin for version 1, which is exactly the relationship a real chain has: the pin is the value the older head published.

`GrimoireSchemaManifestBuilder.Build` is already `public` on an `internal` type, so the test project reaches it through the existing `InternalsVisibleTo`. Confirm that attribute exists before relying on it; every other schema suite already uses these types.

- [ ] **Step 7: Run the chain tests and confirm they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaVersionChainTests"`
Expected: PASS, all ten tests.

- [ ] **Step 8: Confirm the constant split broke nothing**

Run: `dotnet build RetroDownfall.Arcanum.slnx --no-incremental` and `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Data.Schema"`
Expected: `0 Warning(s)`, `0 Error(s)`, all schema tests PASS.

---

### Task 3: The transition journal table and its accessor

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/grimoire_schema_transitions.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTransitionJournal.cs`
- Test: extend `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaInstallerTests.cs`

**Interfaces:**

- Produces:
  - `GrimoireSchemaTransitionJournalRow(GrimoireSchemaFamily Family, GrimoireSchemaTransactionTier TransactionTier, int FromVersion, int TargetVersion, int CompletedThroughVersion, string TargetSourceDefinitionFingerprint, string? BackfillName, string? BackfillCursor, long BackfillRowsProcessed, long Revision)`
  - `GrimoireSchemaTransitionJournal.ReadAsync`, `.InsertAsync`, `.AdvanceAsync`, `.DeleteAsync`, `.RecordErrorAsync`

- [ ] **Step 1: Write the failing test that the table installs**

Add to `GrimoireSchemaInstallerTests`:

```csharp
    [Fact]
    public async Task InstallAsync_creates_the_transition_journal()
    {

        await using SqliteConnection connection = await InstallAsync();

        Assert.True(await TableExistsAsync(connection, "grimoire_schema_transitions"));

        Assert.True(await IndexExistsAsync(connection, "idx_grimoire_schema_transitions_target"));

    }
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaInstallerTests.InstallAsync_creates_the_transition_journal"`
Expected: FAIL — the table does not exist.

- [ ] **Step 3: Author the table**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/grimoire_schema_transitions.sql`:

```sql
-- The always-present core journal for one tier's in-flight version run. A run is in flight exactly
-- while its row exists: the row is written in the same transaction as the first step's DDL and
-- deleted in the same transaction that records the finished version, so no phase column is needed
-- and no other phase is observable.
--
-- The state of a run is (CompletedThroughVersion, BackfillName):
--   (c, NULL)  everything through version c is durably done; the DDL for c -> c+1 has not run.
--   (c, name)  the DDL for c -> c+1 committed and that step's sweep is draining at BackfillCursor.
CREATE TABLE IF NOT EXISTS grimoire_schema_transitions (
    FamilyCode INTEGER NOT NULL,
    TransactionTierCode INTEGER NOT NULL,
    -- The version recorded in grimoire_feature_schemas when the run began. A run whose FromVersion
    -- no longer matches that row describes a database somebody else has since changed.
    FromVersion INTEGER NOT NULL CHECK (FromVersion > 0),
    TargetVersion INTEGER NOT NULL CHECK (TargetVersion > 0),
    CompletedThroughVersion INTEGER NOT NULL CHECK (CompletedThroughVersion > 0),
    -- What head looked like when the run began. A binary swapped mid-run cannot finish a run some
    -- other head defined.
    TargetSourceDefinitionFingerprint TEXT NOT NULL
        CHECK (length(TargetSourceDefinitionFingerprint) = 64),
    BackfillName TEXT NULL CHECK (BackfillName IS NULL OR length(BackfillName) BETWEEN 1 AND 64),
    BackfillCursor TEXT NULL CHECK (BackfillCursor IS NULL OR length(BackfillCursor) BETWEEN 1 AND 256),
    BackfillRowsProcessed INTEGER NOT NULL CHECK (BackfillRowsProcessed >= 0),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    -- A bounded code, never an exception message: an unbounded error string in a core journal is
    -- both an unbounded row and a place for content to leak.
    LastDurableErrorCode TEXT NULL CHECK (
        LastDurableErrorCode IS NULL OR length(LastDurableErrorCode) BETWEEN 1 AND 64
    ),
    StartedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (FamilyCode, TransactionTierCode),
    CHECK (TargetVersion > FromVersion),
    -- A row saying the run is finished is a row that should not exist: finishing writes the metadata
    -- row and deletes this one in one transaction.
    CHECK (CompletedThroughVersion >= FromVersion AND CompletedThroughVersion < TargetVersion),
    CHECK (BackfillCursor IS NULL OR BackfillName IS NOT NULL)
);

-- Startup classification and every coordinator pass select by tier; the target lets a pass report
-- how far a run still has to go without reading the chain.
CREATE INDEX IF NOT EXISTS idx_grimoire_schema_transitions_target
    ON grimoire_schema_transitions (TargetVersion, CompletedThroughVersion);
```

- [ ] **Step 4: Run the test and confirm it passes**

Run the same filter. Expected: PASS.

- [ ] **Step 5: Write the journal accessor**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTransitionJournal.cs` with the row record and a static accessor exposing:

```csharp
    internal static Task<GrimoireSchemaTransitionJournalRow?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GrimoireSchemaTransactionTier tier,
        CancellationToken cancellationToken);

    internal static Task<IReadOnlyList<GrimoireSchemaTransitionJournalRow>> ReadAllAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken);

    internal static Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaTransitionJournalRow row,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Advances a row conditionally on the revision it was read at, and reports whether it won.
    /// </summary>
    /// <remarks>
    /// A host coordinator and a CLI bootstrap can both hold the encrypted file. Making every advance
    /// conditional means the loser's transaction fails and retries on its next pass, rather than two
    /// writers each moving a cursor past work only one of them did.
    /// </remarks>
    internal static Task<bool> AdvanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaTransitionJournalRow row,
        int completedThroughVersion,
        string? backfillName,
        string? backfillCursor,
        long backfillRowsProcessed,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    internal static Task<bool> DeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaTransitionJournalRow row,
        CancellationToken cancellationToken);

    internal static Task RecordErrorAsync(
        SqliteConnection connection,
        GrimoireSchemaTransactionTier tier,
        string errorCode,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
```

`AdvanceAsync` and `DeleteAsync` both carry `AND Revision = $revision` in their `WHERE` clause and return whether one row changed. `AdvanceAsync` sets `Revision = Revision + 1`. `ReadAsync` accepts a nullable transaction so classification can read inside the install transaction and the coordinator can read outside one. `RecordErrorAsync` bounds `errorCode` to 64 characters by construction — pass a closed constant, never an exception message.

- [ ] **Step 6: Write the failing round-trip test**

Add to `GrimoireSchemaInstallerTests` a test that installs, inserts a row through `InsertAsync` inside a transaction, reads it back through `ReadAsync`, advances it through `AdvanceAsync` with the read revision and confirms `true`, advances again with the **stale** revision and confirms `false`, then deletes it and confirms `ReadAsync` returns null. Every value written is distinct per column, so transposing two columns in the projection fails the test.

- [ ] **Step 7: Run it and confirm it passes**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaInstallerTests"`
Expected: PASS.

---

### Task 4: The new health values and the pure planner

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTierHealth.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaEvolutionPlanner.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaEvolutionPlannerTests.cs`

**Interfaces:**

- Consumes: `GrimoireSchemaVersionChain`, `GrimoireSchemaTransitionJournalRow`, `GrimoireSchemaTierHealth`.
- Produces:
  - `GrimoireSchemaEvolutionAction` — `FreshInstall`, `Converge`, `BeginRun`, `ResumeRun`, `Refuse`
  - `GrimoireSchemaRecordedTier(int SchemaVersion, string SourceDefinitionFingerprint)`
  - `GrimoireSchemaEvolutionDecision(GrimoireSchemaEvolutionAction Action, GrimoireSchemaTierHealth? Refusal, int ResumeFromVersion, string? PendingBackfillName)`
  - `GrimoireSchemaEvolutionPlanner.Decide(GrimoireSchemaVersionChain chain, GrimoireSchemaRecordedTier? recorded, bool anyOwnedObjectPresent, bool catalogValidatesAtHead, GrimoireSchemaTransitionJournalRow? journal)`

`catalogValidatesAtHead` is supplied by the caller and consulted **only** on the metadata-below-head, no-journal path, so the installer computes it lazily and the planner stays pure.

- [ ] **Step 1: Add the three health values**

In `GrimoireSchemaTierHealth.cs`, extend the enum:

```csharp
    /// <summary>
    /// A journaled version run this binary can finish has not finished. The tier is not healthy, so
    /// the capability is unavailable and a dependent tier reports
    /// <see cref="DependencyUnavailable"/> — but this is not a refusal. Core in particular must not
    /// throw here: a Core tier whose backfill aborted startup could never run that backfill, and the
    /// installation would be permanently unopenable by the only process able to repair it.
    /// </summary>
    TransitionIncomplete = 7,

    /// <summary>
    /// A journal row this binary cannot finish — a target above head, a head that has changed under
    /// the run, a from-version the metadata row no longer agrees with, or a pending sweep this
    /// binary does not declare. Fail-closed, and a refusal for Core.
    /// </summary>
    TransitionUnresumable = 8,

    /// <summary>
    /// The metadata row and the catalog disagree about version with no journal row to explain it —
    /// a catalog advanced without its metadata, which a restore or a hand edit can produce. Stamping
    /// the newer version would advance past work nothing proves was ever done.
    /// </summary>
    MixedCatalogVersions = 9,
```

- [ ] **Step 2: Write the failing planner test**

Create `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaEvolutionPlannerTests.cs` with one `[Fact]` per row of the spec's §9.2 decision table and one per §9.3 resumability check. Each resumability case is its own `[Fact]`, not a `[Theory]` row, because each must be independently mutation-checked. Representative first case:

```csharp
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The whole classification, decided without a database so every arm is directly reachable.
/// </summary>
public sealed class GrimoireSchemaEvolutionPlannerTests
{

    [Fact]
    public void An_empty_database_installs_fresh_at_head()
    {

        GrimoireSchemaEvolutionDecision decision = GrimoireSchemaEvolutionPlanner.Decide(
            GrimoireSchemaEvolutionFixture.TwoVersionChain(),
            recorded: null,
            anyOwnedObjectPresent: false,
            catalogValidatesAtHead: false,
            journal: null);

        Assert.Equal(GrimoireSchemaEvolutionAction.FreshInstall, decision.Action);

    }

    [Fact]
    public void Objects_without_metadata_refuse_as_metadata_missing()
    {

        GrimoireSchemaEvolutionDecision decision = GrimoireSchemaEvolutionPlanner.Decide(
            GrimoireSchemaEvolutionFixture.TwoVersionChain(),
            recorded: null,
            anyOwnedObjectPresent: true,
            catalogValidatesAtHead: false,
            journal: null);

        Assert.Equal(GrimoireSchemaTierHealth.MetadataMissing, decision.Refusal);

    }

    [Fact]
    public void An_older_version_with_the_pinned_fingerprint_begins_a_run()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaEvolutionFixture.TwoVersionChain();

        GrimoireSchemaEvolutionDecision decision = GrimoireSchemaEvolutionPlanner.Decide(
            chain,
            new GrimoireSchemaRecordedTier(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            anyOwnedObjectPresent: true,
            catalogValidatesAtHead: false,
            journal: null);

        Assert.Equal(GrimoireSchemaEvolutionAction.BeginRun, decision.Action);

        Assert.Equal(1, decision.ResumeFromVersion);

    }

    [Fact]
    public void An_older_version_whose_catalog_is_already_at_head_refuses_as_mixed()
    {

        GrimoireSchemaEvolutionDecision decision = GrimoireSchemaEvolutionPlanner.Decide(
            GrimoireSchemaEvolutionFixture.TwoVersionChain(),
            new GrimoireSchemaRecordedTier(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            anyOwnedObjectPresent: true,
            catalogValidatesAtHead: true,
            journal: null);

        Assert.Equal(GrimoireSchemaTierHealth.MixedCatalogVersions, decision.Refusal);

    }

    [Fact]
    public void An_older_version_whose_recorded_fingerprint_is_not_the_pin_refuses_as_source_mismatch()
    {

        GrimoireSchemaEvolutionDecision decision = GrimoireSchemaEvolutionPlanner.Decide(
            GrimoireSchemaEvolutionFixture.TwoVersionChain(),
            new GrimoireSchemaRecordedTier(
                1,
                "2222222222222222222222222222222222222222222222222222222222222222"),
            anyOwnedObjectPresent: true,
            catalogValidatesAtHead: false,
            journal: null);

        Assert.Equal(GrimoireSchemaTierHealth.SourceDefinitionMismatch, decision.Refusal);

    }

}
```

Add the remaining cases: version above head refuses `IncompatibleNewerVersion`; version equal to head with a matching fingerprint and no journal converges; version equal to head with a differing fingerprint refuses `SourceDefinitionMismatch`; a resumable journal resumes and reports its `ResumeFromVersion` and `PendingBackfillName`; and one case per resumability check — target above head, changed head fingerprint, from-version disagreeing with metadata, no step leaving `CompletedThroughVersion`, and a `BackfillName` the step does not declare — each asserting `TransitionUnresumable`.

- [ ] **Step 3: Run it and confirm it fails to compile**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaEvolutionPlannerTests"`
Expected: build failure.

- [ ] **Step 4: Write the planner**

Create `GrimoireSchemaEvolutionPlanner.cs` implementing exactly the §9.2 table in that order, with `Decide` performing no I/O. The journal arm runs **before** the version arms, because a journal row describes a state the metadata row alone cannot express.

- [ ] **Step 5: Run it and confirm it passes**

Run the same filter. Expected: PASS.

- [ ] **Step 6: Mutation-check the two arms most likely to be silently wrong**

Temporarily replace the `BeginRun` arm's result with `Converge` and rerun: the run-begins test must fail. Temporarily delete the `MixedCatalogVersions` arm and rerun: that test must fail. Restore both.

---

### Task 5: Drive evolution from the installer

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireSchemaTestInstaller.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaEvolutionInstallerTests.cs`

**Interfaces:**

- Consumes: `GrimoireSchemaEvolutionPlanner.Decide`, `GrimoireSchemaTransitionJournal.*`, `GrimoireSchemaVersionChainSet.ForTier`.
- Produces: `GrimoireSchemaInstaller(GrimoireSchemaManifestInspector, GrimoireSchemaDataInitializers, GrimoireSchemaVersionChainSet, TimeProvider, ILogger<GrimoireSchemaInstaller>?)` and an unchanged `InstallAsync`/`InstallCoreOnlyAsync` signature.

The chain set is a new constructor parameter placed **before** the optional logger. `TimeProvider` is added because the journal records timestamps and a fixed clock keeps two runs of a suite byte-identical.

- [ ] **Step 1: Write the failing end-to-end evolution test**

Create `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaEvolutionInstallerTests.cs`. Every test installs version 1 through the real `InstallAsync` with a one-version chain, then calls the real `InstallAsync` again with the two-version chain against the same file.

```csharp
    [Fact]
    public async Task A_backfill_free_step_evolves_the_tier_and_records_head()
    {

        using TempGrimoireFile file = TempGrimoireFile.Create();

        await using (SqliteConnection first = await GrimoireSchemaTestInstaller.OpenAsync(file.ConnectionString, TestContext.Current.CancellationToken))
        {

            _ = await GrimoireSchemaTestInstaller.InstallAsync(
                first,
                GrimoireSchemaEvolutionFixture.OneVersionChainSet(),
                TestContext.Current.CancellationToken);

        }

        await using SqliteConnection second = await GrimoireSchemaTestInstaller.OpenAsync(file.ConnectionString, TestContext.Current.CancellationToken);

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            second,
            GrimoireSchemaEvolutionFixture.TwoVersionChainSet(),
            TestContext.Current.CancellationToken);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(2, result.Core.SchemaVersion);

        Assert.True(await TableExistsAsync(second, "evolution_target"));

        Assert.Null(await ReadJournalAsync(second, GrimoireSchemaTransactionTier.Core));

    }
```

The fixture's two-version chain declares a Core step whose statements create `evolution_source` and `evolution_target`, and whose head manifest is built over `GrimoireSchemaCatalog.CoreObjects` **plus** those two synthetic objects, so the head-manifest inspection after the last step validates the evolved catalog rather than reporting the new tables as unexpected.

Add the companion tests: a step with a backfill leaves the journal row, keeps metadata at 1, and reports `TransitionIncomplete`; a fresh install against an empty database with the two-version chain writes head directly and no journal row; and a Core tier at `TransitionIncomplete` returns rather than throwing, with `CovenantCanonical` reporting `DependencyUnavailable`.

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaEvolutionInstallerTests"`
Expected: build failure on the new fixture members, then a real failure once they exist.

- [ ] **Step 3: Rewrite `InstallTierAsync` around the planner**

Replace `ClassifyExistingAsync` with a read of the recorded tier row and the journal row, feed both to the planner, and switch on the decision:

- `Refuse` — Core throws `GrimoireSchemaRefusedException`; an optional tier returns `Failed(manifest, refusal)`. `TransitionIncomplete` is **not** routed here; it returns a non-healthy result for every tier including Core.
- `FreshInstall` and `Converge` — today's body, unchanged, against `chain.HeadObjects` and `chain.HeadManifest`.
- `BeginRun` — enter the step loop at the recorded version. The journal row is inserted in the **same transaction as the first step's DDL**, so a crash before that commit leaves neither.
- `ResumeRun` — if the journal row's `BackfillName` is **null**, enter the step loop at `CompletedThroughVersion`. If it is **non-null**, that step's DDL has already committed and must not run again; return `TransitionIncomplete` immediately and leave the sweep to the coordinator.

That branch is load-bearing and is the single easiest thing to get wrong here. Re-running a committed step's `ALTER TABLE ... ADD COLUMN` throws duplicate-column, and for Core that propagates out of the install and aborts startup — reintroducing, through the resume path, precisely the deadlock `TransitionIncomplete` exists to prevent. Write the test for it before the code.

The step loop, per step, in its own `SqliteBusyRetry`-wrapped transaction:

1. Execute the step's statements in ordinal order, each through `GrimoireSchemaCatalog.Resolve`-equivalent template substitution and the unresolved-placeholder refusal. On the first step of a `BeginRun`, insert the journal row in this same transaction.
2. If the step declares a backfill: advance the journal to set `BackfillName`, commit, and **return** a `TransitionIncomplete` result carrying the recorded (not head) version. This is checked before the last-step arm, because a last step with a backfill still has a sweep to drain.
3. If the step declares no backfill and is not the last: advance the journal to `CompletedThroughVersion = step.ToVersion` with a null backfill, commit, continue.
4. If the step declares no backfill and is the last: call `FinalizeRunAsync` in this same transaction and return its result.

`FinalizeRunAsync(connection, transaction, chain, journalRow, context, cancellationToken)` is the **one** algorithm that ends a run, and it is shared:

```csharp
    /// <summary>
    /// Ends a run inside the caller's transaction: seed, inspect against the head manifest, record
    /// the head version, and delete the journal row.
    /// </summary>
    /// <remarks>
    /// Shared by the installer's last backfill-free step and by the backfill runner's final batch,
    /// on the division the Covenant sweeps already keep: the driver owns the transaction, this owns
    /// what finishing means. Two copies would be two ideas of when a version is installed, and the
    /// journal has no completion flag for them to disagree through — its own CHECK forbids
    /// <c>CompletedThroughVersion = TargetVersion</c> precisely so that "drained but not finalized"
    /// is not a representable state.
    /// </remarks>
    internal async Task<GrimoireSchemaTierInstallResult> FinalizeRunAsync(...)
```

A failed inspection inside `FinalizeRunAsync` rolls the caller's transaction back, leaves the journal row untouched, and returns `InstalledCatalogDrift` — the run is retried on a later pass rather than half-recorded.

- [ ] **Step 4: Update the test installer fixture**

Give `GrimoireSchemaTestInstaller` an `InstallAsync(connection, GrimoireSchemaVersionChainSet, CancellationToken)` overload that composes the inspector from `GrimoireSchemaTierOwnershipRegistry.ForChains(chains)` and passes a fixed `TimeProvider`, and keep the existing signature delegating to it with `GrimoireSchemaVersionChains.Default`.

- [ ] **Step 5: Run the evolution tests and confirm they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaEvolutionInstallerTests"`
Expected: PASS.

- [ ] **Step 6: Confirm the existing installer suites still pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Data.Schema|FullyQualifiedName~CovenantSchemaRepair|FullyQualifiedName~Backup"`
Expected: PASS. `CovenantSchemaRepairExecutor` and `BackupRestoreDatabaseWorker` both construct or consume the installer.

---

### Task 6: The backfill runner

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaBackfillRunner.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaBackfillTests.cs`

**Interfaces:**

- Consumes: `IGrimoireSchemaBackfill`, `GrimoireSchemaTransitionJournal`, `GrimoireSchemaVersionChain`.
- Produces: `GrimoireSchemaBackfillRunner.AdvanceAsync(SqliteConnection, GrimoireSchemaVersionChain, GrimoireSchemaTransitionJournalRow, int maxBatches, CancellationToken)` returning `GrimoireSchemaBackfillProgress(int BatchesRun, long RowsProcessed, bool StepComplete)`.

Each batch: open a transaction, call `AdvanceBatchAsync`, advance the journal's cursor and row count **inside that same transaction**, commit. On the batch that reports `IsComplete`, that same transaction instead does one of two things, and never leaves the row in a state that means neither:

- the step is **not** the last — advance `CompletedThroughVersion` to the step's `ToVersion` and clear `BackfillName` and `BackfillCursor`;
- the step **is** the last — call the installer's shared `FinalizeRunAsync` in this same transaction, which inspects against the head manifest, records the head version, and deletes the journal row.

There is deliberately no third option in which the runner drains the sweep and leaves finalization to a later convergence. The journal's `CHECK (CompletedThroughVersion < TargetVersion)` makes "drained, awaiting finalization" unrepresentable, so a later reader could not tell it apart from "still draining" and would re-run the sweep forever.

- [ ] **Step 1: Write the failing bounded-batch test**

```csharp
    [Fact]
    public async Task A_backfill_advances_in_bounded_batches_and_completes()
    {

        // Five source rows, two per batch: three batches, and the third reports completion.
        BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 5, maxRowsPerBatch: 2);

        GrimoireSchemaBackfillProgress progress = await harness.AdvanceAsync(maxBatches: 16);

        Assert.True(progress.StepComplete);

        Assert.Equal(3, harness.Backfill.BatchesRun);

        Assert.Equal(5, await harness.TargetRowCountAsync());

    }

    [Fact]
    public async Task A_pass_runs_no_more_than_its_batch_bound()
    {

        BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 100, maxRowsPerBatch: 2);

        GrimoireSchemaBackfillProgress progress = await harness.AdvanceAsync(maxBatches: 3);

        Assert.False(progress.StepComplete);

        Assert.Equal(3, progress.BatchesRun);

        Assert.Equal(6, await harness.TargetRowCountAsync());

    }
```

- [ ] **Step 2: Write the failing cursor-durability test**

This is the acceptance criterion's core property and gets its own case:

```csharp
    [Fact]
    public async Task A_failing_batch_leaves_the_cursor_at_the_last_committed_batch()
    {

        BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 6, maxRowsPerBatch: 2);

        harness.Backfill.ThrowOnBatch = 2;

        _ = await Assert.ThrowsAnyAsync<Exception>(() => harness.AdvanceAsync(maxBatches: 16));

        // Batch one committed two rows and the cursor that describes them. Batch two committed
        // nothing, so neither its rows nor its cursor survive.
        Assert.Equal(2, await harness.TargetRowCountAsync());

        Assert.Equal("2", await harness.CursorAsync());

        harness.Backfill.ThrowOnBatch = null;

        GrimoireSchemaBackfillProgress resumed = await harness.AdvanceAsync(maxBatches: 16);

        Assert.True(resumed.StepComplete);

        Assert.Equal(6, await harness.TargetRowCountAsync());

    }
```

- [ ] **Step 3: Run both and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaBackfillTests"`
Expected: build failure, then real failures.

- [ ] **Step 4: Write the runner**

Implement exactly as described above. The runner never opens a second connection, never swallows an exception, and never advances a cursor outside the transaction that produced it.

- [ ] **Step 5: Run both and confirm they pass**

Expected: PASS.

- [ ] **Step 6: Mutation-check the durability property**

Move the journal cursor advance out of the batch transaction into its own transaction after the commit, rerun `A_failing_batch_leaves_the_cursor_at_the_last_committed_batch`, and confirm it fails. Restore.

---

### Task 7: The transition coordinator

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionCoordinator.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantAvailabilitySnapshot.cs`
- Test: extend `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaBackfillTests.cs`

**Interfaces:**

- Consumes: `ICovenantConnectionSource.GetOpenCoreConnectionAsync`, `GrimoireSchemaBackfillRunner.AdvanceAsync`, `GrimoireSchemaInstaller.InstallAsync`, `CovenantAvailability.PublishSchema`, `CovenantPersistedAvailabilityPublisher.PublishAsync`.
- Produces: `GrimoireSchemaTransitionCoordinator.RunOnceAsync(CancellationToken)` returning `Result<GrimoireSchemaTransitionPassOutcome>`, and `GrimoireSchemaTransitionCoordinator.MaxBatchesPerPass`.

- [ ] **Step 0: Settle where a background pass gets its install arguments**

`InstallAsync` needs `embeddingDimensions` and a `GrimoireSchemaInitializationContext`. The bootstrapper builds the context from a held installation lock and the master API key, neither of which a background pass has, and resolves the dimension from `IOptionsMonitor<ArcanumSettings>` through a private helper. `BackupRestoreDatabaseWorker` is the second production `InstallAsync` caller and already solves the context half: it reads the staged `covenant_authority_state` row and converges against the installation's own identity, so the install is a no-op for authority.

Resolve both by **sharing**, not mirroring. A second copy of either would be a second measurement of one quantity, and this repository has twice paid for that.

1. Move the authority-row reader out of `BackupRestoreDatabaseWorker` into a schema-owned `GrimoireSchemaInitializationContextReader.TryReadAsync(SqliteConnection, DateTimeOffset, CancellationToken)` under `Data/Schema/`, and have the restore worker call it. The reader belongs where the context record lives; a schema concern reaching into the Backup namespace is backwards.
2. Extract `ResolveEmbeddingDimensions` from `GrimoireDatabaseBootstrapper` into an internal `GrimoireEmbeddingDimensionResolver.Resolve(IServiceProvider)` and have the bootstrapper call it.

The coordinator then reads the context from the database and the dimension from the resolver. Unlike the restore path it has **no** fallback: the tier is already installed, so the authority row must exist, and its absence fails the pass with a bounded code rather than seeding fresh material from a background thread.

- [ ] **Step 1: Add the health-transition value**

In `CovenantAvailabilitySnapshot.cs`, append `SchemaEvolution = 11` to `CovenantHealthTransition`.

- [ ] **Step 2: Write the failing coordinator test**

```csharp
    [Fact]
    public async Task The_coordinator_drains_a_pending_backfill_and_finishes_the_run()
    {

        // Install v1, then converge onto a two-version chain whose step declares a backfill: the
        // installer commits the DDL and stops at the journal.
        EvolutionHarness harness = await EvolutionHarness.StartWithPendingBackfillAsync(sourceRows: 5);

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, harness.LastResult.Core.Health);

        while (await harness.Coordinator.RunOnceAsync(TestContext.Current.CancellationToken) is { IsSuccess: true, Value.Swept: true })
        {

        }

        Assert.Null(await harness.ReadJournalAsync(GrimoireSchemaTransactionTier.Core));

        Assert.Equal(2, await harness.RecordedVersionAsync(GrimoireSchemaTransactionTier.Core));

        Assert.Equal(5, await harness.TargetRowCountAsync());

    }
```

Add a second case proving the coordinator is gated on the journal rather than availability: with `ICovenantAvailability` reporting the canonical tier unavailable, a pending Core backfill still drains.

- [ ] **Step 3: Run and confirm it fails**

Expected: build failure, then a real failure.

- [ ] **Step 4: Write the coordinator**

One pass: take a core connection; read every journal row; for each, resolve the chain, drive `GrimoireSchemaBackfillRunner.AdvanceAsync` for at most `MaxBatchesPerPass` batches; when a step completes, re-enter `GrimoireSchemaInstaller.InstallAsync` so the next step's DDL runs without a restart; then republish Covenant tier health under `CovenantHealthTransition.SchemaEvolution`. A failure records a bounded `LastDurableErrorCode` through `GrimoireSchemaTransitionJournal.RecordErrorAsync`, logs the exception, and leaves the row.

```csharp
    /// <summary>How many bounded batches one pass may run before yielding.</summary>
    internal const int MaxBatchesPerPass = 16;
```

- [ ] **Step 5: Run and confirm both pass**

Expected: PASS.

---

### Task 8: Wire the driver into production

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionWiringTests.cs`

- [ ] **Step 1: Write the failing wiring test**

A wiring test whose needle is unique to the call it pins, including the first argument, because a coordinator that names its method after the worker method it drives satisfies a bare-name search from the wrong side:

```csharp
    [Fact]
    public void The_transition_coordinator_has_a_production_caller()
    {

        string source = ReadProductionSource("Covenant/GrimoireSchemaTransitionHostedService.cs");

        Assert.Contains(
            "GetRequiredService<GrimoireSchemaTransitionCoordinator>()",
            source,
            StringComparison.Ordinal);

        Assert.Contains(".RunOnceAsync(stoppingToken)", source, StringComparison.Ordinal);

    }

    [Fact]
    public void The_transition_hosted_service_is_registered()
    {

        string source = ReadProductionSource("DependencyInjection/ServiceCollectionExtensions.cs");

        Assert.Contains(
            "AddInstallationResetRecoveryAwareHostedService<GrimoireSchemaTransitionHostedService>()",
            source,
            StringComparison.Ordinal);

        Assert.Contains("GrimoireSchemaVersionChains.Default", source, StringComparison.Ordinal);

    }
```

- [ ] **Step 2: Run and confirm it fails**

Expected: FAIL, the files and registrations do not exist.

- [ ] **Step 3: Write the hosted service and register everything**

The hosted service mirrors `CovenantMaintenanceHostedService`: a `BackgroundService` with an `Interval` and an `IdleInterval`, an `[ExcludeFromCodeCoverage]` attribute, a scope per pass, and no availability gate. Register in both the server and CLI composition roots:

```csharp
        services.AddSingleton(static _ => GrimoireSchemaVersionChains.Default);
```

and, on the server host only, beside the Covenant maintenance registration:

```csharp
        // Journal-gated rather than availability-gated. A Covenant tier mid-transition is
        // unavailable by design and a Core tier mid-transition would abort startup, so a driver that
        // waited for health could never run the very sweep that restores it.
        services.AddInstallationResetRecoveryAwareHostedService<GrimoireSchemaTransitionHostedService>();
```

Register `GrimoireSchemaTransitionCoordinator` as scoped in the persistence composition so the CLI resolves it too, exactly as the Covenant coordinators are.

- [ ] **Step 4: Run and confirm it passes**

Expected: PASS.

- [ ] **Step 5: Mutation-check the wiring**

Delete the `AddInstallationResetRecoveryAwareHostedService<GrimoireSchemaTransitionHostedService>()` line, rerun, confirm the test fails, restore.

---

### Task 9: The fail-closed cases and the full green gate

**Files:**

- Test: extend `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaEvolutionInstallerTests.cs`

- [ ] **Step 1: Write the remaining fail-closed cases against a real database**

Each enters through `InstallAsync` and reaches its state the way production does — by installing one chain and converging with another — never by writing a metadata or journal row directly:

- A database installed with a **two**-version chain and reopened with the **one**-version chain refuses `IncompatibleNewerVersion`, and Core throws.
- A database installed at v1 and reopened with a chain whose step pins a different version-1 fingerprint refuses `SourceDefinitionMismatch` and runs no step, proven by the step's table still being absent.
- A database whose catalog is at version 2 while its metadata still says version 1, with **no** journal row, refuses `MixedCatalogVersions`. This one case plays the external actor with direct SQL, under the named exception in the global constraints: install the two-version chain to a pending backfill, then delete the journal row and roll the metadata version back by hand, which is the shape a restore or a hand edit produces. A failed finalization cannot produce this state — it leaves the journal row, which routes classification to the journal arm instead — so there is no production path to reach it through, and the comment in the test must say exactly that.
- A resumed run whose pending step's DDL already committed does **not** re-execute that DDL. Install the two-version chain with a backfill so the step commits and the journal names the sweep, then call `InstallAsync` again: the result is `TransitionIncomplete` rather than a duplicate-object exception, and for the Core tier it returns rather than throwing.
- A table created outside every manifest refuses `InstalledCatalogDrift`.

- [ ] **Step 2: Run the whole schema area**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Data.Schema"`
Expected: PASS.

- [ ] **Step 3: Clear accumulated test state, then run the full suite**

```bash
rm -rf tests/RetroDownfall.Arcanum.Tests/TestResults tests/RetroDownfall.Compendium.Tests/TestResults tests/RetroDownfall.TheForge.Tests/TestResults
```

Then:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
```

A single red concurrency test is a known flake in this suite, not a regression: isolate it with a filtered rerun before treating it as one.

- [ ] **Step 4: Zero-warning build**

```bash
dotnet build RetroDownfall.Arcanum.slnx --no-incremental
```

Expected: `0 Warning(s)`, `0 Error(s)`. An incremental build hides analyzer warnings, so this must be `--no-incremental`.

- [ ] **Step 5: Coverage and AOT gates**

```bash
./scripts/coverage.sh --threshold
```

```bash
./scripts/verify-aot-il-warnings.sh
```

Clear both publish trees before the AOT script; it fails closed on a second run against a dirty tree.

- [ ] **Step 6: The acceptance-criterion mutation check**

Break one production behavior per acceptance criterion and confirm the suite fails, restoring each before the next:

1. Collapse the planner's `BeginRun` arm to `Converge` — the evolution tests must fail.
2. Move the journal cursor advance out of the batch transaction — the cursor-durability test must fail.
3. Delete the `TargetSourceDefinitionFingerprint` resumability check — its planner case must fail.
4. Delete the `MixedCatalogVersions` arm — its planner case and its installer case must fail.
5. Remove `MaxRowsPerBatch` bounding from the runner — the batch-bound test must fail.
6. Make `ResumeRun` execute the pending step's DDL unconditionally — the no-re-execution test must fail with a duplicate-object error rather than passing.

Record the observed failure counts; a mutation that leaves the suite green means the test does not reach the production path it claims to.

---

### Task 10: The documentation sweep

**Files:**

- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.OATH.md`
- Modify: `docs/ArcanumOATH.Human.md`
- Modify: `docs/Arcanum.DEBUGGING.Human.md`
- Modify: `README.md`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs`

- [ ] **Step 1: Re-read every section before editing it**

Read DESIGN §5.4.5, §5.4.5a, §5.4.5b, §10.24, §16.2, the OATH §2.1/§2.2/§9.1/§22 sections, `ArcanumOATH.Human.md` §9 and §11, and the README schema tree and Covenant status paragraph. Code and docs travel together, and these sections have moved since this plan was written.

- [ ] **Step 2: Rewrite the two statements this feature reverses**

DESIGN §5.4.5's "**Fresh install only.** … There is intentionally no incremental or data migration." becomes a statement of the two-policy rule: an **undeclared** schema change is still fresh-install-only, and a **declared** version step is applied through the chain. Keep the reinstall instructions, which remain correct for the undeclared case.

`GrimoireSchemaRefusedException`'s message says the same thing in prose to the operator — "This build will not migrate a database written by a different one" — and must be rewritten to name the refusal it actually describes rather than a blanket policy that is no longer true.

- [ ] **Step 3: Extend §5.4.5a**

Add the transition subtree to the tree listing, the three new health values to the fail-closed list, the per-step algorithm, the journal table and its two-column state encoding, the head-only fingerprint invariant and why it is load-bearing, and the statement that intermediate versions are not independently validated.

- [ ] **Step 4: Add the new §10.25**

One section owning the engine: the chain, the pinned fingerprints, the backfill contract, the journal, the planner's decision table, the two drivers, why the driver is journal-gated rather than availability-gated, and a closing subsection stating plainly what is absent — no shipped step, no shipped backfill, no CLI drain, no downgrade, no intermediate validation.

The absent list must also state the one consequence a reader would otherwise have to derive: while a Core run is in flight the host serves ordinary traffic against a Core catalog sitting between versions, and a service compiled against the head shape can meet a column that is not there yet. Nothing shipped can reach that state, because no Core step exists — but the readiness semantics of the first Core backfill are owed by whichever change authors it, and saying so is cheaper than a later reader discovering it.

- [ ] **Step 5: Sweep the remaining documents**

Add glossary rows to §16.2 for `grimoire_schema_transitions` and the three health values. Update the OATH §9.1 tier table, its §2.1/§2.2 status tables, and the §22 document map's section range. Update `ArcanumOATH.Human.md` §9 prose and its §11 status table. Add an operator-facing paragraph to `Arcanum.DEBUGGING.Human.md` on what a transition state means and what to do about it. Update the README schema-tree listing and Covenant status paragraph.

- [ ] **Step 6: Verify the documentation conventions**

```bash
grep -nE "#[0-9]{2,4}\b" docs/*.md
```

Expected: hits in `Arcanum.OATH.md` only. Then read the diff for inferred issue references — any phrase that only makes sense to someone holding the issue list — and for reintroduced hard wrapping. `git diff docs/` must show no paragraph split across physical lines.

- [ ] **Step 7: Confirm the command map is untouched**

```bash
git status --short docs/Arcanum.CommandMap.json
```

Expected: no output. No CLI verb was added.

- [ ] **Step 8: Final verification, then one commit**

Re-run Task 9 steps 3 through 5. Then review the complete worktree diff before staging — a review pass has silently edited this tree before — and commit once:

```bash
git add -A
git commit
```

Subject: `feat(schema): evolve an installed tier through a declared version chain`. Then merge into `long-term-memory` with `--no-ff` and a `Merge issue #102: …` subject, push, close the issue with an acceptance-criteria checklist comment, tick its line in the parent's roadmap checklist, and delete the merged feature branch.

---

## Self-Review

**Spec coverage.** §5 chain → Task 2. §6 transition resources and the fingerprint exclusion → Task 1. §7 backfills → Tasks 2 and 6. §8 journal → Task 3. §9 health and planner → Task 4. §10.1 bootstrap execution → Task 5. §10.2 coordinator and health republication → Tasks 7 and 8. §11 absences → stated in Task 10 step 4. §12 tests → distributed across Tasks 1 through 9, with the mutation check in Task 9 step 6. §13 documentation → Task 10. No spec section is unimplemented.

**Type consistency.** `GrimoireSchemaTransitionStatementResource` is the catalog's loaded resource; `GrimoireSchemaTransitionStatement` is the chain's step statement. They are deliberately different types with the same field meanings, and Task 2's `BuildChain` is the one place that converts. `SourceDefinitionFingerprintFor` returns `string?` and every caller handles null as "the chain does not cover that version". `GrimoireSchemaBackfillRunner.AdvanceAsync` returns `GrimoireSchemaBackfillProgress`; `IGrimoireSchemaBackfill.AdvanceBatchAsync` returns `GrimoireSchemaBackfillBatch`. Task 5 adds `TimeProvider` to the installer's constructor, and Tasks 7 and 8 both resolve the installer from DI, so the registration must supply it.

**Amendments after review.** Five corrections were folded in before execution: the `ResumeRun` arm must not re-execute a committed step's DDL (Task 5); `FinalizeRunAsync` is shared by both drivers rather than duplicated or deferred (Tasks 5 and 6); the `MixedCatalogVersions` case is unreachable through any production path and is the one named exception to the no-seeding constraint (Task 9 and Global Constraints); the fixture's validation-only chain and its installable chains are separate, because an installable chain's head manifest must declare every object its steps create (Task 2); and the coordinator's install arguments are shared with the bootstrapper and the restore worker rather than mirrored (Task 7, Step 0).

**Known risk carried forward.** The shipped pinned tables and the transition subtree are empty, so `BuildChain`'s loop body, the planner's evolve and resume arms, and the backfill runner are reached in production only once a feature authors a step. Every one of them is reached from a test **through the production entry point** by way of the injected chain set, and Task 2's `Every_shipped_tier_is_at_version_one_with_no_step` asserts the emptiness positively rather than leaving it unstated.
