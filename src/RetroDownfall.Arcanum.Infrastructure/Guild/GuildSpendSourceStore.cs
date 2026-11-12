using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Stores initial evidence on the caller's admitted physical-settlement transaction.</summary>
internal static class GuildSpendSourceStore
{
    internal static async Task<Result> CaptureInitialWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildBudgetEffectRequest effect, BillableOperationRecord bill, GuildBudgetEffect outcome, long eventSequence, CancellationToken ct)
    {
        byte[] snapshot = Encode(effect, bill);

        byte[] digest = SHA256.HashData(snapshot);

        byte[] result = JsonSerializer.SerializeToUtf8Bytes(outcome, GuildSpendStorageJsonContext.Default.GuildBudgetEffect);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "INSERT INTO guild_spend_sources(EffectId,SourceId,SourceKindCode) VALUES($effect,$source,0) ON CONFLICT(EffectId) DO NOTHING";

        command.Parameters.AddWithValue("$effect", Id(effect.EffectId));

        command.Parameters.AddWithValue("$source", Id(effect.SourceId));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        command.CommandText = """
            UPDATE guild_spend_sources SET InitialSnapshot=$snapshot,InitialDigest=$digest,InitialOutcome=$outcome,
                InitialEventSequence=$sequence,GenerationId=CASE WHEN SourceKindCode=1 THEN $generation ELSE NULL END
            WHERE EffectId=$effect AND SourceId=$source AND InitialSnapshot IS NULL
            """;

        command.Parameters.AddWithValue("$snapshot", snapshot);

        command.Parameters.AddWithValue("$digest", digest);

        command.Parameters.AddWithValue("$outcome", result);

        command.Parameters.AddWithValue("$sequence", eventSequence);

        command.Parameters.AddWithValue("$generation", string.IsNullOrWhiteSpace(bill.ProviderRequestId) ? DBNull.Value : bill.ProviderRequestId);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Result.Failure(Conflict);
    }

    /// <summary>Returns null only when no legacy source row exists; this does not confer settlement authority.</summary>
    internal static async Task<Result<GuildStoredInitialSpend?>> LoadInitialWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid effectId, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT SourceId,InitialSnapshot,InitialDigest,InitialOutcome,InitialEventSequence FROM guild_spend_sources WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$effect", Id(effectId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildStoredInitialSpend?>.Success(null);

        if (reader.IsDBNull(1) && reader.IsDBNull(2) && reader.IsDBNull(3) && reader.IsDBNull(4))
            return Result<GuildStoredInitialSpend?>.Success(null);

        if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4)) return Conflict;

        try
        {
            byte[] bytes = (byte[])reader.GetValue(1);

            byte[] digest = (byte[])reader.GetValue(2);

            if (digest.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), digest)) return Conflict;

            GuildInitialBillSnapshot? snapshot = JsonSerializer.Deserialize(bytes, GuildSpendStorageJsonContext.Default.GuildInitialBillSnapshot);

            byte[] outcomeBytes = (byte[])reader.GetValue(3);

            GuildBudgetEffect? outcome = JsonSerializer.Deserialize(outcomeBytes, GuildSpendStorageJsonContext.Default.GuildBudgetEffect);

            long sequence = reader.GetInt64(4);

            if (snapshot is null || outcome?.Request is null
                || !outcomeBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(outcome, GuildSpendStorageJsonContext.Default.GuildBudgetEffect))
                || sequence < 1 || snapshot.EffectId != effectId
                || reader.GetString(0) != Id(snapshot.SourceId) || snapshot.SourceId == Guid.Empty || snapshot.QuestId == Guid.Empty
                || snapshot.DispatchId == Guid.Empty || snapshot.RunId == Guid.Empty || snapshot.Provider is null || snapshot.Model is null
                || snapshot.Purpose is null || snapshot.PricingSnapshotJson is null || snapshot.ActualCostUsd is null
                || !Enum.IsDefined(snapshot.OperationType) || !Enum.IsDefined(snapshot.Status)
                || snapshot.InputTokens < 0 || snapshot.OutputTokens < 0 || snapshot.CachedTokens < 0
                || snapshot.CachedTokens > snapshot.InputTokens || snapshot.ReasoningTokens < 0 || snapshot.ReasoningTokens > snapshot.OutputTokens
                || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(snapshot, GuildSpendStorageJsonContext.Default.GuildInitialBillSnapshot))) return Conflict;

            decimal usd = ExactUsdText.Parse(snapshot.ActualCostUsd);

            GuildBudgetEffectRequest request = outcome.Request;

            if (usd < 0 || outcome.KnownUsd != usd || outcome.KnownTokens != checked(snapshot.InputTokens + snapshot.OutputTokens)
                || outcome.Revision < 1 || !Enum.IsDefined(outcome.State) || request.Kind != GuildBudgetEffectKind.LocalProvider
                || request.EffectId != effectId || request.SourceId != snapshot.SourceId || request.QuestId != snapshot.QuestId
                || request.DispatchId != snapshot.DispatchId || request.AccountingAssociationId != snapshot.RunId) return Conflict;

            return new GuildStoredInitialSpend(snapshot, outcome, sequence);
        }
        catch (Exception error) when (error is JsonException or InvalidCastException or FormatException or OverflowException)
        {
            return Conflict;
        }
    }

    internal static async Task<Result<GuildBudgetEffect?>> ReadInitialWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildBudgetEffectRequest effect, BillableOperationRecord bill, CancellationToken ct)
    {
        Result<GuildStoredInitialSpend?> loaded = await LoadInitialWithinAsync(connection, transaction, effect.EffectId, ct).ConfigureAwait(false);

        if (loaded.IsFailure) return loaded.Error;

        if (loaded.Value is not { } initial) return Result<GuildBudgetEffect?>.Success(null);

        byte[] saved = JsonSerializer.SerializeToUtf8Bytes(initial.Snapshot, GuildSpendStorageJsonContext.Default.GuildInitialBillSnapshot);

        return saved.AsSpan().SequenceEqual(Encode(effect, bill))
            && initial.Outcome.Request with { ReservationId = effect.ReservationId } == effect
            ? Result<GuildBudgetEffect?>.Success(initial.Outcome) : Conflict;
    }

    private static byte[] Encode(GuildBudgetEffectRequest effect, BillableOperationRecord bill) =>
        JsonSerializer.SerializeToUtf8Bytes(new GuildInitialBillSnapshot(effect.EffectId, effect.QuestId, effect.DispatchId,
            effect.SourceId, bill.RunId, bill.OperationType, bill.Provider, bill.Model, bill.Purpose,
            bill.StartedAt.ToUniversalTime(), bill.CompletedAt.ToUniversalTime(), bill.InputTokens, bill.OutputTokens,
            bill.ReasoningTokens, bill.CachedTokens, bill.PricingSnapshotJson, ExactUsdText.Format(bill.ActualCostUsd),
            bill.Status, bill.ProviderRequestId), GuildSpendStorageJsonContext.Default.GuildInitialBillSnapshot);

    private static string Id(Guid id) => id.ToString("D").ToUpperInvariant();

    private static Error Conflict => new(ErrorCodes.Guild.IdempotencyConflict, "The original physical settlement evidence does not match.");
}
