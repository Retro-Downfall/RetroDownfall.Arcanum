using System.Data;

using System.Data.Common;

using System.Globalization;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore.Storage;

using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed record SessionAttachmentIndexState(
    Guid AttachmentId,
    SessionAttachmentIndexStatus Status,
    string ContentSha256,
    int AttemptCount,
    string? FailureReason,
    DateTimeOffset? ExtractedAt,
    DateTimeOffset? IndexedAt,
    string? PublishedGenerationId,
    string? PendingGenerationId,
    int NextChunkIndex,
    int? PendingEmbeddingDimension,
    string? PendingPipelineFingerprint,
    DateTimeOffset? PendingExtractedAt,
    DateTimeOffset UpdatedAt);

internal sealed record SessionAttachmentIndexCheckpoint(
    string GenerationId,
    int NextChunkIndex,
    DateTimeOffset ExtractedAt);

internal sealed record SessionAttachmentIndexedChunk(
    string ChunkId,
    Guid SessionId,
    Guid AttachmentId,
    string LogicalKey,
    int Version,
    string OriginalFileName,
    string MimeType,
    string ContentSha256,
    int ChunkIndex,
    int CharacterStart,
    int CharacterEnd,
    int StartLine,
    int EndLine,
    string Content,
    int EmbeddingDimension,
    DateTimeOffset ExtractedAt,
    DateTimeOffset IndexedAt,
    string? RetrievalScope);

internal sealed class SessionAttachmentIndexRepository(
    ArcanumDbContext db,
    WeaveIndexAvailability availability) : ISessionAttachmentIndexMaintenance
{

    public async Task<SessionAttachmentIndexRequest[]> ReconcileAndFindPendingAsync(
        int expectedDimensions,
        int maxAttachments,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (DbCommand stale = connection.CreateCommand())
        {

            stale.Transaction = transaction;

            stale.CommandText =
                """
                UPDATE session_attachment_index_state
                SET Status = 'Stale', UpdatedAt = @updatedAt
                WHERE Status = 'Indexed'
                  AND (
                      ContentSha256 <> COALESCE((
                          SELECT a.ContentSha256
                          FROM SessionAttachments a
                          WHERE a.Id = session_attachment_index_state.AttachmentId
                      ), '')
                      OR EXISTS (
                          SELECT 1
                          FROM session_attachment_chunks c
                          WHERE c.AttachmentId = session_attachment_index_state.AttachmentId
                            AND c.EmbeddingDimension <> @dimensions
                      )
                  )
                """;

            AddParameter(stale, "@updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            AddParameter(stale, "@dimensions", expectedDimensions);

            _ = await stale.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        await using (DbCommand orphanState = connection.CreateCommand())
        {

            orphanState.Transaction = transaction;

            orphanState.CommandText =
                "DELETE FROM session_attachment_index_state WHERE AttachmentId NOT IN (SELECT Id FROM SessionAttachments)";

            _ = await orphanState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        await using (DbCommand orphanChunks = connection.CreateCommand())
        {

            orphanChunks.Transaction = transaction;

            orphanChunks.CommandText =
                "DELETE FROM session_attachment_chunks WHERE AttachmentId NOT IN (SELECT Id FROM SessionAttachments)";

            _ = await orphanChunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        if (availability.IsVecAvailable)
        {

            await using DbCommand orphanVec = connection.CreateCommand();

            orphanVec.Transaction = transaction;

            orphanVec.CommandText =
                "DELETE FROM session_attachment_embeddings_vec WHERE ChunkId NOT IN (SELECT ChunkId FROM session_attachment_chunks)";

            try
            {

                _ = await orphanVec.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {

                availability.SetAvailable(false);

            }

        }

        List<SessionAttachmentIndexRequest> requests = [];

        await using (DbCommand pending = connection.CreateCommand())
        {

            pending.Transaction = transaction;

            pending.CommandText =
                """
                SELECT a.Id, a.SessionId, COALESCE(s.AttemptCount, 0)
                FROM SessionAttachments a
                LEFT JOIN session_attachment_index_state s ON s.AttachmentId = a.Id
                WHERE a.State = 'Bound'
                  AND a.SessionId IS NOT NULL
                  AND (s.AttachmentId IS NULL OR s.Status IN ('Pending', 'Stale'))
                ORDER BY a.CreatedAt, a.Id
                LIMIT @limit
                """;

            AddParameter(pending, "@limit", Math.Max(1, maxAttachments));

            await using DbDataReader reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                requests.Add(new SessionAttachmentIndexRequest(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt32(2)));

            }

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return [.. requests];

    }

    public async Task SetPendingAsync(
        SessionAttachmentRecord attachment,
        int attempt,
        CancellationToken cancellationToken)
    {

        if (attachment.SessionId is not { } sessionId)
        {

            throw new InvalidOperationException("Only bound session attachments can be marked pending.");

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        int latestVersion = await GetLatestVersionAsync(
            connection,
            transaction,
            sessionId,
            attachment.LogicalKey,
            cancellationToken).ConfigureAwait(false);

        if (attachment.Version == latestVersion)
        {

            await using DbCommand historical = connection.CreateCommand();

            historical.Transaction = transaction;

            historical.CommandText =
                """
                UPDATE session_attachment_chunks
                SET RetrievalScope = NULL
                WHERE SessionId = @sessionId
                  AND LogicalKey = @logicalKey
                  AND AttachmentId <> @attachmentId
                """;

            AddParameter(historical, "@sessionId", sessionId.ToString());

            AddParameter(historical, "@logicalKey", attachment.LogicalKey);

            AddParameter(historical, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

            _ = await historical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        await UpsertStateAsync(
            connection,
            transaction,
            attachment.Id,
            SessionAttachmentIndexStatus.Pending,
            attachment.ContentSha256,
            attempt,
            failureReason: null,
            extractedAt: null,
            indexedAt: null,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

    }

    public async Task MarkWithoutIndexAsync(
        Guid attachmentId,
        string contentSha256,
        SessionAttachmentIndexStatus status,
        int attempt,
        string? failureReason,
        DateTimeOffset? extractedAt,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        string? publishedGenerationId = await GetPublishedGenerationIdAsync(
            connection,
            transaction,
            attachmentId,
            cancellationToken).ConfigureAwait(false);

        await DeleteAttachmentGenerationsExceptAsync(
                connection,
                transaction,
                attachmentId,
                publishedGenerationId,
                cancellationToken)
            .ConfigureAwait(false);

        await UpsertStateAsync(
            connection,
            transaction,
            attachmentId,
            status,
            contentSha256,
            attempt,
            failureReason,
            extractedAt,
            indexedAt: null,
            cancellationToken).ConfigureAwait(false);

        await ClearPendingGenerationAsync(
            connection,
            transaction,
            attachmentId,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

    }

    public async Task<SessionAttachmentIndexCheckpoint> BeginReplaceAsync(
        SessionAttachmentRecord attachment,
        int expectedDimensions,
        string pipelineFingerprint,
        DateTimeOffset extractedAt,
        CancellationToken cancellationToken)
    {

        if (attachment.SessionId is null)
        {

            throw new InvalidOperationException("Only bound session attachments can be indexed.");

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        SessionAttachmentIndexCheckpoint? existingCheckpoint = null;

        await using (DbCommand current = connection.CreateCommand())
        {

            current.Transaction = transaction;

            current.CommandText =
                """
                SELECT PendingGenerationId, NextChunkIndex,
                       PendingEmbeddingDimension, PendingPipelineFingerprint,
                       PendingExtractedAt,
                       PublishedGenerationId
                FROM session_attachment_index_state
                WHERE AttachmentId = @attachmentId
                """;

            AddParameter(current, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

            await using DbDataReader reader = await current
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && !reader.IsDBNull(0)
                && !reader.IsDBNull(2)
                && reader.GetInt32(2) == expectedDimensions
                && !reader.IsDBNull(3)
                && string.Equals(reader.GetString(3), pipelineFingerprint, StringComparison.Ordinal)
                && !reader.IsDBNull(4))
            {

                existingCheckpoint = new SessionAttachmentIndexCheckpoint(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));

            }

        }

        if (existingCheckpoint is not null)
        {

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return existingCheckpoint;

        }

        string? publishedGenerationId = await GetPublishedGenerationIdAsync(
            connection,
            transaction,
            attachment.Id,
            cancellationToken).ConfigureAwait(false);

        await DeleteAttachmentGenerationsExceptAsync(
                connection,
                transaction,
                attachment.Id,
                publishedGenerationId,
                cancellationToken)
            .ConfigureAwait(false);

        string generationId = Guid.NewGuid().ToString("N");

        await using (DbCommand checkpoint = connection.CreateCommand())
        {

            checkpoint.Transaction = transaction;

            checkpoint.CommandText =
                """
                UPDATE session_attachment_index_state
                SET PendingGenerationId = @generationId,
                    NextChunkIndex = 0,
                    PendingEmbeddingDimension = @dimensions,
                    PendingPipelineFingerprint = @pipelineFingerprint,
                    PendingExtractedAt = @extractedAt,
                    UpdatedAt = @updatedAt
                WHERE AttachmentId = @attachmentId
                """;

            AddParameter(checkpoint, "@generationId", generationId);

            AddParameter(checkpoint, "@dimensions", expectedDimensions);

            AddParameter(checkpoint, "@pipelineFingerprint", pipelineFingerprint);

            AddParameter(
                checkpoint,
                "@extractedAt",
                extractedAt.ToString("O", CultureInfo.InvariantCulture));

            AddParameter(
                checkpoint,
                "@updatedAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            AddParameter(checkpoint, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

            _ = await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SessionAttachmentIndexCheckpoint(generationId, 0, extractedAt);

    }

    public async Task AppendReplaceBatchAsync(
        SessionAttachmentRecord attachment,
        string generationId,
        IReadOnlyList<SessionAttachmentTextChunk> chunks,
        IReadOnlyList<Embedding<float>> embeddings,
        int expectedDimensions,
        DateTimeOffset extractedAt,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken)
    {

        if (attachment.SessionId is null)
        {

            throw new InvalidOperationException("Only bound session attachments can be indexed.");

        }

        if (chunks.Count != embeddings.Count)
        {

            throw new InvalidOperationException("Attachment chunk and embedding counts do not match.");

        }

        if (embeddings.Any(embedding => embedding.Vector.Length != expectedDimensions))
        {

            throw new InvalidOperationException("Attachment embedding dimension does not match configured dimensions.");

        }

        if (chunks.Count == 0)
        {

            throw new InvalidOperationException("Attachment indexing checkpoint batches cannot be empty.");

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        int nextChunkIndex;

        await using (DbCommand checkpoint = connection.CreateCommand())
        {

            checkpoint.Transaction = transaction;

            checkpoint.CommandText =
                """
                SELECT PendingGenerationId, NextChunkIndex, PendingEmbeddingDimension
                FROM session_attachment_index_state
                WHERE AttachmentId = @attachmentId
                """;

            AddParameter(checkpoint, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

            await using DbDataReader reader = await checkpoint
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.IsDBNull(0)
                || !string.Equals(reader.GetString(0), generationId, StringComparison.Ordinal)
                || reader.IsDBNull(2)
                || reader.GetInt32(2) != expectedDimensions)
            {

                throw new InvalidOperationException(
                    "Attachment indexing checkpoint no longer matches the active generation.");

            }

            nextChunkIndex = reader.GetInt32(1);

        }

        if (chunks[0].ChunkIndex != nextChunkIndex
            || chunks.Where((chunk, index) => chunk.ChunkIndex != nextChunkIndex + index).Any())
        {

            throw new InvalidOperationException(
                $"Attachment indexing checkpoint expected chunk {nextChunkIndex}, but received a non-contiguous batch beginning at {chunks[0].ChunkIndex}.");

        }

        SessionAttachmentRecord stagedAttachment = attachment with
        {

            SessionId = Guid.Empty,

        };

        for (int i = 0; i < chunks.Count; i++)
        {

            SessionAttachmentTextChunk chunk = chunks[i];

            string chunkId = string.Concat(
                attachment.Id.ToString("N"),
                ":",
                generationId,
                ":",
                chunk.ChunkIndex.ToString(CultureInfo.InvariantCulture));

            await InsertChunkAsync(
                connection,
                transaction,
                chunkId,
                generationId,
                stagedAttachment,
                chunk,
                expectedDimensions,
                extractedAt,
                indexedAt,
                retrievalScope: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            float[] vector = embeddings[i].Vector.ToArray();

            await InsertEmbeddingAsync(
                connection,
                transaction,
                chunkId,
                vector,
                cancellationToken).ConfigureAwait(false);

        }

        await using (DbCommand advance = connection.CreateCommand())
        {

            advance.Transaction = transaction;

            advance.CommandText =
                """
                UPDATE session_attachment_index_state
                SET NextChunkIndex = @nextChunkIndex,
                    UpdatedAt = @updatedAt
                WHERE AttachmentId = @attachmentId
                  AND PendingGenerationId = @generationId
                  AND NextChunkIndex = @expectedChunkIndex
                """;

            AddParameter(advance, "@nextChunkIndex", checked(nextChunkIndex + chunks.Count));

            AddParameter(
                advance,
                "@updatedAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            AddParameter(advance, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

            AddParameter(advance, "@generationId", generationId);

            AddParameter(advance, "@expectedChunkIndex", nextChunkIndex);

            if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {

                throw new InvalidOperationException(
                    "Attachment indexing checkpoint was advanced by another worker.");

            }

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

    }

    public async Task CompleteReplaceAsync(
        SessionAttachmentRecord attachment,
        string generationId,
        int expectedChunkCount,
        DateTimeOffset extractedAt,
        DateTimeOffset indexedAt,
        int attempt,
        CancellationToken cancellationToken)
    {

        if (attachment.SessionId is not { } sessionId)
        {

            throw new InvalidOperationException("Only bound session attachments can be indexed.");

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        string? publishedGenerationId;

        await using (DbCommand checkpoint = connection.CreateCommand())
        {

            checkpoint.Transaction = transaction;

            checkpoint.CommandText =
                """
                SELECT PublishedGenerationId, PendingGenerationId, NextChunkIndex
                FROM session_attachment_index_state
                WHERE AttachmentId = @attachmentId
                """;

            AddParameter(checkpoint, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

            await using DbDataReader reader = await checkpoint
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.IsDBNull(1)
                || !string.Equals(reader.GetString(1), generationId, StringComparison.Ordinal)
                || reader.GetInt32(2) != expectedChunkCount)
            {

                throw new InvalidOperationException(
                    "Attachment indexing completion does not match the durable checkpoint.");

            }

            publishedGenerationId = reader.IsDBNull(0) ? null : reader.GetString(0);

        }

        int latestVersion = await GetLatestVersionAsync(
            connection,
            transaction,
            sessionId,
            attachment.LogicalKey,
            cancellationToken).ConfigureAwait(false);

        if (attachment.Version == latestVersion)
        {

            await using DbCommand historical = connection.CreateCommand();

            historical.Transaction = transaction;

            historical.CommandText =
                """
                UPDATE session_attachment_chunks
                SET RetrievalScope = NULL
                WHERE SessionId = @sessionId AND LogicalKey = @logicalKey
                """;

            AddParameter(historical, "@sessionId", sessionId.ToString());

            AddParameter(historical, "@logicalKey", attachment.LogicalKey);

            _ = await historical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        if (publishedGenerationId is not null
            && !string.Equals(publishedGenerationId, generationId, StringComparison.Ordinal))
        {

            await DeleteAttachmentGenerationsExceptAsync(
                connection,
                transaction,
                attachment.Id,
                generationId,
                cancellationToken).ConfigureAwait(false);

        }

        await using (DbCommand publish = connection.CreateCommand())
        {

            publish.Transaction = transaction;

            publish.CommandText =
                """
                UPDATE session_attachment_chunks
                SET SessionId = @sessionId,
                    RetrievalScope = @retrievalScope,
                    IndexedAt = @indexedAt
                WHERE AttachmentId = @attachmentId
                  AND GenerationId = @generationId
                """;

            AddParameter(publish, "@sessionId", sessionId.ToString());

            AddParameter(
                publish,
                "@retrievalScope",
                attachment.Version == latestVersion ? sessionId.ToString() : DBNull.Value);

            AddParameter(publish, "@indexedAt", indexedAt.ToString("O", CultureInfo.InvariantCulture));

            AddParameter(publish, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

            AddParameter(publish, "@generationId", generationId);

            if (await publish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)
                != expectedChunkCount)
            {

                throw new InvalidOperationException(
                    "Attachment indexing completion found an incomplete staged generation.");

            }

        }

        await UpsertStateAsync(
            connection,
            transaction,
            attachment.Id,
            SessionAttachmentIndexStatus.Indexed,
            attachment.ContentSha256,
            attempt,
            failureReason: null,
            extractedAt,
            indexedAt,
            cancellationToken).ConfigureAwait(false);

        await using (DbCommand finalize = connection.CreateCommand())
        {

            finalize.Transaction = transaction;

            finalize.CommandText =
                """
                UPDATE session_attachment_index_state
                SET PublishedGenerationId = @generationId,
                    PendingGenerationId = NULL,
                    NextChunkIndex = 0,
                    PendingEmbeddingDimension = NULL,
                    PendingPipelineFingerprint = NULL,
                    PendingExtractedAt = NULL
                WHERE AttachmentId = @attachmentId
                  AND PendingGenerationId = @generationId
                """;

            AddParameter(finalize, "@generationId", generationId);

            AddParameter(finalize, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

            if (await finalize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {

                throw new InvalidOperationException(
                    "Attachment indexing generation changed before publication completed.");

            }

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

    }

    public async Task<SessionAttachmentIndexState> GetStateAsync(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT AttachmentId, Status, ContentSha256, AttemptCount, FailureReason,
                   ExtractedAt, IndexedAt, PublishedGenerationId, PendingGenerationId,
                   NextChunkIndex, PendingEmbeddingDimension, PendingPipelineFingerprint,
                   PendingExtractedAt, UpdatedAt
            FROM session_attachment_index_state
            WHERE AttachmentId = @attachmentId
            """;

        AddParameter(command, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            throw new InvalidOperationException($"No attachment indexing state exists for '{attachmentId}'.");

        }

        return ReadState(reader);

    }

    public async Task<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>> GetStatusesAsync(
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {

        if (attachmentIds.Count == 0)
        {

            return new Dictionary<Guid, SessionAttachmentIndexStatus>();

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        // Rendered here rather than left to BuildInCommand's Guid branch. That branch is a funnel every
        // list-valued predicate in this file shares, and uppercasing inside it would convert whatever a
        // later caller happens to pass - including the chunk columns that hold a Session identity in the
        // minority form on purpose. The conversion belongs to the column, so it is made where the column
        // is known.
        string[] canonicalAttachmentIds =
            [.. attachmentIds.Select(static id => id.ToString().ToUpperInvariant())];

        command.CommandText = BuildInCommand(
            "SELECT AttachmentId, Status FROM session_attachment_index_state WHERE AttachmentId IN (",
            canonicalAttachmentIds,
            command,
            ")");

        Dictionary<Guid, SessionAttachmentIndexStatus> statuses = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            statuses[Guid.Parse(reader.GetString(0))] = Enum.Parse<SessionAttachmentIndexStatus>(
                reader.GetString(1),
                ignoreCase: false);

        }

        return statuses;

    }

    public async Task<SessionAttachmentIndexedChunk[]> GetChunksForAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT ChunkId, SessionId, AttachmentId, LogicalKey, Version, OriginalFileName,
                   MimeType, ContentSha256, ChunkIndex, CharacterStart, CharacterEnd,
                   StartLine, EndLine, Content, EmbeddingDimension, ExtractedAt, IndexedAt,
                   RetrievalScope
            FROM session_attachment_chunks
            WHERE AttachmentId = @attachmentId
            ORDER BY ChunkIndex
            """;

        AddParameter(command, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

        return await ReadChunksAsync(command, cancellationToken).ConfigureAwait(false);

    }

    public async Task<SessionAttachmentRetrievedChunk[]> GetRetrievedChunksAsync(
        Guid sessionId,
        IReadOnlyList<DivinationResult> hits,
        CancellationToken cancellationToken)
    {

        if (hits.Count == 0)
        {

            return [];

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        List<string> ids = [.. hits.Select(static hit => hit.Id)];

        string suffix = ") AND SessionId = @sessionId";

        command.CommandText = BuildInCommand(
            """
            SELECT ChunkId, SessionId, AttachmentId, LogicalKey, Version, OriginalFileName,
                   MimeType, ContentSha256, ChunkIndex, CharacterStart, CharacterEnd,
                   StartLine, EndLine, Content
            FROM session_attachment_chunks
            WHERE ChunkId IN (
            """,
            ids,
            command,
            suffix);

        AddParameter(command, "@sessionId", sessionId.ToString());

        Dictionary<string, SessionAttachmentRetrievedChunk> byId = new(StringComparer.Ordinal);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string chunkId = reader.GetString(0);

            byId[chunkId] = new SessionAttachmentRetrievedChunk(
                chunkId,
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetString(13),
                Similarity: 0f);

        }

        return hits
            .Where(hit => byId.ContainsKey(hit.Id))
            .Select(hit => byId[hit.Id] with { Similarity = hit.Similarity })
            .ToArray();

    }

    public async Task DeleteForSessionInAmbientTransactionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (db.Database.CurrentTransaction is null)
        {

            throw new InvalidOperationException("Attachment index purge requires an ambient transaction.");

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        DbTransaction transaction = db.Database.CurrentTransaction.GetDbTransaction();

        List<string> chunkIds = await GetChunkIdsForSessionAsync(
            connection,
            transaction,
            sessionId,
            cancellationToken).ConfigureAwait(false);

        await DeleteVecRowsAsync(connection, transaction, chunkIds, cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // Two parameters for one Session, because the two columns hold it in different spellings on
        // purpose. A chunk's own SessionId is the tapestry's live scope-id set and stays as the indexer
        // wrote it; SessionAttachments.SessionId is canonical. One parameter served both until this
        // split, and because the two predicates are joined by OR the failure would have been a silent
        // under-delete leaving orphaned chunks behind, not a loud one.
        command.CommandText =
            """
            DELETE FROM session_attachment_chunks
            WHERE SessionId = @chunkSessionId
               OR AttachmentId IN (
                   SELECT Id FROM SessionAttachments WHERE SessionId = @attachmentSessionId
               )
            """;

        AddParameter(command, "@chunkSessionId", sessionId.ToString());

        AddParameter(command, "@attachmentSessionId", sessionId.ToString().ToUpperInvariant());

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private async Task DeleteAttachmentIndexAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        List<string> ids = [];

        await using (DbCommand select = connection.CreateCommand())
        {

            select.Transaction = transaction;

            select.CommandText = "SELECT ChunkId FROM session_attachment_chunks WHERE AttachmentId = @attachmentId";

            AddParameter(select, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

            await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                ids.Add(reader.GetString(0));

            }

        }

        await DeleteVecRowsAsync(connection, transaction, ids, cancellationToken).ConfigureAwait(false);

        await using DbCommand delete = connection.CreateCommand();

        delete.Transaction = transaction;

        delete.CommandText = "DELETE FROM session_attachment_chunks WHERE AttachmentId = @attachmentId";

        AddParameter(delete, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

        _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<string?> GetPublishedGenerationIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            "SELECT PublishedGenerationId FROM session_attachment_index_state WHERE AttachmentId = @attachmentId";

        AddParameter(command, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    }

    private async Task DeleteAttachmentGenerationsExceptAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid attachmentId,
        string? preservedGenerationId,
        CancellationToken cancellationToken)
    {

        string generationPredicate = preservedGenerationId is null
            ? string.Empty
            : " AND GenerationId <> @preservedGenerationId";

        List<string> chunkIds = [];

        await using (DbCommand select = connection.CreateCommand())
        {

            select.Transaction = transaction;

            select.CommandText =
                "SELECT ChunkId FROM session_attachment_chunks WHERE AttachmentId = @attachmentId"
                + generationPredicate;

            AddParameter(select, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

            if (preservedGenerationId is not null)
            {

                AddParameter(select, "@preservedGenerationId", preservedGenerationId);

            }

            await using DbDataReader reader = await select
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                chunkIds.Add(reader.GetString(0));

            }

        }

        await DeleteVecRowsAsync(connection, transaction, chunkIds, cancellationToken)
            .ConfigureAwait(false);

        await using DbCommand delete = connection.CreateCommand();

        delete.Transaction = transaction;

        delete.CommandText =
            "DELETE FROM session_attachment_chunks WHERE AttachmentId = @attachmentId"
            + generationPredicate;

        AddParameter(delete, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

        if (preservedGenerationId is not null)
        {

            AddParameter(delete, "@preservedGenerationId", preservedGenerationId);

        }

        _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task ClearPendingGenerationAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            UPDATE session_attachment_index_state
            SET PendingGenerationId = NULL,
                NextChunkIndex = 0,
                PendingEmbeddingDimension = NULL,
                PendingPipelineFingerprint = NULL,
                PendingExtractedAt = NULL
            WHERE AttachmentId = @attachmentId
            """;

        AddParameter(command, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private async Task DeleteVecRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<string> chunkIds,
        CancellationToken cancellationToken)
    {

        if (!availability.IsVecAvailable || chunkIds.Count == 0)
        {

            return;

        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = BuildInCommand(
            "DELETE FROM session_attachment_embeddings_vec WHERE ChunkId IN (",
            chunkIds,
            command,
            ")");

        try
        {

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {

            availability.SetAvailable(false);

        }

    }

    private async Task InsertChunkAsync(
        DbConnection connection,
        DbTransaction transaction,
        string chunkId,
        string generationId,
        SessionAttachmentRecord attachment,
        SessionAttachmentTextChunk chunk,
        int dimensions,
        DateTimeOffset extractedAt,
        DateTimeOffset indexedAt,
        string? retrievalScope,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO session_attachment_chunks (
                ChunkId, GenerationId, SessionId, AttachmentId, LogicalKey, Version, OriginalFileName,
                MimeType, ContentSha256, ChunkIndex, CharacterStart, CharacterEnd,
                StartLine, EndLine, Content, EmbeddingDimension, ExtractedAt, IndexedAt,
                RetrievalScope)
            VALUES (
                @chunkId, @generationId, @sessionId, @attachmentId, @logicalKey, @version, @originalFileName,
                @mimeType, @contentSha256, @chunkIndex, @characterStart, @characterEnd,
                @startLine, @endLine, @content, @dimensions, @extractedAt, @indexedAt,
                @retrievalScope)
            """;

        AddParameter(command, "@chunkId", chunkId);

        AddParameter(command, "@generationId", generationId);

        AddParameter(command, "@sessionId", attachment.SessionId!.Value.ToString());

        AddParameter(command, "@attachmentId", attachment.Id.ToString().ToUpperInvariant());

        AddParameter(command, "@logicalKey", attachment.LogicalKey);

        AddParameter(command, "@version", attachment.Version);

        AddParameter(command, "@originalFileName", attachment.OriginalFileName);

        AddParameter(command, "@mimeType", attachment.MimeType);

        AddParameter(command, "@contentSha256", attachment.ContentSha256);

        AddParameter(command, "@chunkIndex", chunk.ChunkIndex);

        AddParameter(command, "@characterStart", chunk.CharacterStart);

        AddParameter(command, "@characterEnd", chunk.CharacterEnd);

        AddParameter(command, "@startLine", chunk.StartLine);

        AddParameter(command, "@endLine", chunk.EndLine);

        AddParameter(command, "@content", chunk.Text);

        AddParameter(command, "@dimensions", dimensions);

        AddParameter(command, "@extractedAt", extractedAt.ToString("O", CultureInfo.InvariantCulture));

        AddParameter(command, "@indexedAt", indexedAt.ToString("O", CultureInfo.InvariantCulture));

        AddParameter(command, "@retrievalScope", retrievalScope is null ? DBNull.Value : retrievalScope);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private async Task InsertEmbeddingAsync(
        DbConnection connection,
        DbTransaction transaction,
        string chunkId,
        float[] vector,
        CancellationToken cancellationToken)
    {

        byte[] encoded = EmbeddingBlobCodec.Encode(vector);

        await using (DbCommand blob = connection.CreateCommand())
        {

            blob.Transaction = transaction;

            blob.CommandText =
                "INSERT INTO session_attachment_embeddings (ChunkId, Embedding, Dim) VALUES (@id, @embedding, @dim)";

            AddParameter(blob, "@id", chunkId);

            AddParameter(blob, "@embedding", encoded);

            AddParameter(blob, "@dim", vector.Length);

            _ = await blob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        if (!availability.IsVecAvailable)
        {

            return;

        }

        await using DbCommand vec = connection.CreateCommand();

        vec.Transaction = transaction;

        vec.CommandText =
            "INSERT INTO session_attachment_embeddings_vec (ChunkId, Embedding) VALUES (@id, @embedding)";

        AddParameter(vec, "@id", chunkId);

        AddParameter(vec, "@embedding", encoded);

        try
        {

            _ = await vec.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {

            availability.SetAvailable(false);

        }

    }

    private async Task UpsertStateAsync(
        Guid attachmentId,
        SessionAttachmentIndexStatus status,
        string contentSha256,
        int attempt,
        string? failureReason,
        DateTimeOffset? extractedAt,
        DateTimeOffset? indexedAt,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await UpsertStateAsync(
            connection,
            transaction: null,
            attachmentId,
            status,
            contentSha256,
            attempt,
            failureReason,
            extractedAt,
            indexedAt,
            cancellationToken).ConfigureAwait(false);

    }

    private static async Task UpsertStateAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid attachmentId,
        SessionAttachmentIndexStatus status,
        string contentSha256,
        int attempt,
        string? failureReason,
        DateTimeOffset? extractedAt,
        DateTimeOffset? indexedAt,
        CancellationToken cancellationToken)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO session_attachment_index_state (
                AttachmentId, Status, ContentSha256, AttemptCount, FailureReason,
                ExtractedAt, IndexedAt, UpdatedAt)
            VALUES (
                @attachmentId, @status, @contentSha256, @attemptCount, @failureReason,
                @extractedAt, @indexedAt, @updatedAt)
            ON CONFLICT(AttachmentId) DO UPDATE SET
                Status = excluded.Status,
                ContentSha256 = excluded.ContentSha256,
                AttemptCount = excluded.AttemptCount,
                FailureReason = excluded.FailureReason,
                ExtractedAt = excluded.ExtractedAt,
                IndexedAt = excluded.IndexedAt,
                UpdatedAt = excluded.UpdatedAt
            """;

        AddParameter(command, "@attachmentId", attachmentId.ToString().ToUpperInvariant());

        AddParameter(command, "@status", status.ToString());

        AddParameter(command, "@contentSha256", contentSha256);

        AddParameter(command, "@attemptCount", attempt);

        AddParameter(command, "@failureReason", failureReason is null ? DBNull.Value : failureReason);

        AddParameter(command, "@extractedAt", extractedAt is null
            ? DBNull.Value
            : extractedAt.Value.ToString("O", CultureInfo.InvariantCulture));

        AddParameter(command, "@indexedAt", indexedAt is null
            ? DBNull.Value
            : indexedAt.Value.ToString("O", CultureInfo.InvariantCulture));

        AddParameter(command, "@updatedAt", now.ToString("O", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<int> GetLatestVersionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        string logicalKey,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            "SELECT COALESCE(MAX(Version), 0) FROM SessionAttachments WHERE SessionId = @sessionId AND LogicalKey = @logicalKey AND State = 'Bound'";

        AddParameter(command, "@sessionId", sessionId.ToString().ToUpperInvariant());

        AddParameter(command, "@logicalKey", logicalKey);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

    }

    private static async Task<List<string>> GetChunkIdsForSessionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // Split for the same reason the delete above it is, and it has to agree with that delete row for
        // row: this is what collects the vector rows the delete is about to orphan.
        command.CommandText =
            """
            SELECT ChunkId
            FROM session_attachment_chunks
            WHERE SessionId = @chunkSessionId
               OR AttachmentId IN (
                   SELECT Id FROM SessionAttachments WHERE SessionId = @attachmentSessionId
               )
            """;

        AddParameter(command, "@chunkSessionId", sessionId.ToString());

        AddParameter(command, "@attachmentSessionId", sessionId.ToString().ToUpperInvariant());

        List<string> ids = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            ids.Add(reader.GetString(0));

        }

        return ids;

    }

    private static SessionAttachmentIndexState ReadState(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Enum.Parse<SessionAttachmentIndexStatus>(reader.GetString(1), ignoreCase: false),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture));

    private static async Task<SessionAttachmentIndexedChunk[]> ReadChunksAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {

        List<SessionAttachmentIndexedChunk> chunks = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            chunks.Add(new SessionAttachmentIndexedChunk(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetString(13),
                reader.GetInt32(14),
                DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture),
                reader.IsDBNull(17) ? null : reader.GetString(17)));

        }

        return [.. chunks];

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

    private static string BuildInCommand<T>(
        string prefix,
        IReadOnlyList<T> values,
        DbCommand command,
        string suffix)
    {

        System.Text.StringBuilder sql = new(prefix);

        for (int i = 0; i < values.Count; i++)
        {

            if (i > 0)
            {

                sql.Append(',');

            }

            string name = "@p" + i.ToString(CultureInfo.InvariantCulture);

            sql.Append(name);

            object value = (object?)values[i] ?? throw new InvalidOperationException();

            // Deliberately not canonicalised. This funnel serves every list-valued predicate in this
            // file, and the columns they compare do not agree on one spelling: an attachment identity is
            // canonical while a chunk's SessionId is not. A caller that binds an identity therefore
            // renders it before it gets here, where the column it is comparing against is known.
            AddParameter(command, name, value is Guid guid ? guid.ToString() : value);

        }

        sql.Append(suffix);

        return sql.ToString();

    }

    private static void AddParameter(DbCommand command, string name, object value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);

    }

}
