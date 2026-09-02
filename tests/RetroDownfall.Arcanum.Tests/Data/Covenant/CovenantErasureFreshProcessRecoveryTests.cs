using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// A process boundary over one real encrypted Grimoire and the shipped recovery composition.
/// </summary>
[Collection("ProcessEnvironment")]
[Trait("Category", "Integration")]
public sealed class CovenantErasureFreshProcessRecoveryTests
{

    private const string OriginalOwner = "task-9-original-process";

    private const string RecoveryOwner = "task-9-recovery-process";

    [SkippableFact]
    public async Task Inventory_checkpoint_is_adopted_before_readiness_and_completed_by_the_fresh_process()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"task-9-restart-{Guid.NewGuid():N}");

        string? originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        string? originalDotnet = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        string? originalAspNet = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        using GrimoireFixture fixture = new();

        try
        {

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", testHome);

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

            global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

            Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

            string source = fixture.CopyDatabase();

            File.Copy(source, ArcanumPaths.GrimoireDatabaseFile, overwrite: true);

            File.Copy(source + ".kdf", ArcanumPaths.GrimoireDatabaseFile + ".kdf", overwrite: true);

            Guid operationId;

            Guid oldDataset;

            CovenantExclusiveRecoveryOwner durableOwner;

            Dictionary<CovenantEnvelopePurpose, string> oldTokens = [];

            await using (ServiceProvider original = CreateProcess(fixture.Passphrase))
            {

                await using (SqliteConnection install = await InitializeProcessAsync(original))
                {
                }

                CovenantAvailabilitySnapshot availability = original
                    .GetRequiredService<CovenantAvailability>()
                    .Current;

                oldDataset = availability.DatasetGeneration
                    ?? throw new InvalidOperationException("The original process has no Covenant dataset.");

                ICovenantEnvelopeCodec codec = original.GetRequiredService<ICovenantEnvelopeCodec>();

                foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
                {

                    Result<string> encoded = codec.Encode(
                        purpose,
                        [(byte)purpose],
                        TimeSpan.FromMinutes(10));

                    Assert.True(encoded.IsSuccess, encoded.Error.Message);

                    oldTokens.Add(purpose, encoded.Value);

                }

                await using AsyncServiceScope scope = original.CreateAsyncScope();

                LongRunningOperationLeaseResult started = await scope.ServiceProvider
                    .GetRequiredService<ILongRunningOperationCoordinator>()
                    .StartAsync(
                        new LongRunningOperationCreateRequest(
                            LongRunningOperationKinds.DataRetentionMutation,
                            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                            "Task 9 interrupted Covenant reset.",
                            DateTimeOffset.UtcNow),
                        OriginalOwner,
                        TimeSpan.FromMinutes(5),
                        CancellationToken.None);

                Assert.True(started.Acquired);

                Result<CovenantResetCheckpointInitiator.GateAdmission> prepared = await scope
                    .ServiceProvider
                    .GetRequiredService<CovenantResetCheckpointInitiator>()
                    .PrepareCovenantResetInventoryAsync(
                        started.Operation,
                        OriginalOwner,
                        new CovenantErasureEffectDigestInput(
                            CovenantExclusiveOperation.CovenantReset,
                            "task-9-restart",
                            oldDataset,
                            Rows: 0,
                            ManagedFiles: 0,
                            LocalArtifacts: 0,
                            AffectedSessions: 0,
                            PossibleDisclosures: 0,
                            CovenantDisclosureCountKind.Exact),
                        requestedOperationId: null,
                        MemoryResetScope.Covenant,
                        CancellationToken.None);

                Assert.True(prepared.IsSuccess, prepared.Error.Message);

                operationId = started.Operation.Id;

                durableOwner = prepared.Value.Owner;

            }

            SqliteConnection.ClearAllPools();

            await using ServiceProvider recovery = CreateProcess(fixture.Passphrase);

            CovenantOperationGate gate = recovery.GetRequiredService<CovenantOperationGate>();

            ICovenantEnvelopeCodec recoveryCodec = recovery.GetRequiredService<ICovenantEnvelopeCodec>();

            await using (SqliteConnection install = await InitializeProcessAsync(recovery))
            {

                foreach ((CovenantEnvelopePurpose purpose, string token) in oldTokens)
                {

                    Assert.True(recoveryCodec.Decode(purpose, token).IsFailure);

                }

                Result<CovenantExclusiveRecoveryOwner?> adopted = await recovery
                    .GetRequiredService<CovenantErasureStartupRecoveryOwnerAdopter>()
                    .AdoptBeforeReadinessAsync(install, CancellationToken.None);

                Assert.True(adopted.IsSuccess, adopted.Error.Message);

                Assert.Equal(durableOwner, adopted.Value);

                gate.PublishReadiness();

                Assert.True((await gate.AcquireReadAsync(
                    CovenantOperationScope.Global,
                    CancellationToken.None)).IsFailure);

            }

            await using AsyncServiceScope recoveryScope = recovery.CreateAsyncScope();

            ILongRunningOperationStore store = recoveryScope.ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>();

            LongRunningOperation interrupted = Assert.IsType<LongRunningOperation>(
                await store.GetAsync(operationId, CancellationToken.None));

            DateTimeOffset recoveryAt = interrupted.LeaseExpiresAt!.Value.AddSeconds(1);

            LongRunningOperationLeaseResult recovered = await store.TryAcquireLeaseAsync(
                operationId,
                RecoveryOwner,
                recoveryAt,
                recoveryAt.AddMinutes(5),
                CancellationToken.None);

            Assert.True(recovered.Acquired);

            ILongRunningOperationRecoveryHandler handler = Assert.Single(
                recoveryScope.ServiceProvider.GetServices<ILongRunningOperationRecoveryHandler>(),
                static candidate => candidate is DataRetentionMutationRecoveryHandler);

            LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
                recovered.Operation,
                CancellationToken.None);

            Assert.Equal(LongRunningOperationState.Completed, outcome.State);

            Assert.Null(outcome.ErrorCode);

            Assert.NotEqual(
                oldDataset,
                recovery.GetRequiredService<CovenantAvailability>().Current.DatasetGeneration);

            await using CovenantReadLease fresh = (await gate.AcquireReadAsync(
                CovenantOperationScope.Global,
                CancellationToken.None)).Value;

            Assert.Equal(
                recovery.GetRequiredService<CovenantAvailability>().Current.DatasetGeneration,
                fresh.Snapshot.DatasetGeneration);

            foreach ((CovenantEnvelopePurpose purpose, string token) in oldTokens)
            {

                Assert.True(recoveryCodec.Decode(purpose, token).IsFailure);

            }

        }
        finally
        {

            SqliteConnection.ClearAllPools();

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", originalTestHome);

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnet);

            global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalAspNet);

            if (Directory.Exists(testHome))
            {

                Directory.Delete(testHome, recursive: true);

            }

        }

    }

    private static ServiceProvider CreateProcess(string passphrase)
    {

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IWeaveService>(static _ => null!);

        builder.Services.AddSingleton<IArcanumIntelligenceProvider>(static _ => null!);

        builder.Services.AddSingleton<IHumanPromptRegistry>(static _ => null!);

        builder.Services.AddSingleton<IModelTokenEstimator>(static _ => null!);

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        ServiceProvider provider = builder.Services.BuildServiceProvider(
            new ServiceProviderOptions
            {

                ValidateOnBuild = true,

                ValidateScopes = true,

            });

        Assert.IsType<GrimoireDbPassphraseSource>(
            provider.GetRequiredService<IGrimoireDbPassphraseSource>())
            .SetPassphrase(passphrase);

        return provider;

    }

    private static async Task<SqliteConnection> InitializeProcessAsync(ServiceProvider provider)
    {

        IDesignTimeGrimoireConnectionFactory connections =
            new DesignTimeGrimoireConnectionFactory(
                provider.GetRequiredService<IGrimoireDbPassphraseSource>());

        SqliteConnection connection = await connections.OpenAsync(CancellationToken.None);

        try
        {

            await provider.GetRequiredService<ICovenantSqliteConnectionInitializer>()
                .InitializeAsync(
                    connection,
                    CovenantSqliteConnectionMode.ReadWrite,
                    CancellationToken.None);

            GrimoireSchemaInstallResult installed = await provider
                .GetRequiredService<GrimoireSchemaInstaller>()
                .InstallAsync(
                    connection,
                    new EmbeddingIntegrationSettings().Dimensions,
                    CovenantAuthorityBootstrapper.PrepareWithoutInstallationLock(
                        GrimoireFixture.TestApiKey,
                        DateTimeOffset.UtcNow),
                    CancellationToken.None);

            Assert.True(installed.Core.IsHealthy);

            Assert.True(installed.CovenantCanonical.IsHealthy);

            CovenantAvailability availability = provider.GetRequiredService<CovenantAvailability>();

            _ = availability.PublishSchema(installed, CovenantHealthTransition.Bootstrap);

            _ = availability.PublishFeatureEnabled(featureEnabled: true);

            bool persisted = await CovenantPersistedAvailabilityPublisher.PublishAsync(
                availability,
                connection,
                installed.CovenantAccelerator.IsHealthy,
                CovenantHealthTransition.Bootstrap,
                CancellationToken.None);

            Assert.True(persisted);

            HostProcessToolsRuntimePolicy policy = provider
                .GetRequiredService<HostProcessToolsRuntimePolicy>();

            Result classified = policy.Publish(
                new HostProcessToolsStartupDecision(
                    HostProcessToolsMarkerPairDisposition.Clean,
                    CovenantPermitted: true,
                    HostProcessToolsPermitted: false));

            Assert.True(classified.IsSuccess, classified.Error.Message);

            CovenantRuntimeGenerationProvider runtime = provider
                .GetRequiredService<CovenantRuntimeGenerationProvider>();

            await CovenantAuthorityStartupReconciler.ReconcileAsync(
                connection,
                runtime,
                provider.GetRequiredService<CovenantEnvelopeMasterKeyProvider>(),
                availability.Current,
                policy,
                GrimoireFixture.TestApiKey,
                CancellationToken.None);

            Assert.NotNull(runtime.Current.ActiveAuthority);

            Assert.NotNull(runtime.Current.Keys);

            return connection;

        }
        catch
        {

            await connection.DisposeAsync();

            throw;

        }

    }

}
