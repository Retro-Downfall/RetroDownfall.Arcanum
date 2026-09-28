using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconLifecycleTests(GrimoireFixture fixture)
{
    [Fact]
    public async Task Deletion_identity_finds_active_and_retired_protected_rows_at_exact_scope_only()
    {
        await using CorrectionFixture test = new(fixture);

        LexiconEntryDetail before = await test.SeedAsync();

        ILexiconService service = test.Concrete;

        var active = await service.FindAllLifecycleIdentityForDeletionAsync("entity", LexiconScope.Global);

        Assert.True(active.IsSuccess, active.Error.Message);

        Assert.Equal(before.Entry.Id, active.Value);

        Assert.Null((await service.FindAllLifecycleIdentityForDeletionAsync("missing", LexiconScope.Global)).Value);

        Assert.Null((await service.FindAllLifecycleIdentityForDeletionAsync("entity", LexiconScope.ForResolvedCampaign(Guid.NewGuid()))).Value);

        await test.ProtectAsync();

        before = await test.ShowProtectedAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        Assert.True((await test.Service.RetireAsync(before.Target, lease)).IsSuccess);

        Assert.Equal(before.Entry.Id, (await service.FindAllLifecycleIdentityForDeletionAsync(" ENTITY ", LexiconScope.Global)).Value);

        Assert.Null((await service.GetByNameInScopeAsync("Entity", LexiconScope.Global)).Value);
    }

    [Fact]
    public async Task Deletion_identity_default_fails_closed()
    {
        ILexiconService legacy = new LegacyDeletionLexicon();

        Assert.Equal(ErrorCodes.Lexicon.SearchFailed, (await legacy.FindAllLifecycleIdentityForDeletionAsync("entity", LexiconScope.Global)).Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scribe_refuses_retired_exact_scope_without_changing_or_falling_through_to_global(bool campaign)
    {
        await using CorrectionFixture test = new(fixture);

        await test.SeedAsync();

        Guid campaignId = Guid.NewGuid();

        LexiconScope scope = campaign ? LexiconScope.ForResolvedCampaign(campaignId) : LexiconScope.Global;

        if (campaign)
        {
            Assert.True((await test.Concrete.UpsertAsync("Entity", "Person", ["campaign fact"], scope)).IsSuccess);
        }

        var inspected = await test.Service.ShowExactAsync(
            new(campaign ? LexiconScopeKind.Campaign : LexiconScopeKind.Global, scope.CampaignId), "Entity", null);

        Assert.True(inspected.IsSuccess, inspected.Error.Message);

        var retired = await test.Service.RetireAsync(inspected.Value.Value.Target, null);

        Assert.True(retired.IsSuccess, retired.Error.Message);

        string[] snapshot = await test.SnapshotAsync();

        var result = await test.Concrete.UpsertAsync("Entity", "Person", ["forbidden revival"], scope);

        Assert.Equal(ErrorCodes.Lexicon.RetiredMutationRefused, result.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scribe_labeled_exact_no_op_succeeds_but_content_change_is_refused(bool change)
    {
        await using CorrectionFixture test = new(fixture);

        await test.SeedAsync();

        await test.ProtectAsync();

        string[] snapshot = await test.SnapshotAsync();

        var result = await test.Concrete.UpsertAsync(" entity ", null, change ? ["new fact"] : [" alpha ", "beta"], LexiconScope.Global);

        if (change)
        {
            Assert.Equal(ErrorCodes.Lexicon.ProtectedMutationRefused, result.Error.Code);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Error.Message);
        }

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Scribe_create_and_merge_publish_format_two_with_resulting_ordinal_provenance()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail before = await test.SeedAsync();

        AnnalClaimVersion asserted = Assert.Single(before.AnnalHistory);

        Assert.Equal(AnnalContentHashFormat.LexiconStructuredSnapshot, asserted.ContentHashFormat);

        Assert.Equal([0, 1], before.HistoricalFactProvenance.Select(source => source.FactOrdinal));

        AttachmentMemoryProvenance source = before.Entry.FactProvenance![0].Source with { AttachmentId = Guid.NewGuid(), ContentHash = "second-hash" };

        var merged = await test.Concrete.UpsertAsync("ENTITY", " Person ", [" beta ", " gamma ", "gamma"], source, LexiconScope.Global);

        Assert.True(merged.IsSuccess, merged.Error.Message);

        LexiconEntryDetail after = await test.ShowAsync();

        Assert.Equal(["alpha", "beta", "gamma"], after.Entry.Facts);

        Assert.Equal("Person", after.Entry.Type);

        Assert.Equal(2, after.CurationGeneration);

        Assert.True(after.Entry.UpdatedAt > before.Entry.UpdatedAt);

        Assert.Equal(2, after.AnnalHistory.Length);

        AnnalClaimVersion correction = after.AnnalHistory[^1];

        Assert.Equal(AnnalContentHashFormat.LexiconStructuredSnapshot, correction.ContentHashFormat);

        Assert.Equal(AnnalOrigin.AgentAsserted, correction.Origin);

        Assert.Equal(AnnalOperation.Correct, correction.Operation);

        var sources = after.HistoricalFactProvenance.Where(item => item.AnnalVersionId == correction.VersionId).ToArray();

        Assert.Equal([0, 1, 2], sources.Select(item => item.FactOrdinal));

        Assert.Equal(before.Entry.FactProvenance[0].Source.AttachmentId, sources[0].AttachmentId);

        Assert.All(sources.Skip(1), item => Assert.Equal(source.AttachmentId, item.AttachmentId));

        string[] snapshot = await test.SnapshotAsync();

        var noOp = await test.Concrete.UpsertAsync("entity", "Person", ["gamma", "alpha"], source with { ContentHash = "ignored-no-op" }, LexiconScope.Global);

        Assert.True(noOp.IsSuccess, noOp.Error.Message);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_disabled_scribe_maintains_existing_curation_evidence_but_does_not_open_claimless_history(bool curated)
    {
        await using CorrectionFixture test = new(fixture);

        LexiconEntryDetail before = await test.SeedAsync();

        if (curated)
        {
            var retired = await test.Service.RetireAsync(before.Target, null);

            Assert.True(retired.IsSuccess, retired.Error.Message);

            var reinstated = await test.Service.ReinstateAsync(retired.Value.Entry.Target, null);

            Assert.True(reinstated.IsSuccess, reinstated.Error.Message);

            before = reinstated.Value.Entry;
        }

        var result = await test.Concrete.UpsertAsync("Entity", "Person", ["gamma"], before.Entry.FactProvenance![0].Source, LexiconScope.Global);

        Assert.True(result.IsSuccess, result.Error.Message);

        LexiconEntryDetail after = await test.ShowAsync();

        Assert.Equal(curated ? 4 : 0, after.AnnalHistory.Length);

        Assert.Equal(before.CurationGeneration + 1, after.CurationGeneration);

        if (curated)
        {
            Assert.Equal(AnnalContentHashFormat.LexiconStructuredSnapshot, after.AnnalHistory[^1].ContentHashFormat);

            Assert.Equal([0, 1, 2], after.HistoricalFactProvenance.Where(source => source.AnnalVersionId == after.AnnalHistory[^1].VersionId).Select(source => source.FactOrdinal));
        }
    }

    [Fact]
    public async Task Scribe_refuses_inconsistent_current_projection_before_a_no_op()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        await test.SeedAsync();

        await test.ExecuteAsync("UPDATE lexicon_entries SET FactsText = 'not the canonical facts'");

        string[] snapshot = await test.SnapshotAsync();

        var result = await test.Concrete.UpsertAsync("Entity", "general", ["alpha"], LexiconScope.Global);

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scribe_evidence_failure_rolls_back_row_fts_and_current_and_historical_provenance(bool merge)
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail before = await test.SeedAsync();

        string[] snapshot = await test.SnapshotAsync();

        await test.ExecuteAsync("CREATE TEMP TRIGGER refuse_scribe_evidence AFTER INSERT ON lexicon_annal_fact_provenance BEGIN SELECT RAISE(ABORT, 'scribe evidence failure'); END");

        var result = await test.Concrete.UpsertAsync(merge ? "Entity" : "Other", "Person", ["gamma"], before.Entry.FactProvenance![0].Source, LexiconScope.Global);

        Assert.Equal(ErrorCodes.Lexicon.WriteFailed, result.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());

        Assert.Equal(0L, await test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'gamma'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retirement_and_reinstatement_preserve_content_and_publish_immutable_evidence(bool capture)
    {
        await using CorrectionFixture test = new(fixture, capture);

        await test.SeedAsync();

        await test.ExecuteAsync("UPDATE lexicon_entries SET PinnedAtUtc = '2026-09-01T00:00:00.0000000Z'");

        LexiconEntryDetail before = await test.ShowAsync();

        var retired = await test.Service.RetireAsync(before.Target, null);

        Assert.True(retired.IsSuccess, retired.Error.Message);

        LexiconEntryDetail tombstoned = retired.Value.Entry;

        Assert.Equal(LexiconCurationOutcomeKind.Applied, retired.Value.Outcome);

        Assert.NotNull(tombstoned.Lifecycle.RetiredAtUtc);

        Assert.Equal(TimeSpan.Zero, tombstoned.Lifecycle.RetiredAtUtc.Value.Offset);

        Assert.Equal(UtcInstantText.Format(tombstoned.Lifecycle.RetiredAtUtc.Value), await test.ScalarAsync("SELECT RetiredAtUtc FROM lexicon_entries"));

        Assert.Equal(before.Entry.UpdatedAt, tombstoned.Entry.UpdatedAt);

        Assert.Equal(before.Lifecycle.PinnedAtUtc, tombstoned.Lifecycle.PinnedAtUtc);

        Assert.Equal(before.Entry.Facts, tombstoned.Entry.Facts);

        Assert.Equal(before.Entry.FactProvenance, tombstoned.Entry.FactProvenance);

        Assert.Equal(before.SnapshotDigest, tombstoned.SnapshotDigest);

        Assert.Equal(2, tombstoned.CurationGeneration);

        Assert.Equal(0L, await test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'alpha'"));

        Assert.Null((await test.Concrete.GetByNameAsync("entity", LexiconScope.Global)).Value);

        Assert.Empty((await test.Concrete.MatchEntitiesAsync(["alpha"], 10, LexiconScope.Global)).Value);

        AnnalClaimVersion tombstone = tombstoned.AnnalHistory[^1];

        Assert.Equal(AnnalOperation.Retire, tombstone.Operation);

        Assert.Equal(AnnalOrigin.OperatorStated, tombstone.Origin);

        Assert.True(Enum.IsDefined(tombstone.ContentHashFormat));

        Assert.Null(tombstone.ContentHash);

        Assert.DoesNotContain(tombstoned.HistoricalFactProvenance, source => source.AnnalVersionId == tombstone.VersionId);

        Assert.Equal(AnnalContentHashFormat.LexiconStructuredSnapshot, tombstoned.AnnalHistory[^2].ContentHashFormat);

        var reinstated = await test.Service.ReinstateAsync(tombstoned.Target, null);

        Assert.True(reinstated.IsSuccess, reinstated.Error.Message);

        LexiconEntryDetail after = reinstated.Value.Entry;

        Assert.Null(after.Lifecycle.RetiredAtUtc);

        Assert.Equal(3, after.CurationGeneration);

        Assert.Equal(before.Entry.UpdatedAt, after.Entry.UpdatedAt);

        Assert.Equal(before.Entry.FactProvenance, after.Entry.FactProvenance);

        Assert.Equal(before.Lifecycle.PinnedAtUtc, after.Lifecycle.PinnedAtUtc);

        Assert.Equal(1L, await test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'alpha'"));

        Assert.Equal(before.Entry.Id, (await test.Concrete.GetByNameAsync("entity", LexiconScope.Global)).Value!.Id);

        AnnalClaimVersion restatement = after.AnnalHistory[^1];

        Assert.Equal(AnnalOperation.Correct, restatement.Operation);

        Assert.Equal(AnnalOrigin.OperatorStated, restatement.Origin);

        Assert.Equal(AnnalContentHashFormat.LexiconStructuredSnapshot, restatement.ContentHashFormat);

        Assert.Equal(before.SnapshotDigest, Convert.ToHexString(restatement.ContentHash!));

        Assert.Equal([0, 1], after.HistoricalFactProvenance.Where(source => source.AnnalVersionId == restatement.VersionId).Select(source => source.FactOrdinal));
    }

    [Fact]
    public async Task Desired_state_no_ops_require_the_current_exact_target()
    {
        await using CorrectionFixture test = new(fixture);

        LexiconEntryDetail before = await test.SeedAsync();

        var active = await test.Service.ReinstateAsync(before.Target, null);

        Assert.True(active.IsSuccess, active.Error.Message);

        Assert.Equal(LexiconCurationOutcomeKind.NotRetired, active.Value.Outcome);

        var retired = await test.Service.RetireAsync(before.Target, null);

        Assert.True(retired.IsSuccess, retired.Error.Message);

        string[] snapshot = await test.SnapshotAsync();

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, (await test.Service.RetireAsync(before.Target, null)).Error.Code);

        Assert.Equal(LexiconCurationOutcomeKind.AlreadyRetired, (await test.Service.RetireAsync(retired.Value.Entry.Target, null)).Value.Outcome);

        Assert.Equal(snapshot, await test.SnapshotAsync());

        var reinstated = await test.Service.ReinstateAsync(retired.Value.Entry.Target, null);

        Assert.True(reinstated.IsSuccess, reinstated.Error.Message);

        snapshot = await test.SnapshotAsync();

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, (await test.Service.ReinstateAsync(retired.Value.Entry.Target, null)).Error.Code);

        Assert.Equal(LexiconCurationOutcomeKind.NotRetired, (await test.Service.ReinstateAsync(reinstated.Value.Entry.Target, null)).Value.Outcome);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protected_lifecycle_requires_write_authority_and_preserves_the_label(bool reinstate)
    {
        await using CorrectionFixture test = new(fixture);

        await test.SeedAsync();

        await test.ProtectAsync();

        LexiconEntryDetail before = await test.ShowProtectedAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        if (reinstate)
        {
            var retired = await test.Service.RetireAsync(before.Target, lease);

            Assert.True(retired.IsSuccess, retired.Error.Message);

            before = retired.Value.Entry;
        }

        string[] snapshot = await test.SnapshotAsync();

        var refused = reinstate ? await test.Service.ReinstateAsync(before.Target, null) : await test.Service.RetireAsync(before.Target, null);

        Assert.Equal(ErrorCodes.Lexicon.ProtectedMutationRefused, refused.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());

        var applied = reinstate ? await test.Service.ReinstateAsync(before.Target, lease) : await test.Service.RetireAsync(before.Target, lease);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(before.Target.SensitivityLabel, applied.Value.Entry.Target.SensitivityLabel);

        Assert.Equal(before.Entry.UpdatedAt, applied.Value.Entry.Entry.UpdatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_before_commit_rolls_back_lifecycle_and_all_evidence(bool reinstate)
    {
        await using CorrectionFixture test = new(fixture);

        LexiconEntryDetail before = await test.SeedAsync();

        if (reinstate)
        {
            var retired = await test.Service.RetireAsync(before.Target, null);

            Assert.True(retired.IsSuccess, retired.Error.Message);

            before = retired.Value.Entry;
        }

        string[] snapshot = await test.SnapshotAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write) { StaleAfter = 1 };

        await using CovenantWriteLease lease = new(registration);

        var result = reinstate ? await test.Service.ReinstateAsync(before.Target, lease) : await test.Service.RetireAsync(before.Target, lease);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

        Assert.Equal(2, registration.Revalidations);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_rejects_exhausted_generation_before_desired_state_handling(bool retire)
    {
        await using CorrectionFixture test = new(fixture);

        await test.SeedAsync();

        await test.ExecuteAsync("UPDATE lexicon_entries SET CurationGeneration = 9223372036854775807");

        LexiconEntryDetail before = await test.ShowAsync();

        string[] snapshot = await test.SnapshotAsync();

        var result = retire ? await test.Service.RetireAsync(before.Target, null) : await test.Service.ReinstateAsync(before.Target, null);

        Assert.Equal(ErrorCodes.Lexicon.CurationGenerationExhausted, result.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("identity")]
    [InlineData("generation")]
    [InlineData("digest")]
    [InlineData("lifecycle")]
    [InlineData("head")]
    [InlineData("label")]
    public async Task Lifecycle_compares_every_target_component_even_when_already_active(string field)
    {
        await using CorrectionFixture test = new(fixture);

        await test.SeedAsync();

        await test.ProtectAsync(withHead: true);

        LexiconEntryDetail before = await test.ShowProtectedAsync();

        LexiconCurationTarget target = field switch
        {
            "scope" => before.Target with { Scope = new(LexiconScopeKind.Campaign, Guid.NewGuid()) },
            "identity" => before.Target with { EntryId = Guid.NewGuid() },
            "generation" => before.Target with { CurationGeneration = 2 },
            "digest" => before.Target with { SnapshotDigest = new string('A', 64) },
            "lifecycle" => before.Target with { Lifecycle = before.Lifecycle with { PinnedAtUtc = DateTimeOffset.UtcNow } },
            "head" => before.Target with { AnnalHead = before.Target.AnnalHead with { VersionId = "other" } },
            _ => before.Target with { SensitivityLabel = before.Target.SensitivityLabel with { ArtifactRevision = 2 } },
        };

        using LeaseRegistration registration = new(CovenantLeaseKind.Write)
        {
            Snapshot = new(Guid.NewGuid(), 1, CovenantLeaseKind.Write, CovenantLeaseCoverage.Scoped,
                field == "scope" ? CovenantOperationScope.ForCampaign(target.Scope.CampaignId!.Value) : CovenantOperationScope.Global,
                null, 1, 1, 0, null, null, null, null, null, false),
        };

        await using CovenantWriteLease lease = new(registration);

        string[] snapshot = await test.SnapshotAsync();

        var result = await test.Service.ReinstateAsync(target, lease);

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, result.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Annals_publication_failure_rolls_back_lifecycle_and_fts(bool reinstate)
    {
        await using CorrectionFixture test = new(fixture);

        LexiconEntryDetail before = await test.SeedAsync();

        if (reinstate)
        {
            before = (await test.Service.RetireAsync(before.Target, null)).Value.Entry;
        }

        string[] snapshot = await test.SnapshotAsync();

        await test.ExecuteAsync("CREATE TEMP TRIGGER refuse_lifecycle AFTER INSERT ON annal_versions WHEN NEW.OriginCode = 1 BEGIN SELECT RAISE(ABORT, 'lifecycle evidence failure'); END");

        var result = reinstate ? await test.Service.ReinstateAsync(before.Target, null) : await test.Service.RetireAsync(before.Target, null);

        Assert.Equal(ErrorCodes.Lexicon.WriteFailed, result.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());

        Assert.Equal(reinstate ? 0L : 1L, await test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'alpha'"));
    }
}

internal sealed class LegacyDeletionLexicon : ILexiconService
{
    public Task<Result<LexiconEntryDto>> UpsertAsync(string name, string? type, IReadOnlyList<string> facts, LexiconScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<bool>> DeleteByNameAsync(string name, LexiconScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException("An unavailable identity lookup must not authorize deletion.");

    public Task<Result<IReadOnlyList<LexiconEntryDto>>> MatchEntitiesAsync(IReadOnlyList<string> entities, int limit, LexiconScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<LexiconEntryDto?>> GetByNameAsync(string name, LexiconScope scope, CancellationToken cancellationToken = default) => Task.FromResult(Result<LexiconEntryDto?>.Success(null));

    public Task<Result<LexiconEntryDto?>> GetByNameInScopeAsync(string name, LexiconScope scope, CancellationToken cancellationToken = default) => GetByNameAsync(name, scope, cancellationToken);
}

internal sealed class LifecyclePurger(CorrectionFixture owner, ArcanumDbContext db, string disposition = "purged") : ICovenantSensitiveArtifactPurger
{
    public async ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets, CancellationToken cancellationToken = default)
    {
        CovenantSensitivePurgeTarget target = Assert.Single(targets);

        Assert.Equal(SensitiveArtifactKind.Lexicon, target.Kind);

        if (disposition == "failed")
        {
            return new Error(ErrorCodes.Covenant.StaleSnapshot, "Injected stale purge authority.");
        }

        if (disposition == "blocked")
        {
            return Result<CovenantSensitivePurgeOutcome>.Success(new(
                [new(target.ArtifactId, target.Kind, CovenantSensitivePurgeDisposition.Blocked, CovenantErasureBlocker.AuthorityStale)],
                CovenantArtifactErasureProgress.Empty));
        }

        ArtifactSensitivityLabel? label = (await ArtifactSensitivityLedger.ReadLabelWithinAsync(
            owner.Connection, null, target.Kind, target.ArtifactId, cancellationToken)).Value;

        if (label is null)
        {
            return Result<CovenantSensitivePurgeOutcome>.Success(new(
                [new(target.ArtifactId, target.Kind, CovenantSensitivePurgeDisposition.Unlabeled, CovenantErasureBlocker.None)],
                CovenantArtifactErasureProgress.Empty));
        }

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize), cancellationToken)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForExclusive(
            lease, CovenantExclusiveOperation.CovenantFamilyReinitialize).Value;

        CovenantProtectedArtifactErasureKernel kernel = new(
            new CovenantConnectionSource(db, new RecordingScopedOrdinaryConnectionFactory()),
            CovenantSqliteConnectionInitializer.Instance, TimeProvider.System);

        var result = await kernel.ErasePageAsync(new(CovenantOperationGateFixture.DatasetGeneration,
            [new(label.ArtifactId, label.ArtifactKind, label.SessionId, label.LabelId, label,
                label.ArtifactContentDigest, label.ArtifactRevision)]), authority, cancellationToken);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0L, await owner.ScalarAsync("SELECT count(*) FROM lexicon_entries"));

        Assert.Equal(0L, await owner.ScalarAsync("SELECT count(*) FROM artifact_sensitivity"));

        return Result<CovenantSensitivePurgeOutcome>.Success(new(
            [new(target.ArtifactId, target.Kind, CovenantSensitivePurgeDisposition.Purged, CovenantErasureBlocker.None)], result.Value));
    }
}
