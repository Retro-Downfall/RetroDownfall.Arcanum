using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>Reconciles disposable embedding companions without gating database admission or retrieval.</summary>
internal sealed class QuantizedEmbeddingBackfillService(
    IServiceScopeFactory scopes,
    IGrimoireConnectionAdmissionGate admission,
    ILogger<QuantizedEmbeddingBackfillService> logger) : BackgroundService
{
    private readonly Dictionary<string, string?> _cursors = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int examined = 0;

            try
            {
                examined = await RunTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Maintenance revokes ordinary work; a later admitted tick resumes missing rows.
            }
            catch (GrimoireMaintenanceUnavailableException)
            {
                // Admission may close between the work lease and physical connection acquisition.
            }
            catch (Exception ex)
            {
                logger.LogWarning("Quantized embedding reconciliation deferred after {FailureType}.", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(examined > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<int> RunTickAsync(CancellationToken cancellationToken)
    {
        if (!admission.TryAcquireWorkLease(GrimoireWorkKind.QuantizedEmbeddingBackfill, out IGrimoireWorkLease? admitted))
        {
            return 0;
        }

        await using IGrimoireWorkLease work = admitted!;

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, work.MaintenanceRevocation);

        CancellationToken token = linked.Token;

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        IGrimoireOrdinaryConnectionFactory connections = scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        Result<IGrimoireOrdinaryConnectionLease> acquired = await connections.AcquireScopedAsync(
            connection, CovenantSqliteConnectionMode.ReadWrite, token).ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            throw GrimoireConnectionUnavailableException.FromError(acquired.Error);
        }

        await using IGrimoireOrdinaryConnectionLease ordinary = acquired.Value;

        int total = 0;

        foreach ((string table, _) in QuantizedEmbeddingStore.Families)
        {
            token.ThrowIfCancellationRequested();

            _cursors.TryGetValue(table, out string? cursor);

            // The write transaction takes its writer slot before selecting source bytes. A concurrent
            // authoritative change therefore cannot slip between quantization and its publication.
            await using SqliteTransaction transaction = connection.BeginTransaction();

            (string? next, int examined) = await QuantizedEmbeddingStore.BackfillBatchAsync(
                connection, transaction, table, cursor, 128, token).ConfigureAwait(false);

            await transaction.CommitAsync(token).ConfigureAwait(false);

            // Advance only after commit. Empty pages wrap around so writes behind a live cursor and
            // rows repaired after an unsafe encoding are revisited on the next reconciliation pass.
            _cursors[table] = next;

            total += examined;
        }

        return total;
    }
}
