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

using RetroDownfall.Arcanum.Tests.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

[Collection("Grimoire")]

[Trait("Category", "Integration")]
public sealed class CovenantErasureStartupRecoveryOwnerAdopterTests : IAsyncLifetime
{

    /// <summary>
    /// The highest checkpoint version an ordinary retention mutation writes.
    /// </summary>
    /// <remarks>
    /// Mirrors the adopter's own bound rather than deriving it as "one less than the launch version",
    /// because the two are not adjacent: the version between them belonged to the retired same-database
    /// reset checkpoint, and the whole point of the tests below is that the gap is not ordinary ground.
    /// </remarks>
    private const int LastOrdinaryMutationCheckpointVersion = 2;

    private static readonly Guid SourceGeneration =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly Guid TargetGeneration =
        Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SqliteConnection Connection =>
        (SqliteConnection)_db!.Database.GetDbConnection();

    private static CovenantOfflineTransitionEpochsV1 SourceEpochs => new(11, 22, 33);

    private static CovenantOfflineTransitionEpochsV1 TargetEpochs => new(12, 23, 34);

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

    /// <summary>
    /// A committed launch adopts exactly the owner it names, on both durable kinds.
    /// </summary>
    /// <remarks>
    /// The theory no longer runs a phase dimension, because a launch has no phase to run: the row
    /// records what was committed to and the authenticated journal records how far it got. Keeping a
    /// phase parameter here would have meant seeding ten rows that differ in nothing, and a test whose
    /// cases are indistinguishable stops being able to fail for a reason it names.
    ///
    /// <para>Both kinds are exercised because the adopter picks the launch shape from the row's kind.
    /// A build that decoded a factory row with the Covenant reader would still find a well-formed
    /// payload — the two shapes have identical members — and would adopt an owner under the wrong
    /// exclusive operation, which is the one mistake no later step can detect.</para>
    /// </remarks>
    [SkippableTheory]

    [InlineData(LongRunningOperationKinds.DataRetentionMutation)]

    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset)]
    public async Task Every_current_launch_adopts_its_exact_owner_before_readiness(string kind)
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner expected = await SeedCurrentAsync(kind);

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
            LongRunningOperationKinds.DataRetentionMutation);

        await ExecuteAsync($"UPDATE \"LongRunningOperations\" SET {assignment};");

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(gate)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.True((await gate.ResumeExclusiveAsync(owner, CancellationToken.None)).IsFailure);

    }

    /// <summary>
    /// A launch whose own identity fields disagree with the plan is refused, and says nothing.
    /// </summary>
    /// <remarks>
    /// The three cases are the three ways a payload can decode as a launch and still be the wrong
    /// one: an exclusive operation this kind never launches, a digest that is not a canonical effect,
    /// and a target tuple whose members were transposed. The last is the subtle one — every transposed
    /// target is still some source plus one, so a rule that compared the tuples as sets would admit a
    /// launch that later verified a replaced family against the wrong counter.
    ///
    /// <para>The refusal is content-free on purpose. An adoption failure is read by whatever surfaces
    /// startup diagnostics, and echoing the rejected bytes back would let a corrupt row choose what
    /// that surface displays.</para>
    /// </remarks>
    [SkippableTheory]

    [InlineData(0)]

    [InlineData(1)]

    [InlineData(2)]
    public async Task Malformed_current_payload_identity_refuses_content_free(int malformedPart)
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner owner = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation);

        string digest = malformedPart == 1
            ? "xyz"
            : CovenantRecoveryCheckpointCodec.EncodeEffectDigest(owner.EffectDigest);

        CovenantExclusiveOperation operation = malformedPart == 0
            ? CovenantExclusiveOperation.SchemaRepair
            : CovenantExclusiveOperation.CovenantReset;

        CovenantOfflineTransitionEpochsV1 target = malformedPart == 2
            ? new CovenantOfflineTransitionEpochsV1(
                SourceEpochs.KeyReclamationEpoch + 1,
                SourceEpochs.AcceleratorEpoch + 1,
                SourceEpochs.EnvelopeKeyEpoch + 1)
            : TargetEpochs;

        byte[] payload = CovenantRecoveryCheckpointCodec.Encode(
            new CovenantOfflineTransitionLaunchV4(
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                owner.OperationId,
                LongRunningOperationKinds.DataRetentionMutation,
                nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
                operation,
                digest,
                SourceGeneration,
                TargetGeneration,
                SourceEpochs,
                target,
                StartingRevision: 0));

        await ExecuteAsync(
            "UPDATE \"LongRunningOperations\" SET \"CheckpointPayload\" = @payload;",
            ("@payload", payload));

        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.DoesNotContain("xyz", adopted.Error.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// Rows below the launch versions are ordinary work, classified without reading their payloads.
    /// </summary>
    /// <remarks>
    /// A retention mutation at or below the last ordinary version, and a factory reset with no
    /// checkpoint at all, both closed nothing. Adopting either would park an ordinary operation behind
    /// admission that was never closed, and no later pass could tell that stall from a real one.
    ///
    /// <para>Both rows carry a payload far larger than the decode bound, because classification is by
    /// version and must stay that way. An adopter that projected the blob before deciding would let a
    /// single corrupt row decide how much memory startup allocates, and startup is the one moment
    /// there is nothing left to fall back to.</para>
    /// </remarks>
    [SkippableFact]
    public async Task Legacy_and_non_covenant_rows_are_ignored_without_projecting_large_payloads()
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner mutation = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation);

        await ExecuteAsync(
            "UPDATE \"LongRunningOperations\" SET \"CheckpointVersion\" = @ordinary, "
                + "\"CheckpointPayload\" = zeroblob(1000000) WHERE \"Id\" = @id;",
            ("@ordinary", LastOrdinaryMutationCheckpointVersion),
            ("@id", mutation.OperationId.ToString("N")));

        CovenantExclusiveRecoveryOwner factory = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionFactoryReset);

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

    /// <summary>
    /// A row still carrying the retired reset checkpoint is refused rather than waved through.
    /// </summary>
    /// <remarks>
    /// The version between the last ordinary mutation and the launch belonged to the same-database
    /// reset checkpoint this build no longer writes or reads. That shape was an erasure: a row still
    /// holding it closed admission and may have already dropped part of a family. Treating it as
    /// ordinary work — which is what "anything under the launch version is a plain mutation" would do —
    /// would hand a half-erased family to an ordinary reconciler that has no notion of an exclusive
    /// owner and no way to finish or undo the erasure it walked into.
    ///
    /// <para>The refusal is decided by the row's version alone, before the payload is projected, so it
    /// holds for a retired payload that is still perfectly well-formed. That is the case that matters:
    /// a corrupt one would refuse for a second reason anyway, and a test that only used a corrupt one
    /// would pass against a build that had quietly reopened the gap.</para>
    /// </remarks>
    [SkippableFact]
    public async Task A_row_still_at_the_retired_reset_checkpoint_version_refuses()
    {

        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner mutation = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation);

        byte[] retired = CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionMutationCheckpointV3(
                DataRetentionMutationCheckpointV3.CurrentVersion,
                "reset-memory",
                ((int)MemoryResetScope.Covenant).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                new CovenantResetEffectArmV1(
                    mutation.OperationId,
                    CovenantRecoveryCheckpointCodec.EncodeEffectDigest(mutation.EffectDigest),
                    CovenantExclusiveOperation.CovenantReset,
                    CovenantResetPhase.InventoryPrepared)));

        await ExecuteAsync(
            "UPDATE \"LongRunningOperations\" SET \"CheckpointVersion\" = @retired, "
                + "\"CheckpointPayload\" = @payload WHERE \"Id\" = @id;",
            ("@retired", DataRetentionMutationCheckpointV3.CurrentVersion),
            ("@payload", retired),
            ("@id", mutation.OperationId.ToString("N")));

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(gate)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.True((await gate.ResumeExclusiveAsync(mutation, CancellationToken.None)).IsFailure);

    }

    [SkippableFact]
    public async Task Pending_pre_covenant_rows_are_ignored_and_do_not_block_readiness()
    {

        RequireSqlCipher();

        LongRunningOperationStore store = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

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
            LongRunningOperationKinds.DataRetentionMutation);

        CovenantExclusiveRecoveryOwner second = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionFactoryReset);

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

        _ = await SeedCurrentAsync(LongRunningOperationKinds.DataRetentionMutation);

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

    private async Task<CovenantExclusiveRecoveryOwner> SeedCurrentAsync(string kind)
    {

        LongRunningOperationStore store = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        bool mutation = kind == LongRunningOperationKinds.DataRetentionMutation;

        LongRunningOperationRecoveryPolicy policy = mutation
            ? LongRunningOperationRecoveryPolicy.ReconcileAndComplete
            : LongRunningOperationRecoveryPolicy.RestartIdempotently;

        CovenantExclusiveOperation operation = mutation
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

        CovenantDigest digest = new(Convert.FromHexString(new string('a', 64)));

        byte[] payload = mutation
            ? CovenantRecoveryCheckpointCodec.Encode(
                new CovenantOfflineTransitionLaunchV4(
                    CovenantOfflineTransitionLaunchV4.CurrentVersion,
                    created.Id,
                    LongRunningOperationKinds.DataRetentionMutation,
                    nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
                    operation,
                    CovenantRecoveryCheckpointCodec.EncodeEffectDigest(digest),
                    SourceGeneration,
                    TargetGeneration,
                    SourceEpochs,
                    TargetEpochs,
                    created.Revision))
            : CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionFactoryTransitionLaunchV2(
                    DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                    created.Id,
                    LongRunningOperationKinds.DataRetentionFactoryReset,
                    nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
                    operation,
                    CovenantRecoveryCheckpointCodec.EncodeEffectDigest(digest),
                    SourceGeneration,
                    TargetGeneration,
                    SourceEpochs,
                    TargetEpochs,
                    created.Revision));

        int version = mutation
            ? CovenantOfflineTransitionLaunchV4.CurrentVersion
            : DataRetentionFactoryTransitionLaunchV2.CurrentVersion;

        Assert.True(await store.SaveCheckpointAsync(
            created.Id,
            "startup-adopter-owner",
            0,
            version,
            payload,
            CovenantResetCheckpointInitiator.CheckpointReference(kind, created.Id),
            created.PublicSummary,
            DateTimeOffset.UtcNow));

        return new CovenantExclusiveRecoveryOwner(created.Id, operation, digest);

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
