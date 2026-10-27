using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed partial class PromptRepositoryTests
{
    [SkippableFact]
    public async Task Summary_pages_read_only_metadata_and_keep_update_time_as_the_name_tie_breaker()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        DateTimeOffset instant = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        Prompt old = CreatePrompt(null, "same", "old", instant.AddTicks(-1));

        Prompt latest = CreatePrompt(null, "same", "latest", instant);

        foreach (Prompt prompt in new[] { old, latest })
        {
            prompt.Template = new string('x', 131072);

            prompt.ParameterSchema = "{\"description\":\"" + new string('s', 32768) + "\"}";

            prompt.DefaultParameters = "{\"large\":\"" + new string('d', 32768) + "\"}";

            Assert.True((await repository.AddAsync(prompt)).IsSuccess);
        }

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        ListPageResult<PromptSummary> page;

        using (SqliteStatementTrace trace = new(connection))
        {
            page = await repository.ListSummariesAsync(null, limit: 1);

            string query = Assert.Single(trace.Statements, static statement => statement.Contains("FROM \"Prompts\"", StringComparison.Ordinal));

            foreach (string forbidden in new[] { "Template", "ParameterSchema", "DefaultParameters", "Temperature", "MaxOutputTokens" })
            {
                Assert.DoesNotContain(forbidden, query, StringComparison.Ordinal);
            }
        }

        PromptSummary summary = Assert.Single(page.Items);

        Assert.Equal(latest.Id, summary.Id);

        Assert.Equal(instant, summary.UpdatedAt);

        Assert.Equal("latest", summary.Version);

        Assert.True(page.HasMore);

        Assert.Equal(1, page.NextOffset);

        ListPageResult<PromptSummary> last = await repository.ListSummariesAsync(null, limit: 1, offset: page.NextOffset!.Value);

        Assert.Equal(old.Id, Assert.Single(last.Items).Id);

        Assert.False(last.HasMore);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task List_scope_uses_one_ordered_index_search_instead_of_a_multi_index_union(bool campaign, bool summary)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        PromptRepository repository = new(_db, NullLogger<PromptRepository>.Instance);

        string sql;

        using (SqliteStatementTrace trace = new(connection))
        {
            if (summary)
            {
                _ = await repository.ListSummariesAsync(campaign ? Guid.NewGuid() : null, query: "literal", tag: "tag", limit: 3);
            }
            else
            {
                _ = await repository.ListAsync(campaign ? Guid.NewGuid() : null, limit: 3);
            }

            sql = Assert.Single(trace.Statements, static statement => statement.Contains("FROM \"Prompts\"", StringComparison.Ordinal));
        }

        await using SqliteCommand plan = connection.CreateCommand();

        plan.CommandText = "EXPLAIN QUERY PLAN " + sql;

        await using SqliteDataReader reader = await plan.ExecuteReaderAsync();

        List<string> operations = [];

        while (await reader.ReadAsync())
        {
            operations.Add(reader.GetString(3));
        }

        Assert.DoesNotContain(operations, static operation => operation.Contains("MULTI-INDEX OR", StringComparison.Ordinal));

        Assert.Single(operations, static operation => operation.Contains("SEARCH Prompts USING INDEX IX_Prompts_CampaignId_Name", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("tag")]
    public async Task Summary_filters_preserve_Unicode_literal_and_embedded_NUL_matching(string field)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        DateTimeOffset instant = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        Prompt match = CreatePrompt(null, "match", "1", instant);

        Prompt decoy = CreatePrompt(null, "decoy", "1", instant);

        const string text = "É\0%_needle";

        if (field == "name")
        {
            match.Name = text;

            decoy.Name = "É\0%_other";
        }
        else if (field == "description")
        {
            match.Description = text;

            decoy.Description = "É\0%_other";
        }
        else
        {
            match.Tags = PromptRepository.SerializeTags([text]);

            decoy.Tags = PromptRepository.SerializeTags(["É\0%_other"]);
        }

        Assert.True((await repository.AddAsync(match)).IsSuccess);

        Assert.True((await repository.AddAsync(decoy)).IsSuccess);

        ListPageResult<PromptSummary> page = await repository.ListSummariesAsync(null, query: " é\0%_NEEDLE ", limit: 1);

        Assert.Equal(match.Id, Assert.Single(page.Items).Id);

        Assert.False(page.HasMore);

        Assert.Empty((await repository.ListSummariesAsync(null, query: "e\0%_needle")).Items);

        if (field == "tag")
        {
            Assert.Equal(match.Id, Assert.Single((await repository.ListSummariesAsync(null, tag: "é\0%_NEEDLE")).Items).Id);

            Assert.Empty((await repository.ListSummariesAsync(null, tag: " é\0%_NEEDLE ")).Items);

            Assert.Empty((await repository.ListSummariesAsync(null, query: "needle", tag: "missing")).Items);
        }
    }

    [SkippableFact]
    public async Task Summary_order_is_global_across_pages_and_uses_date_then_identity_for_equal_names()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        DateTimeOffset instant = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        Prompt lowerName = CreatePrompt(null, "alpha", "1", instant.AddDays(1));

        Prompt upperName = CreatePrompt(null, "Zulu", "1", instant);

        Prompt old = CreatePrompt(null, "same", "old", instant.AddTicks(-1));

        Prompt newLowId = CreatePrompt(null, "same", "new-low", instant);

        Prompt newHighId = CreatePrompt(null, "same", "new-high", instant);

        newLowId.Id = Guid.Parse("00000001-0000-4000-8000-000000000001");

        newHighId.Id = Guid.Parse("00000002-0000-4000-8000-000000000001");

        foreach (Prompt prompt in new[] { lowerName, old, newLowId, upperName, newHighId })
        {
            Assert.True((await repository.AddAsync(prompt)).IsSuccess);
        }

        Guid[] expected = [upperName.Id, lowerName.Id, newHighId.Id, newLowId.Id, old.Id];

        ListPageResult<PromptSummary> all = await repository.ListSummariesAsync(null);

        Assert.Equal(expected, all.Items.Select(static prompt => prompt.Id));

        for (int offset = 0; offset < expected.Length; offset++)
        {
            ListPageResult<PromptSummary> page = await repository.ListSummariesAsync(null, limit: 1, offset: offset);

            Assert.Equal(expected[offset], Assert.Single(page.Items).Id);

            Assert.Equal(offset + 1 < expected.Length, page.HasMore);

            Assert.Equal(offset + 1 < expected.Length ? offset + 1 : (int?)null, page.NextOffset);
        }

        Assert.Equal(upperName.Id, Assert.Single((await repository.ListSummariesAsync(null, limit: 0, offset: -1)).Items).Id);
    }

    [SkippableFact]
    public async Task Summary_tag_filter_refuses_malformed_stored_JSON_instead_of_reporting_no_matches()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        Prompt broken = CreatePrompt(null, "broken", "1", DateTimeOffset.UtcNow);

        broken.Tags = "{malformed";

        Assert.True((await repository.AddAsync(broken)).IsSuccess);

        _ = await Assert.ThrowsAsync<SqliteException>(() => repository.ListSummariesAsync(null, tag: "missing"));
    }

    [SkippableFact]
    public async Task Summary_and_export_honor_pre_cancelled_calls()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListSummariesAsync(null, cancellationToken: cancellation.Token));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadForExportAsync(Guid.NewGuid(), cancellation.Token));
    }
}
