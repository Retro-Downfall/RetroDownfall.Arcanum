using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The authority a crashed transition held, loaded back and checked against the journal that names it.
/// </summary>
/// <remarks>
/// Loading is not the point; agreement is. A catalog whose dataset generation is neither the source
/// this transition was planned against nor the target it preselected is not the catalog this journal
/// describes, and publishing its authority would let a handler resume against a database it has no
/// binding to.
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]
public sealed class CovenantRecoveryAuthorityBootstrapperTests : IAsyncLifetime
{

    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly GrimoireFixture _fixture;

    private readonly TempWorkspace _workspace = new();

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumMaintenanceLock? _lock;

    private string _root = string.Empty;

    private SqliteConnection Connection =>
        (SqliteConnection)_db!.Database.GetDbConnection();

    public CovenantRecoveryAuthorityBootstrapperTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public async Task InitializeAsync()
    {

        await _workspace.InitializeAsync();

        _root = _workspace.CreateSubdir("recovery-authority");

        _lock = Assert.IsType<ArcanumMaintenanceLock>(ArcanumMaintenanceLock.TryAcquire(_root));

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        await _db.Database.OpenConnectionAsync(Token);

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection = Connection;

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        _lock?.Dispose();

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        await _workspace.DisposeAsync();

    }

    [SkippableFact]
    public async Task An_agreeing_catalog_and_journal_load_and_write_nothing()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        long before = await RevisionAsync(seeded.OperationId);

        Result<ICovenantClosedRecoveryHandoff> loaded = await Bootstrapper()
            .LoadAsync(_lock!, _root, Connection, seeded.Evidence, Token);

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);

        Assert.Equal(seeded.OperationId, loaded.Value.OperationId);

        CovenantClosedRecoveryHandoff handoff =
            Assert.IsType<CovenantClosedRecoveryHandoff>(loaded.Value);

        Assert.Equal(seeded.Owner, handoff.Owner);

        Assert.Equal(
            GrimoireOfflineTransitionObservedState.ExactlyNotApplied,
            handoff.ObservedDatabaseState);

        Assert.Equal(before, await RevisionAsync(seeded.OperationId));

    }

    /// <summary>
    /// Every way the two records can disagree, and each of them is a refusal.
    /// </summary>
    /// <remarks>
    /// The dataset case is the load-bearing one and the reason the facts are verified rather than
    /// merely read: a generation that is neither the launch's source nor its preselected target is a
    /// database this journal never described.
    /// </remarks>
    [SkippableTheory]

    [InlineData(Disagreement.MissingRow)]

    [InlineData(Disagreement.LegacyCheckpointVersion)]

    [InlineData(Disagreement.LaunchBindingDigest)]

    [InlineData(Disagreement.EffectDigest)]

    [InlineData(Disagreement.RevisionBehindLaunch)]

    [InlineData(Disagreement.UnrelatedDatasetGeneration)]
    public async Task Every_disagreement_between_the_two_records_refuses(Disagreement disagreement)
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync(disagreement);

        Result<ICovenantClosedRecoveryHandoff> loaded = await Bootstrapper()
            .LoadAsync(_lock!, _root, Connection, seeded.Evidence, Token);

        Assert.True(loaded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, loaded.Error.Code);

    }

    /// <summary>
    /// A build that cannot run this journal's kind refuses rather than resuming it as something else.
    /// </summary>
    /// <remarks>
    /// The registry is the authority on which exclusive operation a kind may be, so it is the registry
    /// this pass asks. Both halves of that answer are refusals here: a kind the table has never heard
    /// of, and a kind whose registered operation is not the one the launch committed to. Neither is
    /// reachable through the two handlers this build ships, which is exactly why they are proved
    /// against a composed table rather than against the production one.
    /// </remarks>
    [SkippableFact]
    public async Task A_kind_this_build_cannot_run_refuses()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        CovenantRecoveryAuthorityBootstrapper bootstrapper = Bootstrapper(
            new RecoveryComposition(),
            registry: Value(GrimoireOfflineTransitionEffectHandlerRegistry.Create(
            [
                new CovenantOfflineTransitionEffectHandler(
                    GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    requiresOrdinaryContinuation: true),
            ])));

        Result<ICovenantClosedRecoveryHandoff> loaded = await bootstrapper
            .LoadAsync(_lock!, _root, Connection, seeded.Evidence, Token);

        Assert.True(loaded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, loaded.Error.Code);

    }

    [SkippableFact]
    public async Task A_kind_registered_against_another_operation_refuses()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        CovenantRecoveryAuthorityBootstrapper bootstrapper = Bootstrapper(
            new RecoveryComposition(),
            registry: Value(GrimoireOfflineTransitionEffectHandlerRegistry.Create(
            [
                new CovenantOfflineTransitionEffectHandler(
                    GrimoireOfflineTransitionKind.CovenantReset,
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    requiresOrdinaryContinuation: false),
            ])));

        Result<ICovenantClosedRecoveryHandoff> loaded = await bootstrapper
            .LoadAsync(_lock!, _root, Connection, seeded.Evidence, Token);

        Assert.True(loaded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, loaded.Error.Code);

    }

    [SkippableFact]
    public async Task An_unpermitted_host_tools_policy_refuses_rather_than_warning()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        Result<ICovenantClosedRecoveryHandoff> loaded = await Bootstrapper(covenantPermitted: false)
            .LoadAsync(_lock!, _root, Connection, seeded.Evidence, Token);

        Assert.True(loaded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, loaded.Error.Code);

    }

    [SkippableFact]
    public async Task Consumption_closes_the_gate_around_the_recovered_owner_exactly_once()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        RecoveryComposition composition = new();

        CovenantRecoveryAuthorityBootstrapper bootstrapper = Bootstrapper(composition);

        Result<ICovenantClosedRecoveryHandoff> loaded = await bootstrapper
            .LoadAsync(_lock!, _root, Connection, seeded.Evidence, Token);

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);

        Result consumed = await loaded.Value
            .ConsumeAsync(_lock!, _root, seeded.Evidence, Connection, Token);

        Assert.True(consumed.IsSuccess, consumed.IsFailure ? consumed.Error.Message : null);

        // The facts are present precisely so this one lease can be resumed. Everything ordinary stays
        // refused, because the adopted owner closes the installation slot.
        Assert.True(
            (await composition.Gate.ResumeExclusiveAsync(seeded.Owner, Token)).IsSuccess);

        Assert.True(
            (await composition.Gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).IsFailure);

        Result second = await loaded.Value
            .ConsumeAsync(_lock!, _root, seeded.Evidence, Connection, Token);

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, second.Error.Code);

    }

    [SkippableFact]
    public async Task Consumption_refuses_a_journal_that_has_moved_since_the_handoff_was_minted()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        Result<ICovenantClosedRecoveryHandoff> loaded = await Bootstrapper()
            .LoadAsync(_lock!, _root, Connection, seeded.Evidence, Token);

        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);

        Result consumed = await loaded.Value.ConsumeAsync(
            _lock!,
            _root,
            seeded.Evidence with { Revision = seeded.Evidence.Revision + 1 },
            Connection,
            Token);

        Assert.True(consumed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, consumed.Error.Code);

    }

    public enum Disagreement
    {

        None = 0,

        MissingRow = 1,

        LegacyCheckpointVersion = 2,

        LaunchBindingDigest = 3,

        EffectDigest = 4,

        RevisionBehindLaunch = 6,

        UnrelatedDatasetGeneration = 7,

    }

    private static CovenantRecoveryAuthorityBootstrapper Bootstrapper(
        bool covenantPermitted = true) =>
        Bootstrapper(new RecoveryComposition(), covenantPermitted);

    private static CovenantRecoveryAuthorityBootstrapper Bootstrapper(
        RecoveryComposition composition,
        bool covenantPermitted = true,
        GrimoireOfflineTransitionEffectHandlerRegistry? registry = null) =>
        new(
            composition.Gate,
            composition.Runtime,
            composition.Keys,
            composition.Availability,
            new FixedHostProcessToolsRuntimePolicy(covenantPermitted),
            new TestApiKeySecretStore(GrimoireFixture.TestApiKey),
            registry ?? Value(GrimoireOfflineTransitionEffectHandlerRegistry.Create(
                GrimoireOfflineTransitionEffectHandlerRegistry.Declared)));

    /// <summary>One uninitialized runtime and the four things that share it.</summary>
    private sealed class RecoveryComposition
    {

        internal CovenantRuntimeGenerationProvider Runtime { get; } = new();

        internal CovenantAvailability Availability { get; }

        internal CovenantEnvelopeMasterKeyProvider Keys { get; }

        internal CovenantOperationGate Gate { get; }

        internal RecoveryComposition()
        {

            Availability = new CovenantAvailability(Runtime);

            Keys = new CovenantEnvelopeMasterKeyProvider(Runtime);

            Gate = new CovenantOperationGate(
                Runtime,
                new FakeCovenantCampaignScopeProbe(),
                TimeSpan.FromSeconds(5));

        }

    }

    private async Task<long> RevisionAsync(Guid operationId)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText =
            "SELECT \"Revision\" FROM \"LongRunningOperations\" WHERE \"Id\" = @id;";

        SqliteParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@id";

        parameter.Value = operationId.ToString("N");

        _ = command.Parameters.Add(parameter);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private sealed record Seeded(
        Guid OperationId,
        CovenantExclusiveRecoveryOwner Owner,
        GrimoireOfflineTransitionRecoveryEvidence Evidence);

    private async Task<Seeded> SeedAsync(Disagreement disagreement = Disagreement.None)
    {

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation created = await store.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted Covenant erasure.",
                DateTimeOffset.UtcNow));

        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            created.Id,
            "crashed-owner",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(leased.Acquired);

        CovenantOfflineTransitionSourceState source = await CurrentSourceAsync();

        CovenantOfflineTransitionEpochsV1 sourceEpochs = new(
            source.AcceleratorEpoch,
            source.KeyReclamationEpoch,
            source.EnvelopeKeyEpoch);

        CovenantOfflineTransitionEpochsV1 targetEpochs = new(
            source.AcceleratorEpoch + 1,
            source.KeyReclamationEpoch + 1,
            source.EnvelopeKeyEpoch + 1);

        Guid sourceGeneration = disagreement == Disagreement.UnrelatedDatasetGeneration
            ? Guid.Parse("99999999-9999-4999-8999-999999999999")
            : source.DatasetGeneration;

        CovenantDigest effect = new(Convert.FromHexString(new string('a', 64)));

        CovenantExclusiveOperation operation = CovenantExclusiveOperation.CovenantReset;

        CovenantOfflineTransitionLaunchV4 launch = new(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            created.Id,
            LongRunningOperationKinds.DataRetentionMutation,
            nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            operation,
            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(effect),
            sourceGeneration,
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            sourceEpochs,
            targetEpochs,
            leased.Operation.Revision);

        int version = disagreement == Disagreement.LegacyCheckpointVersion
            ? 3
            : CovenantOfflineTransitionLaunchV4.CurrentVersion;

        Assert.True(await store.SaveCheckpointAsync(
            created.Id,
            "crashed-owner",
            0,
            version,
            CovenantRecoveryCheckpointCodec.Encode(launch),
            CovenantResetCheckpointInitiator.CheckpointReference(
                LongRunningOperationKinds.DataRetentionMutation,
                created.Id),
            created.PublicSummary,
            DateTimeOffset.UtcNow));

        GrimoireOfflineTransitionLaunchBinding binding = Value(
            GrimoireOfflineTransitionLaunch.FromLaunch(launch));

        GrimoireOfflineTransitionBinding journal = Value(
            GrimoireOfflineTransitionLaunch.JournalBinding(
                binding,
                slotEpoch: 1,
                payloadVersion: 1,
                expectedDatabaseOperationRevision: binding.StartingRevision + 1,
                parentReceiptBindingDigest: null));

        if (disagreement == Disagreement.LaunchBindingDigest)
        {

            journal = journal with
            {

                DatabaseOperationLaunchBindingDigest =
                    new CovenantDigest(Convert.FromHexString(new string('b', 64))),

            };

        }

        if (disagreement == Disagreement.EffectDigest)
        {

            journal = journal with
            {

                EffectDigest = new CovenantDigest(Convert.FromHexString(new string('c', 64))),

            };

        }

        if (disagreement == Disagreement.RevisionBehindLaunch)
        {

            await ExecuteAsync("UPDATE \"LongRunningOperations\" SET \"Revision\" = 0;");

        }

        if (disagreement == Disagreement.MissingRow)
        {

            await ExecuteAsync("DELETE FROM \"LongRunningOperations\";");

        }

        return new Seeded(
            created.Id,
            new CovenantExclusiveRecoveryOwner(created.Id, operation, effect),
            new GrimoireOfflineTransitionRecoveryEvidence(
                journal,
                SlotEpoch: 1,
                Revision: 4,
                new CovenantDigest(Convert.FromHexString(new string('d', 64)))));

    }

    private async Task<CovenantOfflineTransitionSourceState> CurrentSourceAsync()
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            SELECT DatasetGeneration, AcceleratorEpoch, KeyReclamationEpoch, EnvelopeKeyEpoch
            FROM covenant_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));

        return new CovenantOfflineTransitionSourceState(
            new Guid(reader.GetFieldValue<byte[]>(0)),
            (ulong)reader.GetInt64(1),
            (ulong)reader.GetInt64(2),
            (ulong)reader.GetInt64(3));

    }

    private async Task ExecuteAsync(string sql)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;

    }

    private sealed class FixedHostProcessToolsRuntimePolicy(bool permitted)
        : IHostProcessToolsRuntimePolicy
    {

        public bool CovenantPermitted => permitted;

        public bool IsPublished => true;

        public bool HostProcessToolsPermitted => permitted;

        public HostProcessToolsMarkerPairDisposition? Disposition =>
            HostProcessToolsMarkerPairDisposition.Clean;

        public HostProcessToolsStartupBlocker Blocker =>
            permitted ? HostProcessToolsStartupBlocker.None : HostProcessToolsStartupBlocker.MarkerMismatch;

    }

}
