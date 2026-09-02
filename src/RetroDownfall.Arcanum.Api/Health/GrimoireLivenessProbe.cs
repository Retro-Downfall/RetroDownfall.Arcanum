using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Api.Health;

/// <summary>
/// Cached, bounded <c>SELECT 1</c> against the Grimoire. Does not mutate
/// <see cref="IGrimoireDbReadiness"/>.
/// </summary>
public sealed class GrimoireLivenessProbe(IServiceScopeFactory scopeFactory) : IGrimoireLivenessProbe
{

    internal static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(3);

    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();

    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    private bool _cachedOk;

    private string _cachedDetail = string.Empty;

    private TimeSpan _cacheTtl = DefaultCacheTtl;

    private TimeSpan _probeTimeout = DefaultProbeTimeout;

    /// <summary>Test seam: override cache TTL (call <see cref="ResetTestSeams"/> in teardown).</summary>
    internal void SetCacheTtlForTests(TimeSpan ttl) => _cacheTtl = ttl;

    /// <summary>Test seam: override probe timeout.</summary>
    internal void SetProbeTimeoutForTests(TimeSpan timeout) => _probeTimeout = timeout;

    /// <summary>Test seam: clear cached result so the next probe hits the database.</summary>
    internal void ClearCacheForTests()
    {
        lock (_gate)
        {
            _cachedAt = DateTimeOffset.MinValue;
            _cachedOk = false;
            _cachedDetail = string.Empty;
        }
    }

    internal void ResetTestSeams()
    {
        _cacheTtl = DefaultCacheTtl;
        _probeTimeout = DefaultProbeTimeout;
        ClearCacheForTests();
    }

    public async Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _cachedAt < _cacheTtl)
            {
                return (_cachedOk, _cachedDetail);
            }
        }

        (bool ok, string detail) = await ExecuteProbeAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _cachedAt = DateTimeOffset.UtcNow;
            _cachedOk = ok;
            _cachedDetail = detail;
        }

        return (ok, detail);
    }

    private async Task<(bool Ok, string Detail)> ExecuteProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_probeTimeout);

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            IGrimoireOrdinaryConnectionFactory connections =
                scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

            if (db.Database.GetDbConnection() is not SqliteConnection scopedConnection)
            {
                throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
            }

            Result<IGrimoireOrdinaryConnectionLease> acquired = await connections
                .AcquireScopedAsync(
                    scopedConnection,
                    CovenantSqliteConnectionMode.ReadOnly,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                throw new GrimoireMaintenanceUnavailableException();
            }

            await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

            DbConnection connection = lease.Connection;

            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(_probeTimeout.TotalSeconds));

            object? scalar = await command.ExecuteScalarAsync(timeoutCts.Token).ConfigureAwait(false);

            int scalarValue = scalar is null ? 0 : Convert.ToInt32(scalar);

            await GrimoireScopedConsumerTestSeam
                .PauseAsync(
                    "GrimoireLivenessProbe.ExecuteProbeAsync",
                    GrimoireScopedConsumerFinalUseKind.ScalarConverted,
                    scalarValue,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (scalar is null || scalarValue != 1)
            {
                return (false, "Grimoire live probe did not return 1.");
            }

            return (true, "Database ready.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "Grimoire live probe timed out.");
        }
        catch (Exception ex)
        {
            return (false, $"Grimoire live probe failed: {ex.GetType().Name}.");
        }
    }

}
