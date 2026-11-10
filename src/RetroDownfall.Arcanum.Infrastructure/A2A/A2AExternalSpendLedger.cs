using System.Data;
using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Reads delegated spend from legacy Sending records and the authoritative physical Guild remote sources.
/// </summary>
/// <remarks>
/// <para>
/// The record written when a Sending settles is the single place a delegated cost is stated, so summing
/// it counts each Sending exactly once — no double count between an owning turn and the work it
/// delegated, and no way for a restart to lose or repeat a figure.
/// </para>
/// <para>
/// A Sending whose peer reported nothing is <em>counted</em>, never <em>costed</em>. Folding it in at
/// zero would make the day's total look complete while part of it has no price at all, which is the
/// understatement issue #60 removed from the reporting surfaces and #69 must not reintroduce here.
/// </para>
/// </remarks>
internal sealed class A2AExternalSpendLedger(
    ArcanumDbContext db,
    TimeProvider timeProvider,
    ILogger<A2AExternalSpendLedger> logger) : IExternalSpendLedger
{
    public async Task<ExternalSpendSummary> GetTodayAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        DateTimeOffset dayStart = new(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(
                    async () =>
                    {
                        DbConnection connection = db.Database.GetDbConnection();

                        if (connection.State != ConnectionState.Open)
                        {
                            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                        }

                        await using DbCommand cmd = connection.CreateCommand();

                        cmd.CommandText =
                            """
                            SELECT "CheckpointPayload", "CheckpointVersion"
                            FROM "LongRunningOperations"
                            WHERE "Kind" = @kind
                              AND "State" = @completed
                              AND "CompletedAt" >= @dayStart
                              AND "CompletedAt" < @dayEnd
                            """;

                        Add(cmd, "@kind", LongRunningOperationKinds.A2AOutboundSending);

                        Add(cmd, "@completed", (int)LongRunningOperationState.Completed);

                        Add(cmd, "@dayStart", UtcInstantText.Format(dayStart));

                        Add(cmd, "@dayEnd", UtcInstantText.Format(dayStart.AddDays(1)));

                        decimal knownCost = 0m;

                        long knownTokens = 0;

                        int priced = 0;

                        int unpriced = 0;

                        await using DbDataReader reader = await cmd
                            .ExecuteReaderAsync(cancellationToken)
                            .ConfigureAwait(false);

                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            if (reader.IsDBNull(0)
                                || reader.GetInt32(1) != A2ASendingLedger.CheckpointVersion)
                            {
                                continue;
                            }

                            if (A2ASendingLedger.TryReadPayload((byte[])reader.GetValue(0)) is not { } record
                                || record.Direction != A2ASendingRecordDirection.Outbound)
                            {
                                continue;
                            }

                            if (!record.CostKnown)
                            {
                                unpriced = checked(unpriced + 1);

                                continue;
                            }

                            priced = checked(priced + 1);

                            knownCost = ExactUsdText.CheckedAdd(knownCost, record.CostUsd ?? 0m);

                            knownTokens = checked(knownTokens + Math.Max(0, record.TotalTokens ?? 0));
                        }

                        await reader.DisposeAsync().ConfigureAwait(false);

                        // One Guild task owns one cumulative external source across its continuations.
                        // The legacy adapter never writes this source, so combining the lanes cannot duplicate a bill.
                        await using DbCommand present = connection.CreateCommand();

                        present.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='guild_remote_effects')";

                        if (Equals(1L, await present.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
                        {
                            await using DbCommand remote = connection.CreateCommand();

                            remote.CommandText = """
                                SELECT e.KnownUsd,e.KnownTokens,e.UnknownUsd,e.UnknownTokens,
                                    CASE WHEN typeof(e.KnownUsd)='text' AND typeof(e.KnownTokens)='integer'
                                        AND typeof(e.UnknownUsd)='integer' AND e.UnknownUsd IN(0,1)
                                        AND typeof(e.UnknownTokens)='integer' AND e.UnknownTokens IN(0,1) THEN 1 ELSE 0 END
                                FROM guild_remote_effects r JOIN guild_budget_effects e ON e.EffectId=r.EffectId AND e.QuestId=r.QuestId
                                JOIN guild_dispatches d ON d.DispatchId=r.DispatchId AND d.QuestId=r.QuestId
                                WHERE r.SettledAtUtc>=@dayStart AND r.SettledAtUtc<@dayEnd AND e.KindCode=1 AND e.StateCode=2
                                    AND EXISTS(SELECT 1 FROM guild_budget_evidence b WHERE b.EffectId=e.EffectId AND b.AppliedRevision=e.Revision)
                                ORDER BY r.EffectId
                                """;

                            Add(remote, "@dayStart", UtcInstantText.Format(dayStart));
                            Add(remote, "@dayEnd", UtcInstantText.Format(dayStart.AddDays(1)));

                            await using DbDataReader physical = await remote.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                            while (await physical.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                if (physical.GetInt32(4) != 1)
                                {
                                    unpriced = checked(unpriced + 1);

                                    continue;
                                }

                                decimal usd;

                                try { usd = ExactUsdText.Read(physical, 0); }
                                catch (Exception exception) when (exception is FormatException or InvalidDataException or OverflowException)
                                {
                                    unpriced = checked(unpriced + 1);

                                    continue;
                                }

                                long tokens = physical.GetInt64(1);

                                if (usd < 0m || tokens < 0)
                                {
                                    unpriced = checked(unpriced + 1);

                                    continue;
                                }

                                knownCost = ExactUsdText.CheckedAdd(knownCost, usd);
                                knownTokens = checked(knownTokens + tokens);

                                if (physical.GetBoolean(2) || physical.GetBoolean(3)) unpriced = checked(unpriced + 1);
                                else priced = checked(priced + 1);
                            }
                        }

                        return new ExternalSpendSummary(knownCost, knownTokens, priced, unpriced);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable or unrepresentable authoritative total cannot become an empty ledger.
            // Keep the failure visible to the budget gate instead of admitting against a false zero.
            logger.LogWarning(ex, "A2A: could not read today's delegated spend.");

            throw;
        }
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);
    }
}
