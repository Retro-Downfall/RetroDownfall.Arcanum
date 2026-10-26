using System.Data;

using System.Data.Common;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

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

using RetroDownfall.Arcanum.Tests.Support;

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

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adopted = await adopter.AdoptBeforeReadinessAsync(
            Connection,
            CancellationToken.None);

        Assert.True(adopted.IsSuccess);

        Assert.Equal(expected, adopted.Value?.Owner);

        await using CovenantExclusiveLease resumed =
            (await gate.ResumeExclusiveAsync(expected, CancellationToken.None)).Value;

        Assert.Equal(expected, resumed.Snapshot.RecoveryOwner);
    }

    [SkippableTheory]

    [InlineData("\"CheckpointPayload\" = NULL", "UnreadableCheckpoint")]

    [InlineData("\"CheckpointPayload\" = X''", "UnreadableCheckpoint")]

    [InlineData("\"CheckpointPayload\" = 'text payload'", "UnreadableCheckpoint")]

    [InlineData("\"CheckpointPayload\" = zeroblob(4097)", "UnreadableCheckpoint")]

    [InlineData("\"State\" = 99", "MalformedRow")]

    [InlineData("\"State\" = 0", "MalformedRow")]

    [InlineData("\"RecoveryPolicy\" = 0", "PolicyMismatch")]

    [InlineData("\"Revision\" = 'private-revision-canary'", "MalformedRow")]

    [InlineData("\"State\" = 'running'", "MalformedRow")]

    [InlineData("\"RecoveryPolicy\" = 99", "MalformedRow")]

    [InlineData("\"RecoveryPolicy\" = 'policy'", "MalformedRow")]

    [InlineData("\"CheckpointVersion\" = 99", "UnsupportedVersion")]

    [InlineData("\"CheckpointVersion\" = 'version'", "MalformedRow")]

    [InlineData("\"Id\" = upper(\"Id\")", "MalformedRow")]

    [InlineData("\"CheckpointReference\" = upper(\"CheckpointReference\")", "UnreadableCheckpoint")]
    public async Task Malformed_current_evidence_refuses_without_installing_an_owner(
        string assignment, string reason)
    {
        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner owner = await SeedCurrentAsync(
            LongRunningOperationKinds.DataRetentionMutation);

        await ExecuteAsync($"UPDATE \"LongRunningOperations\" SET {assignment};");

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(gate)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.Contains($"Reason={reason}", adopted.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-revision-canary", adopted.Error.Message, StringComparison.Ordinal);

        Assert.NotNull(adopted.Error.CovenantStartupFailure);

        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(adopted.Error, ArcanumJsonContext.Default.Error));

        Assert.DoesNotContain(wire.RootElement.EnumerateObject(), static property =>
            property.Name.Equals("covenantStartupFailure", StringComparison.OrdinalIgnoreCase));

        if (assignment.Contains("\"Id\"", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(owner.OperationId.ToString("N"), adopted.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains(owner.OperationId.ToString("N"), adopted.Error.Message, StringComparison.Ordinal);
        }

        Assert.True((await gate.ResumeExclusiveAsync(owner, CancellationToken.None)).IsFailure);
    }

    /// <summary>
    /// A launch whose own identity fields disagree with the plan is refused without echoing content.
    /// </summary>
    /// <remarks>
    /// Both launch kinds refuse wrong exclusive operations and mismatched operation IDs distinctly
    /// from unreadable JSON, noncanonical digests and transposed target tuples. The last is the subtle one — every transposed
    /// target is still some source plus one, so a rule that compared the tuples as sets would admit a
    /// launch that later verified a replaced family against the wrong counter.
    ///
    /// <para>The refusal is content-free on purpose. An adoption failure is read by whatever surfaces
    /// startup diagnostics, and echoing the rejected bytes back would let a corrupt row choose what
    /// that surface displays.</para>
    /// </remarks>
    [SkippableTheory]

    [InlineData(0, LongRunningOperationKinds.DataRetentionMutation)]
    [InlineData(1, LongRunningOperationKinds.DataRetentionMutation)]
    [InlineData(2, LongRunningOperationKinds.DataRetentionMutation)]
    [InlineData(3, LongRunningOperationKinds.DataRetentionMutation)]
    [InlineData(4, LongRunningOperationKinds.DataRetentionMutation)]
    [InlineData(0, LongRunningOperationKinds.DataRetentionFactoryReset)]
    [InlineData(1, LongRunningOperationKinds.DataRetentionFactoryReset)]
    [InlineData(2, LongRunningOperationKinds.DataRetentionFactoryReset)]
    [InlineData(3, LongRunningOperationKinds.DataRetentionFactoryReset)]
    [InlineData(4, LongRunningOperationKinds.DataRetentionFactoryReset)]
    public async Task Malformed_current_payload_identity_refuses_content_free(int malformedPart, string kind)
    {
        RequireSqlCipher();

        CovenantExclusiveRecoveryOwner owner = await SeedCurrentAsync(kind);

        Guid payloadOperationId = malformedPart == 3 ? Guid.NewGuid() : owner.OperationId;

        string digest = malformedPart == 1
            ? "xyz"
            : CovenantRecoveryCheckpointCodec.EncodeEffectDigest(owner.EffectDigest);

        CovenantExclusiveOperation operation = malformedPart == 0
            ? CovenantExclusiveOperation.SchemaRepair
            : owner.Operation;

        CovenantOfflineTransitionEpochsV1 target = malformedPart == 2
            ? new CovenantOfflineTransitionEpochsV1(
                SourceEpochs.KeyReclamationEpoch + 1,
                SourceEpochs.AcceleratorEpoch + 1,
                SourceEpochs.EnvelopeKeyEpoch + 1)
            : TargetEpochs;

        byte[] payload = kind == LongRunningOperationKinds.DataRetentionMutation
            ? CovenantRecoveryCheckpointCodec.Encode(new CovenantOfflineTransitionLaunchV4(
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                payloadOperationId,
                LongRunningOperationKinds.DataRetentionMutation,
                nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
                operation,
                digest,
                SourceGeneration,
                TargetGeneration,
                SourceEpochs,
                target,
                StartingRevision: 0))
            : CovenantRecoveryCheckpointCodec.Encode(new DataRetentionFactoryTransitionLaunchV2(
                DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                payloadOperationId,
                LongRunningOperationKinds.DataRetentionFactoryReset,
                nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
                operation,
                digest,
                SourceGeneration,
                TargetGeneration,
                SourceEpochs,
                target,
                StartingRevision: 0));

        if (malformedPart == 4)
        {
            payload = "{private-payload-canary"u8.ToArray();
        }

        await ExecuteAsync(
            "UPDATE \"LongRunningOperations\" SET \"CheckpointPayload\" = @payload;",
            ("@payload", payload));

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(gate)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.Equal(
            malformedPart is 0 or 3 ? CovenantStartupFailureReason.WrongOperation : CovenantStartupFailureReason.UnreadableCheckpoint,
            adopted.Error.CovenantStartupFailure?.Reason);

        Assert.Equal(owner.OperationId, adopted.Error.CovenantStartupFailure?.OperationId);

        Assert.True((await gate.ResumeExclusiveAsync(owner, CancellationToken.None)).IsFailure);

        Assert.DoesNotContain("xyz", adopted.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-payload-canary", adopted.Error.Message, StringComparison.Ordinal);

        if (malformedPart == 3)
        {
            Assert.DoesNotContain(payloadOperationId.ToString("N"), adopted.Error.Message, StringComparison.Ordinal);
        }
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

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adopted = await new
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

        byte[] retired = RetiredCovenantCheckpoints.Mutation(
            mutation.OperationId,
            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(mutation.EffectDigest));

        await ExecuteAsync(
            "UPDATE \"LongRunningOperations\" SET \"CheckpointVersion\" = @retired, "
                + "\"CheckpointPayload\" = @payload WHERE \"Id\" = @id;",
            ("@retired", RetiredCovenantCheckpoints.MutationVersion),
            ("@payload", retired),
            ("@id", mutation.OperationId.ToString("N")));

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adopted = await new
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

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adopted = await new
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

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(gate)
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.Contains("Reason=SecondOwner", adopted.Error.Message, StringComparison.Ordinal);

        Assert.Contains(second.OperationId.ToString("N"), adopted.Error.Message, StringComparison.Ordinal);

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

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> conflictResult = await new
            CovenantErasureStartupRecoveryOwnerAdopter(conflict).AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(conflictResult.IsFailure);

        Assert.Equal(CovenantStartupFailureReason.GateRefused, conflictResult.Error.CovenantStartupFailure?.Reason);

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

    [SkippableTheory]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("private-id-canary\nforged warning")]
    public async Task Invalid_operation_identity_is_never_reported_as_a_validated_identifier(string rawId)
    {
        RequireSqlCipher();

        _ = await SeedCurrentAsync(LongRunningOperationKinds.DataRetentionMutation);

        await ExecuteAsync("UPDATE \"LongRunningOperations\" SET \"Id\" = @id;", ("@id", rawId));

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> result = await new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Null(result.Error.CovenantStartupFailure?.OperationId);

        Assert.DoesNotContain(rawId, result.Error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Missing_operation_table_reports_bounded_storage_fault_without_sql_text()
    {
        RequireSqlCipher();

        await ExecuteAsync("ALTER TABLE \"LongRunningOperations\" RENAME TO \"private-table-canary\";");

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(Connection, CancellationToken.None);

        Assert.True(adopted.IsFailure);

        Assert.Contains("Reason=StorageFault", adopted.Error.Message, StringComparison.Ordinal);

        Assert.Contains("Sqlite", adopted.Error.Message, StringComparison.Ordinal);

        Assert.Contains("SQLite=1", adopted.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("LongRunningOperations", adopted.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-table-canary", adopted.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_storage_exception_reports_only_closed_category_and_no_message()
    {
        await using FaultingOpenConnection connection = new(() => new IOException("private-exception-canary"));

        Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> result = await new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(connection, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(CovenantStartupFailureReason.StorageFault, result.Error.CovenantStartupFailure?.Reason);

        Assert.Equal(GrimoireConnectionFailureKind.IO, result.Error.CovenantStartupFailure?.FailureKind);

        Assert.Null(result.Error.CovenantStartupFailure?.OperationId);

        Assert.DoesNotContain("private-exception-canary", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_during_storage_access_is_preserved()
    {
        using CancellationTokenSource cancellation = new();

        await using FaultingOpenConnection connection = new(() =>
        {
            cancellation.Cancel();

            return new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(connection, cancellation.Token));
    }

    private sealed class FaultingOpenConnection(Func<Exception> failure) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() { }

        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw failure();
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
