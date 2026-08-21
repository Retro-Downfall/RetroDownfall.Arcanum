using System.Data;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The complete committed reset through the real host composition and one real SQLCipher Grimoire.
/// </summary>
[Collection("ProcessEnvironment")]
[Trait("Category", "Integration")]
public sealed class CovenantErasureSameProcessTests
{

    [Fact]
    public async Task Successful_erasure_reopens_status_crud_inference_and_disclosure_on_the_fresh_dataset()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync();

        SameProcessBefore before = await harness.SeedAndCaptureAsync();

        await using PausedTurn paused = harness.PauseBeforeLease(before.OldInvocation);

        await paused.WaitUntilPausedAsync();

        Task<Result<CovenantErasureCompletion>> resetTask = harness.RunAsync();

        Task revocation = Task.Delay(Timeout.InfiniteTimeSpan, before.ReadLease.Revocation);

        try
        {

            Task first = await Task.WhenAny(resetTask, revocation)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(revocation, first);

            Assert.True(before.ReadLease.Revocation.IsCancellationRequested);

            Assert.False(resetTask.IsCompleted);

        }
        finally
        {

            await before.ReadLease.DisposeAsync();

        }

        Result<CovenantErasureCompletion> reset = await resetTask.WaitAsync(TimeSpan.FromSeconds(45));

        Assert.True(reset.IsSuccess, reset.Error.Message);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, reset.Value.Disposition);

        Assert.True(reset.Value.CanonicalResetApplied);

        Assert.True(reset.Value.LocalSecureErasureComplete);

        Assert.Equal(3, reset.Value.Exposure.PossibleAttempts);

        Assert.Equal(CovenantDisclosureCountKind.Exact, reset.Value.Exposure.CountKind);

        Assert.True(reset.Value.ExternalDisclosuresNotRevocable);

        Assert.NotEqual(before.DatasetGeneration, harness.Availability.Current.DatasetGeneration);

        Result<CovenantTurnContext> raced = await paused.ReleaseAsync();

        await harness.AssertEveryOldCapabilityRejectedAsync(before, raced);

        await harness.AssertFreshStatusAsync();

        await harness.AssertFreshCrudAsync();

        await harness.AssertFreshInferenceContextAsync(before.OldContent);

        await harness.AssertFreshDisclosureWriteAsync();

    }

    private sealed class SameProcessHarness : IAsyncDisposable
    {

        private const string Owner = "task-9-same-process";

        private const string FreshKey = "task9.fresh";

        private const string FreshContent = "fresh generation only";

        private readonly ArcanumWebApplicationFactory _factory;

        private readonly HttpClient _client;

        private readonly AsyncServiceScope _operationScope;

        private readonly CovenantCanonicalErasureFixture _fixture;

        private SameProcessBefore? _before;

        private SameProcessHarness(
            ArcanumWebApplicationFactory factory,
            HttpClient client,
            AsyncServiceScope operationScope,
            CovenantCanonicalErasureFixture fixture)
        {

            _factory = factory;

            _client = client;

            _operationScope = operationScope;

            _fixture = fixture;

        }

        internal IServiceProvider Services => _factory.Services;

        internal CovenantAvailability Availability => Services.GetRequiredService<CovenantAvailability>();

        internal static async Task<SameProcessHarness> CreateAsync()
        {

            ArcanumWebApplicationFactory factory = new()
            {

                SettingsOverride = static settings => settings with
                {

                    Features = settings.Features with { Covenant = true },

                },

            };

            try
            {

                HttpClient client = factory.CreateAuthenticatedClient();

                AsyncServiceScope operationScope = factory.Services.CreateAsyncScope();

                IServiceProvider services = operationScope.ServiceProvider;

                CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.AttachAsync(
                    factory.Services.GetRequiredService<ICovenantMaintenanceConnectionFactory>(),
                    factory.Services.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                    factory.Services.GetRequiredService<ICovenantConnectionDrain>(),
                    CancellationToken.None);

                return new SameProcessHarness(factory, client, operationScope, fixture);

            }
            catch
            {

                await factory.DisposeAsync();

                throw;

            }

        }

        internal async Task<SameProcessBefore> SeedAndCaptureAsync()
        {

            await _fixture.SeedAcceptanceStateAsync(CancellationToken.None);

            CovenantRuntimeGenerationProvider runtime = Services
                .GetRequiredService<CovenantRuntimeGenerationProvider>();

            CovenantEnvelopeMasterKeyProvider root = Services
                .GetRequiredService<CovenantEnvelopeMasterKeyProvider>();

            CovenantDisclosureWriter writer = Services.GetRequiredService<CovenantDisclosureWriter>();

            CovenantAvailabilitySnapshot availability = Availability.Current;

            Assert.True(availability.FeatureEnabled);

            Assert.Equal(CovenantCapabilityState.Healthy, availability.Canonical);

            Assert.NotEqual(Guid.Empty, availability.DatasetGeneration);

            Assert.NotNull(runtime.Current.ActiveAuthority);

            Assert.NotNull(runtime.Current.Keys);

            Guid datasetGeneration = availability.DatasetGeneration
                ?? throw new InvalidOperationException("The published Covenant dataset is empty.");

            Assert.Equal(
                datasetGeneration,
                await _fixture.ReadDatasetGenerationAsync(CancellationToken.None));

            ICovenantDisclosureJournal journal = Services.GetRequiredService<ICovenantDisclosureJournal>();

            Result<CovenantDisclosureReceipt> warmed = await journal.AcknowledgeAsync(
                Draft(datasetGeneration, effectSeed: 0x31),
                CovenantDisclosureEffectCategory.ProviderDispatch,
                Sensitivity(datasetGeneration),
                CancellationToken.None);

            Assert.True(warmed.IsSuccess, warmed.Error.Message);

            ICovenantEnvelopeCodec codec = Services.GetRequiredService<ICovenantEnvelopeCodec>();

            Dictionary<CovenantEnvelopePurpose, string> tokens = [];

            foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
            {

                Result<string> encoded = codec.Encode(
                    purpose,
                    [(byte)purpose],
                    TimeSpan.FromMinutes(10));

                Assert.True(
                    encoded.IsSuccess,
                    $"{purpose}: {encoded.Error.Code} {encoded.Error.Message}");

                tokens.Add(purpose, encoded.Value);

            }

            IOperatorAuthorityContextIssuer issuer = Services
                .GetRequiredService<IOperatorAuthorityContextIssuer>();

            OperatorAuthorityContext operatorContext = issuer
                .Issue(CovenantAuthorityRequirement.CovenantManage).Value;

            CovenantReadAuthorityEpoch readEpoch = issuer.IssueReadEpoch().Value;

            ICovenantOperationGate gate = Services.GetRequiredService<ICovenantOperationGate>();

            CovenantReadLease readLease = (await gate.AcquireReadAsync(
                CovenantOperationScope.Global,
                CancellationToken.None)).Value;

            ArcanumInvocationContext oldInvocation = CreateInvocation(readEpoch);

            _before = new SameProcessBefore(
                datasetGeneration,
                runtime,
                root,
                writer,
                tokens,
                operatorContext,
                readEpoch,
                readLease,
                oldInvocation,
                "be brief");

            return _before;

        }

        internal async Task<Result<CovenantErasureCompletion>> RunAsync()
        {

            SameProcessBefore before = _before
                ?? throw new InvalidOperationException("The old generation must be captured before reset.");

            ILongRunningOperationCoordinator operations = _operationScope.ServiceProvider
                .GetRequiredService<ILongRunningOperationCoordinator>();

            LongRunningOperationLeaseResult started = await operations.StartAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionMutation,
                    LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                    "Task 9 same-process Covenant reset.",
                    DateTimeOffset.UtcNow),
                Owner,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);

            Assert.True(started.Acquired);

            CovenantResetCheckpointInitiator initiator = _operationScope.ServiceProvider
                .GetRequiredService<CovenantResetCheckpointInitiator>();

            Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await initiator
                .PrepareCovenantResetInventoryAsync(
                    started.Operation,
                    Owner,
                    new CovenantErasureEffectDigestInput(
                        CovenantExclusiveOperation.CovenantReset,
                        "task-9-success",
                        before.DatasetGeneration,
                        Rows: 3,
                        ManagedFiles: 0,
                        LocalArtifacts: 0,
                        AffectedSessions: 0,
                        PossibleDisclosures: 3,
                        CovenantDisclosureCountKind.Exact),
                    requestedOperationId: null,
                    MemoryResetScope.Covenant,
                    CancellationToken.None);

            Assert.True(admitted.IsSuccess, admitted.Error.Message);

            ILongRunningOperationStore store = _operationScope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>();

            LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
                await store.GetAsync(started.Operation.Id, CancellationToken.None));

            Result<CovenantErasureCheckpointState> checkpoint = CovenantErasureCheckpointState
                .FromMutationCheckpoint(
                    operation.Id,
                    operation.CheckpointPayload!,
                    out bool describesCovenantErasure);

            Assert.True(describesCovenantErasure);

            Assert.True(checkpoint.IsSuccess, checkpoint.Error.Message);

            CovenantErasureCoordinator coordinator = _operationScope.ServiceProvider
                .GetRequiredService<CovenantErasureCoordinator>();

            return await coordinator.RunAsync(
                operation,
                checkpoint.Value,
                Owner,
                CancellationToken.None);

        }

        internal PausedTurn PauseBeforeLease(ArcanumInvocationContext invocation)
        {

            TaskCompletionSource<bool> paused = new(TaskCreationOptions.RunContinuationsAsynchronously);

            TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<Result<CovenantTurnContext>> result = Task.Run(
                async () =>
                {

                    await using AsyncServiceScope scope = Services.CreateAsyncScope();

                    ICovenantContextProvider context = scope.ServiceProvider
                        .GetRequiredService<ICovenantContextProvider>();

                    _ = paused.TrySetResult(true);

                    await release.Task;

                    return await context.BeginTurnAsync(
                        invocation,
                        Guid.NewGuid(),
                        CancellationToken.None);

                });

            return new PausedTurn(paused.Task, release, result);

        }

        internal async Task AssertEveryOldCapabilityRejectedAsync(
            SameProcessBefore before,
            Result<CovenantTurnContext> raced)
        {

            Assert.Same(before.Runtime, Services.GetRequiredService<CovenantRuntimeGenerationProvider>());

            Assert.Same(before.Root, Services.GetRequiredService<CovenantEnvelopeMasterKeyProvider>());

            Assert.Same(before.Writer, Services.GetRequiredService<CovenantDisclosureWriter>());

            Assert.Same(
                before.Writer,
                Services.GetRequiredService<ICovenantDisclosureJournal>());

            Assert.Same(
                before.Writer,
                Services.GetRequiredService<ICovenantDisclosureWriterLifecycle>());

            ICovenantEnvelopeCodec codec = Services.GetRequiredService<ICovenantEnvelopeCodec>();

            foreach ((CovenantEnvelopePurpose purpose, string token) in before.Tokens)
            {

                Assert.True(codec.Decode(purpose, token).IsFailure);

                Result<string> issued = codec.Encode(
                    purpose,
                    [(byte)(0x80 + (byte)purpose)],
                    TimeSpan.FromMinutes(10));

                Assert.True(issued.IsSuccess, issued.Error.Message);

                Assert.True(codec.Decode(purpose, issued.Value).IsSuccess);

            }

            IOperatorAuthorityContextIssuer issuer = Services
                .GetRequiredService<IOperatorAuthorityContextIssuer>();

            Assert.True(issuer.Revalidate(before.OperatorContext).IsFailure);

            ICovenantAuthoritySnapshotProvider authority = Services
                .GetRequiredService<ICovenantAuthoritySnapshotProvider>();

            Assert.False(before.ReadEpoch.Matches(authority.Current));

            Assert.True((await before.ReadLease.RevalidateAsync(CancellationToken.None)).IsFailure);

            Assert.True(raced.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, raced.Error.Code);

            await using CovenantReadLease fresh = (await Services
                .GetRequiredService<ICovenantOperationGate>()
                .AcquireReadAsync(CovenantOperationScope.Global, CancellationToken.None)).Value;

            Assert.Equal(Availability.Current.DatasetGeneration, fresh.Snapshot.DatasetGeneration);

        }

        internal async Task AssertFreshStatusAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            DataRetentionStatus status = await scope.ServiceProvider
                .GetRequiredService<IDataRetentionService>()
                .GetStatusAsync(CancellationToken.None);

            DataRetentionCovenantInventory covenant = Assert.IsType<DataRetentionCovenantInventory>(
                status.Covenant);

            Assert.Equal(0, covenant.ManagedFiles);

            Assert.Equal(0, covenant.LocalArtifacts);

            Assert.Equal(0, covenant.AffectedSessions);

            Assert.Equal(3, covenant.PossibleDisclosures);

            Assert.Equal(CovenantDisclosureCountKind.Exact, covenant.DisclosureCountKind);

        }

        internal async Task AssertFreshCrudAsync()
        {

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            ICovenantOperationGate gate = Services.GetRequiredService<ICovenantOperationGate>();

            await using (CovenantWriteLease lease = (await gate.AcquireWriteAsync(
                CovenantOperationScope.Global,
                CancellationToken.None)).Value)
            {

                ICovenantConnectionSource connections = scope.ServiceProvider
                    .GetRequiredService<ICovenantConnectionSource>();

                SqliteConnection connection = await connections.GetOpenConnectionAsync(CancellationToken.None);

                await using SqliteTransaction transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None);

                long keyEpoch = await ScalarAsync(
                    connection,
                    transaction,
                    "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;");

                long registryEpoch = await ScalarAsync(
                    connection,
                    transaction,
                    "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;");

                CovenantMutationBatch batch = new(
                    Availability.Current.DatasetGeneration
                        ?? throw new InvalidOperationException("The fresh Covenant dataset is empty."),
                    keyEpoch,
                    registryEpoch,
                    DateTimeOffset.UtcNow,
                    [
                        CovenantMutationFixture.OperatorSet(
                            CovenantOperationScope.Global,
                            FreshKey,
                            FreshContent,
                            expectedRevision: 0,
                            expectedKeyEpoch: 0),
                    ]);

                Result<IReadOnlyList<CovenantMutationReceipt>> applied = await scope.ServiceProvider
                    .GetRequiredService<CovenantMutationKernel>()
                    .ApplyBatchAsync(
                        batch,
                        new CovenantMutationTransaction(connection, transaction),
                        CancellationToken.None);

                Assert.True(applied.IsSuccess, applied.Error.Message);

                await transaction.CommitAsync(CancellationToken.None);

            }

            await using CovenantReadLease readLease = (await gate.AcquireReadAsync(
                CovenantOperationScope.Global,
                CancellationToken.None)).Value;

            Result<CovenantTurnSnapshot> snapshot = await scope.ServiceProvider
                .GetRequiredService<ICovenantStore>()
                .ReadTurnSnapshotAsync(
                    CanonicalCampaignContext.GlobalOnly,
                    readLease,
                    CancellationToken.None);

            Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

            CovenantSnapshotCandidate fresh = Assert.Single(snapshot.Value.Candidates);

            Assert.Equal(FreshKey, fresh.NormalizedKey.Value);

            Assert.Equal(
                CovenantMutationFixture.Artifact(FreshKey, FreshContent).CompiledContent,
                Encoding.UTF8.GetString(fresh.CompiledFragment.ToArray()));

        }

        internal async Task AssertFreshInferenceContextAsync(string oldContent)
        {

            IOperatorAuthorityContextIssuer issuer = Services
                .GetRequiredService<IOperatorAuthorityContextIssuer>();

            ArcanumInvocationContext invocation = CreateInvocation(issuer.IssueReadEpoch().Value);

            await using AsyncServiceScope scope = Services.CreateAsyncScope();

            Result<CovenantTurnContext> begun = await scope.ServiceProvider
                .GetRequiredService<ICovenantContextProvider>()
                .BeginTurnAsync(invocation, Guid.NewGuid(), CancellationToken.None);

            Assert.True(begun.IsSuccess, begun.Error.Message);

            await using CovenantTurnContext context = begun.Value;

            Assert.True(context.HasPlan);

            Assert.Contains(FreshContent, context.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

            Assert.DoesNotContain(oldContent, context.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

        }

        internal async Task AssertFreshDisclosureWriteAsync()
        {

            Guid dataset = Availability.Current.DatasetGeneration
                ?? throw new InvalidOperationException("The fresh Covenant dataset is empty.");

            Result<CovenantDisclosureReceipt> acknowledged = await Services
                .GetRequiredService<ICovenantDisclosureJournal>()
                .AcknowledgeAsync(
                    Draft(dataset, effectSeed: 0x42),
                    CovenantDisclosureEffectCategory.ProviderDispatch,
                    Sensitivity(dataset),
                    CancellationToken.None);

            Assert.True(acknowledged.IsSuccess, acknowledged.Error.Message);

        }

        public async ValueTask DisposeAsync()
        {

            await _fixture.DisposeAsync();

            await _operationScope.DisposeAsync();

            _client.Dispose();

            await _factory.DisposeAsync();

        }

        private static ArcanumInvocationContext CreateInvocation(CovenantReadAuthorityEpoch epoch) =>
            ArcanumInvocationContext.Create(
                ArcanumExecutionSurface.StatelessOperatorTurn,
                CanonicalCampaignContext.GlobalOnly,
                InvocationAttendance.Attended,
                CovenantContextPolicy.Default,
                ToolPolicy.AllTools,
                epoch).Value;

        private static ProviderCallSensitivity Sensitivity(Guid dataset)
        {

            GenerationProvenance provenance = GenerationProvenance.CreateExact([dataset]);

            return new ProviderCallSensitivity(
                ContentSensitivity.CovenantDerived,
                provenance,
                CovenantDigests.Sensitivity(provenance.ToDigestInput(ContentSensitivity.CovenantDerived)));

        }

        private static CovenantDisclosureDraft Draft(Guid dataset, byte effectSeed)
        {

            ProviderCallSensitivity sensitivity = Sensitivity(dataset);

            return new CovenantDisclosureDraft(
                new Guid("6f1c0b2e-9a44-4e1d-8b7a-2c5d3f6a8e90"),
                CovenantDisclosureSubjectKind.Operation,
                new Guid("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                CovenantOperationGateFixture.Digest(effectSeed),
                CovenantEgressDestination.Provider,
                CovenantDisclosureRevocability.Nonrevocable,
                CovenantOperationGateFixture.Digest(0x51),
                sensitivity.Digest,
                wardEvidenceDigest: null,
                CovenantOperationGateFixture.Digest(0x52),
                backupEvidenceDigest: null,
                timestamp: 1_700_000_000_000L + effectSeed);

        }

        private static async Task<long> ScalarAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql)
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = sql;

            object? value = await command.ExecuteScalarAsync(CancellationToken.None);

            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

        }

    }

    private sealed class PausedTurn(
        Task paused,
        TaskCompletionSource<bool> release,
        Task<Result<CovenantTurnContext>> result) : IAsyncDisposable
    {

        private int _released;

        internal Task WaitUntilPausedAsync() => paused.WaitAsync(TimeSpan.FromSeconds(5));

        internal async Task<Result<CovenantTurnContext>> ReleaseAsync()
        {

            Release();

            return await result.WaitAsync(TimeSpan.FromSeconds(5));

        }

        public async ValueTask DisposeAsync()
        {

            Release();

            try
            {

                _ = await result.WaitAsync(TimeSpan.FromSeconds(5));

            }
            catch
            {

                // The owning assertion reports the task failure; disposal only guarantees release.

            }

        }

        private void Release()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                _ = release.TrySetResult(true);

            }

        }

    }

    private sealed record SameProcessBefore(
        Guid DatasetGeneration,
        CovenantRuntimeGenerationProvider Runtime,
        CovenantEnvelopeMasterKeyProvider Root,
        CovenantDisclosureWriter Writer,
        IReadOnlyDictionary<CovenantEnvelopePurpose, string> Tokens,
        OperatorAuthorityContext OperatorContext,
        CovenantReadAuthorityEpoch ReadEpoch,
        CovenantReadLease ReadLease,
        ArcanumInvocationContext OldInvocation,
        string OldContent);

}
