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
    /// The one statement that lists the leaf keys a verification reads, for a corpus pass (a page after a cursor) and for a step's window
    /// (one window, picked by the revision), so the two cannot disagree about which leaves there are. It reads only keys: an index walk, no
    /// leaf is read. The window is the revision's turn among ceil(leaves / limit) windows that tile the leaves in identity order, and the
    /// revision is the sweep's plus the generation's own, because the sweep advances per committed unit and the generation per hierarchy
    /// step, and a window that followed only one would stand still through the other's phase.
    /// </summary>
    internal const string LeafKeysSql = """
        SELECT MemoryId FROM long_rest_discovery_leaves WHERE GenerationId = $generation AND ($cursor IS NULL OR MemoryId > $cursor)
        ORDER BY MemoryId LIMIT $limit
        OFFSET CASE WHEN $window = 1 THEN (COALESCE((SELECT Revision FROM long_rest_sweeps WHERE OperationId = $operation), 0) + $revision)
            % MAX(1, ((SELECT count(*) FROM long_rest_discovery_leaves WHERE GenerationId = $generation) + $limit - 1) / $limit) * $limit ELSE 0 END;
        """;

    /// <summary>The page size of a corpus pass.</summary>
    private const int CorpusPageLeaves = 128;

    /// <summary>The leaves the rotating window selects for the sweep as it stands now; what a test compares a step's reads with.</summary>
    internal static async Task<List<string>> ReadScrubWindowAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        List<string> window = [];

        await using SqliteCommand command = LeafKeys(connection, transaction, generation, null, true);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            window.Add(reader.GetString(0));
        }

        return window;
    }

    private static SqliteCommand LeafKeys(SqliteConnection connection, SqliteTransaction transaction, LongRestDiscoveryGeneration generation,
        string? cursor, bool window) => Command(connection, transaction, LeafKeysSql, ("$generation", Format(generation.GenerationId)),
        ("$operation", generation.OperationId.ToString("N")), ("$revision", generation.Revision), ("$cursor", cursor),
        ("$limit", window ? ScrubWindowLeaves : CorpusPageLeaves), ("$window", window ? 1 : 0));

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

    /// <summary>
    /// Re-proves these leaves in full: text, vector, labels and owner. The one place a corpus pass, a step's window and the inputs a step is
    /// about to act on all read leaves from, so there is one body to keep right.
    /// </summary>
    internal static async Task ReadLeavesAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestDiscoveryGeneration generation,
        IEnumerable<string> memories, LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        foreach (string memory in memories)
        {
            _ = await ReadFrozenLeafAsync(connection, transaction, generation, memory, capability, ct).ConfigureAwait(false);
        }
    }
}
