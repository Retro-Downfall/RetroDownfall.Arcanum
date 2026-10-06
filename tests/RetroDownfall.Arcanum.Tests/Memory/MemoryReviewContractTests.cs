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
    /// (<c>corrected</c> beside <c>AlreadyRetired</c>). The vocabulary every store writes from is one
    /// closed, PascalCase set, and receipts persisted with an earlier build's lowercase spelling still read
    /// back as that set. This is the helper's contract; that the three stores write only from it is
    /// <see cref="Every_store_reports_outcomes_from_one_closed_vocabulary"/>, and the behaviour store by
    /// store is pinned where each store is driven (<c>MemoryCrossStoreIsolationTests</c> for applied
    /// outcomes, and the no-op tests of each review service for the per-action no-op spellings below).
    /// </summary>
    [Fact]
    public void The_outcome_helpers_define_one_closed_pascal_case_vocabulary_and_read_legacy_spellings()
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
    /// R-172: every store reports its per-item outcomes from the one closed vocabulary, which is what the
    /// register row names.
    /// </summary>
    /// <remarks>
    /// A store can only drift from the vocabulary in two ways, and this closes both over the three real
    /// stores. What the helper hands a store is closed: every action's applied outcome and every no-op
    /// action's outcome is a member of the set. And a store takes its outcomes from the helper and nowhere
    /// else: each Infrastructure file that names an item result is one of the three review services, each
    /// reaches <c>MemoryReviewOutcomes</c>, and none spells an outcome as a literal, so a word outside the
    /// set cannot reach a client or a receipt. The only outcome-shaped literals they hold are two readers of
    /// a spelling an earlier build persisted, which a replay must recognise and never writes. The behaviour
    /// store by store is pinned where each store is driven: the applied outcome of every action through the
    /// real routes in <c>MemoryCrossStoreIsolationTests</c>, and the no-op outcomes in each review
    /// service's own tests.
    /// </remarks>
    [Fact]
    public void Every_store_reports_outcomes_from_one_closed_vocabulary()
    {
        foreach (MemoryReviewAction action in Enum.GetValues<MemoryReviewAction>())
        {
            Assert.Contains(MemoryReviewOutcomes.Applied(action), MemoryReviewOutcomes.All);

            if (action != MemoryReviewAction.Confirm)
            {
                Assert.Contains(MemoryReviewOutcomes.NoOp(action), MemoryReviewOutcomes.All);
            }
        }

        // Every spelling a store could put in front of a client or into a receipt: the closed set in any
        // casing, and the three words an earlier build persisted that its casing alone does not cover.
        HashSet<string> spellings = new(MemoryReviewOutcomes.All, StringComparer.OrdinalIgnoreCase)
        {
            "NoChange",
            "acknowledged",
            "auto-acknowledged",
        };

        // The literals a store may hold, each a reader of what an earlier build wrote and never a writer.
        Dictionary<string, string[]> readers = new(StringComparer.Ordinal)
        {
            // A replay reads the word an earlier Covenant persisted for every no-op and reports the per-action one.
            ["Covenant/CovenantMemoryReviewService.cs"] = ["NoChange"],

            // A replay of an earlier receipt reproduces the lowercase acknowledgement its digest was sealed over.
            ["Lexicon/LexiconService.MemoryReview.cs"] = ["auto-acknowledged"],

            ["Memory/SagaMemoryReviewService.cs"] = [],
        };

        string infrastructure = Path.Combine(
            RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure");

        string[] builders =
        [
            .. Directory
                .EnumerateFiles(infrastructure, "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(infrastructure, path).Replace('\\', '/'))
                .Where(static relative => !relative.StartsWith("obj/", StringComparison.Ordinal)
                    && !relative.StartsWith("bin/", StringComparison.Ordinal))
                .Where(relative => File
                    .ReadAllText(Path.Combine(infrastructure, relative))
                    .Contains(nameof(MemoryReviewBulkItemResultDto), StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        // A fourth Infrastructure file that names item results has to be added here, with its literals, to
        // be covered.
        string[] stores = [.. readers.Keys.Order(StringComparer.Ordinal)];

        Assert.Equal(stores, builders);

        foreach ((string relative, string[] allowed) in readers)
        {
            Microsoft.CodeAnalysis.SyntaxNode root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree
                .ParseText(File.ReadAllText(Path.Combine(infrastructure, relative)))
                .GetRoot();

            Assert.True(
                root
                    .DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>()
                    .Any(static name => name.Identifier.ValueText == "MemoryReviewOutcomes"),
                $"{relative} does not take its outcomes from MemoryReviewOutcomes.");

            Microsoft.CodeAnalysis.SyntaxToken[] literals =
            [
                .. root
                    .DescendantTokens()
                    .Where(static token => token.RawKind is (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralToken
                        or (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.InterpolatedStringTextToken),
            ];

            string[] stray =
            [
                .. literals
                    .Where(token => spellings.Contains(token.ValueText)
                        && !allowed.Contains(token.ValueText, StringComparer.Ordinal))
                    .Select(token => $"{relative}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1} \"{token.ValueText}\""),
            ];

            Assert.True(
                stray.Length == 0,
                "A review store spells an outcome as a literal instead of taking it from MemoryReviewOutcomes: "
                    + string.Join(", ", stray));

            // An allowance for a reader that no longer exists would excuse the next literal that looks like it.
            Assert.All(
                allowed,
                reader => Assert.Contains(literals, token => token.ValueText == reader));
        }
    }

    /// <summary>
    /// R-172: a no-op used to be reported differently by each store (Covenant answered one word for every
    /// no-op while Saga and Lexicon named the state the memory was already in), so a client had to know
    /// which store answered. There is now one spelling per action, drawn from the closed set, and none of
    /// them is the word an earlier Covenant persisted.
    /// </summary>
    [Fact]
    public void Every_no_op_action_has_one_distinct_outcome_in_the_closed_set()
    {
        MemoryReviewAction[] noOpActions =
        [
            MemoryReviewAction.Correct,
            MemoryReviewAction.Retire,
            MemoryReviewAction.Pin,
            MemoryReviewAction.Unpin,
        ];

        string[] outcomes = [.. noOpActions.Select(MemoryReviewOutcomes.NoOp)];

        Assert.All(outcomes, static outcome => Assert.True(MemoryReviewOutcomes.IsKnown(outcome), outcome));

        Assert.Equal(outcomes.Length, outcomes.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(
            ["Unchanged", "AlreadyRetired", "AlreadyPinned", "NotPinned"],
            outcomes);

        // A Confirm is always recorded, so it has no no-op outcome to report.
        _ = Assert.Throws<ArgumentOutOfRangeException>(static () => MemoryReviewOutcomes.NoOp(MemoryReviewAction.Confirm));

        // The word an earlier Covenant persisted for every no-op is not a member any store writes now.
        Assert.False(MemoryReviewOutcomes.IsKnown("NoChange"));
    }

    /// <summary>
    /// R-172: API §8.34 says the closed set is enumerated there, so a member added to the code without the
    /// section naming it (or the reverse) is a contract a client cannot read.
    /// </summary>
    [Fact]
    public void The_api_reference_enumerates_every_outcome_of_the_closed_set()
    {
        string api = File.ReadAllText(
            Path.Combine(RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.API.md"));

        string paragraph = Assert.Single(
            api.Split('\n'),
            static line => line.StartsWith("**Per-item outcomes are one closed set.**", StringComparison.Ordinal));

        Assert.All(
            MemoryReviewOutcomes.All,
            outcome => Assert.Contains($"`{outcome}`", paragraph, StringComparison.Ordinal));
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
