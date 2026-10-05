using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A catalog another connection holds under an exclusive lock, and a recovery connection to it
/// whose every read meets <c>SQLITE_BUSY</c>.
/// </summary>
/// <remarks>
/// The catalog is a plain rollback-journal database, because there <c>BEGIN EXCLUSIVE</c> keeps
/// readers out as well as writers; under WAL the read would go through. It holds one exact
/// <c>covenant_authority_state</c> row, so the read fails only because of the lock. The recovery
/// connection's command timeout is one second, so the provider's own busy retry gives up quickly
/// instead of after its 30-second default.
/// </remarks>
internal sealed class ExclusivelyLockedCatalog : IAsyncDisposable
{
    private readonly string _directory;

    private readonly SqliteConnection _holder;

    private ExclusivelyLockedCatalog(
        string directory,
        SqliteConnection holder,
        SqliteConnection recoveryConnection)
    {
        _directory = directory;

        _holder = holder;

        RecoveryConnection = recoveryConnection;
    }

    internal SqliteConnection RecoveryConnection { get; }

    internal static async Task<ExclusivelyLockedCatalog> CreateAsync(
        Guid installationId,
        CancellationToken cancellationToken)
    {
        SqliteNativeRuntime.Instance.Initialize();

        string directory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-locked-catalog-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "locked.db"),
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();

        SqliteConnection holder = new(connectionString);

        SqliteConnection recovery = new(connectionString);

        try
        {
            await holder.OpenAsync(cancellationToken);

            await using (SqliteCommand create = holder.CreateCommand())
            {
                create.CommandText = """
                    PRAGMA journal_mode=DELETE;
                    CREATE TABLE covenant_authority_state(
                        StateKey INTEGER PRIMARY KEY,
                        InstallationIdentity TEXT NOT NULL);
                    INSERT INTO covenant_authority_state(StateKey, InstallationIdentity)
                    VALUES (1, $identity);
                    """;

                create.Parameters.AddWithValue(
                    "$identity",
                    installationId.ToString("D").ToUpperInvariant());

                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await recovery.OpenAsync(cancellationToken);

            await using (SqliteCommand exclusive = holder.CreateCommand())
            {
                exclusive.CommandText = "BEGIN EXCLUSIVE;";

                await exclusive.ExecuteNonQueryAsync(cancellationToken);
            }

            return new ExclusivelyLockedCatalog(directory, holder, recovery);
        }
        catch
        {
            await recovery.DisposeAsync();

            await holder.DisposeAsync();

            Directory.Delete(directory, recursive: true);

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RecoveryConnection.DisposeAsync();

        await _holder.DisposeAsync();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temporary directory.
        }
    }
}
