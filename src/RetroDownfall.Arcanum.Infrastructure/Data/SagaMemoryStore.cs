using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for Saga memories, reusing the scoped <see cref="ArcanumDbContext"/>'s
/// connection. None of <c>saga_memories</c>, <c>saga_memory_embeddings</c>,
/// <c>saga_memory_embeddings_vec</c>, or <c>saga_extraction_watermarks</c> is part of the compiled EF
/// model (they are declared in <c>Data/Schema/Tables/</c> and installed with the rest of the schema),
/// so all access goes through <see cref="DbCommand"/> rather than LINQ, mirroring
/// <see cref="UnseenServantWatermarkStore"/> and <see cref="SanctumBreachRepository"/>.
/// </summary>
internal sealed class SagaMemoryStore(
    ArcanumDbContext db,
    WeaveIndexAvailability availability,
    IOptionsMonitor<ArcanumSettings> options,
    ICovenantLabeledArtifactGuard? labeledArtifactGuard = null) : ISagaMemoryStore
{

    public Task InsertAsync(
        string id,
        string content,
        DateTimeOffset createdAt,
        Guid? sessionId,
        string? tags,
        string? source,
        float[] embedding,
        CancellationToken cancellationToken) =>
        InsertCoreAsync(
            id,
            content,
            createdAt,
            sessionId,
            tags,
            source,
            embedding,
            provenance: null,
            cancellationToken);

    public Task InsertAsync(
        string id,
        string content,
        DateTimeOffset createdAt,
        Guid? sessionId,
        string? tags,
        string? source,
        float[] embedding,
        AttachmentMemoryProvenance provenance,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(provenance);

        return InsertCoreAsync(
            id,
            content,
            createdAt,
            sessionId,
            tags,
            source,
            embedding,
            provenance,
            cancellationToken);

    }

    private Task InsertCoreAsync(
        string id,
        string content,
        DateTimeOffset createdAt,
        Guid? sessionId,
        string? tags,
        string? source,
        float[] embedding,
        AttachmentMemoryProvenance? provenance,
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

                if (provenance is not null)
                {

                    await InsertProvenanceAsync(
                        connection,
                        transaction,
                        id,
                        provenance,
                        cancellationToken).ConfigureAwait(false);

                }

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
                    SELECT m."Id", m."Content", m."CreatedAt", m."SessionId", m."Tags", m."Source",
                           p.SessionId, p.AttachmentId, p.LogicalKey, p.Version,
                           p.ContentHash, p.MaterializedAt, p.SourceType,
                           EXISTS(
                               SELECT 1 FROM "SessionAttachments" a
                               WHERE a."Id" = p.AttachmentId AND a."State" = 'Bound'
                           )
                    FROM "saga_memories" m
                    LEFT JOIN saga_memory_attachment_provenance p ON p.MemoryId = m."Id"
                    WHERE 1 = 1
                    """);

                if (!string.IsNullOrWhiteSpace(query))
                {

                    sql.Append(" AND m.\"Content\" LIKE @query ESCAPE '\\'");

                    AddParameter(cmd, "@query", "%" + EscapeLikePattern(query) + "%");

                }

                if (sessionId is not null)
                {

                    sql.Append(" AND m.\"SessionId\" = @sessionId");

                    AddParameter(cmd, "@sessionId", sessionId.Value.ToString());

                }

                sql.Append(" ORDER BY m.\"CreatedAt\" DESC LIMIT @limit OFFSET @offset");

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
                    SELECT m."Id", m."Content", m."CreatedAt", m."SessionId", m."Tags", m."Source",
                           p.SessionId, p.AttachmentId, p.LogicalKey, p.Version,
                           p.ContentHash, p.MaterializedAt, p.SourceType,
                           EXISTS(
                               SELECT 1 FROM "SessionAttachments" a
                               WHERE a."Id" = p.AttachmentId AND a."State" = 'Bound'
                           )
                    FROM "saga_memories" m
                    LEFT JOIN saga_memory_attachment_provenance p ON p.MemoryId = m."Id"
                    WHERE m."Id" IN ({string.Join(", ", parameterNames)})
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

        // The guard, not the purge. A caller that reached this method without going through the
        // sensitivity purge boundary would remove a labelled Saga fact and leave its label behind,
        // pointing at content nothing admits is tainted (§10.20.2).
        if (labeledArtifactGuard is { } guard && Guid.TryParse(id, out Guid memoryId))
        {

            Result unlabeled = await guard
                .EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, memoryId, cancellationToken)
                .ConfigureAwait(false);

            if (unlabeled.IsFailure)
            {

                throw new InvalidOperationException(unlabeled.Error.Message);

            }

        }

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand memoryCmd = connection.CreateCommand();

                memoryCmd.Transaction = transaction;

                await using DbCommand provenanceCmd = connection.CreateCommand();

                provenanceCmd.Transaction = transaction;

                provenanceCmd.CommandText =
                    "DELETE FROM saga_memory_attachment_provenance WHERE MemoryId = @id";

                AddParameter(provenanceCmd, "@id", id);

                _ = await provenanceCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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

    public async Task DeleteAllAsync(CancellationToken cancellationToken)
    {

        // A set-based delete examines no identity at all, so there is no single artifact to ask about.
        // The only honest question is whether the kind still has a labelled member anywhere, and the
        // only safe answer for "yes" is to refuse rather than remove rows nothing ever examined.
        if (labeledArtifactGuard is { } guard)
        {

            Result none = await guard
                .EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, cancellationToken)
                .ConfigureAwait(false);

            if (none.IsFailure)
            {

                throw new InvalidOperationException(none.Error.Message);

            }

        }

        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand memoryCmd = connection.CreateCommand();

                memoryCmd.Transaction = transaction;

                await using DbCommand provenanceCmd = connection.CreateCommand();

                provenanceCmd.Transaction = transaction;

                provenanceCmd.CommandText = "DELETE FROM saga_memory_attachment_provenance";

                _ = await provenanceCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

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

        AttachmentMemoryProvenance? provenance = reader.IsDBNull(6)
            ? null
            : new AttachmentMemoryProvenance(
                Guid.Parse(reader.GetString(6)),
                Guid.Parse(reader.GetString(7)),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetString(10),
                DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                reader.GetString(12),
                reader.GetInt32(13) == 1
                    ? AttachmentSourceAvailability.Available
                    : AttachmentSourceAvailability.Unavailable);

        return new SagaMemoryDto(
            id,
            content,
            createdAt,
            sessionId,
            tags,
            source,
            provenance);

    }

    private static async Task InsertProvenanceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string memoryId,
        AttachmentMemoryProvenance provenance,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO saga_memory_attachment_provenance (
                MemoryId, SessionId, AttachmentId, LogicalKey, Version,
                ContentHash, MaterializedAt, SourceType)
            VALUES (
                @memoryId, @sessionId, @attachmentId, @logicalKey, @version,
                @contentHash, @materializedAt, @sourceType)
            """;

        AddParameter(command, "@memoryId", memoryId);

        AddParameter(command, "@sessionId", provenance.SessionId.ToString());

        AddParameter(command, "@attachmentId", provenance.AttachmentId.ToString());

        AddParameter(command, "@logicalKey", provenance.LogicalKey);

        AddParameter(command, "@version", provenance.Version);

        AddParameter(command, "@contentHash", provenance.ContentHash);

        AddParameter(
            command,
            "@materializedAt",
            provenance.MaterializedAt.ToString("o", CultureInfo.InvariantCulture));

        AddParameter(command, "@sourceType", provenance.SourceType);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

}
