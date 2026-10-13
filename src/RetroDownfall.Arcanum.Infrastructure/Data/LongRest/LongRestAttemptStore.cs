using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>One bounded current physical attempt, with immutable completion and cumulative accounting.</summary>
internal sealed partial class LongRestAttemptStore(ArcanumDbContext db, TimeProvider timeProvider) : ILongRestAttemptStore
{
    public Task<Result<LongRestDispatchAttempt>> RecordDisclosureAsync(LongRestDispatchAttempt attempt, CovenantDigest receipt, CancellationToken ct) => WithinAsync(async (connection, transaction) =>
    {
        if (!receipt.IsValid || attempt.State != LongRestDispatchAttemptState.Prepared || attempt.DisclosureReceiptDigest is not null)
        {
            throw new InvalidDataException();
        }

        string unit = await CurrentUnitAsync(connection, transaction, attempt.Work, ct).ConfigureAwait(false);

        _ = await ExactAsync(connection, transaction, unit, attempt, ct).ConfigureAwait(false);

        LongRestDispatchAttempt disclosed = attempt with { State = LongRestDispatchAttemptState.Disclosed, Revision = checked(attempt.Revision + 1), DisclosureReceiptDigest = receipt };

        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE long_rest_provider_effects SET DisclosureReceiptDigest = $receipt, StateCode = 2, Revision = $next, UpdatedAtUtc = $at
            WHERE WorkItemId = $unit AND AttemptId = $attempt AND Revision = $revision AND StateCode = 1 AND DisclosureReceiptDigest IS NULL;
            """, ("$receipt", receipt.Bytes), ("$next", disclosed.Revision), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())),
            ("$unit", unit), ("$attempt", Format(attempt.AttemptId)), ("$revision", attempt.Revision));

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        return disclosed;
    }, ct);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<Result<LongRestDispatchAttempt>> PrepareAttemptAsync(LongRestWorkIdentity work, LongRestProviderPayloadKind kind,
        CovenantDigest payload, CovenantDigest destination, CancellationToken ct) => WithinAsync(async (connection, transaction) =>
    {
        if (!payload.IsValid || !destination.IsValid || kind is not (LongRestProviderPayloadKind.Summary or LongRestProviderPayloadKind.Adjudication or LongRestProviderPayloadKind.Embedding))
        {
            throw new InvalidDataException();
        }

        string unit = await CurrentUnitAsync(connection, transaction, work, ct).ConfigureAwait(false);

        Effect? prior = await ReadAsync(connection, transaction, unit, work, ct).ConfigureAwait(false);

        if (prior?.Attempt.State == LongRestDispatchAttemptState.Sent)
        {
            throw new InvalidDataException();
        }

        if (prior is not null)
        {
            await LongRestDiscoveryStore.ValidateStoredWorkAssociationAsync(connection, transaction, prior.OriginalWork, work, ct).ConfigureAwait(false);
        }

        string originalWork = EncodeOriginalWork(work);

        LongRestDispatchAttempt next = new(Guid.NewGuid(), work, kind, checked((prior?.Attempt.PhysicalOrdinal ?? 0) + 1),
            checked((prior?.Attempt.Revision ?? -1) + 1), LongRestDispatchAttemptState.Prepared, payload, destination, null);

        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO long_rest_provider_effects (WorkItemId, WorkDigest, OriginalWorkJson, AttemptId, PayloadKindCode, PhysicalOrdinal, Revision,
                StateCode, PayloadDigest, DestinationDigest, UpdatedAtUtc)
            VALUES ($unit, $work, $original, $attempt, $kind, $ordinal, $revision, 1, $payload, $destination, $at)
            ON CONFLICT(WorkItemId) DO UPDATE SET WorkDigest = excluded.WorkDigest, OriginalWorkJson = excluded.OriginalWorkJson, AttemptId = excluded.AttemptId,
                PayloadKindCode = excluded.PayloadKindCode, PhysicalOrdinal = excluded.PhysicalOrdinal, Revision = excluded.Revision,
                StateCode = 1, PayloadDigest = excluded.PayloadDigest, DestinationDigest = excluded.DestinationDigest,
                DisclosureReceiptDigest = NULL, CompletionJson = NULL, FailureCode = NULL, UpdatedAtUtc = excluded.UpdatedAtUtc
            WHERE long_rest_provider_effects.Revision = $previous AND long_rest_provider_effects.StateCode <> 3;
            """, ("$unit", unit), ("$work", LongRestDiscoveryHash.Work(work).Value.Bytes), ("$original", originalWork), ("$attempt", Format(next.AttemptId)),
            ("$kind", (int)kind), ("$ordinal", checked((long)next.PhysicalOrdinal)), ("$revision", next.Revision),
            ("$payload", payload.Bytes), ("$destination", destination.Bytes), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())),
            ("$previous", prior?.Attempt.Revision ?? -1));

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        return next;
    }, ct);

    public Task<Result<LongRestDispatchAttempt>> MarkSentAsync(LongRestDispatchAttempt attempt, CancellationToken ct) => WithinAsync(async (connection, transaction) =>
    {
        string unit = await CurrentUnitAsync(connection, transaction, attempt.Work, ct).ConfigureAwait(false);

        Effect current = await ExactAsync(connection, transaction, unit, attempt, ct).ConfigureAwait(false);

        LongRestDispatchAttemptState required = attempt.Work.Scope.ProtectedPartitionDigest is null
            ? LongRestDispatchAttemptState.Prepared : LongRestDispatchAttemptState.Disclosed;

        if (attempt.State != required || (required == LongRestDispatchAttemptState.Disclosed && attempt.DisclosureReceiptDigest is not { IsValid: true }))
        {
            throw new InvalidDataException();
        }

        LongRestDispatchAttempt sent = attempt with { State = LongRestDispatchAttemptState.Sent, Revision = checked(attempt.Revision + 1) };

        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE long_rest_provider_effects SET StateCode = 3, Revision = $next, SentCount = $sent, UpdatedAtUtc = $at
            WHERE WorkItemId = $unit AND AttemptId = $attempt AND Revision = $revision AND StateCode = $state;
            """, ("$next", sent.Revision), ("$sent", checked(current.Sent + 1)), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())),
            ("$unit", unit), ("$attempt", Format(attempt.AttemptId)), ("$revision", attempt.Revision), ("$state", (int)required));

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        return sent;
    }, ct);

    public Task<Result<LongRestDispatchAttempt>> CompleteAttemptAsync(LongRestDispatchAttempt attempt, LongRestDispatchCompletion completion, CancellationToken ct) =>
        WithinAsync((connection, transaction) => CompleteWithinAsync(connection, transaction, attempt, completion, timeProvider.GetUtcNow(), ct), ct);

    internal static async Task AcceptWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestPaidEvidence paid, LongRestDispatchCompletion completion, DateTimeOffset now, CancellationToken ct, bool committedOnly = false)
    {
        ValidateCompletion(completion);

        if (paid is null || paid.Attempt is null || completion.State != LongRestDispatchAttemptState.Accepted || paid.Attempt.State != completion.State
            || paid.Attempt.Revision < 1 || paid.InputTokens != completion.Usage.InputTokens
            || paid.OutputTokens != completion.Usage.OutputTokens || paid.CostUsd != completion.CostUsd)
        {
            throw new InvalidDataException();
        }

        // Read-only exact completion replay also works after the fold disposed its unit. This
        // does not relax CurrentUnitAsync for new writes or for accounting compensation.
        await using SqliteCommand replay = Command(connection, transaction,
            "SELECT WorkItemId FROM long_rest_work_items WHERE WorkDigest = $work;",
            ("$work", LongRestDiscoveryHash.Work(paid.Attempt.Work).Value.Bytes));

        if (await replay.ExecuteScalarAsync(ct).ConfigureAwait(false) is string unit)
        {
            Effect? existing = await ReadAsync(connection, transaction, unit, paid.Attempt.Work, ct).ConfigureAwait(false);

            if (existing is not null && existing.Attempt == paid.Attempt && existing.WorkDigest == LongRestDiscoveryHash.Work(paid.Attempt.Work).Value
                && existing.Completion == JsonSerializer.Serialize(completion, LongRestJsonContext.Default.LongRestDispatchCompletion))
            {
                return;
            }
        }

        if (committedOnly) throw new InvalidDataException();

        LongRestDispatchAttempt sent = paid.Attempt with { State = LongRestDispatchAttemptState.Sent, Revision = checked(paid.Attempt.Revision - 1) };

        if (await CompleteWithinAsync(connection, transaction, sent, completion, now, ct).ConfigureAwait(false) != paid.Attempt)
        {
            throw new InvalidDataException();
        }
    }

    private static async Task<LongRestDispatchAttempt> CompleteWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDispatchAttempt attempt, LongRestDispatchCompletion completion, DateTimeOffset now, CancellationToken ct)
    {
        ValidateCompletion(completion);

        string json = JsonSerializer.Serialize(completion, LongRestJsonContext.Default.LongRestDispatchCompletion);

        string unit = await CurrentUnitAsync(connection, transaction, attempt.Work, ct).ConfigureAwait(false);

        Effect current = await ReadAsync(connection, transaction, unit, attempt.Work, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        if (current.Attempt.State is LongRestDispatchAttemptState.Accepted or LongRestDispatchAttemptState.Invalid or LongRestDispatchAttemptState.Failed)
        {
            bool replay = attempt == current.Attempt || (attempt.State == LongRestDispatchAttemptState.Sent
                && attempt with { State = current.Attempt.State, Revision = checked(attempt.Revision + 1) } == current.Attempt);

            if (!replay || current.WorkDigest != LongRestDiscoveryHash.Work(attempt.Work).Value || current.Completion != json)
            {
                throw new InvalidDataException();
            }

            return current.Attempt;
        }

        if (attempt.State != LongRestDispatchAttemptState.Sent || current.Attempt != attempt
            || current.WorkDigest != LongRestDiscoveryHash.Work(attempt.Work).Value || current.Completion is not null)
        {
            throw new InvalidDataException();
        }

        LongRestDispatchAttempt completed = attempt with { State = completion.State, Revision = checked(attempt.Revision + 1) };

        LongRestProviderUsage usage = completion.Usage;

        bool paid = completion.State != LongRestDispatchAttemptState.Failed || usage.InputTokens > 0 || usage.OutputTokens > 0
            || usage.TotalTokens > 0 || usage.CachedInputTokens > 0 || usage.ReasoningTokens > 0 || completion.CostUsd > 0;

        decimal? cost = !paid ? current.Cost : current.Paid == 0 ? completion.CostUsd
            : current.Cost is { } previous && completion.CostUsd is { } added ? checked(previous + added) : null;

        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE long_rest_provider_effects SET StateCode = $state, Revision = $next, CompletionJson = $completion,
                PaidCount = $paid, InvalidCount = $invalid, FailureCount = $failed, InputTokens = $input, OutputTokens = $output,
                TotalTokens = $total, CachedInputTokens = $cached, ReasoningTokens = $reasoning, CostUsd = $cost, FailureCode = $failure, UpdatedAtUtc = $at
            WHERE WorkItemId = $unit AND WorkDigest = $work AND AttemptId = $attempt AND PhysicalOrdinal = $ordinal AND Revision = $revision AND StateCode = 3 AND CompletionJson IS NULL;
            """, ("$state", (int)completed.State), ("$next", completed.Revision), ("$completion", json), ("$paid", checked(current.Paid + (paid ? 1 : 0))),
            ("$invalid", checked(current.Invalid + (completion.State == LongRestDispatchAttemptState.Invalid ? 1 : 0))),
            ("$failed", checked(current.Failed + (completion.State == LongRestDispatchAttemptState.Failed ? 1 : 0))),
            ("$input", checked(current.Usage.InputTokens + usage.InputTokens)), ("$output", checked(current.Usage.OutputTokens + usage.OutputTokens)),
            ("$total", checked(current.Usage.TotalTokens + usage.TotalTokens)), ("$cached", checked(current.Usage.CachedInputTokens + usage.CachedInputTokens)),
            ("$reasoning", checked(current.Usage.ReasoningTokens + usage.ReasoningTokens)), ("$cost", cost?.ToString(CultureInfo.InvariantCulture)),
            ("$failure", completion.FailureCode), ("$at", UtcInstantText.Format(now)), ("$unit", unit),
            ("$work", current.WorkDigest.Bytes), ("$attempt", Format(attempt.AttemptId)), ("$ordinal", checked((long)attempt.PhysicalOrdinal)), ("$revision", attempt.Revision));

        if (StrictUtf8.GetByteCount(json) > 4096 || await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        return completed;
    }

    public async Task<Result> ValidateAttemptAsync(LongRestDispatchAttempt attempt, CancellationToken ct)
    {
        Result<LongRestDispatchAttempt> result = await WithinAsync(async (connection, transaction) =>
        {
            string unit = await CurrentUnitAsync(connection, transaction, attempt.Work, ct).ConfigureAwait(false);

            return (await ExactAsync(connection, transaction, unit, attempt, ct).ConfigureAwait(false)).Attempt;
        }, ct).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    private static void ValidateCompletion(LongRestDispatchCompletion completion)
    {
        if (completion is null || completion.State is not (LongRestDispatchAttemptState.Accepted or LongRestDispatchAttemptState.Invalid or LongRestDispatchAttemptState.Failed)
            || completion.Usage is not { InputTokens: >= 0, OutputTokens: >= 0, TotalTokens: >= 0, CachedInputTokens: >= 0, ReasoningTokens: >= 0 }
            || completion.CostUsd < 0 || (completion.State == LongRestDispatchAttemptState.Accepted && completion.FailureCode is not null)
            || (completion.FailureCode is { } code && (code.Length is < 1 or > 128 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))))
        {
            throw new InvalidDataException();
        }
    }

    private static async Task<string> CurrentUnitAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestWorkIdentity work, CancellationToken ct)
    {
        Result<CovenantDigest> hash = LongRestDiscoveryHash.Work(work);

        if (hash.IsFailure)
        {
            throw new InvalidDataException();
        }

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT unit.WorkItemId, generation.FenceJson FROM long_rest_work_items unit
            JOIN long_rest_discovery_generations generation ON generation.GenerationId = unit.GenerationId AND generation.OperationId = unit.OperationId
            JOIN LongRunningOperations operation ON operation.Id = unit.OperationId AND operation.Kind = 'long-rest-sweep'
            WHERE unit.WorkDigest = $work AND unit.GenerationId = $generation AND unit.OperationId = $operation AND unit.ItemId = $item
                AND unit.KindCode = $kind AND unit.SourceDigest = $source AND unit.RecipeDigest = $recipe AND unit.DispositionCode = 1
                AND generation.ScopeKindCode = $scope AND generation.CampaignId IS $campaign AND generation.PartitionDigest IS $partition
                AND generation.RecipeDigest = $recipe AND generation.PhaseCode NOT IN (6, 7, 8, 9);
            """, ("$work", hash.Value.Bytes), ("$generation", Format(work.GenerationId)), ("$operation", work.OperationId.ToString("N")),
            ("$item", work.ItemId), ("$kind", (int)work.Kind), ("$source", work.SourceDigest.Bytes), ("$recipe", work.RecipeDigest.Bytes),
            ("$scope", (int)work.Scope.Kind), ("$campaign", work.Scope.CampaignId is { } campaign ? Format(campaign) : null), ("$partition", work.Scope.ProtectedPartitionDigest?.Bytes));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidDataException();
        }

        LongRestStoredFence fence = JsonSerializer.Deserialize(reader.GetString(1), LongRestJsonContext.Default.LongRestStoredFence) ?? throw new InvalidDataException();

        if (fence.ToDomain(work.Scope) != work.Fence)
        {
            throw new InvalidDataException();
        }

        string unit = reader.GetString(0);

        await reader.DisposeAsync().ConfigureAwait(false);

        await LongRestDiscoveryStore.ValidateAttemptSourceMetadataAsync(connection, transaction, work, unit, ct).ConfigureAwait(false);

        return unit;
    }

    private static async Task<Effect> ExactAsync(SqliteConnection connection, SqliteTransaction transaction, string unit, LongRestDispatchAttempt attempt, CancellationToken ct)
    {
        Effect current = await ReadAsync(connection, transaction, unit, attempt.Work, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        if (current.Attempt != attempt || current.WorkDigest != LongRestDiscoveryHash.Work(attempt.Work).Value)
        {
            throw new InvalidDataException();
        }

        return current;
    }

    private static async Task<Effect?> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, string unit, LongRestWorkIdentity work, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT WorkDigest, AttemptId, PayloadKindCode, PhysicalOrdinal, Revision, StateCode, PayloadDigest, DestinationDigest,
                DisclosureReceiptDigest, SentCount, PaidCount, InvalidCount, FailureCount, InputTokens, OutputTokens, TotalTokens,
                CachedInputTokens, ReasoningTokens, CostUsd, CompletionJson, OriginalWorkJson FROM long_rest_provider_effects WHERE WorkItemId = $unit;
            """, ("$unit", unit));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        LongRestDispatchAttempt attempt = new(Guid.Parse(reader.GetString(1)), work, (LongRestProviderPayloadKind)reader.GetInt32(2),
            checked((ulong)reader.GetInt64(3)), reader.GetInt64(4), (LongRestDispatchAttemptState)reader.GetInt32(5),
            new((byte[])reader[6]), new((byte[])reader[7]), reader.IsDBNull(8) ? null : new CovenantDigest((byte[])reader[8]));

        CovenantDigest digest = new((byte[])reader[0]);

        LongRestWorkIdentity original = ReadOriginalWork(reader.GetString(20), digest);

        if (original with { Fence = work.Fence } != work
            || attempt.AttemptId == Guid.Empty || attempt.PhysicalOrdinal == 0 || attempt.Revision < 0
            || !attempt.PayloadDigest.IsValid || !attempt.DestinationDigest.IsValid
            || (original.Kind == LongRestWorkKind.Anchor
                ? attempt.PayloadKind != LongRestProviderPayloadKind.Adjudication
                : original.Kind != LongRestWorkKind.HierarchyFold || attempt.PayloadKind is not (LongRestProviderPayloadKind.Summary or LongRestProviderPayloadKind.Embedding)))
        {
            throw new InvalidDataException();
        }

        return new(attempt, digest, original, reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12),
            new(reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15), reader.GetInt64(16), reader.GetInt64(17)),
            reader.IsDBNull(18) ? null : decimal.Parse(reader.GetString(18), CultureInfo.InvariantCulture), reader.IsDBNull(19) ? null : reader.GetString(19));
    }

    private async Task<Result<T>> WithinAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

                if (connection.State != ConnectionState.Open)
                {
                    await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                }

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                T result = await action(connection, transaction).ConfigureAwait(false);

                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return Result<T>.Success(result);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or ArgumentException or FormatException or OverflowException or JsonException or InvalidOperationException)
        {
            return new Error(ErrorCodes.LongRest.StaleInput, "The physical attempt no longer matches exact current work or completion evidence.");
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] values)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object? value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private sealed record Effect(LongRestDispatchAttempt Attempt, CovenantDigest WorkDigest, LongRestWorkIdentity OriginalWork, long Sent, long Paid, long Invalid, long Failed,
        LongRestProviderUsage Usage, decimal? Cost, string? Completion);
}
