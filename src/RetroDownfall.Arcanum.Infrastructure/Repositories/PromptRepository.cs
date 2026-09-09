using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class PromptRepository : IPromptRepository
{
    private const int DefaultListLimit = 100;

    private readonly ArcanumDbContext _db;

    private readonly ILogger<PromptRepository> _logger;

    internal Func<int, Exception, CancellationToken, ValueTask>? RetryingForTesting { get; set; }

    public PromptRepository(ArcanumDbContext db, ILogger<PromptRepository> logger)
    {
        _db = db;

        _logger = logger;
    }

    public async Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await ReadSingleAsync(
            $"SELECT {GrimoireEntitySql.PromptColumns} FROM \"Prompts\" WHERE \"Id\" = $id LIMIT 1;",
            command => GrimoireEntitySql.AddParameter(command, "$id", GrimoireEntitySql.Format(id)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Prompt?> GetByNameAndVersionAsync(
        string name,
        string version,
        Guid? campaignId,
        CancellationToken cancellationToken = default)
    {
        string trimmedName = name.Trim();

        string trimmedVersion = version.Trim();

        return await ReadSingleAsync(
            $"""
            SELECT {GrimoireEntitySql.PromptColumns}
            FROM "Prompts"
            WHERE "Name" = $name
              AND "Version" = $version
              AND
              (
                  "CampaignId" = $campaignId
                  OR ("CampaignId" IS NULL AND $campaignId IS NULL)
              )
            LIMIT 1;
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$name", trimmedName);
                GrimoireEntitySql.AddParameter(command, "$version", trimmedVersion);
                GrimoireEntitySql.AddParameter(
                    command,
                    "$campaignId",
                    campaignId is { } id ? GrimoireEntitySql.Format(id) : null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Prompt>> ListVersionsAsync(
        string name,
        Guid? campaignId,
        CancellationToken cancellationToken = default)
    {
        string trimmedName = name.Trim();

        // EF Core's SQLite provider cannot translate DateTimeOffset in ORDER BY (see
        // PromptRepository.ListAsync for the same constraint). Materialize the name+campaign-
        // scoped rows (small set) and sort client-side.
        List<Prompt> matched = await ReadManyAsync(
            $"""
            SELECT {GrimoireEntitySql.PromptColumns}
            FROM "Prompts"
            WHERE "Name" = $name
              AND
              (
                  "CampaignId" = $campaignId
                  OR ("CampaignId" IS NULL AND $campaignId IS NULL)
              );
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$name", trimmedName);
                GrimoireEntitySql.AddParameter(
                    command,
                    "$campaignId",
                    campaignId is { } id ? GrimoireEntitySql.Format(id) : null);
            },
            cancellationToken).ConfigureAwait(false);

        return matched
            .OrderByDescending(p => p.UpdatedAt)
            .ToArray();
    }

    public async Task<ListPageResult<Prompt>> ListAsync(
        Guid? campaignId,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        int pageSize = ArcanumSettingClamps.ListQueryLimit(limit ?? DefaultListLimit);

        int skip = Math.Max(0, offset);

        // W: composite server-side ORDER BY (Name, UpdatedAt) combined with Skip/Take triggers
        // "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses" from the
        // EF Core Sqlite provider's paging translator. Sort and page client-side instead; prompt tables
        // are workspace-scoped and small, so this is not a performance concern.
        List<Prompt> matched = await ReadManyAsync(
            $"""
            SELECT {GrimoireEntitySql.PromptColumns}
            FROM "Prompts"
            WHERE "CampaignId" = $campaignId
               OR ("CampaignId" IS NULL AND $campaignId IS NULL);
            """,
            command => GrimoireEntitySql.AddParameter(
                command,
                "$campaignId",
                campaignId is { } id ? GrimoireEntitySql.Format(id) : null),
            cancellationToken).ConfigureAwait(false);

        Prompt[] ordered = matched
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenByDescending(p => p.UpdatedAt)
            .ToArray();

        Prompt[] page = ordered.Skip(skip).Take(pageSize + 1).ToArray();

        bool hasMore = page.Length > pageSize;

        if (hasMore)
        {
            page = page.Take(pageSize).ToArray();
        }

        int? nextOffset = hasMore ? skip + pageSize : null;

        return new ListPageResult<Prompt>(page, hasMore, nextOffset);
    }

    public async Task<Prompt> AddAsync(Prompt prompt, CancellationToken cancellationToken = default)
    {
        _db.Prompts.Add(prompt);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(_db, cancellationToken, RetryingForTesting)
            .ConfigureAwait(false);

        return prompt;
    }

    public async Task<Prompt> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default)
    {
        _db.Prompts.Update(prompt);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(_db, cancellationToken, RetryingForTesting)
            .ConfigureAwait(false);

        return prompt;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            "DELETE FROM \"Prompts\" WHERE \"Id\" = $id;",
            cancellationToken).ConfigureAwait(false);
        GrimoireEntitySql.AddParameter(command, "$id", GrimoireEntitySql.Format(id));

        int deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return deleted > 0;
    }

    private async Task<Prompt?> ReadSingleAsync(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            commandText,
            cancellationToken).ConfigureAwait(false);
        bind(command);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? GrimoireEntitySql.ReadPrompt(reader)
            : null;
    }

    private async Task<List<Prompt>> ReadManyAsync(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            commandText,
            cancellationToken).ConfigureAwait(false);
        bind(command);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<Prompt> prompts = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            prompts.Add(GrimoireEntitySql.ReadPrompt(reader));
        }

        return prompts;
    }

    public static string[] DeserializeTags(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, ArcanumCoreJsonContext.Default.StringArray) ?? [];
    }

    public static string SerializeTags(string[] tags) =>
        JsonSerializer.Serialize(tags, ArcanumCoreJsonContext.Default.StringArray);
}
