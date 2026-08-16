using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Applies SQLCipher and SQLite policy to every connection Arcanum opens, and issues the scoped
/// authorizations privileged writes require.
/// </summary>
/// <remarks>
/// One implementation, one place. Before this existed, pragma application was duplicated across the
/// bootstrapper, the EF interceptor, backup, restore, reset, and diagnostics, which meant a policy
/// change had six places to miss and no single point could verify the policy actually took effect.
/// </remarks>
internal interface ICovenantSqliteConnectionInitializer
{

    /// <summary>
    /// Initializes an already-open connection: selects the native runtime, registers the
    /// authorization functions in their denied state, applies the policy for
    /// <paramref name="mode" />, and reads it back.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The connection is not open, or the applied policy did not read back as set.
    /// </exception>
    ValueTask InitializeAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Grants one privileged operation on <paramref name="connection" /> until the returned scope is
    /// disposed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind" /> is
    /// <see cref="CovenantSqliteAuthorizationKind.RestoreStagingManagedAuthoritySanitization" />,
    /// which is reachable only through the sealed restore-staging capability.
    /// </exception>
    CovenantSqliteAuthorizationScope Authorize(
        SqliteConnection connection,
        CovenantSqliteAuthorizationKind kind);

    /// <summary>
    /// Grants restore-staging authority sanitization to the one sealed capability entitled to it.
    /// </summary>
    /// <remarks>
    /// No raw connection parameter and no general authorization kind. The capability privately holds
    /// the unpublished candidate connection and hands it nothing back; this method exists so the
    /// grant has exactly one call site that can be pinned, rather than being reachable from anything
    /// that can name the enum value.
    ///
    /// <para>The run identity is what makes the grant single-use: it is minted inside the
    /// capability's one invocation and invalidated before control returns, so a scope obtained during
    /// that invocation cannot be replayed by a later one.</para>
    /// </remarks>
    CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
        RestoreStagingManagedAuthoritySanitizationCapability authority,
        RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity);

}
