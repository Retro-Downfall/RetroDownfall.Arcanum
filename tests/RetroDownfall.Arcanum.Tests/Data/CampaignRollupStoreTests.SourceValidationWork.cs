using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class CampaignRollupStoreTests
{
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 32)]
    [InlineData(false, true, 32)]
    [InlineData(true, false, 32)]
    [InlineData(true, true, 32)]
    [InlineData(false, false, 129)]
    [InlineData(false, true, 129)]
    [InlineData(true, false, 129)]
    [InlineData(true, true, 129)]
    public async Task Native_campaign_source_validation_queries_are_bounded_by_pages_not_each_source(
        bool legacy, bool validate, int sources)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync(legacy);

        for (int index = 0; index < sources; index++)
        {
            Guid session = index == 0 ? SessionA : Guid.Parse($"{index:x8}-0000-4000-8000-000000000000");

            if (index > 0)
            {
                await fixture.AddSessionAsync(session, "source decision");
            }

            Result<CampaignContributionInput?> prepared = await fixture.Store.PrepareContributionAsync(
                session, null, null, CancellationToken.None);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            Result<CampaignRollupArtifact> contribution = await fixture.Store.PublishContributionAsync(
                prepared.Value!, "source decision", null, null, CancellationToken.None);

            Assert.True(contribution.IsSuccess, contribution.Error.Message);
        }

        CampaignRollupArtifact? publication = null;

        int folds = 0;

        while (true)
        {
            Result<CampaignRollupInput?> prepared = await fixture.Store.PrepareRollupAsync(
                Campaign, null, CancellationToken.None);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            if (prepared.Value is null)
            {
                break;
            }

            Result<CampaignRollupArtifact> published = await fixture.Store.PublishRollupAsync(
                prepared.Value, "Campaign summary", null, null, CancellationToken.None);

            Assert.True(published.IsSuccess, published.Error.Message);

            publication = published.Value;

            folds++;
        }

        Assert.NotNull(publication);

        Assert.Equal(sources, publication.SourceCount);

        using SqliteStatementTrace trace = new(fixture.Connection);

        if (validate)
        {
            Result result = await fixture.Store.ValidateAsync(publication, null, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);
        }
        else
        {
            Result<CampaignRollupArtifact?> result = await fixture.Store.ReadCurrentAsync(
                Campaign, null, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(publication, result.Value);
        }

        int pages = (sources + 127) / 128;

        int maximumStatements = 20 + 10 * pages + 4 * folds;

        Assert.True(trace.Count <= maximumStatements,
            $"Validation of {sources} sources used {trace.Count} statements; {pages} pages and {folds} folds permit at most {maximumStatements}. Source evidence must be read in bounded pages.");

        // Explain the actual parameter-expanded batch SQLite executed, rather than a parallel query.
        foreach (string statement in trace.Statements.Where(static sql => sql.Contains("FROM artifact_sensitivity label", StringComparison.Ordinal)
            && sql.Contains(" IN (", StringComparison.Ordinal)))
        {
            await using SqliteCommand command = fixture.Connection.CreateCommand();

            command.CommandText = "EXPLAIN QUERY PLAN " + statement;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            List<string> details = [];

            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(3));
            }

            Assert.Contains(details, static detail => detail.Contains("idx_artifact_sensitivity_artifact_norm", StringComparison.Ordinal));

            Assert.DoesNotContain(details, static detail => detail.Contains("SCAN label", StringComparison.Ordinal));
        }
    }
}
