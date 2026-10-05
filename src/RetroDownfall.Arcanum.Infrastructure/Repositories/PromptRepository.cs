using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

    private const int SqliteConstraintErrorCode = 19;

    private const int SqliteConstraintUniqueExtendedCode = 2067;

    private const int SqliteConstraintForeignKeyExtendedCode = 787;

    private readonly ArcanumDbContext _db;

    private readonly ILogger<PromptRepository> _logger;

    internal Func<int, Exception, CancellationToken, ValueTask>? RetryingForTesting { get; set; }

    internal Func<CancellationToken, Task>? AfterReplaceDeleteForTesting { get; set; }

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

        // Newest first; "Id" makes the order total for versions written in the same clock tick.
        return await ReadManyAsync(
            $"""
            SELECT {GrimoireEntitySql.PromptColumns}
            FROM "Prompts"
            WHERE "Name" = $name
              AND
              (
                  "CampaignId" = $campaignId
                  OR ("CampaignId" IS NULL AND $campaignId IS NULL)
              )
            ORDER BY "UpdatedAt" DESC, "Id" DESC;
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
    }

    public async Task<ListPageResult<Prompt>> ListAsync(
        Guid? campaignId,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        int pageSize = ArcanumSettingClamps.ListQueryLimit(limit ?? DefaultListLimit);

        int skip = Math.Max(0, offset);

        // The order and the page bound run in SQL, so a prompt outside the page (and its Template) is never
        // read. "UpdatedAt" is fixed-width UTC text, so ordinal TEXT order is chronological, and "Id"
        // makes the order total for versions written in the same clock tick. One extra row says whether
        // another page exists.
        List<Prompt> rows = await ReadManyAsync(
            $"""
            SELECT {GrimoireEntitySql.PromptColumns}
            FROM "Prompts"
            WHERE "CampaignId" = $campaignId
               OR ("CampaignId" IS NULL AND $campaignId IS NULL)
            ORDER BY "Name", "UpdatedAt" DESC, "Id" DESC
            LIMIT $take OFFSET $skip;
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(
                    command,
                    "$campaignId",
                    campaignId is { } id ? GrimoireEntitySql.Format(id) : null);
                GrimoireEntitySql.AddParameter(command, "$take", pageSize + 1);
                GrimoireEntitySql.AddParameter(command, "$skip", skip);
            },
            cancellationToken).ConfigureAwait(false);

        bool hasMore = rows.Count > pageSize;

        Prompt[] page = hasMore
            ? [.. rows.Take(pageSize)]
            : [.. rows];

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

    public async Task<Result<int>> ReplaceCampaignPromptsAsync(
        Guid campaignId,
        IReadOnlyList<Prompt> prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompts);

        cancellationToken.ThrowIfCancellationRequested();

        await using IDbContextTransaction transaction =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using (SqliteCommand delete = await GrimoireSqlCommandFactory.CreateAsync(
                _db,
                "DELETE FROM \"Prompts\" WHERE \"CampaignId\" = $campaignId;",
                cancellationToken).ConfigureAwait(false))
            {
                GrimoireEntitySql.AddParameter(delete, "$campaignId", GrimoireEntitySql.Format(campaignId));
                _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The first delete has run inside the transaction. Stopping now would still roll back cleanly,
            // but the caller's token only says the caller went away, so the adds and the commit finish on
            // None and the swap is never abandoned midway.
            if (AfterReplaceDeleteForTesting is { } afterDelete)
            {
                await afterDelete(cancellationToken).ConfigureAwait(false);
            }

            foreach (Prompt prompt in prompts)
            {
                prompt.CampaignId = campaignId;

                _db.Prompts.Add(prompt);
            }

            _ = await EfSaveChangesRetry
                .ExecuteAsync(_db, CancellationToken.None, RetryingForTesting)
                .ConfigureAwait(false);

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);

            return Result<int>.Success(prompts.Count);
        }
        catch (Exception exception)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);

            DetachAll(prompts);

            if (ClassifyConstraintViolation(exception) is { } error)
            {
                return Result<int>.Failure(error);
            }

            throw;
        }
    }

    private void DetachAll(IReadOnlyList<Prompt> prompts)
    {
        foreach (Prompt prompt in prompts)
        {
            if (_db.Entry(prompt).State != EntityState.Detached)
            {
                _db.Entry(prompt).State = EntityState.Detached;
            }
        }
    }

    private static async Task TryRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackFailure) when (rollbackFailure is InvalidOperationException or DbUpdateException or SqliteException)
        {
            // The connection is unusable; the original failure is the one worth surfacing and SQLite
            // discards an uncommitted transaction when the connection closes.
        }
    }

    /// <summary>
    /// Maps a SQLite constraint failure raised by a prompt write to the domain error the endpoint's
    /// pre-check would have produced had it won the race, or null for any other failure.
    /// </summary>
    /// <remarks>
    /// Only the two outcomes a caller can act on are mapped: the (name, version, campaign) unique index and
    /// the Campaign foreign key. A primary-key or other constraint is a bug and keeps surfacing as one.
    /// </remarks>
    internal static Error? ClassifyConstraintViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode } sqlite)
            {
                return sqlite.SqliteExtendedErrorCode switch
                {
                    SqliteConstraintUniqueExtendedCode => new Error(
                        ErrorCodes.Prompt.DuplicateVersion,
                        "A prompt with this name and version already exists in the target scope."),
                    SqliteConstraintForeignKeyExtendedCode => new Error(
                        ErrorCodes.Campaign.NotFound,
                        "No campaign exists with that identifier."),
                    _ => null,
                };
            }
        }

        return null;
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
