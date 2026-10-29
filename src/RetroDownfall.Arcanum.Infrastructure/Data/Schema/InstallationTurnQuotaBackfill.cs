using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Reconciles installation lifetime capacity against the complete surviving owner set.</summary>
internal sealed class InstallationTurnQuotaBackfill : IGrimoireRestartableReadOnlyBackfill
{
    public string Name => "installation-turn-quota-owner-census";

    public int MaxRowsPerBatch => 128;

    public void ValidateRestartCursor(string? cursor, long rowsProcessed)
    {
        if (cursor is null)
        {
            if (rowsProcessed < 0) throw Refused();

            return;
        }

        Cursor state = Parse(cursor);

        if (rowsProcessed != checked(state.Owners + state.Quotas)) throw Refused();
    }

    public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(SqliteConnection connection,
        SqliteTransaction transaction, string? cursor, CancellationToken cancellationToken) =>
        throw Refused();

    public async Task<GrimoireSchemaBackfillBatch> AdvanceQuiescentBatchAsync(SqliteConnection connection,
        SqliteTransaction transaction, string? cursor, GrimoireSchemaQuiescentExecution execution,
        CancellationToken cancellationToken)
    {
        execution.Require(connection);

        CovenantMutationTransaction owned = new(connection, transaction);

        Totals original = await ReadInstallationAsync(owned, cancellationToken).ConfigureAwait(false);

        Cursor state = cursor is null ? new(1, "", 0, 0, new(0, 0, 0), original) : Parse(cursor);

        if (state.Original != original) throw Refused();

        int rows = 0;

        await using (SqliteCommand read = owned.CreateCommand())
        {
            string afterPredicate = state.After.Length == 0
                ? string.Empty
                : state.Phase == 1 ? "WHERE s.Id COLLATE BINARY > $after" : "WHERE q.SessionId COLLATE BINARY > $after";

            read.CommandText = state.Phase == 1
                ? $"""
                    SELECT s.Id, q.SessionId, q.ClaimCount, q.ReservedFinalizationCount, q.ConsumedFinalizationCount,
                           typeof(q.ClaimCount), typeof(q.ReservedFinalizationCount), typeof(q.ConsumedFinalizationCount),
                           EXISTS(SELECT 1 FROM Sessions other INDEXED BY IX_Sessions_Id_Norm
                               WHERE lower(replace(other.Id,'-',''))=lower(replace(s.Id,'-',''))
                                 AND other.Id COLLATE BINARY<>s.Id LIMIT 1)
                    FROM Sessions s LEFT JOIN session_turn_quota_state q ON q.SessionId = s.Id
                    {afterPredicate} ORDER BY s.Id COLLATE BINARY LIMIT $limit;
                    """
                : $"""
                    SELECT q.SessionId, s.Id FROM session_turn_quota_state q LEFT JOIN Sessions s ON s.Id = q.SessionId
                    {afterPredicate} ORDER BY q.SessionId COLLATE BINARY LIMIT $limit;
                    """;

            _ = read.Parameters.AddWithValue("$after", state.After);

            _ = read.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string identity = reader.GetString(0);

                RequireStoredIdentity(identity);

                if (reader.IsDBNull(1) || !string.Equals(identity, reader.GetString(1), StringComparison.Ordinal)
                    || string.CompareOrdinal(identity, state.After) <= 0) throw Refused();

                rows++;

                if (state.Phase == 1)
                {
                    if (reader.GetInt64(8) != 0) throw Refused();

                    if (reader.GetString(5) != "integer" || reader.GetString(6) != "integer" || reader.GetString(7) != "integer") throw Refused();

                    Totals owner = new(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));

                    Validate(owner, CovenantLimits.MaxPublicTurnClaimsPerSession, CovenantLimits.MaxAssistantFinalizationGuardsPerSession);

                    Totals sum = new(checked(state.Sum.Claims + owner.Claims), checked(state.Sum.Reserved + owner.Reserved), checked(state.Sum.Consumed + owner.Consumed));

                    if (sum.Claims > original.Claims || sum.Reserved > original.Reserved || sum.Consumed > original.Consumed) throw Refused();

                    state = state with { After = identity, Owners = checked(state.Owners + 1), Sum = sum };
                }
                else
                {
                    state = state with { After = identity, Quotas = checked(state.Quotas + 1) };

                    if (state.Quotas > state.Owners) throw Refused();
                }
            }
        }

        if (rows == MaxRowsPerBatch) return new(Serialize(state), rows, false);

        if (state.Phase == 1)
        {
            return new(Serialize(state with { Phase = 2, After = "" }), rows, false);
        }

        if (state.Quotas != state.Owners) throw Refused();

        Result corrected = await new CovenantQuotaGuard().CorrectInstallationCapacityAsync(
            original.Claims, original.Reserved, original.Consumed,
            state.Sum.Claims, state.Sum.Reserved, state.Sum.Consumed,
            owned, execution, cancellationToken).ConfigureAwait(false);

        if (corrected.IsFailure || await ReadInstallationAsync(owned, cancellationToken).ConfigureAwait(false) != state.Sum) throw Refused();

        return new(Serialize(state), rows, true);
    }

    private static async Task<Totals> ReadInstallationAsync(CovenantMutationTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand read = transaction.CreateCommand();

        read.CommandText = """
            SELECT StateKey, ClaimCount, ReservedFinalizationCount, ConsumedFinalizationCount,
                   typeof(ClaimCount), typeof(ReservedFinalizationCount), typeof(ConsumedFinalizationCount)
            FROM installation_turn_quota_state ORDER BY StateKey LIMIT 2;
            """;

        await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(0) != 1
            || reader.GetString(4) != "integer" || reader.GetString(5) != "integer" || reader.GetString(6) != "integer") throw Refused();

        Totals value = new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));

        Validate(value, CovenantLimits.MaxPublicTurnClaimsInstallationWide, CovenantLimits.MaxAssistantFinalizationGuardsInstallationWide);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw Refused();

        return value;
    }

    private static void Validate(Totals value, long claimLimit, long finalizationLimit)
    {
        if (value.Claims < 0 || value.Claims > claimLimit || value.Reserved < 0 || value.Reserved > value.Claims
            || value.Consumed < 0 || value.Consumed > finalizationLimit - value.Reserved) throw Refused();
    }

    private static void RequireStoredIdentity(string identity)
    {
        if (!Guid.TryParseExact(identity, "D", out Guid parsed) || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), identity, StringComparison.OrdinalIgnoreCase)) throw Refused();
    }

    private static Cursor Parse(string cursor)
    {
        if (cursor.Length > 256) throw Refused();

        string[] fields = cursor.Split('|');

        if (fields.Length != 11 || fields[0] != "v1" || fields[1] is not ("1" or "2")) throw Refused();

        if (fields[2].Length != 0) RequireStoredIdentity(fields[2]);

        long[] numbers = new long[8];

        for (int i = 0; i < numbers.Length; i++)
        {
            if (!long.TryParse(fields[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])
                || fields[i + 3] != numbers[i].ToString(CultureInfo.InvariantCulture)) throw Refused();
        }

        Cursor value = new(fields[1] == "1" ? 1 : 2, fields[2], numbers[0], numbers[1],
            new(numbers[2], numbers[3], numbers[4]), new(numbers[5], numbers[6], numbers[7]));

        if ((value.After.Length > 0 && (value.Phase == 1 ? value.Owners == 0 : value.Quotas == 0))
            || (value.Owners == 0 && value.Sum != new Totals(0, 0, 0))
            || value.Quotas > value.Owners || (value.Phase == 1 && value.Quotas != 0)
            || (value.After.Length == 0 && (value.Phase == 1 ? value.Owners != 0 : value.Quotas != 0))
            || value.Sum.Claims > value.Original.Claims || value.Sum.Reserved > value.Original.Reserved || value.Sum.Consumed > value.Original.Consumed) throw Refused();

        Validate(value.Sum, CovenantLimits.MaxPublicTurnClaimsInstallationWide, CovenantLimits.MaxAssistantFinalizationGuardsInstallationWide);

        Validate(value.Original, CovenantLimits.MaxPublicTurnClaimsInstallationWide, CovenantLimits.MaxAssistantFinalizationGuardsInstallationWide);

        return value;
    }

    private static string Serialize(Cursor value) => string.Create(CultureInfo.InvariantCulture,
        $"v1|{value.Phase}|{value.After}|{value.Owners}|{value.Quotas}|{value.Sum.Claims}|{value.Sum.Reserved}|{value.Sum.Consumed}|{value.Original.Claims}|{value.Original.Reserved}|{value.Original.Consumed}");

    private static InvalidOperationException Refused() => new("The installation turn-capacity census could not prove coherent complete ownership.");

    private sealed record Totals(long Claims, long Reserved, long Consumed);

    private sealed record Cursor(int Phase, string After, long Owners, long Quotas, Totals Sum, Totals Original);
}
