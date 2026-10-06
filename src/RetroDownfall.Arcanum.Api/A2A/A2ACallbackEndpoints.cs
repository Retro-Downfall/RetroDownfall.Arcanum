using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.A2A;

/// <summary>
/// The endpoint a remote agent posts an outbound Sending's task transitions to (issue #67).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <strong>outside</strong> <c>ApiKeyEndpointFilter</c>, unlike every other A2A route
/// (&#167;5.7.1): the caller is a peer agent, which by definition does not hold this instance's operator
/// API key. Authentication is instead a 256-bit secret minted per Sending, handed to the peer inside the
/// push-notification config, and compared in constant time against a stored digest. A post that presents
/// the wrong secret — or names a callback nobody is waiting on — is answered <c>404</c>, so the endpoint
/// never confirms which config ids exist.
/// </para>
/// <para>
/// The whole surface is off unless an operator sets <c>Arcanum:Integrations:A2A:PushNotifications</c>,
/// and the route is not mapped at all when it is off.
/// </para>
/// </remarks>
[ExcludeFromCodeCoverage] // Reason: thin HTTP glue; behavior covered via A2ASendingCallbackRegistry and A2A callback tests.
internal static class A2ACallbackEndpoints
{
    /// <summary>
    /// Whether the callback route exists for these settings: Conclave and the A2A surface on, and
    /// push notifications enabled. The one definition the route mapping, the per-call gate and the
    /// Host-header allow-list (which answers the callback's configured name) all read.
    /// </summary>
    internal static bool IsSurfaceEnabled(ArcanumSettings settings)
    {
        ConclaveA2ASettings a2a = settings.ResolveA2A();

        return settings.ResolveConclave().Enabled && a2a.Enabled && a2a.PushNotificationsEnabled;
    }

    public static IEndpointRouteBuilder MapA2ACallbacks(
        this IEndpointRouteBuilder app,
        ArcanumSettings startupSettings,
        string? rateLimiterPolicyName)
    {
        if (!IsSurfaceEnabled(startupSettings))
        {
            return app;
        }

        ConclaveA2ASettings a2a = startupSettings.ResolveA2A();

        RouteHandlerBuilder route = app.MapPost(
            $"{A2AClientService.ResolveCallbackPath(a2a)}/{{configId}}",
            HandleAsync)
        .WithName("PostA2ASendingCallback")
        .WithMetadata(InstallationResetRecoveryHiddenRouteMetadata.Instance)
        .AllowAnonymous();

        if (!string.IsNullOrWhiteSpace(rateLimiterPolicyName))
        {
            route.RequireRateLimiting(rateLimiterPolicyName);
        }

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string configId,
        A2ASendingCallbackRegistry callbacks,
        IOptionsMonitor<ArcanumSettings> settings,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!IsSurfaceEnabled(settings.CurrentValue))
        {
            // Routes are mapped from the boot snapshot but gated per call, like every other Conclave
            // surface: turning the feature off mid-run closes the door immediately.
            return Results.NotFound();
        }

        string token = context.Request.Headers[A2APushNotificationHeaders.NotificationToken].ToString();

        switch (callbacks.TrySignal(configId, token))
        {
            case A2ACallbackOutcome.Delivered:

                return Results.Accepted();

            case A2ACallbackOutcome.Unauthenticated:

                return Results.NotFound();

            default:

                // Nothing in this process is waiting: either the Sending was registered by a previous
                // process, or the config id is fiction. The durable record tells the two apart.
                return await SettleFromLedgerAsync(
                        configId,
                        token,
                        scopeFactory,
                        loggerFactory.CreateLogger(typeof(A2ACallbackEndpoints)),
                        cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Settles an outbound Sending whose awaiting process is gone, from its durable record.
    /// </summary>
    /// <remarks>
    /// The cost is recorded as <em>unknown</em> rather than fetched: this process never observed the
    /// remote task, and inventing a figure — or a zero — is exactly what issue #60 removed. The Sending
    /// shows up as unpriced delegated work, which is the honest description of it.
    /// <para>The production ledger is best-effort and answers most of its own store faults: a lookup it
    /// cannot complete is "no durable record" (404 here), and a settlement it cannot finish is logged and
    /// leaves the row open, so a retried callback finds it and settles it then. The arms below are for a
    /// ledger fault that does escape.</para>
    /// </remarks>
    internal static async Task<IResult> SettleFromLedgerAsync(
        string configId,
        string? token,
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IA2ASendingLedger>() is not { } ledger)
        {
            return Results.NotFound();
        }

        A2AOutboundCallback? recorded;

        try
        {
            recorded = await ledger
                .FindOutboundCallbackAsync(configId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GrimoireMaintenanceUnavailableException)
        {
            // Nothing has been authenticated yet, so the answer must be the one an unknown config id gets: a
            // lookup that failed for one id's row and not another's would otherwise tell a stranger which ids
            // exist. A maintenance window propagates instead, to the same 503 every request gets, which says
            // nothing about this id.
            logger.LogWarning(ex, "A2A: could not look up the Sending behind a callback.");

            return Results.NotFound();
        }

        if (recorded is not { } callback || !A2ACallbackToken.Matches(token, callback.TokenHash))
        {
            return Results.NotFound();
        }

        try
        {
            await ledger
                .SettleOutboundAsync(callback.Ledger, A2ARemoteCost.Unknown, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GrimoireMaintenanceUnavailableException)
        {
            // The caller proved the Sending's secret, so it may learn that the ledger could not settle it. A
            // peer treats 404 as terminal and stops retrying, and this Sending is still owed its settlement;
            // 503 asks it to come back.
            logger.LogWarning(ex, "A2A: could not settle a Sending from callback config {ConfigId}.", configId);

            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation(
            "A2A: settled outbound Sending for remote task {TaskId} from a callback that arrived after "
            + "the process which dispatched it had gone.",
            callback.TaskId);

        return Results.Accepted();
    }
}
