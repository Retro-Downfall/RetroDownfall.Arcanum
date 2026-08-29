namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Which capability owns a declarative schema object. Family and
/// <see cref="GrimoireSchemaTransactionTier"/> are independent dimensions: family says whose feature
/// the object belongs to, tier says which install transaction it lives or dies with (DESIGN §5.4.5,
/// Covenant design "Three schema tiers").
/// </summary>
internal enum GrimoireSchemaFamily
{

    Core = 0,

    Covenant = 1,

}

/// <summary>
/// The install transaction an object belongs to, and therefore its failure domain:
///
/// <list type="bullet">
/// <item><see cref="Core"/> — the durable Grimoire schema. One transaction; failure aborts startup
/// because nothing else can be trusted without it.</item>
/// <item><see cref="CovenantCanonical"/> — Covenant's authoritative tables. Its own transaction
/// after Core succeeds; failure disables Covenant paths and leaves the rest of Arcanum
/// operational.</item>
/// <item><see cref="CovenantAccelerator"/> — the FTS5 inspection index. Its own transaction after
/// canonical succeeds; failure degrades inspection search only.</item>
/// </list>
///
/// The declaration order <b>is</b> the install order, and a later tier is attempted only when the
/// earlier one is healthy.
/// </summary>
internal enum GrimoireSchemaTransactionTier
{

    Core = 0,

    CovenantCanonical = 1,

    CovenantAccelerator = 2,

}

/// <summary>
/// Install order of an object <i>within</i> its transaction tier. SQLite resolves foreign keys at
/// DML time rather than at <c>CREATE TABLE</c> time, so tables inside a category install in plain
/// ordinal file-name order without a dependency sort.
///
/// <list type="bullet">
/// <item><see cref="Tables"/> — real tables plus their co-located indexes.</item>
/// <item><see cref="FullTextSearch"/> — FTS5 virtual tables, after the content tables they mirror
/// and before the triggers that write to them.</item>
/// <item><see cref="Triggers"/> — the only objects that reference both a table and an FTS5 virtual
/// table, so they come last.</item>
/// <item><see cref="Views"/> — reserved for views; none exist yet.</item>
/// </list>
///
/// The retired <c>Accelerators</c> category is gone: it existed for dynamically loaded <c>vec0</c>
/// virtual tables, and the hermetic SQLCipher runtime is compiled with
/// <c>SQLITE_OMIT_LOAD_EXTENSION</c>, so no such object can ever install. Optional acceleration is
/// now a transaction <i>tier</i>, not a category.
/// </summary>
internal enum GrimoireSchemaCategory
{

    Tables = 0,

    FullTextSearch = 1,

    Triggers = 2,

    Views = 3,

}

/// <summary>
/// The family, tier, category, and object name decoded from one embedded resource path.
/// </summary>
internal sealed record GrimoireSchemaResourcePath(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    GrimoireSchemaCategory Category,
    string Name);

/// <summary>
/// One declarative schema object, loaded from exactly one embedded <c>.sql</c> file.
///
/// <para><paramref name="ResourcePath"/> is the dotted path below <c>Data/Schema/</c> with the
/// <c>.sql</c> suffix removed. It is carried rather than recomputed because it is the stable
/// identity that both source fingerprints frame, and because a diagnostic naming the file a reader
/// can open is worth more than one naming a table.</para>
///
/// <para><paramref name="Sql"/> is the raw file text, which may still contain the
/// <see cref="GrimoireSchemaCatalog.EmbeddingDimensionsToken"/> placeholder;
/// <see cref="GrimoireSchemaCatalog.Resolve"/> produces the installable statement.</para>
/// </summary>
internal sealed record GrimoireSchemaObject(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    GrimoireSchemaCategory Category,
    string Name,
    string ResourcePath,
    string Sql);

/// <summary>
/// The family, tier, target version, ordinal, and step name decoded from one embedded transition
/// resource path.
/// </summary>
/// <remarks>
/// A transition resource has no <see cref="GrimoireSchemaCategory"/>: it is one statement in an
/// ordered version step, not an object anything converges. Sharing
/// <see cref="GrimoireSchemaResourcePath"/> would let a step be mistaken for an object in the one
/// place that mistake cannot be recovered from - the startup-blocking Core install transaction.
/// </remarks>
internal sealed record GrimoireSchemaTransitionResourcePath(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int ToVersion,
    int Ordinal,
    string Name);

/// <summary>
/// One transition statement, loaded from exactly one embedded <c>.sql</c> file under a
/// <c>Transitions/V&lt;n&gt;/</c> folder.
/// </summary>
/// <remarks>
/// Unlike a head object this need not be <c>CREATE ... IF NOT EXISTS</c>. A step's statements commit
/// in one transaction with the journal write that records the step, so a step either fully applies or
/// leaves nothing behind, and nothing re-runs a committed step. <c>ALTER TABLE ... ADD COLUMN</c>,
/// which has no idempotent form, is therefore legal here and is not legal in the head tree.
/// </remarks>
internal sealed record GrimoireSchemaTransitionStatementResource(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int ToVersion,
    int Ordinal,
    string Name,
    string ResourcePath,
    string Sql);
