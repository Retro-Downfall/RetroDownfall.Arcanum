using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_pruning_clears_its_clean_summary_artifact_and_skips_labelled_summary_dependencies(bool protectedSummary)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        ISessionSummaryArtifactStore summaries = new SessionDerivedArtifactStore(
            new CovenantConnectionSource(_db!, FixtureOrdinaryConnectionFactory.For(_db!)),
            CovenantSqliteConnectionInitializer.Instance);

        Result<SessionDerivedArtifactWriteReceipt> summary = await summaries.ReplaceAsync(
            new SessionSummaryArtifactWrite(sessionId, "a retained summary of the pruned source", null,
                protectedSummary ? ContentSensitivity.CovenantDerived : ContentSensitivity.None,
                GenerationProvenance.CreateExact(protectedSummary ? new[] { Guid.NewGuid() } : Array.Empty<Guid>())),
            CancellationToken.None);

        Assert.True(summary.IsSuccess, summary.Error.Message);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(protectedSummary ? 1 : 0, await CountAsync("Entries", "Id", Canonical(entryId)));

        Assert.Equal(protectedSummary ? 1 : 0, await CountAsync("session_summary_artifacts", "ArtifactId", Canonical(summary.Value.ArtifactId)));

        Assert.Equal(protectedSummary ? 1 : 0, await CountAsync("session_summary_state", "SessionId", Canonical(sessionId)));

        if (protectedSummary)
        {
            Assert.Equal(1, await CountAsync("artifact_sensitivity", "ArtifactId", Canonical(summary.Value.ArtifactId)));
        }
    }
}
