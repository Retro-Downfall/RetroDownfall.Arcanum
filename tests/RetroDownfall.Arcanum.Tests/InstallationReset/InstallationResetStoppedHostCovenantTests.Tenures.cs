using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Cli;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// What a second tenure of the same process may take for granted about the Covenant runtime the
/// first one put in place, what the key leaves behind, and what an operator is shown.
/// </summary>
public sealed partial class InstallationResetStoppedHostCovenantTests
{
    /// <summary>The fact of the catalog that moves between two tenures of one process.</summary>
    public enum MovedFact
    {
        Nothing = 0,

        DatasetGeneration = 1,

        AuthorityEpoch = 2,

        MasterKeyVersion = 3,
    }

    public static TheoryData<bool, MovedFact> MovedFactCases()
    {
        TheoryData<bool, MovedFact> cases = [];

        foreach (bool feature in new[] { false, true })
        {
            foreach (MovedFact moved in Enum.GetValues<MovedFact>())
            {
                cases.Add(feature, moved);
            }
        }

        return cases;
    }

    private static string StatementMoving(MovedFact moved) => moved switch
    {
        MovedFact.DatasetGeneration => "UPDATE covenant_state SET DatasetGeneration = randomblob(16);",
        MovedFact.AuthorityEpoch => "UPDATE covenant_authority_state SET AuthorityEpoch = AuthorityEpoch + 1;",
        MovedFact.MasterKeyVersion => "UPDATE covenant_authority_state SET CurrentMasterKeyVersion = CurrentMasterKeyVersion + 1;",
        _ => throw new ArgumentOutOfRangeException(nameof(moved)),
    };

    /// <summary>
    /// The first tenure of a process establishes the Covenant runtime and every later one verifies it
    /// against the catalog, so a fact that moved in between is refused as a changed plan and never
    /// initializes the runtime a second time.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(MovedFactCases))]
    public async Task A_second_tenure_of_one_process_verifies_the_runtime_the_first_established(
        bool covenantFeature,
        MovedFact moved)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await using ServiceProvider provider = Compose(covenantFeature).BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetStoppedHostDataService data = scope.ServiceProvider
            .GetRequiredService<IInstallationResetStoppedHostDataService>();

        Task<Result<DataRetentionPlan>> PlanAsync() => UnderStoppedHostAuthorityAsync(
            (issuer, token) => data.PlanUnderStoppedHostAuthorityAsync(
                new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
                issuer,
                token));

        Result<DataRetentionPlan> first = await PlanAsync();

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : string.Empty);

        // The lock is released between tenures, which is the window a catalog can move in.
        if (moved is not MovedFact.Nothing)
        {
            _ = await StoppedHostResetTestSupport.ExecuteAsync(StatementMoving(moved));
        }

        Result<DataRetentionPlan> second = await PlanAsync();

        if (moved is MovedFact.Nothing)
        {
            Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : string.Empty);

            Assert.Equal(first.Value.PlanId, second.Value.PlanId);

            Assert.Equal(first.Value.Covenant, second.Value.Covenant);

            return;
        }

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, second.Error.Code);
    }

    /// <summary>
    /// The same movement between the confirmation and the apply of one command is refused before the
    /// record exists, so the daemon registration is never touched.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(MovedFactCases))]
    public async Task A_fact_that_moves_between_the_plan_and_the_apply_is_refused_before_the_daemon_is_touched(
        bool covenantFeature,
        MovedFact moved)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        if (moved is MovedFact.Nothing)
        {
            return;
        }

        await InstallPriorGrimoireAsync(covenantFeature);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        int plans = 0;

        CliTestResult result = await RunAsync(
            covenantFeature,
            services => StoppedHostResetTestSupport.InterceptDataService(
                services,
                inner => new StoppedHostResetTestSupport.InterceptingStoppedHostDataService(inner)
                {
                    // After the plan the operator confirmed, and before the apply plans again.
                    AfterPlanAsync = async plan =>
                    {
                        if (plan.IsSuccess && Interlocked.Increment(ref plans) == 1)
                        {
                            _ = await StoppedHostResetTestSupport.ExecuteAsync(StatementMoving(moved));
                        }
                    },
                }),
            GlobalArguments(apply: true));

        string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains(ErrorCodes.Data.PlanChanged, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ErrorCodes.Covenant.LifecycleConflict, result.Output, StringComparison.Ordinal);

        Assert.Equal(0, _mutations.Calls);

        AssertNoActiveResetOrJournal();

        Assert.True(Directory.Exists(ArcanumPaths.GrimoireDirectory), described);

        AssertNoSidecars();
    }

    /// <summary>
    /// The UTF-8 copy of the master key the Covenant authority is built from is zeroed by the time the
    /// plan returns, whatever the plan went on to do.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_master_key_copy_a_global_plan_builds_is_zeroed_before_it_returns(bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        byte[]? captured = null;

        CovenantAuthorityStartupReconciler.MasterKeyMaterialObserverForTests = material => captured = material;

        try
        {
            CliTestResult result = await RunAsync(covenantFeature, GlobalArguments(apply: false));

            Assert.Equal(
                (int)CliExitCode.Success,
                result.ExitCode);
        }
        finally
        {
            CovenantAuthorityStartupReconciler.MasterKeyMaterialObserverForTests = null;
        }

        Assert.NotNull(captured);

        Assert.NotEmpty(captured);

        Assert.All(captured, static value => Assert.Equal(0, value));
    }

    /// <summary>
    /// The plan an operator reads says what the apply will bind: the five measured numbers on a line
    /// of their own, in the words the disclosure uses.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_human_dry_run_prints_the_measured_inventory_line(bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        CliTestResult result = await RunAsync(
            covenantFeature,
            "data",
            "factory-reset",
            "--global",
            "--dry-run");

        string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        string line = Assert.Single(
            result.Output.Split('\n'),
            static candidate => candidate.StartsWith("Covenant inventory:", StringComparison.Ordinal));

        Assert.Matches(
            @"^Covenant inventory: \d+ rows, \d+ managed files, \d+ local erasure artifacts, \d+ affected sessions; nonrevocable disclosures: (exactly|at least) \d+\s*$",
            line);

        // The labelled memory is part of what would be erased, so the inventory is not the empty one.
        Assert.DoesNotContain("Covenant inventory: 0 rows, 0 managed files, 0 local erasure artifacts", line, StringComparison.Ordinal);

        Assert.Contains(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            result.Error,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            result.Output,
            StringComparison.Ordinal);

        Assert.True(described.Length > 0);
    }
}
