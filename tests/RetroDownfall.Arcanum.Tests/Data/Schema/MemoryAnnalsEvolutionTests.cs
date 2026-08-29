using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
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
/// The upgrade that gives every existing durable memory a claim, driven the way a host reaches it:
/// install version 2, then hand the same installer the shipped chain and let the shipped driver drain
/// the sweep.
/// </summary>
/// <remarks>
/// The rows this suite seeds are the one thing no writer can produce: a memory that predates the Annals.
/// Everything the suite <i>asserts</i> — the claim each row receives, the origin it records, and the
/// scope it keeps — is produced by production code from that legacy state.
/// </remarks>
public sealed class MemoryAnnalsEvolutionTests
{

    private static readonly Guid CampaignA = new("A0000000-0000-4000-8000-000000000001");

    static MemoryAnnalsEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// The pin is a literal captured before the version-2 tree was edited, and nothing can recompute it
    /// from a tree that no longer exists. A wrong pin means every version-2 installation refuses the
    /// upgrade with <c>SourceDefinitionMismatch</c>, so it has to fail here instead.
    /// </summary>
    [Fact]
    public void The_shipped_chain_pins_the_fingerprint_the_version_two_tree_published()
    {

        GrimoireSchemaVersionChain core =
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.Equal(CoreSchemaVersionTwoFixture.Fingerprint, core.SourceDefinitionFingerprintFor(2));

    }

    [Fact]
    public async Task Every_existing_saga_memory_and_lexicon_entry_receives_exactly_one_claim()
    {

        await using AnnalsUpgradeHarness harness = await AnnalsUpgradeHarness.StartAsync();

        string memory = await harness.SeedMemoryAsync("a conclusion", SagaMemoryScopeKind.Global);

        string entry = await harness.SeedLexiconEntryAsync("config");

        await harness.UpgradeAsync();

        ClaimRow saga = await harness.ReadClaimAsync(AnnalSubjectStore.Saga, memory);

        Assert.Equal(1, saga.Revision);

        Assert.Equal(AnnalOperation.Assert, saga.Operation);

        ClaimRow lexicon = await harness.ReadClaimAsync(AnnalSubjectStore.Lexicon, entry);

        Assert.Equal(1, lexicon.Revision);

        Assert.Equal(2, await harness.ClaimCountAsync());

        Assert.Equal(2, await harness.HeadCountAsync());

    }

    /// <summary>
    /// A backfilled version is evidence of an upgrade, not of an assertion. Nobody attested it, so it
    /// names no Session, and a later curation surface has to be able to say so.
    /// </summary>
    [Fact]
    public async Task A_backfilled_claim_records_that_nobody_attested_it()
    {

        await using AnnalsUpgradeHarness harness = await AnnalsUpgradeHarness.StartAsync();

        string memory = await harness.SeedMemoryAsync("a conclusion", SagaMemoryScopeKind.Global);

        await harness.UpgradeAsync();

        ClaimRow claim = await harness.ReadClaimAsync(AnnalSubjectStore.Saga, memory);

        Assert.Equal(AnnalOrigin.SystemBackfilled, claim.Origin);

        Assert.Null(claim.SourceSessionId);

    }

    /// <summary>
    /// Transaction time is when Arcanum actually first held the claim. Stamping the upgrade's clock on a
    /// six-month-old memory would make it useless for the historical questions it exists to answer.
    /// </summary>
    [Fact]
    public async Task A_backfilled_version_is_recorded_at_the_memorys_own_timestamp_and_not_the_sweeps()
    {

        await using AnnalsUpgradeHarness harness = await AnnalsUpgradeHarness.StartAsync();

        string memory = await harness.SeedMemoryAsync("an old conclusion", SagaMemoryScopeKind.Global);

        await harness.UpgradeAsync();

        ClaimRow claim = await harness.ReadClaimAsync(AnnalSubjectStore.Saga, memory);

        Assert.Equal(AnnalsUpgradeHarness.SeededTimestamp, claim.RecordedAtUtc);

        Assert.Equal(AnnalsUpgradeHarness.SeededTimestamp, claim.ValidFromUtc);

        Assert.Null(claim.ValidToUtc);

    }

    /// <summary>
    /// The contract line the whole sweep exists for: ambiguous legacy ownership never becomes
    /// installation-global authority, because a global claim is retrievable inside every Campaign.
    /// </summary>
    [Theory]
    [InlineData(SagaMemoryScopeKind.Unclassified)]
    [InlineData(SagaMemoryScopeKind.LegacyUnresolved)]
    public async Task An_unresolved_saga_memory_is_never_laundered_into_global_authority(
        SagaMemoryScopeKind seeded)
    {

        await using AnnalsUpgradeHarness harness = await AnnalsUpgradeHarness.StartAsync();

        string memory = await harness.SeedMemoryAsync("an ambiguous conclusion", seeded);

        await harness.UpgradeAsync();

        ClaimRow claim = await harness.ReadClaimAsync(AnnalSubjectStore.Saga, memory);

        Assert.Equal(seeded, claim.ScopeKind);

        Assert.NotEqual(SagaMemoryScopeKind.Global, claim.ScopeKind);

        Assert.Null(claim.CampaignId);

    }

    [Fact]
    public async Task A_campaign_scoped_saga_memory_keeps_its_campaign()
    {

        await using AnnalsUpgradeHarness harness = await AnnalsUpgradeHarness.StartAsync();

        string memory = await harness.SeedMemoryAsync(
            "a campaign conclusion",
            SagaMemoryScopeKind.Campaign,
            CampaignA.ToString());

        await harness.UpgradeAsync();

        ClaimRow claim = await harness.ReadClaimAsync(AnnalSubjectStore.Saga, memory);

        Assert.Equal(SagaMemoryScopeKind.Campaign, claim.ScopeKind);

        Assert.Equal(CampaignA.ToString(), claim.CampaignId);

    }

    /// <summary>
    /// The Lexicon's empty-string scope is the global tier rather than an absent one, because the column
    /// is NOT NULL DEFAULT '' and every row has always had an unambiguous tier.
    /// </summary>
    [Fact]
    public async Task A_global_lexicon_entry_is_claimed_global_and_a_campaign_scoped_one_names_its_campaign()
    {

        await using AnnalsUpgradeHarness harness = await AnnalsUpgradeHarness.StartAsync();

        string global = await harness.SeedLexiconEntryAsync("config");

        string scoped = await harness.SeedLexiconEntryAsync("config", CampaignA.ToString());

        await harness.UpgradeAsync();

        ClaimRow globalClaim = await harness.ReadClaimAsync(AnnalSubjectStore.Lexicon, global);

        Assert.Equal(SagaMemoryScopeKind.Global, globalClaim.ScopeKind);

        Assert.Null(globalClaim.CampaignId);

        ClaimRow scopedClaim = await harness.ReadClaimAsync(AnnalSubjectStore.Lexicon, scoped);

        Assert.Equal(SagaMemoryScopeKind.Campaign, scopedClaim.ScopeKind);

        Assert.Equal(CampaignA.ToString(), scopedClaim.CampaignId);

    }

    /// <summary>
    /// Interruption is the ordinary case for a sweep this size, so one batch at a time must reach the
    /// same state as one uninterrupted drain, and a drain over an already-claimed corpus must add
    /// nothing.
    /// </summary>
    [Fact]
    public async Task The_backfill_is_idempotent_and_safe_to_interrupt()
    {

        await using AnnalsUpgradeHarness harness = await AnnalsUpgradeHarness.StartAsync();

        for (int index = 0; index < 60; index++)
        {

            _ = await harness.SeedMemoryAsync($"conclusion {index}", SagaMemoryScopeKind.Global);

            _ = await harness.SeedLexiconEntryAsync($"entity-{index}");

        }

        await harness.UpgradeInOnePassAtATimeAsync();

        Assert.Equal(120, await harness.ClaimCountAsync());

        Assert.Equal(120, await harness.VersionCountAsync());

        Assert.Equal(120, await harness.HeadCountAsync());

        // A finished run leaves nothing for a later pass to claim, which is what makes an interrupted
        // upgrade safe to restart.
        Assert.False((await harness.RunOnePassAsync()).Value.Advanced);

        Assert.Equal(120, await harness.ClaimCountAsync());

    }

    /// <summary>
    /// A sweep that has not drained must not let the tier advertise the version whose work it promises.
    /// </summary>
    [Fact]
    public async Task An_interrupted_upgrade_leaves_the_tier_below_head_until_the_sweep_drains()
    {

        await using AnnalsUpgradeHarness harness = await AnnalsUpgradeHarness.StartAsync();

        _ = await harness.SeedMemoryAsync("a conclusion", SagaMemoryScopeKind.Global);

        GrimoireSchemaInstallResult installed = await harness.InstallShippedChainAsync();

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, installed.Core.Health);

        Assert.Equal(2, await harness.RecordedVersionAsync());

        await harness.DrainAsync();

        Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, await harness.RecordedVersionAsync());

    }

    /// <summary>One claim as production wrote it, joined across the three tables that hold it.</summary>
    private sealed record ClaimRow(
        string ClaimId,
        string VersionId,
        int Revision,
        AnnalOperation Operation,
        AnnalOrigin Origin,
        SagaMemoryScopeKind ScopeKind,
        string? CampaignId,
        DateTimeOffset ValidFromUtc,
        DateTimeOffset? ValidToUtc,
        DateTimeOffset RecordedAtUtc,
        string? SourceSessionId);

    /// <summary>
    /// One open scratch installation that starts at version 2 and is upgraded through the shipped chain.
    /// </summary>
    private sealed class AnnalsUpgradeHarness : IAsyncDisposable
    {

        /// <summary>The timestamp every seeded row carries, so an assertion can name it exactly.</summary>
        internal static readonly DateTimeOffset SeededTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly EvolutionScratchDatabase _file;

        private readonly SqliteConnection _connection;

        private readonly ServiceProvider _services;

        private readonly GrimoireSchemaTransitionCoordinator _coordinator;

        private AnnalsUpgradeHarness(
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

        internal static async Task<AnnalsUpgradeHarness> StartAsync()
        {

            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CoreSchemaVersionTwoFixture.ChainSet(),
                1536,
                CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

            Assert.Equal(2, installed.Core.SchemaVersion);

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

            return new AnnalsUpgradeHarness(file, connection, services, coordinator);

        }

        internal Task<GrimoireSchemaInstallResult> InstallShippedChainAsync() =>
            GrimoireSchemaTestInstaller.InstallAsync(
                _connection,
                GrimoireSchemaVersionChains.Default,
                1536,
                CancellationToken.None);

        internal Task<Result<GrimoireSchemaTransitionPassOutcome>> RunOnePassAsync() =>
            _coordinator.RunOnceAsync(CancellationToken.None);

        internal async Task DrainAsync()
        {

            while ((await RunOnePassAsync()).Value.Advanced)
            {

            }

        }

        internal async Task UpgradeAsync()
        {

            _ = await InstallShippedChainAsync();

            await DrainAsync();

            Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, await RecordedVersionAsync());

        }

        /// <summary>The same upgrade, one bounded pass at a time, which is what an interrupted host does.</summary>
        internal async Task UpgradeInOnePassAtATimeAsync()
        {

            _ = await InstallShippedChainAsync();

            for (int pass = 0; pass < 400; pass++)
            {

                if (!(await RunOnePassAsync()).Value.Advanced)
                {

                    break;

                }

            }

            Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, await RecordedVersionAsync());

        }

        /// <summary>A version-2 Saga row, carrying the scope classification version 2 gave it.</summary>
        internal async Task<string> SeedMemoryAsync(
            string content,
            SagaMemoryScopeKind scopeKind,
            string? campaignId = null)
        {

            string id = Guid.NewGuid().ToString();

            await ExecuteAsync(
                """
                INSERT INTO "saga_memories"
                    ("Id", "Content", "CreatedAt", "SessionId", "Tags", "Source", ScopeKindCode, CampaignId)
                VALUES ($id, $content, $now, NULL, NULL, 'test', $scopeKindCode, $campaignId);
                """,
                ("$id", id),
                ("$content", content),
                ("$now", Timestamp),
                ("$scopeKindCode", (int)scopeKind),
                ("$campaignId", campaignId));

            return id;

        }

        /// <summary>A version-2 Lexicon row, carrying the scope column version 2 gave it.</summary>
        internal async Task<string> SeedLexiconEntryAsync(string nameNormalized, string? scopeCampaignId = null)
        {

            string id = Guid.NewGuid().ToString();

            await ExecuteAsync(
                """
                INSERT INTO lexicon_entries
                    (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt, ScopeCampaignId)
                VALUES ($id, $name, $name, 'Concept', '[]', 'a fact', $now, $scope);
                """,
                ("$id", id),
                ("$name", nameNormalized),
                ("$now", Timestamp),
                ("$scope", scopeCampaignId ?? string.Empty));

            return id;

        }

        internal async Task<ClaimRow> ReadClaimAsync(AnnalSubjectStore subjectStore, string subjectId)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText =
                """
                SELECT claim.ClaimId, head.CurrentVersionId, version.Revision, version.OperationCode,
                       version.OriginCode, version.ScopeKindCode, version.CampaignId,
                       version.ValidFromUtc, version.ValidToUtc, version.RecordedAtUtc, version.SourceSessionId
                FROM annal_claims AS claim
                JOIN annal_heads AS head ON head.ClaimId = claim.ClaimId
                JOIN annal_versions AS version ON version.VersionId = head.CurrentVersionId
                WHERE claim.SubjectStoreCode = $storeCode AND claim.SubjectId = $subjectId;
                """;

            _ = command.Parameters.AddWithValue("$storeCode", (int)subjectStore);

            _ = command.Parameters.AddWithValue("$subjectId", subjectId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

            Assert.True(await reader.ReadAsync(CancellationToken.None), $"no claim for {subjectStore} {subjectId}");

            ClaimRow row = new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                (AnnalOperation)reader.GetInt32(3),
                (AnnalOrigin)reader.GetInt32(4),
                (SagaMemoryScopeKind)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
                Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10));

            Assert.False(await reader.ReadAsync(CancellationToken.None), "a durable row carried more than one claim");

            return row;

        }

        internal Task<int> ClaimCountAsync() => ScalarAsync("SELECT COUNT(*) FROM annal_claims;");

        internal Task<int> VersionCountAsync() => ScalarAsync("SELECT COUNT(*) FROM annal_versions;");

        internal Task<int> HeadCountAsync() => ScalarAsync("SELECT COUNT(*) FROM annal_heads;");

        internal Task<int> RecordedVersionAsync() =>
            ScalarAsync("SELECT SchemaVersion FROM grimoire_feature_schemas WHERE TransactionTierCode = 0;");

        public async ValueTask DisposeAsync()
        {

            await _services.DisposeAsync();

            await _connection.DisposeAsync();

            _file.Dispose();

        }

        private static string Timestamp => SeededTimestamp.ToString("o", CultureInfo.InvariantCulture);

        private static DateTimeOffset Parse(string value) =>
            DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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

        private async Task<int> ScalarAsync(string sql)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

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
