using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class ApprenticeRepository : IApprenticeRepository
{
    private const int DefaultListLimit = 100;

    private readonly ArcanumDbContext _db;

    private readonly ILogger<ApprenticeRepository> _logger;

    public ApprenticeRepository(ArcanumDbContext db, ILogger<ApprenticeRepository> logger)
    {
        _db = db;

        _logger = logger;
    }

    public async Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Apprentice? apprentice = await ReadSingleAsync(
            $"SELECT {GrimoireEntitySql.ApprenticeColumns} FROM \"Apprentices\" WHERE \"Id\" = $id LIMIT 1;",
            command => GrimoireEntitySql.AddParameter(command, "$id", GrimoireEntitySql.Format(id)),
            cancellationToken).ConfigureAwait(false);

        if (apprentice is not null)
        {
            apprentice.ParentApprenticeId = DeserializeCheckpoint(apprentice.CheckpointData)?.ParentApprenticeId;
        }

        return apprentice;
    }

    public async Task<ListPageResult<Apprentice>> ListAsync(
        Guid? campaignId,
        string? status,
        int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        int pageSize = ArcanumSettingClamps.ListQueryLimit(limit ?? DefaultListLimit);

        string? statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Trim();

        // DateTimeOffset comparison and ORDER BY cannot be translated by EF Core's SQLite
        // provider (see EntryTemporalQueries and PromptRepository.ListAsync). Materialize the
        // server-side-filtered set, then apply the temporal cursor and ordering client-side.
        // Apprentice tables are workspace-scoped and small, so this is not a performance concern.
        List<Apprentice> matched;

        if (campaignId is { } campaignFilter && statusFilter is not null)
        {
            matched = await ReadManyAsync(
                $"""
                SELECT {GrimoireEntitySql.ApprenticeColumns}
                FROM "Apprentices"
                WHERE "CampaignId" = $campaignId AND "Status" = $status;
                """,
                command =>
                {
                    GrimoireEntitySql.AddParameter(
                        command,
                        "$campaignId",
                        GrimoireEntitySql.Format(campaignFilter));
                    GrimoireEntitySql.AddParameter(command, "$status", statusFilter);
                },
                cancellationToken).ConfigureAwait(false);
        }
        else if (campaignId is { } campaignOnly)
        {
            matched = await ReadManyAsync(
                $"""
                SELECT {GrimoireEntitySql.ApprenticeColumns}
                FROM "Apprentices"
                WHERE "CampaignId" = $campaignId;
                """,
                command => GrimoireEntitySql.AddParameter(
                    command,
                    "$campaignId",
                    GrimoireEntitySql.Format(campaignOnly)),
                cancellationToken).ConfigureAwait(false);
        }
        else if (statusFilter is not null)
        {
            matched = await ReadManyAsync(
                $"""
                SELECT {GrimoireEntitySql.ApprenticeColumns}
                FROM "Apprentices"
                WHERE "Status" = $status;
                """,
                command => GrimoireEntitySql.AddParameter(command, "$status", statusFilter),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            matched = await ReadManyAsync(
                $"SELECT {GrimoireEntitySql.ApprenticeColumns} FROM \"Apprentices\";",
                static _ => { },
                cancellationToken).ConfigureAwait(false);
        }

        if (beforeUpdatedAt is DateTimeOffset beforeCutoff)
        {
            matched = matched.Where(a => a.UpdatedAt < beforeCutoff).ToList();
        }

        // "Id" is the identity tie-breaker. Without it the sort is undefined among Apprentices sharing
        // an "UpdatedAt" — a Conclave fan-out creates a batch of children within one clock tick — so a
        // tie straddling a page boundary would be ordered differently on each query and the keyset
        // cursor below could not reason about it at all.
        List<Apprentice> ordered = matched
            .OrderByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.Id)
            .Take(pageSize + 1)
            .ToList();

        bool hasMore = ordered.Count > pageSize;

        List<Apprentice> page = ordered;

        if (hasMore)
        {
            // The cursor handed back is a bare timestamp consumed as a strict "UpdatedAt" < @before, so
            // any Apprentice sharing the boundary timestamp with the last row of this page would be
            // excluded from the next page and become permanently unreachable. Rather than widen the wire
            // contract, the page is cut at the tie boundary: every row sharing the first excluded row's
            // timestamp is deferred to the next page, which the strict predicate then admits in full.
            // This mirrors SessionRepository.ListAsync, which carries the same cursor contract.
            DateTimeOffset boundary = ordered[pageSize].UpdatedAt;

            List<Apprentice> kept = ordered
                .Take(pageSize)
                .Where(a => a.UpdatedAt != boundary)
                .ToList();

            if (kept.Count > 0)
            {
                page = kept;
            }
            else
            {
                // Degenerate case: the whole page is one timestamp, so cutting at the tie boundary would
                // return nothing and leave the cursor exactly where it started. Return the complete tie
                // group instead — the page exceeds the requested limit, but it is whole and the cursor
                // advances past it. The set is already materialized, so no second query is needed.
                page = matched
                    .Where(a => a.UpdatedAt == boundary)
                    .OrderByDescending(a => a.Id)
                    .ToList();

                hasMore = matched.Exists(a => a.UpdatedAt < boundary);
            }
        }

        DateTimeOffset? nextBefore = hasMore && page.Count > 0 ? page[^1].UpdatedAt : null;

        return new ListPageResult<Apprentice>(page.ToArray(), hasMore, NextBeforeUpdatedAt: nextBefore);
    }

    public async Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
    {
        _db.Apprentices.Add(apprentice);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        return apprentice;
    }

    public async Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
    {
        apprentice.UpdatedAt = DateTimeOffset.UtcNow;

        EntityEntry? tracked = _db.ChangeTracker
            .Entries()
            .FirstOrDefault(e => e.Entity is Apprentice a && a.Id == apprentice.Id);

        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        _db.Apprentices.Update(apprentice);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        return apprentice;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            "DELETE FROM \"Apprentices\" WHERE \"Id\" = $id;",
            cancellationToken).ConfigureAwait(false);
        GrimoireEntitySql.AddParameter(command, "$id", GrimoireEntitySql.Format(id));

        int deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return deleted > 0;
    }

    public async Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default)
    {
        string running = ApprenticeStatus.Running.ToString();

        string planning = ApprenticeStatus.Planning.ToString();

        string idle = ApprenticeStatus.Idle.ToString();

        string emptyPlan = SerializePlan([]);

        List<Apprentice> candidates = await ReadManyAsync(
            $"""
            SELECT {GrimoireEntitySql.ApprenticeColumns}
            FROM "Apprentices"
            WHERE "Status" = $running
               OR (
                    "Status" = $planning
                    AND (TRIM("Plan") = '' OR "Plan" = $emptyPlan))
               OR (
                    "Status" = $idle
                    AND COALESCE(
                        json_extract(
                            CASE
                                WHEN json_valid("CheckpointData") = 1 THEN "CheckpointData"
                                ELSE NULL
                            END,
                            '$.launchRequested'),
                        0) = 1);
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$running", running);
                GrimoireEntitySql.AddParameter(command, "$planning", planning);
                GrimoireEntitySql.AddParameter(command, "$idle", idle);
                GrimoireEntitySql.AddParameter(command, "$emptyPlan", emptyPlan);
            },
            cancellationToken).ConfigureAwait(false);

        return candidates;
    }

    public async Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default)
    {
        string planning = ApprenticeStatus.Planning.ToString();

        string emptyPlan = SerializePlan([]);

        return await ReadManyAsync(
            $"""
            SELECT {GrimoireEntitySql.ApprenticeColumns}
            FROM "Apprentices"
            WHERE "Status" = $planning
              AND "Plan" <> $emptyPlan
              AND "Plan" <> '';
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$planning", planning);
                GrimoireEntitySql.AddParameter(command, "$emptyPlan", emptyPlan);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Apprentice?> ReadSingleAsync(
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
            ? GrimoireEntitySql.ReadApprentice(reader)
            : null;
    }

    private async Task<List<Apprentice>> ReadManyAsync(
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
        List<Apprentice> apprentices = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            apprentices.Add(GrimoireEntitySql.ReadApprentice(reader));
        }

        return apprentices;
    }

    public static List<PlanStep> DeserializePlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, ArcanumCoreJsonContext.Default.ListPlanStep) ?? [];
    }

    public static string SerializePlan(IReadOnlyList<PlanStep> plan) =>
        JsonSerializer.Serialize(plan.ToList(), ArcanumCoreJsonContext.Default.ListPlanStep);

    public static ApprenticeCheckpoint? DeserializeCheckpoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize(json, ArcanumCoreJsonContext.Default.ApprenticeCheckpoint);
    }

    public static string SerializeCheckpoint(ApprenticeCheckpoint checkpoint) =>
        JsonSerializer.Serialize(checkpoint, ArcanumCoreJsonContext.Default.ApprenticeCheckpoint);
}
