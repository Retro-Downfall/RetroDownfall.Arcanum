using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

/// <summary>Complete evidence closure. Every selector reads the immutable root until its final deletion.</summary>
internal static class MemoryEvaluationErasurePlan
{
    internal static readonly string[] Tables = ["memory_evaluation_reports", "memory_evaluation_sources", "memory_evaluations"];

    internal static string StorePredicate(MemoryEvaluationSource store) =>
        $"EXISTS (SELECT 1 FROM json_each(evaluation.ErasureKeysJson) key WHERE key.value LIKE '{(int)store}:subject:%')";

    internal static string SubjectPredicate(MemoryEvaluationSource store, string subjects) =>
        $"EXISTS (SELECT 1 FROM json_each(evaluation.ErasureKeysJson) key WHERE key.value IN (SELECT '{(int)store}:subject:' || subject FROM ({subjects}) AS selected))";

    internal static string SessionPredicate(string sessions) =>
        $"EXISTS (SELECT 1 FROM json_each(evaluation.ErasureKeysJson) key WHERE key.value IN (SELECT 'session:' || lower(replace(subject, '-', '')) FROM ({sessions}) AS selected))";

    internal static string CampaignPredicate => "evaluation.CampaignId = $evaluationOwner";

    // The query must name its sole output column subject. Source identities remain exact opaque strings.
    internal static IReadOnlyList<AnnalsErasureStep> Steps(string rootPredicate)
    {
        string predicate = $"EvaluationId IN (SELECT evaluation.EvaluationId FROM memory_evaluations evaluation WHERE {rootPredicate})";

        return [.. Tables.Select(table => new AnnalsErasureStep(table, predicate, RequiredFromCoreVersion: 19))];
    }

    internal static Task<long> ReadSessionClosureAsync(DbConnection connection, DbTransaction? transaction, Guid sessionId, CancellationToken ct) =>
        MeasureOrClearAsync(connection, transaction, SessionPredicate("SELECT $evaluationOwner AS subject"), false, ct, ("$evaluationOwner", sessionId.ToString("N")));

    internal static Task<long> ClearSessionAsync(DbConnection connection, DbTransaction? transaction, Guid sessionId, CancellationToken ct) =>
        MeasureOrClearAsync(connection, transaction, SessionPredicate("SELECT $evaluationOwner AS subject"), true, ct, ("$evaluationOwner", sessionId.ToString("N")));

    internal static Task<long> ClearCampaignAsync(DbConnection connection, DbTransaction? transaction, Guid campaignId, CancellationToken ct) =>
        MeasureOrClearAsync(connection, transaction, CampaignPredicate, true, ct, ("$evaluationOwner", campaignId.ToString("N")));

    internal static Task<long> ClearStoreAsync(DbConnection connection, DbTransaction? transaction, MemoryEvaluationSource store, CancellationToken ct) =>
        MeasureOrClearAsync(connection, transaction, StorePredicate(store), true, ct);

    internal static async Task<long> MeasureOrClearAsync(DbConnection connection, DbTransaction? transaction, string predicate, bool clear, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var counts = await RunAsync(connection, transaction, predicate, clear, ct, parameters).ConfigureAwait(false);

        return counts.Sum(count => count.Rows);
    }

    internal static async Task<IReadOnlyList<MemoryErasureTableCount>> RunAsync(DbConnection connection, DbTransaction? transaction, string predicate, bool clear, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        if (!await IsInstalledAsync(connection, transaction, ct).ConfigureAwait(false)) return [.. Tables.Select(table => new MemoryErasureTableCount(table, 0))];

        var steps = Steps(predicate);

        List<MemoryErasureTableCount> counts = [];

        foreach (var step in steps)
        {
            counts.Add(new(step.Table, await ExecuteAsync(connection, transaction, step, true, ct, parameters).ConfigureAwait(false)));
        }

        if (clear)
        {
            foreach (var step in steps) await ExecuteAsync(connection, transaction, step, false, ct, parameters).ConfigureAwait(false);
        }

        return counts;
    }

    internal static async Task<bool> IsInstalledAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using DbCommand command = ((SqliteConnection)connection).CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name = 'grimoire_feature_schemas')";

        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0) return false;

        command.CommandText = "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 0 AND TransactionTierCode = 0";

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is long version && version >= 19;
    }

    private static async Task<long> ExecuteAsync(DbConnection connection, DbTransaction? transaction, AnnalsErasureStep step, bool count, CancellationToken ct, (string Name, object? Value)[] parameters)
    {
        await using DbCommand command = ((SqliteConnection)connection).CreateCommand();

        command.Transaction = transaction;

        command.CommandText = count ? $"SELECT count(*) FROM {step.Table} WHERE {step.Predicate}" : $"DELETE FROM {step.Table} WHERE {step.Predicate}";

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value ?? DBNull.Value;

            command.Parameters.Add(parameter);
        }

        return count ? Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) : await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
