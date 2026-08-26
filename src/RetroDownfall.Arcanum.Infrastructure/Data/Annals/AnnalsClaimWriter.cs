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

    /// <summary>
    /// Ends a claim: appends a tombstone version that supersedes the head and moves the head to it.
    /// </summary>
    /// <remarks>
    /// Binds to no content by construction -- <see cref="InsertVersionAsync"/> is called with a null
    /// hash, which is the only value <c>annal_versions</c> permits for a <see cref="AnnalOperation.Retire"/>
    /// row, and there is no <c>contentHash</c> parameter here for a caller to be tempted to fill in.
    ///
    /// <para>Two refusals, both silent. A subject with no claim gets none here: opening one would mean
    /// guessing an origin for a version this method never saw asserted, and that guess belongs to the
    /// caller, not to this method. The Saga retirement path opens the claim itself first, with
    /// <see cref="AnnalOrigin.AgentExtracted"/> and the memory's own content, precisely so the history
    /// reads "extraction asserted this, then the operator ended it" rather than a warrant nobody
    /// held.</para>
    ///
    /// <para>A head already at <see cref="AnnalOperation.Retire"/> gets no second tombstone, because
    /// retiring an already-retired claim records no change.</para>
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> without writing when the subject has no claim, or when its head is
    /// already a retirement.
    /// </returns>
    internal static async Task<bool> AppendRetirementAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalSubjectStore subjectStore,
        string subjectId,
        AnnalOrigin origin,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        ContentSensitivity sensitivity,
        DateTimeOffset validFrom,
        DateTimeOffset recordedAt,
        Guid? sourceSessionId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        HeadRow? head = await ReadHeadAsync(connection, transaction, subjectStore, subjectId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null)
        {

            return false;

        }

        if (head.CurrentOperation == AnnalOperation.Retire)
        {

            return false;

        }

        int revision = head.CurrentRevision + 1;

        string versionId = await InsertVersionAsync(
            connection,
            transaction,
            head.ClaimId,
            revision,
            AnnalOperation.Retire,
            origin,
            scopeKind,
            campaignId,
            sensitivity,
            contentHash: null,
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
            ("@operationCode", (int)AnnalOperation.Retire),
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

        await DeleteInOrderAsync(
            connection,
            transaction,
            AnnalsErasurePlan.ForSubjectQuery(subjectStore, "SELECT @subjectId"),
            cancellationToken,
            ("@subjectId", subjectId));

    }

    /// <summary>
    /// Removes the claims over every subject row a query selects, for a caller that knows its subjects
    /// by a predicate rather than by id.
    /// </summary>
    /// <param name="subjectIdQuery">
    /// A code-owned <c>SELECT</c> of subject ids, never anything a caller supplied. Its parameters are
    /// bound from <paramref name="parameters"/>.
    /// </param>
    internal static async Task DeleteClaimsForSubjectQueryAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalSubjectStore subjectStore,
        string subjectIdQuery,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(subjectIdQuery);

        await DeleteInOrderAsync(
            connection,
            transaction,
            AnnalsErasurePlan.ForSubjectQuery(subjectStore, subjectIdQuery),
            cancellationToken,
            parameters);

    }

    /// <summary>Removes every claim belonging to one store, leaving the other store's untouched.</summary>
    internal static async Task DeleteClaimsForStoreAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalSubjectStore subjectStore,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await DeleteInOrderAsync(
            connection,
            transaction,
            AnnalsErasurePlan.ForStore(subjectStore),
            cancellationToken);

    }

    /// <summary>
    /// Runs one erasure plan's steps in the order it states, which is the order foreign keys require.
    /// </summary>
    /// <remarks>
    /// The order and the predicates belong to <see cref="AnnalsErasurePlan"/> rather than to this
    /// method, because the memory-reset executor runs the same erasure and two statements of it would
    /// eventually disagree about which rows an erasure owns.
    /// </remarks>
    private static async Task DeleteInOrderAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<AnnalsErasureStep> steps,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {

        foreach (AnnalsErasureStep step in steps)
        {

            await ExecuteAsync(
                connection,
                transaction,
                cancellationToken,
                $"DELETE FROM {step.Table} WHERE {step.Predicate}",
                parameters);

        }

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
        byte[]? contentHash,
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
            SELECT head.ClaimId, head.CurrentVersionId, head.CurrentRevision, head.CurrentOperationCode,
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
            (AnnalOperation)reader.GetInt32(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5));

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
        AnnalOperation CurrentOperation,
        long CurrentSequence,
        byte[]? ContentHash);

}
