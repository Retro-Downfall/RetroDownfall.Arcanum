using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The three-tier installation contract: Core, Covenant canonical, and Covenant accelerator each
/// commit in their own transaction, each record their own metadata row, and each fail without taking
/// the tiers beside them down.
/// </summary>
/// <remarks>
/// Every test here installs against a real database rather than inspecting the catalog, because the
/// properties under test are transactional. A tier that "rolled back" is only proven by the absence
/// of its objects, its seeded rows, and its metadata row after the failure, and only a live
/// connection can show all three.
///
/// <para>Failures are injected through a tier's data initializer. That is the last statement inside
/// the tier's transaction before the installed catalog is fingerprinted and committed, so a rollback
/// proven at that point covers every DDL statement the tier ran before it.</para>
/// </remarks>
public sealed class CovenantSchemaInstallerTests
{

    /// <summary>
    /// The SQLCipher provider has to be installed before the first connection is constructed. The
    /// shared installer fixture does this too, but repeating it keeps a run filtered to this class
    /// from depending on whichever type some earlier test happened to touch first.
    /// </summary>
    static CovenantSchemaInstallerTests() => SqliteNativeRuntime.Instance.Initialize();

    private const int Dimensions = 1536;

    /// <summary>
    /// A second, structurally valid source identity. The metadata column checks only its length, so
    /// this is the cheapest way to say "some other build wrote this row" without inventing a
    /// fingerprint that would pass for a real one.
    /// </summary>
    private const string ForeignSourceFingerprint =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    /// <summary>
    /// A version no shipped manifest can ever reach, standing in for a database some future build
    /// installed. It has to be greater than the manifest version rather than merely different: only
    /// the newer direction is unsafe to converge onto.
    /// </summary>
    private const long FutureSchemaVersion = 99;

    /// <summary>
    /// Every tier installs and commits on a database this build has never touched, and each records
    /// exactly one metadata row keyed by its family and transaction tier.
    /// </summary>
    /// <remarks>
    /// Three rows rather than one is the whole point of the design. A single "schema installed" flag
    /// could not express a healthy Core beside a failed Covenant, which is the outcome the tiers
    /// exist to make representable.
    /// </remarks>
    [Fact]
    public async Task Fresh_install_commits_core_canonical_and_accelerator_in_three_transactions()
    {

        await using SqliteConnection connection = await OpenAsync();

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.CovenantCanonical.Health);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.CovenantAccelerator.Health);

        Assert.Equal(3L, await CountMetadataRowsAsync(connection));

        Assert.Equal(1L, await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.Core));

        Assert.Equal(
            1L,
            await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Equal(
            1L,
            await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.CovenantAccelerator));

        Assert.True(await ObjectExistsAsync(connection, "Sessions"), "missing core table Sessions");

        Assert.True(await ObjectExistsAsync(connection, "Entries"), "missing core table Entries");

        Assert.True(
            await ObjectExistsAsync(connection, "grimoire_feature_schemas"),
            "missing core table grimoire_feature_schemas");

        Assert.True(await ObjectExistsAsync(connection, "covenant_entries"), "missing canonical table");

        Assert.True(await ObjectExistsAsync(connection, "covenant_fts"), "missing accelerator index");

    }

    /// <summary>
    /// The same install runs on a fresh database and on one this build wrote a moment ago, so it has
    /// to converge rather than duplicate. Identical installed-catalog fingerprints prove convergence
    /// at the level that matters: the second run observed the same installed catalog, not merely the
    /// same source definitions.
    /// </summary>
    [Fact]
    public async Task Repeat_install_is_idempotent_and_preserves_fingerprints()
    {

        await using SqliteConnection connection = await OpenAsync();

        GrimoireSchemaInstallResult first = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        GrimoireSchemaInstallResult second = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        Assert.Equal(first.Core.InstalledCatalogFingerprint, second.Core.InstalledCatalogFingerprint);

        Assert.Equal(
            first.CovenantCanonical.InstalledCatalogFingerprint,
            second.CovenantCanonical.InstalledCatalogFingerprint);

        Assert.Equal(
            first.CovenantAccelerator.InstalledCatalogFingerprint,
            second.CovenantAccelerator.InstalledCatalogFingerprint);

        // A non-null fingerprint on both runs is what makes the equality above meaningful; two nulls
        // would compare equal and prove nothing.
        Assert.NotNull(second.CovenantCanonical.InstalledCatalogFingerprint);

        Assert.Equal(3L, await CountMetadataRowsAsync(connection));

    }

    [Fact]
    public async Task Repeat_install_preserves_a_full_width_taint_version()
    {

        await using SqliteConnection connection = await OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        await using (SqliteCommand taint = connection.CreateCommand())
        {

            taint.CommandText = """
                UPDATE covenant_authority_state
                SET HostToolsStateCode = 3,
                    TaintTimeMasterVersion = X'FFFFFFFFFFFFFFFF',
                    TaintFingerprint = zeroblob(32),
                    TransitionId = '11111111-2222-4333-8444-555555555555'
                WHERE StateKey = 1;
                """;

            Assert.Equal(1, await taint.ExecuteNonQueryAsync(CancellationToken.None));

        }

        GrimoireSchemaInstallResult repeated = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, repeated.Core.Health);

        await using SqliteCommand inspect = connection.CreateCommand();

        inspect.CommandText = "SELECT hex(TaintTimeMasterVersion) FROM covenant_authority_state WHERE StateKey = 1;";

        Assert.Equal("FFFFFFFFFFFFFFFF", await inspect.ExecuteScalarAsync(CancellationToken.None));

    }

    /// <summary>
    /// Canonical failure is confined to canonical. Core stays committed and healthy, the metadata
    /// table it created survives, and Covenant is simply unavailable for this process.
    /// </summary>
    /// <remarks>
    /// This is the property that lets status, diagnosis, and offline repair keep working on a
    /// database whose Covenant tier cannot install. If the canonical rollback reached back into Core,
    /// a damaged optional capability would take the whole host with it, which is exactly the coupling
    /// the tier boundary removes.
    /// </remarks>
    [Fact]
    public async Task Canonical_failure_rolls_back_only_canonical_and_returns_unavailable()
    {

        await using SqliteConnection connection = await OpenAsync();

        GrimoireSchemaInstallResult result = await InstallWithFailingTierAsync(
            connection,
            new ThrowingDataInitializer(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.True(await ObjectExistsAsync(connection, "Sessions"), "core rollback reached past its tier");

        Assert.True(await ObjectExistsAsync(connection, "grimoire_feature_schemas"));

        Assert.Equal(1L, await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.Core));

        Assert.Equal(GrimoireSchemaTierHealth.Unavailable, result.CovenantCanonical.Health);

        Assert.False(result.CovenantCanonical.IsHealthy);

        Assert.False(await ObjectExistsAsync(connection, "covenant_entries"));

        Assert.Equal(
            0L,
            await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.CovenantCanonical));

    }

    /// <summary>
    /// Accelerator failure is confined to the accelerator. Canonical stays committed with its
    /// metadata row, so Covenant remains authoritative and only inspection search falls back.
    /// </summary>
    [Fact]
    public async Task Accelerator_failure_rolls_back_only_accelerator_and_returns_unavailable_without_disturbing_canonical()
    {

        await using SqliteConnection connection = await OpenAsync();

        GrimoireSchemaInstallResult result = await InstallWithFailingTierAsync(
            connection,
            new ThrowingDataInitializer(GrimoireSchemaTransactionTier.CovenantAccelerator));

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.CovenantCanonical.Health);

        Assert.True(await ObjectExistsAsync(connection, "covenant_entries"));

        Assert.Equal(
            1L,
            await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.False(result.CovenantAccelerator.IsHealthy);

        Assert.Equal(GrimoireSchemaTierHealth.Unavailable, result.CovenantAccelerator.Health);

        Assert.False(await ObjectExistsAsync(connection, "covenant_search_documents"));

        Assert.Equal(
            0L,
            await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.CovenantAccelerator));

    }

    /// <summary>
    /// A tier whose dependency is unavailable is never attempted, and says so with its own code
    /// rather than with a generic failure.
    /// </summary>
    /// <remarks>
    /// The distinction is operational, not cosmetic. <see cref="GrimoireSchemaTierHealth.Unavailable"/>
    /// on the accelerator would send an operator looking for a broken FTS5 index; the accelerator was
    /// never touched, and the only thing worth repairing is the canonical tier underneath it.
    /// </remarks>
    [Fact]
    public async Task Accelerator_is_not_attempted_when_canonical_is_unavailable()
    {

        await using SqliteConnection connection = await OpenAsync();

        GrimoireSchemaInstallResult result = await InstallWithFailingTierAsync(
            connection,
            new ThrowingDataInitializer(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Equal(
            GrimoireSchemaTierHealth.DependencyUnavailable,
            result.CovenantAccelerator.Health);

        Assert.Null(result.CovenantAccelerator.InstalledCatalogFingerprint);

        Assert.False(await ObjectExistsAsync(connection, "covenant_fts"));

    }

    /// <summary>
    /// A tier installed by a newer build is refused before any DDL runs. Arcanum has no installed
    /// base, so the only databases it can meet are ones some build of Arcanum itself wrote, and
    /// downgrading one in place would corrupt it rather than migrate it.
    /// </summary>
    /// <remarks>
    /// The surviving future version in the metadata row is the assertion that matters: it proves the
    /// refusal happened at the gate, before the install could reach the statement that would have
    /// rewritten the row back to this binary's version.
    /// </remarks>
    [Fact]
    public async Task Installed_newer_version_is_refused_without_DDL()
    {

        await using SqliteConnection connection = await OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, Dimensions, CancellationToken.None);

        await ExecuteAsync(
            connection,
            """
            UPDATE grimoire_feature_schemas
            SET SchemaVersion = $version
            WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
            """,
            FutureSchemaVersion);

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        Assert.Equal(
            GrimoireSchemaTierHealth.IncompatibleNewerVersion,
            result.CovenantCanonical.Health);

        Assert.Null(result.CovenantCanonical.InstalledCatalogFingerprint);

        Assert.Equal(FutureSchemaVersion, await ReadCanonicalSchemaVersionAsync(connection));

    }

    /// <summary>
    /// The same version recorded against a different source definition is refused: the two builds
    /// disagree about what that version means, and there is no safe way to guess which is right.
    /// </summary>
    [Fact]
    public async Task Same_version_different_source_fingerprint_is_refused()
    {

        await using SqliteConnection connection = await OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, Dimensions, CancellationToken.None);

        await ExecuteAsync(
            connection,
            """
            UPDATE grimoire_feature_schemas
            SET SourceDefinitionFingerprint = $fingerprint
            WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
            """,
            ForeignSourceFingerprint);

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        Assert.Equal(
            GrimoireSchemaTierHealth.SourceDefinitionMismatch,
            result.CovenantCanonical.Health);

        Assert.Equal(ForeignSourceFingerprint, await ReadCanonicalSourceFingerprintAsync(connection));

    }

    /// <summary>
    /// The identity each tier records is scoped to that tier's own resources, so a release that
    /// changes one capability's definitions cannot be read as a change to another's.
    /// </summary>
    /// <remarks>
    /// This is the value <c>ClassifyExistingAsync</c> compares on the next open, and Core is the tier
    /// whose mismatch throws instead of degrading. Recording the whole-tree fingerprint against Core
    /// would make a comment-only edit under <c>Capabilities/Covenant/</c> refuse an intact Core tier
    /// and abort the host and every CLI verb that opens the Grimoire - the total outage the three
    /// failure domains exist to prevent.
    /// </remarks>
    [Fact]
    public async Task Each_tier_records_a_source_identity_scoped_to_its_own_resources()
    {

        await using SqliteConnection connection = await OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, Dimensions, CancellationToken.None);

        Assert.Equal(
            GrimoireSchemaCatalog.CoreSchemaFingerprint,
            await ReadSourceFingerprintAsync(
                connection,
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core));

        Assert.NotEqual(
            GrimoireSchemaCatalog.CanonicalSchemaFingerprint,
            await ReadSourceFingerprintAsync(
                connection,
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core));

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            await ReadSourceFingerprintAsync(
                connection,
                GrimoireSchemaFamily.Covenant,
                GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint,
            await ReadSourceFingerprintAsync(
                connection,
                GrimoireSchemaFamily.Covenant,
                GrimoireSchemaTransactionTier.CovenantAccelerator));

    }

    /// <summary>
    /// Objects that exist with no metadata row prove nothing about what installed them, so the tier
    /// is refused rather than converged onto.
    /// </summary>
    /// <remarks>
    /// This is the shape a partial manual repair leaves behind. Installing over it would produce a
    /// database that is half this build's and half something else's, with a metadata row afterwards
    /// asserting it is entirely this build's - a lie the next start would believe.
    /// </remarks>
    [Fact]
    public async Task Optional_objects_without_metadata_are_refused()
    {

        await using SqliteConnection connection = await OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, Dimensions, CancellationToken.None);

        await ExecuteAsync(
            connection,
            """
            DELETE FROM grimoire_feature_schemas
            WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
            """);

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            Dimensions,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.MetadataMissing, result.CovenantCanonical.Health);

        Assert.Equal(
            0L,
            await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.CovenantCanonical));

        // Refusing is not repairing: the objects are left exactly where they were for an operator to
        // inspect, rather than dropped by a startup path guessing at what they are.
        Assert.True(await ObjectExistsAsync(connection, "covenant_entries"));

    }

    /// <summary>
    /// The core-only install seeds the authority row a new-install startup gate has to read before
    /// any optional service may initialize, and touches nothing Covenant owns.
    /// </summary>
    /// <remarks>
    /// The gate runs on one non-pooled connection before the host decides what may start, so it must
    /// not be able to create a Covenant object as a side effect. If it could, the decision it exists
    /// to inform would already have been made by the act of asking.
    /// </remarks>
    [Fact]
    public async Task Core_only_install_seeds_authority_without_creating_optional_tiers()
    {

        await using SqliteConnection connection = await OpenAsync();

        GrimoireSchemaTierInstallResult core = await GrimoireSchemaTestInstaller.Create()
            .InstallCoreOnlyAsync(
                connection,
                GrimoireSchemaTestInstaller.CreateContext(),
                CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, core.Health);

        Assert.Equal(GrimoireSchemaTransactionTier.Core, core.TransactionTier);

        Assert.True(await ObjectExistsAsync(connection, "covenant_authority_state"));

        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM covenant_authority_state;"));

        Assert.False(await ObjectExistsAsync(connection, "covenant_entries"));

        Assert.False(await ObjectExistsAsync(connection, "covenant_fts"));

        Assert.Equal(1L, await CountMetadataRowsAsync(connection));

    }

    /// <summary>
    /// The gate runs on every start, not only the first, so repeating it has to converge onto the
    /// identity already installed rather than mint a new one.
    /// </summary>
    [Fact]
    public async Task Repeat_core_only_install_is_idempotent_and_returns_the_same_identity()
    {

        await using SqliteConnection connection = await OpenAsync();

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create();

        GrimoireSchemaTierInstallResult first = await installer.InstallCoreOnlyAsync(
            connection,
            GrimoireSchemaTestInstaller.CreateContext(),
            CancellationToken.None);

        GrimoireSchemaTierInstallResult second = await installer.InstallCoreOnlyAsync(
            connection,
            GrimoireSchemaTestInstaller.CreateContext(),
            CancellationToken.None);

        Assert.NotNull(first.InstalledCatalogFingerprint);

        Assert.Equal(first.InstalledCatalogFingerprint, second.InstalledCatalogFingerprint);

        Assert.Equal(first.SourceDefinitionFingerprint, second.SourceDefinitionFingerprint);

        Assert.Equal(1L, await CountMetadataRowsAsync(connection));

        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM covenant_authority_state;"));

    }

    /// <summary>
    /// Cancellation propagates instead of being recorded as tier health. A cancelled install is the
    /// operator stopping the host, not a damaged tier, and writing it down as a health state would
    /// make the next start refuse a database that is perfectly fine.
    /// </summary>
    [Fact]
    public async Task Cancellation_propagates_instead_of_being_recorded_as_tier_health()
    {

        await using SqliteConnection connection = await OpenAsync();

        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GrimoireSchemaTestInstaller.Create().InstallAsync(
                connection,
                Dimensions,
                GrimoireSchemaTestInstaller.CreateContext(),
                cancellation.Token));

        Assert.False(await ObjectExistsAsync(connection, "Sessions"));

        Assert.False(await ObjectExistsAsync(connection, "grimoire_feature_schemas"));

    }

    /// <summary>
    /// A tier's DDL, its seeded rows, and its metadata row are one transaction. An initializer that
    /// throws leaves none of the three behind.
    /// </summary>
    /// <remarks>
    /// The manifest is the closed set of objects the tier installs, so walking it is what makes this
    /// assertion complete: a hand-written list of table names would silently stop covering any object
    /// added to the tier afterwards.
    /// </remarks>
    [Fact]
    public async Task Initializer_failure_rolls_back_its_DDL_metadata_and_data()
    {

        await using SqliteConnection connection = await OpenAsync();

        GrimoireSchemaInstallResult result = await InstallWithFailingTierAsync(
            connection,
            new ThrowingDataInitializer(GrimoireSchemaTransactionTier.CovenantCanonical));

        Assert.False(result.CovenantCanonical.IsHealthy);

        int inspected = 0;

        foreach (GrimoireSchemaManifestEntry entry in GrimoireSchemaManifests.CovenantCanonical.Entries)
        {

            inspected++;

            Assert.False(
                await ObjectExistsAsync(connection, entry.Name),
                $"canonical object {entry.Name} survived a rolled-back tier");

        }

        // Guards against the loop above passing because the manifest returned nothing at all.
        Assert.True(inspected > 0, "the canonical manifest declared no objects");

        Assert.Equal(
            0L,
            await CountMetadataRowsAsync(connection, GrimoireSchemaTransactionTier.CovenantCanonical));

    }

    private static Task<SqliteConnection> OpenAsync() =>
        GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

    /// <summary>
    /// Installs all three tiers with one stock initializer swapped for a failing one, leaving the
    /// other two tiers seeding exactly as they do in the host.
    /// </summary>
    private static Task<GrimoireSchemaInstallResult> InstallWithFailingTierAsync(
        SqliteConnection connection,
        IGrimoireSchemaDataInitializer failing)
    {

        List<IGrimoireSchemaDataInitializer> initializers = [];

        foreach (IGrimoireSchemaDataInitializer stock in new IGrimoireSchemaDataInitializer[]
        {
            new CoreGrimoireSchemaDataInitializer(),
            new CovenantCanonicalSchemaDataInitializer(),
            new CovenantAcceleratorSchemaDataInitializer(),
        })
        {

            initializers.Add(stock.TransactionTier == failing.TransactionTier ? failing : stock);

        }

        GrimoireSchemaInstaller installer = new(
            new GrimoireSchemaManifestInspector(GrimoireSchemaTierOwnershipRegistry.CreateDefault()),
            new GrimoireSchemaDataInitializers(initializers));

        return installer.InstallAsync(
            connection,
            Dimensions,
            GrimoireSchemaTestInstaller.CreateContext(),
            CancellationToken.None);

    }

    /// <summary>
    /// Fails at the one point where a tier's DDL is fully applied and its transaction has not yet
    /// committed, which is the latest moment the tier can still fail.
    /// </summary>
    private sealed class ThrowingDataInitializer(GrimoireSchemaTransactionTier tier)
        : IGrimoireSchemaDataInitializer
    {

        public GrimoireSchemaTransactionTier TransactionTier { get; } = tier;

        public Task InitializeAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            GrimoireSchemaInitializationContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"The {TransactionTier} tier seed failed for this test.");

    }

    private static async Task<bool> ObjectExistsAsync(SqliteConnection connection, string name)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """SELECT 1 FROM sqlite_master WHERE "name" = $name LIMIT 1;""";

        _ = command.Parameters.AddWithValue("$name", name);

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is not null and not DBNull;

    }

    private static Task<long> CountMetadataRowsAsync(SqliteConnection connection) =>
        ScalarLongAsync(connection, "SELECT COUNT(*) FROM grimoire_feature_schemas;");

    private static async Task<long> CountMetadataRowsAsync(
        SqliteConnection connection,
        GrimoireSchemaTransactionTier tier)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*)
            FROM grimoire_feature_schemas
            WHERE TransactionTierCode = $tierCode;
            """;

        _ = command.Parameters.AddWithValue("$tierCode", (long)tier);

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is null or DBNull ? 0L : (long)result;

    }

    private static async Task<long> ReadCanonicalSchemaVersionAsync(SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT SchemaVersion
            FROM grimoire_feature_schemas
            WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
            """;

        AddCanonicalKey(command);

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is null or DBNull ? 0L : (long)result;

    }

    private static async Task<string?> ReadCanonicalSourceFingerprintAsync(SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT SourceDefinitionFingerprint
            FROM grimoire_feature_schemas
            WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
            """;

        AddCanonicalKey(command);

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is null or DBNull ? null : (string)result;

    }

    private static async Task<string?> ReadSourceFingerprintAsync(
        SqliteConnection connection,
        GrimoireSchemaFamily family,
        GrimoireSchemaTransactionTier tier)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT SourceDefinitionFingerprint
            FROM grimoire_feature_schemas
            WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
            """;

        _ = command.Parameters.AddWithValue("$familyCode", (long)family);

        _ = command.Parameters.AddWithValue("$tierCode", (long)tier);

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is null or DBNull ? null : (string)result;

    }

    /// <summary>
    /// Runs a statement keyed to the Covenant canonical metadata row, with an optional replacement
    /// value. Every statement here rewrites metadata rather than schema, which is how a test stands
    /// in for a database some other build left behind.
    /// </summary>
    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        object? replacement = null)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        AddCanonicalKey(command);

        if (replacement is long version)
        {

            _ = command.Parameters.AddWithValue("$version", version);

        }
        else if (replacement is string fingerprint)
        {

            _ = command.Parameters.AddWithValue("$fingerprint", fingerprint);

        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static void AddCanonicalKey(SqliteCommand command)
    {

        _ = command.Parameters.AddWithValue("$familyCode", (long)GrimoireSchemaFamily.Covenant);

        _ = command.Parameters.AddWithValue(
            "$tierCode",
            (long)GrimoireSchemaTransactionTier.CovenantCanonical);

    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return result is null or DBNull ? 0L : (long)result;

    }

}
