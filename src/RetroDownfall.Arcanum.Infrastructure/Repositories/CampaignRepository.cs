using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class CampaignRepository : ICampaignRepository
{
    private const int DefaultListLimit = 100;

    private const int ImmediateTransactionTimeoutSeconds = 1;

    private const int ImmediateTransactionBusyTimeoutMilliseconds =
        ImmediateTransactionTimeoutSeconds * 1000;

    private readonly ArcanumDbContext _db;

    private readonly ILogger<CampaignRepository> _logger;

    private readonly IOptionsSnapshot<ArcanumSettings> _arcOptions;

    internal Func<CancellationToken, Task>? AfterImmediateTransactionBeganForTesting { get; set; }

    internal Func<int, Exception, CancellationToken, ValueTask>? RetryingForTesting { get; set; }

    public CampaignRepository(
        ArcanumDbContext db,
        ILogger<CampaignRepository> logger,
        IOptionsSnapshot<ArcanumSettings> arcOptions)
    {
        _db = db;

        _logger = logger;

        _arcOptions = arcOptions;
    }

    public async Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await ReadSingleAsync(
            $"SELECT {GrimoireEntitySql.CampaignColumns} FROM \"Campaigns\" WHERE \"Id\" = $id LIMIT 1;",
            command => GrimoireEntitySql.AddParameter(command, "$id", GrimoireEntitySql.Format(id)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        string normalized;

        try
        {
            normalized = Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return null;
        }

        return await ReadSingleAsync(
            $"SELECT {GrimoireEntitySql.CampaignColumns} FROM \"Campaigns\" WHERE \"Path\" = $path LIMIT 1;",
            command => GrimoireEntitySql.AddParameter(command, "$path", normalized),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string nameLower = name.Trim().ToLowerInvariant();

        return await ReadSingleAsync(
            $"SELECT {GrimoireEntitySql.CampaignColumns} FROM \"Campaigns\" WHERE \"NameLower\" = $name LIMIT 1;",
            command => GrimoireEntitySql.AddParameter(command, "$name", nameLower),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ListPageResult<Campaign>> ListAsync(
        WorkspaceType? typeFilter,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        int pageSize = ArcanumSettingClamps.ListQueryLimit(limit ?? DefaultListLimit);

        int skip = Math.Max(0, offset);

        List<Campaign> page;

        if (typeFilter is { } type)
        {
            page = await ReadManyAsync(
                $"""
                SELECT {GrimoireEntitySql.CampaignColumns}
                FROM "Campaigns"
                WHERE "Type" = $type
                ORDER BY "Name"
                LIMIT $take OFFSET $skip;
                """,
                command =>
                {
                    GrimoireEntitySql.AddParameter(command, "$type", (int)type);
                    GrimoireEntitySql.AddParameter(command, "$take", pageSize + 1);
                    GrimoireEntitySql.AddParameter(command, "$skip", skip);
                },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            page = await ReadManyAsync(
                $"""
                SELECT {GrimoireEntitySql.CampaignColumns}
                FROM "Campaigns"
                ORDER BY "Name"
                LIMIT $take OFFSET $skip;
                """,
                command =>
                {
                    GrimoireEntitySql.AddParameter(command, "$take", pageSize + 1);
                    GrimoireEntitySql.AddParameter(command, "$skip", skip);
                },
                cancellationToken).ConfigureAwait(false);
        }

        bool hasMore = page.Count > pageSize;

        if (hasMore)
        {
            page = page.Take(pageSize).ToList();
        }

        int? nextOffset = hasMore ? skip + pageSize : null;

        return new ListPageResult<Campaign>(page.ToArray(), hasMore, nextOffset);
    }

    public async Task<Result<Campaign>> AddAsync(
        Campaign campaign,
        CancellationToken cancellationToken = default)
    {
        if (_db.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("Campaign persistence requires a SQLite connection.");
        }

        bool openedHere = connection.State != ConnectionState.Open;

        int originalTimeout = connection.DefaultTimeout;

        connection.DefaultTimeout = originalTimeout <= 0
            ? ImmediateTransactionTimeoutSeconds
            : Math.Min(originalTimeout, ImmediateTransactionTimeoutSeconds);

        int? originalBusyTimeout = null;

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    if (connection.State == ConnectionState.Broken)
                    {
                        await connection.CloseAsync().ConfigureAwait(false);
                    }

                    if (connection.State != ConnectionState.Open)
                    {
                        await _db.Database
                            .OpenConnectionAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (originalBusyTimeout is null)
                    {
                        originalBusyTimeout = ReadBusyTimeout(connection, cancellationToken);

                        SetBusyTimeout(
                            connection,
                            ImmediateTransactionBusyTimeoutMilliseconds,
                            cancellationToken);
                    }

                    return await AddWithinImmediateTransactionAsync(
                        connection,
                        campaign,
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken,
                RetryingForTesting).ConfigureAwait(false);
        }
        finally
        {
            if (originalBusyTimeout is { } busyTimeout
                && connection.State == ConnectionState.Open)
            {
                SetBusyTimeout(connection, busyTimeout, CancellationToken.None);
            }

            connection.DefaultTimeout = originalTimeout;

            if (openedHere && connection.State != ConnectionState.Closed)
            {
                await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<Result<Campaign>> AddWithinImmediateTransactionAsync(
        SqliteConnection connection,
        Campaign campaign,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteTransaction sqliteTransaction =
            connection.BeginTransaction(deferred: false);

        await using IDbContextTransaction efTransaction =
            await _db.Database
                .UseTransactionAsync(sqliteTransaction, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException("EF could not attach the SQLite campaign transaction.");

        bool committed = false;

        bool commitStarted = false;

        try
        {
            if (AfterImmediateTransactionBeganForTesting is { } afterTransactionBegan)
            {
                await afterTransactionBegan(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            campaign.NameLower = campaign.Name.Trim().ToLowerInvariant();

            _db.Campaigns.Add(campaign);

            _ = await EfSaveChangesRetry
                .ExecuteOnceAsync(_db, cancellationToken)
                .ConfigureAwait(false);

            commitStarted = true;

            await efTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            committed = true;

            return Result<Campaign>.Success(campaign);
        }
        catch (Exception exception)
        {
            bool rolledBack = await TryRollbackAsync(efTransaction).ConfigureAwait(false);

            if (!rolledBack)
            {
                throw new InvalidOperationException(
                    commitStarted
                        ? "The campaign insert outcome is ambiguous because commit and rollback both failed."
                        : "The campaign insert outcome is ambiguous because the failed transaction could not be rolled back.",
                    exception);
            }

            // The unique indexes on NameLower and Path are the authority: the endpoint's pre-check is
            // check-then-act, so the loser of a concurrent registration lands here. That is an
            // ordinary domain outcome, not an infrastructure fault, and must not surface as a 500.
            if (IsUniqueConstraintViolation(exception))
            {
                if (_db.Entry(campaign).State != EntityState.Detached)
                {
                    _db.Entry(campaign).State = EntityState.Detached;
                }

                await efTransaction.DisposeAsync().ConfigureAwait(false);

                return await MapDuplicateCampaignAsync(campaign, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (!committed && _db.Entry(campaign).State != EntityState.Detached)
            {
                _db.Entry(campaign).State = EntityState.Detached;
            }
        }
    }

    public async Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default)
    {
        campaign.NameLower = campaign.Name.Trim().ToLowerInvariant();

        _db.Campaigns.Update(campaign);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        return campaign;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using SqliteCommand unbind = await GrimoireSqlCommandFactory.CreateAsync(
                _db,
                "UPDATE \"Sessions\" SET \"CampaignId\" = NULL WHERE \"CampaignId\" = $id;",
                cancellationToken).ConfigureAwait(false);
            GrimoireEntitySql.AddParameter(unbind, "$id", GrimoireEntitySql.Format(id));
            _ = await unbind.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand delete = await GrimoireSqlCommandFactory.CreateAsync(
                _db,
                "DELETE FROM \"Campaigns\" WHERE \"Id\" = $id;",
                cancellationToken).ConfigureAwait(false);
            GrimoireEntitySql.AddParameter(delete, "$id", GrimoireEntitySql.Format(id));
            int deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            return deleted > 0;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            "SELECT COUNT(*) FROM \"Campaigns\";",
            cancellationToken).ConfigureAwait(false);
        object? count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// True when <paramref name="exception"/> (or anything it wraps) is a SQLite uniqueness
    /// violation. <c>SqliteBusyRetry</c> deliberately matches only BUSY/LOCKED, so a constraint
    /// failure reaches the caller unretried.
    /// </summary>
    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Re-reads the conflicting row after a uniqueness violation and returns the domain error the
    /// endpoint's pre-check would have produced had it won the race.
    /// </summary>
    private async Task<Result<Campaign>> MapDuplicateCampaignAsync(
        Campaign campaign,
        CancellationToken cancellationToken)
    {
        string nameLower = campaign.Name.Trim().ToLowerInvariant();

        bool nameTaken = await ExistsAsync(
            "SELECT 1 FROM \"Campaigns\" WHERE \"NameLower\" = $value LIMIT 1;",
            nameLower,
            cancellationToken).ConfigureAwait(false);

        if (nameTaken)
        {
            return Result<Campaign>.Failure(
                new Error("Campaign.DuplicateName", "A campaign with this name already exists."));
        }

        bool pathTaken = await ExistsAsync(
            "SELECT 1 FROM \"Campaigns\" WHERE \"Path\" = $value LIMIT 1;",
            campaign.Path,
            cancellationToken).ConfigureAwait(false);

        if (pathTaken)
        {
            return Result<Campaign>.Failure(
                new Error("Campaign.DuplicatePath", "A campaign with this path already exists."));
        }

        return Result<Campaign>.Failure(
            new Error("Campaign.DuplicateName", "A campaign with this name or path already exists."));
    }

    private async Task<bool> ExistsAsync(
        string commandText,
        object value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            commandText,
            cancellationToken).ConfigureAwait(false);
        GrimoireEntitySql.AddParameter(command, "$value", value);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<Campaign?> ReadSingleAsync(
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
            ? GrimoireEntitySql.ReadCampaign(reader)
            : null;
    }

    private async Task<List<Campaign>> ReadManyAsync(
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
        List<Campaign> campaigns = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            campaigns.Add(GrimoireEntitySql.ReadCampaign(reader));
        }

        return campaigns;
    }

    private static async Task<bool> TryRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or ObjectDisposedException)
        {
            return false;
        }
    }

    private static int ReadBusyTimeout(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA busy_timeout;";

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetBusyTimeout(
        SqliteConnection connection,
        int milliseconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = SqlitePragmaStatementFactory.BusyTimeout(milliseconds);

        _ = command.ExecuteNonQuery();
    }

    public static CampaignSettings DeserializeSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CampaignSettings.CreateDefault();
        }

        return JsonSerializer.Deserialize(json, ArcanumCoreJsonContext.Default.CampaignSettings)
            ?? CampaignSettings.CreateDefault();
    }

    public static string SerializeSettings(CampaignSettings settings) =>
        JsonSerializer.Serialize(settings, ArcanumCoreJsonContext.Default.CampaignSettings);

    public static SanctumConfig GetSanctumConfig(Campaign campaign) =>
        DeserializeSanctumConfig(campaign.SanctumConfigJson);

    public static void SetSanctumConfig(Campaign campaign, SanctumConfig config) =>
        campaign.SanctumConfigJson = SerializeSanctumConfig(config);

    public static SanctumConfig DefaultSanctumConfig() => new();

    public static SanctumConfig DeserializeSanctumConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return DefaultSanctumConfig();
        }

        return JsonSerializer.Deserialize(json, ArcanumCoreJsonContext.Default.SanctumConfig)
            ?? DefaultSanctumConfig();
    }

    public static string SerializeSanctumConfig(SanctumConfig config) =>
        JsonSerializer.Serialize(config, ArcanumCoreJsonContext.Default.SanctumConfig);
}
