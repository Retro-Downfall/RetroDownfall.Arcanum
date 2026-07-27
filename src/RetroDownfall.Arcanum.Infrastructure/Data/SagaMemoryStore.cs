using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for Saga memories, reusing the scoped <see cref="ArcanumDbContext"/>'s
/// connection. None of <c>saga_memories</c>, <c>saga_memory_embeddings</c>,
/// <c>saga_memory_embeddings_vec</c>, or <c>saga_extraction_watermarks</c> is part of the compiled EF
/// model (they are created by <see cref="WeaveSchemaInitializer"/>, not a migration — see its remarks),
/// so all access goes through <see cref="DbCommand"/> rather than LINQ, mirroring
/// <see cref="UnseenServantWatermarkStore"/> and <see cref="SanctumBreachRepository"/>.
/// </summary>
internal sealed class SagaMemoryStore(
    ArcanumDbContext db,
    WeaveIndexAvailability availability,
    IOptionsMonitor<ArcanumSettings> options) : ISagaMemoryStore
{

    public Task InsertAsync(
        string id,
        string content,
        DateTimeOffset createdAt,
        Guid? sessionId,
        string? tags,
        string? source,
        float[] embedding,
        CancellationToken cancellationToken)
    {

        int expectedDimensions = ArcanumSettingClamps.EmbeddingsDimensions(
            options.CurrentValue.Integrations.Embeddings.Dimensions);

        if (embedding.Length != expectedDimensions)
        {

            throw new InvalidOperationException(
                $"""Saga memory embedding has {embedding.Length} dimensions but {expectedDimensions} are configured at Arcanum:Integrations:Embeddings:Dimensions. Rejecting insert to avoid corrupting the vec0 index.""");

        }

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Transaction and commands are created fresh on every invocation of this delegate: if
                // SqliteBusyRetry retries after a SQLITE_BUSY failure, the prior transaction has already
                // been rolled back/disposed by the `await using` blocks below, so the retry starts a
                // brand-new transaction rather than reusing a stale one or leaving a partial insert split
                // across saga_memories/saga_memory_embeddings/saga_memory_embeddings_vec.
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand memoryCmd = connection.CreateCommand();

                memoryCmd.Transaction = transaction;

                memoryCmd.CommandText =
                    """
                    INSERT INTO "saga_memories" ("Id", "Content", "CreatedAt", "SessionId", "Tags", "Source")
                    VALUES (@id, @content, @createdAt, @sessionId, @tags, @source)
                    """;

                AddParameter(memoryCmd, "@id", id);

                AddParameter(memoryCmd, "@content", content);

                AddParameter(memoryCmd, "@createdAt", createdAt.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(memoryCmd, "@sessionId", sessionId is null ? DBNull.Value : sessionId.Value.ToString());

                AddParameter(memoryCmd, "@tags", (object?)tags ?? DBNull.Value);

                AddParameter(memoryCmd, "@source", (object?)source ?? DBNull.Value);

                _ = await memoryCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                byte[] blob = EmbeddingBlobCodec.Encode(embedding);

                await using DbCommand embeddingCmd = connection.CreateCommand();

                embeddingCmd.Transaction = transaction;

                embeddingCmd.CommandText =
                    """
                    INSERT INTO "saga_memory_embeddings" ("MemoryId", "Embedding", "Dim")
                    VALUES (@memoryId, @embedding, @dim)
                    """;

                AddParameter(embeddingCmd, "@memoryId", id);

                AddParameter(embeddingCmd, "@embedding", blob);

                AddParameter(embeddingCmd, "@dim", embedding.Length);

                _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (availability.IsVecAvailable)
                {

                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.Transaction = transaction;

                    vecCmd.CommandText =
                        """
                        INSERT OR REPLACE INTO "saga_memory_embeddings_vec" ("MemoryId", "Embedding")
                        VALUES (@memoryId, @embedding)
                        """;

                    AddParameter(vecCmd, "@memoryId", id);

                    AddParameter(vecCmd, "@embedding", blob);

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText = """SELECT COUNT(*) FROM "saga_memories" """;

                object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                return Convert.ToInt32(result, CultureInfo.InvariantCulture);

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT COUNT(*) FROM "saga_memories" WHERE "SessionId" = @sessionId
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                return Convert.ToInt32(result, CultureInfo.InvariantCulture);

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<SagaMemoryDto[]> ListAsync(
        string? query,
        Guid? sessionId,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                StringBuilder sql = new(
                    """
                    SELECT "Id", "Content", "CreatedAt", "SessionId", "Tags", "Source"
                    FROM "saga_memories"
                    WHERE 1 = 1
                    """);

                if (!string.IsNullOrWhiteSpace(query))
                {

                    sql.Append(" AND \"Content\" LIKE @query ESCAPE '\\'");

                    AddParameter(cmd, "@query", "%" + EscapeLikePattern(query) + "%");

                }

                if (sessionId is not null)
                {

                    sql.Append(" AND \"SessionId\" = @sessionId");

                    AddParameter(cmd, "@sessionId", sessionId.Value.ToString());

                }

                sql.Append(" ORDER BY \"CreatedAt\" DESC LIMIT @limit OFFSET @offset");

                AddParameter(cmd, "@limit", limit);

                AddParameter(cmd, "@offset", offset);

                cmd.CommandText = sql.ToString();

                List<SagaMemoryDto> results = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    results.Add(ReadMemory(reader));

                }

                return results.ToArray();

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyDictionary<string, SagaMemoryDto>> GetByIdsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {

        if (ids.Count == 0)
        {
            return new Dictionary<string, SagaMemoryDto>(0);

        }

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                string[] parameterNames = new string[ids.Count];

                for (int i = 0; i < ids.Count; i++)
                {

                    string parameterName = "@id" + i.ToString(CultureInfo.InvariantCulture);

                    parameterNames[i] = parameterName;

                    AddParameter(cmd, parameterName, ids[i]);

                }

                cmd.CommandText =
                    $"""
                    SELECT "Id", "Content", "CreatedAt", "SessionId", "Tags", "Source"
                    FROM "saga_memories"
                    WHERE "Id" IN ({string.Join(", ", parameterNames)})
                    """;

                Dictionary<string, SagaMemoryDto> results = new(ids.Count, StringComparer.Ordinal);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    SagaMemoryDto memory = ReadMemory(reader);

                    results[memory.Id] = memory;

                }

                return (IReadOnlyDictionary<string, SagaMemoryDto>)results;

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand memoryCmd = connection.CreateCommand();

                memoryCmd.Transaction = transaction;

                memoryCmd.CommandText = """DELETE FROM "saga_memories" WHERE "Id" = @id""";

                AddParameter(memoryCmd, "@id", id);

                int affected = await memoryCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (affected == 0)
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return false;

                }

                await using DbCommand embeddingCmd = connection.CreateCommand();

                embeddingCmd.Transaction = transaction;

                embeddingCmd.CommandText = """DELETE FROM "saga_memory_embeddings" WHERE "MemoryId" = @id""";

                AddParameter(embeddingCmd, "@id", id);

                _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (availability.IsVecAvailable)
                {

                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.Transaction = transaction;

                    vecCmd.CommandText = """DELETE FROM "saga_memory_embeddings_vec" WHERE "MemoryId" = @id""";

                    AddParameter(vecCmd, "@id", id);

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return true;

            },
            cancellationToken).ConfigureAwait(false);

    }

    public Task DeleteAllAsync(CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand memoryCmd = connection.CreateCommand();

                memoryCmd.Transaction = transaction;

                memoryCmd.CommandText = """DELETE FROM "saga_memories" """;

                _ = await memoryCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand embeddingCmd = connection.CreateCommand();

                embeddingCmd.Transaction = transaction;

                embeddingCmd.CommandText = """DELETE FROM "saga_memory_embeddings" """;

                _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand watermarkCmd = connection.CreateCommand();

                watermarkCmd.Transaction = transaction;

                watermarkCmd.CommandText = """DELETE FROM "saga_extraction_watermarks" """;

                _ = await watermarkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (availability.IsVecAvailable)
                {

                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.Transaction = transaction;

                    vecCmd.CommandText = """DELETE FROM "saga_memory_embeddings_vec" """;

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    public async Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT
                        COUNT(*),
                        COUNT(DISTINCT "SessionId"),
                        MIN("CreatedAt"),
                        MAX("CreatedAt")
                    FROM "saga_memories"
                    """;

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new SagaStats(0, 0, null, null);

                }

                int totalCount = reader.GetInt32(0);

                int sessionCount = reader.GetInt32(1);

                DateTimeOffset? oldest = reader.IsDBNull(2)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);

                DateTimeOffset? newest = reader.IsDBNull(3)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);

                return new SagaStats(totalCount, sessionCount, oldest, newest);

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync<DateTimeOffset?>(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "LastExtractedEntryCreatedAt"
                    FROM "saga_extraction_watermarks"
                    WHERE "SessionId" = @sessionId
                    LIMIT 1
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                if (result is null or DBNull)
                {
                    return null;

                }

                return DateTimeOffset.Parse((string)result, CultureInfo.InvariantCulture);

            },
            cancellationToken).ConfigureAwait(false);

    }

    public Task SetWatermarkAsync(Guid sessionId, DateTimeOffset lastExtractedEntryCreatedAt, CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    INSERT INTO "saga_extraction_watermarks" ("SessionId", "LastExtractedEntryCreatedAt")
                    VALUES (@sessionId, @lastExtractedEntryCreatedAt)
                    ON CONFLICT("SessionId") DO UPDATE SET
                        "LastExtractedEntryCreatedAt" = @lastExtractedEntryCreatedAt
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                AddParameter(cmd, "@lastExtractedEntryCreatedAt", lastExtractedEntryCreatedAt.ToString("o", CultureInfo.InvariantCulture));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static SagaMemoryDto ReadMemory(DbDataReader reader)
    {

        string id = reader.GetString(0);

        string content = reader.GetString(1);

        DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);

        Guid? sessionId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3));

        string? tags = reader.IsDBNull(4) ? null : reader.GetString(4);

        string? source = reader.IsDBNull(5) ? null : reader.GetString(5);

        return new SagaMemoryDto(id, content, createdAt, sessionId, tags, source);

    }

}
