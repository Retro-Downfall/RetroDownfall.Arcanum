using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.A2A;

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

    public static IEndpointRouteBuilder MapA2ACallbacks(
        this IEndpointRouteBuilder app,
        ArcanumSettings startupSettings,
        string? rateLimiterPolicyName)
    {

        ConclaveA2ASettings a2a = startupSettings.ResolveA2A();

        if (!startupSettings.ResolveConclave().Enabled || !a2a.Enabled || !a2a.PushNotificationsEnabled)
        {

            return app;

        }

        RouteHandlerBuilder route = app.MapPost(
            $"{A2AClientService.ResolveCallbackPath(a2a)}/{{configId}}",
            HandleAsync)
        .WithName("PostA2ASendingCallback")
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

        ConclaveA2ASettings current = settings.CurrentValue.ResolveA2A();

        if (!settings.CurrentValue.ResolveConclave().Enabled
            || !current.Enabled
            || !current.PushNotificationsEnabled)
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
    /// </remarks>
    private static async Task<IResult> SettleFromLedgerAsync(
        string configId,
        string? token,
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {

        try
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            if (scope.ServiceProvider.GetService<IA2ASendingLedger>() is not { } ledger)
            {

                return Results.NotFound();

            }

            A2AOutboundCallback? recorded = await ledger
                .FindOutboundCallbackAsync(configId, cancellationToken)
                .ConfigureAwait(false);

            if (recorded is not { } callback || !A2ACallbackToken.Matches(token, callback.TokenHash))
            {

                return Results.NotFound();

            }

            await ledger
                .SettleOutboundAsync(callback.Ledger, A2ARemoteCost.Unknown, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "A2A: settled outbound Sending for remote task {TaskId} from a callback that arrived after "
                + "the process which dispatched it had gone.",
                callback.TaskId);

            return Results.Accepted();

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "A2A: could not settle a Sending from callback config {ConfigId}.", configId);

            return Results.NotFound();

        }

    }

}
