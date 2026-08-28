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
                    SELECT Content, CreatedAt, SessionId, ScopeKindCode, CampaignId, RetiredAtUtc, PinnedAtUtc
                    FROM saga_memories WHERE Id = @id
                    """;

                AddParameter(readCmd, "@id", id);

                string content;

                DateTimeOffset createdAt;

                Guid? sessionId;

                SagaMemoryScopeKind scopeKind;

                string? campaignId;

                DateTimeOffset? pinnedAtUtc;

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

                    pinnedAtUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);

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

                    // ON CONFLICT(SuppressionDigest) DO NOTHING rather than INSERT OR IGNORE: two
                    // memories with identical content in one scope hash to the same digest, and the
                    // second retirement must not abort on the first's row -- but SQLite's IGNORE
                    // conflict algorithm also silently swallows a CHECK or NOT NULL violation, and a
                    // malformed suppression row here is the only thing standing between a retired
                    // memory and the next extraction pass re-adding what the operator just removed.
                    // Naming the conflict target ignores only the conflict this insert actually expects
                    // and lets any other constraint abort the transaction the way it should.
                    suppressionCmd.CommandText =
                        """
                        INSERT INTO saga_retirement_suppressions (
                            SuppressionDigest, ScopeKindCode, CampaignId, RetiredAtUtc)
                        VALUES (@digest, @scopeKindCode, @campaignId, @retiredAt)
                        ON CONFLICT(SuppressionDigest) DO NOTHING
                        """;

                    AddParameter(suppressionCmd, "@digest", suppressionDigest);

                    AddParameter(suppressionCmd, "@scopeKindCode", (int)scopeKind);

                    // The stored scope is canonicalized; the digest above is not, and the asymmetry is
                    // deliberate. The digest binds the spelling this memory row holds now and cannot be
                    // recomputed once the retired content is gone, so a reader of it has to ask for
                    // whichever spellings it might carry - SuppressionDigests is where that pair is
                    // decided. The column is a governed stored identity and is written as one: the
                    // memory row this reads from may not have been swept yet, and a suppression left
                    // holding that spelling would be invisible to a selection binding the canonical
                    // form.
                    AddParameter(
                        suppressionCmd,
                        "@campaignId",
                        (object?)SagaMemoryScopeClassifier.CanonicalCampaignIdentity(campaignId)
                            ?? DBNull.Value);

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

                return new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(retiredAt, pinnedAtUtc));

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

        ArgumentNullException.ThrowIfNull(embedding);

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
                    SELECT Content, CreatedAt, SessionId, ScopeKindCode, CampaignId, RetiredAtUtc, PinnedAtUtc
                    FROM saga_memories WHERE Id = @id
                    """;

                AddParameter(readCmd, "@id", id);

                string content;

                DateTimeOffset createdAt;

                Guid? sessionId;

                SagaMemoryScopeKind scopeKind;

                string? campaignId;

                DateTimeOffset? pinnedAtUtc;

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

                    pinnedAtUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);

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
                //
                // Because the digest binds content-and-scope rather than a memory identity, reinstating
                // one of two co-retired memories that share identical content releases the one
                // suppression covering both, so the still-retired twin's content becomes re-extractable
                // too. That is deliberate, not a leak: the evidence a retirement records is
                // "this content, in this scope, was rejected," not "this specific memory was rejected,"
                // and once the operator has put that content back into that scope, extraction writing it
                // again is no longer something they rejected.
                byte[]? suppressionKey = await SagaSuppressionKeyStore
                    .ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

                if (suppressionKey is not null)
                {

                    // Both digests, symmetrically with the check the write path makes. A memory retired
                    // before the Campaign spelling was settled has its suppression recorded under the
                    // minority rendering, and releasing only the settled one deleted nothing at all - so
                    // an operator could reinstate a memory and watch the next extraction pass refuse it
                    // again, permanently, with no error anywhere. Deleting both is right rather than
                    // merely tolerant: the digest binds content-and-scope, and the two renderings are one
                    // Campaign, so they are two records of the same rejection.
                    //
                    // The pair is canonicalized inside SuppressionDigests rather than here, and that is
                    // the part this once got wrong. campaignId below is read out of the memory row, which
                    // the version-5 sweep may not have reached; handing that on unchanged made the pair
                    // one digest twice and released nothing at all whenever the two ends of the digest
                    // disagreed about the spelling.
                    (byte[] suppressionDigest, byte[] legacySuppressionDigest) =
                        SuppressionDigests(suppressionKey, scopeKind, campaignId, content);

                    await using DbCommand releaseCmd = connection.CreateCommand();

                    releaseCmd.Transaction = transaction;

                    releaseCmd.CommandText =
                        "DELETE FROM saga_retirement_suppressions"
                        + " WHERE SuppressionDigest IN (@digest, @legacyDigest)";

                    AddParameter(releaseCmd, "@digest", suppressionDigest);

                    AddParameter(releaseCmd, "@legacyDigest", legacySuppressionDigest);

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

                return new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(null, pinnedAtUtc));

            },
            cancellationToken);

    }

    public Task<SagaCurationOutcome> CorrectAsync(
        string id,
        byte[] expectedContentDigest,
        string content,
        float[] embedding,
        DateTimeOffset correctedAt,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        ArgumentNullException.ThrowIfNull(expectedContentDigest);

        ArgumentNullException.ThrowIfNull(content);

        ArgumentNullException.ThrowIfNull(embedding);

        int expectedDimensions = ArcanumSettingClamps.EmbeddingsDimensions(
            options.CurrentValue.Integrations.Embeddings.Dimensions);

        if (embedding.Length != expectedDimensions)
        {

            throw new InvalidOperationException(
                $"""Saga memory embedding has {embedding.Length} dimensions but {expectedDimensions} are configured at Arcanum:Integrations:Embeddings:Dimensions. Rejecting correct to avoid corrupting the vec0 index.""");

        }

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Fresh transaction per attempt, for the same reason RetireAsync's and ReinstateAsync's
                // are: a SqliteBusyRetry retry must start clean rather than reuse a transaction the
                // `await using` below already rolled back and disposed.
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand readCmd = connection.CreateCommand();

                readCmd.Transaction = transaction;

                readCmd.CommandText =
                    """
                    SELECT Content, CreatedAt, SessionId, ScopeKindCode, CampaignId, RetiredAtUtc, PinnedAtUtc
                    FROM saga_memories WHERE Id = @id
                    """;

                AddParameter(readCmd, "@id", id);

                string currentContent;

                DateTimeOffset createdAt;

                Guid? sessionId;

                SagaMemoryScopeKind scopeKind;

                string? campaignId;

                DateTimeOffset? pinnedAtUtc;

                await using (DbDataReader reader = await readCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {

                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {

                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return new SagaCurationOutcome(SagaCurationOutcomeKind.NotFound, null);

                    }

                    currentContent = reader.GetString(0);

                    createdAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);

                    sessionId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));

                    scopeKind = (SagaMemoryScopeKind)reader.GetInt32(3);

                    campaignId = reader.IsDBNull(4) ? null : reader.GetString(4);

                    pinnedAtUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);

                    if (!reader.IsDBNull(5))
                    {

                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return new SagaCurationOutcome(SagaCurationOutcomeKind.AlreadyRetired, null);

                    }

                }

                byte[] currentDigest = AnnalContentDigest.ForSagaMemory(currentContent);

                if (!CryptographicOperations.FixedTimeEquals(currentDigest, expectedContentDigest))
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return new SagaCurationOutcome(SagaCurationOutcomeKind.StaleContent, null);

                }

                byte[] newDigest = AnnalContentDigest.ForSagaMemory(content);

                if (CryptographicOperations.FixedTimeEquals(currentDigest, newDigest))
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return new SagaCurationOutcome(SagaCurationOutcomeKind.Unchanged, null);

                }

                await using (DbCommand contentCmd = connection.CreateCommand())
                {

                    contentCmd.Transaction = transaction;

                    contentCmd.CommandText = """UPDATE saga_memories SET Content = @content WHERE Id = @id""";

                    AddParameter(contentCmd, "@content", content);

                    AddParameter(contentCmd, "@id", id);

                    _ = await contentCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                byte[] blob = EmbeddingBlobCodec.Encode(embedding);

                await using (DbCommand embeddingCmd = connection.CreateCommand())
                {

                    embeddingCmd.Transaction = transaction;

                    embeddingCmd.CommandText =
                        """
                        UPDATE "saga_memory_embeddings" SET "Embedding" = @embedding, "Dim" = @dim
                        WHERE "MemoryId" = @memoryId
                        """;

                    AddParameter(embeddingCmd, "@embedding", blob);

                    AddParameter(embeddingCmd, "@dim", embedding.Length);

                    AddParameter(embeddingCmd, "@memoryId", id);

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

                // The same ungated pair RetireAsync and ReinstateAsync write, and for the same reason:
                // the record that the operator corrected this memory is evidence rather than retrieval.
                // The assert reconstructs the honest assertion extraction would have written, at the
                // memory's own CreatedAt, before the operator's correction is appended -- it no-ops when
                // InsertAsync already opened the claim (Annals on) so only the correction below actually
                // lands in that case. That is what makes the history read "extraction asserted this,
                // then the operator corrected it" rather than crediting the operator with the original
                // assertion.
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

                _ = await AnnalsClaimWriter.AppendCorrectionAsync(
                    connection,
                    transaction,
                    AnnalSubjectStore.Saga,
                    id,
                    AnnalOrigin.OperatorStated,
                    scopeKind,
                    campaignId,
                    ContentSensitivity.None,
                    newDigest,
                    correctedAt,
                    correctedAt,
                    sourceSessionId: null,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new SagaCurationOutcome(SagaCurationOutcomeKind.Applied, new SagaMemoryLifecycle(null, pinnedAtUtc));

            },
            cancellationToken);

    }

    public Task<SagaCurationOutcome> SetPinAsync(
        string id, bool pinned, DateTimeOffset changedAt, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // A single RETURNING statement is already atomic on its own, so this transaction buys
                // nothing today -- but CorrectAsync, RetireAsync, and ReinstateAsync all open one inside
                // this same delegate, and a reader who has just read those three would otherwise assume
                // the pattern holds here too. Opening one keeps that assumption true, and keeps a later
                // edit that adds a second statement to this method from silently losing atomicity.
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand pinCmd = connection.CreateCommand();

                pinCmd.Transaction = transaction;

                // RETURNING folds the write and the read this outcome needs -- whether the memory is
                // retired -- into the one statement, rather than an UPDATE followed by a second SELECT
                // that could observe a different row if something else wrote between them.
                pinCmd.CommandText =
                    """
                    UPDATE saga_memories SET PinnedAtUtc = @value WHERE Id = @id
                    RETURNING RetiredAtUtc
                    """;

                AddParameter(
                    pinCmd,
                    "@value",
                    pinned ? changedAt.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);

                AddParameter(pinCmd, "@id", id);

                DateTimeOffset? retiredAtUtc;

                await using (DbDataReader reader = await pinCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {

                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {

                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return new SagaCurationOutcome(SagaCurationOutcomeKind.NotFound, null);

                    }

                    retiredAtUtc = reader.IsDBNull(0)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new SagaCurationOutcome(
                    SagaCurationOutcomeKind.Applied,
                    new SagaMemoryLifecycle(retiredAtUtc, pinned ? changedAt : null));

            },
            cancellationToken);

    }

}
