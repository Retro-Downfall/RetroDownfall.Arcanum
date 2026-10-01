using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed class SagaMemoryReviewServiceTests
{
    [SkippableFact]
    public async Task Queue_is_newest_first_bounded_and_continues_over_its_frozen_frontier()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.Parse(
            "2026-09-28T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        await InsertAsync(harness, "m-1", "first", created).ConfigureAwait(false);

        await InsertAsync(harness, "m-2", "second", created.AddMinutes(1)).ConfigureAwait(false);

        await InsertAsync(harness, "m-3", "third", created.AddMinutes(2)).ConfigureAwait(false);

        SagaMemoryReviewService service = CreateService(harness);

        Result<SagaReviewPageDto> first = await service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, CampaignId: null, Limit: 2, Cursor: null),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(["m-3", "m-2"], first.Value.Items.Select(static item => item.SubjectId));

        SagaReviewItemDto newest = first.Value.Items[0];

        Assert.NotNull(newest.Current);

        Assert.Equal("third", newest.Current.Memory.Content);

        Assert.Equal("saga-extraction", newest.Current.Memory.Source);

        Assert.Equal(SagaMemoryScopeKind.Global, newest.Current.Memory.ScopeKind);

        Assert.Equal(newest.VersionId, newest.Current.Claim?.CurrentVersionId);

        Assert.True(first.Value.Truncated);

        Assert.NotNull(first.Value.NextCursor);

        Assert.All(first.Value.Items, static item => Assert.True(item.IsCurrent));

        await InsertAsync(harness, "m-4", "arrived after the frozen page", created.AddMinutes(3))
            .ConfigureAwait(false);

        Result<SagaReviewPageDto> second = await service.ListAsync(
            new SagaReviewListRequest(
                SagaMemoryScopeKind.Global,
                CampaignId: null,
                Limit: 2,
                first.Value.NextCursor),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(second.IsSuccess, second.Error.Message);

        SagaReviewItemDto remaining = Assert.Single(second.Value.Items);

        Assert.Equal("m-1", remaining.SubjectId);

        Assert.Equal(first.Value.FrozenThroughEventSequence, second.Value.FrozenThroughEventSequence);

        Assert.False(second.Value.Truncated);

        Assert.Null(second.Value.NextCursor);
    }

    [SkippableFact]
    public async Task High_revision_claim_returns_only_bounded_current_snapshot_with_exact_head()
    {
        const int RevisionCount = 64;

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.Parse(
            "2026-09-28T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        string content = "revision-1";

        await InsertAsync(harness, "m-1", content, created).ConfigureAwait(false);

        for (int revision = 2; revision <= RevisionCount; revision++)
        {
            string replacement = $"revision-{revision}";

            SagaCurationOutcome corrected = await harness.Store.CorrectAsync(
                "m-1",
                AnnalContentDigest.ForSagaMemory(content),
                replacement,
                harness.Embedding(revision),
                created.AddMinutes(revision),
                CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(SagaCurationOutcomeKind.Applied, corrected.Kind);

            content = replacement;
        }

        AnnalClaimHead expectedHead = (await harness.Annals.GetClaimAsync(
            AnnalSubjectStore.Saga,
            "m-1",
            CancellationToken.None).ConfigureAwait(false))!;

        Assert.Equal(RevisionCount, expectedHead.CurrentRevision);

        Assert.Equal(
            RevisionCount,
            await harness.CountAsync("annal_versions", "1 = 1").ConfigureAwait(false));

        SagaMemoryReviewService service = CreateService(harness);

        Result<SagaReviewPageDto> page = await service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, CampaignId: null, Limit: 1, Cursor: null),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(page.IsSuccess, page.Error.Message);

        SagaReviewItemDto item = Assert.Single(page.Value.Items);

        SagaReviewCurrentDto current = Assert.IsType<SagaReviewCurrentDto>(item.Current);

        Assert.Equal(content, current.Memory.Content);

        Assert.Equal(expectedHead, current.Claim);

        Assert.Equal(expectedHead.CurrentVersionId, item.VersionId);

        Assert.Equal(expectedHead.CurrentRevision, item.Revision);

        Assert.Null(typeof(SagaReviewCurrentDto).GetProperty("History"));
    }

    [SkippableFact]
    public async Task Concurrent_first_lists_converge_on_one_persisted_marker_generation()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        await InsertAsync(
            harness,
            "m-1",
            "remembered",
            DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        await using ArcanumDbContext sibling = harness.CreateSiblingContext();

        ReviewRuntime firstRuntime = CreateRuntime(harness.Context);

        ReviewRuntime secondRuntime = CreateRuntime(sibling);

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result<SagaReviewPageDto>> first = ListAfterReleaseAsync(firstRuntime.Service, start.Task);

        Task<Result<SagaReviewPageDto>> second = ListAfterReleaseAsync(secondRuntime.Service, start.Task);

        start.TrySetResult();

        Result<SagaReviewPageDto>[] results = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Assert.All(results, static result => Assert.True(result.IsSuccess, result.Error.Message));

        MemoryReviewObservationTokenFacts firstObservation = firstRuntime.Codec.ReadObservation(
            Assert.Single(results[0].Value.Items).ObservationToken).Value;

        MemoryReviewObservationTokenFacts secondObservation = firstRuntime.Codec.ReadObservation(
            Assert.Single(results[1].Value.Items).ObservationToken).Value;

        Assert.Equal(firstObservation.MarkerGeneration, secondObservation.MarkerGeneration);

        Assert.Equal(
            1,
            await harness.CountAsync(
                "annal_review_markers",
                "SubjectStoreCode = 1 AND ScopeKindCode = 1 AND CampaignId IS NULL").ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task Campaign_queue_reports_only_the_exact_campaign_scope()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        Guid campaignSession = await harness.SessionBoundToNewCampaignAsync().ConfigureAwait(false);

        await InsertAsync(
            harness,
            "campaign-memory",
            "campaign",
            DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            campaignSession).ConfigureAwait(false);

        await InsertAsync(
            harness,
            "global-memory",
            "global",
            DateTimeOffset.Parse("2026-09-28T12:01:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        SagaMemoryCurationRow campaignRow = (await harness.Store.ReadCurationRowAsync(
            "campaign-memory",
            CancellationToken.None).ConfigureAwait(false))!;

        SagaMemoryReviewService service = CreateRuntime(harness).Service;

        Result<SagaReviewPageDto> result = await service.ListAsync(
            new SagaReviewListRequest(
                SagaMemoryScopeKind.Campaign,
                campaignRow.Memory.ScopeCampaignId,
                Limit: 10,
                Cursor: null),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.IsSuccess, result.Error.Message);

        SagaReviewItemDto item = Assert.Single(result.Value.Items);

        Assert.Equal("campaign-memory", item.SubjectId);

        Assert.Equal(SagaMemoryScopeKind.Campaign, item.ScopeKind);

        Assert.Equal(campaignRow.Memory.ScopeCampaignId, item.CampaignId);
    }

    [SkippableFact]
    public async Task Observations_bound_to_another_store_or_scope_are_rejected_before_planning()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        await InsertAsync(
            harness,
            "m-1",
            "remembered",
            DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        ReviewRuntime runtime = CreateRuntime(harness);

        SagaReviewItemDto item = Assert.Single((await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        MemoryReviewObservationTokenFacts observed = runtime.Codec.ReadObservation(item.ObservationToken).Value;

        string wrongStore = runtime.Codec.IssueObservation(
            observed with { Store = MemoryReviewStore.Lexicon }).Value;

        SagaReviewBulkPrepareRequest wrongStoreRequest = new(
            Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision(wrongStore, ReplacementContent: null)]);

        Result<MemoryReviewBulkPlanDto> storeResult = await runtime.Service.PrepareAsync(
            wrongStoreRequest,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(storeResult.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.InvalidToken, storeResult.Error.Code);

        SagaReviewBulkPrepareRequest wrongScopeRequest = new(
            Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC"),
            SagaMemoryScopeKind.LegacyUnresolved,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision(item.ObservationToken, ReplacementContent: null)]);

        Result<MemoryReviewBulkPlanDto> scopeResult = await runtime.Service.PrepareAsync(
            wrongScopeRequest,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(scopeResult.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.InvalidToken, scopeResult.Error.Code);
    }

    [SkippableFact]
    public async Task Distinct_tokens_for_the_same_exact_version_are_rejected_before_mutation()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        await InsertAsync(
            harness,
            "m-1",
            "remembered",
            DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        ReviewRuntime runtime = CreateRuntime(harness);

        SagaReviewItemDto observed = Assert.Single((await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        MemoryReviewObservationTokenFacts facts = runtime.Codec
            .ReadObservation(observed.ObservationToken).Value;

        runtime.Time.Advance(TimeSpan.FromSeconds(1));

        string reissued = runtime.Codec.IssueObservation(facts).Value;

        Assert.NotEqual(observed.ObservationToken, reissued);

        SagaReviewBulkPrepareRequest request = new(
            Guid.Parse("CDCDCDCD-CDCD-CDCD-CDCD-CDCDCDCDCDCD"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [
                new SagaReviewDecision(observed.ObservationToken, ReplacementContent: null),
                new SagaReviewDecision(reissued, ReplacementContent: null),
            ]);

        Result<MemoryReviewBulkPlanDto> refused = await runtime.Service.PrepareAsync(
            request,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.InvalidToken, refused.Error.Code);

        Assert.Equal(
            0,
            await harness.CountAsync("annal_review_decision_receipts", "1 = 1").ConfigureAwait(false));

        Assert.Equal(
            0,
            await harness.CountAsync(
                "annal_review_markers",
                "SubjectStoreCode = 1 AND ReviewedThroughSequence <> 0").ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task Confirm_records_only_review_and_marker_waits_for_every_older_gap()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.Parse(
            "2026-09-28T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        await InsertAsync(harness, "m-1", "first", created).ConfigureAwait(false);

        await InsertAsync(harness, "m-2", "second", created.AddMinutes(1)).ConfigureAwait(false);

        await InsertAsync(harness, "m-3", "third", created.AddMinutes(2)).ConfigureAwait(false);

        ReviewRuntime runtime = CreateRuntime(harness);

        SagaReviewPageDto initial = (await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        SagaReviewBulkPrepareRequest newestRequest = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision(initial.Items[0].ObservationToken, ReplacementContent: null)]);

        MemoryReviewBulkPlanDto newestPlan = (await runtime.Service.PrepareAsync(
            newestRequest,
            CancellationToken.None).ConfigureAwait(false)).Value;

        Result<MemoryReviewBulkResultDto> newest = await runtime.Service.ApplyAsync(
            new SagaReviewBulkApplyRequest(newestRequest, newestPlan.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(newest.IsSuccess, newest.Error.Message);

        Assert.Equal(0, newest.Value.ReviewedThroughEventSequence);

        SagaMemoryCurationRow unchanged = (await harness.Store.ReadCurationRowAsync(
            "m-3",
            CancellationToken.None).ConfigureAwait(false))!;

        Assert.Equal("third", unchanged.Memory.Content);

        Assert.Null(unchanged.Lifecycle.RetiredAtUtc);

        Assert.Null(unchanged.Lifecycle.PinnedAtUtc);

        SagaReviewPageDto remaining = (await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        Assert.Equal(["m-2", "m-1"], remaining.Items.Select(static item => item.SubjectId));

        SagaReviewBulkPrepareRequest gapsRequest = new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            remaining.Items.Select(static item => new SagaReviewDecision(
                item.ObservationToken,
                ReplacementContent: null)).ToArray());

        MemoryReviewBulkPlanDto gapsPlan = (await runtime.Service.PrepareAsync(
            gapsRequest,
            CancellationToken.None).ConfigureAwait(false)).Value;

        MemoryReviewBulkResultDto completed = (await runtime.Service.ApplyAsync(
            new SagaReviewBulkApplyRequest(gapsRequest, gapsPlan.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false)).Value;

        Assert.Equal(initial.FrozenThroughEventSequence, completed.ReviewedThroughEventSequence);

        SagaReviewPageDto empty = (await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        Assert.Empty(empty.Items);
    }

    [SkippableFact]
    public async Task Exact_apply_replays_from_receipts_after_the_prepared_token_expires()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        await InsertAsync(
            harness,
            "m-1",
            "remembered",
            DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        ReviewRuntime runtime = CreateRuntime(harness);

        SagaReviewItemDto item = Assert.Single((await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        SagaReviewBulkPrepareRequest request = new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision(item.ObservationToken, ReplacementContent: null)]);

        MemoryReviewBulkPlanDto plan = (await runtime.Service.PrepareAsync(
            request,
            CancellationToken.None).ConfigureAwait(false)).Value;

        MemoryReviewBulkResultDto first = (await runtime.Service.ApplyAsync(
            new SagaReviewBulkApplyRequest(request, plan.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false)).Value;

        runtime.Time.Advance(MemoryReviewLimits.TokenLifetime + TimeSpan.FromSeconds(1));

        Result<MemoryReviewBulkResultDto> replayed = await runtime.Service.ApplyAsync(
            new SagaReviewBulkApplyRequest(request, plan.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(replayed.IsSuccess, replayed.Error.Message);

        Assert.True(replayed.Value.Replayed);

        Assert.Equal(first.Items, replayed.Value.Items);

        Assert.Equal(first.ReviewedThroughEventSequence, replayed.Value.ReviewedThroughEventSequence);

        SagaReviewBulkPrepareRequest conflicting = request with { Action = MemoryReviewAction.Pin };

        Result<MemoryReviewBulkResultDto> conflict = await runtime.Service.ApplyAsync(
            new SagaReviewBulkApplyRequest(conflicting, plan.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(conflict.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.RequestReuse, conflict.Error.Code);
    }

    [SkippableFact]
    public async Task Identical_correction_is_acknowledged_as_unchanged_without_a_replacement()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        await InsertAsync(
            harness,
            "m-1",
            "remembered",
            DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        ReviewRuntime runtime = CreateRuntime(harness);

        SagaReviewItemDto observed = Assert.Single((await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        SagaReviewBulkPrepareRequest request = new(
            Guid.Parse("34343434-3434-3434-3434-343434343434"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Correct,
            [new SagaReviewDecision(observed.ObservationToken, "remembered")]);

        Result<MemoryReviewBulkPlanDto> prepared = await runtime.Service.PrepareAsync(
            request,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        SagaReviewBulkApplyRequest apply = new(request, prepared.Value.PreparedPlanToken);

        Result<MemoryReviewBulkResultDto> first = await runtime.Service.ApplyAsync(
            apply,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(first.IsSuccess, first.Error.Message);

        MemoryReviewBulkItemResultDto item = Assert.Single(first.Value.Items);

        Assert.Equal("Unchanged", item.Outcome);

        Assert.Null(item.ResultingVersionId);

        Assert.Equal(observed.EventSequence, first.Value.ReviewedThroughEventSequence);

        Assert.Equal(1, await harness.CountAsync("annal_versions", "1 = 1").ConfigureAwait(false));

        Assert.Equal(1, await harness.CountAsync(
            "annal_review_decision_receipts", "1 = 1").ConfigureAwait(false));

        SagaReviewPageDto empty = (await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        Assert.Empty(empty.Items);

        Result<MemoryReviewBulkResultDto> replay = await runtime.Service.ApplyAsync(
            apply,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(first.Value.Items, replay.Value.Items);

        Assert.Equal(first.Value.ReviewedThroughEventSequence, replay.Value.ReviewedThroughEventSequence);

        Assert.Equal(1, await harness.CountAsync("annal_versions", "1 = 1").ConfigureAwait(false));

        Assert.Equal(1, await harness.CountAsync(
            "annal_review_decision_receipts", "1 = 1").ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task Retiring_an_already_retired_head_reports_already_retired_and_replays_exactly()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.Parse(
            "2026-09-28T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        await InsertAsync(harness, "m-1", "remembered", created).ConfigureAwait(false);

        SagaCurationOutcome retired = await harness.Store.RetireAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("remembered"),
            created.AddMinutes(1),
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaCurationOutcomeKind.Applied, retired.Kind);

        ReviewRuntime runtime = CreateRuntime(harness);

        SagaReviewItemDto observed = Assert.Single((await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        Assert.Equal(AnnalOperation.Retire, observed.Operation);

        SagaReviewBulkPrepareRequest request = new(
            Guid.Parse("35353535-3535-3535-3535-353535353535"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Retire,
            [new SagaReviewDecision(observed.ObservationToken, ReplacementContent: null)]);

        MemoryReviewBulkPlanDto plan = (await runtime.Service.PrepareAsync(
            request,
            CancellationToken.None).ConfigureAwait(false)).Value;

        SagaReviewBulkApplyRequest apply = new(request, plan.PreparedPlanToken);

        Result<MemoryReviewBulkResultDto> first = await runtime.Service.ApplyAsync(
            apply,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(first.IsSuccess, first.Error.Message);

        MemoryReviewBulkItemResultDto item = Assert.Single(first.Value.Items);

        Assert.Equal("AlreadyRetired", item.Outcome);

        Assert.Null(item.ResultingVersionId);

        Assert.Equal(observed.EventSequence, first.Value.ReviewedThroughEventSequence);

        Assert.Equal(2, await harness.CountAsync("annal_versions", "1 = 1").ConfigureAwait(false));

        Assert.Equal(1, await harness.CountAsync(
            "annal_review_decision_receipts", "1 = 1").ConfigureAwait(false));

        Assert.Empty((await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        Result<MemoryReviewBulkResultDto> replay = await runtime.Service.ApplyAsync(
            apply,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(first.Value.Items, replay.Value.Items);

        Assert.Equal(first.Value.ReviewedThroughEventSequence, replay.Value.ReviewedThroughEventSequence);

        Assert.Equal(2, await harness.CountAsync("annal_versions", "1 = 1").ConfigureAwait(false));

        Assert.Equal(1, await harness.CountAsync(
            "annal_review_decision_receipts", "1 = 1").ConfigureAwait(false));
    }

    [SkippableTheory]
    [InlineData(MemoryReviewAction.Retire)]
    [InlineData(MemoryReviewAction.Pin)]
    [InlineData(MemoryReviewAction.Unpin)]
    public async Task Lifecycle_actions_apply_to_the_exact_head_and_retirement_head_remains_reviewable(
        MemoryReviewAction action)
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.Parse(
            "2026-09-28T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        await InsertAsync(harness, "m-1", "remembered", created).ConfigureAwait(false);

        if (action == MemoryReviewAction.Unpin)
        {
            _ = await harness.Store.SetPinAsync(
                "m-1",
                true,
                created.AddMinutes(1),
                CancellationToken.None).ConfigureAwait(false);
        }

        SagaMemoryReviewService service = CreateService(harness);

        SagaReviewItemDto item = Assert.Single((await service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        SagaReviewBulkPrepareRequest request = new(
            action switch
            {
                MemoryReviewAction.Retire => Guid.Parse("88888888-8888-8888-8888-888888888888"),
                MemoryReviewAction.Pin => Guid.Parse("99999999-9999-9999-9999-999999999999"),
                _ => Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            },
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            action,
            [new SagaReviewDecision(item.ObservationToken, ReplacementContent: null)]);

        MemoryReviewBulkPlanDto plan = (await service.PrepareAsync(
            request,
            CancellationToken.None).ConfigureAwait(false)).Value;

        Result<MemoryReviewBulkResultDto> applied = await service.ApplyAsync(
            new SagaReviewBulkApplyRequest(request, plan.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        MemoryReviewBulkItemResultDto result = Assert.Single(applied.Value.Items);

        Assert.Equal(action == MemoryReviewAction.Retire, result.ResultingVersionId is not null);

        SagaMemoryCurationRow row = (await harness.Store.ReadCurationRowAsync(
            "m-1",
            CancellationToken.None).ConfigureAwait(false))!;

        Assert.Equal(action == MemoryReviewAction.Retire, row.Lifecycle.RetiredAtUtc is not null);

        Assert.Equal(action == MemoryReviewAction.Pin, row.Lifecycle.PinnedAtUtc is not null);

        SagaReviewPageDto queue = (await service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        if (action == MemoryReviewAction.Retire)
        {
            SagaReviewItemDto retirement = Assert.Single(queue.Items);

            Assert.Equal(result.ResultingVersionId, retirement.VersionId);

            Assert.Equal(AnnalOperation.Retire, retirement.Operation);
        }
        else
        {
            Assert.Empty(queue.Items);
        }
    }

    [SkippableFact]
    public async Task Bulk_retire_removes_the_mirror_row_while_the_accelerator_flag_is_off()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        Result<MemoryReviewBulkResultDto> applied = await ApplyOverAFilledMirrorAsync(
            harness,
            Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"),
            MemoryReviewAction.Retire,
            replacementContent: null).ConfigureAwait(false);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.NotNull(Assert.Single(applied.Value.Items).ResultingVersionId);

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1").ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task Bulk_correct_removes_the_stale_mirror_vector_while_the_accelerator_flag_is_off()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        Result<MemoryReviewBulkResultDto> applied = await ApplyOverAFilledMirrorAsync(
            harness,
            Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC"),
            MemoryReviewAction.Correct,
            replacementContent: "corrected").ConfigureAwait(false);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.NotNull(Assert.Single(applied.Value.Items).ResultingVersionId);

        // The corrected vector cannot be mirrored without the accelerator, so the old one must not stay
        // behind describing text the memory no longer holds.
        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1").ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task A_stale_item_refuses_the_whole_bulk_before_any_pin_changes()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.Parse(
            "2026-09-28T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        await InsertAsync(harness, "m-1", "first", created).ConfigureAwait(false);

        await InsertAsync(harness, "m-2", "second", created.AddMinutes(1)).ConfigureAwait(false);

        ReviewRuntime runtime = CreateRuntime(harness);

        SagaReviewPageDto page = (await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        SagaReviewBulkPrepareRequest request = new(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Pin,
            page.Items.Select(static item => new SagaReviewDecision(
                item.ObservationToken,
                ReplacementContent: null)).ToArray());

        MemoryReviewBulkPlanDto plan = (await runtime.Service.PrepareAsync(
            request,
            CancellationToken.None).ConfigureAwait(false)).Value;

        _ = await harness.Store.CorrectAsync(
            "m-2",
            AnnalContentDigest.ForSagaMemory("second"),
            "second changed elsewhere",
            harness.Embedding(99),
            created.AddMinutes(2),
            CancellationToken.None).ConfigureAwait(false);

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
            new SagaReviewBulkApplyRequest(request, plan.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(applied.IsFailure);

        Assert.Null((await harness.Store.ReadCurationRowAsync(
            "m-1",
            CancellationToken.None).ConfigureAwait(false))!.Lifecycle.PinnedAtUtc);

        Assert.Null((await harness.Store.ReadCurationRowAsync(
            "m-2",
            CancellationToken.None).ConfigureAwait(false))!.Lifecycle.PinnedAtUtc);
    }

    [SkippableFact]
    public async Task Superseded_confirm_token_is_stale_and_refresh_only_exposes_the_actionable_head()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.Parse(
            "2026-09-28T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        await InsertAsync(harness, "m-1", "before", created).ConfigureAwait(false);

        ReviewRuntime runtime = CreateRuntime(harness);

        SagaReviewItemDto observed = Assert.Single((await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        _ = await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("before"),
            "after",
            harness.Embedding(17),
            created.AddMinutes(1),
            CancellationToken.None).ConfigureAwait(false);

        SagaReviewBulkPrepareRequest staleRequest = new(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision(observed.ObservationToken, ReplacementContent: null)]);

        Result<MemoryReviewBulkPlanDto> stale = await runtime.Service.PrepareAsync(
            staleRequest,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(stale.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.StaleObservation, stale.Error.Code);

        SagaReviewPageDto refreshed = (await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        SagaReviewItemDto current = Assert.Single(refreshed.Items);

        Assert.Equal("m-1", current.SubjectId);

        Assert.True(current.IsCurrent);

        Assert.NotEqual(observed.VersionId, current.VersionId);

        SagaReviewBulkPrepareRequest confirmRequest = new(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision(current.ObservationToken, ReplacementContent: null)]);

        MemoryReviewBulkPlanDto plan = (await runtime.Service.PrepareAsync(
            confirmRequest,
            CancellationToken.None).ConfigureAwait(false)).Value;

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
            new SagaReviewBulkApplyRequest(confirmRequest, plan.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(refreshed.FrozenThroughEventSequence, applied.Value.ReviewedThroughEventSequence);

        SagaReviewPageDto empty = (await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        Assert.Empty(empty.Items);
    }

    [SkippableFact]
    public async Task Correction_finishes_every_embedding_before_opening_its_atomic_write()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.Parse(
            "2026-09-28T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);

        await InsertAsync(harness, "m-1", "before", created).ConfigureAwait(false);

        GatedWeaveService weave = new();

        ReviewRuntime runtime = CreateRuntime(harness, weave);

        SagaReviewItemDto item = Assert.Single((await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        SagaReviewBulkPrepareRequest request = new(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Correct,
            [new SagaReviewDecision(item.ObservationToken, "after")]);

        MemoryReviewBulkPlanDto plan = (await runtime.Service.PrepareAsync(
            request,
            CancellationToken.None).ConfigureAwait(false)).Value;

        Task<Result<MemoryReviewBulkResultDto>> applying = runtime.Service.ApplyAsync(
            new SagaReviewBulkApplyRequest(request, plan.PreparedPlanToken),
            CancellationToken.None);

        await weave.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await InsertAsync(
            harness,
            "concurrent",
            "written while embedding",
            created.AddMinutes(1)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        weave.Release.TrySetResult();

        Result<MemoryReviewBulkResultDto> result = await applying.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.NotNull(Assert.Single(result.Value.Items).ResultingVersionId);

        SagaMemoryCurationRow corrected = (await harness.Store.ReadCurationRowAsync(
            "m-1",
            CancellationToken.None).ConfigureAwait(false))!;

        Assert.Equal("after", corrected.Memory.Content);

        SagaReviewPageDto queue = (await runtime.Service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value;

        SagaReviewItemDto unreviewed = Assert.Single(queue.Items);

        Assert.Equal("concurrent", unreviewed.SubjectId);
    }

    /// <summary>
    /// Fills the plain vector mirror through the harness store with its accelerator flag on, then
    /// drives one bulk decision through a review service whose own flag is off.
    /// </summary>
    private static async Task<Result<MemoryReviewBulkResultDto>> ApplyOverAFilledMirrorAsync(
        SagaStoreHarness harness,
        Guid requestId,
        MemoryReviewAction action,
        string? replacementContent)
    {
        await harness.CreatePlainVectorMirrorAsync().ConfigureAwait(false);

        harness.VectorAccelerator.SetAvailable(true);

        await InsertAsync(
            harness,
            "m-1",
            "remembered",
            DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1").ConfigureAwait(false));

        SagaMemoryReviewService service = CreateService(harness);

        SagaReviewItemDto item = Assert.Single((await service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false)).Value.Items);

        SagaReviewBulkPrepareRequest request = new(
            requestId,
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            action,
            [new SagaReviewDecision(item.ObservationToken, replacementContent)]);

        Result<MemoryReviewBulkPlanDto> prepared = await service.PrepareAsync(
            request,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        return await service.ApplyAsync(
            new SagaReviewBulkApplyRequest(request, prepared.Value.PreparedPlanToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static SagaMemoryReviewService CreateService(SagaStoreHarness harness) =>
        CreateRuntime(harness).Service;

    private static ReviewRuntime CreateRuntime(
        SagaStoreHarness harness,
        IWeaveService? weave = null)
        => CreateRuntime(harness.Context, weave);

    private static ReviewRuntime CreateRuntime(
        ArcanumDbContext context,
        IWeaveService? weave = null)
    {
        FakeTimeProvider time = new();

        time.SetUtcNow(DateTimeOffset.Parse(
            "2026-09-28T13:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture));

        MemoryReviewTokenCodec codec = new(time);

        SagaMemoryReviewService service = new(
            context,
            weave ?? FakeWeaveService.Available,
            codec,
            new WeaveIndexAvailability(),
            Settings(),
            MemoryErasureTestKeys.Isolated(),
            time);

        return new ReviewRuntime(service, codec, time);
    }

    private static async Task<Result<SagaReviewPageDto>> ListAfterReleaseAsync(
        SagaMemoryReviewService service,
        Task release)
    {
        await release.ConfigureAwait(false);

        return await service.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 10, null),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task InsertAsync(
        SagaStoreHarness harness,
        string id,
        string content,
        DateTimeOffset createdAt,
        Guid? sessionId = null)
    {
        SagaMemoryWriteOutcome outcome = await harness.Store.InsertAsync(
            id,
            content,
            createdAt,
            sessionId,
            tags: null,
            source: "saga-extraction",
            harness.Embedding(id.GetHashCode(StringComparison.Ordinal)),
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);
    }

    private static TestOptionsMonitor<ArcanumSettings> Settings() =>
        new(
            new ArcanumSettings
            {
                Features = new FeatureSettings { Annals = true },
                Integrations = new IntegrationSettings
                {
                    Embeddings = new EmbeddingIntegrationSettings { Dimensions = 64 },
                },
            });

    private sealed class FakeWeaveService : IWeaveService
    {
        public static FakeWeaveService Available { get; } = new();

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(new float[64])));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(
            string text,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class GatedWeaveService : IWeaveService
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;

        public async Task<Result<Embedding<float>>> EmbedAsync(
            string text,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();

            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return Result<Embedding<float>>.Success(new Embedding<float>(new float[64]));
        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(
            string text,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record ReviewRuntime(
        SagaMemoryReviewService Service,
        MemoryReviewTokenCodec Codec,
        FakeTimeProvider Time);
}
