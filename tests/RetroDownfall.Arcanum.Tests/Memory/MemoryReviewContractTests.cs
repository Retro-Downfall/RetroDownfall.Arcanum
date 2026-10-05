using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed class MemoryReviewContractTests
{
    private static readonly Guid Campaign = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid RequestId = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    /// <summary>
    /// R-172: each store used to invent its own per-item outcome strings, and Lexicon mixed casings
    /// (<c>corrected</c> beside <c>AlreadyRetired</c>). Every outcome is now one member of a single closed,
    /// PascalCase set that API §8.34 enumerates, and receipts persisted with an earlier build's lowercase
    /// spelling still read back as that set.
    /// </summary>
    [Fact]
    public void Every_store_reports_outcomes_from_one_closed_vocabulary()
    {
        Assert.Equal(MemoryReviewOutcomes.All.Count, MemoryReviewOutcomes.All.Distinct(StringComparer.Ordinal).Count());

        Assert.All(
            MemoryReviewOutcomes.All,
            static outcome => Assert.Matches("^[A-Z][A-Za-z]+$", outcome));

        foreach (MemoryReviewAction action in Enum.GetValues<MemoryReviewAction>())
        {
            string applied = MemoryReviewOutcomes.Applied(action);

            Assert.Contains(applied, MemoryReviewOutcomes.All);

            Assert.Equal(applied, MemoryReviewOutcomes.FromPersisted(applied));
        }

        // What an earlier Lexicon persisted.
        Assert.Equal(MemoryReviewOutcomes.Confirmed, MemoryReviewOutcomes.FromPersisted("acknowledged"));

        Assert.Equal(MemoryReviewOutcomes.Corrected, MemoryReviewOutcomes.FromPersisted("corrected"));

        Assert.Equal(MemoryReviewOutcomes.Retired, MemoryReviewOutcomes.FromPersisted("retired"));

        Assert.Equal(MemoryReviewOutcomes.Pinned, MemoryReviewOutcomes.FromPersisted("pinned"));

        Assert.Equal(MemoryReviewOutcomes.Unpinned, MemoryReviewOutcomes.FromPersisted("unpinned"));

        Assert.Equal(MemoryReviewOutcomes.AutoAcknowledged, MemoryReviewOutcomes.FromPersisted("auto-acknowledged"));

        // Closed means closed: neither another casing of a member nor an invented word is an outcome.
        Assert.Null(MemoryReviewOutcomes.FromPersisted("CORRECTED"));

        Assert.Null(MemoryReviewOutcomes.FromPersisted("Applied"));

        Assert.Null(MemoryReviewOutcomes.FromPersisted(null));

        Assert.False(MemoryReviewOutcomes.IsKnown("corrected"));
    }

    /// <summary>
    /// R-168: replaying a request looks up every receipt of that request, and it ran twice per apply, once
    /// of them under the write lock. The predicate was a <c>substr</c> over the primary key, which SQLite
    /// cannot seek, so every apply scanned every receipt ever written. The three stores' statements are
    /// explained as the services run them, and each must seek the primary-key index over a key range.
    /// </summary>
    [SkippableFact]
    public async Task Receipt_lookup_uses_the_primary_key_index()
    {
        await using SagaStoreHarness grimoire = await SagaStoreHarness.CreateAsync(annalsEnabled: true)
            .ConfigureAwait(false);

        AssertSeeksPrimaryKey(
            "Saga",
            await ExplainReceiptLookupAsync(grimoire.Connection, SagaMemoryReviewService.ReceiptLookupSql, "@")
                .ConfigureAwait(false));

        AssertSeeksPrimaryKey(
            "Lexicon",
            await ExplainReceiptLookupAsync(grimoire.Connection, LexiconService.ReceiptLookupSql, "@")
                .ConfigureAwait(false));

        await using CovenantCanonicalFixture canonical = await CovenantCanonicalFixture
            .CreateAsync(CancellationToken.None)
            .ConfigureAwait(false);

        AssertSeeksPrimaryKey(
            "Covenant",
            await ExplainReceiptLookupAsync(canonical.Connection, CovenantMemoryReviewService.ReceiptLookupSql, "$")
                .ConfigureAwait(false));
    }

    private static async Task<string> ExplainReceiptLookupAsync(
        System.Data.Common.DbConnection connection,
        string sql,
        string parameterPrefix)
    {
        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        string prefix = RequestId.ToString("N") + ":";

        foreach ((string name, string value) in new[]
        {
            ("prefix", prefix),
            ("prefixUpper", RequestId.ToString("N") + ";"),
        })
        {
            // Bound only where the statement names it, so the current predicate (one parameter) and the
            // range predicate (two) are both explained as written.
            if (!sql.Contains(parameterPrefix + name, StringComparison.Ordinal))
            {
                continue;
            }

            System.Data.Common.DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = parameterPrefix + name;

            parameter.Value = value;

            command.Parameters.Add(parameter);
        }

        System.Text.StringBuilder plan = new();

        await using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            _ = plan.AppendLine(reader.GetString(reader.FieldCount - 1));
        }

        return plan.ToString();
    }

    private static void AssertSeeksPrimaryKey(string store, string plan)
    {
        Assert.True(
            plan.Contains("SEARCH", StringComparison.Ordinal)
            && plan.Contains("sqlite_autoindex_", StringComparison.Ordinal)
            && !plan.Contains("SCAN", StringComparison.Ordinal),
            $"{store} receipt lookup does not seek the DecisionId primary key: {plan}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Review_pages_refuse_limits_outside_the_closed_bound(int limit)
    {
        AssertInvalid(new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, limit, null).Validate());

        AssertInvalid(new LexiconReviewListRequest(GlobalLexiconScope(), limit, null).Validate());

        AssertInvalid(new CovenantReviewListRequest(
            CovenantScope.Global,
            CampaignId: null,
            CovenantLane.Confirmed,
            limit,
            Cursor: null).Validate());
    }

    [Fact]
    public void Review_pages_accept_the_fifty_item_bound()
    {
        Assert.True(new SagaReviewListRequest(
            SagaMemoryScopeKind.Campaign,
            Campaign,
            MemoryReviewLimits.MaxPageSize,
            Cursor: null).Validate().IsSuccess);

        Assert.True(new LexiconReviewListRequest(
            new LexiconCurationScope(LexiconScopeKind.Campaign, Campaign),
            MemoryReviewLimits.MaxPageSize,
            Cursor: null).Validate().IsSuccess);

        Assert.True(new CovenantReviewListRequest(
            CovenantScope.Campaign,
            Campaign,
            CovenantLane.Proposed,
            MemoryReviewLimits.MaxPageSize,
            Cursor: null).Validate().IsSuccess);
    }

    [Fact]
    public void Review_pages_require_one_exact_store_scope()
    {
        AssertInvalid(new SagaReviewListRequest(
            SagaMemoryScopeKind.Global,
            Campaign,
            10,
            Cursor: null).Validate());

        AssertInvalid(new SagaReviewListRequest(
            SagaMemoryScopeKind.Campaign,
            CampaignId: null,
            10,
            Cursor: null).Validate());

        AssertInvalid(new CovenantReviewListRequest(
            CovenantScope.Global,
            Campaign,
            CovenantLane.Confirmed,
            10,
            Cursor: null).Validate());

        AssertInvalid(new CovenantReviewListRequest(
            CovenantScope.Campaign,
            CampaignId: null,
            CovenantLane.Confirmed,
            10,
            Cursor: null).Validate());
    }

    [Fact]
    public void Bulk_requests_are_single_action_nonempty_and_bounded()
    {
        SagaReviewBulkPrepareRequest empty = new(
            RequestId,
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            []);

        SagaReviewDecision[] oversized = Enumerable
            .Range(0, MemoryReviewLimits.MaxBulkOperations + 1)
            .Select(index => new SagaReviewDecision($"observation-{index}", ReplacementContent: null))
            .ToArray();

        AssertInvalid(empty.Validate());

        AssertInvalid((empty with { Decisions = oversized }).Validate());

        AssertInvalid((empty with
        {
            Decisions = [new SagaReviewDecision("observation", ReplacementContent: null)],
            Action = default,
        }).Validate());
    }

    [Fact]
    public void Confirm_is_acknowledgement_only_and_rejects_replacement_content()
    {
        SagaReviewBulkPrepareRequest request = new(
            RequestId,
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision("observation", "replacement")]);

        AssertInvalid(request.Validate());
    }

    [Fact]
    public void Correction_requires_replacement_content_and_other_actions_forbid_it()
    {
        SagaReviewBulkPrepareRequest baseline = new(
            RequestId,
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Correct,
            [new SagaReviewDecision("observation", ReplacementContent: null)]);

        AssertInvalid(baseline.Validate());

        Assert.True((baseline with
        {
            Decisions = [new SagaReviewDecision("observation", "corrected")],
        }).Validate().IsSuccess);

        AssertInvalid((baseline with
        {
            Action = MemoryReviewAction.Retire,
            Decisions = [new SagaReviewDecision("observation", "corrected")],
        }).Validate());
    }

    [Fact]
    public void Duplicate_observations_are_refused_before_any_store_is_called()
    {
        LexiconReviewBulkPrepareRequest request = new(
            RequestId,
            GlobalLexiconScope(),
            MemoryReviewAction.Pin,
            [
                new LexiconReviewDecision("same", ReplacementContent: null),
                new LexiconReviewDecision("same", ReplacementContent: null),
            ]);

        AssertInvalid(request.Validate());
    }

    [Fact]
    public void Apply_repeats_the_exact_prepared_request_and_requires_its_token()
    {
        CovenantReviewBulkPrepareRequest prepared = new(
            RequestId,
            CovenantScope.Global,
            CampaignId: null,
            CovenantLane.Confirmed,
            MemoryReviewAction.Unpin,
            [new CovenantReviewDecision("observation", ReplacementContent: null)]);

        AssertInvalid(new CovenantReviewBulkApplyRequest(prepared, PreparedPlanToken: " ").Validate());

        Assert.True(new CovenantReviewBulkApplyRequest(prepared, PreparedPlanToken: "prepared-token").Validate().IsSuccess);
    }

    [Fact]
    public void Bounded_review_items_use_current_snapshots_without_unbounded_histories()
    {
        Assert.Equal(
            typeof(SagaReviewCurrentDto),
            typeof(SagaReviewItemDto).GetProperty(nameof(SagaReviewItemDto.Current))!.PropertyType);

        Assert.Equal(
            typeof(LexiconReviewCurrentDto),
            typeof(LexiconReviewItemDto).GetProperty(nameof(LexiconReviewItemDto.Current))!.PropertyType);

        Assert.Null(typeof(SagaReviewCurrentDto).GetProperty("History"));
        Assert.Null(typeof(LexiconReviewCurrentDto).GetProperty("AnnalHistory"));
        Assert.Null(typeof(LexiconReviewCurrentDto).GetProperty("HistoricalFactProvenance"));
    }

    private static LexiconCurationScope GlobalLexiconScope() =>
        new(LexiconScopeKind.Global, CampaignId: null);

    private static void AssertInvalid(Result result) => Assert.True(result.IsFailure);
}
