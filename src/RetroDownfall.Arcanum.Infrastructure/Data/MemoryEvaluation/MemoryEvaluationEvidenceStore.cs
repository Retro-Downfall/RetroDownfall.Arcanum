using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

/// <summary>Metadata-only, immutable evidence on the admitted Core connection. Writer admission and quota share one immediate transaction.</summary>
internal sealed partial class MemoryEvaluationEvidenceStore(ICovenantConnectionSource connections) : IMemoryEvaluationEvidenceStore
{
    private static readonly Error Invalid = new(ErrorCodes.MemoryEvaluation.InvalidMetadata, "Evaluation metadata is invalid or exceeds its bounded content-free contract.");

    private static readonly Error Missing = new(ErrorCodes.MemoryEvaluation.NotFound, "Evaluation evidence is absent or has been erased.");

    private static readonly Error Conflict = new("Security.IdempotencyConflict", "The retained evaluation has different immutable evidence.");

    private static readonly Error Unavailable = new(ErrorCodes.MemoryEvaluation.Unavailable, "Evaluation metadata storage is unavailable.");

    private static readonly Error Quota = new(ErrorCodes.MemoryEvaluation.QuotaExceeded, "The 1,024 evaluation limit requires confirmed report-only cleanup through memory-evaluations/delete.");

    public Task<Result<MemoryEvaluationStoredEvidence>> RegisterAsync(MemoryEvaluationRegistration registration, ImmutableArray<MemoryEvaluationSourceCoordinate> sources, CancellationToken ct) =>
        RunAsync<MemoryEvaluationStoredEvidence>(true, async (connection, transaction) =>
        {
            if (!ValidRegistration(registration, sources)) return Invalid;

            string registrationJson = JsonSerializer.Serialize(registration, GrimoireJsonContext.Default.MemoryEvaluationRegistration);

            string keys = JsonSerializer.Serialize(ErasureKeys(sources), GrimoireJsonContext.Default.ImmutableArrayString);

            string[] coordinateJson = sources.Select(source => JsonSerializer.Serialize(source, GrimoireJsonContext.Default.MemoryEvaluationSourceCoordinate)).ToArray();

            int preparedBytes = PreparedSize(registration, sources, registrationJson, keys, coordinateJson);

            if (!FitsTerminalBudget(registration, sources, preparedBytes)) return Invalid;

            MemoryEvaluationStoredEvidence? existing = await ReadAsync(connection, transaction, registration.EvaluationId, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                return SamePrepared(existing, registration, sources) ? existing : Conflict;
            }

            await using SqliteCommand count = Command(connection, transaction, "SELECT count(*) FROM memory_evaluations");

            if ((long)(await count.ExecuteScalarAsync(ct).ConfigureAwait(false))! >= MemoryEvaluationLimits.RetainedRegistrations) return Quota;

            // The deferred FK permits child-first insertion. Root insertion seals the complete manifest;

            // later source inserts are refused by trigger, including on an FK-disabled connection.
            for (int index = 0; index < sources.Length; index++)
            {
                MemoryEvaluationSourceCoordinate source = sources[index];
                await using SqliteCommand insert = Command(connection, transaction, """
                    INSERT INTO memory_evaluation_sources(EvaluationId,Ordinal,StoreCode,RoleCode,SubjectId,VersionId,CoordinateJson)
                    VALUES($id,$ordinal,$store,$role,$subject,$version,$json)
                    """, ("$id", Id(registration.EvaluationId)), ("$ordinal", index), ("$store", (int)source.Store), ("$role", (int)source.Role),
                    ("$subject", source.SubjectId), ("$version", source.VersionId), ("$json", coordinateJson[index]));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using SqliteCommand root = Command(connection, transaction, """
                INSERT INTO memory_evaluations(EvaluationId,OperationId,DatasetGeneration,ScopeCode,CampaignId,CreatedAtUtc,SourceAsOfUtc,SourceCount,RegistrationJson,ErasureKeysJson,PreparedBytes)
                VALUES($id,$operation,$dataset,$scope,$campaign,$created,$asof,$count,$registration,$keys,$bytes)
                """, ("$id", Id(registration.EvaluationId)), ("$operation", Id(registration.OperationId)), ("$dataset", Id(registration.Ownership.DatasetGeneration)),
                ("$scope", (int)registration.Ownership.Scope), ("$campaign", registration.Ownership.CampaignId is { } campaign ? Id(campaign) : null),
                ("$count", sources.Length), ("$registration", registrationJson), ("$keys", keys), ("$bytes", preparedBytes));
            UtcInstantSql.AddParameter(root, "$created", registration.CreatedAtUtc);

            UtcInstantSql.AddParameter(root, "$asof", registration.SourceAsOfUtc!.Value);

            await root.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return new MemoryEvaluationStoredEvidence(registration, sources, null, null);
        }, ct);

    public Task<Result<MemoryEvaluationOwnership>> ReadOwnershipAsync(Guid evaluationId, CancellationToken ct) =>
        RunAsync<MemoryEvaluationOwnership>(false, async (connection, transaction) =>
        {
            MemoryEvaluationStoredEvidence? value = await ReadAsync(connection, transaction, evaluationId, ct).ConfigureAwait(false);

            return value is null ? Missing : value.Registration.Ownership;
        }, ct);

    public Task<Result<MemoryEvaluationStoredEvidence>> GetAsync(Guid evaluationId, CancellationToken ct) =>
        RunAsync<MemoryEvaluationStoredEvidence>(false, async (connection, transaction) =>
        {
            MemoryEvaluationStoredEvidence? value = await ReadAsync(connection, transaction, evaluationId, ct).ConfigureAwait(false);

            return value is null ? Missing : value;
        }, ct);

    public Task<Result<MemoryEvaluationStoredEvidence>> CompleteAsync(Guid evaluationId, MemoryEvaluationTerminalReport report, CancellationToken ct) =>
        RunAsync<MemoryEvaluationStoredEvidence>(true, async (connection, transaction) =>
        {
            MemoryEvaluationStoredEvidence? existing = await ReadAsync(connection, transaction, evaluationId, ct).ConfigureAwait(false);

            if (existing is null) return Missing;

            if (report is null || !SamePrepared(existing, report.Registration, report.Sources) || !ValidTerminal(report)) return Invalid;

            CovenantDigest digest = MemoryEvaluationDigests.Report(report);

            if (existing.Report is not null) return existing.ReportDigest == digest ? existing : Conflict;

            var evidence = new MemoryEvaluationStoredEvidence(existing.Registration, existing.Sources, report, digest);

            if (JsonSerializer.SerializeToUtf8Bytes(evidence, GrimoireJsonContext.Default.MemoryEvaluationStoredEvidence).Length > MemoryEvaluationLimits.EvidenceBytes) return Invalid;

            string aggregate = JsonSerializer.Serialize(report.Aggregate, GrimoireJsonContext.Default.MemoryEvaluationAggregate);

            if (Utf8.GetByteCount(aggregate) + 512 > TerminalReserveBytes) return Invalid;
            await using SqliteCommand insert = Command(connection, transaction, """
                INSERT INTO memory_evaluation_reports(EvaluationId,CompletedAtUtc,StateCode,OutcomeCode,ReasonCode,AggregateJson,InputTokens,OutputTokens,Cost,ElapsedMilliseconds,ReportDigest)
                SELECT EvaluationId,$completed,$state,$outcome,$reason,$aggregate,$input,$output,$cost,$elapsed,$digest
                FROM memory_evaluations WHERE EvaluationId = $id
                """, ("$id", Id(evaluationId)), ("$state", (int)report.State), ("$outcome", (int)report.Decision.Outcome), ("$reason", (int)report.Decision.Reason),
                ("$aggregate", aggregate), ("$input", report.InputTokens), ("$output", report.OutputTokens),
                ("$cost", report.Cost?.ToString("G29", CultureInfo.InvariantCulture)), ("$elapsed", report.ElapsedMilliseconds), ("$digest", digest.Bytes));
            UtcInstantSql.AddParameter(insert, "$completed", report.CompletedAtUtc);

            return await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? evidence : Missing;
        }, ct);

    public Task<Result<MemoryEvaluationDeleteResult>> DeleteAsync(MemoryEvaluationDeleteRequest request, CancellationToken ct) =>
        RunAsync<MemoryEvaluationDeleteResult>(true, async (connection, transaction) =>
        {
            if (request is null || !request.Confirm || request.EvaluationId == Guid.Empty || !request.ExpectedReportDigest.IsValid) return Invalid;

            MemoryEvaluationStoredEvidence? existing = await ReadAsync(connection, transaction, request.EvaluationId, ct).ConfigureAwait(false);

            if (existing is null) return new MemoryEvaluationDeleteResult(request.EvaluationId, false);

            if (existing.Report is null || existing.ReportDigest != request.ExpectedReportDigest) return Conflict;

            await using SqliteCommand delete = Command(connection, transaction,
                "DELETE FROM memory_evaluation_reports WHERE EvaluationId = $id AND ReportDigest = $digest", ("$id", Id(request.EvaluationId)), ("$digest", request.ExpectedReportDigest.Bytes));
            if (await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;

            delete.CommandText = "DELETE FROM memory_evaluation_sources WHERE EvaluationId = $id";

            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            delete.CommandText = "DELETE FROM memory_evaluations WHERE EvaluationId = $id";

            if (await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;

            return new MemoryEvaluationDeleteResult(request.EvaluationId, true);
        }, ct);

    private async Task<Result<T>> RunAsync<T>(bool write, Func<SqliteConnection, SqliteTransaction, Task<Result<T>>> action, CancellationToken ct)
    {
        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(ct).ConfigureAwait(false);

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: !write);

                if (await GrimoireCoreSchemaVersion.ReadAsync(connection, ct, transaction).ConfigureAwait(false) < 19) return Result<T>.Failure(Unavailable);

                Result<T> result = await action(connection, transaction).ConfigureAwait(false);

                if (result.IsSuccess) await transaction.CommitAsync(ct).ConfigureAwait(false);

                return result;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SqliteException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return Unavailable;
        }
    }

    private static async Task<MemoryEvaluationStoredEvidence?> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, Guid evaluationId, CancellationToken ct)
    {
        if (evaluationId == Guid.Empty) return null;

        await using SqliteCommand root = Command(connection, transaction, "SELECT RegistrationJson,SourceCount,ErasureKeysJson FROM memory_evaluations WHERE EvaluationId = $id AND length(CAST(RegistrationJson AS BLOB)) <= 65536 AND length(CAST(ErasureKeysJson AS BLOB)) <= 65536", ("$id", Id(evaluationId)));

        MemoryEvaluationRegistration registration;

        int count;

        string erasureKeys;

        await using (SqliteDataReader reader = await root.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

            registration = JsonSerializer.Deserialize(reader.GetString(0), GrimoireJsonContext.Default.MemoryEvaluationRegistration) ?? throw new InvalidOperationException();

            count = reader.GetInt32(1);

            erasureKeys = reader.GetString(2);
        }

        await using SqliteCommand sources = Command(connection, transaction, "SELECT CoordinateJson FROM memory_evaluation_sources WHERE EvaluationId = $id AND length(CAST(CoordinateJson AS BLOB)) <= 65536 ORDER BY Ordinal LIMIT 129", ("$id", Id(evaluationId)));

        ImmutableArray<MemoryEvaluationSourceCoordinate>.Builder coordinates = ImmutableArray.CreateBuilder<MemoryEvaluationSourceCoordinate>();

        await using (SqliteDataReader reader = await sources.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) coordinates.Add(JsonSerializer.Deserialize(reader.GetString(0), GrimoireJsonContext.Default.MemoryEvaluationSourceCoordinate) ?? throw new InvalidOperationException());
        }
        ImmutableArray<MemoryEvaluationSourceCoordinate> manifest = coordinates.ToImmutable();

        if (manifest.Length != count || registration.EvaluationId != evaluationId || !ValidRegistration(registration, manifest)) throw new InvalidOperationException();

        if (erasureKeys != JsonSerializer.Serialize(ErasureKeys(manifest), GrimoireJsonContext.Default.ImmutableArrayString)) throw new InvalidOperationException();

        await using SqliteCommand terminal = Command(connection, transaction, "SELECT CompletedAtUtc,StateCode,OutcomeCode,ReasonCode,AggregateJson,InputTokens,OutputTokens,Cost,ElapsedMilliseconds,ReportDigest FROM memory_evaluation_reports WHERE EvaluationId = $id AND length(CAST(AggregateJson AS BLOB)) <= 8192", ("$id", Id(evaluationId)));

        await using SqliteDataReader report = await terminal.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await report.ReadAsync(ct).ConfigureAwait(false)) return new(registration, manifest, null, null);

        MemoryEvaluationTerminalReport value = new(registration, UtcInstantText.Parse(report.GetString(0)), (MemoryEvaluationState)report.GetInt32(1),
            new((MemoryEvaluationOutcome)report.GetInt32(2), (MemoryEvaluationReason)report.GetInt32(3)),
            JsonSerializer.Deserialize(report.GetString(4), GrimoireJsonContext.Default.MemoryEvaluationAggregate) ?? throw new InvalidOperationException(), manifest,
            report.IsDBNull(5) ? null : report.GetInt64(5), report.IsDBNull(6) ? null : report.GetInt64(6),
            report.IsDBNull(7) ? null : decimal.Parse(report.GetString(7), NumberStyles.Float, CultureInfo.InvariantCulture), report.GetInt64(8), registration.Dependencies);
        CovenantDigest digest = new((byte[])report[9]);

        if (!ValidTerminal(value) || MemoryEvaluationDigests.Report(value) != digest) throw new InvalidOperationException();

        return new(registration, manifest, value, digest);
    }

    private static bool SamePrepared(MemoryEvaluationStoredEvidence existing, MemoryEvaluationRegistration registration, ImmutableArray<MemoryEvaluationSourceCoordinate> sources) =>
        JsonSerializer.Serialize(existing.Registration, GrimoireJsonContext.Default.MemoryEvaluationRegistration) == JsonSerializer.Serialize(registration, GrimoireJsonContext.Default.MemoryEvaluationRegistration)
        && existing.Sources.SequenceEqual(sources);

    private static string Id(Guid id) => id.ToString("N");

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        return command;
    }
}
