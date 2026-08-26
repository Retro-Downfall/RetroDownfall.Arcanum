using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class SagaMemoryStore
{

    public async Task<SagaMemoryCurationRow?> ReadCurationRowAsync(string id, CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // One LEFT JOIN against saga_memory_embeddings rather than a second round trip: the row,
                // its lifecycle, and whether an embedding still exists are three facts about the same
                // instant, and a caller reading them separately would be describing three instants as
                // though they were one.
                cmd.CommandText =
                    """
                    SELECT m."Id", m."Content", m."CreatedAt", m."SessionId", m."Tags", m."Source",
                           p.SessionId, p.AttachmentId, p.LogicalKey, p.Version,
                           p.ContentHash, p.MaterializedAt, p.SourceType,
                           EXISTS(
                               SELECT 1 FROM "SessionAttachments" a
                               WHERE a."Id" = p.AttachmentId AND a."State" = 'Bound'
                           ),
                           m.ScopeKindCode, m.CampaignId, m."RetiredAtUtc", m."PinnedAtUtc",
                           e."MemoryId" IS NOT NULL
                    FROM "saga_memories" m
                    LEFT JOIN saga_memory_attachment_provenance p ON p.MemoryId = m."Id"
                    LEFT JOIN "saga_memory_embeddings" e ON e."MemoryId" = m."Id"
                    WHERE m."Id" = @id
                    """;

                AddParameter(cmd, "@id", id);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    return null;

                }

                SagaMemoryDto memory = ReadMemory(reader);

                bool hasEmbedding = reader.GetInt32(18) == 1;

                SagaMemoryLifecycle lifecycle = new(memory.RetiredAtUtc, memory.PinnedAtUtc);

                return new SagaMemoryCurationRow(memory, lifecycle, hasEmbedding);

            },
            cancellationToken).ConfigureAwait(false);

    }

    public Task<SagaCurationOutcome> RetireAsync(
        string id, byte[] expectedContentDigest, DateTimeOffset retiredAt, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        ArgumentNullException.ThrowIfNull(expectedContentDigest);

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Transaction and commands are created fresh on every invocation of this delegate, exactly
                // as InsertCoreAsync's: if SqliteBusyRetry retries after a SQLITE_BUSY failure, the prior
                // transaction has already been rolled back/disposed by the `await using` below, so the
                // retry starts a brand-new transaction rather than reusing a stale one or leaving a
                // retirement split across saga_memories, saga_memory_embeddings,
                // saga_memory_embeddings_vec, saga_retirement_suppressions, and the Annals.
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand readCmd = connection.CreateCommand();

                readCmd.Transaction = transaction;

                readCmd.CommandText =
                    """
                    SELECT Content, CreatedAt, SessionId, ScopeKindCode, CampaignId, RetiredAtUtc
                    FROM saga_memories WHERE Id = @id
                    """;

                AddParameter(readCmd, "@id", id);

                string content;

                DateTimeOffset createdAt;

                Guid? sessionId;

                SagaMemoryScopeKind scopeKind;

                string? campaignId;

                await using (DbDataReader reader = await readCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {

                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {

                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return new SagaCurationOutcome(SagaCurationOutcomeKind.NotFound, null);

                    }

                    content = reader.GetString(0);

                    createdAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);

                    sessionId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));

                    scopeKind = (SagaMemoryScopeKind)reader.GetInt32(3);

                    campaignId = reader.IsDBNull(4) ? null : reader.GetString(4);

                    if (!reader.IsDBNull(5))
                    {

                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return new SagaCurationOutcome(SagaCurationOutcomeKind.AlreadyRetired, null);

                    }

                }

                byte[] currentDigest = AnnalContentDigest.ForSagaMemory(content);

                if (!CryptographicOperations.FixedTimeEquals(currentDigest, expectedContentDigest))
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return new SagaCurationOutcome(SagaCurationOutcomeKind.StaleContent, null);

                }

                await using (DbCommand retireCmd = connection.CreateCommand())
                {

                    retireCmd.Transaction = transaction;

                    retireCmd.CommandText = """UPDATE saga_memories SET RetiredAtUtc = @retiredAt WHERE Id = @id""";

                    AddParameter(retireCmd, "@retiredAt", retiredAt.ToString("o", CultureInfo.InvariantCulture));

                    AddParameter(retireCmd, "@id", id);

                    _ = await retireCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await using (DbCommand embeddingCmd = connection.CreateCommand())
                {

                    embeddingCmd.Transaction = transaction;

                    embeddingCmd.CommandText = """DELETE FROM "saga_memory_embeddings" WHERE "MemoryId" = @id""";

                    AddParameter(embeddingCmd, "@id", id);

                    _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                if (availability.IsVecAvailable)
                {

                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.Transaction = transaction;

                    vecCmd.CommandText = """DELETE FROM "saga_memory_embeddings_vec" WHERE "MemoryId" = @id""";

                    AddParameter(vecCmd, "@id", id);

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                byte[] suppressionKey = await SagaSuppressionKeyStore
                    .ReadOrCreateAsync(connection, transaction, retiredAt, cancellationToken).ConfigureAwait(false);

                byte[] suppressionDigest = SagaSuppressionDigest.Compute(suppressionKey, scopeKind, campaignId, content);

                await using (DbCommand suppressionCmd = connection.CreateCommand())
                {

                    suppressionCmd.Transaction = transaction;

                    // OR IGNORE: two memories with identical content in one scope hash to the same
                    // digest, and the second retirement must not abort on the first's row.
                    suppressionCmd.CommandText =
                        """
                        INSERT OR IGNORE INTO saga_retirement_suppressions (
                            SuppressionDigest, ScopeKindCode, CampaignId, RetiredAtUtc)
                        VALUES (@digest, @scopeKindCode, @campaignId, @retiredAt)
                        """;

                    AddParameter(suppressionCmd, "@digest", suppressionDigest);

                    AddParameter(suppressionCmd, "@scopeKindCode", (int)scopeKind);

                    AddParameter(suppressionCmd, "@campaignId", (object?)campaignId ?? DBNull.Value);

                    AddParameter(suppressionCmd, "@retiredAt", retiredAt.ToString("o", CultureInfo.InvariantCulture));

                    _ = await suppressionCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                // Both Annals writes below are deliberately ungated: they run whatever
                // Arcanum:Features:Annals says, because the record that the operator ended this memory is
                // evidence rather than retrieval, mirroring the ungated-deletion write in DeleteAsync.
                //
                // The assert opens the claim the retirement is about to end. AppendRetirementAsync refuses
                // a subject with no claim on purpose -- opening one is a decision only the caller can make
                // -- so this reconstructs the honest assertion extraction would have written, at the
                // memory's own CreatedAt, before the operator's tombstone is appended. That is what makes
                // the history read "extraction asserted this, then the operator ended it" rather than
                // crediting the operator with the original assertion.
                _ = await AnnalsClaimWriter.AppendAssertAsync(
                    connection,
                    transaction,
                    AnnalSubjectStore.Saga,
                    id,
                    AnnalOrigin.AgentExtracted,
                    scopeKind,
                    campaignId,
                    ContentSensitivity.None,
                    currentDigest,
                    createdAt,
                    createdAt,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

                _ = await AnnalsClaimWriter.AppendRetirementAsync(
                    connection,
                    transaction,
                    AnnalSubjectStore.Saga,
                    id,
                    AnnalOrigin.OperatorStated,
                    scopeKind,
                    campaignId,
                    ContentSensitivity.None,
                    retiredAt,
                    retiredAt,
                    sourceSessionId: null,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(retiredAt, null));

            },
            cancellationToken);

    }

    public Task<SagaCurationOutcome> ReinstateAsync(
        string id,
        byte[] expectedContentDigest,
        float[] embedding,
        DateTimeOffset reinstatedAt,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        ArgumentNullException.ThrowIfNull(expectedContentDigest);

        int expectedDimensions = ArcanumSettingClamps.EmbeddingsDimensions(
            options.CurrentValue.Integrations.Embeddings.Dimensions);

        if (embedding.Length != expectedDimensions)
        {

            throw new InvalidOperationException(
                $"""Saga memory embedding has {embedding.Length} dimensions but {expectedDimensions} are configured at Arcanum:Integrations:Embeddings:Dimensions. Rejecting reinstate to avoid corrupting the vec0 index.""");

        }

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Fresh transaction per attempt, for the same reason RetireAsync and InsertCoreAsync are:
                // a SqliteBusyRetry retry must start clean rather than reuse a transaction the `await
                // using` below already rolled back and disposed.
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand readCmd = connection.CreateCommand();

                readCmd.Transaction = transaction;

                readCmd.CommandText =
                    """
                    SELECT Content, CreatedAt, SessionId, ScopeKindCode, CampaignId, RetiredAtUtc
                    FROM saga_memories WHERE Id = @id
                    """;

                AddParameter(readCmd, "@id", id);

                string content;

                DateTimeOffset createdAt;

                Guid? sessionId;

                SagaMemoryScopeKind scopeKind;

                string? campaignId;

                await using (DbDataReader reader = await readCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {

                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {

                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return new SagaCurationOutcome(SagaCurationOutcomeKind.NotFound, null);

                    }

                    content = reader.GetString(0);

                    createdAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);

                    sessionId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));

                    scopeKind = (SagaMemoryScopeKind)reader.GetInt32(3);

                    campaignId = reader.IsDBNull(4) ? null : reader.GetString(4);

                    if (reader.IsDBNull(5))
                    {

                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return new SagaCurationOutcome(SagaCurationOutcomeKind.NotRetired, null);

                    }

                }

                byte[] currentDigest = AnnalContentDigest.ForSagaMemory(content);

                if (!CryptographicOperations.FixedTimeEquals(currentDigest, expectedContentDigest))
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return new SagaCurationOutcome(SagaCurationOutcomeKind.StaleContent, null);

                }

                await using (DbCommand clearCmd = connection.CreateCommand())
                {

                    clearCmd.Transaction = transaction;

                    clearCmd.CommandText = """UPDATE saga_memories SET RetiredAtUtc = NULL WHERE Id = @id""";

                    AddParameter(clearCmd, "@id", id);

                    _ = await clearCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                byte[] blob = EmbeddingBlobCodec.Encode(embedding);

                await using (DbCommand embeddingCmd = connection.CreateCommand())
                {

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

                }

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

                // The digest is recomputed from the installation's key rather than remembered from the
                // retirement that created it: nothing here names which retirement a memory's suppression
                // came from, only the content-and-scope it was computed over, and that is what the delete
                // below has to reproduce. When no key exists yet, nothing was ever retired and there is no
                // suppression to release.
                byte[]? suppressionKey = await SagaSuppressionKeyStore
                    .ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

                if (suppressionKey is not null)
                {

                    byte[] suppressionDigest = SagaSuppressionDigest.Compute(suppressionKey, scopeKind, campaignId, content);

                    await using DbCommand releaseCmd = connection.CreateCommand();

                    releaseCmd.Transaction = transaction;

                    releaseCmd.CommandText = "DELETE FROM saga_retirement_suppressions WHERE SuppressionDigest = @digest";

                    AddParameter(releaseCmd, "@digest", suppressionDigest);

                    _ = await releaseCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                // Ungated for the same reason RetireAsync's Annals writes are: the record that the
                // operator put this memory back is evidence rather than retrieval, mirroring the
                // ungated-deletion write in DeleteAsync.
                _ = await AnnalsClaimWriter.AppendCorrectionAsync(
                    connection,
                    transaction,
                    AnnalSubjectStore.Saga,
                    id,
                    AnnalOrigin.OperatorStated,
                    scopeKind,
                    campaignId,
                    ContentSensitivity.None,
                    currentDigest,
                    reinstatedAt,
                    reinstatedAt,
                    sourceSessionId: null,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(null, null));

            },
            cancellationToken);

    }

}
