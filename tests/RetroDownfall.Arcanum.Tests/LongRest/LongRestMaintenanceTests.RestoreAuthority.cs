using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Backup;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(BackupRestoreConflictMode.ReplaceInstallation)]
    [InlineData(BackupRestoreConflictMode.NewProfileRoot)]
    public async Task Full_restore_without_covenant_staging_sanitizes_terminal_paid_and_linked_empty_long_rest_authority(
        BackupRestoreConflictMode mode)
    {
        await using MaintenanceFixture source = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        RestoreAuthoritySeed seed = await AddLinkedEmptyRestoreGenerationAsync(source);

        await using MemoryErasureRestoreHarness archived = await MemoryErasureRestoreHarness.CreateAsync();

        await SnapshotPaidRestoreInstallationAsync(source, archived);

        string archive = await archived.CreateArchiveAsync("long-rest-authority.arcbackup");

        await using MaintenanceFixture current = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await using MemoryErasureRestoreHarness destination = await MemoryErasureRestoreHarness.CreateAsync();

        await SnapshotPaidRestoreInstallationAsync(current, destination);

        string currentBytes = await destination.LiveDatabaseDigestAsync();

        string sentinel = Path.Combine(destination.InstallationRoot, "CODEX.md");

        await File.WriteAllTextAsync(sentinel, "Current installation remains its own profile.");

        BackupRestoreServiceTests.RecordingSecretStore secrets = RestoreAuthoritySecrets();

        string? currentApiKey = secrets.ApiKey;

        string? currentFileKey = secrets.FileEncryptionSecret;

        string sideRoot = Path.Combine(destination.Profile.TempHome, "restored-profile");

        BackupRestoreResult result = await destination.CreateRestoreService(secrets: secrets,
            options: new BackupRestoreServiceOptions { EmbeddingDimensions = 64 }, covenantStaging: false)
            .RestoreAsync(new(archive, mode, mode == BackupRestoreConflictMode.NewProfileRoot ? sideRoot : null,
                Confirmed: true, CreateSafetyBackup: false), MemoryErasureRestoreHarness.Passphrase.AsMemory(), CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        string database = mode == BackupRestoreConflictMode.NewProfileRoot
            ? Path.Combine(sideRoot, "arcanum.db") : destination.DatabasePath;

        await using (SqliteConnection restored = await BackupRestoreDatabaseWorker.OpenAsync(database,
            GrimoireFixture.TestGrimoireSecret, readOnly: true, CancellationToken.None))
        {
            Assert.Equal(seed.Dataset, await RestoreAuthorityTextAsync(restored, "SELECT hex(DatasetGeneration) FROM covenant_state;"));

            Assert.Equal(seed.Canonical, await RestoreAuthorityRowsAsync(restored, RestoreCanonicalTables));

            Assert.Equal(seed.Billing, await RestoreAuthorityRowsAsync(restored, RestoreBillingTables));

            Assert.Equal(seed.ClaimOrigins, await RestoreAuthorityQueryRowsAsync(restored, RestoreClaimOrigins));

            Assert.Equal(seed.TerminalOperation, await RestoreAuthorityQueryRowsAsync(restored,
                "SELECT * FROM LongRunningOperations WHERE Id = $id;", ("$id", source.Operation.ToString("N"))));

            Assert.Equal(0L, await RestoreAuthorityCountAsync(restored,
                "SELECT count(*) FROM pragma_foreign_key_check;"));

            // A retained linked historical operation is allowed, but its foreign live lease is not.
            Assert.Equal(0L, await RestoreAuthorityCountAsync(restored,
                "SELECT count(*) FROM LongRunningOperations WHERE Id = $id AND (LeaseOwner IS NOT NULL OR LeaseExpiresAt IS NOT NULL);",
                ("$id", seed.ChildOperation.ToString("N"))));

            await AssertImportedRestoreAuthorityIneligibleAsync(restored);
        }

        await using (var restoredContext = destination.Profile.Grimoire!.CreateContext(database))
        {
            ILongRestSweepStore sweeps = new LongRestSweepStore(restoredContext, TimeProvider.System);

            Assert.Null(LongRestSweepHarness.Require(await sweeps.FindCompletedReplayAsync(
                seed.Completed.Fence.CanonicalScope, seed.Completed.Scope, seed.Completed.Recipe, CancellationToken.None)));

            ILongRestDiscoveryStore discovery = new LongRestDiscoveryStore(restoredContext, TimeProvider.System);

            foreach (Guid operation in new[] { source.Operation, seed.ChildOperation })
            {
                Result<LongRestDiscoveryGeneration?> metadata = await discovery.ReadGenerationMetadataAsync(operation, CancellationToken.None);

                if (metadata.IsFailure)
                {
                    Assert.Equal(ErrorCodes.LongRest.StaleInput, metadata.Error.Code);
                }
                else
                {
                    Assert.True(metadata.Value is null || metadata.Value.Phase == LongRestSweepPhase.Invalidated,
                        "An imported operation still owns an eligible generation.");
                }
            }
        }

        if (mode == BackupRestoreConflictMode.NewProfileRoot)
        {
            Assert.Equal(currentBytes, await destination.LiveDatabaseDigestAsync());

            Assert.Equal("Current installation remains its own profile.", await File.ReadAllTextAsync(sentinel));

            Assert.Equal(GrimoireFixture.TestGrimoireSecret, secrets.GrimoireSecret);

            Assert.Equal(currentApiKey, secrets.ApiKey);

            Assert.Equal(currentFileKey, secrets.FileEncryptionSecret);
        }
    }

    [Fact]
    public async Task Selective_restore_omits_source_long_rest_and_claim_authority_and_preserves_destination_paid_graph()
    {
        await using MaintenanceFixture source = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        RestoreAuthoritySeed seed = await AddLinkedEmptyRestoreGenerationAsync(source);

        await using MemoryErasureRestoreHarness archived = await MemoryErasureRestoreHarness.CreateAsync();

        await SnapshotPaidRestoreInstallationAsync(source, archived);

        string archive = await archived.CreateArchiveAsync("selected-long-rest-session.arcbackup");

        await using MaintenanceFixture current = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await using MemoryErasureRestoreHarness destination = await MemoryErasureRestoreHarness.CreateAsync();

        await SnapshotPaidRestoreInstallationAsync(current, destination);

        string[] before;

        string dataset;

        await using (SqliteConnection live = await destination.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            before = await RestoreAuthorityRowsAsync(live, PaidGenerationTables.Concat(RestoreCanonicalTables)
                .Concat(RestoreBillingTables).Concat(["LongRunningOperations", "session_turn_claims"]));

            dataset = await RestoreAuthorityTextAsync(live, "SELECT hex(DatasetGeneration) FROM covenant_state;");

            Assert.Equal(0L, await RestoreAuthorityCountAsync(live, "SELECT count(*) FROM Sessions WHERE Id = $id;",
                ("$id", source.Claim.Claim.SessionId.ToString("D").ToUpperInvariant())));
        }

        BackupRestoreResult result = await destination.CreateRestoreService(secrets: RestoreAuthoritySecrets(),
            options: new BackupRestoreServiceOptions { EmbeddingDimensions = 64 }, covenantStaging: false)
            .RestoreAsync(new(archive, BackupRestoreConflictMode.ImportSelectedSessions,
                SessionIds: [source.Claim.Claim.SessionId], Confirmed: true, CreateSafetyBackup: false),
                MemoryErasureRestoreHarness.Passphrase.AsMemory(), CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        await using SqliteConnection restored = await destination.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        Assert.Equal(1L, await RestoreAuthorityCountAsync(restored,
            "SELECT count(*) FROM Sessions WHERE Id = $id AND CampaignId IS NULL;",
            ("$id", source.Claim.Claim.SessionId.ToString("D").ToUpperInvariant())));

        Assert.Equal(dataset, await RestoreAuthorityTextAsync(restored, "SELECT hex(DatasetGeneration) FROM covenant_state;"));

        Assert.Equal(before, await RestoreAuthorityRowsAsync(restored, PaidGenerationTables.Concat(RestoreCanonicalTables)
            .Concat(RestoreBillingTables).Concat(["LongRunningOperations", "session_turn_claims"])));

        Assert.Equal(0L, await RestoreAuthorityCountAsync(restored,
            "SELECT count(*) FROM LongRunningOperations WHERE Id IN ($root, $child);",
            ("$root", source.Operation.ToString("N")), ("$child", seed.ChildOperation.ToString("N"))));

        Assert.Equal(0L, await RestoreAuthorityCountAsync(restored,
            "SELECT count(*) FROM session_turn_claims WHERE ClaimId = $id;",
            ("$id", source.Claim.Claim.ClaimId.ToString("D").ToUpperInvariant())));

        foreach (string attempt in seed.AttemptIds)
        {
            Assert.Equal(0L, await RestoreAuthorityCountAsync(restored,
                "SELECT count(*) FROM long_rest_provider_effects WHERE AttemptId = $id;", ("$id", attempt)));
        }

        Assert.Equal(0L, await RestoreAuthorityCountAsync(restored, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    private static IEnumerable<string> RestoreCanonicalTables =>
        PaidSagaTables.Concat(PaidReceiptTables).Append("long_rest_sources");

    private static readonly string[] RestoreBillingTables = ["BillableOperations", "InferenceRuns", "external_disclosure_receipts"];

    private const string RestoreClaimOrigins =
        "SELECT ClaimId, OriginInstallationId, OriginRestoreEpoch, ClientTurnId, SessionId, SurfaceCode, RequestDigest, DependencyDigest FROM session_turn_claims;";

    private static async Task<RestoreAuthoritySeed> AddLinkedEmptyRestoreGenerationAsync(MaintenanceFixture f)
    {
        ILongRestDiscoveryStore discovery = f.H.Store;

        LongRestDiscoveryGeneration completed = LongRestSweepHarness.Require(
            await discovery.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        Assert.True(completed.Published);

        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, TimeProvider.System)
            .FindCompletedReplayAsync(completed.Fence.CanonicalScope, completed.Scope, completed.Recipe, CancellationToken.None)));

        LongRunningOperation terminal = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Completed, terminal.State);

        Assert.NotNull(terminal.CheckpointPayload);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation child = await f.Operations.CreateAsync(new(LongRunningOperationKinds.LongRestSweep,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint, "Linked imported empty sweep", now,
            RootOperationId: f.Operation, ParentOperationId: f.Operation, SessionId: f.Claim.Claim.SessionId));

        const string owner = "archived-empty-sweep-worker";

        Assert.True((await f.Operations.TryAcquireLeaseAsync(child.Id, owner, now, now.AddMinutes(5))).Acquired);

        LongRestDiscoveryGeneration empty = LongRestSweepHarness.Require(await discovery.StartOrResumeAsync(
            child.Id, completed.Scope, completed.Fence, completed.Recipe, CancellationToken.None));

        LongRestSweep sweep = LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, TimeProvider.System)
            .BeginOrResumeAsync(child.Id, completed.Scope, completed.Fence.Configuration, CancellationToken.None));

        Assert.Equal(empty.GenerationId, sweep.GenerationId);

        Assert.Equal(LongRestSweepPhase.Capture, empty.Phase);

        Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_discovery_leaves", "GenerationId = $id",
            ("$id", empty.GenerationId.ToString("D").ToUpperInvariant())));

        LongRestSweepCheckpoint checkpoint = new(1, child.Id, sweep.Scope.Kind, sweep.Scope.CampaignId,
            sweep.GenerationId, sweep.Phase, sweep.Revision, sweep.CommittedItemCursor, sweep.CommittedWorkDigest,
            sweep.Scope.ProtectedPartitionDigest);

        Assert.True(await f.Operations.SaveCheckpointAsync(child.Id, owner, 0, 1,
            LongRestSweepCheckpointCodec.Encode(checkpoint).Value, null, "Linked empty Long Rest", now));

        LongRunningOperation linked = (await f.Operations.GetAsync(child.Id))!;

        Assert.Equal(f.Operation, linked.RootOperationId);

        Assert.Equal(f.Operation, linked.ParentOperationId);

        Assert.Equal(LongRunningOperationState.Running, linked.State);

        Assert.NotNull(linked.CheckpointPayload);

        Assert.Equal(owner, linked.LeaseOwner);

        string[] attempts = await RestoreAuthorityStringsAsync(f.H.Connection,
            "SELECT AttemptId FROM long_rest_provider_effects ORDER BY AttemptId;");

        Assert.NotEmpty(attempts);

        string dataset = await RestoreAuthorityTextAsync(f.H.Connection, "SELECT hex(DatasetGeneration) FROM covenant_state;");

        Assert.Equal(32, dataset.Length);

        string[] claims = await RestoreAuthorityQueryRowsAsync(f.H.Connection, RestoreClaimOrigins);

        Assert.NotEmpty(claims);

        return new(child.Id, completed, dataset, attempts,
            await RestoreAuthorityRowsAsync(f.H.Connection, RestoreCanonicalTables),
            await RestoreAuthorityRowsAsync(f.H.Connection, RestoreBillingTables), claims,
            await RestoreAuthorityQueryRowsAsync(f.H.Connection,
                "SELECT * FROM LongRunningOperations WHERE Id = $id;", ("$id", f.Operation.ToString("N"))));
    }

    private static async Task SnapshotPaidRestoreInstallationAsync(MaintenanceFixture f, MemoryErasureRestoreHarness profile)
    {
        // The database is encrypted and snapshot through the product's online backup primitive.
        // No accepted effect, checkpoint, claim or executable capability is copied by SQL.
        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(profile.InstallationRoot);

        await new BackupDatabaseSnapshotter().CreateAsync(f.H.Connection.DataSource, profile.DatabasePath,
            new SqliteConnectionStringBuilder(f.H.Connection.ConnectionString).Password, CancellationToken.None);

        GrimoireKdfSidecarFile.Write(profile.DatabasePath, GrimoireKdfSidecarFile.Read(f.H.Connection.DataSource));
    }

    private static BackupRestoreServiceTests.RecordingSecretStore RestoreAuthoritySecrets() => new()
    {
        GrimoireSecret = GrimoireFixture.TestGrimoireSecret,
        ApiKey = "isolated-current-restore-key",
        FileEncryptionSecret = Convert.ToBase64String(new byte[32]),
    };

    private static async Task AssertImportedRestoreAuthorityIneligibleAsync(SqliteConnection connection)
    {
        foreach (string table in PaidGenerationTables.Where(table => table is not "long_rest_discovery_generations" and not "long_rest_sweeps"))
        {
            Assert.Equal(0L, await RestoreAuthorityCountAsync(connection, $"SELECT count(*) FROM {table};"));
        }

        // Either deletion or the existing durable invalidation grammar is sufficient. A retained
        // completed/active/published root or non-invalidated sweep can still grant archived replay.
        Assert.Equal(0L, await RestoreAuthorityCountAsync(connection,
            "SELECT count(*) FROM long_rest_discovery_generations WHERE PhaseCode <> 7 OR Published <> 0;"));

        Assert.Equal(0L, await RestoreAuthorityCountAsync(connection,
            "SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode <> 7;"));

        Assert.Equal(0L, await RestoreAuthorityCountAsync(connection,
            "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16;"));
    }

    private static async Task<long> RestoreAuthorityCountAsync(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> RestoreAuthorityTextAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> RestoreAuthorityStringsAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> rows = [];

        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));

        return rows.ToArray();
    }

    private static async Task<string[]> RestoreAuthorityRowsAsync(SqliteConnection connection, IEnumerable<string> tables)
    {
        List<string> rows = [];

        foreach (string table in tables.Distinct(StringComparer.Ordinal))
        {
            rows.AddRange((await RestoreAuthorityQueryRowsAsync(connection, $"SELECT * FROM \"{table}\";"))
                .Select(row => table + ":" + row));
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> RestoreAuthorityQueryRowsAsync(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> rows = [];

        while (await reader.ReadAsync())
        {
            List<string> values = [];

            for (int column = 0; column < reader.FieldCount; column++)
            {
                values.Add(reader.GetValue(column) switch
                {
                    DBNull => "null",
                    byte[] bytes => "blob:" + Convert.ToHexString(bytes),
                    string text => "text:" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)),
                    long integer => "integer:" + integer.ToString(CultureInfo.InvariantCulture),
                    double number => "real:" + number.ToString("R", CultureInfo.InvariantCulture),
                    _ => throw new InvalidDataException("Unexpected SQLite field type."),
                });
            }

            rows.Add(string.Join("|", values));
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed record RestoreAuthoritySeed(Guid ChildOperation, LongRestDiscoveryGeneration Completed,
        string Dataset, string[] AttemptIds, string[] Canonical, string[] Billing, string[] ClaimOrigins,
        string[] TerminalOperation);
}
