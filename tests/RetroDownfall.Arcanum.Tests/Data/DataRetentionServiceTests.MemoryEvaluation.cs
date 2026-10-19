using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Tests.ProvingGrounds;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public async Task Memory_evaluation_prune_counts_one_whole_report_across_selected_sources_and_session_owners(bool sessions, bool saga, bool lexicon)
    {
        RequireSqlCipher();

        var connection = (SqliteConnection)_db!.Database.GetDbConnection();

        (Guid owner, _) = await SeedSessionAsync(pinned: false);

        (Guid secondOwner, _) = await SeedSessionAsync(pinned: false);

        string first = await SeedGlobalSagaMemoryAsync();

        string second = await SeedGlobalSagaMemoryAsync();

        await SeedClaimAsync(1, first);

        await SeedClaimAsync(1, second);

        var lex = await SeedCuratableLexiconAsync("evaluation lexicon");

        var settings = CreatePruneSettings();

        if (sessions) settings.Retention.ArchivedSessions = EnabledRule();

        if (saga) settings.Retention.SagaMemories = EnabledRule();

        if (lexicon) settings.Retention.LexiconEntries = EnabledRule();

        var service = CreateService(settings);

        var request = new DataRetentionRequest(DataRetentionOperation.Prune);

        var baseline = await service.PlanAsync(request);

        var source = MemoryEvaluationEvidenceStoreTests.Source();

        var evaluation = await MemoryEvaluationLifecycleTests.SeedAsync(connection,
            [source with { SubjectId = first, CanonicalOwnerSessionId = owner.ToString("D").ToUpperInvariant() },
             source with { SubjectId = second, VersionId = "second-version", CanonicalOwnerSessionId = secondOwner.ToString("D").ToUpperInvariant() },
             source with { Store = MemoryEvaluationSource.Lexicon, SubjectId = lex.Entry.Id.ToString("N"), VersionId = "lexicon-version", ClaimId = "lexicon-claim", Role = MemoryEvaluationCoordinateRole.Dependency }]);

        await MemoryEvaluationLifecycleTests.ExecuteAsync(connection, "PRAGMA foreign_keys = OFF");

        var plan = await service.PlanAsync(request);

        Assert.Equal(baseline.DerivedRecords + 5, plan.DerivedRecords);

        var applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : "");

        Assert.True(applied.Value.Reconciled);

        Assert.Equal(plan.DerivedRecords, applied.Value.DerivedRecordsDeleted);

        await MemoryEvaluationLifecycleTests.AssertAbsentAsync(connection, evaluation.EvaluationId);
    }

    [SkippableFact]
    public async Task Memory_evaluation_Annals_status_and_factory_preview_include_all_three_groups()
    {
        RequireSqlCipher();

        var service = CreateService();

        var request = new DataRetentionRequest(DataRetentionOperation.FactoryReset);

        var before = await service.PlanAsync(request);

        var connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await MemoryEvaluationLifecycleTests.SeedAsync(connection, [MemoryEvaluationEvidenceStoreTests.Source()]);

        var plan = await service.PlanAsync(request);

        Assert.Equal(before.DerivedRecords + 3, plan.DerivedRecords);

        var status = await service.GetStatusAsync();

        Assert.Equal(3, Assert.Single(status.Items, item => item.DataClass == RetentionDataClass.Annals).Rows);
    }
}
