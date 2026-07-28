using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class EntriesFtsIntegrationTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public EntriesFtsIntegrationTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task Ef_insert_populates_Entries_fts_and_SearchArchivesAsync_finds_it()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            Title = "FTS probe",
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.Entries.Add(new Entry
        {
            Id = entryId,
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = "moonstone archive beneath observatory",
            ModelUsed = "test-model",
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(CancellationToken.None);

        int ftsCount = await CountFtsRowsMatchingAsync("moonstone", CancellationToken.None);

        Assert.True(ftsCount >= 1);

        GrimoireRepository repository = new(
            _db,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

        string results = await repository.SearchArchivesAsync("moonstone", maxResults: 10, CancellationToken.None);

        Assert.Contains("moonstone", results, StringComparison.OrdinalIgnoreCase);

    }

    [SkippableFact]
    public async Task Ef_insert_populates_Entries_fts_and_SessionRepository_QueryAsync_search_finds_session()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        Session session = await repository.CreateAsync(campaignId: null, title: "Hidden title", CancellationToken.None);

        _ = await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "northern ward sigil cobalt",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        HashSet<Guid> ftsSessionIds = await QueryFtsSessionIdsAsync("cobalt", CancellationToken.None);

        Assert.Contains(session.Id, ftsSessionIds);

    }

    private async Task<HashSet<Guid>> QueryFtsSessionIdsAsync(string term, CancellationToken cancellationToken)
    {

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        await using System.Data.Common.DbCommand cmd = connection.CreateCommand();

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {

            await cmd.Connection.OpenAsync(cancellationToken);

        }

        cmd.CommandText =
            """
            SELECT DISTINCT c."SessionId"
            FROM "Entries_fts"
            INNER JOIN "Entries" AS c ON c."Id" = "Entries_fts"."Id"
            WHERE "Entries_fts" MATCH @query
            LIMIT 500;
            """;

        System.Data.Common.DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = "@query";

        parameter.Value = term;

        cmd.Parameters.Add(parameter);

        HashSet<Guid> sessionIds = [];

        await using System.Data.Common.DbDataReader reader =
            await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {

            _ = sessionIds.Add(reader.GetGuid(0));

        }

        return sessionIds;

    }

    private async Task<int> CountFtsRowsMatchingAsync(string term, CancellationToken cancellationToken)
    {

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        await using System.Data.Common.DbCommand cmd = connection.CreateCommand();

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {

            await cmd.Connection.OpenAsync(cancellationToken);

        }

        cmd.CommandText = """
            SELECT COUNT(*)
            FROM "Entries_fts"
            WHERE "Entries_fts" MATCH @query;
            """;

        System.Data.Common.DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = "@query";

        parameter.Value = term;

        cmd.Parameters.Add(parameter);

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);

    }

}
