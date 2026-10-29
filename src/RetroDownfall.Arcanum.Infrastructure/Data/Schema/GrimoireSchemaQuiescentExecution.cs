using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Connection-bound evidence of a held startup lock or private staged restore generation.</summary>
internal sealed class GrimoireSchemaQuiescentExecution : IDisposable
{
    private readonly SqliteConnection _connection;

    private readonly HashSet<string> _begunReadOnlyCensuses = new(StringComparer.Ordinal);

    private readonly SQLitePCL.sqlite3 _handle;

    private volatile bool _invalidated;

    private readonly ArcanumMaintenanceLock _maintenance;

    private readonly string _guardedDirectory;

    private readonly string _dataSource;

    private readonly IGrimoireDbReadiness? _readiness;

    private readonly string _configuredSource;

    private GrimoireSchemaQuiescentExecution(SqliteConnection connection, ArcanumMaintenanceLock maintenance,
        string guardedDirectory, IGrimoireDbReadiness? readiness)
    {
        _connection = connection;

        _handle = connection.Handle ?? throw new InvalidOperationException("The schema census requires an open native handle.");

        _maintenance = maintenance;

        _guardedDirectory = guardedDirectory;

        _dataSource = Path.GetFullPath(connection.DataSource);

        _configuredSource = Path.GetFullPath(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource);

        _readiness = readiness;

        connection.StateChange += OnStateChange;
    }

    internal static GrimoireSchemaQuiescentExecution ForStartup(SqliteConnection connection,
        ArcanumMaintenanceLock maintenance, string guardedDirectory, IGrimoireDbReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        return new(connection, maintenance, guardedDirectory, readiness);
    }

    internal static GrimoireSchemaQuiescentExecution ForStagedRestore(SqliteConnection connection,
        ArcanumMaintenanceLock maintenance, string guardedDirectory, string stagedDatabase)
    {
        string actual = Path.GetFullPath(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource);

        if (!string.Equals(actual, Path.GetFullPath(stagedDatabase), StringComparison.Ordinal)
            || string.Equals(actual, Path.GetFullPath(Path.Combine(guardedDirectory, "arcanum.db")), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A schema restore census requires the exact private staged database.");
        }

        return new(connection, maintenance, guardedDirectory, null);
    }

    internal void Require(SqliteConnection connection)
    {
        if (_invalidated || !ReferenceEquals(connection, _connection)
            || connection.State != System.Data.ConnectionState.Open
            || !ReferenceEquals(connection.Handle, _handle)
            || !string.Equals(Path.GetFullPath(connection.DataSource), _dataSource, StringComparison.Ordinal)
            || _readiness?.IsReady == true
            || (_readiness is not null && !string.Equals(_configuredSource,
                Path.GetFullPath(Path.Combine(_guardedDirectory, "arcanum.db")), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The schema census no longer owns its quiescent source connection.");
        }

        _maintenance.AssertHeldFor(_guardedDirectory);
    }

    internal bool HasBegunReadOnlyCensus(string name) => _begunReadOnlyCensuses.Contains(name);

    internal void MarkReadOnlyCensusBegun(string name) => _begunReadOnlyCensuses.Add(name);

    private void OnStateChange(object? sender, System.Data.StateChangeEventArgs args)
    {
        if (args.CurrentState != System.Data.ConnectionState.Open) _invalidated = true;
    }

    public void Dispose()
    {
        _invalidated = true;

        _connection.StateChange -= OnStateChange;
    }
}
