namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Installation tier of a declarative Grimoire schema object file. The declaration order below
/// <b>is</b> the install order, and it is the only ordering the installer needs:
///
/// <list type="bullet">
/// <item><see cref="Tables"/> — real tables plus their co-located indexes. SQLite resolves foreign
/// keys at DML time rather than at <c>CREATE TABLE</c> time, so tables inside this tier install in
/// plain ordinal file-name order without a dependency sort.</item>
/// <item><see cref="FullTextSearch"/> — FTS5 virtual tables, after the content tables they mirror
/// and before the triggers that write to them.</item>
/// <item><see cref="Triggers"/> — triggers, which are the only objects that reference both a table
/// and an FTS5 virtual table, so they must come last among the always-installed objects.</item>
/// <item><see cref="Views"/> — reserved for views; none exist yet.</item>
/// <item><see cref="Accelerators"/> — optional <c>vec0</c> virtual tables, installed only when the
/// sqlite-vec extension loads (§21.2). Never part of the durable schema transaction.</item>
/// </list>
/// </summary>
internal enum GrimoireSchemaCategory
{

    Tables = 0,

    FullTextSearch = 1,

    Triggers = 2,

    Views = 3,

    Accelerators = 4,

}

/// <summary>
/// One declarative schema object, loaded from exactly one embedded <c>.sql</c> file under
/// <c>Data/Schema/&lt;category&gt;/&lt;name&gt;.sql</c>. <paramref name="Sql"/> is the raw file text,
/// which may still contain the <see cref="GrimoireSchemaCatalog.EmbeddingDimensionsToken"/>
/// placeholder; <see cref="GrimoireSchemaCatalog.Resolve"/> produces the installable statement.
/// </summary>
internal sealed record GrimoireSchemaObject(GrimoireSchemaCategory Category, string Name, string Sql);
