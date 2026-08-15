namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The kinds of object a tier manifest can own. Closed on purpose: an unrecognized
/// <c>sqlite_master</c> type is drift, not a new category to accommodate.
/// </summary>
internal enum GrimoireSchemaObjectType
{

    Table = 0,

    VirtualTable = 1,

    Trigger = 2,

    View = 3,

}

/// <summary>
/// One object a tier expects to find installed, with its normalized definition and every index it
/// owns.
/// </summary>
/// <remarks>
/// <paramref name="IsSynthetic"/> marks an object that has no source resource file because SQLite
/// generates it - the four <c>covenant_fts_*</c> shadow tables behind an FTS5 virtual table. They
/// are still owned and still validated: a missing or altered shadow means the index is not the one
/// this build believes it created, which is exactly the condition that silently returns wrong search
/// results. Their expected DDL is pinned to the accepted SQLite runtime, so a runtime change has to
/// be reviewed rather than absorbed.
/// </remarks>
internal sealed record GrimoireSchemaManifestEntry(
    GrimoireSchemaObjectType Type,
    string Name,
    string TableName,
    string NormalizedSql,
    bool IsSynthetic,
    IReadOnlyList<GrimoireExpectedIndex> Indexes);
