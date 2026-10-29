namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// A declared census whose pre-final batches only read source rows and journal progress. A fresh
/// quiescent execution validates old evidence before recomputing it; the final correction stays
/// atomic with schema publication. Other backfills retain their ordinary persisted progress.
/// </summary>
internal interface IGrimoireRestartableReadOnlyBackfill : IGrimoireQuiescentBackfill
{
    void ValidateRestartCursor(string? cursor, long rowsProcessed);
}
