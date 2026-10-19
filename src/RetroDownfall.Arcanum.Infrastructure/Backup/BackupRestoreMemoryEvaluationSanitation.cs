using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>Retains compatible historical evidence only; imported preparations never become executable authority.</summary>
internal static class BackupRestoreMemoryEvaluationSanitation
{
    internal static async Task<Result> SanitizeStagedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);

        if (!await MemoryEvaluationErasurePlan.IsInstalledAsync(connection, transaction, ct).ConfigureAwait(false))
            return await VerifyLegacyAsync(connection, transaction, ct).ConfigureAwait(false);

        string[] ids = await InvalidIdsAsync(connection, transaction, ct).ConfigureAwait(false);

        // Freeze the IDs while source manifests still exist. The final root delete cannot depend on a removed child.
        _ = await MemoryEvaluationErasurePlan.MeasureOrClearAsync(connection, transaction,
            "evaluation.EvaluationId IN (SELECT value FROM json_each($ids))", true, ct,
            ("$ids", JsonSerializer.Serialize(ids, GrimoireJsonContext.Default.StringArray))).ConfigureAwait(false);

        foreach (string table in MemoryEvaluationErasurePlan.Tables.Take(2))
        {
            await using var orphan = connection.CreateCommand();

            orphan.Transaction = transaction;

            orphan.CommandText = $"DELETE FROM {table} WHERE NOT EXISTS (SELECT 1 FROM memory_evaluations root WHERE root.EvaluationId = {table}.EvaluationId)";

            await orphan.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        Result proof = await VerifyAsync(connection, transaction, ct).ConfigureAwait(false);

        if (proof.IsSuccess) await transaction.CommitAsync(ct).ConfigureAwait(false);

        return proof;
    }

    internal static async Task<Result> VerifyCommittedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var transaction = connection.BeginTransaction(deferred: true);

        return await VerifyAsync(connection, transaction, ct).ConfigureAwait(false);
    }

    private static async Task<Result> VerifyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (!await MemoryEvaluationErasurePlan.IsInstalledAsync(connection, transaction, ct).ConfigureAwait(false))
            return await VerifyLegacyAsync(connection, transaction, ct).ConfigureAwait(false);

        if ((await InvalidIdsAsync(connection, transaction, ct).ConfigureAwait(false)).Length != 0) return Unknown();

        foreach (string table in MemoryEvaluationErasurePlan.Tables.Take(2))
        {
            await using var command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE NOT EXISTS (SELECT 1 FROM memory_evaluations root WHERE root.EvaluationId = {table}.EvaluationId))";

            if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0) return Unknown();
        }

        return Result.Success();
    }

    private static async Task<string[]> InvalidIdsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        string? generation = null;

        bool canonical = await BackupRestoreDatabaseWorker.TableExistsAsync(connection, "covenant_state", ct, transaction).ConfigureAwait(false);

        if (canonical)
        {
            await using var state = connection.CreateCommand();

            state.Transaction = transaction;

            state.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1";

            if (await state.ExecuteScalarAsync(ct).ConfigureAwait(false) is byte[] bytes && bytes.Length == 16) generation = new Guid(bytes).ToString("N");
        }

        string covenantInvalid = canonical
            ? "NOT EXISTS (SELECT 1 FROM covenant_versions version WHERE lower(replace(version.VersionId, '-', '')) = lower(replace(source.VersionId, '-', '')) AND lower(replace(version.EntryId, '-', '')) = lower(replace(source.SubjectId, '-', '')))"
            : "1 = 1";

        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"""
            SELECT evaluation.EvaluationId FROM memory_evaluations evaluation
            WHERE $generation IS NULL OR evaluation.DatasetGeneration <> $generation
              OR NOT EXISTS (SELECT 1 FROM memory_evaluation_reports report WHERE report.EvaluationId = evaluation.EvaluationId)
              OR evaluation.SourceCount <> (SELECT count(*) FROM memory_evaluation_sources source WHERE source.EvaluationId = evaluation.EvaluationId)
              OR (evaluation.CampaignId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Campaigns campaign WHERE lower(replace(campaign.Id, '-', '')) = evaluation.CampaignId))
              OR EXISTS (SELECT 1 FROM memory_evaluation_sources source WHERE source.EvaluationId = evaluation.EvaluationId AND (
                  (json_extract(source.CoordinateJson, '$.canonicalOwnerSessionId') IS NOT NULL AND NOT EXISTS (
                      SELECT 1 FROM Sessions owner WHERE lower(replace(owner.Id, '-', '')) = lower(replace(json_extract(source.CoordinateJson, '$.canonicalOwnerSessionId'), '-', ''))))
                  OR (source.StoreCode = 1 AND ({covenantInvalid}))
                  OR (source.StoreCode IN (2,3) AND NOT EXISTS (
                      SELECT 1 FROM annal_versions version JOIN annal_claims claim ON claim.ClaimId = version.ClaimId
                      WHERE version.VersionId = source.VersionId AND claim.ClaimId = json_extract(source.CoordinateJson, '$.claimId')
                        AND claim.SubjectStoreCode = source.StoreCode - 1 AND claim.SubjectId = source.SubjectId))
                  OR (source.StoreCode = 2 AND NOT EXISTS (SELECT 1 FROM saga_memories memory WHERE memory.Id = source.SubjectId
                      AND lower(replace(memory.SessionId, '-', '')) IS lower(replace(json_extract(source.CoordinateJson, '$.canonicalOwnerSessionId'), '-', ''))))
                  OR (source.StoreCode = 3 AND NOT EXISTS (SELECT 1 FROM lexicon_entries entry WHERE entry.Id = source.SubjectId))
              ))
            ORDER BY evaluation.EvaluationId
            """;

        command.Parameters.AddWithValue("$generation", (object?)generation ?? DBNull.Value);

        List<string> ids = [];

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetString(0));

        return [.. ids];
    }

    private static async Task<Result> VerifyLegacyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        foreach (string table in MemoryEvaluationErasurePlan.Tables)
            if (await BackupRestoreDatabaseWorker.TableExistsAsync(connection, table, ct, transaction).ConfigureAwait(false)) return Unknown();

        return Result.Success();
    }

    private static Result Unknown() => new Error(ErrorCodes.Covenant.ManualRecoveryRequired,
        "Restored evaluation evidence has incompatible ownership, source coordinates or incomplete execution state.");
}
