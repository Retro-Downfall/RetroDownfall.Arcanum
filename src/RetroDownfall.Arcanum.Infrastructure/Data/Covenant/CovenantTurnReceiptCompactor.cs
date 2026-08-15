using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// One fold of a Session's oldest turn receipts into its guarded aggregate row.
/// </summary>
/// <remarks>
/// Bounded on purpose: at most <see cref="MaxReceiptsPerFold"/> receipts move per call, from the
/// oldest end. The write path must never perform an unbounded fold, because a Session that has
/// accumulated its full tail would otherwise turn one ordinary turn commit into a thousand-row
/// rewrite.
///
/// <para>The aggregate's chain digest is order-sensitive, so it is evidence that this exact sequence
/// of receipts folded rather than that some set of them did. Folded detail is deliberately
/// unnecessary for terminal replay: replay resolves from the mutation receipt ledger, not from turn
/// evidence.</para>
/// </remarks>
internal sealed class CovenantTurnReceiptCompactor(ICovenantSqliteConnectionInitializer initializer)
{

    internal const int MaxReceiptsPerFold = 128;

    internal CovenantTurnReceiptCompactor()
        : this(CovenantSqliteConnectionInitializer.Instance)
    {
    }

    /// <summary>
    /// Folds the oldest receipts of one Session, leaving the bounded live tail in place.
    /// </summary>
    public async ValueTask<Result<int>> FoldAsync(
        Guid sessionId,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(transaction);

        using CovenantSqliteAuthorizationScope authorization = initializer.Authorize(
            transaction.Connection,
            CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

        long total = await CountAsync(transaction, sessionId, cancellationToken).ConfigureAwait(false);

        long over = total - CovenantLimits.MaxTurnReceiptsPerSession;

        if (over <= 0)
        {

            return 0;

        }

        int take = (int)Math.Min(over, MaxReceiptsPerFold);

        ImmutableArray<FoldRow> rows = await ReadOldestAsync(transaction, sessionId, take, cancellationToken)
            .ConfigureAwait(false);

        if (rows.IsEmpty)
        {

            return 0;

        }

        AggregateRow current = await ReadAggregateAsync(transaction, sessionId, cancellationToken)
            .ConfigureAwait(false);

        AggregateRow folded = current;

        foreach (FoldRow row in rows)
        {

            folded = Fold(folded, row);

        }

        await WriteAggregateAsync(transaction, sessionId, current.Exists, folded, cancellationToken)
            .ConfigureAwait(false);

        await DeleteAsync(transaction, rows, cancellationToken).ConfigureAwait(false);

        return rows.Length;

    }

    private static AggregateRow Fold(AggregateRow aggregate, FoldRow row)
    {

        return aggregate with
        {

            CoveredCount = checked(aggregate.CoveredCount + 1),

            EarliestCoveredAtUtc = aggregate.EarliestCoveredAtUtc ?? row.CreatedAtUtc,

            LatestCoveredAtUtc = row.CreatedAtUtc,

            ConfirmedTokenTotal = checked(aggregate.ConfirmedTokenTotal + row.ConfirmedTokenCost),

            ProposedTokenTotal = checked(aggregate.ProposedTokenTotal + row.ProposedTokenCost),

            MutationTotal = checked(aggregate.MutationTotal + row.MutationCount),

            CompletedOutcomeCount = aggregate.CompletedOutcomeCount
                + (row.FinalOutcome == CovenantFinalOutcome.Completed ? 1 : 0),

            FailedOutcomeCount = aggregate.FailedOutcomeCount
                + (row.FinalOutcome == CovenantFinalOutcome.Failed ? 1 : 0),

            CancelledOutcomeCount = aggregate.CancelledOutcomeCount
                + (row.FinalOutcome == CovenantFinalOutcome.Cancelled ? 1 : 0),

            InterruptedOutcomeCount = aggregate.InterruptedOutcomeCount
                + (row.FinalOutcome == CovenantFinalOutcome.Interrupted ? 1 : 0),

            // Chained rather than summed: the digest attests to an order, and a sum would fold two
            // different sequences of the same receipts to the same value.
            ChainDigest = Chain(aggregate.ChainDigest, row),

        };

    }

    /// <summary>
    /// Folds one receipt into the running chain head.
    /// </summary>
    /// <remarks>
    /// A local construction rather than one of the pinned policy-v1 digests, because this preimage
    /// is exactly the stored aggregate columns and nothing outside this table consumes it. Borrowing
    /// a pinned digest input here would mean inventing values for fields the receipt row does not
    /// store, and a digest over invented values attests to nothing.
    /// </remarks>
    private static CovenantDigest Chain(CovenantDigest head, FoldRow row)
    {

        using System.Security.Cryptography.IncrementalHash hash =
            System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);

        hash.AppendData("Arcanum.Covenant.TurnReceiptFold.v1"u8);

        hash.AppendData(head.Bytes);

        hash.AppendData(row.AssistantEntryId.ToByteArray());

        hash.AppendData(row.PlanDigest.Bytes);

        hash.AppendData(BitConverter.GetBytes(row.AttemptedAdmissionCount));

        hash.AppendData(row.AttemptChainHead.Bytes);

        hash.AppendData(row.CommittedBranchDigest.Bytes);

        hash.AppendData(row.LineageHeadDigest.Bytes);

        hash.AppendData(BitConverter.GetBytes(row.ExternalDisclosureCount));

        hash.AppendData(row.DisclosureChainHead.Bytes);

        hash.AppendData(BitConverter.GetBytes(row.ConfirmedTokenCost));

        hash.AppendData(BitConverter.GetBytes(row.ProposedTokenCost));

        hash.AppendData(BitConverter.GetBytes(row.MutationCount));

        hash.AppendData([(byte)row.FinalOutcome]);

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private static async ValueTask<long> CountAsync(
        CovenantMutationTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM covenant_turn_receipts WHERE SessionId = $session;";

        _ = command.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async ValueTask<ImmutableArray<FoldRow>> ReadOldestAsync(
        CovenantMutationTransaction transaction,
        Guid sessionId,
        int take,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT AssistantEntryId, PlanDigest, AttemptedAdmissionCount, AttemptChainHead,
                   CommittedBranchDigest, LineageHeadDigest, ExternalDisclosureCount, DisclosureChainHead,
                   ConfirmedTokenCost, ProposedTokenCost, MutationCount, FinalOutcomeCode, CreatedAtUtc
            FROM covenant_turn_receipts
            WHERE SessionId = $session
            ORDER BY CreatedAtUtc, AssistantEntryId
            LIMIT $take;
            """;

        _ = command.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$take", take);

        List<FoldRow> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rows.Add(
                new FoldRow(
                    Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    new CovenantDigest((byte[])reader.GetValue(1)),
                    BitConverter.ToUInt64((byte[])reader.GetValue(2)),
                    new CovenantDigest((byte[])reader.GetValue(3)),
                    new CovenantDigest((byte[])reader.GetValue(4)),
                    new CovenantDigest((byte[])reader.GetValue(5)),
                    BitConverter.ToUInt64((byte[])reader.GetValue(6)),
                    new CovenantDigest((byte[])reader.GetValue(7)),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    (CovenantFinalOutcome)reader.GetInt32(11),
                    reader.GetString(12)));

        }

        return [.. rows];

    }

    private static async ValueTask<AggregateRow> ReadAggregateAsync(
        CovenantMutationTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT CoveredCount, EarliestCoveredAtUtc, LatestCoveredAtUtc, ConfirmedTokenTotal,
                   ProposedTokenTotal, CompletedOutcomeCount, FailedOutcomeCount, CancelledOutcomeCount,
                   InterruptedOutcomeCount, MutationTotal, ChainDigest
            FROM covenant_turn_receipt_aggregate
            WHERE SessionId = $session;
            """;

        _ = command.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return new AggregateRow(
                Exists: false,
                CoveredCount: 0,
                EarliestCoveredAtUtc: null,
                LatestCoveredAtUtc: null,
                ConfirmedTokenTotal: 0,
                ProposedTokenTotal: 0,
                CompletedOutcomeCount: 0,
                FailedOutcomeCount: 0,
                CancelledOutcomeCount: 0,
                InterruptedOutcomeCount: 0,
                MutationTotal: 0,
                ChainDigest: new CovenantDigest(new byte[CovenantLimits.DigestBytes]));

        }

        return new AggregateRow(
            Exists: true,
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            new CovenantDigest((byte[])reader.GetValue(10)));

    }

    private static async ValueTask WriteAggregateAsync(
        CovenantMutationTransaction transaction,
        Guid sessionId,
        bool exists,
        AggregateRow aggregate,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = exists
            ? """
              UPDATE covenant_turn_receipt_aggregate
              SET CoveredCount = $covered,
                  EarliestCoveredAtUtc = $earliest,
                  LatestCoveredAtUtc = $latest,
                  ConfirmedTokenTotal = $confirmed,
                  ProposedTokenTotal = $proposed,
                  CompletedOutcomeCount = $completed,
                  FailedOutcomeCount = $failed,
                  CancelledOutcomeCount = $cancelled,
                  InterruptedOutcomeCount = $interrupted,
                  MutationTotal = $mutations,
                  ChainDigest = $chain,
                  UpdatedAtUtc = $updated
              WHERE SessionId = $session;
              """
            : """
              INSERT INTO covenant_turn_receipt_aggregate (
                  SessionId, CoveredCount, EarliestCoveredAtUtc, LatestCoveredAtUtc, ConfirmedTokenTotal,
                  ProposedTokenTotal, CompletedOutcomeCount, FailedOutcomeCount, CancelledOutcomeCount,
                  InterruptedOutcomeCount, MutationTotal, ChainDigest, UpdatedAtUtc)
              VALUES ($session, $covered, $earliest, $latest, $confirmed, $proposed, $completed, $failed,
                      $cancelled, $interrupted, $mutations, $chain, $updated);
              """;

        _ = command.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$covered", aggregate.CoveredCount);

        _ = command.Parameters.AddWithValue("$earliest", (object?)aggregate.EarliestCoveredAtUtc ?? DBNull.Value);

        _ = command.Parameters.AddWithValue("$latest", (object?)aggregate.LatestCoveredAtUtc ?? DBNull.Value);

        _ = command.Parameters.AddWithValue("$confirmed", aggregate.ConfirmedTokenTotal);

        _ = command.Parameters.AddWithValue("$proposed", aggregate.ProposedTokenTotal);

        _ = command.Parameters.AddWithValue("$completed", aggregate.CompletedOutcomeCount);

        _ = command.Parameters.AddWithValue("$failed", aggregate.FailedOutcomeCount);

        _ = command.Parameters.AddWithValue("$cancelled", aggregate.CancelledOutcomeCount);

        _ = command.Parameters.AddWithValue("$interrupted", aggregate.InterruptedOutcomeCount);

        _ = command.Parameters.AddWithValue("$mutations", aggregate.MutationTotal);

        _ = command.Parameters.AddWithValue("$chain", aggregate.ChainDigest.Bytes);

        _ = command.Parameters.AddWithValue(
            "$updated",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async ValueTask DeleteAsync(
        CovenantMutationTransaction transaction,
        ImmutableArray<FoldRow> rows,
        CancellationToken cancellationToken)
    {

        foreach (FoldRow row in rows)
        {

            await using SqliteCommand command = transaction.CreateCommand();

            command.CommandText = "DELETE FROM covenant_turn_receipts WHERE AssistantEntryId = $assistant;";

            _ = command.Parameters.AddWithValue("$assistant", row.AssistantEntryId.ToString("D"));

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

    }

    private sealed record FoldRow(
        Guid AssistantEntryId,
        CovenantDigest PlanDigest,
        ulong AttemptedAdmissionCount,
        CovenantDigest AttemptChainHead,
        CovenantDigest CommittedBranchDigest,
        CovenantDigest LineageHeadDigest,
        ulong ExternalDisclosureCount,
        CovenantDigest DisclosureChainHead,
        long ConfirmedTokenCost,
        long ProposedTokenCost,
        long MutationCount,
        CovenantFinalOutcome FinalOutcome,
        string CreatedAtUtc);

    private sealed record AggregateRow(
        bool Exists,
        long CoveredCount,
        string? EarliestCoveredAtUtc,
        string? LatestCoveredAtUtc,
        long ConfirmedTokenTotal,
        long ProposedTokenTotal,
        long CompletedOutcomeCount,
        long FailedOutcomeCount,
        long CancelledOutcomeCount,
        long InterruptedOutcomeCount,
        long MutationTotal,
        CovenantDigest ChainDigest);

}
