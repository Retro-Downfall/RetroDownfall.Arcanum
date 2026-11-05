using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Recovery remains available with Guild admission disabled.</summary>
internal sealed class GuildMaintenanceHostedService(
    IServiceScopeFactory scopes,
    IGrimoireConnectionAdmissionGate admission,
    IGrimoireDbReadiness readiness,
    TimeProvider clock,
    ILogger<GuildMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await readiness.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);

        string? previousError = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Result<GuildCleanupOutcome> outcome = await RunOnceAsync(stoppingToken).ConfigureAwait(false);

                if (outcome.IsFailure && !string.Equals(previousError, outcome.Error.Code, StringComparison.Ordinal))
                {
                    logger.LogWarning("Guild owner cleanup deferred: {ErrorCode}.", outcome.Error.Code);
                }

                previousError = outcome.IsFailure ? outcome.Error.Code : null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Guild owner cleanup deferred after {ExceptionType}.", exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<Result<GuildCleanupOutcome>> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildMaintenance, out IGrimoireWorkLease? acquired) || acquired is null)
        {
            return new Error(ErrorCodes.Guild.Unavailable, "Guild owner cleanup is deferred while ordinary work admission is closed.");
        }

        await using IGrimoireWorkLease work = acquired;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, work.MaintenanceRevocation);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        IOperatorAuthorityContextIssuer issuer = scope.ServiceProvider.GetRequiredService<IOperatorAuthorityContextIssuer>();

        Result<OperatorAuthorityContext> authority = issuer.Issue(CovenantAuthorityRequirement.SensitivityRetentionPurge);

        if (authority.IsSuccess)
        {
            Result published = scope.ServiceProvider.GetRequiredService<CovenantSensitivePurgeAuthorityScope>().Publish(authority.Value);

            if (published.IsFailure)
            {
                return published.Error;
            }
        }

        // An unavailable Covenant authority still permits the shared purger's unlabelled arm. A
        // labelled target is refused by that same purger and keeps its durable checkpoint and cursor.
        return await scope.ServiceProvider.GetRequiredService<GuildOwnerCleanupCoordinator>()
            .RunBatchAsync(64, 64, lifetime.Token).ConfigureAwait(false);
    }
}
