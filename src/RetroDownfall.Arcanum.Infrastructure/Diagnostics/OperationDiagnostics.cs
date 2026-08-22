using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Diagnostics;

/// <summary>
/// Reads durable-operation state straight out of the encrypted Grimoire, read-only.
///
/// <para>It deliberately does not use <c>ILongRunningOperationStore</c>. That store rides the EF
/// <c>ArcanumDbContext</c>, whose connection interceptor demands a passphrase that only
/// <c>IGrimoireCliInitialization.RunExclusiveWithBootstrapAsync{T}</c> installs — and that exclusive
/// boundary resolves the master key, installs schema, and can perform a KDF upgrade. A read-only
/// diagnostic must not do any of that, so it opens its own read-only connection through
/// <see cref="GrimoireProbe"/> and counts rows.</para>
/// </summary>
internal static class DurableOperationCounts
{

    internal const string Table = "LongRunningOperations";

    internal readonly record struct Snapshot(
        long ReconciliationRequired,
        long Failed,
        long Abandoned,
        long Active,
        long ExpiredLeases,
        IReadOnlyList<string> ExpiredKinds);

    internal static async Task<Snapshot> ReadAsync(
        SqliteConnection connection,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {

        long reconciliation = 0;

        long failed = 0;

        long abandoned = 0;

        long active = 0;

        await using (SqliteCommand states = connection.CreateCommand())
        {

            states.CommandText = $"SELECT \"State\", count(*) FROM \"{Table}\" GROUP BY \"State\";";

            await using SqliteDataReader reader =
                await states.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                LongRunningOperationState state = (LongRunningOperationState)reader.GetInt32(0);

                long count = reader.GetInt64(1);

                switch (state)
                {

                    case LongRunningOperationState.ReconciliationRequired:
                        reconciliation += count;
                        break;

                    case LongRunningOperationState.Failed:
                        failed += count;
                        break;

                    case LongRunningOperationState.Abandoned:
                        abandoned += count;
                        break;

                    case LongRunningOperationState.Pending:
                    case LongRunningOperationState.Running:
                    case LongRunningOperationState.Waiting:
                        active += count;
                        break;

                    default:
                        break;

                }

            }

        }

        long expired = 0;

        List<string> kinds = [];

        await using (SqliteCommand leases = connection.CreateCommand())
        {

            // Lease expiry is stored as a round-trip timestamp string, so it is compared as one.
            leases.CommandText =
                $"SELECT \"Kind\", count(*) FROM \"{Table}\" "
                + "WHERE \"LeaseExpiresAt\" IS NOT NULL AND \"LeaseExpiresAt\" < $now "
                + "AND \"State\" IN ($pending, $running, $waiting, $cancelling) GROUP BY \"Kind\";";

            _ = leases.Parameters.AddWithValue("$now", utcNow.UtcDateTime.ToString("O"));

            _ = leases.Parameters.AddWithValue("$pending", (int)LongRunningOperationState.Pending);

            _ = leases.Parameters.AddWithValue("$running", (int)LongRunningOperationState.Running);

            _ = leases.Parameters.AddWithValue("$waiting", (int)LongRunningOperationState.Waiting);

            _ = leases.Parameters.AddWithValue("$cancelling", (int)LongRunningOperationState.Cancelling);

            await using SqliteDataReader reader =
                await leases.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                kinds.Add(reader.GetString(0));

                expired += reader.GetInt64(1);

            }

        }

        return new Snapshot(reconciliation, failed, abandoned, active, expired, kinds);

    }

}

/// <summary>
/// Durable operations that a crash left needing a human, read locally rather than from the host.
///
/// <para>That distinction is the point. The pre-#33 <c>DurableOperations</c> check asked the running
/// host, so it degraded to "host unreachable" in exactly the situation an operator cares about —
/// after a crash, before the host comes back. Reading the database answers the question that
/// actually matters then: is there work stuck in
/// <see cref="LongRunningOperationState.ReconciliationRequired"/> waiting for me?</para>
/// </summary>
public sealed class DurableOperationRepairCheck(ISecretStore secretStore) : IDoctorCheck
{

    public const string CheckId = "operations.awaiting_repair";

    public string Id => CheckId;

    public string Name => "Operations awaiting repair";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Operations;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        await using GrimoireProbe.OpenResult opened =
            await GrimoireProbe.OpenReadOnlyAsync(secretStore, cancellationToken).ConfigureAwait(false);

        if (opened.State != GrimoireProbe.OpenState.Opened)
        {

            return GrimoireProbe.DescribeUnreadable(
                opened,
                "so no durable operation can be recorded");

        }

        DurableOperationCounts.Snapshot snapshot = await DurableOperationCounts
            .ReadAsync(opened.Connection!, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.ReconciliationRequired == 0 && snapshot.Failed == 0 && snapshot.Abandoned == 0)
        {

            return new DoctorFinding(
                DoctorOutcome.Healthy,
                snapshot.Active == 0
                    ? "No durable operation needs attention."
                    : $"{snapshot.Active} durable operation(s) are in flight and none needs attention.");

        }

        return new DoctorFinding(
            DoctorOutcome.Degraded,
            $"{snapshot.ReconciliationRequired} operation(s) require reconciliation, {snapshot.Failed} failed, and "
            + $"{snapshot.Abandoned} were abandoned. Reconciliation runs on the host, not from this command.",
            [
                new DoctorRemedy(
                    "operations.reconcile",
                    null,
                    DoctorRemedyCommands.OperationListNeedingRepair,
                    "List what needs a human, then run 'arcanum operation reconcile' with the host running."),
            ]);

    }

}

/// <summary>
/// Operations whose lease expired while still claiming to run — the signature of a host killed
/// mid-work. Reported separately from <see cref="DurableOperationRepairCheck"/> because the remedy
/// differs: an expired lease is reclaimed automatically by the next host start, whereas a
/// reconciliation-required operation is not.
/// </summary>
public sealed class StaleOperationLeaseCheck(ISecretStore secretStore) : IDoctorCheck
{

    public const string CheckId = "operations.stale_leases";

    public string Id => CheckId;

    public string Name => "Operation leases";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Operations;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        await using GrimoireProbe.OpenResult opened =
            await GrimoireProbe.OpenReadOnlyAsync(secretStore, cancellationToken).ConfigureAwait(false);

        if (opened.State != GrimoireProbe.OpenState.Opened)
        {

            return GrimoireProbe.DescribeUnreadable(opened, "so no operation lease can be recorded");

        }

        DurableOperationCounts.Snapshot snapshot = await DurableOperationCounts
            .ReadAsync(opened.Connection!, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.ExpiredLeases == 0)
        {

            return new DoctorFinding(DoctorOutcome.Healthy, "No operation lease has expired.");

        }

        // Kinds only — an operation id identifies a specific piece of user work and has no place in
        // a diagnostic an operator may paste into an issue.
        return new DoctorFinding(
            DoctorOutcome.Degraded,
            $"{snapshot.ExpiredLeases} operation lease(s) have expired "
            + $"({string.Join(", ", snapshot.ExpiredKinds.Order(StringComparer.Ordinal))}). "
            + "The next host start reclaims them.",
            [
                new DoctorRemedy(
                    "operations.reclaim_leases",
                    null,
                    DoctorRemedyCommands.OperationList,
                    "Start the host to reclaim expired leases, then review anything still stuck."),
            ]);

    }

}
