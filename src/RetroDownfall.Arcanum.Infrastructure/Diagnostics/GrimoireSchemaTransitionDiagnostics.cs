using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Diagnostics;

/// <summary>One bounded metadata read, shared by local doctor and the admitted HTTP status surface.</summary>
public static class GrimoireSchemaTransitionDiagnostics
{
    public static async Task<GrimoireSchemaTransitionStatusDto> ReadAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand exists = connection.CreateCommand();

        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'grimoire_schema_transitions');";

        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
        {
            return new GrimoireSchemaTransitionStatusDto(false, []);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT TransactionTierCode, FromVersion, CompletedThroughVersion, TargetVersion,
                BackfillName, BackfillRowsProcessed, LastDurableErrorCode, UpdatedAtUtc
            FROM grimoire_schema_transitions
            ORDER BY TransactionTierCode;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<GrimoireSchemaTransitionRunDto> runs = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            GrimoireSchemaTransactionTier tier = (GrimoireSchemaTransactionTier)reader.GetInt32(0);

            string tierName = tier switch
            {
                GrimoireSchemaTransactionTier.Core => "Core",
                GrimoireSchemaTransactionTier.CovenantCanonical => "CovenantCanonical",
                GrimoireSchemaTransactionTier.CovenantAccelerator => "CovenantAccelerator",
                _ => "UnrecognizedTier",
            };

            string sweep = "DDL";

            if (!reader.IsDBNull(4))
            {
                string stored = reader.GetString(4);

                sweep = tierName != "UnrecognizedTier"
                    && GrimoireSchemaVersionChains.Default.ForTier(tier).TryGetStep(reader.GetInt32(2), out GrimoireSchemaVersionStep step)
                    && step.Backfill is { Name: var name } && string.Equals(name, stored, StringComparison.Ordinal)
                        ? name : "unrecognized";
            }

            runs.Add(new GrimoireSchemaTransitionRunDto(
                tierName, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), sweep,
                reader.GetInt64(5),
                GrimoireSchemaTransitionFacts.SafeErrorCode(reader.IsDBNull(6) ? null : reader.GetString(6)),
                UtcInstantText.Parse(reader.GetString(7))));
        }

        return new GrimoireSchemaTransitionStatusDto(true, runs.ToArray());
    }
}

/// <summary>Inspects pending evolution without starting a host, bootstrap, write transaction or repair.</summary>
public sealed class GrimoireSchemaTransitionCheck(ISecretStore secretStore) : IDoctorCheck
{
    public const string CheckId = "grimoire.schema_transition";

    public string Id => CheckId;

    public string Name => "Grimoire schema transitions";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Grimoire;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {
        await using GrimoireProbe.OpenResult opened = await GrimoireProbe
            .OpenReadOnlyAsync(secretStore, cancellationToken).ConfigureAwait(false);

        if (opened.State != GrimoireProbe.OpenState.Opened)
        {
            return GrimoireProbe.DescribeUnopenable(opened);
        }

        GrimoireSchemaTransitionStatusDto status = await GrimoireSchemaTransitionDiagnostics
            .ReadAsync(opened.Connection!, cancellationToken).ConfigureAwait(false);

        if (!status.JournalAvailable)
        {
            return new DoctorFinding(DoctorOutcome.Unavailable,
                "This database has no schema transition journal; pending evolution was not measured. Start the host to classify its schema.");
        }

        return status.Runs.Length == 0
            ? new DoctorFinding(DoctorOutcome.Healthy, "No schema transitions are pending.")
            : new DoctorFinding(DoctorOutcome.Degraded,
                string.Join(" ", status.Runs.Select(GrimoireSchemaTransitionFacts.Describe))
                + " The host drains resumable sweeps in bounded passes. Re-run this check to compare committed progress; elapsed time alone does not prove a stall.",
                [new DoctorRemedy("grimoire.drain_schema_transitions", null, DoctorRemedyCommands.Serve,
                    "Keep this installation's compatible host running to finish resumable schema transitions; inspect the reported code if progress remains unchanged.")]);
    }
}
