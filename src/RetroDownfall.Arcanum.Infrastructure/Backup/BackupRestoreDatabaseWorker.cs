using System.Globalization;

using System.Text.Json;

using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

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
    long EmbeddingsToRebuild,
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

    /// <summary>
    /// Every derived-vector table, paired with the accelerator mirror that projects it.
    /// </summary>
    /// <remarks>
    /// The pairing is the point. A base table and its <c>*_vec</c> mirror hold one vector twice and
    /// nothing keeps them in step — there is no trigger, and every other component that removes a base
    /// vector removes the mirror row explicitly. A list of base tables alone is how a restore under a
    /// different configured width came to hand the operator two tables that disagree about which rows
    /// have vectors, with retrieval answering from the half that was left behind.
    /// </remarks>
    private static readonly (string Table, string Mirror, string Key)[] EmbeddingTables =
    [
        ("entry_embeddings", "entry_embeddings_vec", "EntryId"),
        ("session_attachment_embeddings", "session_attachment_embeddings_vec", "ChunkId"),
        ("workspace_file_embeddings", "workspace_file_embeddings_vec", "ChunkId"),
        ("saga_memory_embeddings", "saga_memory_embeddings_vec", "MemoryId"),
        ("tapestry_node_embeddings", "tapestry_node_embeddings_vec", "NodeId"),
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

            // The staged snapshot is converged through the same installer the host uses, and the
            // schema's guard triggers call the arcanum_*_authorized scalar functions this initializer
            // registers. Without it a guarded write fails with "no such function" instead of being
            // denied, which would abort the restore rather than refuse one statement.
            await CovenantSqliteConnectionInitializer.Instance
                .InitializeAsync(
                    connection,
                    readOnly
                        ? CovenantSqliteConnectionMode.ReadOnly
                        : CovenantSqliteConnectionMode.ReadWrite,
                    cancellationToken)
                .ConfigureAwait(false);

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
    /// <remarks>
    /// The installer is injected rather than constructed here so the restore path shares the host's
    /// composed instance, logger included. It previously passed <c>logger: null</c>, which silently
    /// discarded the Lexicon-rebuild and embedding-dimension-mismatch warnings on exactly the path
    /// where an operator most needs to see them.
    ///
    /// <para>The whole <see cref="GrimoireSchemaInstallResult"/> is returned rather than swallowed:
    /// a restore that converged Core but left a Covenant tier unavailable is a materially different
    /// outcome from one where all three are healthy, and the caller has to be able to tell.</para>
    /// </remarks>
    public static Task<GrimoireSchemaInstallResult> MigrateAsync(
        SqliteConnection connection,
        GrimoireSchemaInstaller installer,
        int embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(installer);

        ArgumentNullException.ThrowIfNull(context);

        return installer.InstallAsync(connection, embeddingDimensions, context, cancellationToken);

    }

    /// <summary>
    /// Resolves the installation context the staged snapshot must be converged against.
    /// </summary>
    /// <remarks>
    /// A snapshot that already carries a well-formed authority row is converged against its own
    /// identity, fingerprint, and counters, so migration is a no-op for authority: reusing the stored
    /// fingerprint makes the Core initializer's fixed-time comparison match and nothing advances. A
    /// restore is not a key rotation, and recording one would make the restored installation claim a
    /// generation the operator never caused.
    ///
    /// <para>A snapshot with no authority row predates the table. That one is seeded fresh from this
    /// machine's key material, which is the only correct answer available: the row has to exist before
    /// any Covenant path may run.</para>
    /// </remarks>
    public static async Task<GrimoireSchemaInitializationContext> ResolveInitializationContextAsync(
        SqliteConnection connection,
        string masterKeyMaterial,
        DateTimeOffset installedAtUtc,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(masterKeyMaterial);

        GrimoireSchemaInitializationContext? staged = await GrimoireSchemaInitializationContextReader
            .TryReadAsync(connection, installedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        return staged
            ?? CovenantAuthorityBootstrapper.PrepareWithoutInstallationLock(
                masterKeyMaterial,
                installedAtUtc);

    }

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
    /// embedding size, and the accelerator mirror rows that projected them. They are recomputed on
    /// demand; transporting them would poison similarity search with vectors from another provider or
    /// another model width.
    /// </summary>
    /// <remarks>
    /// The returned count is base-table rows only. A mirror row is the same vector projected, not a
    /// second one, and counting it would double what the operator is told a restore left to rebuild.
    /// </remarks>
    public static async Task<long> DropMismatchedEmbeddingsAsync(
        SqliteConnection connection,
        int embeddingDimensions,
        CancellationToken cancellationToken)
    {

        long removed = 0;

        foreach ((string table, string mirror, string key) in EmbeddingTables)
        {

            if (!await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

            await using (SqliteCommand command = connection.CreateCommand())
            {

                command.CommandText = $"DELETE FROM \"{table}\" WHERE \"Dim\" <> $dim;";

                _ = command.Parameters.AddWithValue("$dim", embeddingDimensions);

                removed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }

            await DropOrphanedVectorMirrorRowsAsync(connection, table, mirror, key, cancellationToken)
                .ConfigureAwait(false);

        }

        return removed;

    }

    /// <summary>
    /// Removes every mirror row whose key no longer has a row in the table it mirrors.
    /// </summary>
    /// <remarks>
    /// Keyed one row at a time rather than as a single set-based delete, because a mirror is a
    /// <c>vec0</c> virtual table on builds that have the accelerator and its delete surface is the
    /// keyed one — the same shape the live erasure kernel and the retention service use. Guarded by an
    /// existence check, so a build without the accelerator is untouched.
    ///
    /// <para>Written against "has no base row" rather than "was in the batch just deleted", so a
    /// snapshot that already arrived inconsistent converges too. The identity comparison is normalised
    /// because a mirror's key column is deliberately outside the canonicalisation family and an archive
    /// is somebody else's database.</para>
    /// </remarks>
    private static async Task DropOrphanedVectorMirrorRowsAsync(
        SqliteConnection connection,
        string table,
        string mirror,
        string key,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(connection, mirror, cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        List<string> orphaned = [];

        await using (SqliteCommand read = connection.CreateCommand())
        {

            read.CommandText = $"""
                SELECT mirror."{key}" FROM "{mirror}" mirror
                WHERE NOT EXISTS (
                    SELECT 1 FROM "{table}" base
                    WHERE lower(replace(base."{key}", '-', ''))
                        = lower(replace(mirror."{key}", '-', '')));
                """;

            await using SqliteDataReader reader = await read
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                if (!reader.IsDBNull(0))
                {

                    orphaned.Add(reader.GetString(0));

                }

            }

        }

        foreach (string identity in orphaned)
        {

            await using SqliteCommand delete = connection.CreateCommand();

            delete.CommandText = $"DELETE FROM \"{mirror}\" WHERE \"{key}\" = $identity;";

            _ = delete.Parameters.AddWithValue("$identity", identity);

            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

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
            EmbeddingsToRebuild: 0,
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

    /// <summary>
    /// Reads every Campaign identity this database holds, so a caller can check a mapping against what
    /// actually exists rather than against what a request claims.
    /// </summary>
    /// <remarks>
    /// Every row is read and parsed rather than the identities being probed one at a time. EF's SQLite
    /// provider stores <c>Campaigns.Id</c> as uppercase "D"-format text and SQLite's default TEXT
    /// collation is BINARY, so a <c>WHERE "Id" = $id</c> bound with the lowercase form a
    /// <see cref="Guid"/> renders by default would silently match nothing and report every destination
    /// Campaign as absent. <see cref="Guid.TryParse(string, out Guid)"/> is case-insensitive, so
    /// comparing parsed identities cannot be wrong about case in either direction.
    /// </remarks>
    public static async Task<IReadOnlyCollection<Guid>> ReadCampaignIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(connection, "Campaigns", cancellationToken).ConfigureAwait(false))
        {

            return [];

        }

        HashSet<Guid> identities = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT "Id" FROM "Campaigns";
            """;

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!reader.IsDBNull(0) && Guid.TryParse(reader.GetString(0), out Guid identity))
            {

                _ = identities.Add(identity);

            }

        }

        return identities;

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
