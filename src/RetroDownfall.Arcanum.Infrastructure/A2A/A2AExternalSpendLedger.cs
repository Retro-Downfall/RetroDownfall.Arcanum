using System.Data;
using System.Data.Common;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Reads today's delegated spend from the durable outbound Sending records (issue #69).
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

    /// <summary>
    /// Rows read per day. A ceiling on the scan, not on delegation: exceeding it is logged rather than
    /// silently truncating the figure an operator is about to make a spending decision on.
    /// </summary>
    private const int MaxRowsPerDay = 10_000;

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

                            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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
                            LIMIT @limit
                            """;

                        Add(cmd, "@kind", LongRunningOperationKinds.A2AOutboundSending);

                        Add(cmd, "@completed", (int)LongRunningOperationState.Completed);

                        Add(cmd, "@dayStart", dayStart.ToString("o", CultureInfo.InvariantCulture));

                        Add(cmd, "@dayEnd", dayStart.AddDays(1).ToString("o", CultureInfo.InvariantCulture));

                        Add(cmd, "@limit", MaxRowsPerDay);

                        decimal knownCost = 0m;

                        long knownTokens = 0;

                        int priced = 0;

                        int unpriced = 0;

                        int rows = 0;

                        await using DbDataReader reader = await cmd
                            .ExecuteReaderAsync(cancellationToken)
                            .ConfigureAwait(false);

                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {

                            rows++;

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

                                unpriced++;

                                continue;

                            }

                            priced++;

                            knownCost += record.CostUsd ?? 0m;

                            knownTokens += Math.Max(0, record.TotalTokens ?? 0);

                        }

                        if (rows >= MaxRowsPerDay)
                        {

                            logger.LogWarning(
                                "A2A: today's delegated-spend figure was computed from the first {Rows} settled "
                                + "Sendings; anything beyond that is not included in the reported total.",
                                MaxRowsPerDay);

                        }

                        return new ExternalSpendSummary(knownCost, knownTokens, priced, unpriced);

                    },
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            // Budget surfaces degrade to "no delegated spend known" rather than failing: a Grimoire read
            // that cannot answer must not take down the operator's view of local spend with it.
            logger.LogWarning(ex, "A2A: could not read today's delegated spend.");

            return ExternalSpendSummary.None;

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
