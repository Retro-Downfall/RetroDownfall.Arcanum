using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    /// <summary>
    /// How many of a generation's leaves one step re-proves in full. The windows tile the leaves in identity order and the sweep's
    /// revision picks which one a step takes, so whatever no trigger saw is met within ceil(leaves / this) revisions of the sweep.
    /// </summary>
    internal const int ScrubWindowLeaves = 32;

    /// <summary>
    /// The leaves the rotating window selects for the sweep as it stands now. The window follows the sweep's revision and the
    /// generation's own, because the sweep advances per committed unit and the generation per hierarchy step, and a window that
    /// followed only one would stand still through the other's phase. It reads only the leaf keys: an index walk, no leaf is read.
    /// </summary>
    internal static async Task<List<string>> ReadScrubWindowAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        long revision;

        await using (SqliteCommand sweep = Command(connection, transaction, "SELECT Revision FROM long_rest_sweeps WHERE OperationId = $operation;",
            ("$operation", generation.OperationId.ToString("N"))))
        {
            revision = await sweep.ExecuteScalarAsync(ct).ConfigureAwait(false) is long stored ? stored : 0;
        }

        long leaves;

        await using (SqliteCommand count = Command(connection, transaction, "SELECT count(*) FROM long_rest_discovery_leaves WHERE GenerationId = $generation;",
            ("$generation", Format(generation.GenerationId))))
        {
            leaves = (long)(await count.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        List<string> window = [];

        if (leaves == 0)
        {
            return window;
        }

        long windows = (leaves + ScrubWindowLeaves - 1) / ScrubWindowLeaves;

        long offset = checked((revision + generation.Revision) % windows * ScrubWindowLeaves);

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT MemoryId FROM long_rest_discovery_leaves WHERE GenerationId = $generation ORDER BY MemoryId LIMIT $limit OFFSET $offset;
            """, ("$generation", Format(generation.GenerationId)), ("$limit", ScrubWindowLeaves), ("$offset", offset));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            window.Add(reader.GetString(0));
        }

        return window;
    }

    /// <summary>
    /// The full recompute a sweep owes when it resumes. A clean generation is re-proved leaf by leaf in full. A protected one is re-proved by
    /// metadata, because resuming carries no capability to read its content with; the first units re-prove a window of it in full under
    /// the capability, and publish and completion re-prove all of it.
    /// </summary>
    internal static async Task<bool> VerifyResumeSeamAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        try
        {
            if (generation.Scope.ProtectedPartitionDigest is null)
            {
                await VerifyFrozenSourcesAsync(connection, transaction, generation, null, LongRestVerificationDepth.Seam, ct).ConfigureAwait(false);
            }
            else
            {
                await VerifyFrozenMetadataAsync(connection, transaction, generation, LongRestVerificationDepth.Seam, ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    // The leaf's consolidated and transformation-output flags are projections of rows the sweep itself writes when it applies a decision,
    // so no trigger on them can tell a foreign writer from the sweep. What distinguishes them is attribution: every receipt that sets a flag
    // on one of this generation's leaves must be the receipt of one of its own committed work items, unless the leaf was frozen with the flag
    // already set. The question is set-based and conservative (it asks for attribution, not for the full proof of the receipt that the
    // per-leaf read makes), so a step that skips the corpus pass still refuses a foreign consolidation of any leaf.
    internal const string ForeignProjectionSql = """
        SELECT NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            JOIN long_rest_suppressions suppression ON suppression.SourceVersionId = leaf.VersionId
            WHERE leaf.GenerationId = $generation AND COALESCE(json_extract(leaf.SnapshotJson, '$.isConsolidated'), 0) = 0
                AND suppression.ReceiptId NOT IN (SELECT ReceiptId FROM long_rest_work_items WHERE GenerationId = $generation AND ReceiptId IS NOT NULL))
          AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            JOIN long_rest_receipts receipt ON receipt.SurvivorVersionId = leaf.VersionId AND receipt.OutcomeCode = 1
            WHERE leaf.GenerationId = $generation AND COALESCE(json_extract(leaf.SnapshotJson, '$.isTransformationOutput'), 0) = 0
                AND receipt.ReceiptId NOT IN (SELECT ReceiptId FROM long_rest_work_items WHERE GenerationId = $generation AND ReceiptId IS NOT NULL));
        """;

    private static async Task<bool> OwnProjectionsOnlyAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        LongRestVerificationCounters.AntiJoinPass();

        await using SqliteCommand command = Command(connection, transaction, ForeignProjectionSql, ("$generation", Format(generation.GenerationId)));

        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 1;
    }

    /// <summary>Re-proves the rotating window in full: text, vector, labels and owner, as a step would before it acts.</summary>
    private static async Task ScrubWindowAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        LongRestVerificationCounters.ScrubWindow();

        foreach (string memory in await ReadScrubWindowAsync(connection, transaction, generation, ct).ConfigureAwait(false))
        {
            _ = await ReadFrozenLeafAsync(connection, transaction, generation, memory, capability, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The metadata-only window, for a caller that holds no capability and so cannot read protected content.</summary>
    private static async Task ScrubWindowMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        LongRestVerificationCounters.ScrubWindow();

        foreach (string memory in await ReadScrubWindowAsync(connection, transaction, generation, ct).ConfigureAwait(false))
        {
            _ = await ReadFrozenMetadataLeafAsync(connection, transaction, generation, memory, ct).ConfigureAwait(false);
        }
    }
}
