using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed record BackupSessionImportResult(
    long Sessions,
    long Entries,
    long Attachments,
    long RemappedIds,
    long DeduplicatedBlobs,
    BackupVerifyIssue[] Issues)
{

    public static BackupSessionImportResult Failed(BackupVerifyIssue issue) =>
        new(0, 0, 0, 0, 0, [issue]);

}

/// <summary>
/// Merges chosen Sessions from a restored snapshot into the live installation.
/// </summary>
/// <remarks>
/// This is explicitly not a restore: nothing is replaced, and the destination keeps its own
/// configuration, secrets, and every Session it already had. Colliding primary keys are remapped
/// rather than overwritten, foreign keys follow the remap, and attachment payloads that already
/// exist byte-for-byte are deduplicated instead of copied again. The whole merge runs in one
/// destination transaction, so a failure imports nothing. Payload bytes are the one part no
/// transaction covers — they are copied into the live attachment tree before the commit — so they
/// are unwound explicitly on every path that does not reach a successful commit, cancellation
/// included.
/// </remarks>
internal static class BackupSessionImporter
{

    public static async Task<BackupSessionImportResult> ImportAsync(
        string sourceDatabasePath,
        string destinationDatabasePath,
        IReadOnlyList<Guid> sessionIds,
        string sourceAttachmentsRoot,
        string destinationAttachmentsRoot,
        string destinationGrimoireSecret,
        string sourceGrimoireSecret,
        CancellationToken cancellationToken,
        Action? beforeCommitForTests = null)
    {

        if (!File.Exists(sourceDatabasePath) || sourceGrimoireSecret.Length == 0)
        {

            return BackupSessionImportResult.Failed(
                new BackupVerifyIssue(
                    "backup.restore_import_source_unavailable",
                    "The archive does not contain a readable Grimoire snapshot to import from."));

        }

        await using SqliteConnection source = await BackupRestoreDatabaseWorker
            .OpenAsync(sourceDatabasePath, sourceGrimoireSecret, readOnly: true, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteConnection destination = await BackupRestoreDatabaseWorker
            .OpenAsync(destinationDatabasePath, destinationGrimoireSecret, readOnly: false, cancellationToken)
            .ConfigureAwait(false);

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "Sessions", cancellationToken)
                .ConfigureAwait(false))
        {

            return BackupSessionImportResult.Failed(
                new BackupVerifyIssue(
                    "backup.restore_import_source_unavailable",
                    "The archived Grimoire snapshot has no Sessions table."));

        }

        List<string> missing = [];

        foreach (Guid sessionId in sessionIds)
        {

            if (!await RowExistsAsync(source, "Sessions", sessionId.ToString(), cancellationToken)
                    .ConfigureAwait(false))
            {

                missing.Add(sessionId.ToString());

            }

        }

        if (missing.Count > 0)
        {

            return BackupSessionImportResult.Failed(
                new BackupVerifyIssue(
                    "backup.restore_import_session_absent",
                    "The archive does not contain every requested Session: "
                    + string.Join(", ", missing)));

        }

        await using SqliteTransaction transaction = (SqliteTransaction)await destination
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        long sessions = 0;

        long entries = 0;

        long attachments = 0;

        long remapped = 0;

        long deduplicated = 0;

        List<PendingAttachmentCopy> copies = [];

        // Payloads land in the live attachment tree, which no transaction can roll back. Nothing but
        // a successful commit entitles them to stay, so the cleanup is keyed on this rather than on
        // the exception types the catch below happens to name.
        bool committed = false;

        try
        {

            foreach (Guid sessionId in sessionIds)
            {

                cancellationToken.ThrowIfCancellationRequested();

                string original = sessionId.ToString();

                string effective = original;

                if (await RowExistsAsync(destination, "Sessions", original, cancellationToken, transaction)
                        .ConfigureAwait(false))
                {

                    effective = Guid.NewGuid().ToString();

                    remapped++;

                }

                await CopySessionAsync(source, destination, transaction, original, effective, cancellationToken)
                    .ConfigureAwait(false);

                sessions++;

                entries += await CopyEntriesAsync(
                    source,
                    destination,
                    transaction,
                    original,
                    effective,
                    cancellationToken).ConfigureAwait(false);

                (long copied, long deduped) = await CopyAttachmentsAsync(
                    source,
                    destination,
                    transaction,
                    original,
                    effective,
                    sourceAttachmentsRoot,
                    destinationAttachmentsRoot,
                    copies,
                    cancellationToken).ConfigureAwait(false);

                attachments += copied;

                deduplicated += deduped;

            }

            foreach (PendingAttachmentCopy copy in copies)
            {

                cancellationToken.ThrowIfCancellationRequested();

                SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(
                    Path.GetDirectoryName(copy.Destination)!);

                File.Copy(copy.Source, copy.Destination, overwrite: false);

                SecureFilePermissions.ApplyOwnerOnlyFile(copy.Destination);

            }

            beforeCommitForTests?.Invoke();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            committed = true;

            return new BackupSessionImportResult(
                sessions,
                entries,
                attachments,
                remapped,
                deduplicated,
                []);

        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            return BackupSessionImportResult.Failed(
                new BackupVerifyIssue(
                    "backup.restore_import_failed",
                    "The selected Sessions could not be imported; the destination is unchanged."));

        }
        finally
        {

            if (!committed)
            {

                // Cancellation unwinds here too, and it is the one failure the catch above never
                // sees: the transaction is rolled back by its own disposal, but the bytes are not.
                Discard(copies);

            }

        }

    }

    private static void Discard(List<PendingAttachmentCopy> copies)
    {

        foreach (PendingAttachmentCopy copy in copies)
        {

            try
            {

                File.Delete(copy.Destination);

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

            }

        }

    }

    private static async Task CopySessionAsync(
        SqliteConnection source,
        SqliteConnection destination,
        SqliteTransaction transaction,
        string originalId,
        string effectiveId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand read = source.CreateCommand();

        read.CommandText = """
            SELECT "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt", "Summary",
                   "LastSummarizedMessageAt", "TotalTokensUsed", "TotalCostUsd",
                   "UnsummarizedEntryCount", "ForkedFromSessionId"
            FROM "Sessions" WHERE "Id" = $id;
            """;

        _ = read.Parameters.AddWithValue("$id", originalId);

        await using SqliteDataReader reader = await read
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        await using SqliteCommand write = destination.CreateCommand();

        write.Transaction = transaction;

        write.CommandText = """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt",
                                    "Summary", "LastSummarizedMessageAt", "TotalTokensUsed",
                                    "TotalCostUsd", "UnsummarizedEntryCount", "ForkedFromSessionId")
            VALUES ($id, NULL, $title, $status, $createdAt, $updatedAt, $summary,
                    $lastSummarizedAt, $tokens, $cost, $unsummarized, NULL);
            """;

        _ = write.Parameters.AddWithValue("$id", effectiveId);

        _ = write.Parameters.AddWithValue("$title", Value(reader, 1));

        _ = write.Parameters.AddWithValue("$status", Value(reader, 2));

        _ = write.Parameters.AddWithValue("$createdAt", Value(reader, 3));

        _ = write.Parameters.AddWithValue("$updatedAt", Value(reader, 4));

        _ = write.Parameters.AddWithValue("$summary", Value(reader, 5));

        _ = write.Parameters.AddWithValue("$lastSummarizedAt", Value(reader, 6));

        _ = write.Parameters.AddWithValue("$tokens", Value(reader, 7));

        _ = write.Parameters.AddWithValue("$cost", Value(reader, 8));

        _ = write.Parameters.AddWithValue("$unsummarized", Value(reader, 9));

        _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<long> CopyEntriesAsync(
        SqliteConnection source,
        SqliteConnection destination,
        SqliteTransaction transaction,
        string originalSessionId,
        string effectiveSessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "Entries", cancellationToken)
                .ConfigureAwait(false))
        {

            return 0;

        }

        List<object[]> rows = [];

        await using (SqliteCommand read = source.CreateCommand())
        {

            read.CommandText = """
                SELECT "Role", "Content", "ModelUsed", "CreatedAt", "Sequence", "ToolCallId",
                       "ToolName", "ToolArguments", "IsPinned"
                FROM "Entries" WHERE "SessionId" = $id ORDER BY "Sequence";
                """;

            _ = read.Parameters.AddWithValue("$id", originalSessionId);

            await using SqliteDataReader reader = await read
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add(
                [
                    Value(reader, 0),
                    Value(reader, 1),
                    Value(reader, 2),
                    Value(reader, 3),
                    Value(reader, 4),
                    Value(reader, 5),
                    Value(reader, 6),
                    Value(reader, 7),
                    Value(reader, 8),
                ]);

            }

        }

        foreach (object[] row in rows)
        {

            await using SqliteCommand write = destination.CreateCommand();

            write.Transaction = transaction;

            write.CommandText = """
                INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                       "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
                VALUES ($id, $sessionId, $role, $content, $model, $createdAt, $sequence,
                        $toolCallId, $toolName, $toolArguments, $pinned);
                """;

            _ = write.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());

            _ = write.Parameters.AddWithValue("$sessionId", effectiveSessionId);

            _ = write.Parameters.AddWithValue("$role", row[0]);

            _ = write.Parameters.AddWithValue("$content", row[1]);

            _ = write.Parameters.AddWithValue("$model", row[2]);

            _ = write.Parameters.AddWithValue("$createdAt", row[3]);

            _ = write.Parameters.AddWithValue("$sequence", row[4]);

            _ = write.Parameters.AddWithValue("$toolCallId", row[5]);

            _ = write.Parameters.AddWithValue("$toolName", row[6]);

            _ = write.Parameters.AddWithValue("$toolArguments", row[7]);

            _ = write.Parameters.AddWithValue("$pinned", row[8]);

            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        return rows.Count;

    }

    private static async Task<(long Copied, long Deduplicated)> CopyAttachmentsAsync(
        SqliteConnection source,
        SqliteConnection destination,
        SqliteTransaction transaction,
        string originalSessionId,
        string effectiveSessionId,
        string sourceAttachmentsRoot,
        string destinationAttachmentsRoot,
        List<PendingAttachmentCopy> copies,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "SessionAttachments", cancellationToken)
                .ConfigureAwait(false))
        {

            return (0, 0);

        }

        List<AttachmentRow> rows = [];

        await using (SqliteCommand read = source.CreateCommand())
        {

            read.CommandText = """
                SELECT "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName", "Version",
                       "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                       "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath",
                       "SourceCanonicalPath", "SourceContentSha256", "SourceFileIdentity",
                       "SourceLastWriteAt", "SourceByteLength", "EncryptionVersion", "EncryptionKeyId"
                FROM "SessionAttachments" WHERE "SessionId" = $id;
                """;

            _ = read.Parameters.AddWithValue("$id", originalSessionId);

            await using SqliteDataReader reader = await read
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                object[] values = new object[22];

                for (int index = 0; index < values.Length; index++)
                {

                    values[index] = Value(reader, index);

                }

                rows.Add(new AttachmentRow(values));

            }

        }

        long copied = 0;

        long deduplicated = 0;

        foreach (AttachmentRow row in rows)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string attachmentId = Guid.NewGuid().ToString();

            string relative = row.Values[6] as string ?? string.Empty;

            string effectiveRelative = relative;

            if (relative.Length > 0)
            {

                string sourceFile = ResolveContained(sourceAttachmentsRoot, relative);

                effectiveRelative = ReplaceLeadingSegment(relative, originalSessionId, effectiveSessionId);

                string destinationFile = ResolveContained(destinationAttachmentsRoot, effectiveRelative);

                if (!File.Exists(sourceFile))
                {

                    throw new IOException(
                        "An imported attachment row references bytes the archive does not carry.");

                }

                if (File.Exists(destinationFile)
                    && SameContent(sourceFile, destinationFile))
                {

                    deduplicated++;

                }
                else if (File.Exists(destinationFile))
                {

                    effectiveRelative = ReplaceLeadingSegment(
                        relative,
                        originalSessionId,
                        effectiveSessionId + "-" + attachmentId[..8]);

                    copies.Add(new PendingAttachmentCopy(
                        sourceFile,
                        ResolveContained(destinationAttachmentsRoot, effectiveRelative)));

                }
                else
                {

                    copies.Add(new PendingAttachmentCopy(sourceFile, destinationFile));

                }

            }

            await using SqliteCommand write = destination.CreateCommand();

            write.Transaction = transaction;

            write.CommandText = """
                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey",
                     "OriginalFileName", "Version", "RelativePath", "ContentSha256", "MimeType",
                     "ByteLength", "Kind", "CreatedAt", "SourceKind", "SourceWorkspaceIdentity",
                     "SourceRelativePath", "SourceCanonicalPath", "SourceContentSha256",
                     "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength", "SourceStatus",
                     "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId")
                VALUES ($id, $sessionId, NULL, NULL, $state, $logicalKey, $originalFileName, $version,
                        $relativePath, $contentSha256, $mimeType, $byteLength, $kind, $createdAt,
                        $sourceKind, $sourceWorkspaceIdentity, $sourceRelativePath,
                        $sourceCanonicalPath, $sourceContentSha256, $sourceFileIdentity,
                        $sourceLastWriteAt, $sourceByteLength, $sourceStatus, $sourceDiagnosticReason,
                        $encryptionVersion, $encryptionKeyId);
                """;

            _ = write.Parameters.AddWithValue("$id", attachmentId);

            _ = write.Parameters.AddWithValue("$sessionId", effectiveSessionId);

            _ = write.Parameters.AddWithValue("$state", row.Values[2]);

            _ = write.Parameters.AddWithValue("$logicalKey", row.Values[3]);

            _ = write.Parameters.AddWithValue("$originalFileName", row.Values[4]);

            _ = write.Parameters.AddWithValue("$version", row.Values[5]);

            _ = write.Parameters.AddWithValue("$relativePath", effectiveRelative);

            _ = write.Parameters.AddWithValue("$contentSha256", row.Values[7]);

            _ = write.Parameters.AddWithValue("$mimeType", row.Values[8]);

            _ = write.Parameters.AddWithValue("$byteLength", row.Values[9]);

            _ = write.Parameters.AddWithValue("$kind", row.Values[10]);

            _ = write.Parameters.AddWithValue("$createdAt", row.Values[11]);

            _ = write.Parameters.AddWithValue(
                "$sourceKind",
                row.Values[12] is string kind ? kind : "SnapshotOnly");

            _ = write.Parameters.AddWithValue("$sourceWorkspaceIdentity", row.Values[13]);

            _ = write.Parameters.AddWithValue("$sourceRelativePath", row.Values[14]);

            _ = write.Parameters.AddWithValue("$sourceCanonicalPath", row.Values[15]);

            _ = write.Parameters.AddWithValue("$sourceContentSha256", row.Values[16]);

            _ = write.Parameters.AddWithValue("$sourceFileIdentity", row.Values[17]);

            _ = write.Parameters.AddWithValue("$sourceLastWriteAt", row.Values[18]);

            _ = write.Parameters.AddWithValue("$sourceByteLength", row.Values[19]);

            _ = write.Parameters.AddWithValue(
                "$sourceStatus",
                string.Equals(row.Values[12] as string, "WorkspaceFile", StringComparison.Ordinal)
                    ? "WorkspaceUnavailable"
                    : "NotApplicable");

            _ = write.Parameters.AddWithValue(
                "$sourceDiagnosticReason",
                string.Equals(row.Values[12] as string, "WorkspaceFile", StringComparison.Ordinal)
                    ? "Imported from a portable backup; rebind and validate the workspace before refreshing."
                    : (object)DBNull.Value);

            _ = write.Parameters.AddWithValue(
                "$encryptionVersion",
                row.Values[20] is DBNull ? 0 : row.Values[20]);

            _ = write.Parameters.AddWithValue("$encryptionKeyId", row.Values[21]);

            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            copied++;

        }

        return (copied, deduplicated);

    }

    private static string ReplaceLeadingSegment(
        string relative,
        string oldSegment,
        string newSegment)
    {

        string normalized = relative.Replace('\\', '/');

        return normalized.StartsWith(oldSegment + "/", StringComparison.Ordinal)
            ? newSegment + normalized[oldSegment.Length..]
            : normalized;

    }

    private static string ResolveContained(string root, string relative)
    {

        string fullRoot = Path.GetFullPath(root);

        string candidate = Path.GetFullPath(
            Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

        return candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : throw new IOException("An imported attachment path escapes the attachment root.");

    }

    private static bool SameContent(string left, string right)
    {

        try
        {

            using FileStream leftStream = File.OpenRead(left);

            using FileStream rightStream = File.OpenRead(right);

            return leftStream.Length == rightStream.Length
                && SHA256.HashData(leftStream).AsSpan()
                    .SequenceEqual(SHA256.HashData(rightStream));

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static async Task<bool> RowExistsAsync(
        SqliteConnection connection,
        string table,
        string id,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(connection, table, cancellationToken, transaction)
                .ConfigureAwait(false))
        {

            return false;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"SELECT 1 FROM \"{table}\" WHERE \"Id\" = $id LIMIT 1;";

        _ = command.Parameters.AddWithValue("$id", id);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;

    }

    /// <summary>Never returns null: an unset SqliteParameter binds as "value must be set" rather than NULL.</summary>
    private static object Value(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? DBNull.Value : reader.GetValue(ordinal);

    private sealed record PendingAttachmentCopy(string Source, string Destination);

    private sealed record AttachmentRow(object[] Values);

}
