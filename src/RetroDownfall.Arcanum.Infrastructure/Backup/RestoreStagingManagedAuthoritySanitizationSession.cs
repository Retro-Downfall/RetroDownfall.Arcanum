using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// Which authority-bearing table one tombstone was stripped from.
/// </summary>
internal enum RestoredManagedFileAuthoritySourceKind
{

    ManagedWriteIntent = 1,

    LocalErasureWorkItem = 2,

}

/// <summary>
/// Whether the stripped row had a live sensitivity label, and what became of it.
/// </summary>
internal enum RestoredManagedFileLabelDisposition
{

    NoLiveLabel = 1,

    ExactLabelRemoved = 2,

}

/// <summary>
/// The eight typed operations that constitute restore-staging authority sanitation.
/// </summary>
/// <remarks>
/// Sealed, nonserializable, and nondefault, constructed only inside the capability's one invocation.
/// It exports no connection, transaction, command, SQL text, path, service provider, repository,
/// callback, delegate, or transfer method — the complete surface is the eight methods below, in the
/// one order the ordinal permits.
///
/// <para>The ordering is the safety property. Source tombstones exist before the local tombstones
/// that link to them, both exist before anything is deleted, and the labels go after the rows that
/// referenced them. A skipped, repeated, reordered, or escaped session fails before it reaches
/// SQL.</para>
/// </remarks>
internal sealed class RestoreStagingManagedAuthoritySanitizationSession
{

    internal const string TombstoneVectorDomain =
        "Arcanum.BackupRestore.ManagedAuthorityTombstones.v1";

    internal const string StrippedAuthorityDomain =
        "Arcanum.BackupRestore.StrippedManagedAuthority.v1";

    private readonly SqliteConnection _connection;

    private readonly SqliteTransaction _transaction;

    private readonly RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity _run;

    private readonly CovenantExclusiveRecoveryOwner _owner;

    private readonly Guid _stagedDatasetGeneration;

    private readonly TimeProvider _timeProvider;

    private readonly List<SourceRow> _managedIntents = [];

    private readonly List<SourceRow> _workItems = [];

    private readonly List<TombstoneItem> _tombstones = [];

    private int _ordinal;

    private ulong _removedLabels;

    private bool _invalidated;

    internal RestoreStagingManagedAuthoritySanitizationSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CovenantExclusiveRecoveryOwner owner,
        Guid stagedDatasetGeneration,
        TimeProvider timeProvider)
    {

        _connection = connection;

        _transaction = transaction;

        _run = run;

        _owner = owner;

        _stagedDatasetGeneration = stagedDatasetGeneration;

        _timeProvider = timeProvider;

    }

    /// <summary>Ends the session before control returns to the caller.</summary>
    internal void Invalidate() => _invalidated = true;

    internal Task<Result> InventoryManagedWriteIntentsAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken) =>
        StepAsync(run, 0, () => InventoryAsync(_managedIntents, managed: true, cancellationToken));

    internal Task<Result> InsertAndValidateManagedSourceTombstonesAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken) =>
        StepAsync(
            run,
            1,
            () => InsertTombstonesAsync(
                _managedIntents,
                RestoredManagedFileAuthoritySourceKind.ManagedWriteIntent,
                cancellationToken));

    internal Task<Result> InventoryLocalErasureWorkItemsAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken) =>
        StepAsync(run, 2, () => InventoryAsync(_workItems, managed: false, cancellationToken));

    internal Task<Result> InsertAndValidateLinkedLocalTombstonesAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken) =>
        StepAsync(
            run,
            3,
            () => InsertTombstonesAsync(
                _workItems,
                RestoredManagedFileAuthoritySourceKind.LocalErasureWorkItem,
                cancellationToken));

    internal Task<Result> GuardDeleteLocalErasureWorkItemsAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken) =>
        StepAsync(run, 4, () => DeleteGuardedAsync("local_erasure_work_items", "WorkItemId", _workItems, cancellationToken));

    internal Task<Result> DeleteExactAdoptedLabelsAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken) =>
        StepAsync(run, 5, () => DeleteLabelsAsync(cancellationToken));

    internal Task<Result> GuardDeleteManagedWriteIntentsAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken) =>
        StepAsync(
            run,
            6,
            () => DeleteGuardedAsync(
                "managed_file_write_intents",
                "WriteOperationId",
                _managedIntents,
                cancellationToken));

    /// <summary>
    /// Proves both authority tables are empty and returns the exact receipt.
    /// </summary>
    internal async Task<Result<BackupRestoreManagedAuthoritySanitizationReceipt>> VerifyCompleteAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken)
    {

        Result guarded = Guard(run, 7);

        if (guarded.IsFailure)
        {

            return guarded.Error;

        }

        _ordinal++;

        foreach (string table in (string[])["managed_file_write_intents", "local_erasure_work_items"])
        {

            if (await CountAsync(table, cancellationToken).ConfigureAwait(false) != 0)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    $"Staged {table} still holds authority after sanitation.");

            }

        }

        long tombstones = await CountAsync(
            "restored_managed_file_authority_tombstones",
            cancellationToken).ConfigureAwait(false);

        if (tombstones != _tombstones.Count)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The tombstone count does not match the rows this sanitation enumerated.");

        }

        return new BackupRestoreManagedAuthoritySanitizationReceipt(
            _owner.OperationId,
            checked((ulong)_managedIntents.Count),
            checked((ulong)_workItems.Count),
            _removedLabels,
            ComputeVectorDigest());

    }

    /// <summary>
    /// The canonical commitment to everything this run stripped.
    /// </summary>
    /// <remarks>
    /// Items are sorted by source-kind code and then by RFC-4122 source-row bytes, so two runs over
    /// the same staged database produce the same digest regardless of the order SQLite returned rows
    /// in. Zero source rows use the same preimage with a checked zero and no item bytes.
    /// </remarks>
    private CovenantDigest ComputeVectorDigest()
    {

        TombstoneItem[] ordered = [.. _tombstones
            .OrderBy(static item => (int)item.SourceKind)
            .ThenBy(static item => item.SourceRowId, StringComparer.Ordinal)];

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(TombstoneVectorDomain));

        hash.AppendData(_owner.OperationId.ToByteArray(bigEndian: true));

        hash.AppendData(_owner.EffectDigest.Bytes);

        hash.AppendData(_stagedDatasetGeneration.ToByteArray(bigEndian: true));

        Span<byte> count = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(count, checked((ulong)ordered.Length));

        hash.AppendData(count);

        foreach (TombstoneItem item in ordered)
        {

            hash.AppendData([(byte)item.SourceKind]);

            hash.AppendData(Encoding.UTF8.GetBytes(item.SourceRowId));

            hash.AppendData(Encoding.UTF8.GetBytes(item.SourceWriteOperationId));

            hash.AppendData(Encoding.UTF8.GetBytes(item.ArtifactId));

            hash.AppendData(Encoding.UTF8.GetBytes(item.SensitivityLabelId));

            hash.AppendData([(byte)item.OriginalStateCode]);

            hash.AppendData([(byte)item.OwnerScopeCode]);

            hash.AppendData(item.OwnerCampaignId is { } campaign
                ? [(byte)1, .. campaign.ToByteArray(bigEndian: true)]
                : [(byte)0]);

            hash.AppendData([(byte)item.LabelDisposition]);

            hash.AppendData(item.StrippedAuthorityDigest.Bytes);

        }

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private Result Guard(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        int expectedOrdinal)
    {

        if (_invalidated)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This sanitation session has already ended."));

        }

        if (!ReferenceEquals(run, _run))
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "A sanitation operation must present this session's own run identity."));

        }

        return _ordinal == expectedOrdinal
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "A sanitation operation was skipped, repeated, or reordered."));

    }

    private async Task<Result> StepAsync(
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        int expectedOrdinal,
        Func<Task<Result>> operation)
    {

        Result guarded = Guard(run, expectedOrdinal);

        if (guarded.IsFailure)
        {

            return guarded;

        }

        Result executed = await operation().ConfigureAwait(false);

        if (executed.IsSuccess)
        {

            _ordinal++;

        }

        return executed;

    }

    private async Task<Result> InventoryAsync(
        List<SourceRow> destination,
        bool managed,
        CancellationToken cancellationToken)
    {

        destination.Clear();

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        // Every arm, not only the pending ones. An adopted, erased, completed, or manual row still
        // names a file on a machine this installation is not.
        command.CommandText = managed
            ? """
              SELECT i.WriteOperationId, i.WriteOperationId, i.ArtifactId, i.SensitivityLabelId,
                     i.PhaseCode, s.CampaignId
              FROM managed_file_write_intents i
              LEFT JOIN artifact_sensitivity s ON s.LabelId = i.SensitivityLabelId
              ORDER BY i.WriteOperationId;
              """
            : """
              SELECT w.WorkItemId, w.SourceWriteOperationId, w.ArtifactId, w.SourceSensitivityLabelId,
                     w.StateCode, s.CampaignId
              FROM local_erasure_work_items w
              LEFT JOIN artifact_sensitivity s ON s.LabelId = w.SourceSensitivityLabelId
              ORDER BY w.WorkItemId;
              """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            destination.Add(new SourceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5))));

        }

        return Result.Success();

    }

    private async Task<Result> InsertTombstonesAsync(
        List<SourceRow> rows,
        RestoredManagedFileAuthoritySourceKind kind,
        CancellationToken cancellationToken)
    {

        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);

        foreach (SourceRow row in rows)
        {

            // A local-erasure tombstone copies its artifact, label, owner scope, and label
            // disposition from the source tombstone that was already inserted, never from its own
            // row. Otherwise a local row could invent a disposition its producer never recorded, and
            // the schema's link guard refuses exactly that.
            TombstoneItem? linked = kind == RestoredManagedFileAuthoritySourceKind.LocalErasureWorkItem
                ? _tombstones.FirstOrDefault(candidate =>
                    candidate.SourceKind == RestoredManagedFileAuthoritySourceKind.ManagedWriteIntent
                    && string.Equals(
                        candidate.SourceRowId,
                        row.SourceWriteOperationId,
                        StringComparison.Ordinal))
                : null;

            if (kind == RestoredManagedFileAuthoritySourceKind.LocalErasureWorkItem && linked is null)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A staged erasure work item names a managed write this sanitation never enumerated.");

            }

            RestoredManagedFileLabelDisposition disposition;

            if (linked is not null)
            {

                disposition = linked.LabelDisposition;

            }
            else
            {

                bool hasLabel = await LabelExistsAsync(row.SensitivityLabelId, cancellationToken)
                    .ConfigureAwait(false);

                disposition = hasLabel
                    ? RestoredManagedFileLabelDisposition.ExactLabelRemoved
                    : RestoredManagedFileLabelDisposition.NoLiveLabel;

            }

            CovenantDigest stripped = StrippedAuthority(row, kind);

            TombstoneItem item = new(
                kind,
                row.SourceRowId,
                row.SourceWriteOperationId,
                linked?.ArtifactId ?? row.ArtifactId,
                linked?.SensitivityLabelId ?? row.SensitivityLabelId,
                row.OriginalStateCode,
                linked?.OwnerScopeCode ?? (row.OwnerCampaignId is null ? 1 : 2),
                linked is null ? row.OwnerCampaignId : linked.OwnerCampaignId,
                disposition,
                stripped);

            await using SqliteCommand command = _connection.CreateCommand();

            command.Transaction = _transaction;

            command.CommandText = """
                INSERT INTO restored_managed_file_authority_tombstones (
                    RestoreOperationId, SourceKind, SourceRowId, RestoreEffectDigest,
                    StagedDatasetGeneration, SourceWriteOperationId, ArtifactId, SensitivityLabelId,
                    OriginalStateCode, OwnerScopeCode, OwnerCampaignId, LabelDispositionCode,
                    StrippedAuthorityDigest, RecordedAtUtc)
                VALUES ($operation, $kind, $row, $effect,
                        $generation, $write, $artifact, $label,
                        $state, $scope, $campaign, $disposition,
                        $stripped, $now);
                """;

            _ = command.Parameters.AddWithValue("$operation", _owner.OperationId.ToString("D"));

            _ = command.Parameters.AddWithValue("$kind", (int)kind);

            _ = command.Parameters.AddWithValue("$row", row.SourceRowId);

            _ = command.Parameters.AddWithValue("$effect", _owner.EffectDigest.Bytes);

            _ = command.Parameters.AddWithValue(
                "$generation",
                _stagedDatasetGeneration.ToByteArray(bigEndian: true));

            _ = command.Parameters.AddWithValue("$write", row.SourceWriteOperationId);

            _ = command.Parameters.AddWithValue("$artifact", item.ArtifactId);

            _ = command.Parameters.AddWithValue("$label", item.SensitivityLabelId);

            _ = command.Parameters.AddWithValue("$state", row.OriginalStateCode);

            _ = command.Parameters.AddWithValue("$scope", item.OwnerScopeCode);

            _ = command.Parameters.AddWithValue(
                "$campaign",
                item.OwnerCampaignId is { } campaign ? campaign.ToString("D") : DBNull.Value);

            _ = command.Parameters.AddWithValue("$disposition", (int)disposition);

            _ = command.Parameters.AddWithValue("$stripped", stripped.Bytes);

            _ = command.Parameters.AddWithValue("$now", now);

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _tombstones.Add(item);

        }

        return Result.Success();

    }

    /// <summary>
    /// Deletes only the exact rows this run enumerated, one at a time.
    /// </summary>
    /// <remarks>
    /// Row by row rather than a bulk statement, so a row that appeared between the inventory and the
    /// delete survives and is caught by the final count check instead of being removed by a predicate
    /// nobody enumerated.
    /// </remarks>
    private async Task<Result> DeleteGuardedAsync(
        string table,
        string keyColumn,
        List<SourceRow> rows,
        CancellationToken cancellationToken)
    {

        foreach (SourceRow row in rows)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.Transaction = _transaction;

            command.CommandText = $"DELETE FROM {table} WHERE {keyColumn} = $key;";

            _ = command.Parameters.AddWithValue("$key", row.SourceRowId);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    $"A staged {table} row changed between its inventory and its removal.");

            }

        }

        return Result.Success();

    }

    private async Task<Result> DeleteLabelsAsync(CancellationToken cancellationToken)
    {

        foreach (TombstoneItem item in _tombstones
            .Where(static item => item.LabelDisposition == RestoredManagedFileLabelDisposition.ExactLabelRemoved)
            .DistinctBy(static item => item.SensitivityLabelId))
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.Transaction = _transaction;

            command.CommandText = "DELETE FROM artifact_sensitivity WHERE LabelId = $label;";

            _ = command.Parameters.AddWithValue("$label", item.SensitivityLabelId);

            _removedLabels += checked((ulong)await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false));

        }

        return Result.Success();

    }

    private async Task<bool> LabelExistsAsync(string labelId, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = "SELECT 1 FROM artifact_sensitivity WHERE LabelId = $label LIMIT 1;";

        _ = command.Parameters.AddWithValue("$label", labelId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;

    }

    private async Task<long> CountAsync(string table, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = $"SELECT COUNT(*) FROM {table};";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// A one-way commitment to the authority projection being removed.
    /// </summary>
    private CovenantDigest StrippedAuthority(SourceRow row, RestoredManagedFileAuthoritySourceKind kind) =>
        new(SHA256.HashData(
        [
            .. Encoding.ASCII.GetBytes(StrippedAuthorityDomain),
            0x00,
            (byte)kind,
            .. _owner.OperationId.ToByteArray(bigEndian: true),
            .. Encoding.UTF8.GetBytes(row.SourceRowId),
            .. Encoding.UTF8.GetBytes(row.SourceWriteOperationId),
            .. Encoding.UTF8.GetBytes(row.ArtifactId),
            .. Encoding.UTF8.GetBytes(row.SensitivityLabelId),
            (byte)row.OriginalStateCode,
        ]));

    private sealed record SourceRow(
        string SourceRowId,
        string SourceWriteOperationId,
        string ArtifactId,
        string SensitivityLabelId,
        int OriginalStateCode,
        Guid? OwnerCampaignId);

    private sealed record TombstoneItem(
        RestoredManagedFileAuthoritySourceKind SourceKind,
        string SourceRowId,
        string SourceWriteOperationId,
        string ArtifactId,
        string SensitivityLabelId,
        int OriginalStateCode,
        int OwnerScopeCode,
        Guid? OwnerCampaignId,
        RestoredManagedFileLabelDisposition LabelDisposition,
        CovenantDigest StrippedAuthorityDigest);

}

/// <summary>
/// The exact statement sequence restore-staging authority sanitation runs.
/// </summary>
/// <remarks>
/// A static method taking only the sealed session, so it has no way to reach a connection, a
/// filesystem service, a managed-file opener, or an ownership verifier. It makes no filesystem call at
/// all: the rows it removes describe files that belong to a different machine, and touching them would
/// be acting on authority this installation has just proven it does not have (§10.16).
/// </remarks>
internal static class BackupRestoreManagedAuthoritySanitizer
{

    internal static async Task<Result<BackupRestoreManagedAuthoritySanitizationReceipt>> ExecuteInSessionAsync(
        RestoreStagingManagedAuthoritySanitizationSession session,
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity run,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(session);

        ArgumentNullException.ThrowIfNull(run);

        // Source tombstones before linked local tombstones, both before any delete, local rows before
        // labels, and labels before the managed sources that referenced them.
        Func<Task<Result>>[] ladder =
        [
            () => session.InventoryManagedWriteIntentsAsync(run, cancellationToken),
            () => session.InsertAndValidateManagedSourceTombstonesAsync(run, cancellationToken),
            () => session.InventoryLocalErasureWorkItemsAsync(run, cancellationToken),
            () => session.InsertAndValidateLinkedLocalTombstonesAsync(run, cancellationToken),
            () => session.GuardDeleteLocalErasureWorkItemsAsync(run, cancellationToken),
            () => session.DeleteExactAdoptedLabelsAsync(run, cancellationToken),
            () => session.GuardDeleteManagedWriteIntentsAsync(run, cancellationToken),
        ];

        foreach (Func<Task<Result>> step in ladder)
        {

            Result executed = await step().ConfigureAwait(false);

            if (executed.IsFailure)
            {

                return executed.Error;

            }

        }

        return await session.VerifyCompleteAsync(run, cancellationToken).ConfigureAwait(false);

    }

}
