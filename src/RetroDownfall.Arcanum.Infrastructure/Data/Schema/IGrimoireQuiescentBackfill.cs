using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>A Core sweep whose accumulated pages require exclusion of ordinary source writers.</summary>
internal interface IGrimoireQuiescentBackfill : IGrimoireSchemaBackfill
{
    Task<GrimoireSchemaBackfillBatch> AdvanceQuiescentBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        GrimoireSchemaQuiescentExecution execution,
        CancellationToken cancellationToken);
}
