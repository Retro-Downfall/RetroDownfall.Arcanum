using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed partial class SessionRepositoryTests
{
    [SkippableFact]
    public async Task Analytics_model_counts_read_a_partial_covering_index_without_transcript_rows_or_sort_trees()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(),
            _fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db!));

        Session session = await repository.CreateAsync(null, "Analytics model index", CancellationToken.None);

        foreach (string model in new[] { "", "model-a", "MODEL-A", "model-b", "Émodel", "émodel", "model\0tail", "MODEL\0TAIL" })
        {
            Result<Entry> added = await repository.AddEntryAsync(session.Id, new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.Assistant,
                Content = new string('x', 32768),
                ModelUsed = model,
                CreatedAt = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
            }, CancellationToken.None);

            Assert.True(added.IsSuccess);
        }

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string sql;

        using (SqliteStatementTrace trace = new(connection))
        {
            SessionAnalytics analytics = await repository.GetAnalyticsAsync(CancellationToken.None);

            Assert.Equal(2, analytics.EntriesByModel["model-a"]);

            Assert.Equal(1, analytics.EntriesByModel["model-b"]);

            Assert.Equal(2, analytics.EntriesByModel["émodel"]);

            Assert.Equal(2, analytics.EntriesByModel["model\0tail"]);

            Assert.Equal(new[] { "MODEL\0TAIL", "MODEL-A", "model-b", "Émodel" }, analytics.EntriesByModel.Keys);

            Assert.False(analytics.EntriesByModel.ContainsKey(""));

            sql = Assert.Single(trace.Statements,
                static statement => statement.Contains("GROUP BY \"ModelUsed\"", StringComparison.Ordinal));
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> operations = [];

        while (await reader.ReadAsync())
        {
            operations.Add(reader.GetString(3));
        }

        Assert.Contains(operations, static operation => operation.Contains(
            "USING COVERING INDEX IX_Entries_ModelUsed", StringComparison.Ordinal));

        Assert.DoesNotContain(operations, static operation => operation.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }
}
