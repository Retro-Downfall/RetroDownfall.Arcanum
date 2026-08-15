namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// One expected index on a manifest-owned table, in the shape <c>PRAGMA index_list</c> and
/// <c>PRAGMA index_xinfo</c> report it.
/// </summary>
/// <remarks>
/// Indexes are validated by shape rather than by name for one reason: SQLite's implicit indexes have
/// generated names (<c>sqlite_autoindex_&lt;table&gt;_&lt;n&gt;</c>) whose numbering depends on
/// constraint declaration order and whose <c>sql</c> is null. Fingerprinting those names would report
/// drift for a harmless reordering and would fail to notice a genuinely changed key.
///
/// <para><see cref="Origin"/> uses SQLite's closed vocabulary: <c>pk</c> for a primary key,
/// <c>u</c> for a unique constraint, <c>c</c> for an explicitly created index. Explicit indexes match
/// by their declared name; implicit ones match by origin plus exact column shape.</para>
/// </remarks>
internal sealed record GrimoireExpectedIndex(
    string Name,
    bool IsUnique,
    string Origin,
    bool IsPartial,
    IReadOnlyList<GrimoireExpectedIndexColumn> Columns);

/// <summary>
/// One column position within an index, as <c>PRAGMA index_xinfo</c> reports it.
/// </summary>
/// <remarks>
/// <paramref name="IsKey"/> separates key columns from the auxiliary columns SQLite appends to carry
/// the rowid or the table's primary key. Comparing only key columns would let a changed
/// <c>WITHOUT ROWID</c> primary key pass unnoticed, so both are captured.
/// <paramref name="Name"/> is null for an expression or rowid position, and
/// <paramref name="ColumnId"/> carries SQLite's own marker for which: <c>-2</c> for an expression,
/// <c>-1</c> for the rowid. An expression position is therefore expected by its place in the shape
/// rather than by its text; the expression itself lives in the index's stored DDL.
/// </remarks>
internal sealed record GrimoireExpectedIndexColumn(
    int Sequence,
    int ColumnId,
    string? Name,
    bool Descending,
    string Collation,
    bool IsKey);
