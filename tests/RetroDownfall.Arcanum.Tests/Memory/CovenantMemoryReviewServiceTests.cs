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

    /// <summary>
    /// A review correction is an operator write of an identity's Confirmed content. When a fingerprint
    /// for its exact scope and key is still recorded, as a re-creation made while no key was latched
    /// leaves one, the correction releases it in the review's own transaction with the key the review
    /// captured before <c>BEGIN</c>, and says so on the item. Without a latched key it cannot check, and
    /// says that instead. A fingerprint of the same key in Global is another identity, so a Campaign
    /// correction leaves it. A replay answers from its receipt and released nothing.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_review_correction_releases_only_its_own_scopes_fingerprint_and_only_with_a_latched_key(
        bool keyLatched,
        bool erasedInGlobal)
    {
        const string key = "response.detail";

        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync(withErasureEvidence: true);

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            key,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Use moderate detail.",
            Token);

        MemoryErasureIdentity identity = erasedInGlobal
            ? MemoryErasureIdentity.ForCovenant(CovenantScope.Global, null, key)
            : MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, CampaignOne, key);

        bool? expected = erasedInGlobal ? false : keyLatched ? true : null;

        // A latched key is the review's to use; one created and read by another keyring leaves this
        // process's latch unresolved.
        using (MemoryErasureKey erasureKey = keyLatched
            ? runtime.Fixture.ErasureKeys.OpenOrCreate(evidenceRowsExist: false).Key!
            : MemoryErasureTestKeys.CreateKey(runtime.Fixture.Credentials))
        {
            await MemoryErasureTestKeys.SeedFingerprintAsync(runtime.Fixture.Connection, erasureKey, identity, Token);
        }

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

        // The plan discloses, before the question, exactly what the apply will do.
        Assert.Equal(expected, Assert.Single(plan.Items).ReleasesErasureFingerprint);

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

        Assert.Equal(expected, corrected.ReleasedErasureFingerprint);

        Assert.Equal(
            expected is true ? 0L : 1L,
            await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM memory_erasure_fingerprints;"));

        await using CovenantWriteLease replayLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> replayed = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(correction, plan.PreparedPlanToken),
            replayLease,
            Token);

        Assert.True(replayed.IsSuccess, replayed.IsFailure ? replayed.Error.Message : string.Empty);

        Assert.True(replayed.Value.Replayed);

        Assert.False(Assert.Single(replayed.Value.Items).ReleasedErasureFingerprint);
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

    /// <summary>
    /// R-171: Covenant's apply took its write transaction outside any busy retry, so a database another
    /// writer briefly held surfaced as a raw SQLITE_BUSY exception from the first attempt. The transaction
    /// now retries busy and, once the bound is spent, answers the store's own stable write-failed error
    /// with nothing written; the same prepared request then applies cleanly when the writer lets go.
    /// </summary>
    [Fact]
    public async Task Apply_returns_a_stable_error_when_the_database_is_busy()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync(busyRetryDeadline: TimeSpan.Zero);

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "busy.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "content",
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

        CovenantReviewBulkPrepareRequest confirm = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Confirm,
            [new CovenantReviewDecision(observed.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(confirm, prepareLease, Token)).Value;
        }

        // The review's own connection gives up on a held write lock quickly, so the busy answer is prompt.
        runtime.Fixture.Connection.DefaultTimeout = 1;

        await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA busy_timeout = 50;");

        await using SqliteConnection writer = await runtime.Fixture.OpenAdditionalConnectionAsync(Token);

        await ExecuteAsync(writer, "BEGIN IMMEDIATE;");

        Result<MemoryReviewBulkResultDto> busy;

        await using (CovenantWriteLease writeLease = runtime.WriteLease())
        {
            busy = await runtime.Service.ApplyAsync(
                new CovenantReviewBulkApplyRequest(confirm, plan.PreparedPlanToken),
                writeLease,
                Token);
        }

        Assert.True(busy.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.WriteFailed, busy.Error.Code);

        Assert.DoesNotContain("busy.key", busy.Error.Message, StringComparison.Ordinal);

        await ExecuteAsync(writer, "ROLLBACK;");

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));

        await using CovenantWriteLease retryLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(confirm, plan.PreparedPlanToken),
            retryLease,
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal("Confirmed", Assert.Single(applied.Value.Items).Outcome);
    }

    /// <summary>
    /// R-171: the retry is real, not just a bound. A writer that holds the database across the first
    /// attempt and lets go while the apply waits does not fail the apply; the second attempt commits it
    /// once, and replaying that request answers from the receipts it wrote.
    /// </summary>
    [Fact]
    public async Task Apply_retries_a_busy_database_and_commits_once_the_writer_lets_go()
    {
        SqliteConnection? writer = null;

        int waits = 0;

        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync(
            busyRetryDeadline: TimeSpan.FromMinutes(1),
            busyRetryDelay: async (_, _) =>
            {
                waits++;

                await ExecuteAsync(writer!, "ROLLBACK;");
            });

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "busy.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "content",
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

        CovenantReviewBulkPrepareRequest confirm = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            CampaignOne,
            CovenantLane.Confirmed,
            MemoryReviewAction.Confirm,
            [new CovenantReviewDecision(observed.ObservationToken, null)]);

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease prepareLease = runtime.ReadLease())
        {
            plan = (await runtime.Service.PrepareAsync(confirm, prepareLease, Token)).Value;
        }

        runtime.Fixture.Connection.DefaultTimeout = 1;

        await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA busy_timeout = 50;");

        writer = await runtime.Fixture.OpenAdditionalConnectionAsync(Token);

        await using (writer)
        {
            await ExecuteAsync(writer, "BEGIN IMMEDIATE;");

            await using CovenantWriteLease writeLease = runtime.WriteLease();

            Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
                new CovenantReviewBulkApplyRequest(confirm, plan.PreparedPlanToken),
                writeLease,
                Token);

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

            Assert.False(applied.Value.Replayed);

            Assert.Equal(1, waits);
        }

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
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

    /// <summary>
    /// A pin applied from the review queue is the same pin the curation verbs apply: its head records
    /// the key's binding epoch, which a later write to the key leaves alone, and its receipt records
    /// the dependency epoch the change was committed against.
    /// </summary>
    [Fact]
    public async Task A_review_pin_binds_the_key_binding_epoch()
    {
        const string Key = "pin.binding";

        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        SeededHead first = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "stable",
            Token);

        SeededHead second = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "stable, and said twice",
            Token,
            entryId: first.EntryId,
            laneRevision: 2,
            predecessorVersionId: first.VersionId);

        long bindingEpoch = await ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';");

        Assert.Equal(0L, bindingEpoch);

        Assert.Equal(2L, await DependencyEpochAsync(runtime, Key));

        MemoryReviewBulkResultDto pinned = await ApplyLifecycleAsync(runtime, MemoryReviewAction.Pin);

        Assert.Equal("Pinned", Assert.Single(pinned.Items).Outcome);

        Assert.Equal(bindingEpoch, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT KeyEpoch FROM covenant_curation_heads;"));

        Assert.Equal(bindingEpoch, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT KeyEpoch FROM covenant_curation_versions;"));

        Assert.Equal(2L, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT KeyEpoch FROM covenant_curation_receipts;"));

        // A third write moves the dependency epoch and queues a fresh review event. The unpin that
        // follows has to find the pin it is lifting, which it can only do through the binding epoch.
        _ = await runtime.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "stable, and said a third time",
            Token,
            entryId: first.EntryId,
            laneRevision: 3,
            predecessorVersionId: second.VersionId);

        Assert.Equal(3L, await DependencyEpochAsync(runtime, Key));

        MemoryReviewBulkResultDto unpinned = await ApplyLifecycleAsync(runtime, MemoryReviewAction.Unpin);

        Assert.Equal("Unpinned", Assert.Single(unpinned.Items).Outcome);

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_curation_heads;"));

        Assert.Equal(0L, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT IsPinned FROM covenant_curation_heads;"));

        Assert.Equal(2L, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT CurrentRevision FROM covenant_curation_heads;"));

        Assert.Equal(3L, await ScalarAsync(
            runtime.Fixture.Connection,
            "SELECT KeyEpoch FROM covenant_curation_receipts WHERE ResultingRevision = 2;"));
    }

    private static Task<long> DependencyEpochAsync(ReviewRuntime runtime, string key) =>
        ScalarAsync(
            runtime.Fixture.Connection,
            $"SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{key}';");

    /// <summary>Observes the one queued Confirmed item, then prepares and applies one lifecycle action.</summary>
    private static async Task<MemoryReviewBulkResultDto> ApplyLifecycleAsync(
        ReviewRuntime runtime,
        MemoryReviewAction action)
    {
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
            Result<MemoryReviewBulkPlanDto> prepared = await runtime.Service.PrepareAsync(request, prepareLease, Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

            plan = prepared.Value;
        }

        await using CovenantWriteLease writeLease = runtime.WriteLease();

        Result<MemoryReviewBulkResultDto> applied = await runtime.Service.ApplyAsync(
            new CovenantReviewBulkApplyRequest(request, plan.PreparedPlanToken),
            writeLease,
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        return applied.Value;
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

        /// <param name="withErasureEvidence">
        /// Gives the catalog the erasure fingerprint table and builds the kernel over the fixture's own
        /// keyring, so the review captures the latch the suite drives.
        /// </param>
        internal static async Task<ReviewRuntime> CreateAsync(
            bool withErasureEvidence = false,
            TimeSpan? busyRetryDeadline = null,
            Func<TimeSpan, CancellationToken, Task>? busyRetryDelay = null)
        {
            CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
                Token,
                withErasureEvidence: withErasureEvidence);
            Guid dataset = await fixture.ReadDatasetGenerationAsync(Token);
            FakeTimeProvider time = new();
            MemoryReviewTokenCodec codec = new(time);

            CovenantMemoryReviewService service = new(
                new FixedCovenantConnectionSource(fixture.Connection),
                new CovenantCompiler(),
                codec,
                new CovenantMutationKernel(
                    new CovenantQuotaGuard(),
                    withErasureEvidence ? fixture.ErasureKeys : MemoryErasureTestKeys.Isolated()),
                new CovenantCurationKernel(),
                time,
                DetachedAvailabilityRepublisher.Create())
            {
                // Retrying is real; only the clock it waits on is the test's, so exhausting it is instant.
                BusyRetryDeadlineForTesting = busyRetryDeadline,
                BusyRetryDelayForTesting = busyRetryDelay
                    ?? (busyRetryDeadline is null ? null : static (_, _) => Task.CompletedTask),
            };

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
