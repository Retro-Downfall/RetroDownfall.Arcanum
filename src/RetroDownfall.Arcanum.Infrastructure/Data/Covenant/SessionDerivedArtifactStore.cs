using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The shared replacement protocol behind the Session summary and title stores.
/// </summary>
/// <remarks>
/// Both columns have the same problem: they are mutable, EF owns them, and issue #74 cannot
/// <c>ALTER TABLE</c> them. The answer for both is an immutable artifact row carrying the exact
/// content digest, a current pointer with a composite foreign key back to it, and a sensitivity
/// label bound to that revision. One implementation, because two would eventually disagree about
/// the order in which the pointer, the column, and the label move (§10.12).
///
/// <para>The order inside the transaction is deliberate: insert the new artifact, move the pointer,
/// write the column, label the new revision, and only then delete the prior artifact and its label
/// under the replacement authorization. Deleting first would leave a window in which the pointer's
/// foreign key had nothing to name, and labelling last means a label failure aborts before any
/// evidence has been destroyed.</para>
///
/// <para>Every one of those steps names the Session, and this type used to spell that identity
/// uppercase for itself. Both artifact tables and both pointer tables carry
/// <c>REFERENCES "Sessions" ("Id")</c>, and a Session created by protected transfer or backup import
/// holds a lowercase identity there — so for such a Session the first insert aborted on the foreign
/// key and no summary or title could be written at all. The Session's own spelling is resolved once
/// per replacement and bound to every step, which keeps all four comparisons exact and indexed.</para>
/// </remarks>
internal sealed class SessionDerivedArtifactStore(
    ICovenantConnectionSource connections,
    ICovenantSqliteConnectionInitializer initializer)
    : ISessionSummaryArtifactStore, ISessionTitleArtifactStore
{

    public Task<Result<SessionDerivedArtifactWriteReceipt>> ReplaceAsync(
        SessionSummaryArtifactWrite request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        return ReplaceCoreAsync(
            new ReplacementPlan(
                request.SessionId,
                SensitiveArtifactKind.Summary,
                "session_summary_artifacts",
                "session_summary_state",
                """
                UPDATE "Sessions"
                SET "Summary" = $content, "LastSummarizedMessageAt" = $watermark
                WHERE "Id" = $sessionId;
                """,
                request.Summary,
                request.SummarizedThroughUtc,
                request.Sensitivity,
                request.Provenance,
                request.CampaignId,
                request.TurnId,
                request.ProducingPlanDigest,
                request.ProducingAdmissionDigest,
                request.ProducingMaintenanceReceiptDigest),
            cancellationToken);

    }

    public Task<Result<SessionDerivedArtifactWriteReceipt>> ReplaceAsync(
        SessionTitleArtifactWrite request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        return ReplaceCoreAsync(
            new ReplacementPlan(
                request.SessionId,
                SensitiveArtifactKind.SessionTitle,
                "session_title_artifacts",
                "session_title_state",
                """
                UPDATE "Sessions" SET "Title" = $content WHERE "Id" = $sessionId;
                """,
                request.Title,
                SummarizedThroughUtc: null,
                request.Sensitivity,
                request.Provenance,
                request.CampaignId,
                request.TurnId,
                request.ProducingPlanDigest,
                request.ProducingAdmissionDigest,
                request.ProducingMaintenanceReceiptDigest),
            cancellationToken);

    }

    private async Task<Result<SessionDerivedArtifactWriteReceipt>> ReplaceCoreAsync(
        ReplacementPlan plan,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        try
        {

            // The identity the Session row actually holds, resolved once and used by every step below.
            // Falls back to this store's own spelling when no Session resolves, so a replacement for a
            // Session that does not exist still fails its foreign key rather than being rewritten into
            // a form that would not have resolved either.
            string sessionKey = await CovenantIdentitySql
                .ResolveStoredSessionIdAsync(connection, transaction, plan.SessionId, cancellationToken)
                .ConfigureAwait(false)
                ?? Format(plan.SessionId);

            (Guid? priorArtifactId, long priorRevision) = await ReadCurrentAsync(
                connection,
                transaction,
                plan,
                sessionKey,
                cancellationToken).ConfigureAwait(false);

            Guid artifactId = Guid.NewGuid();

            long revision = checked(priorRevision + 1);

            CovenantDigest contentDigest = DerivedArtifactContentDigest.ForText(plan.Content);

            await InsertArtifactAsync(
                connection,
                transaction,
                plan,
                sessionKey,
                artifactId,
                revision,
                contentDigest,
                cancellationToken).ConfigureAwait(false);

            await MovePointerAsync(
                connection,
                transaction,
                plan,
                sessionKey,
                artifactId,
                revision,
                cancellationToken).ConfigureAwait(false);

            await WriteColumnAsync(connection, transaction, plan, sessionKey, cancellationToken)
                .ConfigureAwait(false);

            Result<LabeledArtifactWriteReceipt> labelled = await ArtifactSensitivityLedger.WriteWithinAsync(
                connection,
                transaction,
                new DerivedArtifactWrite(
                    plan.ArtifactKind,
                    artifactId,
                    plan.SessionId,
                    plan.CampaignId,
                    plan.TurnId,
                    checked((ulong)revision),
                    contentDigest,
                    plan.Sensitivity,
                    plan.Provenance,
                    plan.ProducingPlanDigest,
                    plan.ProducingAdmissionDigest,
                    plan.ProducingMaintenanceReceiptDigest),
                cancellationToken).ConfigureAwait(false);

            if (labelled.IsFailure)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return labelled.Error;

            }

            if (priorArtifactId is { } prior)
            {

                await RetirePriorAsync(connection, transaction, plan, prior, cancellationToken)
                    .ConfigureAwait(false);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new SessionDerivedArtifactWriteReceipt(
                artifactId,
                revision,
                labelled.Value.Sensitivity,
                labelled.Value.LabelId);

        }
        catch (SqliteException exception)
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            return new Error(
                ErrorCodes.Grimoire.WriteFailed,
                $"The Session derived artifact could not be replaced ({exception.SqliteErrorCode}).");

        }

    }

    private static async Task<(Guid? ArtifactId, long Revision)> ReadCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReplacementPlan plan,
        string sessionKey,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"SELECT CurrentArtifactId, Revision FROM {plan.StateTable} WHERE SessionId = $sessionId;";

        _ = command.Parameters.AddWithValue("$sessionId", sessionKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (Guid.Parse(reader.GetString(0)), reader.GetInt64(1))
            : (null, 0);

    }

    private static async Task InsertArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReplacementPlan plan,
        string sessionKey,
        Guid artifactId,
        long revision,
        CovenantDigest contentDigest,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // The two artifact tables differ by exactly one column, and the table name comes from this
        // type's own closed literal set rather than from a caller.
        command.CommandText = plan.ArtifactKind is SensitiveArtifactKind.Summary
            ? """
                INSERT INTO session_summary_artifacts (
                    ArtifactId, SessionId, Revision, ContentDigest,
                    SensitivityCode, SensitivityDigest, SummarizedThroughUtc, CreatedAtUtc)
                VALUES ($artifactId, $sessionId, $revision, $contentDigest,
                    $sensitivityCode, $sensitivityDigest, $watermark, $createdAtUtc);
                """
            : """
                INSERT INTO session_title_artifacts (
                    ArtifactId, SessionId, Revision, ContentDigest,
                    SensitivityCode, SensitivityDigest, CreatedAtUtc)
                VALUES ($artifactId, $sessionId, $revision, $contentDigest,
                    $sensitivityCode, $sensitivityDigest, $createdAtUtc);
                """;

        _ = command.Parameters.AddWithValue("$artifactId", Format(artifactId));

        _ = command.Parameters.AddWithValue("$sessionId", sessionKey);

        _ = command.Parameters.AddWithValue("$revision", revision);

        _ = command.Parameters.AddWithValue("$contentDigest", contentDigest.Bytes);

        _ = command.Parameters.AddWithValue("$sensitivityCode", (long)plan.Sensitivity);

        _ = command.Parameters.AddWithValue(
            "$sensitivityDigest",
            CovenantDigests.Sensitivity(plan.Provenance.ToDigestInput(plan.Sensitivity)).Bytes);

        if (plan.ArtifactKind is SensitiveArtifactKind.Summary)
        {

            _ = command.Parameters.AddWithValue(
                "$watermark",
                plan.SummarizedThroughUtc is { } watermark
                    ? watermark.ToString("o", CultureInfo.InvariantCulture)
                    : DBNull.Value);

        }

        _ = command.Parameters.AddWithValue("$createdAtUtc", Now());

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task MovePointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReplacementPlan plan,
        string sessionKey,
        Guid artifactId,
        long revision,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"""
            INSERT INTO {plan.StateTable} (SessionId, CurrentArtifactId, Revision, UpdatedAtUtc)
            VALUES ($sessionId, $artifactId, $revision, $updatedAtUtc)
            ON CONFLICT(SessionId) DO UPDATE SET
                CurrentArtifactId = excluded.CurrentArtifactId,
                Revision = excluded.Revision,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        _ = command.Parameters.AddWithValue("$sessionId", sessionKey);

        _ = command.Parameters.AddWithValue("$artifactId", Format(artifactId));

        _ = command.Parameters.AddWithValue("$revision", revision);

        _ = command.Parameters.AddWithValue("$updatedAtUtc", Now());

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task WriteColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReplacementPlan plan,
        string sessionKey,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = plan.ColumnUpdateSql;

        _ = command.Parameters.AddWithValue("$sessionId", sessionKey);

        _ = command.Parameters.AddWithValue("$content", (object?)plan.Content ?? DBNull.Value);

        if (plan.ArtifactKind is SensitiveArtifactKind.Summary)
        {

            _ = command.Parameters.AddWithValue(
                "$watermark",
                plan.SummarizedThroughUtc is { } watermark
                    ? watermark.ToString("o", CultureInfo.InvariantCulture)
                    : DBNull.Value);

        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Removes the superseded artifact and any label it carried, under one scoped authorization.
    /// </summary>
    /// <remarks>
    /// The label goes first. The artifact row is what a label's owner index points at, and deleting
    /// the artifact while its label survived would leave evidence describing a revision that no
    /// longer exists — which reads to a purge as an artifact it can never find.
    /// </remarks>
    private async Task RetirePriorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReplacementPlan plan,
        Guid priorArtifactId,
        CancellationToken cancellationToken)
    {

        using CovenantSqliteAuthorizationScope replacement = initializer.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.ArtifactReplacement);

        await using (SqliteCommand label = connection.CreateCommand())
        {

            label.Transaction = transaction;

            label.CommandText = """
                DELETE FROM artifact_sensitivity
                WHERE ArtifactKindCode = $kind AND ArtifactId = $artifactId;
                """;

            _ = label.Parameters.AddWithValue("$kind", (long)plan.ArtifactKind);

            _ = label.Parameters.AddWithValue("$artifactId", Format(priorArtifactId));

            _ = await label.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        await using SqliteCommand artifact = connection.CreateCommand();

        artifact.Transaction = transaction;

        artifact.CommandText = $"DELETE FROM {plan.ArtifactTable} WHERE ArtifactId = $artifactId;";

        _ = artifact.Parameters.AddWithValue("$artifactId", Format(priorArtifactId));

        _ = await artifact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static string Format(Guid value) => value.ToString().ToUpperInvariant();

    /// <summary>The closed set of facts one replacement needs, shared by both artifact kinds.</summary>
    private sealed record ReplacementPlan(
        Guid SessionId,
        SensitiveArtifactKind ArtifactKind,
        string ArtifactTable,
        string StateTable,
        string ColumnUpdateSql,
        string? Content,
        DateTimeOffset? SummarizedThroughUtc,
        ContentSensitivity Sensitivity,
        GenerationProvenance Provenance,
        Guid? CampaignId,
        Guid? TurnId,
        CovenantDigest? ProducingPlanDigest,
        CovenantDigest? ProducingAdmissionDigest,
        CovenantDigest? ProducingMaintenanceReceiptDigest);

}
