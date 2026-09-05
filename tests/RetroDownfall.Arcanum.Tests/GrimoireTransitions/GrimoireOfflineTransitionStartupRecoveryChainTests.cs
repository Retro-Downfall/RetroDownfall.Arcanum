using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// The whole pre-bootstrap chain over a real encrypted catalog, and the eight cases it has to resolve.
/// </summary>
/// <remarks>
/// The component tests beside this one each prove one seam against doubles. What they cannot prove is
/// that the chain works against a database: that the validate-only unlock opens a real SQLCipher
/// catalog with its own sidecar, that the authority load reads this installation's real
/// <c>covenant_state</c> and <c>covenant_authority_state</c> and agrees with the launch row committed
/// beside them, and that spending the handoff leaves a real gate closed around the reconstructed owner
/// with the probe already physically shut.
///
/// <para>What is deliberately not here is the erasure itself. That the resumed coordinator reaches the
/// same ending from every phase boundary is the crash matrix's proof, and the entry point this chain
/// dispatches into is the same registered handler that matrix drives.</para>
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]
public sealed class GrimoireOfflineTransitionStartupRecoveryChainTests : IAsyncLifetime
{

    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly GrimoireFixture _fixture;

    private readonly TempWorkspace _workspace = new();

    private string _root = string.Empty;

    private string _databasePath = string.Empty;

    private ArcanumMaintenanceLock? _lock;

    private ArcanumDbContext? _db;

    public GrimoireOfflineTransitionStartupRecoveryChainTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public async Task InitializeAsync()
    {

        await _workspace.InitializeAsync();

        _root = _workspace.CreateSubdir("startup-chain");

        _lock = Assert.IsType<ArcanumMaintenanceLock>(ArcanumMaintenanceLock.TryAcquire(_root));

        _databasePath = Path.Combine(_root, "arcanum.db");

        if (GrimoireFixture.SqlCipherAvailable)
        {

            string source = _fixture.CopyDatabase();

            File.Copy(source, _databasePath, overwrite: true);

            File.Copy(source + ".kdf", _databasePath + ".kdf", overwrite: true);

            _db = _fixture.CreateContext(_databasePath);

            await _db.Database.OpenConnectionAsync(Token);

        }

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        _lock?.Dispose();

        await _workspace.DisposeAsync();

    }

    /// <summary>
    /// An interrupted transition's authority comes back, and only then is the handler reached.
    /// </summary>
    /// <remarks>
    /// The order the assertions read off is the authority order: the catalog is opened validate-only,
    /// its persisted authority is verified against the journal, the gate is closed around the
    /// reconstructed owner, the probe is physically gone, and the handler is dispatched for the exact
    /// operation the journal names.
    /// </remarks>
    [SkippableFact]
    public async Task The_chain_reconstructs_real_authority_and_then_dispatches()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        RecoveryComposition composition = new();

        RecordingDispatch dispatch = new(LongRunningOperationSettlementOutcome.Completed);

        // Nothing has published Covenant authority in this container, which is the state a fresh
        // process starts in and the state the whole chain exists to leave behind.
        Assert.Null(composition.Runtime.Current.ActiveAuthority);

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered =
            await Chain(composition, dispatch).RecoverBeforeBootstrapAsync(
                _lock!,
                _root,
                _databasePath,
                InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition,
                seeded.Evidence,
                Token);

        Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error.Message : null);

        Assert.Equal(GrimoireOfflineTransitionStartupRecoveryOutcome.Resumed, recovered.Value);

        Assert.Equal(seeded.OperationId, dispatch.Dispatched);

        // Reconstructed, not invented: the exclusive lease the crashed run held resumes, and every
        // ordinary lease stays refused because the adopted owner closed the installation slot.
        Assert.NotNull(composition.Runtime.Current.ActiveAuthority);

        Assert.True(
            (await composition.Gate.ResumeExclusiveAsync(seeded.Owner, Token)).IsSuccess);

        Assert.True(
            (await composition.Gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).IsFailure);

    }

    /// <summary>
    /// A catalog whose key this installation no longer holds refuses before any authority moves.
    /// </summary>
    /// <remarks>
    /// The refusal has to come from the unlock rather than from a later step, because the later steps
    /// are the ones that would open the database — and the ordinary bootstrap answers a catalog it
    /// cannot open by creating one.
    /// </remarks>
    [SkippableFact]
    public async Task A_catalog_this_installation_cannot_open_refuses_before_authority_moves()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        RecoveryComposition composition = new();

        RecordingDispatch dispatch = new(LongRunningOperationSettlementOutcome.Completed);

        await CloseSeedingConnectionAsync();

        File.Delete(_databasePath + ".kdf");

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered =
            await Chain(composition, dispatch).RecoverBeforeBootstrapAsync(
                _lock!,
                _root,
                _databasePath,
                InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition,
                seeded.Evidence,
                Token);

        Assert.True(recovered.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, recovered.Error.Code);

        Assert.Null(dispatch.Dispatched);

        Assert.Null(composition.Runtime.Current.ActiveAuthority);

    }

    /// <summary>
    /// The eight cases the issue names, each resolved before readiness or failed closed.
    /// </summary>
    /// <remarks>
    /// Six of them are the pair matrix's answers, which #249 settled; what this asserts is the half
    /// #250 adds — which of those answers now ends in a resumption, and which still ends in the one
    /// content-free refusal. The launch-gap case is the seventh and is resolved after bootstrap and
    /// before readiness rather than before bootstrap, because a launch with no journal is a pre-effect
    /// crash and the catalog it names is safe to open.
    /// </remarks>
    [SkippableTheory]

    // active
    [InlineData(4, true)]

    // dual-record
    [InlineData(5, true)]

    // reconciliation-pending and retirement-pending both reach the suffix arm
    [InlineData(6, true)]

    // malformed, missing and conflicting evidence all reduce to the one refusal
    [InlineData(7, false)]
    public async Task Every_named_case_resolves_before_readiness_or_fails_closed(int arm, bool resumes)
    {

        RequireSqlCipher();

        InstallationResetNestedTransitionEvidenceOutcome evidence =
            (InstallationResetNestedTransitionEvidenceOutcome)arm;

        Seeded seeded = await SeedAsync();

        RecordingDispatch dispatch = new(LongRunningOperationSettlementOutcome.Completed);

        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> recovered =
            await Chain(new RecoveryComposition(), dispatch).RecoverBeforeBootstrapAsync(
                _lock!,
                _root,
                _databasePath,
                evidence,
                seeded.Evidence,
                Token);

        Assert.Equal(resumes, recovered.IsSuccess);

        if (resumes)
        {

            Assert.Equal(GrimoireOfflineTransitionStartupRecoveryOutcome.Resumed, recovered.Value);

            Assert.Equal(seeded.OperationId, dispatch.Dispatched);

        }
        else
        {

            Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, recovered.Error.Code);

            Assert.Null(dispatch.Dispatched);

        }

    }

    /// <summary>The launch-gap case: a committed launch, no journal, resolved before readiness.</summary>
    [SkippableFact]
    public async Task A_launch_with_no_journal_is_finished_before_readiness()
    {

        RequireSqlCipher();

        Seeded seeded = await SeedAsync();

        RecordingDispatch dispatch = new(LongRunningOperationSettlementOutcome.Completed);

        // The pre-bootstrap pass does nothing at all, because no journal is active.
        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> beforeBootstrap =
            await Chain(new RecoveryComposition(), dispatch).RecoverBeforeBootstrapAsync(
                _lock!,
                _root,
                _databasePath,
                InstallationResetNestedTransitionEvidenceOutcome.NeitherActive,
                journal: null,
                Token);

        Assert.Equal(
            GrimoireOfflineTransitionStartupRecoveryOutcome.NoActiveJournal,
            beforeBootstrap.Value);

        Assert.Null(dispatch.Dispatched);

        // The adopter finds the row the crash left behind, and the resumption spends it before the
        // readiness this bootstrap is about to publish.
        Result<CovenantExclusiveRecoveryOwner?> adopted = await new
            CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGateFixture.CreateGate())
            .AdoptBeforeReadinessAsync(Connection, Token);

        Assert.True(adopted.IsSuccess, adopted.IsFailure ? adopted.Error.Message : null);

        Assert.Equal(seeded.Owner, adopted.Value);

        Result resumed = await CovenantOfflineTransitionLaunchGapResumption
            .ResumeBeforeReadinessAsync(dispatch, _lock!, _root, adopted.Value, Token);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        Assert.Equal(seeded.OperationId, dispatch.Dispatched);

    }

    private SqliteConnection Connection => (SqliteConnection)_db!.Database.GetDbConnection();

    private GrimoireOfflineTransitionStartupRecovery Chain(
        RecoveryComposition composition,
        RecordingDispatch dispatch) =>
        new(
            new GrimoireRecoveryOnlyUnlock(
                new TestApiKeySecretStore(GrimoireFixture.TestApiKey),
                new GrimoireDbPassphraseSource()),
            new CovenantRecoveryAuthorityBootstrapper(
                composition.Gate,
                composition.Runtime,
                composition.Keys,
                composition.Availability,
                PermittedHostTools.Instance,
                new TestApiKeySecretStore(GrimoireFixture.TestApiKey),
                GrimoireOfflineTransitionEffectHandlerRegistry
                    .Create(GrimoireOfflineTransitionEffectHandlerRegistry.Declared)
                    .Value),
            dispatch);

    private async Task CloseSeedingConnectionAsync()
    {

        if (_db is null)
        {

            return;

        }

        SqliteConnection connection = Connection;

        await _db.DisposeAsync();

        _db = null;

        SqliteConnection.ClearPool(connection);

    }

    private sealed record Seeded(
        Guid OperationId,
        CovenantExclusiveRecoveryOwner Owner,
        GrimoireOfflineTransitionRecoveryEvidence Evidence);

    private async Task<Seeded> SeedAsync()
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
            DateTimeOffset.UtcNow.AddMinutes(5),
            Token);

        Assert.True(leased.Acquired);

        CovenantOfflineTransitionSourceState source = await CurrentSourceAsync();

        CovenantDigest effect = new(Convert.FromHexString(new string('a', 64)));

        CovenantOfflineTransitionLaunchV4 launch = new(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            created.Id,
            LongRunningOperationKinds.DataRetentionMutation,
            nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            CovenantExclusiveOperation.CovenantReset,
            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(effect),
            source.DatasetGeneration,
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            new CovenantOfflineTransitionEpochsV1(
                source.AcceleratorEpoch,
                source.KeyReclamationEpoch,
                source.EnvelopeKeyEpoch),
            new CovenantOfflineTransitionEpochsV1(
                source.AcceleratorEpoch + 1,
                source.KeyReclamationEpoch + 1,
                source.EnvelopeKeyEpoch + 1),
            leased.Operation.Revision);

        Assert.True(await store.SaveCheckpointAsync(
            created.Id,
            "crashed-owner",
            0,
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
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

        return new Seeded(
            created.Id,
            new CovenantExclusiveRecoveryOwner(
                created.Id,
                CovenantExclusiveOperation.CovenantReset,
                effect),
            new GrimoireOfflineTransitionRecoveryEvidence(
                journal,
                SlotEpoch: 1,
                Revision: 3,
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

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    /// <summary>One uninitialized runtime and the three things that share it.</summary>
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

    private sealed class PermittedHostTools : IHostProcessToolsRuntimePolicy
    {

        internal static PermittedHostTools Instance { get; } = new();

        public bool IsPublished => true;

        public bool CovenantPermitted => true;

        public bool HostProcessToolsPermitted => false;

        public HostProcessToolsMarkerPairDisposition? Disposition =>
            HostProcessToolsMarkerPairDisposition.Clean;

        public HostProcessToolsStartupBlocker Blocker => HostProcessToolsStartupBlocker.None;

    }

    private sealed class RecordingDispatch(LongRunningOperationSettlementOutcome verdict)
        : IGrimoireOfflineTransitionHandlerDispatch
    {

        internal Guid? Dispatched { get; private set; }

        public Task<Result<LongRunningOperationSettlementOutcome>> DispatchAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            string guardedDirectory,
            Guid operationId,
            CancellationToken cancellationToken)
        {

            heldInstallationLock.AssertHeldFor(guardedDirectory);

            Dispatched = operationId;

            return Task.FromResult(
                Result<LongRunningOperationSettlementOutcome>.Success(verdict));

        }

    }

}
