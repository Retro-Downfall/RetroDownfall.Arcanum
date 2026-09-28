using System.Collections.Immutable;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

[Trait("Category", "Integration")]
public sealed class CovenantMemoryReviewServiceTests
{
    private static readonly Guid CampaignOne = Guid.Parse("247a1767-7fc1-4c93-bc17-d021e52ff665");

    private static readonly Guid CampaignTwo = Guid.Parse("876a43f9-ff2d-49c4-8ed7-eac134f9857f");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Queue_is_exact_newest_first_and_cursor_freezes_its_frontier()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);
        await runtime.Fixture.AddCampaignAsync(CampaignTwo, "Two", Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "confirmed.other-lane",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "confirmed",
            Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignTwo,
            "proposed.other-campaign",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "other campaign",
            Token,
            origin: CovenantOrigin.AgentProposed);

        SeededHead oldest = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "proposed.oldest",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "oldest proposal",
            Token,
            origin: CovenantOrigin.AgentProposed,
            sources:
            [
                new MaterializationSourceDigestInput(
                    Guid.Parse("965cc7aa-d0da-46cf-9aa7-f18e9222ac70"),
                    Guid.Parse("8f1c2e39-a259-4ca9-9594-81d668415662"),
                    "brief.md",
                    Digest(0x41),
                    CovenantMaterializationSourceRange.WholeSource,
                    null,
                    null,
                    ImmutableArray<MaterializationOccurrenceDigestInput>.Empty),
            ]);

        SeededHead newest = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "proposed.newest",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "newest proposal",
            Token,
            origin: CovenantOrigin.AgentProposed);

        CovenantReviewListRequest request = new(
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Proposed,
            Limit: 1,
            Cursor: null);

        await using CovenantReadLease firstLease = runtime.ReadLease();

        Result<CovenantReviewPageDto> first = await runtime.Service.ListAsync(request, firstLease, Token);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : string.Empty);
        Assert.Single(first.Value.Items);
        Assert.Equal(newest.VersionId, first.Value.Items[0].VersionId);
        Assert.Equal(CovenantOrigin.AgentProposed, first.Value.Items[0].Origin);
        Assert.Equal(CovenantScope.Campaign, first.Value.Items[0].Scope);
        Assert.Equal(CampaignOne, first.Value.Items[0].CampaignId);
        Assert.Equal(CovenantLane.Proposed, first.Value.Items[0].Lane);
        Assert.NotNull(first.Value.Items[0].SourceTurnId);
        Assert.Equal("call-1", first.Value.Items[0].SourceToolCallId);
        Assert.Equal(newest.Compiled!.Fragment, first.Value.Items[0].CompiledContent);
        Assert.True(first.Value.Truncated);
        Assert.NotNull(first.Value.NextCursor);

        SeededHead arrivedLater = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "proposed.after-frontier",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "arrived after the first page",
            Token,
            origin: CovenantOrigin.AgentProposed);

        await using CovenantReadLease secondLease = runtime.ReadLease();

        Result<CovenantReviewPageDto> second = await runtime.Service.ListAsync(
            request with { Cursor = first.Value.NextCursor },
            secondLease,
            Token);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : string.Empty);
        Assert.Single(second.Value.Items);
        Assert.Equal(oldest.VersionId, second.Value.Items[0].VersionId);
        Assert.Single(second.Value.Items[0].Sources);
        Assert.Equal("brief.md", second.Value.Items[0].Sources[0].LogicalKey);
        Assert.DoesNotContain(second.Value.Items, item => item.VersionId == arrivedLater.VersionId);

        await using CovenantReadLease refreshedLease = runtime.ReadLease();

        Result<CovenantReviewPageDto> refreshed = await runtime.Service.ListAsync(
            request with { Limit = MemoryReviewLimits.MaxPageSize },
            refreshedLease,
            Token);

        Assert.True(refreshed.IsSuccess, refreshed.IsFailure ? refreshed.Error.Message : string.Empty);
        Assert.Equal(arrivedLater.VersionId, refreshed.Value.Items[0].VersionId);
    }

    [Fact]
    public async Task Superseded_event_is_stale_for_confirm_and_does_not_strand_the_filtered_marker()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        SeededHead original = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "preference.tests",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Run the focused tests.",
            Token);

        CovenantReviewListRequest listRequest = new(
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewLimits.MaxPageSize,
            Cursor: null);

        await using CovenantReadLease originalListLease = runtime.ReadLease();

        CovenantReviewPageDto firstPage = (await runtime.Service.ListAsync(
            listRequest,
            originalListLease,
            Token)).Value;

        CovenantReviewItemDto observedOriginal = Assert.Single(firstPage.Items);

        SeededHead replacement = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "preference.tests",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Run the focused tests, then the exact delivery gate.",
            Token,
            entryId: original.EntryId,
            laneRevision: 2,
            predecessorVersionId: original.VersionId);

        CovenantReviewBulkPrepareRequest staleRequest = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Confirm,
            [new CovenantReviewDecision(observedOriginal.ObservationToken, null)]);

        await using CovenantReadLease staleLease = runtime.ReadLease();

        Result<MemoryReviewBulkPlanDto> stale = await runtime.Service.PrepareAsync(
            staleRequest,
            staleLease,
            Token);

        Assert.True(stale.IsFailure);
        Assert.Equal(ErrorCodes.MemoryReview.StaleObservation, stale.Error.Code);

        await using CovenantReadLease refreshedLease = runtime.ReadLease();

        CovenantReviewPageDto refreshed = (await runtime.Service.ListAsync(
            listRequest,
            refreshedLease,
            Token)).Value;

        CovenantReviewItemDto current = Assert.Single(refreshed.Items);

        Assert.Equal(replacement.VersionId, current.VersionId);
        Assert.DoesNotContain(refreshed.Items, item => item.VersionId == original.VersionId);

        CovenantReviewBulkPrepareRequest confirm = staleRequest with
        {
            RequestId = Guid.CreateVersion7(),
            Decisions = [new CovenantReviewDecision(current.ObservationToken, null)],
        };

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(confirm, prepareLease, Token)).Value;
        }

        await using CovenantWriteLease writeLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(confirm, plan.PreparedPlanToken),
            writeLease,
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);
        Assert.Equal(current.EventSequence, applied.Value.ReviewedThroughEventSequence);
        Assert.Equal(1L, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT count(*) FROM covenant_review_decision_receipts;"));
        Assert.Equal(2L, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT count(*) FROM covenant_versions;"));
        Assert.Equal(0L, await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT count(*) FROM covenant_review_decision_receipts WHERE ReviewEventSequence = {observedOriginal.EventSequence};"));
    }

    [Fact]
    public async Task Confirm_is_exact_scope_and_lane_bound_and_changes_only_review_state()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);
        await runtime.Fixture.AddCampaignAsync(CampaignTwo, "Two", Token);

        SeededHead seeded = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "response.style",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Answer directly.",
            Token);

        CovenantReviewPageDto page;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            page = (await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value;
        }

        CovenantReviewItemDto observed = Assert.Single(page.Items);

        CovenantReviewBulkPrepareRequest wrongLane = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Proposed,
            MemoryReviewAction.Confirm,
            [new CovenantReviewDecision(observed.ObservationToken, null)]);

        await using (CovenantReadLease wrongLaneLease = runtime.ReadLease())
        {
            Result<MemoryReviewBulkPlanDto> refused = await runtime.Service.PrepareAsync(
                wrongLane,
                wrongLaneLease,
                Token);

            Assert.True(refused.IsFailure);
            Assert.Equal(ErrorCodes.MemoryReview.InvalidToken, refused.Error.Code);
        }

        CovenantReviewBulkPrepareRequest wrongCampaign = wrongLane with
        {
            RequestId = Guid.CreateVersion7(),
            CampaignId = CampaignTwo,
            Lane = CovenantLane.Confirmed,
        };

        await using (CovenantReadLease wrongCampaignLease = runtime.ReadLease(CampaignTwo))
        {
            Result<MemoryReviewBulkPlanDto> refused = await runtime.Service.PrepareAsync(
                wrongCampaign,
                wrongCampaignLease,
                Token);

            Assert.True(refused.IsFailure);
            Assert.Equal(ErrorCodes.MemoryReview.InvalidToken, refused.Error.Code);
        }

        CovenantReviewBulkPrepareRequest confirm = wrongLane with
        {
            RequestId = Guid.CreateVersion7(),
            Lane = CovenantLane.Confirmed,
        };

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(confirm, prepareLease, Token)).Value;
        }

        await using CovenantWriteLease writeLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(confirm, plan.PreparedPlanToken),
            writeLease,
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);
        Assert.Equal("Confirmed", Assert.Single(applied.Value.Items).Outcome);
        Assert.Null(applied.Value.Items[0].ResultingVersionId);
        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));
        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_curation_heads;"));
        Assert.Equal((long)CovenantLane.Confirmed, await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT LaneCode FROM covenant_heads WHERE CurrentVersionId = '{seeded.VersionId:D}';"));
        Assert.Equal((long)CovenantOperation.Set, await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT CurrentOperationCode FROM covenant_heads WHERE CurrentVersionId = '{seeded.VersionId:D}';"));
        Assert.Equal("Answer directly.", await ScalarTextAsync(
            runtime.Fixture.Connection,
            $"SELECT AuthoredContent FROM covenant_versions WHERE VersionId = '{seeded.VersionId:D}';"));
    }

    [Fact]
    public async Task Correction_auto_acknowledges_its_output_and_exact_replay_precedes_token_expiry()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "response.detail",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Use moderate detail.",
            Token);

        CovenantReviewPageDto page;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            page = (await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value;
        }

        CovenantReviewItemDto observed = Assert.Single(page.Items);

        CovenantReviewBulkPrepareRequest correction = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Correct,
            [new CovenantReviewDecision(observed.ObservationToken, "Use concise detail.")]);

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(correction, prepareLease, Token)).Value;
        }

        MemoryReviewBulkResultDto first;

        await using (CovenantWriteLease writeLease = runtime.WriteLease())
        {
            Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
                new CovenantReviewBulkApplyRequest(correction, plan.PreparedPlanToken),
                writeLease,
                Token);

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);
            first = applied.Value;
        }

        MemoryReviewBulkItemResultDto corrected = Assert.Single(first.Items);
        Assert.Equal("Corrected", corrected.Outcome);
        Assert.NotNull(corrected.ResultingVersionId);
        Assert.True(first.ReviewedThroughEventSequence > observed.EventSequence);
        Assert.Equal(2L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));
        Assert.Equal(2L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
        Assert.Equal("Use concise detail.", await ScalarTextAsync(
            runtime.Fixture.Connection,
            $"SELECT AuthoredContent FROM covenant_versions WHERE VersionId = '{corrected.ResultingVersionId}';"));

        await using (CovenantReadLease refreshedLease = runtime.ReadLease())
        {
            CovenantReviewPageDto refreshed = (await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                refreshedLease,
                Token)).Value;

            Assert.Empty(refreshed.Items);
        }

        runtime.Time.Advance(MemoryReviewLimits.TokenLifetime + TimeSpan.FromSeconds(1));

        await using (CovenantWriteLease replayLease = runtime.WriteLease())
        {
            Result<MemoryReviewBulkResultDto> replayed = await runtime.Service.ApplyAsync(
                new CovenantReviewBulkApplyRequest(correction, plan.PreparedPlanToken),
                replayLease,
                Token);

            Assert.True(replayed.IsSuccess, replayed.IsFailure ? replayed.Error.Message : string.Empty);
            Assert.True(replayed.Value.Replayed);
            Assert.Equal(first.Store, replayed.Value.Store);
            Assert.Equal(first.RequestId, replayed.Value.RequestId);
            Assert.Equal(first.Action, replayed.Value.Action);
            Assert.Equal(first.ReviewedThroughEventSequence, replayed.Value.ReviewedThroughEventSequence);
            Assert.Equal(first.Items, replayed.Value.Items);
        }

        CovenantReviewBulkPrepareRequest conflicting = correction with
        {
            Decisions = [new CovenantReviewDecision(observed.ObservationToken, "Use exhaustive detail.")],
        };

        await using CovenantWriteLease conflictLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> conflict = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(conflicting, plan.PreparedPlanToken),
            conflictLease,
            Token);

        Assert.True(conflict.IsFailure);
        Assert.Equal(ErrorCodes.MemoryReview.RequestReuse, conflict.Error.Code);

        await ExecuteAsync(
            runtime.Fixture.Connection,
            $"""
            UPDATE covenant_review_decision_receipts
            SET ResponseReceiptDigest = zeroblob(32)
            WHERE DecisionId = (
                SELECT MIN(DecisionId)
                FROM covenant_review_decision_receipts
                WHERE DecisionId LIKE '{correction.RequestId:N}:%');
            """);

        await using CovenantWriteLease corruptReplayLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> corruptReplay = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(correction, plan.PreparedPlanToken),
            corruptReplayLease,
            Token);

        Assert.True(corruptReplay.IsFailure);
        Assert.Equal(ErrorCodes.MemoryReview.IntegrityFailure, corruptReplay.Error.Code);
    }

    [Fact]
    public async Task Identical_correction_is_acknowledged_as_no_change_without_a_replacement()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "response.stable",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Keep this response stable.",
            Token);

        CovenantReviewItemDto observed;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            observed = Assert.Single((await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value.Items);
        }

        CovenantReviewBulkPrepareRequest request = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Correct,
            [new CovenantReviewDecision(observed.ObservationToken, "Keep this response stable.")]);

        Result<MemoryReviewBulkPlanDto> prepared;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            prepared = await runtime.Service.PrepareAsync(request, prepareLease, Token);
        }

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        CovenantReviewBulkApplyRequest apply = new(request, prepared.Value.PreparedPlanToken);

        MemoryReviewBulkResultDto first;

        await using (CovenantWriteLease writeLease = runtime.WriteLease())
        {
            Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(apply, writeLease, Token);

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

            first = applied.Value;
        }

        MemoryReviewBulkItemResultDto item = Assert.Single(first.Items);

        Assert.Equal("NoChange", item.Outcome);

        Assert.Null(item.ResultingVersionId);

        Assert.Equal(observed.EventSequence, first.ReviewedThroughEventSequence);

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_mutation_receipts;"));

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));

        await using (CovenantReadLease emptyLease = runtime.ReadLease())
        {
            CovenantReviewPageDto empty = (await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                emptyLease,
                Token)).Value;

            Assert.Empty(empty.Items);
        }

        await using (CovenantWriteLease replayLease = runtime.WriteLease())
        {
            Result<MemoryReviewBulkResultDto> replay = await runtime.Service.ApplyAsync(apply, replayLease, Token);

            Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : string.Empty);

            Assert.True(replay.Value.Replayed);

            Assert.Equal(first.Items, replay.Value.Items);

            Assert.Equal(first.ReviewedThroughEventSequence, replay.Value.ReviewedThroughEventSequence);
        }

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
    }

    [Theory]
    [InlineData(MemoryReviewAction.Retire)]
    [InlineData(MemoryReviewAction.Pin)]
    [InlineData(MemoryReviewAction.Unpin)]
    public async Task Already_satisfied_lifecycle_action_is_acknowledged_as_no_change_and_replays_exactly(
        MemoryReviewAction action)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        CovenantOperation operation = action == MemoryReviewAction.Retire
            ? CovenantOperation.Retire
            : CovenantOperation.Set;

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "lifecycle.stable",
            CovenantLane.Confirmed,
            operation,
            operation == CovenantOperation.Set ? "Stable." : null,
            Token);

        if (action == MemoryReviewAction.Pin)
        {
            CovenantCurationIntent pin = CovenantCurationFixture.Pin(
                CovenantOperationScope.ForCampaign(CampaignOne),
                "lifecycle.stable",
                expectedRevision: 0,
                keyEpoch: 1);

            Result<CovenantCurationReceipt> pinned = await CovenantCurationFixture.ApplyAsync(
                runtime.Fixture,
                new CovenantCurationCommit(
                    await runtime.Fixture.ReadDatasetGenerationAsync(Token),
                    expectedKeyReclamationEpoch: 1,
                    CovenantMutationFixture.CommitTime,
                    pin),
                Token);

            Assert.True(pinned.IsSuccess, pinned.IsFailure ? pinned.Error.Message : string.Empty);
        }

        CovenantReviewItemDto observed;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            observed = Assert.Single((await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value.Items);
        }

        CovenantReviewBulkPrepareRequest request = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            action,
            [new CovenantReviewDecision(observed.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(request, prepareLease, Token)).Value;
        }

        CovenantReviewBulkApplyRequest apply = new(request, plan.PreparedPlanToken);

        MemoryReviewBulkResultDto first;

        await using (CovenantWriteLease writeLease = runtime.WriteLease())
        {
            Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(apply, writeLease, Token);

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

            first = applied.Value;
        }

        MemoryReviewBulkItemResultDto item = Assert.Single(first.Items);

        Assert.Equal("NoChange", item.Outcome);

        Assert.Null(item.ResultingVersionId);

        Assert.Equal(observed.EventSequence, first.ReviewedThroughEventSequence);

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));

        await using (CovenantReadLease emptyLease = runtime.ReadLease())
        {
            CovenantReviewPageDto empty = (await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                emptyLease,
                Token)).Value;

            Assert.Empty(empty.Items);
        }

        await using (CovenantWriteLease replayLease = runtime.WriteLease())
        {
            Result<MemoryReviewBulkResultDto> replay = await runtime.Service.ApplyAsync(apply, replayLease, Token);

            Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : string.Empty);

            Assert.True(replay.Value.Replayed);

            Assert.Equal(first.Items, replay.Value.Items);

            Assert.Equal(first.ReviewedThroughEventSequence, replay.Value.ReviewedThroughEventSequence);
        }

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
    }

    [Fact]
    public async Task A_later_kernel_refusal_rolls_back_every_earlier_decision_and_marker_write()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        SeededHead first = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "bulk.first",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "first",
            Token);

        SeededHead second = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "bulk.second",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "second",
            Token);

        CovenantReviewPageDto page;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            page = (await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value;
        }

        Assert.Equal(2, page.Items.Length);

        CovenantReviewBulkPrepareRequest correction = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Correct,
            [
                new CovenantReviewDecision(page.Items[0].ObservationToken, new string('x', CovenantLimits.MaxAuthoredContentBytes)),
                new CovenantReviewDecision(page.Items[1].ObservationToken, new string('y', CovenantLimits.MaxAuthoredContentBytes)),
            ]);

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(correction, prepareLease, Token)).Value;
        }

        await using CovenantWriteLease writeLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> refused = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(correction, plan.PreparedPlanToken),
            writeLease,
            Token);

        Assert.True(refused.IsFailure);
        Assert.Equal(2L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));
        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_mutation_receipts;"));
        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT ReviewedThroughSequence FROM covenant_review_markers;"));
        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT Revision FROM covenant_review_markers;"));
        Assert.Equal(2L, await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT count(*) FROM covenant_heads WHERE CurrentVersionId IN ('{first.VersionId:D}', '{second.VersionId:D}');"));
    }

    [Fact]
    public async Task Retire_uses_the_mutation_kernel_and_leaves_its_new_tombstone_event_reviewable()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        SeededHead original = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "retire.me",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "temporary",
            Token);

        CovenantReviewItemDto observed;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            observed = Assert.Single((await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value.Items);
        }

        CovenantReviewBulkPrepareRequest retire = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Retire,
            [new CovenantReviewDecision(observed.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(retire, prepareLease, Token)).Value;
        }

        MemoryReviewBulkResultDto applied;

        await using (CovenantWriteLease writeLease = runtime.WriteLease())
        {
            applied = (await runtime.Service.ApplyAsync(
                new CovenantReviewBulkApplyRequest(retire, plan.PreparedPlanToken),
                writeLease,
                Token)).Value;
        }

        MemoryReviewBulkItemResultDto result = Assert.Single(applied.Items);
        Assert.Equal("Retired", result.Outcome);
        Assert.NotNull(result.ResultingVersionId);
        Assert.Equal(2L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));
        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_mutation_receipts;"));
        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
        Assert.Equal((long)CovenantOperation.Retire, await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT CurrentOperationCode FROM covenant_heads WHERE EntryId = '{original.EntryId:D}';"));

        await using CovenantReadLease refreshedLease = runtime.ReadLease();

        CovenantReviewItemDto tombstone = Assert.Single((await runtime.Service.ListAsync(
            new CovenantReviewListRequest(
                CovenantScope.Campaign,
                CampaignOne,
                CovenantLane.Confirmed,
                MemoryReviewLimits.MaxPageSize,
                Cursor: null),
            refreshedLease,
            Token)).Value.Items);

        Assert.Equal(Guid.Parse(result.ResultingVersionId!), tombstone.VersionId);
        Assert.Equal(CovenantOperation.Retire, tombstone.Operation);
        Assert.True(tombstone.EventSequence > applied.ReviewedThroughEventSequence);
    }

    [Theory]
    [InlineData(MemoryReviewAction.Retire, 0L, 0L, 0L, 1L, 3L)]
    [InlineData(MemoryReviewAction.Pin, 1L, 1L, 1L, 0L, 2L)]
    [InlineData(MemoryReviewAction.Unpin, 0L, 0L, 1L, 0L, 2L)]
    public async Task Retire_and_pin_actions_use_their_kernels_acknowledge_only_the_source_and_preserve_the_other_lane(
        MemoryReviewAction action,
        long expectedHeads,
        long expectedPinned,
        long expectedCurationReceipts,
        long expectedMutationReceipts,
        long expectedVersions)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        SeededHead original = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "pin.subject",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "stable",
            Token);

        SeededHead oppositeLane = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "pin.subject",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "candidate",
            Token,
            origin: CovenantOrigin.AgentProposed,
            entryId: original.EntryId);

        CovenantReviewItemDto observed;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            observed = Assert.Single((await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value.Items);
        }

        CovenantReviewBulkPrepareRequest request = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            action,
            [new CovenantReviewDecision(observed.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(request, prepareLease, Token)).Value;
        }

        await using CovenantWriteLease writeLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(request, plan.PreparedPlanToken),
            writeLease,
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);
        MemoryReviewBulkItemResultDto result = Assert.Single(applied.Value.Items);
        Assert.Equal(expectedCurationReceipts, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_curation_receipts;"));
        Assert.Equal(expectedMutationReceipts, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_mutation_receipts;"));
        Assert.Equal(expectedHeads, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_curation_heads;"));
        Assert.Equal(expectedPinned, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT COALESCE(MAX(IsPinned), 0) FROM covenant_curation_heads;"));
        Assert.Equal(expectedVersions, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_versions;"));
        Assert.Equal(action == MemoryReviewAction.Retire ? 0L : 1L, await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT count(*) FROM covenant_heads WHERE CurrentVersionId = '{original.VersionId:D}';"));
        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
        Assert.Equal(1L, await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT count(*) FROM covenant_review_decision_receipts WHERE ReviewEventSequence = {observed.EventSequence};"));
        Assert.Equal(1L, await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT count(*) FROM covenant_heads WHERE CurrentVersionId = '{oppositeLane.VersionId:D}' AND LaneCode = {(int)CovenantLane.Proposed};"));
        Assert.Equal(0L, await ScalarAsync(
            runtime.Fixture.Connection,
            $"""
            SELECT count(*)
            FROM covenant_review_decision_receipts receipt
            JOIN covenant_review_events event ON event.Sequence = receipt.ReviewEventSequence
            WHERE event.LaneCode = {(int)CovenantLane.Proposed};
            """));

        if (action == MemoryReviewAction.Retire)
        {
            Assert.NotNull(result.ResultingVersionId);
            Assert.Equal(0L, await ScalarAsync(
                runtime.Fixture.Connection,
                $"""
                SELECT count(*)
                FROM covenant_review_decision_receipts receipt
                JOIN covenant_review_events event ON event.Sequence = receipt.ReviewEventSequence
                WHERE event.VersionId = '{result.ResultingVersionId}';
                """));
        }
    }

    [Theory]
    [InlineData("store", ErrorCodes.MemoryReview.InvalidToken)]
    [InlineData("dataset", ErrorCodes.MemoryReview.StaleObservation)]
    [InlineData("version", ErrorCodes.MemoryReview.UnseenObservation)]
    public async Task Authenticated_observations_reject_wrong_store_dataset_and_unseen_version(
        string mismatch,
        string expectedCode)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "bound.target",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "bound",
            Token);

        CovenantReviewItemDto observed;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            observed = Assert.Single((await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value.Items);
        }

        MemoryReviewObservationTokenFacts facts = runtime.Codec
            .ReadObservation(observed.ObservationToken).Value;

        MemoryReviewObservationTokenFacts altered = mismatch switch
        {
            "store" => facts with { Store = MemoryReviewStore.Saga },
            "dataset" => facts with { MarkerGeneration = Guid.CreateVersion7() },
            _ => facts with { VersionIdentity = new MemoryReviewDigest(Digest(0x7f).Bytes) },
        };

        string token = runtime.Codec.IssueObservation(altered).Value;

        CovenantReviewBulkPrepareRequest request = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Confirm,
            [new CovenantReviewDecision(token, null)]);

        await using CovenantReadLease prepareLease = runtime.ReadLease();

        Result<MemoryReviewBulkPlanDto> refused = await runtime.Service.PrepareAsync(
            request,
            prepareLease,
            Token);

        Assert.True(refused.IsFailure);
        Assert.Equal(expectedCode, refused.Error.Code);
    }

    [Fact]
    public async Task Distinct_tokens_for_the_same_exact_version_are_rejected_before_mutation()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "duplicate.target",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "unchanged",
            Token);

        CovenantReviewItemDto observed;

        await using (CovenantReadLease listLease = runtime.ReadLease())
        {
            observed = Assert.Single((await runtime.Service.ListAsync(
                new CovenantReviewListRequest(
                    CovenantScope.Campaign,
                    CampaignOne,
                    CovenantLane.Confirmed,
                    MemoryReviewLimits.MaxPageSize,
                    Cursor: null),
                listLease,
                Token)).Value.Items);
        }

        MemoryReviewObservationTokenFacts facts = runtime.Codec
            .ReadObservation(observed.ObservationToken).Value;

        runtime.Time.Advance(TimeSpan.FromSeconds(1));

        string reissued = runtime.Codec.IssueObservation(facts).Value;

        Assert.NotEqual(observed.ObservationToken, reissued);

        CovenantReviewBulkPrepareRequest request = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Confirm,
            [
                new CovenantReviewDecision(observed.ObservationToken, null),
                new CovenantReviewDecision(reissued, null),
            ]);

        await using CovenantReadLease prepareLease = runtime.ReadLease();

        Result<MemoryReviewBulkPlanDto> refused = await runtime.Service.PrepareAsync(
            request,
            prepareLease,
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.InvalidToken, refused.Error.Code);

        Assert.Equal(0L, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT count(*) FROM covenant_review_decision_receipts;"));

        Assert.Equal(0L, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT ReviewedThroughSequence FROM covenant_review_markers;"));
    }

    private static CovenantDigest Digest(byte value)
    {
        byte[] bytes = new byte[CovenantLimits.DigestBytes];
        bytes.AsSpan().Fill(value);
        return new CovenantDigest(bytes);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private sealed class ReviewRuntime : IAsyncDisposable
    {
        private ReviewRuntime(
            CovenantCanonicalFixture fixture,
            CovenantMemoryReviewService service,
            MemoryReviewTokenCodec codec,
            Guid datasetGeneration,
            FakeTimeProvider time)
        {
            Fixture = fixture;
            Service = service;
            Codec = codec;
            DatasetGeneration = datasetGeneration;
            Time = time;
        }

        internal CovenantCanonicalFixture Fixture { get; }

        internal CovenantMemoryReviewService Service { get; }

        internal MemoryReviewTokenCodec Codec { get; }

        internal FakeTimeProvider Time { get; }

        private Guid DatasetGeneration { get; }

        internal static async Task<ReviewRuntime> CreateAsync()
        {
            CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);
            Guid dataset = await fixture.ReadDatasetGenerationAsync(Token);
            FakeTimeProvider time = new();
            MemoryReviewTokenCodec codec = new(time);

            CovenantMemoryReviewService service = new(
                new FixedCovenantConnectionSource(fixture.Connection),
                new CovenantCompiler(),
                codec,
                new CovenantMutationKernel(),
                new CovenantCurationKernel(),
                time);

            return new ReviewRuntime(fixture, service, codec, dataset, time);
        }

        internal CovenantReadLease ReadLease(Guid? campaignId = null) =>
            new(new LeaseRegistration(
                DatasetGeneration,
                CovenantLeaseKind.Read,
                Scope(campaignId ?? CampaignOne)));

        internal CovenantWriteLease WriteLease() =>
            new(new LeaseRegistration(DatasetGeneration, CovenantLeaseKind.Write, Scope(CampaignOne)));

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        private static CovenantOperationScope Scope(Guid campaignId) =>
            CovenantOperationScope.ForCampaign(campaignId);
    }

    private sealed class LeaseRegistration(
        Guid datasetGeneration,
        CovenantLeaseKind kind,
        CovenantOperationScope scope) : ICovenantLeaseRegistration
    {
        internal int RevalidationCount { get; private set; }

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.CreateVersion7(),
            RuntimeAuthorityGeneration: 1,
            kind,
            CovenantLeaseCoverage.Scoped,
            scope,
            datasetGeneration,
            CapabilityGeneration: 1,
            AuthorityEpoch: 1,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: scope.CampaignId is null ? null : 1,
            CampaignPathRevision: scope.CampaignId is null ? null : 1,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevalidationCount++;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }
}
