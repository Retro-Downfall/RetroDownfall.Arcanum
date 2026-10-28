using System.Data.Common;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Annals;

/// <summary>Code-owned receipt identities proved by selected versions or explicit subject coordinates.</summary>
internal sealed record AnnalsReceiptSelection(string BeforeProtectedQuery, string Query)
{
    internal string Predicate => $"ReceiptId IN ({Query})";

    internal string ForCoreVersion(int version) => version >= 18 ? Query : BeforeProtectedQuery;

    internal static AnnalsReceiptSelection ForSubjects(
        string parentQuery,
        string versionQuery,
        string subjectQuery,
        bool hasMemoryCoordinates,
        string? normalizedSubjectParameter)
    {
        string ordinary = parentQuery
            + $" UNION SELECT ReceiptId FROM long_rest_receipt_inputs WHERE VersionId IN ({versionQuery})";

        string? protectedInputs = null;

        if (hasMemoryCoordinates)
        {
            // The normalized expression is an access path. The ordinary lane retains the exact raw
            // subject predicate; the protected lane retains its existing normalized authority.
            string memoryPredicate = normalizedSubjectParameter is { } parameter
                ? CovenantIdentitySql.Keyed("MemoryId", parameter)
                : $"lower(replace(MemoryId, '-', '')) IN (WITH receipt_subjects(subject) AS ({subjectQuery}) SELECT lower(replace(subject, '-', '')) FROM receipt_subjects) AND MemoryId IN ({subjectQuery})";

            ordinary += $" UNION SELECT ReceiptId FROM long_rest_receipt_inputs WHERE {memoryPredicate}";

            // A protected-only orphan's contradictory VersionId is not independent subject
            // authority. Existing ordinary receipt/version witnesses and positive MemoryId own it.
            protectedInputs = $"SELECT ReceiptId FROM long_rest_protected_receipt_inputs WHERE {memoryPredicate}";
        }

        return new(ordinary, protectedInputs is null ? ordinary : ordinary + " UNION " + protectedInputs);
    }
}

/// <summary>Clears complete selected receipt families in bounded frozen batches on the caller's transaction.</summary>
internal static class AnnalsReceiptErasure
{
    private const int ReceiptBatchSize = 128;

    private static readonly (string Table, int Version)[] Tables =
    [
        ("long_rest_protected_receipt_inputs", 18),
        ("long_rest_protected_receipt_evidence", 18),
        ("long_rest_suppressions", 17),
        ("long_rest_receipt_inputs", 17),
        ("long_rest_receipts", 17),
    ];

    internal static async Task<IReadOnlyDictionary<string, long>> DeleteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalsReceiptSelection selection,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        int version = await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false);

        Dictionary<string, long> changes = Tables.ToDictionary(static item => item.Table, static _ => 0L, StringComparer.Ordinal);

        if (version < 17)
        {
            return changes;
        }

        while (true)
        {
            List<string> receipts = [];

            await using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                command.CommandText = $"SELECT ReceiptId FROM ({selection.ForCoreVersion(version)}) ORDER BY ReceiptId LIMIT {ReceiptBatchSize};";

                AddParameters(command, parameters);

                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    receipts.Add(reader.GetString(0));
                }
            }

            if (receipts.Count == 0)
            {
                return changes;
            }

            // Every sibling is selected by the frozen receipt identity before any child mutation can
            // remove its ownership witness. No subject-erasure statement classifies global orphans.
            string ids = string.Join(", ", Enumerable.Range(0, receipts.Count).Select(static index => "$annalsReceipt" + index));

            foreach ((string table, int requiredVersion) in Tables)
            {
                if (version < requiredVersion)
                {
                    continue;
                }

                await using DbCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = $"DELETE FROM {table} WHERE ReceiptId IN ({ids});";

                AddParameters(command, receipts.Select(static (id, index) => ("$annalsReceipt" + index, (object?)id)));

                changes[table] += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<long> CountAsync(
        DbConnection connection,
        DbTransaction? transaction,
        AnnalsReceiptSelection selection,
        string table,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        int version = await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false);

        if (version < Tables.Single(item => item.Table == table).Version)
        {
            return 0;
        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"SELECT count(*) FROM {table} WHERE ReceiptId IN ({selection.ForCoreVersion(version)});";

        AddParameters(command, parameters);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long count
            ? count
            : throw new InvalidDataException("The selected receipt count did not return an integer.");
    }

    private static void AddParameters(DbCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value ?? DBNull.Value;

            command.Parameters.Add(parameter);
        }
    }
}
