using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconMemoryReviewServiceTests(GrimoireFixture fixture)
{
    [Fact]
    public async Task Global_queue_is_newest_first_and_confirm_advances_without_changing_content()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail first = await test.SeedAsync();

        var secondWrite = await test.Concrete.UpsertAsync(
            "Second", "place", ["east"], LexiconScope.Global);

        Assert.True(secondWrite.IsSuccess, secondWrite.Error.Message);

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, null), null, CancellationToken.None);

        Assert.True(listed.IsSuccess, listed.Error.Message);
        Assert.Equal(2, listed.Value.Items.Length);
        Assert.True(listed.Value.Items[0].EventSequence > listed.Value.Items[1].EventSequence);
        Assert.Equal(secondWrite.Value.Id, listed.Value.Items[0].EntryId);
        Assert.Equal(first.Entry.Id, listed.Value.Items[1].EntryId);
        Assert.All(listed.Value.Items, item =>
        {
            Assert.Equal(AnnalOrigin.AgentAsserted, item.Origin);
            Assert.Equal(LexiconScopeKind.Global, item.Scope.Kind);
            Assert.True(item.IsCurrent);
            Assert.NotNull(item.Current);
            Assert.False(string.IsNullOrWhiteSpace(item.ObservationToken));
        });

        string[] contentBefore = await test.SnapshotAsync();

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [.. listed.Value.Items.Select(static item => new LexiconReviewDecision(item.ObservationToken, null))]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        var applied = await review.ApplyAsync(
            new(request, prepared.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);
        Assert.False(applied.Value.Replayed);
        Assert.Equal(listed.Value.FrozenThroughEventSequence, applied.Value.ReviewedThroughEventSequence);
        Assert.Equal(contentBefore, await test.SnapshotAsync());

        var empty = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, null), null, CancellationToken.None);

        Assert.True(empty.IsSuccess, empty.Error.Message);
        Assert.Empty(empty.Value.Items);
    }

    [Fact]
    public async Task High_history_queue_returns_bounded_current_snapshot_and_preserves_protected_authority()
    {
        await using CorrectionFixture test = new(fixture);

        _ = await test.SeedAsync();
        _ = await test.ProtectAsync(withHead: true);

        LexiconEntryDetail expected = await test.ShowProtectedAsync();

        using LeaseRegistration writeRegistration = new(CovenantLeaseKind.Write);
        await using CovenantWriteLease writeLease = new(writeRegistration);

        for (int index = 0; index < 24; index++)
        {
            Result<LexiconCurationResult> corrected = await test.Service.CorrectAsync(
                expected.Target,
                new("general", ["alpha", $"revision-{index:D2}"]),
                writeLease);

            Assert.True(corrected.IsSuccess, corrected.Error.Message);

            expected = corrected.Value.Entry;
        }

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        Result<LexiconReviewPageDto> refused = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        Assert.True(refused.IsFailure);

        using LeaseRegistration registration = new(CovenantLeaseKind.Read);
        await using CovenantReadLease readLease = new(registration);

        List<string> statements = [];

        raw.sqlite3_trace(test.Connection.Handle, (object _, string sql) => statements.Add(sql), null);

        Result<LexiconReviewPageDto> listed;

        try
        {
            listed = await review.ListAsync(
                new(LexiconCorrectionTests.Global, 1, null), readLease, CancellationToken.None);
        }
        finally
        {
            raw.sqlite3_trace(test.Connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.True(listed.IsSuccess, listed.Error.Message);
        Assert.True(listed.Value.ContainsProtectedContent);

        Assert.DoesNotContain(
            statements,
            static sql => sql.Contains("ORDER BY v.Revision", StringComparison.Ordinal));

        Assert.DoesNotContain(
            statements,
            static sql => sql.Contains("FROM lexicon_annal_fact_provenance", StringComparison.Ordinal));

        LexiconReviewCurrentDto current = Assert.IsType<LexiconReviewCurrentDto>(Assert.Single(listed.Value.Items).Current);

        Assert.Equal(expected.Entry.Name, current.Entry.Name);
        Assert.Equal(expected.Entry.Type, current.Entry.Type);
        Assert.Equal(expected.Entry.Facts, current.Entry.Facts);
        Assert.Equal(expected.Scope, current.Scope);
        Assert.Equal(expected.CurrentOrigin, current.CurrentOrigin);
        Assert.Equal(expected.Lifecycle, current.Lifecycle);
        Assert.Equal(expected.Eligibility, current.Eligibility);
        Assert.Equal(expected.CurationGeneration, current.CurationGeneration);
        Assert.Equal(expected.SnapshotDigest, current.SnapshotDigest);
        Assert.Equal(expected.Target, current.Target);
        Assert.True(current.Target.SensitivityLabel.IsPresent);
    }

    [Fact]
    public async Task Cursor_freezes_the_upper_frontier_while_new_events_arrive()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        _ = await test.SeedAsync();
        Assert.True((await test.Concrete.UpsertAsync("Second", "place", ["east"], LexiconScope.Global)).IsSuccess);
        Assert.True((await test.Concrete.UpsertAsync("Third", "place", ["west"], LexiconScope.Global)).IsSuccess);

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var first = await review.ListAsync(new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(first.Value.Truncated);

        long frozen = first.Value.FrozenThroughEventSequence;

        Assert.True((await test.Concrete.UpsertAsync("Fourth", "place", ["north"], LexiconScope.Global)).IsSuccess);

        var second = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, first.Value.NextCursor), null, CancellationToken.None);

        Assert.True(second.IsSuccess, second.Error.Message);
        Assert.Equal(frozen, second.Value.FrozenThroughEventSequence);
        Assert.All(second.Value.Items, item => Assert.True(item.EventSequence <= frozen));
        Assert.DoesNotContain(second.Value.Items, item => item.Current?.Entry.Name == "Fourth");
    }

    [Fact]
    public async Task Cursor_is_rejected_when_its_keyset_event_was_hard_deleted()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        _ = await test.SeedAsync();
        Assert.True((await test.Concrete.UpsertAsync("Second", "place", ["east"], LexiconScope.Global)).IsSuccess);

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var first = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.NotNull(first.Value.NextCursor);

        await test.ExecuteAsync(
            $"DELETE FROM annal_review_events WHERE Sequence = {first.Value.Items[0].EventSequence}");

        var continuation = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, first.Value.NextCursor), null, CancellationToken.None);

        Assert.True(continuation.IsFailure);
        Assert.Equal(ErrorCodes.MemoryReview.InvalidToken, continuation.Error.Code);
    }

    [Fact]
    public async Task Observation_is_bound_to_the_exact_scope()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        _ = await test.SeedAsync();

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        Assert.True(listed.IsSuccess, listed.Error.Message);

        LexiconCurationScope campaign = new(LexiconScopeKind.Campaign, Guid.NewGuid());

        var prepared = await review.PrepareAsync(
            new(
                Guid.NewGuid(),
                campaign,
                MemoryReviewAction.Confirm,
                [new(listed.Value.Items[0].ObservationToken, null)]),
            null,
            CancellationToken.None);

        Assert.True(prepared.IsFailure);
    }

    [Fact]
    public async Task Correction_is_exact_version_bound_and_its_output_is_auto_acknowledged()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        _ = await test.SeedAsync();

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Correct,
            [new(listed.Value.Items[0].ObservationToken, new("person", ["gamma"]))]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);
        var applied = await review.ApplyAsync(new(request, prepared.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        MemoryReviewBulkItemResultDto appliedItem = Assert.Single(applied.Value.Items);

        Assert.Equal(MemoryReviewOutcomes.Corrected, appliedItem.Outcome);
        Assert.NotNull(appliedItem.ResultingVersionId);

        LexiconEntryDetail after = await test.ShowAsync();

        Assert.Equal("person", after.Entry.Type);
        Assert.Equal(["gamma"], after.Entry.Facts);

        var empty = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, null), null, CancellationToken.None);

        Assert.True(empty.IsSuccess, empty.Error.Message);
        Assert.Empty(empty.Value.Items);
    }

    [Theory]
    [InlineData(MemoryReviewAction.Retire)]
    [InlineData(MemoryReviewAction.Pin)]
    [InlineData(MemoryReviewAction.Unpin)]
    public async Task Lifecycle_batches_apply_atomically_to_the_observed_version(MemoryReviewAction action)
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail before = await test.SeedAsync();

        if (action == MemoryReviewAction.Unpin)
        {
            Assert.True((await test.Service.PinAsync(before.Target, null)).IsSuccess);
        }

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            action,
            [new(listed.Value.Items[0].ObservationToken, null)]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);
        var applied = await review.ApplyAsync(new(request, prepared.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(
            MemoryReviewOutcomes.Applied(action),
            Assert.Single(applied.Value.Items).Outcome);

        LexiconEntryDetail after = await test.ShowAsync();

        Assert.Equal(action == MemoryReviewAction.Retire, after.Lifecycle.RetiredAtUtc is not null);
        Assert.Equal(action == MemoryReviewAction.Pin, after.Lifecycle.PinnedAtUtc is not null);
    }

    [Fact]
    public async Task Exact_request_replay_returns_the_original_result_after_the_marker_moves()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        _ = await test.SeedAsync();

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [new(listed.Value.Items[0].ObservationToken, null)]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);
        LexiconReviewBulkApplyRequest apply = new(request, prepared.Value.PreparedPlanToken);

        var first = await review.ApplyAsync(apply, null, CancellationToken.None);
        var replay = await review.ApplyAsync(apply, null, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.True(replay.Value.Replayed);
        Assert.Equal(first.Value.Items, replay.Value.Items);
        Assert.Equal(first.Value.ReviewedThroughEventSequence, replay.Value.ReviewedThroughEventSequence);
    }

    [Fact]
    public async Task Exact_request_replay_precedes_plan_expiry_and_changed_reuse_is_rejected()
    {
        FakeTimeProvider time = new();

        time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

        MemoryReviewTokenCodec codec = new(time);

        await using CorrectionFixture test = new(fixture, annals: true, reviewTokenCodec: codec);

        _ = await test.SeedAsync();

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [new(listed.Value.Items[0].ObservationToken, null)]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);
        LexiconReviewBulkApplyRequest apply = new(request, prepared.Value.PreparedPlanToken);

        Assert.True((await review.ApplyAsync(apply, null, CancellationToken.None)).IsSuccess);

        time.Advance(MemoryReviewLimits.TokenLifetime + TimeSpan.FromSeconds(1));

        var replay = await review.ApplyAsync(apply, null, CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.True(replay.Value.Replayed);

        LexiconReviewBulkPrepareRequest changed = request with { Action = MemoryReviewAction.Pin };

        var conflict = await review.ApplyAsync(
            new(changed, prepared.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(conflict.IsFailure);
    }

    [Fact]
    public async Task Exact_request_replay_preserves_the_original_durable_result_after_later_reviews()
    {
        FakeTimeProvider time = new();

        time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

        MemoryReviewTokenCodec codec = new(time);

        await using CorrectionFixture test = new(fixture, annals: true, reviewTokenCodec: codec);

        _ = await test.SeedAsync();

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest correction = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Correct,
            [new(listed.Value.Items[0].ObservationToken, new("person", ["corrected"]))]);

        var prepared = await review.PrepareAsync(correction, null, CancellationToken.None);
        LexiconReviewBulkApplyRequest originalApply = new(correction, prepared.Value.PreparedPlanToken);
        var original = await review.ApplyAsync(originalApply, null, CancellationToken.None);

        Assert.True(original.IsSuccess, original.Error.Message);
        Assert.NotNull(Assert.Single(original.Value.Items).ResultingVersionId);

        Assert.True((await test.Concrete.UpsertAsync(
            "Later", "place", ["north"], LexiconScope.Global)).IsSuccess);

        var laterPage = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest laterConfirmation = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [new(laterPage.Value.Items[0].ObservationToken, null)]);

        var laterPlan = await review.PrepareAsync(laterConfirmation, null, CancellationToken.None);
        var laterApplied = await review.ApplyAsync(
            new(laterConfirmation, laterPlan.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(laterApplied.IsSuccess, laterApplied.Error.Message);
        Assert.True(laterApplied.Value.ReviewedThroughEventSequence > original.Value.ReviewedThroughEventSequence);

        time.Advance(MemoryReviewLimits.TokenLifetime + TimeSpan.FromSeconds(1));

        var replay = await review.ApplyAsync(originalApply, null, CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.True(replay.Value.Replayed);
        Assert.Equal(original.Value.Store, replay.Value.Store);
        Assert.Equal(original.Value.RequestId, replay.Value.RequestId);
        Assert.Equal(original.Value.Action, replay.Value.Action);
        Assert.Equal(original.Value.Items, replay.Value.Items);
        Assert.Equal(original.Value.ReviewedThroughEventSequence, replay.Value.ReviewedThroughEventSequence);
    }

    [Theory]
    [InlineData(MemoryReviewAction.Correct, "Unchanged")]
    [InlineData(MemoryReviewAction.Retire, "AlreadyRetired")]
    [InlineData(MemoryReviewAction.Pin, "AlreadyPinned")]
    [InlineData(MemoryReviewAction.Unpin, "NotPinned")]
    public async Task Accepted_no_op_action_remains_exactly_replayable(
        MemoryReviewAction action,
        string expectedOutcome)
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail before = await test.SeedAsync();

        if (action == MemoryReviewAction.Retire)
        {
            Assert.True((await test.Service.RetireAsync(before.Target, null)).IsSuccess);
        }
        else if (action == MemoryReviewAction.Pin)
        {
            Assert.True((await test.Service.PinAsync(before.Target, null)).IsSuccess);
        }

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReplacementContent? replacement = action == MemoryReviewAction.Correct
            ? new("general", ["alpha", "beta"])
            : null;

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            action,
            [new(listed.Value.Items[0].ObservationToken, replacement)]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);
        LexiconReviewBulkApplyRequest apply = new(request, prepared.Value.PreparedPlanToken);
        var original = await review.ApplyAsync(apply, null, CancellationToken.None);
        var replay = await review.ApplyAsync(apply, null, CancellationToken.None);

        Assert.True(original.IsSuccess, original.Error.Message);

        MemoryReviewBulkItemResultDto originalItem = Assert.Single(original.Value.Items);

        Assert.Equal(expectedOutcome, originalItem.Outcome);
        Assert.Null(originalItem.ResultingVersionId);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.True(replay.Value.Replayed);
        Assert.Equal(original.Value.Items, replay.Value.Items);
        Assert.Equal(original.Value.ReviewedThroughEventSequence, replay.Value.ReviewedThroughEventSequence);
        Assert.Equal(expectedOutcome, Assert.Single(replay.Value.Items).Outcome);
    }

    [Fact]
    public async Task Marker_advances_over_only_the_contiguous_acknowledged_events_in_its_filtered_scope()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        _ = await test.SeedAsync();
        Assert.True((await test.Concrete.UpsertAsync("Second", "place", ["east"], LexiconScope.Global)).IsSuccess);
        Assert.True((await test.Concrete.UpsertAsync("Third", "place", ["west"], LexiconScope.Global)).IsSuccess);

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, null), null, CancellationToken.None);

        LexiconReviewItemDto newest = listed.Value.Items[0];

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [new(newest.ObservationToken, null)]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);
        var applied = await review.ApplyAsync(new(request, prepared.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);
        Assert.Equal(0, applied.Value.ReviewedThroughEventSequence);

        var remaining = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, null), null, CancellationToken.None);

        Assert.True(remaining.IsSuccess, remaining.Error.Message);
        Assert.Equal(2, remaining.Value.Items.Length);
        Assert.DoesNotContain(remaining.Value.Items, item => item.EventSequence == newest.EventSequence);

        LexiconReviewBulkPrepareRequest duplicate = request with { RequestId = Guid.NewGuid() };

        var alreadyReviewed = await review.PrepareAsync(duplicate, null, CancellationToken.None);

        Assert.True(alreadyReviewed.IsFailure);
        Assert.Equal(ErrorCodes.MemoryReview.UnseenObservation, alreadyReviewed.Error.Code);
    }

    [Fact]
    public async Task Concurrent_identical_apply_rechecks_receipts_after_acquiring_the_write_lock()
    {
        await using CorrectionFixture first = new(fixture, annals: true);

        _ = await first.SeedAsync();

        ILexiconMemoryReviewService firstReview = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(first.Concrete);

        var listed = await firstReview.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [new(listed.Value.Items[0].ObservationToken, null)]);

        var prepared = await firstReview.PrepareAsync(request, null, CancellationToken.None);
        LexiconReviewBulkApplyRequest apply = new(request, prepared.Value.PreparedPlanToken);

        await using var secondDb = fixture.CreateContext(first.Path);
        LexiconService secondService = new(
            secondDb,
            NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings>(first.Settings),
            MemoryErasureTestKeys.Isolated());

        ILexiconMemoryReviewService secondReview = secondService;

        await using var blockerDb = fixture.CreateContext(first.Path);
        await blockerDb.Database.OpenConnectionAsync();

        SqliteConnection blocker = (SqliteConnection)blockerDb.Database.GetDbConnection();

        await using (SqliteCommand begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync();
        }

        TaskCompletionSource firstPrecheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondPrecheck = new(TaskCreationOptions.RunContinuationsAsynchronously);

        raw.sqlite3_trace(first.Connection.Handle, (_, sql) =>
        {
            if (sql.Contains("FROM annal_review_decision_receipts", StringComparison.Ordinal))
            {
                firstPrecheck.TrySetResult();
            }
        }, null);

        await secondDb.Database.OpenConnectionAsync();

        SqliteConnection secondConnection = (SqliteConnection)secondDb.Database.GetDbConnection();

        raw.sqlite3_trace(secondConnection.Handle, (_, sql) =>
        {
            if (sql.Contains("FROM annal_review_decision_receipts", StringComparison.Ordinal))
            {
                secondPrecheck.TrySetResult();
            }
        }, null);

        Task<Result<MemoryReviewBulkResultDto>> firstTask = Task.Run(
            () => firstReview.ApplyAsync(apply, null, CancellationToken.None));
        Task<Result<MemoryReviewBulkResultDto>> secondTask = Task.Run(
            () => secondReview.ApplyAsync(apply, null, CancellationToken.None));

        await Task.WhenAll(
            firstPrecheck.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            secondPrecheck.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await using (SqliteCommand commit = blocker.CreateCommand())
        {
            commit.CommandText = "COMMIT";
            await commit.ExecuteNonQueryAsync();
        }

        Result<MemoryReviewBulkResultDto>[] results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error.Message));
        Assert.Single(results, result => !result.Value.Replayed);
        Assert.Single(results, result => result.Value.Replayed);
        Assert.Equal(1L, await first.ScalarAsync("SELECT count(*) FROM annal_review_decision_receipts"));
    }

    [Fact]
    public async Task Protected_review_requires_matching_read_and_write_leases()
    {
        await using CorrectionFixture test = new(fixture);

        _ = await test.SeedAsync();
        _ = await test.ProtectAsync(withHead: true);

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var refused = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        Assert.True(refused.IsFailure);

        using LeaseRegistration readRegistration = new(CovenantLeaseKind.Read);
        await using CovenantReadLease readLease = new(readRegistration);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), readLease, CancellationToken.None);

        Assert.True(listed.IsSuccess, listed.Error.Message);
        Assert.True(listed.Value.ContainsProtectedContent);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [new(listed.Value.Items[0].ObservationToken, null)]);

        var prepared = await review.PrepareAsync(request, readLease, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        var missingWrite = await review.ApplyAsync(
            new(request, prepared.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(missingWrite.IsFailure);

        using LeaseRegistration writeRegistration = new(CovenantLeaseKind.Write);
        await using CovenantWriteLease writeLease = new(writeRegistration);

        var applied = await review.ApplyAsync(
            new(request, prepared.Value.PreparedPlanToken), writeLease, CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        LexiconReviewBulkApplyRequest apply = new(request, prepared.Value.PreparedPlanToken);

        var missingReplay = await review.ApplyAsync(apply, null, CancellationToken.None);

        Assert.True(missingReplay.IsFailure);
        Assert.Equal(ErrorCodes.Lexicon.ProtectedMutationRefused, missingReplay.Error.Code);

        using LeaseRegistration staleRegistration = new(CovenantLeaseKind.Write) { StaleAfter = 0 };
        await using CovenantWriteLease staleLease = new(staleRegistration);

        var staleReplay = await review.ApplyAsync(apply, staleLease, CancellationToken.None);

        Assert.True(staleReplay.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, staleReplay.Error.Code);

        Guid otherCampaign = Guid.NewGuid();

        using LeaseRegistration mismatchRegistration = new(CovenantLeaseKind.Write)
        {
            Snapshot = new(
                Guid.NewGuid(),
                1,
                CovenantLeaseKind.Write,
                CovenantLeaseCoverage.Scoped,
                CovenantOperationScope.ForCampaign(otherCampaign),
                null,
                1,
                1,
                0,
                null,
                null,
                null,
                null,
                null,
                false),
        };
        await using CovenantWriteLease mismatchLease = new(mismatchRegistration);

        var mismatchedReplay = await review.ApplyAsync(apply, mismatchLease, CancellationToken.None);

        Assert.True(mismatchedReplay.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, mismatchedReplay.Error.Code);

        var authorizedReplay = await review.ApplyAsync(apply, writeLease, CancellationToken.None);

        Assert.True(authorizedReplay.IsSuccess, authorizedReplay.Error.Message);
        Assert.True(authorizedReplay.Value.Replayed);
    }

    /// <summary>
    /// R-025: a batch that applies one correction and then meets a stale target has to roll the applied
    /// correction back with its receipts and marker. The earlier version of this test used Confirm, which
    /// writes nothing, and receipts are written after the loop, so a zero receipt count held with or
    /// without a rollback.
    /// </summary>
    /// <summary>
    /// R-172: a receipt an earlier build persisted spells its outcome in lowercase, in the receipt's
    /// identifier and in the digest that seals it. Replay has to keep accepting that spelling and report
    /// the closed vocabulary's. The legacy receipts are written here exactly as that build wrote them, by
    /// the same canonical encoding, because they are persisted data that can never be rewritten.
    /// </summary>
    [Theory]
    [InlineData(MemoryReviewAction.Confirm)]
    [InlineData(MemoryReviewAction.Correct)]
    [InlineData(MemoryReviewAction.Retire)]
    [InlineData(MemoryReviewAction.Pin)]
    public async Task A_receipt_persisted_with_a_lowercase_outcome_still_replays_as_the_closed_spelling(
        MemoryReviewAction action)
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        _ = await test.SeedAsync();

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 1, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            action,
            [new(
                listed.Value.Items[0].ObservationToken,
                action == MemoryReviewAction.Correct ? new LexiconReplacementContent("person", ["gamma"]) : null)]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);

        LexiconReviewBulkApplyRequest apply = new(request, prepared.Value.PreparedPlanToken);

        var original = await review.ApplyAsync(apply, null, CancellationToken.None);

        Assert.True(original.IsSuccess, original.Error.Message);

        await RewriteReceiptsAsLegacyAsync(test, request);

        var replay = await review.ApplyAsync(apply, null, CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(original.Value.Items, replay.Value.Items);

        Assert.Equal(MemoryReviewOutcomes.Applied(action), Assert.Single(replay.Value.Items).Outcome);
    }

    /// <summary>
    /// Rewrites every receipt of the request into the spelling builds before the closed vocabulary wrote:
    /// lowercase outcomes in the identifier, <c>auto-acknowledged</c> on a correction's replacement, and
    /// the response digest recomputed over exactly those strings.
    /// </summary>
    private static async Task RewriteReceiptsAsLegacyAsync(
        CorrectionFixture test,
        LexiconReviewBulkPrepareRequest request)
    {
        static string Canonical(System.Text.StringBuilder builder, string value) =>
            builder.Append(value.Length).Append(':').Append(value).Append('|').ToString();

        System.Text.StringBuilder ordered = new();

        _ = Canonical(ordered, "lexicon");
        _ = Canonical(ordered, request.RequestId.ToString("D"));
        _ = Canonical(ordered, ((int)request.Action).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = Canonical(ordered, request.Scope.Kind.ToString());
        _ = Canonical(ordered, request.Scope.CampaignId?.ToString("D") ?? string.Empty);

        foreach (LexiconReviewDecision decision in request.Decisions)
        {
            _ = Canonical(ordered, decision.ObservationToken);
            _ = Canonical(ordered, decision.ReplacementContent?.Type ?? string.Empty);

            foreach (string fact in decision.ReplacementContent?.Facts ?? [])
            {
                _ = Canonical(ordered, fact);
            }

            _ = ordered.Append(';');
        }

        string orderedHex = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ordered.ToString())));

        List<(string DecisionId, long EventSequence)> rows = [];

        await using (SqliteCommand read = test.Connection.CreateCommand())
        {
            read.CommandText = "SELECT DecisionId, ReviewEventSequence FROM annal_review_decision_receipts";

            await using SqliteDataReader reader = await read.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        Assert.NotEmpty(rows);

        foreach ((string decisionId, long eventSequence) in rows)
        {
            string[] parts = decisionId.Split(':');

            bool replacement = parts[2] == "R";

            string legacy = parts[4] switch
            {
                "Confirmed" => "acknowledged",
                "Corrected" => "corrected",
                "Retired" => "retired",
                "Pinned" => "pinned",
                "Unpinned" => "unpinned",
                _ => parts[4],
            };

            parts[4] = legacy;

            string legacyId = string.Join(':', parts);

            string subject;

            string version;

            await using (SqliteCommand eventRead = test.Connection.CreateCommand())
            {
                eventRead.CommandText = "SELECT SubjectId, VersionId FROM annal_review_events WHERE Sequence = $sequence";

                _ = eventRead.Parameters.AddWithValue("$sequence", eventSequence);

                await using SqliteDataReader reader = await eventRead.ExecuteReaderAsync();

                Assert.True(await reader.ReadAsync());

                subject = Guid.Parse(reader.GetString(0)).ToString("D");

                version = reader.GetString(1);
            }

            string? resulting = replacement ? version : parts[7] == "-" ? null : parts[7];

            System.Text.StringBuilder response = new();

            _ = Canonical(response, orderedHex);
            _ = Canonical(response, legacyId);
            _ = Canonical(response, eventSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = Canonical(response, subject);
            _ = Canonical(response, version);
            _ = Canonical(response, replacement ? "auto-acknowledged" : legacy);
            _ = Canonical(response, resulting ?? string.Empty);

            await using SqliteCommand update = test.Connection.CreateCommand();

            update.CommandText =
                "UPDATE annal_review_decision_receipts SET DecisionId = $new, ResponseReceiptDigest = $digest WHERE DecisionId = $old";

            _ = update.Parameters.AddWithValue("$new", legacyId);

            _ = update.Parameters.AddWithValue(
                "$digest",
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(response.ToString())));

            _ = update.Parameters.AddWithValue("$old", decisionId);

            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task One_stale_target_after_an_applied_correction_rolls_back_the_earlier_correction_receipts_and_marker()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail first = await test.SeedAsync();

        Assert.True((await test.Concrete.UpsertAsync("Second", "place", ["east"], LexiconScope.Global)).IsSuccess);

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, null), null, CancellationToken.None);

        // Newest first: "Second" is decided, and its correction written, before the stale "Entity" is met.
        Assert.Equal(2, listed.Value.Items.Length);

        Assert.Equal(first.Entry.Id, listed.Value.Items[1].EntryId);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Correct,
            [
                new LexiconReviewDecision(listed.Value.Items[0].ObservationToken, new("place", ["east", "west"])),
                new LexiconReviewDecision(listed.Value.Items[1].ObservationToken, new("person", ["gamma"])),
            ]);

        var prepared = await review.PrepareAsync(request, null, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        // The stale target: corrected elsewhere after the plan was prepared.
        Assert.True((await test.Service.CorrectAsync(first.Target, new("person", ["changed"]), null)).IsSuccess);

        string[] storeBefore = await test.SnapshotAsync();

        object? markerBefore = await test.ScalarAsync(
            "SELECT ReviewedThroughSequence || ':' || Revision FROM annal_review_markers WHERE SubjectStoreCode = 2");

        Assert.NotNull(markerBefore);

        object? eventsBefore = await test.ScalarAsync("SELECT count(*) FROM annal_review_events");

        var applied = await review.ApplyAsync(new(request, prepared.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.StaleObservation, applied.Error.Code);

        Assert.Equal(0L, await test.ScalarAsync("SELECT count(*) FROM annal_review_decision_receipts"));

        Assert.Equal(
            markerBefore,
            await test.ScalarAsync(
                "SELECT ReviewedThroughSequence || ':' || Revision FROM annal_review_markers WHERE SubjectStoreCode = 2"));

        Assert.Equal(eventsBefore, await test.ScalarAsync("SELECT count(*) FROM annal_review_events"));

        // The first decision's correction is gone with everything it wrote: entry, search row, Annals
        // version and head, and provenance.
        Assert.Equal(storeBefore, await test.SnapshotAsync());

        LexiconEntryDetail second = (await test.Concrete.ShowExactAsync(
            LexiconCorrectionTests.Global,
            "second",
            null)).Value.Value;

        Assert.Equal("place", second.Entry.Type);

        Assert.Equal(["east"], second.Entry.Facts);
    }

    [Fact]
    public async Task Distinct_tokens_for_the_same_exact_version_are_rejected_before_mutation()
    {
        FakeTimeProvider time = new();

        time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

        MemoryReviewTokenCodec codec = new(time);

        await using CorrectionFixture test = new(
            fixture,
            annals: true,
            reviewTokenCodec: codec);

        _ = await test.SeedAsync();

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        LexiconReviewItemDto observed = Assert.Single((await review.ListAsync(
            new LexiconReviewListRequest(LexiconCorrectionTests.Global, 1, Cursor: null),
            readLease: null,
            CancellationToken.None)).Value.Items);

        MemoryReviewObservationTokenFacts facts = codec.ReadObservation(observed.ObservationToken).Value;

        time.Advance(TimeSpan.FromSeconds(1));

        string reissued = codec.IssueObservation(facts).Value;

        Assert.NotEqual(observed.ObservationToken, reissued);

        LexiconReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [
                new LexiconReviewDecision(observed.ObservationToken, null),
                new LexiconReviewDecision(reissued, null),
            ]);

        Result<MemoryReviewBulkPlanDto> refused = await review.PrepareAsync(
            request,
            readLease: null,
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.InvalidToken, refused.Error.Code);

        Assert.Equal(0L, await test.ScalarAsync("SELECT count(*) FROM annal_review_decision_receipts"));

        Assert.Equal(0L, await test.ScalarAsync(
            "SELECT ReviewedThroughSequence FROM annal_review_markers WHERE SubjectStoreCode = 2"));
    }

    [Fact]
    public async Task Superseded_observation_is_rejected_and_refresh_lists_only_the_actionable_head()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail before = await test.SeedAsync();

        ILexiconMemoryReviewService review = Assert.IsAssignableFrom<ILexiconMemoryReviewService>(test.Concrete);

        var listed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, null), null, CancellationToken.None);

        LexiconReviewBulkPrepareRequest staleRequest = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [new(listed.Value.Items[0].ObservationToken, null)]);

        var stalePlan = await review.PrepareAsync(staleRequest, null, CancellationToken.None);

        Assert.True(stalePlan.IsSuccess, stalePlan.Error.Message);
        Assert.True((await test.Service.CorrectAsync(before.Target, new("person", ["changed"]), null)).IsSuccess);

        var stale = await review.ApplyAsync(
            new(staleRequest, stalePlan.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(stale.IsFailure);

        var refreshed = await review.ListAsync(
            new(LexiconCorrectionTests.Global, 50, null), null, CancellationToken.None);

        LexiconReviewItemDto actionable = Assert.Single(refreshed.Value.Items);

        Assert.True(actionable.IsCurrent);
        Assert.NotEqual(listed.Value.Items[0].VersionId, actionable.VersionId);

        LexiconReviewBulkPrepareRequest confirm = new(
            Guid.NewGuid(),
            LexiconCorrectionTests.Global,
            MemoryReviewAction.Confirm,
            [new(actionable.ObservationToken, null)]);

        var plan = await review.PrepareAsync(confirm, null, CancellationToken.None);
        var applied = await review.ApplyAsync(new(confirm, plan.Value.PreparedPlanToken), null, CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);
        Assert.Equal(actionable.EventSequence, applied.Value.ReviewedThroughEventSequence);
        Assert.Equal(1L, await test.ScalarAsync("SELECT count(*) FROM annal_review_decision_receipts"));
    }
}
