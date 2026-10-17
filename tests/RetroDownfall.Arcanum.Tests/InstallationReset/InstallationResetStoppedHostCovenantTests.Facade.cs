using System.Reflection;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Tests.Cli;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The reset-only facade for the protected erasure: what it will act on, what it refuses, how it is
/// composed, and that a workspace reset never goes near any of it.
/// </summary>
public sealed partial class InstallationResetStoppedHostCovenantTests
{
    /// <summary>
    /// The position a claimed nested erasure is in is a pure function of the ledger row its identity
    /// resolves to and of whether the journal slot holds anything.
    /// </summary>
    [Theory]
    [InlineData(null, false, "Absent")]
    [InlineData(null, true, "Launched")]
    [InlineData("Running", true, "Launched")]
    [InlineData("Failed", true, "Launched")]
    public void A_journal_in_the_slot_means_the_erasure_may_have_acted_whatever_the_row_says(
        string? state,
        bool journalActive,
        string expected)
    {
        LongRunningOperation? row = state is null
            ? null
            : Row(Enum.Parse<LongRunningOperationState>(state), checkpointVersion: 0, payload: null);

        Assert.Equal(
            Enum.Parse<StoppedHostNestedLaunchState>(expected),
            StoppedHostFactoryErasure.Classify(row, journalActive));
    }

    [Theory]
    [InlineData("Pending", 0, false, "Unlaunched")]
    [InlineData("Running", 0, false, "Unlaunched")]
    [InlineData("Failed", 0, false, "Unlaunched")]
    [InlineData("Abandoned", 0, false, "Unlaunched")]
    [InlineData("Completed", 0, false, "Terminal")]
    [InlineData("Running", 2, true, "Launched")]
    [InlineData("Waiting", 2, true, "Launched")]
    [InlineData("ReconciliationRequired", 2, true, "Launched")]
    [InlineData("Completed", 2, true, "Terminal")]
    [InlineData("Failed", 2, true, "Terminal")]
    [InlineData("Abandoned", 2, true, "Terminal")]
    public void A_ledger_row_is_unlaunched_until_its_launch_checkpoint_has_a_payload(
        string state,
        int checkpointVersion,
        bool hasPayload,
        string expected)
    {
        LongRunningOperation row = Row(
            Enum.Parse<LongRunningOperationState>(state),
            checkpointVersion,
            hasPayload ? [1, 2, 3] : null);

        Assert.Equal(
            Enum.Parse<StoppedHostNestedLaunchState>(expected),
            StoppedHostFactoryErasure.Classify(row, journalActive: false));
    }

    /// <summary>
    /// The facade runs one operation, for one claim an authenticated record made, and refuses every
    /// other request before it touches anything, leaving no lock lent and no handle open behind it.
    /// </summary>
    [SkippableFact]
    public async Task The_facade_acts_only_on_what_an_authenticated_record_claimed()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature: true);

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        // A record that claimed a nested erasure, and a process that ended before it began one.
        CliTestResult ended = await RunAsync(
            covenantFeature: true,
            services => EndTheProcess(services, EndOfProcess.ClaimWithNoOperation),
            GlobalArguments(apply: true));

        Assert.NotEqual((int)CliExitCode.Success, ended.ExitCode);

        InstallationResetActiveStore store = new(ArcanumPaths.GrimoireDirectory, _credentials);

        InstallationResetActivePayloadV3 payload = Assert.IsType<InstallationResetActivePublication>(
            (await store.InspectAsync(CancellationToken.None)).Value.Publication).Payload;

        Guid claimed = Assert.IsType<InstallationResetNestedTransitionReceiptV1>(
            payload.NestedTransitionReceipt).NestedOperationId;

        string planId = Assert.Single(payload.AcceptedBinding.DataPlanIds);

        await using ServiceProvider provider = Compose(covenantFeature: true).BuildServiceProvider();

        provider.GetRequiredService<IGrimoireDbPassphraseSource>()
            .SetPassphrase(StoppedHostResetTestSupport.FixturePassphrase);

        using IServiceScope scope = provider.CreateScope();

        StoppedHostFactoryErasure facade = scope.ServiceProvider.GetRequiredService<StoppedHostFactoryErasure>();

        InstallationResetMaintenanceLockAccessor accessor = provider
            .GetRequiredService<InstallationResetMaintenanceLockAccessor>();

        Task<Result<DataRetentionApplyResult>> Call(DataRetentionApplyRequest request) =>
            UnderStoppedHostAuthorityAsync(
                (issuer, token) => facade.ApplyAsync(request, issuer, token));

        // Any operation but a factory reset.
        Result<DataRetentionApplyResult> other = await Call(new DataRetentionApplyRequest(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            planId,
            claimed));

        Assert.Equal(ErrorCodes.Data.InvalidRequest, other.Error.Code);

        // No identity, an identity the record did not claim, and a plan it did not accept.
        foreach (DataRetentionApplyRequest unclaimed in new[]
        {
            new DataRetentionApplyRequest(new DataRetentionRequest(DataRetentionOperation.FactoryReset), planId),
            new DataRetentionApplyRequest(new DataRetentionRequest(DataRetentionOperation.FactoryReset), planId, Guid.NewGuid()),
            new DataRetentionApplyRequest(new DataRetentionRequest(DataRetentionOperation.FactoryReset), "another-plan", claimed),
        })
        {
            Result<DataRetentionApplyResult> refused = await Call(unclaimed);

            Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);
        }

        // Nothing was lent, nothing was started, nothing was erased, and nothing is left open.
        Assert.True(accessor.BorrowHeldLock(ArcanumPaths.GrimoireDirectory).IsFailure);

        Assert.Equal(0, await CountFactoryResetOperationsAsync());

        Assert.Equal(1, await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM saga_memories;"));

        Assert.True(await StoppedHostResetTestSupport.NoHandleRemainsOpenAsync());
    }

    /// <summary>
    /// The plan the query-only lease measured and the plan the ordinary context re-derives inside the
    /// facade are one identity, so a confirmed plan survives the change of connection: an apply that
    /// differed in either would be refused as a changed plan, and this one completes.
    /// </summary>
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task The_plan_a_lease_measured_is_the_plan_the_ordinary_context_replans_to(
        bool covenantFeature,
        bool labelledData)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        if (labelledData)
        {
            await LabeledSagaMemorySeed.SeedAsync(_fixture, Guid.NewGuid());
        }

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        CliTestResult dryRun = await RunAsync(covenantFeature, GlobalArguments(apply: false));

        Assert.Equal((int)CliExitCode.Success, dryRun.ExitCode);

        using (JsonDocument plan = JsonDocument.Parse(dryRun.Output))
        {
            Assert.Equal(
                labelledData,
                plan.RootElement.GetProperty("covenantInventory").GetProperty("rows").GetInt64() > 0
                || plan.RootElement.GetProperty("covenantInventory").GetProperty("localArtifacts").GetInt64() > 0);
        }

        CliTestResult applied = await RunAsync(covenantFeature, GlobalArguments(apply: true));

        string described = $"exit {applied.ExitCode}\nstdout: {applied.Output}\nstderr: {applied.Error}";

        Assert.True(applied.ExitCode == (int)CliExitCode.Success, described);

        using JsonDocument result = JsonDocument.Parse(applied.Output);

        Assert.Equal("Completed", result.RootElement.GetProperty("phase").GetString());
    }

    /// <summary>
    /// The command line container carries the facade and still carries no general retention service
    /// and no background work, and the journal's phase authority reads the installation identity and
    /// the parent receipt through the same two types in the command line and in the serving host.
    /// </summary>
    [SkippableFact]
    public async Task The_command_composition_carries_the_facade_and_nothing_that_could_host_durable_work()
    {
        await using ServiceProvider provider = Compose(covenantFeature: true).BuildServiceProvider();

        Assert.Null(provider.GetService<IDataRetentionService>());

        // The framework's own Data Protection service is the one hosted service a container may carry.
        // Nothing of Arcanum's runs recovery or any other durable work in the command line process.
        Assert.DoesNotContain(
            provider.GetServices<IHostedService>(),
            static hosted => hosted.GetType().Namespace?.StartsWith("RetroDownfall.Arcanum", StringComparison.Ordinal) == true);

        provider.GetRequiredService<IGrimoireDbPassphraseSource>()
            .SetPassphrase(StoppedHostResetTestSupport.FixturePassphrase);

        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<StoppedHostFactoryErasure>());

        AssertTheJournalReadsTheIdentityAndTheParentThroughTheReset(scope.ServiceProvider);

        Assert.IsType<InstallationResetNestedTransitionReceiptResolver>(
            scope.ServiceProvider.GetRequiredService<IGrimoireOfflineTransitionParentReceiptResolver>());

        await Task.CompletedTask;
    }

    [SkippableFact]
    public async Task The_serving_composition_wires_the_journal_to_the_same_identity_reader_and_parent_resolver()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        await using ServiceProvider provider = builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });

        provider.GetRequiredService<IGrimoireDbPassphraseSource>()
            .SetPassphrase(StoppedHostResetTestSupport.FixturePassphrase);

        using IServiceScope scope = provider.CreateScope();

        AssertTheJournalReadsTheIdentityAndTheParentThroughTheReset(scope.ServiceProvider);

        Assert.IsType<InstallationResetNestedTransitionReceiptResolver>(
            scope.ServiceProvider.GetRequiredService<IGrimoireOfflineTransitionParentReceiptResolver>());

        await Task.CompletedTask;
    }

    /// <summary>
    /// A workspace reset never builds the Covenant graph, never reads the master key, and never leases
    /// the gate: the planner, the facade and the gate are all made to refuse if it tries.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_workspace_reset_never_touches_the_Covenant_graph(bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await RegisterCampaignAtCurrentDirectoryAsync();

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        byte[]? captured = null;

        CovenantAuthorityStartupReconciler.MasterKeyMaterialObserverForTests = material => captured = material;

        try
        {
            foreach (string[] arguments in new[]
            {
                new[] { "--json", "data", "factory-reset", "--workspace", "--dry-run" },
                new[] { "--json", "--yes", "data", "factory-reset", "--workspace", "--apply", "--force" },
            })
            {
                CliTestResult result = await RunAsync(
                    covenantFeature,
                    static services =>
                    {
                        StoppedHostResetTestSupport.RefuseToConstruct<StoppedHostCovenantAuthority>(services);

                        StoppedHostResetTestSupport.RefuseToConstruct<StoppedHostFactoryErasure>(services);

                        StoppedHostResetTestSupport.RefuseEveryCall<ICovenantOperationGate>(services);

                        StoppedHostResetTestSupport.RefuseTheMasterKeyRead(services);
                    },
                    arguments);

                string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

                Assert.True(result.ExitCode == (int)CliExitCode.Success, described);
            }
        }
        finally
        {
            CovenantAuthorityStartupReconciler.MasterKeyMaterialObserverForTests = null;
        }

        Assert.Null(captured);

        // A workspace reset has no daemon registration to remove.
        Assert.Equal(0, _mutations.Calls);
    }

    private static void AssertTheJournalReadsTheIdentityAndTheParentThroughTheReset(IServiceProvider services)
    {
        IGrimoireOfflineTransitionPhaseAuthority authority = services
            .GetRequiredService<IGrimoireOfflineTransitionPhaseAuthority>();

        object? reader = FieldOfType(authority, typeof(IInstallationResetDatabaseIdentityReader));

        object? parents = FieldOfType(authority, typeof(IGrimoireOfflineTransitionParentReceiptResolver));

        // The concrete reader in both: in the command line the interface maps to a service whose
        // issuer-free read answers unavailable, and a journal that cannot learn the installation's
        // identity cannot be opened at all.
        Assert.IsType<InstallationResetDatabaseIdentityReader>(reader);

        Assert.IsType<InstallationResetNestedTransitionReceiptResolver>(parents);
    }

    private static object? FieldOfType(object instance, Type interfaceType) =>
        instance.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => interfaceType.IsAssignableFrom(field.FieldType))
            .Select(field => field.GetValue(instance))
            .Single(static value => value is not null);

    private static LongRunningOperation Row(
        LongRunningOperationState state,
        int checkpointVersion,
        byte[]? payload) =>
        new(
            Guid.NewGuid(),
            LongRunningOperationKinds.DataRetentionFactoryReset,
            state,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: null,
            InferenceRunId: null,
            BudgetReservationId: null,
            IdempotencyClaimId: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            StartedAt: null,
            HeartbeatAt: null,
            CompletedAt: null,
            LeaseOwner: null,
            LeaseExpiresAt: null,
            AttemptCount: 0,
            CheckpointVersion: checkpointVersion,
            CheckpointPayload: payload,
            CheckpointReference: null,
            PublicSummary: "A factory reset.",
            TerminalErrorCode: null,
            Revision: 0);
}
