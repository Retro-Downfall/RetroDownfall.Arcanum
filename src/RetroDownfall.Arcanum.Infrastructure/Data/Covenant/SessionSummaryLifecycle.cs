using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Transaction-bound invalidation of compression text after its source history changes.</summary>
internal static class SessionSummaryLifecycle
{
    internal static async Task<Result> EnsureUnlabeledAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        ICovenantLabeledArtifactTransactionGuard guard,
        CancellationToken cancellationToken)
    {
        Guid[] artifacts = await ReadArtifactsAsync(connection, transaction, sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (artifacts.Length == 0)
        {
            return Result.Success();
        }

        return await guard.EnsureAllUnlabeledAsync(
            SensitiveArtifactKind.Summary,
            artifacts,
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The caller owns authorization and the immediate transaction. Protected callers reach this only
    /// through the shared erasure kernel; ordinary callers first prove every selected artifact unlabelled.
    /// </summary>
    internal static async Task<long> ClearAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        Guid[] artifacts = await ReadArtifactsAsync(connection, transaction, sessionId, cancellationToken)
            .ConfigureAwait(false);

        long removed = 0;

        if (await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            removed = await ExecuteAsync(connection, transaction,
                "DELETE FROM session_summary_state WHERE lower(replace(SessionId, '-', '')) = $sessionKey;",
                sessionId, cancellationToken).ConfigureAwait(false);

            removed += await ExecuteAsync(connection, transaction,
                "DELETE FROM session_summary_artifacts WHERE lower(replace(SessionId, '-', '')) = $sessionKey;",
                sessionId, cancellationToken).ConfigureAwait(false);

            foreach (Guid artifact in artifacts)
            {
                await using DbCommand label = connection.CreateCommand();

                label.Transaction = transaction;

                label.CommandText = "DELETE FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND "
                    + CovenantIdentitySql.Keyed("ArtifactId", "$artifactKey") + ";";

                Add(label, "$kind", (long)SensitiveArtifactKind.Summary);

                Add(label, "$artifactKey", CovenantIdentitySql.Key(artifact));

                removed += await label.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _ = await ExecuteAsync(connection, transaction,
            """
            UPDATE "Sessions"
            SET "Summary" = NULL,
                "LastSummarizedMessageAt" = NULL,
                "UnsummarizedEntryCount" = (
                    SELECT COUNT(*) FROM "Entries"
                    WHERE lower(replace("SessionId", '-', '')) = $sessionKey)
            WHERE lower(replace("Id", '-', '')) = $sessionKey;
            """, sessionId, cancellationToken).ConfigureAwait(false);

        return removed;
    }

    internal static async Task<SessionSummaryClosureSnapshot> ReadClosureAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return new(0, "");
        }

        const string artifacts = "SELECT lower(replace(ArtifactId, '-', '')) FROM session_summary_artifacts WHERE lower(replace(SessionId, '-', '')) = $sessionKey";

        (string Table, string Predicate, string Identity)[] targets =
        [
            ("session_summary_artifacts", "lower(replace(SessionId, '-', '')) = $sessionKey", "ArtifactId || ':' || Revision || ':' || hex(ContentDigest) || ':' || hex(SensitivityDigest)"),
            ("session_summary_state", "lower(replace(SessionId, '-', '')) = $sessionKey", "SessionId || ':' || CurrentArtifactId || ':' || Revision || ':' || UpdatedAtUtc"),
            ("artifact_sensitivity", $"ArtifactKindCode = {(long)SensitiveArtifactKind.Summary} AND lower(replace(ArtifactId, '-', '')) IN ({artifacts})", "LabelId || ':' || ArtifactRevision || ':' || hex(ArtifactContentDigest) || ':' || hex(SensitivityDigest)"),
        ];

        long rows = 0;

        System.Text.StringBuilder authority = new();

        foreach ((string table, string predicate, string identity) in targets)
        {
            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"SELECT {identity} FROM {table} WHERE {predicate} ORDER BY 1;";

            Add(command, "$sessionKey", sessionId.ToString("N"));

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string value = reader.GetString(0);

                authority.Append(table).Append(':').Append(value.Length).Append(':').Append(value).Append('\n');

                rows = checked(rows + 1);
            }
        }

        return new(rows, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(authority.ToString()))));
    }

    internal static async Task<bool> IsInstalledAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'session_summary_artifacts');";

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<Guid[]> ReadArtifactsAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT ArtifactId FROM session_summary_artifacts WHERE lower(replace(SessionId, '-', '')) = $sessionKey;";

        Add(command, "$sessionKey", sessionId.ToString("N"));

        List<Guid> artifacts = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            artifacts.Add(Guid.Parse(reader.GetString(0)));
        }

        return [.. artifacts];
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        Add(command, "$sessionKey", sessionId.ToString("N"));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);
    }
}


internal sealed record SessionSummaryClosureSnapshot(long Rows, string Authority);
