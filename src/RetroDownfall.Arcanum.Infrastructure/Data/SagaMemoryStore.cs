using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
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
internal sealed partial class SagaMemoryStore(
    ArcanumDbContext db,
    WeaveIndexAvailability availability,
    IOptionsMonitor<ArcanumSettings> options,
    IMemoryErasureKeyProvider erasureKeys,
    ICovenantLabeledArtifactGuard? labeledArtifactGuard = null,
    IOperatorAuthorityContextIssuer? releaseAuthority = null) : ISagaMemoryStore
{
    public Task<SagaMemoryWriteOutcome> InsertAsync(
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

    public Task<SagaMemoryWriteOutcome> InsertAsync(
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

    private async Task<SagaMemoryWriteOutcome> InsertCoreAsync(
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

        // Opened before the erasure guard's first phase, which must see the connection outside any
        // transaction: resolving the erasure key can read the OS credential store, and that never happens
        // under a SQLite lock.
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await MemoryErasureGuard.RunWithRetryAsync(
            (SqliteConnection)connection,
            MemoryReviewStore.Saga,
            erasureKeys,
            guard => SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    // Transaction and commands are created fresh on every invocation of this delegate: if
                    // SqliteBusyRetry retries after a SQLITE_BUSY failure, the prior transaction has already
                    // been rolled back/disposed by the `await using` blocks below, so the retry starts a
                    // brand-new transaction rather than reusing a stale one or leaving a partial insert split
                    // across saga_memories/saga_memory_embeddings/saga_memory_embeddings_vec.
                    await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                    // Derived here, from the owning Session's canonical binding, rather than accepted from
                    // the caller. A memory's scope is a statement about authority, and a writer that could
                    // name its own Campaign could fabricate one for a Session that never carried it.
                    (SagaMemoryScopeKind scopeKind, string? scopeCampaignId) =
                        await SagaMemoryScopeClassifier
                            .ResolveForSessionAsync(connection, transaction, sessionId, cancellationToken)
                            .ConfigureAwait(false);

                    // The one chokepoint every Saga write goes through, so extraction cannot re-add what an
                    // operator just retired and no future writer can reach around the check by calling
                    // something else. A null key means nothing has ever been retired.
                    byte[]? suppressionKey = await SagaSuppressionKeyStore
                        .ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

                    if (suppressionKey is not null)
                    {
                        (byte[] suppressionDigest, byte[] legacySuppressionDigest) =
                            SagaRetirementSuppression.Digests(suppressionKey, scopeKind, scopeCampaignId, content);

                        await using DbCommand suppressionCheckCmd = connection.CreateCommand();

                        suppressionCheckCmd.Transaction = transaction;

                        suppressionCheckCmd.CommandText =
                            "SELECT 1 FROM saga_retirement_suppressions"
                            + " WHERE SuppressionDigest IN (@digest, @legacyDigest)";

                        AddParameter(suppressionCheckCmd, "@digest", suppressionDigest);

                        AddParameter(suppressionCheckCmd, "@legacyDigest", legacySuppressionDigest);

                        object? hit = await suppressionCheckCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                        if (hit is not null)
                        {
                            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                            return SagaMemoryWriteOutcome.Suppressed;
                        }
                    }

                    // The erasure chokepoint, in the same transaction and over the same derived scope. The
                    // identity is a factory so an installation that has erased nothing never parses the bound
                    // Campaign, and its insert behaves exactly as it did before erasure existed. A Campaign
                    // the factory cannot name throws, which rolls this transaction back and fails the write
                    // closed.
                    MemoryErasureGuardVerdict erasure = await MemoryErasureGuard.CheckAsync(
                        (SqliteConnection)connection,
                        (SqliteTransaction)transaction,
                        guard,
                        () => SagaErasureWriteGate.SagaIdentity(scopeKind, scopeCampaignId, content),
                        cancellationToken).ConfigureAwait(false);

                    if (erasure != MemoryErasureGuardVerdict.Allowed)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return erasure switch
                        {
                            MemoryErasureGuardVerdict.Withheld => SagaMemoryWriteOutcome.Suppressed,
                            MemoryErasureGuardVerdict.RetryWithKey => throw new MemoryErasureRetryException(),
                            _ => throw new MemoryErasureGuardException(MemoryErasureGuard.KeyLostError),
                        };
                    }

                    await using DbCommand memoryCmd = connection.CreateCommand();

                    memoryCmd.Transaction = transaction;

                    memoryCmd.CommandText =
                        """
                        INSERT INTO "saga_memories" (
                            "Id", "Content", "CreatedAt", "SessionId", "Tags", "Source", ScopeKindCode, CampaignId)
                        VALUES (@id, @content, @createdAt, @sessionId, @tags, @source, @scopeKindCode, @scopeCampaignId)
                        """;

                    AddParameter(memoryCmd, "@scopeKindCode", (int)scopeKind);

                    AddParameter(memoryCmd, "@scopeCampaignId", (object?)scopeCampaignId ?? DBNull.Value);

                    AddParameter(memoryCmd, "@id", id);

                    AddParameter(memoryCmd, "@content", content);

                    AddParameter(memoryCmd, "@createdAt", UtcInstantText.Format(createdAt));

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

                    if (options.CurrentValue.Features.Annals)
                    {
                        // Inside the memory's own transaction, and reusing the scope the classifier just
                        // derived rather than deriving a second one. Two derivations of one authority
                        // eventually disagree, and the disagreement would land on what a turn may recall.
                        //
                        // AgentExtracted is the only honest origin here: no scribe tool writes to Saga and no
                        // operator writes a memory into it, so a row arriving on this path is a headless
                        // extraction's inference from a finished transcript rather than something anyone chose
                        // to state. The operator's curation verbs append their own OperatorStated versions over
                        // a memory this path already wrote; they never open a claim as an assertion of their
                        // own, which is why this origin stays the only one an insert can record.
                        _ = await AnnalsClaimWriter.AppendAssertAsync(
                            connection,
                            transaction,
                            AnnalSubjectStore.Saga,
                            id,
                            AnnalOrigin.AgentExtracted,
                            scopeKind,
                            scopeCampaignId,
                            ContentSensitivity.None,
                            AnnalContentDigest.ForSagaMemory(content),
                            createdAt,
                            createdAt,
                            sessionId,
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

                    // Written only while the accelerator is live, and classified from the catalog either
                    // way, so a legacy virtual mirror this runtime cannot open is never touched.
                    _ = await SagaVectorMirror.UpsertAsync(
                        connection,
                        transaction,
                        id,
                        embedding,
                        availability.IsVecAvailable,
                        cancellationToken).ConfigureAwait(false);

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return SagaMemoryWriteOutcome.Written;
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
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
        MemoryScope scope,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                BuildListCommand(cmd, query, sessionId, scope, limit, offset, includeEmbeddingProbe: false);

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

    public async Task<SagaMemoryCurationRow[]> ListCurationRowsAsync(
        string? query,
        Guid? sessionId,
        MemoryScope scope,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                BuildListCommand(cmd, query, sessionId, scope, limit, offset, includeEmbeddingProbe: true);

                List<SagaMemoryCurationRow> results = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    SagaMemoryDto memory = ReadMemory(reader);

                    bool hasEmbedding = reader.GetInt32(18) == 1;

                    results.Add(new SagaMemoryCurationRow(
                        memory,
                        new SagaMemoryLifecycle(memory.RetiredAtUtc, memory.PinnedAtUtc),
                        hasEmbedding));
                }

                return results.ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SagaMemoryPosition[]> ListPositionsAfterAsync(
        SagaMemoryPosition? after,
        int limit,
        CancellationToken cancellationToken)
    {
        // SQLite reads a negative LIMIT as "no limit", so a bad value here would not fail, it would read
        // the whole table into one page.
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // ListAsync's newest-first order with the identity added, which is the primary key, so no
                // two rows tie. Strictly after the cursor: the CreatedAt bound alone is what the
                // CreatedAt index can seek on, and the identity only decides among the rows that share the
                // cursor's instant. The cursor is bound as the stored text, never as a parsed instant, so
                // the comparison is against exactly what ORDER BY sorts.
                if (after is null)
                {
                    cmd.CommandText =
                        """
                        SELECT m."Id", m."CreatedAt"
                        FROM "saga_memories" m
                        ORDER BY m."CreatedAt" DESC, m."Id" DESC
                        LIMIT @limit
                        """;
                }
                else
                {
                    cmd.CommandText =
                        """
                        SELECT m."Id", m."CreatedAt"
                        FROM "saga_memories" m
                        WHERE m."CreatedAt" <= @afterCreatedAt
                          AND (m."CreatedAt" < @afterCreatedAt OR m."Id" < @afterId)
                        ORDER BY m."CreatedAt" DESC, m."Id" DESC
                        LIMIT @limit
                        """;

                    AddParameter(cmd, "@afterCreatedAt", after.CreatedAt);

                    AddParameter(cmd, "@afterId", after.Id);
                }

                AddParameter(cmd, "@limit", limit);

                List<SagaMemoryPosition> results = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(new SagaMemoryPosition(reader.GetString(1), reader.GetString(0)));
                }

                return results.ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> AnyRetrievableAsync(MemoryScope scope, CancellationToken cancellationToken)
    {
        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // Retrieval's own choice, read the other way round: a turn ranks only embedded rows, so an
                // inner join through the embeddings is what "a turn could reach it" means. Unscoped when the
                // gate is off, exactly as the turn calls SearchAsync then; scoped to the installation and
                // this scope's Campaign when it is on, as SearchCampaignScopedAsync over ToSagaScope() is.
                // Retirement already removes the embedding, so the RetiredAtUtc predicate is the statement
                // of intent rather than the thing that does the work.
                cmd.CommandText =
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM "saga_memory_embeddings" e
                        INNER JOIN "saga_memories" m ON m."Id" = e."MemoryId"
                        WHERE m."RetiredAtUtc" IS NULL
                          AND (@enforced = 0
                               OR m.ScopeKindCode = @globalScopeKind
                               OR (m.ScopeKindCode = @campaignScopeKind AND m.CampaignId = @campaignId)))
                    """;

                AddParameter(cmd, "@enforced", scope.IsEnforced ? 1 : 0);

                AddParameter(cmd, "@globalScopeKind", (int)SagaMemoryScopeKind.Global);

                AddParameter(cmd, "@campaignScopeKind", (int)SagaMemoryScopeKind.Campaign);

                // Canonical, as ListAsync and DivinationService bind it. No Campaign binds a null that no
                // row can equal, leaving the installation-scoped rows alone, which is what a turn that
                // resolved no Campaign ranks.
                AddParameter(
                    cmd,
                    "@campaignId",
                    scope.CampaignId is { } campaignId
                        ? campaignId.ToString("D").ToUpperInvariant()
                        : DBNull.Value);

                object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The listing's one query, shared by <see cref="ListAsync"/> and <see cref="ListCurationRowsAsync"/>
    /// so the two can never select different memories for the same arguments.
    /// </summary>
    /// <remarks>
    /// <paramref name="includeEmbeddingProbe"/> appends one projected column, at ordinal 18, after every
    /// column <see cref="ReadMemory"/> reads. It is a correlated <c>EXISTS</c> rather than a join, so it
    /// can only add a column and never multiply or drop a row.
    /// </remarks>
    private static void BuildListCommand(
        DbCommand cmd,
        string? query,
        Guid? sessionId,
        MemoryScope scope,
        int limit,
        int offset,
        bool includeEmbeddingProbe)
    {
        StringBuilder sql = new(
            """
            SELECT m."Id", m."Content", m."CreatedAt", m."SessionId", m."Tags", m."Source",
                   p.SessionId, p.AttachmentId, p.LogicalKey, p.Version,
                   p.ContentHash, p.MaterializedAt, p.SourceType,
                   EXISTS(
                       SELECT 1 FROM "SessionAttachments" a
                       WHERE a."Id" = p.AttachmentId AND a."State" = 'Bound'
                   ),
                   m.ScopeKindCode, m.CampaignId, m."RetiredAtUtc", m."PinnedAtUtc"
            """);

        if (includeEmbeddingProbe)
        {
            sql.Append(
                """
                ,
                       EXISTS(SELECT 1 FROM "saga_memory_embeddings" e WHERE e."MemoryId" = m."Id")
                """);
        }

        sql.Append(
            """

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

        // The same ownership predicate retrieval ranks by, so this never shows a memory a turn in this
        // scope could not own. The converse does not follow, in either direction. The SessionId filter
        // above narrows further, to what one Session wrote, so a sibling Session's memory in the same
        // Campaign is ranked by that turn and is still not listed beside it. And there is no join to the
        // embeddings and no predicate over RetiredAtUtc here, so a retired memory lists exactly as a live
        // one does while no turn can recall it. That second one is deliberate -- retirement's promise is
        // about retrieval, and an operator has to be able to see what they took out in order to put it
        // back. Each row carries RetiredAtUtc and PinnedAtUtc, so the listing can say which it is.
        if (scope.IsEnforced)
        {
            if (scope.CampaignId is { } campaignId)
            {
                sql.Append(
                    " AND (m.ScopeKindCode = @globalScopeKind"
                    + " OR (m.ScopeKindCode = @campaignScopeKind AND m.CampaignId = @campaignId))");

                AddParameter(cmd, "@campaignScopeKind", (int)SagaMemoryScopeKind.Campaign);

                // Canonical, exactly as DivinationService binds it: the listing and retrieval have to
                // select the same candidate set, so a spelling that halved one would have to halve the
                // other or the promise above this block is false.
                AddParameter(cmd, "@campaignId", campaignId.ToString("D").ToUpperInvariant());
            }
            else
            {
                sql.Append(" AND m.ScopeKindCode = @globalScopeKind");
            }

            AddParameter(cmd, "@globalScopeKind", (int)SagaMemoryScopeKind.Global);
        }

        // The identity breaks a tie, so the order is total and is the one ListPositionsAfterAsync walks. A
        // page boundary inside memories that share a CreatedAt would otherwise fall wherever the scan
        // happened to leave them, which is a property of how the rows are laid out and not of the data.
        sql.Append(" ORDER BY m.\"CreatedAt\" DESC, m.\"Id\" DESC LIMIT @limit OFFSET @offset");

        AddParameter(cmd, "@limit", limit);

        AddParameter(cmd, "@offset", offset);

        cmd.CommandText = sql.ToString();
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
                           ),
                           m.ScopeKindCode, m.CampaignId, m."RetiredAtUtc", m."PinnedAtUtc"
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
        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = BeginWriteTransaction(connection);

                // The guard, not the purge. A caller that reached this method without going through the
                // sensitivity purge boundary would remove a labelled Saga fact and leave its label behind,
                // pointing at content nothing admits is tainted (§10.20.2). Asked here, after the write
                // lock is taken, so no label can be committed between the answer and the delete below.
                if (labeledArtifactGuard is { } guard && Guid.TryParse(id, out Guid memoryId))
                {
                    Result unlabeled = await guard
                        .EnsureUnlabeledAsync(SensitiveArtifactKind.Saga, memoryId, transaction, cancellationToken)
                        .ConfigureAwait(false);

                    if (unlabeled.IsFailure)
                    {
                        throw new InvalidOperationException(unlabeled.Error.Message);
                    }
                }

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

                // Whatever the accelerator flag says: a mirror an earlier build filled still holds this
                // memory's embedding, and a delete that skipped it would leave that content behind.
                _ = await SagaVectorMirror.DeleteAsync(connection, transaction, id, cancellationToken)
                    .ConfigureAwait(false);

                // Deliberately ungated. A claim written while the Annals was enabled has to stay
                // removable after it is disabled, or turning the feature off would strand records no
                // surface can reach and no reset can clear.
                await AnnalsClaimWriter.DeleteClaimsForSubjectAsync(
                    connection,
                    transaction,
                    AnnalSubjectStore.Saga,
                    id,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = BeginWriteTransaction(connection);

                // A set-based delete examines no identity at all, so there is no single artifact to ask
                // about. The only honest question is whether the kind still has a labelled member
                // anywhere, and the only safe answer for "yes" is to refuse rather than remove rows
                // nothing ever examined. Asked here, after the write lock is taken, so no label can be
                // committed between the answer and the deletes below.
                if (labeledArtifactGuard is { } guard)
                {
                    Result none = await guard
                        .EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, transaction, cancellationToken)
                        .ConfigureAwait(false);

                    if (none.IsFailure)
                    {
                        throw new InvalidOperationException(none.Error.Message);
                    }
                }

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

                // Whatever the accelerator flag says, for the same reason DeleteAsync's mirror delete is.
                _ = await SagaVectorMirror.DeleteAllAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);

                // Saga's claims and no others. The Lexicon's stay exactly where they are, which is what
                // makes a store-scoped reset mean what the operator asked for.
                await AnnalsClaimWriter.DeleteClaimsForStoreAsync(
                    connection,
                    transaction,
                    AnnalSubjectStore.Saga,
                    cancellationToken).ConfigureAwait(false);

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
                    : UtcInstantText.Parse(reader.GetString(2));

                DateTimeOffset? newest = reader.IsDBNull(3)
                    ? null
                    : UtcInstantText.Parse(reader.GetString(3));

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

                return UtcInstantText.Parse((string)result);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SagaExtractionCursor?> GetExtractionCursorAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        return await SqliteBusyRetry.ExecuteAsync<SagaExtractionCursor?>(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "LastExtractedEntrySequence", "LastExtractedEntryCreatedAt"
                    FROM "saga_extraction_watermarks"
                    WHERE "SessionId" = @sessionId
                    LIMIT 1
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                await using DbDataReader reader =
                    await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return new SagaExtractionCursor(
                    reader.GetInt64(0),
                    UtcInstantText.Parse(reader.GetString(1)));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task SetExtractionCursorAsync(
        Guid sessionId,
        SagaExtractionCursor cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        ArgumentOutOfRangeException.ThrowIfNegative(cursor.EntrySequence);

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await UpsertExtractionCursorAsync(
                    connection,
                    sessionId,
                    cursor,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task SetWatermarkAsync(Guid sessionId, DateTimeOffset lastExtractedEntryCreatedAt, CancellationToken cancellationToken)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                long sequence = await SagaExtractionCursorResolver.ResolveContiguousPrefixAsync(
                    connection,
                    transaction: null,
                    sessionId.ToString(),
                    lastExtractedEntryCreatedAt,
                    cancellationToken).ConfigureAwait(false);

                await UpsertExtractionCursorAsync(
                    connection,
                    sessionId,
                    new SagaExtractionCursor(sequence, lastExtractedEntryCreatedAt),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Opens a write transaction that takes the write lock at once rather than at its first write.
    /// </summary>
    /// <remarks>
    /// A delete that asks the labelled-artifact guard has to ask while it already holds the lock. A
    /// deferred transaction would take it only at the first delete, leaving the answer and the delete
    /// to be separated by a label committed in between.
    /// </remarks>
    private static SqliteTransaction BeginWriteTransaction(DbConnection connection) =>
        ((SqliteConnection)connection).BeginTransaction(deferred: false);

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task UpsertExtractionCursorAsync(
        DbConnection connection,
        Guid sessionId,
        SagaExtractionCursor cursor,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            INSERT INTO "saga_extraction_watermarks"
                ("SessionId", "LastExtractedEntryCreatedAt", "LastExtractedEntrySequence")
            VALUES (@sessionId, @lastExtractedEntryCreatedAt, @lastExtractedEntrySequence)
            ON CONFLICT("SessionId") DO UPDATE SET
                "LastExtractedEntryCreatedAt" = @lastExtractedEntryCreatedAt,
                "LastExtractedEntrySequence" = @lastExtractedEntrySequence
            """;

        AddParameter(cmd, "@sessionId", sessionId.ToString());

        AddParameter(
            cmd,
            "@lastExtractedEntryCreatedAt",
            UtcInstantText.Format(cursor.EntryCreatedAt));

        AddParameter(cmd, "@lastExtractedEntrySequence", cursor.EntrySequence);

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

        DateTimeOffset createdAt = UtcInstantText.Parse(reader.GetString(2));

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
                UtcInstantText.Parse(reader.GetString(11)),
                reader.GetString(12),
                reader.GetInt32(13) == 1
                    ? AttachmentSourceAvailability.Available
                    : AttachmentSourceAvailability.Unavailable);

        DateTimeOffset? retiredAtUtc = reader.IsDBNull(16)
            ? null
            : UtcInstantText.Parse(reader.GetString(16));

        DateTimeOffset? pinnedAtUtc = reader.IsDBNull(17)
            ? null
            : UtcInstantText.Parse(reader.GetString(17));

        return new SagaMemoryDto(
            id,
            content,
            createdAt,
            sessionId,
            tags,
            source,
            provenance,
            (SagaMemoryScopeKind)reader.GetInt32(14),
            reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
            retiredAtUtc,
            pinnedAtUtc);
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

        AddParameter(command, "@attachmentId", provenance.AttachmentId.ToString().ToUpperInvariant());

        AddParameter(command, "@logicalKey", provenance.LogicalKey);

        AddParameter(command, "@version", provenance.Version);

        AddParameter(command, "@contentHash", provenance.ContentHash);

        AddParameter(
            command,
            "@materializedAt",
            UtcInstantText.Format(provenance.MaterializedAt));

        AddParameter(command, "@sourceType", provenance.SourceType);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
