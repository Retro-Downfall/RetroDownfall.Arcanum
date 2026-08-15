using System.Globalization;

using System.Text.Json;

using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Security;


using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed record BackupRestoreRemapOutcome(
    IReadOnlyDictionary<BackupPathMappingKind, long> MatchesByKind,
    IReadOnlyList<string> UnmappedNonportablePaths);

internal sealed record BackupRestoreDatabaseReconciliation(
    long Attachments,
    long StaleAttachmentSources,
    long UploadedFiles,
    long BatchFiles,
    long EmbeddingsRebuilt,
    long PendingOperationsCleared);

/// <summary>
/// Every mutation a restore makes inside the staged Grimoire snapshot.
/// </summary>
/// <remarks>
/// All of this runs against the *staged* database, before commit, so a failure anywhere leaves the
/// live installation untouched. Schema convergence goes through
/// <see cref="GrimoireSchemaInstaller"/> — the same declarative authority the host uses at startup —
/// rather than hand-written DDL or a migration-history edit.
/// </remarks>
internal static class BackupRestoreDatabaseWorker
{

    private static readonly string[] EmbeddingTables =
    [
        "entry_embeddings",
        "session_attachment_embeddings",
        "workspace_file_embeddings",
        "saga_memory_embeddings",
        "tapestry_node_embeddings",
    ];

    public static async Task<SqliteConnection> OpenAsync(
        string databasePath,
        string grimoireSecret,
        bool readOnly,
        CancellationToken cancellationToken)
    {

        SqliteNativeRuntime.Instance.Initialize();

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(databasePath);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase;

        try
        {

            passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
                grimoireSecret,
                salt);

        }
        finally
        {

            System.Security.Cryptography.CryptographicOperations.ZeroMemory(salt);

        }

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = passphrase,

                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,

                Pooling = false,

            }.ToString());

        try
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return connection;

        }
        catch
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            throw;

        }

    }

    public static Task<string> ReadSchemaIdentityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        GrimoireSchemaIdentity.ComputeAsync(connection, cancellationToken);

    /// <summary>
    /// Converges the staged snapshot onto this build's declarative schema. Older supported snapshots
    /// gain whatever objects they lack; a snapshot already at this schema is unchanged.
    /// </summary>
    public static Task MigrateAsync(
        SqliteConnection connection,
        int embeddingDimensions,
        CancellationToken cancellationToken) =>
        GrimoireSchemaInstaller.InstallAsync(
            connection,
            embeddingDimensions,
            logger: null,
            cancellationToken);

    /// <summary>
    /// Applies typed root rewrites to the machine-specific columns that own them, and reports every
    /// absolute path that no mapping claimed so the operator sees what stays broken.
    /// </summary>
    public static async Task<BackupRestoreRemapOutcome> RemapAsync(
        SqliteConnection connection,
        BackupPathRemapper remapper,
        CancellationToken cancellationToken)
    {

        Dictionary<BackupPathMappingKind, long> matches = Enum
            .GetValues<BackupPathMappingKind>()
            .ToDictionary(static kind => kind, static _ => 0L);

        SortedSet<string> unmapped = new(StringComparer.Ordinal);

        await RemapColumnAsync(
            connection,
            "Campaigns",
            "Id",
            "Path",
            BackupPathMappingKind.CampaignRoot,
            remapper,
            matches,
            unmapped,
            cancellationToken).ConfigureAwait(false);

        await RemapColumnAsync(
            connection,
            "WorkspaceContexts",
            "Id",
            "RootPath",
            BackupPathMappingKind.WorkspaceRoot,
            remapper,
            matches,
            unmapped,
            cancellationToken).ConfigureAwait(false);

        await RemapColumnAsync(
            connection,
            "SessionAttachments",
            "Id",
            "SourceCanonicalPath",
            BackupPathMappingKind.AttachmentSourceProvenance,
            remapper,
            matches,
            unmapped,
            cancellationToken,
            fallbackKind: BackupPathMappingKind.WorkspaceRoot).ConfigureAwait(false);

        await RemapSanctumAllowedPathsAsync(
            connection,
            remapper,
            matches,
            unmapped,
            cancellationToken).ConfigureAwait(false);

        return new BackupRestoreRemapOutcome(matches, [.. unmapped]);

    }

    /// <summary>
    /// Demotes every live workspace-file provenance record to <c>WorkspaceUnavailable</c>. The
    /// snapshot bytes stay readable; only the ability to silently refresh from a path that may now
    /// belong to unrelated content is withdrawn until the workspace is explicitly rebound.
    /// </summary>
    public static async Task<long> MarkAttachmentSourcesStaleAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(connection, "SessionAttachments", cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            UPDATE "SessionAttachments"
            SET "SourceStatus" = 'WorkspaceUnavailable',
                "SourceDiagnosticReason" =
                    'Restored from a portable backup; rebind and validate the workspace before refreshing.'
            WHERE "SourceKind" = 'WorkspaceFile'
              AND "SourceStatus" <> 'WorkspaceUnavailable';
            """;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Drops durable operations captured mid-flight. Their leases, in-process state, and peer
    /// connections died with the source machine, so resuming them here would invent progress.
    /// </summary>
    public static async Task<long> ClearPendingOperationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(connection, "LongRunningOperations", cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            DELETE FROM "LongRunningOperations"
            WHERE "State" NOT IN ($completed, $failed, $abandoned)
              AND "RootOperationId" IS NULL
              AND "ParentOperationId" IS NULL;
            """;

        _ = command.Parameters.AddWithValue(
            "$completed",
            (int)LongRunningOperationState.Completed);

        _ = command.Parameters.AddWithValue(
            "$failed",
            (int)LongRunningOperationState.Failed);

        _ = command.Parameters.AddWithValue(
            "$abandoned",
            (int)LongRunningOperationState.Abandoned);

        long deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand children = connection.CreateCommand();

        children.CommandText = """
            UPDATE "LongRunningOperations"
            SET "State" = $abandoned,
                "LeaseOwner" = NULL,
                "LeaseExpiresAt" = NULL,
                "TerminalErrorCode" = 'backup.restore_interrupted_elsewhere'
            WHERE "State" NOT IN ($completed, $failed, $abandoned);
            """;

        _ = children.Parameters.AddWithValue(
            "$completed",
            (int)LongRunningOperationState.Completed);

        _ = children.Parameters.AddWithValue(
            "$failed",
            (int)LongRunningOperationState.Failed);

        _ = children.Parameters.AddWithValue(
            "$abandoned",
            (int)LongRunningOperationState.Abandoned);

        return deleted
            + await children.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Removes derived vectors whose dimension no longer matches this installation's configured
    /// embedding size. They are recomputed on demand; transporting them would poison similarity
    /// search with vectors from another provider or another model width.
    /// </summary>
    public static async Task<long> DropMismatchedEmbeddingsAsync(
        SqliteConnection connection,
        int embeddingDimensions,
        CancellationToken cancellationToken)
    {

        long removed = 0;

        foreach (string table in EmbeddingTables)
        {

            if (!await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"DELETE FROM \"{table}\" WHERE \"Dim\" <> $dim;";

            _ = command.Parameters.AddWithValue("$dim", embeddingDimensions);

            removed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        return removed;

    }

    public static async Task<BackupRestoreDatabaseReconciliation> ReconcileAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        long attachments = await CountRowsAsync(
            connection,
            "SessionAttachments",
            cancellationToken).ConfigureAwait(false);

        long stale = await CountAsync(
            connection,
            """
            SELECT COUNT(*) FROM "SessionAttachments"
            WHERE "SourceStatus" = 'WorkspaceUnavailable';
            """,
            "SessionAttachments",
            cancellationToken).ConfigureAwait(false);

        long uploaded = await CountRowsAsync(
            connection,
            "UploadedFiles",
            cancellationToken).ConfigureAwait(false);

        long batches = await CountRowsAsync(
            connection,
            "Batches",
            cancellationToken).ConfigureAwait(false);

        return new BackupRestoreDatabaseReconciliation(
            attachments,
            stale,
            uploaded,
            batches,
            EmbeddingsRebuilt: 0,
            PendingOperationsCleared: 0);

    }

    /// <summary>
    /// Enumerates the relative attachment/upload payload paths the restored database expects, so the
    /// caller can prove every referenced byte actually arrived instead of trusting the manifest.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadReferencedAttachmentPathsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(connection, "SessionAttachments", cancellationToken).ConfigureAwait(false))
        {

            return [];

        }

        List<string> paths = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT "RelativePath" FROM "SessionAttachments" WHERE "RelativePath" <> '';
            """;

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!reader.IsDBNull(0))
            {

                paths.Add(reader.GetString(0));

            }

        }

        return paths;

    }

    private static async Task RemapColumnAsync(
        SqliteConnection connection,
        string table,
        string keyColumn,
        string column,
        BackupPathMappingKind kind,
        BackupPathRemapper remapper,
        Dictionary<BackupPathMappingKind, long> matches,
        SortedSet<string> unmapped,
        CancellationToken cancellationToken,
        BackupPathMappingKind? fallbackKind = null)
    {

        if (!await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        List<(string Key, string Value)> rows = [];

        await using (SqliteCommand read = connection.CreateCommand())
        {

            read.CommandText =
                $"SELECT \"{keyColumn}\", \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL AND \"{column}\" <> '';";

            await using SqliteDataReader reader = await read
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add((reader.GetString(0), reader.GetString(1)));

            }

        }

        foreach ((string key, string value) in rows)
        {

            cancellationToken.ThrowIfCancellationRequested();

            BackupPathMappingKind effective = kind;

            if (!remapper.TryRemap(kind, value, out string? remapped)
                && (fallbackKind is null
                    || !remapper.TryRemap(fallbackKind.Value, value, out remapped)))
            {

                _ = unmapped.Add(value);

                continue;

            }

            if (fallbackKind is not null && !remapper.TryRemap(kind, value, out _))
            {

                effective = fallbackKind.Value;

            }

            await using SqliteCommand update = connection.CreateCommand();

            update.CommandText =
                $"UPDATE \"{table}\" SET \"{column}\" = $value WHERE \"{keyColumn}\" = $key;";

            _ = update.Parameters.AddWithValue("$value", remapped);

            _ = update.Parameters.AddWithValue("$key", key);

            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            matches[effective]++;

        }

    }

    /// <summary>
    /// Rewrites per-campaign Sanctum allow-lists. Any mapping kind may appear here: an allow-list
    /// entry can name a campaign root, a workspace, a Codex tree, or a Spell tree.
    /// </summary>
    private static async Task RemapSanctumAllowedPathsAsync(
        SqliteConnection connection,
        BackupPathRemapper remapper,
        Dictionary<BackupPathMappingKind, long> matches,
        SortedSet<string> unmapped,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(connection, "Campaigns", cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        List<(string Id, string Json)> rows = [];

        await using (SqliteCommand read = connection.CreateCommand())
        {

            read.CommandText = """
                SELECT "Id", "SanctumConfigJson" FROM "Campaigns"
                WHERE "SanctumConfigJson" IS NOT NULL AND "SanctumConfigJson" <> '';
                """;

            await using SqliteDataReader reader = await read
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add((reader.GetString(0), reader.GetString(1)));

            }

        }

        foreach ((string id, string json) in rows)
        {

            cancellationToken.ThrowIfCancellationRequested();

            JsonNode? node;

            try
            {

                node = JsonNode.Parse(json);

            }
            catch (JsonException)
            {

                continue;

            }

            if (node?["allowedPaths"] is not JsonArray allowed)
            {

                continue;

            }

            bool changed = false;

            for (int index = 0; index < allowed.Count; index++)
            {

                // A hand-edited or partially corrupt allow-list can hold a number, object, array, or
                // boolean. Those are not paths, so they are skipped exactly like the malformed-JSON
                // case above rather than aborting the whole staging phase.
                if (allowed[index] is not JsonValue entry
                    || !entry.TryGetValue(out string? value)
                    || value.Length == 0)
                {

                    continue;

                }

                bool remappedAny = false;

                foreach (BackupPathMappingKind kind in Enum.GetValues<BackupPathMappingKind>())
                {

                    if (kind == BackupPathMappingKind.AttachmentSourceProvenance
                        || !remapper.TryRemap(kind, value, out string? remapped))
                    {

                        continue;

                    }

                    allowed[index] = JsonValue.Create(remapped);

                    matches[kind]++;

                    changed = true;

                    remappedAny = true;

                    break;

                }

                if (!remappedAny)
                {

                    _ = unmapped.Add(value);

                }

            }

            if (!changed)
            {

                continue;

            }

            await using SqliteCommand update = connection.CreateCommand();

            update.CommandText = """
                UPDATE "Campaigns" SET "SanctumConfigJson" = $json WHERE "Id" = $id;
                """;

            _ = update.Parameters.AddWithValue("$json", node!.ToJsonString());

            _ = update.Parameters.AddWithValue("$id", id);

            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

    }

    private static async Task<long> CountRowsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken) =>
        await CountAsync(
            connection,
            $"SELECT COUNT(*) FROM \"{table}\";",
            table,
            cancellationToken).ConfigureAwait(false);

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string sql,
        string requiredTable,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(connection, requiredTable, cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    internal static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;
            """;

        _ = command.Parameters.AddWithValue("$name", table);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;

    }

}
