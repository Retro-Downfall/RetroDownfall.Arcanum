using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

[Collection("Grimoire")]

[Trait("Category", "Integration")]
public sealed class CovenantErasureStartupRecoveryOwnerAdopterTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SqliteConnection Connection =>
        (SqliteConnection)_db!.Database.GetDbConnection();

    public CovenantErasureStartupRecoveryOwnerAdopterTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public async Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        await _db.Database.OpenConnectionAsync();

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection = Connection;

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    public static TheoryData<string, CovenantResetPhase> CurrentCheckpointCases()
    {

        TheoryData<string, CovenantResetPhase> cases = [];

        foreach (CovenantResetPhase phase in CovenantResetPhaseMachine.Ordered)
        {

            cases.Add(LongRunningOperationKinds.DataRetentionMutation, phase);

            cases.Add(LongRunningOperationKinds.DataRetentionFactoryReset, phase);

        }

        return cases;

    }

    [SkippableTheory]

    [MemberData(nameof(CurrentCheckpointCases))]
    public async Task Every_current_phase_adopts_its_exact_owner_before_readiness(
        string kind,
        CovenantResetPhase phase)
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner expected = await SeedCurrentAsync(kind, phase);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantErasureStartupRecoveryOwnerAdopter adopter = new(gate);

        Result<CovenantExclusiveRecoveryOwner?> adopted = await adopter.AdoptBeforeReadinessAsync(
            Connection,
            CancellationToken.None);

        Assert.True(adopted.IsSuccess);

        Assert.Equal(expected, adopted.Value);

        await using CovenantExclusiveLease resumed =
            (await gate.ResumeExclusiveAsync(expected, CancellationToken.None)).Value;

        Assert.Equal(expected, resumed.Snapshot.RecoveryOwner);

    }

    [SkippableTheory]

    [InlineData("\"CheckpointPayload\" = NULL")]

    [InlineData("\"CheckpointPayload\" = X''")]

    [InlineData("\"CheckpointPayload\" = 'text payload'")]

    [InlineData("\"CheckpointPayload\" = zeroblob(4097)")]

    [InlineData("\"State\" = 99")]

    [InlineData("\"State\" = 'running'")]

    [InlineData("\"RecoveryPolicy\" = 99")]

    [InlineData("\"RecoveryPolicy\" = 'policy'")]

    [InlineData("\"CheckpointVersion\" = 99")]

    [InlineData("\"CheckpointVersion\" = 'version'")]

    [InlineData("\"Id\" = upper(\"Id\")")]

    [InlineData("\"CheckpointReference\" = upper(\"CheckpointReference\")")]
    public async Task Malformed_current_evidence_refuses_without_installing_an_owner(
        string assignment)
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner owner = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation,
            CovenantResetPhase.InventoryPrepared);

        await ExecuteAsync($"UPDATE \"LongRunningOperations\" SET {assignment};");

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(gate)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.True((await gate.ResumeExclusiveAsync(owner, CancellationToken.None)).IsFailure);

    }

    [SkippableTheory]

    [InlineData(0)]

    [InlineData(1)]

    [InlineData(2)]
    public async Task Malformed_current_payload_identity_refuses_content_free(int malformedPart)
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner owner = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation,
            CovenantResetPhase.InventoryPrepared);

        string digest = malformedPart == 1 ? "xyz" : new string('a', 64);

        CovenantExclusiveOperation operation = malformedPart == 0
            ? CovenantExclusiveOperation.SchemaRepair
            : CovenantExclusiveOperation.CovenantReset;

        byte[] payload = CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionMutationCheckpointV3(
                DataRetentionMutationCheckpointV3.CurrentVersion,
                "reset-memory",
                ((int)MemoryResetScope.Covenant).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                new CovenantResetEffectArmV1(
                    owner.OperationId,
                    digest,
                    operation,
                    CovenantResetPhase.InventoryPrepared)));

        if (malformedPart == 2)
        {

            payload = System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(payload).Replace(
                    "\"phase\":\"InventoryPrepared\"",
                    "\"phase\":\"Unknown\"",
                    StringComparison.Ordinal));

        }

        await ExecuteAsync(
            "UPDATE \"LongRunningOperations\" SET \"CheckpointPayload\" = @payload;",
            ("@payload", payload));

        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.DoesNotContain("xyz", adopted.Error.Message, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task Legacy_and_non_covenant_rows_are_ignored_without_projecting_large_payloads()
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner mutation = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation,
            CovenantResetPhase.InventoryPrepared);

        byte[] ordinary = CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionMutationCheckpointV3(
                DataRetentionMutationCheckpointV3.CurrentVersion,
                "delete-session",
                "target",
                Covenant: null));

        await ExecuteAsync(
            "UPDATE \"LongRunningOperations\" SET \"CheckpointPayload\" = @payload WHERE \"Id\" = @id;",
            ("@payload", ordinary),
            ("@id", mutation.OperationId.ToString("N")));

        CovenantExclusiveRecoveryOwner factory = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionFactoryReset,
            CovenantResetPhase.InventoryPrepared);

        await ExecuteAsync(
            "UPDATE \"LongRunningOperations\" SET \"CheckpointVersion\" = 0, "
                + "\"CheckpointPayload\" = zeroblob(1000000) WHERE \"Id\" = @id;",
            ("@id", factory.OperationId.ToString("N")));

        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsSuccess);

        Assert.Null(adopted.Value);

    }

    [SkippableFact]
    public async Task Pending_pre_covenant_rows_are_ignored_and_do_not_block_readiness()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!);

        _ = await store.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Queued legacy retention mutation.",
                DateTimeOffset.UtcNow));

        _ = await store.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Queued legacy factory reset.",
                DateTimeOffset.UtcNow));

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(gate)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsSuccess);

        Assert.Null(adopted.Value);

        gate.PublishReadiness();

        Result<CovenantInstallationReadLease> read = await gate
            .AcquireInstallationReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess);

        await read.Value.DisposeAsync();

    }

    [SkippableFact]
    public async Task Two_valid_owners_refuse_before_either_is_installed()
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner first = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation,
            CovenantResetPhase.InventoryPrepared);

        CovenantExclusiveRecoveryOwner second = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionFactoryReset,
            CovenantResetPhase.CanonicalApplied);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(gate)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.True((await gate.ResumeExclusiveAsync(first, CancellationToken.None)).IsFailure);

        Assert.True((await gate.ResumeExclusiveAsync(second, CancellationToken.None)).IsFailure);

    }

    [SkippableFact]
    public async Task A_conflicting_or_post_readiness_gate_refuses_adoption()
    {

        RequireSqlCipher();

        _ = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation,
            CovenantResetPhase.InventoryPrepared);

        CovenantOperationGate conflict = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner existing = CovenantOperationGateFixture.Owner(
            CovenantExclusiveOperation.SchemaRepair);

        conflict.AdoptDurableRecoveryOwner(existing, null, cleanupOnlyHistoricalCampaign: false);

        Assert.True((await new CovenantErasureStartupRecoveryOwnerAdopter(conflict)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None)).IsFailure);

        CovenantOperationGate ready = CovenantOperationGateFixture.CreateGate();

        ready.PublishReadiness();

        Assert.True((await new CovenantErasureStartupRecoveryOwnerAdopter(ready)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None)).IsFailure);

    }

    [SkippableFact]
    public async Task Caller_cancellation_is_preserved()
    {

        RequireSqlCipher();

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(Connection, cancelled.Token));

    }

    private async Task<CovenantExclusiveRecoveryOwner> SeedCurrentAsync(
        string kind,
        CovenantResetPhase phase)
    {

        LongRunningOperationStore store = new(_db!);

        LongRunningOperationRecoveryPolicy policy = kind
            == LongRunningOperationKinds.DataRetentionMutation
            ? LongRunningOperationRecoveryPolicy.ReconcileAndComplete
            : LongRunningOperationRecoveryPolicy.RestartIdempotently;

        CovenantExclusiveOperation operation = kind
            == LongRunningOperationKinds.DataRetentionMutation
            ? CovenantExclusiveOperation.CovenantReset
            : CovenantExclusiveOperation.HealthyCatalogFactoryErasure;

        LongRunningOperation created = await store.CreateAsync(
            new LongRunningOperationCreateRequest(kind, policy, "Interrupted Covenant erasure.", DateTimeOffset.UtcNow));

        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            created.Id,
            "startup-adopter-owner",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(leased.Acquired);

        string digest = new('a', 64);

        byte[] payload = kind == LongRunningOperationKinds.DataRetentionMutation
            ? CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionMutationCheckpointV3(
                    DataRetentionMutationCheckpointV3.CurrentVersion,
                    "reset-memory",
                    ((int)MemoryResetScope.Covenant).ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    new CovenantResetEffectArmV1(created.Id, digest, operation, phase)))
            : CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionFactoryResetCheckpointV1(
                    DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                    created.Id,
                    digest,
                    operation,
                    phase));

        int version = kind == LongRunningOperationKinds.DataRetentionMutation
            ? DataRetentionMutationCheckpointV3.CurrentVersion
            : DataRetentionFactoryResetCheckpointV1.CurrentVersion;

        Assert.True(await store.SaveCheckpointAsync(
            created.Id,
            "startup-adopter-owner",
            0,
            version,
            payload,
            CovenantResetCheckpointInitiator.CheckpointReference(kind, created.Id),
            created.PublicSummary,
            DateTimeOffset.UtcNow));

        return new CovenantExclusiveRecoveryOwner(
            created.Id,
            operation,
            new CovenantDigest(Convert.FromHexString(digest)));

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private async Task ExecuteAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        _ = await command.ExecuteNonQueryAsync();

    }

}
