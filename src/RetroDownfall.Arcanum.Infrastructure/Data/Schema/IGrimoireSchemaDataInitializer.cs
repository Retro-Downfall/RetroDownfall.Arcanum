using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Code-owned, idempotent data initialization for exactly one transaction tier.
/// </summary>
/// <remarks>
/// Schema resource files are DDL-only, which leaves nowhere to express "and seed the singleton row".
/// An initializer fills that gap without reintroducing a migration chain: it runs inside its tier's
/// install transaction, after that tier's ordered DDL and before the installed catalog is
/// fingerprinted and committed, so DDL and seeded data commit or roll back together.
///
/// <para>Every implementation must be idempotent. The same install runs on a fresh database and on
/// one this build already wrote, and a repeat run has to converge rather than duplicate or fail.</para>
/// </remarks>
internal interface IGrimoireSchemaDataInitializer
{

    /// <summary>The single tier this initializer belongs to.</summary>
    GrimoireSchemaTransactionTier TransactionTier { get; }

    /// <summary>
    /// Seeds and converges tier-owned rows on the caller's live transaction. Throwing rolls that
    /// tier's DDL back with it.
    /// </summary>
    Task InitializeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken);

}
