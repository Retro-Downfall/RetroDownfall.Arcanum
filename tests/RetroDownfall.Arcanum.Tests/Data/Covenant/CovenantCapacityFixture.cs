using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Turn-capacity helpers over a canonical scratch Grimoire: the core counter tables, a Session, and
/// the caller-owned transaction the quota guard runs inside.
/// </summary>
internal static class CovenantCapacityFixture
{

    private static readonly DateTimeOffset SeedTime = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The always-present core objects the capacity ledger needs, in dependency order.
    /// </summary>
    internal static readonly string[] CoreObjects =
    [
        "Sessions",

        "session_turn_quota_state",

        "installation_turn_quota_state",

        "assistant_finalization_capacity_reservations",

        "Sessions_turn_quota_state",

        "session_turn_quota_state_validate_insert",

        "session_turn_quota_state_validate_update",

        "session_turn_quota_state_guard_delete",

        "installation_turn_quota_state_validate_insert",

        "installation_turn_quota_state_validate_update",

        "assistant_finalization_capacity_reservations_validate_insert",

        "assistant_finalization_capacity_reservations_validate_update",

        "assistant_finalization_capacity_reservations_guard_delete",
    ];

    internal static async Task<CovenantCanonicalFixture> CreateAsync(CancellationToken cancellationToken)
    {

        CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            cancellationToken,
            coreObjects: CoreObjects);

        await ExecuteAsync(
            fixture,
            """
            INSERT OR IGNORE INTO installation_turn_quota_state
                (StateKey, ClaimCount, ReservedFinalizationCount, ConsumedFinalizationCount)
            VALUES (1, 0, 0, 0);
            """,
            cancellationToken);

        return fixture;

    }

    internal static async Task AddSessionAsync(
        CovenantCanonicalFixture fixture,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, 'active', $created, $created);
            """;

        // Bound as a Guid so the seeded row carries the same text EF writes, which is what the
        // capacity ledger's foreign keys actually resolve against.
        _ = command.Parameters.AddWithValue("$id", sessionId);

        _ = command.Parameters.AddWithValue("$created", Iso(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    internal static async Task AddTurnReceiptAsync(
        CovenantCanonicalFixture fixture,
        Guid sessionId,
        Guid assistantEntryId,
        DateTimeOffset createdAt,
        CovenantFinalOutcome outcome,
        CancellationToken cancellationToken,
        long confirmedTokens = 10,
        long proposedTokens = 3,
        long mutations = 1)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_turn_receipts (
                AssistantEntryId, SessionId, CampaignId, DatasetGeneration, PlanDigest,
                AttemptedAdmissionCount, AttemptChainHead, CommittedBranchDigest, LineageHeadDigest,
                ExternalDisclosureCount, DisclosureChainHead, ConfirmedTokenCost, ProposedTokenCost,
                MutationCount, FinalOutcomeCode, CreatedAtUtc)
            VALUES ($assistant, $session, NULL, $dataset, $plan, $admissions, $attempt, $branch, $lineage,
                    $disclosures, $chain, $confirmed, $proposed, $mutations, $outcome, $created);
            """;

        _ = command.Parameters.AddWithValue("$assistant", assistantEntryId.ToString("D"));

        _ = command.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        _ = command.Parameters.AddWithValue(
            "$dataset",
            (await fixture.ReadDatasetGenerationAsync(cancellationToken)).ToByteArray());

        _ = command.Parameters.AddWithValue("$plan", CovenantOperationGateFixture.Digest(1).Bytes);

        _ = command.Parameters.AddWithValue("$admissions", BitConverter.GetBytes(1UL));

        _ = command.Parameters.AddWithValue("$attempt", CovenantOperationGateFixture.Digest(2).Bytes);

        _ = command.Parameters.AddWithValue("$branch", CovenantOperationGateFixture.Digest(3).Bytes);

        _ = command.Parameters.AddWithValue("$lineage", CovenantOperationGateFixture.Digest(4).Bytes);

        _ = command.Parameters.AddWithValue("$disclosures", BitConverter.GetBytes(0UL));

        _ = command.Parameters.AddWithValue("$chain", CovenantOperationGateFixture.Digest(5).Bytes);

        _ = command.Parameters.AddWithValue("$confirmed", confirmedTokens);

        _ = command.Parameters.AddWithValue("$proposed", proposedTokens);

        _ = command.Parameters.AddWithValue("$mutations", mutations);

        _ = command.Parameters.AddWithValue("$outcome", (int)outcome);

        _ = command.Parameters.AddWithValue("$created", Iso(createdAt));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    /// <summary>
    /// Runs one unit of capacity work inside a caller-owned immediate transaction and commits it when
    /// the guard succeeds.
    /// </summary>
    internal static async Task<T> InTransactionAsync<T>(
        CovenantCanonicalFixture fixture,
        Func<CovenantMutationTransaction, Task<T>> work,
        CancellationToken cancellationToken,
        bool commit = true)
    {

        await using SqliteTransaction transaction = (SqliteTransaction)await fixture.Connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        T result = await work(new CovenantMutationTransaction(fixture.Connection, transaction));

        if (commit)
        {

            await transaction.CommitAsync(cancellationToken);

        }
        else
        {

            await transaction.RollbackAsync(cancellationToken);

        }

        return result;

    }

    internal static async Task<long> ScalarAsync(
        CovenantCanonicalFixture fixture,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async Task ExecuteAsync(
        CovenantCanonicalFixture fixture,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

}
