using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Opens the one unpooled handle a Covenant exclusive maintenance step is allowed to hold.
/// </summary>
/// <remarks>
/// Separate from <see cref="ICovenantConnectionSource"/> because the two answer opposite questions.
/// The source hands back the scoped Grimoire connection an ordinary read or write shares; this opens
/// a connection nothing else can be holding, which is the only kind an exclusive lock can be taken
/// on. A maintenance step that reached for the scoped connection would be asking the drain to close
/// the handle it is about to work through.
///
/// <para>Pooling is off by construction. A pooled maintenance handle would be returned to a pool on
/// close and handed back out later carrying <c>locking_mode=EXCLUSIVE</c>, which is a mode an
/// ordinary repository connection must never inherit.</para>
/// </remarks>
internal interface ICovenantMaintenanceConnectionFactory
{

    /// <summary>Opens one unpooled, uninitialized connection to this installation's Grimoire.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken);

}

/// <summary>
/// The installation's own Grimoire file, keyed by the passphrase the host already resolved.
/// </summary>
internal sealed class CovenantMaintenanceConnectionFactory(IGrimoireDbPassphraseSource passphrase)
    : ICovenantMaintenanceConnectionFactory
{

    private readonly IGrimoireDbPassphraseSource _passphrase =
        passphrase ?? throw new ArgumentNullException(nameof(passphrase));

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = ArcanumPaths.GrimoireDatabaseFile,

                Password = _passphrase.Passphrase,

                Pooling = false,
            }.ToString());

        try
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return connection;

        }
        catch
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            throw;

        }

    }

}
