using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildSchemaTests
{
    [Fact]
    public async Task Fresh_install_creates_Guild_content_and_zero_owner_cursors()
    {
        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='guild_content'";

        Assert.True(Equals(1L, await command.ExecuteScalarAsync()), GrimoireSchemaCatalog.CoreSchemaFingerprint);

        command.CommandText = "SELECT AppliedCampaignSequence + AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode=2";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Unrelated_Covenant_drift_does_not_prevent_Guild_installation()
    {
        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "ALTER TABLE covenant_state ADD COLUMN Unexpected INTEGER";

        _ = await command.ExecuteNonQueryAsync();

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.False(result.CovenantCanonical.IsHealthy);

        command.CommandText = "SELECT COUNT(*) FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3 AND SchemaVersion=11 AND HealthCode=0";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Content_is_immutable_and_oversize_body_is_refused_by_database()
    {
        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc) VALUES ('11111111-1111-1111-1111-111111111111',0,'22222222-2222-2222-2222-222222222222',1,zeroblob(32),'application/json',x'7B7D',0,0,'2026-01-01T00:00:00.0000000Z')";

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE guild_content SET Body=x'01' WHERE ContentId='11111111-1111-1111-1111-111111111111'";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        command.CommandText = "INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc) VALUES ('33333333-3333-3333-3333-333333333333',0,'22222222-2222-2222-2222-222222222222',2,zeroblob(32),'application/json',zeroblob(65537),0,0,'2026-01-01T00:00:00.0000000Z')";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Published_Core37_upgrades_through_declared_transition_without_changing_prior_pin()
    {
        Assert.Equal(CoreSchemaVersionThirtySevenFixture.PublishedFingerprint, CoreSchemaVersionThirtySevenFixture.Fingerprint);

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtySevenFixture.ChainSet(), 768, CancellationToken.None);

        Guid oldArtifact = Guid.NewGuid();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            DerivedArtifactWrite old = new(SensitiveArtifactKind.Notification, oldArtifact, null, null, null, 3,
                new CovenantDigest(new byte[32]), ContentSensitivity.CovenantDerived, GenerationProvenance.Create([Guid.NewGuid()]));

            var written = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, old, CancellationToken.None);

            Assert.True(written.IsSuccess, written.Error.Message);

            await transaction.CommitAsync();
        }

        var before = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, null, SensitiveArtifactKind.Notification, oldArtifact, CancellationToken.None);

        GrimoireSchemaInstallResult upgraded = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        var after = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, null, SensitiveArtifactKind.Notification, oldArtifact, CancellationToken.None);

        Assert.Equal(before.Value!.LabelDigest, after.Value!.LabelDigest);

        Assert.True(upgraded.Core.IsHealthy);

        Assert.True(upgraded.GuildCanonical.IsHealthy);

        Assert.Equal(38, upgraded.Core.SchemaVersion);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT sql FROM sqlite_master WHERE name='artifact_sensitivity'";

        Assert.Contains("15, 16, 17", (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Guild_catalog_damage_leaves_Core_and_Covenant_healthy()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "ALTER TABLE guild_content ADD COLUMN Unexpected INTEGER";

        _ = await command.ExecuteNonQueryAsync();

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(installed.Core.IsHealthy);

        Assert.True(installed.CovenantCanonical.IsHealthy);

        Assert.True(installed.CovenantAccelerator.IsHealthy);

        Assert.False(installed.GuildCanonical.IsHealthy);

        Assert.True((await GuildStorageAvailability.CheckAsync(connection, null, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Core_only_offline_bootstrap_defers_optional_Guild_until_full_admission()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create();

        Assert.True((await installer.InstallCoreOnlyAsync(connection, GrimoireSchemaTestInstaller.CreateContext(), CancellationToken.None)).IsHealthy);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='guild_content'";

        Assert.Equal(0L, await command.ExecuteScalarAsync());

        GrimoireSchemaInstallResult full = await installer.InstallAsync(connection, 768, GrimoireSchemaTestInstaller.CreateContext(), CancellationToken.None);

        Assert.True(full.GuildCanonical.IsHealthy);
    }

    [Fact]
    public async Task Restored_Core37_snapshot_admits_the_same_Guild_chain_before_use()
    {
        using EvolutionScratchDatabase source = EvolutionScratchDatabase.Create();

        using EvolutionScratchDatabase staged = EvolutionScratchDatabase.Create();

        await using SqliteConnection original = await source.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(original, CoreSchemaVersionThirtySevenFixture.ChainSet(), 768, CancellationToken.None);

        await using SqliteConnection restored = await staged.OpenAsync(CancellationToken.None);

        original.BackupDatabase(restored);

        GrimoireSchemaInstallResult admitted = await BackupRestoreDatabaseWorker.MigrateAsync(restored,
            GrimoireSchemaTestInstaller.Create(), 768, GrimoireSchemaTestInstaller.CreateContext(), CancellationToken.None);

        Assert.True(admitted.Core.IsHealthy);

        Assert.True(admitted.GuildCanonical.IsHealthy);

        Assert.True((await GuildStorageAvailability.CheckAsync(restored, null, CancellationToken.None)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_in_Guild_initializer_rolls_back_optional_install_without_damage(bool previouslyInstalled)
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        if (previouslyInstalled)
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        using CancellationTokenSource cancellation = new();

        GrimoireSchemaInstaller installer = new(new GrimoireSchemaManifestInspector(GrimoireSchemaTierOwnershipRegistry.CreateDefault()),
            new GrimoireSchemaDataInitializers([new CoreGrimoireSchemaDataInitializer(), new CovenantCanonicalSchemaDataInitializer(),
                new CovenantAcceleratorSchemaDataInitializer(), new CancelGuildInitializer(cancellation)]),
            GrimoireSchemaVersionChains.Default, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(connection, 768,
            GrimoireSchemaTestInstaller.CreateContext(), cancellation.Token));

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='guild_content'";

        Assert.Equal(previouslyInstalled ? 1L : 0L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT COUNT(*) FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(previouslyInstalled ? 1L : 0L, await command.ExecuteScalarAsync());

        if (previouslyInstalled)
        {
            command.CommandText = "SELECT HealthCode FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3";

            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        GrimoireSchemaInstallResult retried = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(retried.Core.IsHealthy);

        Assert.True(retried.CovenantCanonical.IsHealthy);

        Assert.True(retried.GuildCanonical.IsHealthy);
    }

    [Fact]
    public async Task Restore_inconsistent_card_binding_persists_unavailability_until_repaired_readmission()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

            string identity = (string)(await command.ExecuteScalarAsync())!;

            command.CommandText = """
                INSERT INTO guild_agent_cards(CardId,AgentId,SourceKindCode,SourceIdentity,CurrentRevision,ContentId,Available,CreatedAtUtc,UpdatedAtUtc)
                VALUES('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222',0,'invalid-restore',1,
                    '33333333-3333-3333-3333-333333333333',1,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')
                """;

            _ = await command.ExecuteNonQueryAsync();

            GrimoireSchemaInstallResult refused = await BackupRestoreDatabaseWorker.MigrateAsync(connection,
                GrimoireSchemaTestInstaller.Create(), 768, GrimoireSchemaTestInstaller.CreateContext(), CancellationToken.None);

            Assert.True(refused.Core.IsHealthy);

            Assert.True(refused.CovenantCanonical.IsHealthy);

            Assert.False(refused.GuildCanonical.IsHealthy);

            command.CommandText = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

            Assert.Equal(identity, await command.ExecuteScalarAsync());
        }

        await using SqliteConnection fresh = await file.OpenAsync(CancellationToken.None);

        Assert.True((await GuildStorageAvailability.CheckAsync(fresh, null, CancellationToken.None)).IsFailure);

        await using SqliteCommand repair = fresh.CreateCommand();

        repair.CommandText = "DELETE FROM guild_agent_cards WHERE SourceIdentity='invalid-restore'";

        _ = await repair.ExecuteNonQueryAsync();

        Assert.True((await GuildStorageAvailability.CheckAsync(fresh, null, CancellationToken.None)).IsFailure);

        GrimoireSchemaInstallResult admitted = await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, CancellationToken.None);

        Assert.True(admitted.GuildCanonical.IsHealthy);

        Assert.True((await GuildStorageAvailability.CheckAsync(fresh, null, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Caller_cancellation_after_definite_integrity_failure_cannot_skip_durable_unavailability_publication()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        string pins;

        await using (SqliteConnection connection = await database.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                SELECT SchemaVersion || '|' || SourceDefinitionFingerprint || '|' || InstalledCatalogFingerprint || '|' || InstalledAtUtc
                FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3;
                """;

            pins = (string)(await command.ExecuteScalarAsync())!;

            command.CommandText = """
                INSERT INTO guild_agent_cards(CardId,AgentId,SourceKindCode,SourceIdentity,CurrentRevision,ContentId,Available,CreatedAtUtc,UpdatedAtUtc)
                VALUES('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222',0,'cancelled-integrity-publication',1,
                    '33333333-3333-3333-3333-333333333333',1,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
                """;

            await command.ExecuteNonQueryAsync();

            using CancellationTokenSource cancellation = new();

            var initializer = new CancelAfterDefiniteGuildIntegrityFailure(cancellation);

            GrimoireSchemaInstaller installer = new(new GrimoireSchemaManifestInspector(GrimoireSchemaTierOwnershipRegistry.CreateDefault()),
                new GrimoireSchemaDataInitializers([new CoreGrimoireSchemaDataInitializer(), new CovenantCanonicalSchemaDataInitializer(),
                    new CovenantAcceleratorSchemaDataInitializer(), initializer]), GrimoireSchemaVersionChains.Default, TimeProvider.System);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(connection, 768,
                GrimoireSchemaTestInstaller.CreateContext(), cancellation.Token));

            Assert.True(initializer.DefiniteFailureObserved);
        }

        await using SqliteConnection fresh = await database.OpenAsync(CancellationToken.None);

        await using SqliteCommand verified = fresh.CreateCommand();

        verified.CommandText = "SELECT HealthCode FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3";

        Assert.Equal((long)GrimoireSchemaTierHealth.Unavailable, await verified.ExecuteScalarAsync());

        verified.CommandText = "SELECT HealthDetailCode FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3";

        Assert.Equal("Guild.StorageIntegrity", await verified.ExecuteScalarAsync());

        verified.CommandText = """
            SELECT SchemaVersion || '|' || SourceDefinitionFingerprint || '|' || InstalledCatalogFingerprint || '|' || InstalledAtUtc
            FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3;
            """;

        Assert.Equal(pins, await verified.ExecuteScalarAsync());

        verified.CommandText = "SELECT COUNT(*) FROM guild_agent_cards WHERE SourceIdentity='cancelled-integrity-publication'";

        Assert.Equal(1L, await verified.ExecuteScalarAsync());

        Assert.True((await GuildStorageAvailability.CheckAsync(fresh, null, CancellationToken.None)).IsFailure);
    }

    private sealed class CancelAfterDefiniteGuildIntegrityFailure(CancellationTokenSource cancellation) : IGrimoireSchemaDataInitializer
    {
        public GrimoireSchemaTransactionTier TransactionTier => GrimoireSchemaTransactionTier.GuildCanonical;

        internal bool DefiniteFailureObserved { get; private set; }

        public async Task InitializeAsync(SqliteConnection connection, SqliteTransaction transaction,
            GrimoireSchemaInitializationContext context, CancellationToken cancellationToken)
        {
            try
            {
                await new GuildCanonicalSchemaDataInitializer().InitializeAsync(connection, transaction, context,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GuildStorageIntegrityException)
            {
                DefiniteFailureObserved = true;

                cancellation.Cancel();

                throw;
            }
        }
    }

    private sealed class CancelGuildInitializer(CancellationTokenSource cancellation) : IGrimoireSchemaDataInitializer
    {
        public GrimoireSchemaTransactionTier TransactionTier => GrimoireSchemaTransactionTier.GuildCanonical;

        public Task InitializeAsync(SqliteConnection connection, SqliteTransaction transaction, GrimoireSchemaInitializationContext context,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();

            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }
}
