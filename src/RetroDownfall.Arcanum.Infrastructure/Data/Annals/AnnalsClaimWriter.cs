using System.Data.Common;

using System.Globalization;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Annals;

/// <summary>
/// The single producer of Annals rows, shared by the Saga store, the Lexicon service, and the version
/// step's backfill.
/// </summary>
/// <remarks>
/// One writer rather than three. Three would be three ideas of what a revision means, and a claim
/// written by the live path would eventually disagree with one written by the sweep about the same
/// question — which is the failure mode where two measurements of one quantity land on the operator.
///
/// <para>Every method takes the caller's live connection and transaction rather than opening its own,
/// mirroring <see cref="SagaMemoryScopeClassifier"/>. A claim therefore commits or rolls back with the
/// memory it describes, and no second transaction can interleave between them.</para>
/// </remarks>
internal static class AnnalsClaimWriter
{

    /// <summary>The one timestamp format the Grimoire stores, and the one the validity check orders by.</summary>
    private const string TimestampFormat = "o";

    /// <summary>
    /// Opens a claim at revision one and points its head at it.
    /// </summary>
    /// <param name="transaction">
    /// The caller's transaction, or <see langword="null"/> when the caller drives its transaction with
    /// raw <c>BEGIN IMMEDIATE</c> text and has no object to hand over. Null therefore means "already
    /// inside one", which is the opposite of its ordinary reading: the commands run on the caller's own
    /// connection either way, so they are inside whatever transaction that connection has open.
    /// </param>
    /// <returns>
    /// <see langword="false"/> without writing when the subject row already has a claim, which is what
    /// makes the upgrade sweep idempotent.
    /// </returns>
    internal static async Task<bool> AppendAssertAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalSubjectStore subjectStore,
        string subjectId,
        AnnalOrigin origin,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        ContentSensitivity sensitivity,
        byte[] contentHash,
        DateTimeOffset validFrom,
        DateTimeOffset recordedAt,
        Guid? sourceSessionId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        ArgumentNullException.ThrowIfNull(contentHash);

        if (await ReadHeadAsync(connection, transaction, subjectStore, subjectId, cancellationToken)
                .ConfigureAwait(false) is not null)
        {

            return false;

        }

        string claimId = Guid.NewGuid().ToString();

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            """
            INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
            VALUES (@claimId, @storeCode, @subjectId, @createdAt)
            """,
            ("@claimId", claimId),
            ("@storeCode", (int)subjectStore),
            ("@subjectId", subjectId),
            ("@createdAt", Format(recordedAt)));

        string versionId = await InsertVersionAsync(
            connection,
            transaction,
            claimId,
            revision: 1,
            AnnalOperation.Assert,
            origin,
            scopeKind,
            campaignId,
            sensitivity,
            contentHash,
            validFrom,
            recordedAt,
            predecessorVersionId: null,
            sourceSessionId,
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            """
            INSERT INTO annal_heads (
                ClaimId, SubjectStoreCode, CurrentVersionId, CurrentRevision, CurrentOperationCode, UpdatedAtUtc)
            VALUES (@claimId, @storeCode, @versionId, 1, @operationCode, @updatedAt)
            """,
            ("@claimId", claimId),
            ("@storeCode", (int)subjectStore),
            ("@versionId", versionId),
            ("@operationCode", (int)AnnalOperation.Assert),
            ("@updatedAt", Format(recordedAt)));

        return true;

    }

    /// <summary>
    /// Restates a claim whose content has changed, or opens one when the subject has none yet.
    /// </summary>
    /// <remarks>
    /// The content-hash comparison is what keeps a correction deterministic. A Lexicon upsert merges
    /// incoming facts with existing ones, and a merge that adds nothing new produces an unchanged fact
    /// set; without the comparison every repeated <c>scribe_lexicon</c> call restating a known fact would
    /// append a revision recording no change, and a claim's history would fill with noise that a reader
    /// could not tell from real corrections.
    /// </remarks>
    /// <returns><see langword="false"/> when the content is unchanged and nothing was written.</returns>
    internal static async Task<bool> AppendCorrectionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalSubjectStore subjectStore,
        string subjectId,
        AnnalOrigin origin,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        ContentSensitivity sensitivity,
        byte[] contentHash,
        DateTimeOffset validFrom,
        DateTimeOffset recordedAt,
        Guid? sourceSessionId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        ArgumentNullException.ThrowIfNull(contentHash);

        HeadRow? head = await ReadHeadAsync(connection, transaction, subjectStore, subjectId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null)
        {

            return await AppendAssertAsync(
                connection,
                transaction,
                subjectStore,
                subjectId,
                origin,
                scopeKind,
                campaignId,
                sensitivity,
                contentHash,
                validFrom,
                recordedAt,
                sourceSessionId,
                cancellationToken).ConfigureAwait(false);

        }

        if (head.ContentHash is byte[] current && current.AsSpan().SequenceEqual(contentHash))
        {

            return false;

        }

        int revision = head.CurrentRevision + 1;

        string versionId = await InsertVersionAsync(
            connection,
            transaction,
            head.ClaimId,
            revision,
            AnnalOperation.Correct,
            origin,
            scopeKind,
            campaignId,
            sensitivity,
            contentHash,
            validFrom,
            recordedAt,
            head.CurrentVersionId,
            sourceSessionId,
            cancellationToken).ConfigureAwait(false);

        long dependentSequence = await ReadSequenceAsync(connection, transaction, versionId, cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            """
            INSERT INTO annal_dependencies (
                DependentVersionId, DependentSequence, DependencyVersionId, DependencySequence,
                RelationCode, Ordinal, CreatedAtUtc)
            VALUES (@dependent, @dependentSequence, @dependency, @dependencySequence, @relationCode, 1, @createdAt)
            """,
            ("@dependent", versionId),
            ("@dependentSequence", dependentSequence),
            ("@dependency", head.CurrentVersionId),
            ("@dependencySequence", head.CurrentSequence),
            ("@relationCode", (int)AnnalDependencyRelation.Supersedes),
            ("@createdAt", Format(recordedAt)));

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            """
            UPDATE annal_heads
            SET CurrentVersionId = @versionId,
                CurrentRevision = @revision,
                CurrentOperationCode = @operationCode,
                UpdatedAtUtc = @updatedAt
            WHERE ClaimId = @claimId
            """,
            ("@versionId", versionId),
            ("@revision", revision),
            ("@operationCode", (int)AnnalOperation.Correct),
            ("@updatedAt", Format(recordedAt)),
            ("@claimId", head.ClaimId));

        return true;

    }

    /// <summary>Removes the claim describing one durable row, with every version and edge it owns.</summary>
    internal static async Task DeleteClaimsForSubjectAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalSubjectStore subjectStore,
        string subjectId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        const string ClaimScope =
            "SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = @storeCode AND SubjectId = @subjectId";

        await DeleteInOrderAsync(
            connection,
            transaction,
            ClaimScope,
            "SubjectStoreCode = @storeCode AND SubjectId = @subjectId",
            cancellationToken,
            ("@storeCode", (int)subjectStore),
            ("@subjectId", subjectId));

    }

    /// <summary>Removes every claim belonging to one store, leaving the other store's untouched.</summary>
    internal static async Task DeleteClaimsForStoreAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalSubjectStore subjectStore,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        const string ClaimScope = "SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = @storeCode";

        await DeleteInOrderAsync(
            connection,
            transaction,
            ClaimScope,
            "SubjectStoreCode = @storeCode",
            cancellationToken,
            ("@storeCode", (int)subjectStore));

    }

    /// <summary>
    /// The four statements every erasure runs, in the one order that works.
    /// </summary>
    /// <remarks>
    /// SQLite enforces an immediate foreign key as each row is deleted rather than at the end of the
    /// statement, so a head must release its version before the version may go and a version must go
    /// before its claim. Edges would cascade, and are deleted explicitly anyway: <c>SagaMemoryStore</c>
    /// already deletes <c>saga_memory_attachment_provenance</c> explicitly although that table declares
    /// <c>ON DELETE CASCADE</c>, and an erasure the operator asked for is the wrong place to depend on a
    /// pragma being what it is expected to be.
    ///
    /// <para>The edge delete names <b>both</b> endpoint columns, because an edge dies when either end
    /// does: a claim being erased may be the target of an edge asserted by a version that survives, and
    /// leaving that edge would leave a dependency pointing at nothing.</para>
    /// </remarks>
    private static async Task DeleteInOrderAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string claimScopeQuery,
        string claimPredicate,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {

        string versionScope = $"SELECT VersionId FROM annal_versions WHERE ClaimId IN ({claimScopeQuery})";

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            $"""
            DELETE FROM annal_dependencies
            WHERE DependentVersionId IN ({versionScope})
               OR DependencyVersionId IN ({versionScope})
            """,
            parameters);

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            $"DELETE FROM annal_heads WHERE ClaimId IN ({claimScopeQuery})",
            parameters);

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            $"DELETE FROM annal_versions WHERE ClaimId IN ({claimScopeQuery})",
            parameters);

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            $"DELETE FROM annal_claims WHERE {claimPredicate}",
            parameters);

    }

    private static async Task<string> InsertVersionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string claimId,
        int revision,
        AnnalOperation operation,
        AnnalOrigin origin,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        ContentSensitivity sensitivity,
        byte[] contentHash,
        DateTimeOffset validFrom,
        DateTimeOffset recordedAt,
        string? predecessorVersionId,
        Guid? sourceSessionId,
        CancellationToken cancellationToken)
    {

        string versionId = Guid.NewGuid().ToString();

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            """
            INSERT INTO annal_versions (
                VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, CampaignId,
                SensitivityCode, ContentHash, ValidFromUtc, ValidToUtc, RecordedAtUtc,
                PredecessorVersionId, SourceSessionId)
            VALUES (
                @versionId, @claimId, @revision, @operationCode, @originCode, @scopeKindCode, @campaignId,
                @sensitivityCode, @contentHash, @validFrom, NULL, @recordedAt,
                @predecessor, @sourceSessionId)
            """,
            ("@versionId", versionId),
            ("@claimId", claimId),
            ("@revision", revision),
            ("@operationCode", (int)operation),
            ("@originCode", (int)origin),
            ("@scopeKindCode", (int)scopeKind),
            ("@campaignId", campaignId),
            ("@sensitivityCode", (int)sensitivity),
            ("@contentHash", contentHash),
            ("@validFrom", Format(validFrom)),
            ("@recordedAt", Format(recordedAt)),
            ("@predecessor", predecessorVersionId),
            // The column is TEXT. Binding the Guid itself would store a BLOB no later read would match.
            ("@sourceSessionId", sourceSessionId?.ToString()));

        return versionId;

    }

    private static async Task<HeadRow?> ReadHeadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalSubjectStore subjectStore,
        string subjectId,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT head.ClaimId, head.CurrentVersionId, head.CurrentRevision,
                   version.Sequence, version.ContentHash
            FROM annal_heads AS head
            JOIN annal_claims AS claim ON claim.ClaimId = head.ClaimId
            JOIN annal_versions AS version ON version.VersionId = head.CurrentVersionId
            WHERE claim.SubjectStoreCode = @storeCode AND claim.SubjectId = @subjectId
            """;

        AddParameter(command, "@storeCode", (int)subjectStore);

        AddParameter(command, "@subjectId", subjectId);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        return new HeadRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4));

    }

    private static async Task<long> ReadSequenceAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string versionId,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT Sequence FROM annal_versions WHERE VersionId = @versionId";

        AddParameter(command, "@versionId", versionId);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = commandText;

        foreach ((string name, object? value) in parameters)
        {

            AddParameter(command, name, value);

        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value ?? DBNull.Value;

        _ = command.Parameters.Add(parameter);

    }

    private static string Format(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>A claim's head joined to the version it points at, which is all any writer needs.</summary>
    private sealed record HeadRow(
        string ClaimId,
        string CurrentVersionId,
        int CurrentRevision,
        long CurrentSequence,
        byte[]? ContentHash);

}
