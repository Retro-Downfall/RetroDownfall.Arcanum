using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The upgrade that gives every existing Saga memory an explicit scope, driven the way a host reaches
/// it: install version 1, then hand the same installer the shipped chain and let the shipped driver
/// drain the sweep.
/// </summary>
/// <remarks>
/// The rows this suite seeds are the one thing that cannot be produced through a writer: a version-1
/// <c>saga_memories</c> row has no scope column at all, and no writer in this binary can create one.
/// Everything the suite <i>asserts</i> — the classification each row receives — is produced by
/// production code from that legacy state.
///
/// <para>The classification, not the column, is the point. A single nullable Campaign column cannot tell
/// an explicitly installation-global memory from one whose ownership was never resolved, and the
/// Covenant forward contract is explicit that unresolved ownership never becomes installation-global.
/// So the scope kind is carried separately and the two unresolved kinds are excluded from
/// cross-Campaign retrieval rather than admitted to it.</para>
/// </remarks>
public sealed class SagaMemoryCampaignScopeEvolutionTests
{

    private static readonly Guid CampaignA = new("A0000000-0000-4000-8000-000000000001");

    private static readonly Guid CampaignB = new("B0000000-0000-4000-8000-000000000002");

    static SagaMemoryCampaignScopeEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// The pin is a literal captured before the version-1 tree was edited, and nothing can recompute it
    /// from a tree that no longer exists. Reconstructing that tree and hashing it is the only check that
    /// the pinned value is the one version 1 actually published — and a wrong pin means every version-1
    /// installation refuses the upgrade with <c>SourceDefinitionMismatch</c>.
    /// </summary>
    [Fact]
    public void The_shipped_chain_pins_the_fingerprint_the_version_one_tree_published()
    {

        GrimoireSchemaVersionChain core =
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.Equal(CoreSchemaVersionOneFixture.Fingerprint, core.SourceDefinitionFingerprintFor(1));

    }

    [Fact]
    public async Task A_memory_owned_by_a_campaign_bound_session_is_classified_to_that_campaign()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        Guid session = await harness.SeedCampaignSessionAsync(CampaignA);

        string memory = await harness.SeedMemoryAsync(session, "campaign A concluded something");

        await harness.UpgradeAsync();

        Assert.Equal(
            (SagaMemoryScopeKind.Campaign, CampaignA),
            await harness.ReadScopeAsync(memory));

    }

    [Fact]
    public async Task A_memory_owned_by_a_global_only_session_is_classified_global()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        Guid session = await harness.SeedGlobalOnlySessionAsync();

        string memory = await harness.SeedMemoryAsync(session, "a conclusion bound to no campaign");

        await harness.UpgradeAsync();

        Assert.Equal(
            (SagaMemoryScopeKind.Global, (Guid?)null),
            await harness.ReadScopeAsync(memory));

    }

    /// <summary>
    /// A memory that was never bound to a Session was never bound to a Campaign either, so it is the
    /// one legacy row that is genuinely installation-scoped.
    /// </summary>
    [Fact]
    public async Task A_memory_with_no_owning_session_is_classified_global()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        string memory = await harness.SeedMemoryAsync(sessionId: null, "an unowned conclusion");

        await harness.UpgradeAsync();

        Assert.Equal(
            (SagaMemoryScopeKind.Global, (Guid?)null),
            await harness.ReadScopeAsync(memory));

    }

    /// <summary>
    /// The contract line this whole classification exists for: ambiguous legacy ownership never becomes
    /// installation-global by default.
    /// </summary>
    [Fact]
    public async Task A_memory_owned_by_a_legacy_unresolved_session_is_never_classified_global()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        Guid session = await harness.SeedLegacyUnresolvedSessionAsync();

        string memory = await harness.SeedMemoryAsync(session, "an ambiguous conclusion");

        await harness.UpgradeAsync();

        Assert.Equal(
            (SagaMemoryScopeKind.LegacyUnresolved, (Guid?)null),
            await harness.ReadScopeAsync(memory));

    }

    /// <summary>
    /// A memory whose Session is gone has no binding to read, which is exactly as unresolved as an
    /// ambiguous one and must not fall back to installation-global.
    /// </summary>
    [Fact]
    public async Task A_memory_whose_session_no_longer_exists_is_never_classified_global()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        string memory = await harness.SeedMemoryAsync(Guid.NewGuid(), "an orphaned conclusion");

        await harness.UpgradeAsync();

        Assert.Equal(
            (SagaMemoryScopeKind.LegacyUnresolved, (Guid?)null),
            await harness.ReadScopeAsync(memory));

    }

    /// <summary>
    /// Deleting a Campaign never converts the memories it owned into installation-global ones.
    /// </summary>
    [Fact]
    public async Task A_memory_owned_by_a_deleted_campaign_keeps_that_campaign_and_never_becomes_global()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        Guid session = await harness.SeedCampaignSessionAsync(CampaignB);

        string memory = await harness.SeedMemoryAsync(session, "a conclusion from a campaign since deleted");

        await harness.DeleteCampaignAsync(CampaignB);

        await harness.UpgradeAsync();

        Assert.Equal(
            (SagaMemoryScopeKind.Campaign, CampaignB),
            await harness.ReadScopeAsync(memory));

    }

    /// <summary>
    /// Interruption is the ordinary case for a sweep this size, so one batch at a time must reach the
    /// same state as one uninterrupted drain, and a drain over an already-classified corpus must change
    /// nothing.
    /// </summary>
    [Fact]
    public async Task The_backfill_is_idempotent_and_safe_to_interrupt()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        Guid campaignSession = await harness.SeedCampaignSessionAsync(CampaignA);

        Guid globalSession = await harness.SeedGlobalOnlySessionAsync();

        List<string> owned = [];

        for (int index = 0; index < 40; index++)
        {

            owned.Add(
                await harness.SeedMemoryAsync(
                    index % 2 == 0 ? campaignSession : globalSession,
                    $"conclusion {index}"));

        }

        await harness.UpgradeInOnePassAtATimeAsync();

        Assert.Equal(0, await harness.UnclassifiedCountAsync());

        for (int index = 0; index < owned.Count; index++)
        {

            Assert.Equal(
                index % 2 == 0
                    ? (SagaMemoryScopeKind.Campaign, (Guid?)CampaignA)
                    : (SagaMemoryScopeKind.Global, null),
                await harness.ReadScopeAsync(owned[index]));

        }

        // A finished run leaves nothing for a later pass to move, which is what makes an interrupted
        // upgrade safe to restart.
        Assert.False((await harness.RunOnePassAsync()).Value.Advanced);

        Assert.Equal(0, await harness.UnclassifiedCountAsync());

    }

    /// <summary>
    /// An upgrade that stops mid-sweep leaves the tier below head, so nothing may read the new column as
    /// if the classification were complete.
    /// </summary>
    [Fact]
    public async Task An_interrupted_upgrade_leaves_the_tier_below_head_until_the_sweep_drains()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        Guid session = await harness.SeedCampaignSessionAsync(CampaignA);

        _ = await harness.SeedMemoryAsync(session, "a conclusion");

        GrimoireSchemaInstallResult installed = await harness.InstallShippedChainAsync();

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, installed.Core.Health);

        Assert.Equal(1, await harness.RecordedVersionAsync());

        _ = await harness.RunOnePassAsync();

        Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, await harness.RecordedVersionAsync());

    }

    /// <summary>
    /// The Lexicon's existing rows are installation-global authored content, and the issue's own
    /// requirement is that they stay exactly that. The unique index moves from the name alone to the
    /// scope plus the name, so a global name is still unique among global names.
    /// </summary>
    [Fact]
    public async Task An_upgraded_lexicon_entry_keeps_global_scope_and_its_name_stays_unique_there()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        await harness.SeedLexiconEntryAsync("config");

        await harness.UpgradeAsync();

        Assert.Equal(string.Empty, await harness.ReadLexiconScopeAsync("config"));

        _ = await Assert.ThrowsAsync<SqliteException>(() => harness.SeedLexiconEntryAsync("config"));

    }

    /// <summary>
    /// The whole point of the scope column: two Campaigns may hold an entity of the same name.
    /// </summary>
    [Fact]
    public async Task Two_campaigns_may_each_hold_a_lexicon_entry_of_the_same_name()
    {

        await using CampaignScopeUpgradeHarness harness = await CampaignScopeUpgradeHarness.StartAsync();

        await harness.SeedLexiconEntryAsync("config");

        await harness.UpgradeAsync();

        await harness.SeedLexiconEntryAsync("config", CampaignA.ToString());

        await harness.SeedLexiconEntryAsync("config", CampaignB.ToString());

        Assert.Equal(3, await harness.LexiconCountAsync("config"));

    }

    /// <summary>
    /// One open scratch installation that starts at version 1 and is upgraded through the shipped chain.
    /// </summary>
    private sealed class CampaignScopeUpgradeHarness : IAsyncDisposable
    {

        private readonly EvolutionScratchDatabase _file;

        private readonly SqliteConnection _connection;

        private readonly ServiceProvider _services;

        private readonly GrimoireSchemaTransitionCoordinator _coordinator;

        private CampaignScopeUpgradeHarness(
            EvolutionScratchDatabase file,
            SqliteConnection connection,
            ServiceProvider services,
            GrimoireSchemaTransitionCoordinator coordinator)
        {

            _file = file;

            _connection = connection;

            _services = services;

            _coordinator = coordinator;

        }

        internal static async Task<CampaignScopeUpgradeHarness> StartAsync()
        {

            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CoreSchemaVersionOneFixture.ChainSet(),
                1536,
                CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

            Assert.Equal(1, installed.Core.SchemaVersion);

            ServiceCollection collection = new();

            _ = collection.AddOptions();

            _ = collection.Configure<ArcanumSettings>(static _ => { });

            ServiceProvider services = collection.BuildServiceProvider();

            GrimoireSchemaInstaller installer =
                GrimoireSchemaTestInstaller.Create(GrimoireSchemaVersionChains.Default);

            GrimoireSchemaTransitionCoordinator coordinator = new(
                new FixedCoreConnectionSource(connection),
                GrimoireSchemaVersionChains.Default,
                installer,
                new GrimoireSchemaBackfillRunner(installer, TimeProvider.System),
                services,
                new CovenantAvailability(new CovenantRuntimeGenerationProvider()),
                TimeProvider.System);

            return new CampaignScopeUpgradeHarness(file, connection, services, coordinator);

        }

        internal Task<GrimoireSchemaInstallResult> InstallShippedChainAsync() =>
            GrimoireSchemaTestInstaller.InstallAsync(
                _connection,
                GrimoireSchemaVersionChains.Default,
                1536,
                CancellationToken.None);

        internal Task<Result<GrimoireSchemaTransitionPassOutcome>> RunOnePassAsync() =>
            _coordinator.RunOnceAsync(CancellationToken.None);

        /// <summary>Installs the shipped chain and drains whatever sweep it opened.</summary>
        internal async Task UpgradeAsync()
        {

            _ = await InstallShippedChainAsync();

            while ((await RunOnePassAsync()).Value.Advanced)
            {

            }

            Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, await RecordedVersionAsync());

        }

        /// <summary>
        /// The same upgrade, one bounded pass at a time, which is what an interrupted host does.
        /// </summary>
        internal async Task UpgradeInOnePassAtATimeAsync()
        {

            _ = await InstallShippedChainAsync();

            for (int pass = 0; pass < 200; pass++)
            {

                if (!(await RunOnePassAsync()).Value.Advanced)
                {

                    break;

                }

            }

            Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, await RecordedVersionAsync());

        }

        internal async Task<Guid> SeedCampaignSessionAsync(Guid campaignId)
        {

            await ExecuteAsync(
                """
                INSERT OR IGNORE INTO "Campaigns"
                    ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                VALUES ($id, $name, $nameLower, $path, 0, '{}', $now, $now);
                """,
                ("$id", campaignId.ToString()),
                ("$name", campaignId.ToString("N")),
                ("$nameLower", campaignId.ToString("N")),
                ("$path", $"/campaigns/{campaignId:N}"),
                ("$now", Timestamp));

            return await SeedSessionAsync(campaignId);

        }

        /// <summary>
        /// A Session that live turn admission resolved to no Campaign. Its binding is written under the
        /// same false-by-default write scope production borrows, because that scope is the only way any
        /// writer may state a Session's authority.
        /// </summary>
        internal async Task<Guid> SeedGlobalOnlySessionAsync()
        {

            Guid sessionId = await InsertSessionRowAsync(campaignId: null);

            using (CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance
                .Authorize(_connection, CovenantSqliteAuthorizationKind.SessionBindingWrite))
            {

                await ExecuteAsync(
                    """
                    INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
                    VALUES ($id, 1, NULL, $now);
                    """,
                    ("$id", sessionId.ToString()),
                    ("$now", Timestamp));

            }

            await ConvergeVersionOneAsync();

            return sessionId;

        }

        /// <summary>
        /// A Session from before bindings existed. Nothing here writes its binding: the Core data
        /// initializer does, at the next convergence, exactly as it does on a real installation.
        /// </summary>
        internal Task<Guid> SeedLegacyUnresolvedSessionAsync() => SeedSessionAsync(campaignId: null);

        internal async Task DeleteCampaignAsync(Guid campaignId) =>
            await ExecuteAsync(
                """DELETE FROM "Campaigns" WHERE "Id" = $id;""",
                ("$id", campaignId.ToString()));

        /// <summary>
        /// A version-1 Saga row: no scope column, because version 1 had none and no writer in this
        /// binary can produce one.
        /// </summary>
        internal async Task<string> SeedMemoryAsync(Guid? sessionId, string content)
        {

            string id = Guid.NewGuid().ToString();

            await ExecuteAsync(
                """
                INSERT INTO "saga_memories" ("Id", "Content", "CreatedAt", "SessionId", "Tags", "Source")
                VALUES ($id, $content, $now, $sessionId, NULL, 'test');
                """,
                ("$id", id),
                ("$content", content),
                ("$now", Timestamp),
                ("$sessionId", sessionId?.ToString()));

            return id;

        }

        internal Task SeedLexiconEntryAsync(string nameNormalized, string? scopeCampaignId = null) =>
            scopeCampaignId is null
                ? ExecuteAsync(
                    """
                    INSERT INTO lexicon_entries
                        (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
                    VALUES ($id, $name, $name, 'Concept', '[]', '', $now);
                    """,
                    ("$id", Guid.NewGuid().ToString()),
                    ("$name", nameNormalized),
                    ("$now", Timestamp))
                : ExecuteAsync(
                    """
                    INSERT INTO lexicon_entries
                        (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt, ScopeCampaignId)
                    VALUES ($id, $name, $name, 'Concept', '[]', '', $now, $scope);
                    """,
                    ("$id", Guid.NewGuid().ToString()),
                    ("$name", nameNormalized),
                    ("$now", Timestamp),
                    ("$scope", scopeCampaignId));

        internal async Task<(SagaMemoryScopeKind Kind, Guid? CampaignId)> ReadScopeAsync(string memoryId)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            // Deliberately unquoted. SQLite falls back to reading a double-quoted identifier that
            // matches no column as a string literal, so `SELECT "CampaignId"` against a table without
            // that column returns the word rather than failing, and an assertion would be comparing
            // against a value nothing stored.
            command.CommandText =
                """SELECT ScopeKindCode, CampaignId FROM "saga_memories" WHERE "Id" = $id;""";

            _ = command.Parameters.AddWithValue("$id", memoryId);

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(CancellationToken.None);

            Assert.True(await reader.ReadAsync(CancellationToken.None));

            return (
                (SagaMemoryScopeKind)reader.GetInt32(0),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)));

        }

        internal Task<int> UnclassifiedCountAsync() =>
            ScalarAsync("""SELECT COUNT(*) FROM "saga_memories" WHERE "ScopeKindCode" = 0;""");

        internal async Task<string> ReadLexiconScopeAsync(string nameNormalized)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText =
                "SELECT ScopeCampaignId FROM lexicon_entries WHERE NameNormalized = $name LIMIT 1;";

            _ = command.Parameters.AddWithValue("$name", nameNormalized);

            return (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;

        }

        internal Task<int> LexiconCountAsync(string nameNormalized) =>
            ScalarAsync(
                "SELECT COUNT(*) FROM lexicon_entries WHERE NameNormalized = $name;",
                ("$name", nameNormalized));

        internal Task<int> RecordedVersionAsync() =>
            ScalarAsync("SELECT SchemaVersion FROM grimoire_feature_schemas WHERE TransactionTierCode = 0;");

        public async ValueTask DisposeAsync()
        {

            await _services.DisposeAsync();

            await _connection.DisposeAsync();

            _file.Dispose();

        }

        private static string Timestamp =>
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture);

        private async Task<Guid> SeedSessionAsync(Guid? campaignId)
        {

            Guid sessionId = await InsertSessionRowAsync(campaignId);

            await ConvergeVersionOneAsync();

            return sessionId;

        }

        private async Task<Guid> InsertSessionRowAsync(Guid? campaignId)
        {

            Guid sessionId = Guid.NewGuid();

            await ExecuteAsync(
                """
                INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
                VALUES ($id, $campaignId, 'active', $now, $now);
                """,
                ("$id", sessionId.ToString()),
                ("$campaignId", campaignId?.ToString()),
                ("$now", Timestamp));

            return sessionId;

        }

        /// <summary>
        /// A Session on a real installation never stays unbound: the Core data initializer writes its
        /// binding at the next convergence, and convergence runs at every start. Running it here rather
        /// than writing the binding row directly keeps the derivation from the legacy Campaign column in
        /// the one place that owns it.
        /// </summary>
        private Task<GrimoireSchemaInstallResult> ConvergeVersionOneAsync() =>
            GrimoireSchemaTestInstaller.InstallAsync(
                _connection,
                CoreSchemaVersionOneFixture.ChainSet(),
                1536,
                CancellationToken.None);

        private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

            foreach ((string name, object? value) in parameters)
            {

                _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

            }

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        private async Task<int> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

            foreach ((string name, object? value) in parameters)
            {

                _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

            }

            return Convert.ToInt32(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture);

        }

    }

    /// <summary>The one open connection the harness has, which a request scope would otherwise supply.</summary>
    private sealed class FixedCoreConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);

    }

}
