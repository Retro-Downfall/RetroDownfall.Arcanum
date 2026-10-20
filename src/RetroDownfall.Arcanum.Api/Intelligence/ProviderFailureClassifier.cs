using System.ClientModel;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class ProviderFailureClassifier
{
    /// <summary>
    /// Classifies an exception observed during lease construction or inference as a connectivity
    /// failure eligible for resilience fallback (retry on the next healthy candidate provider).
    /// Caller-initiated cancellation (<paramref name="callerToken"/> already cancelled) is never a
    /// connectivity failure — it propagates immediately and is never retried. Model-not-found, token
    /// limit, content filter, and tool-loop-limit failures also do not count.
    /// </summary>
    /// <remarks>
    /// The classification (which triggers provider fallback and health tracking) goes purely by exception
    /// type/status — never by inspecting <see cref="Exception.Message"/>. A substring match on the
    /// message text is both too broad (any error whose text happens to contain "connection" — including
    /// some 4xx/5xx model errors — would wrongly fall back) and too narrow (a real connectivity failure
    /// phrased differently would be missed), and message text is not a stable contract across
    /// SDK/runtime versions.
    /// </remarks>
    internal static bool IsConnectivityFailure(Exception ex, CancellationToken callerToken)
    {
        if (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            return true;
        }

        if (ex is TaskCanceledException { InnerException: TimeoutException })
        {
            return true;
        }

        // A Familiar has no socket, so its transport failures arrive as a typed transport exception
        // rather than an HTTP one. The same distinction applies: an unreachable transport (binary
        // missing, spawn refused, deadline passed) is a connectivity-class failure and may fall back
        // to the next candidate, while the CLI answering and refusing — a rejected model name, a
        // rate limit — is a provider verdict that must stand.
        if (ex is Familiars.FamiliarTransportException familiar)
        {
            return familiar.Failure
                is FamiliarProcessFailure.NotInstalled
                or FamiliarProcessFailure.StartFailed
                or FamiliarProcessFailure.TimedOut;
        }

        if (ex is OperationCanceledException)
        {
            return !callerToken.IsCancellationRequested;
        }

        if (ex is ClientResultException clientResultEx)
        {
            // OpenAI-SDK-shaped providers (OpenAI, DeepSeek, most self-hosted OpenAI-compatible
            // servers — see WeaveService's identical handling for the embedding path) surface HTTP
            // error responses as ClientResultException, not HttpRequestException. A genuine
            // connectivity failure (DNS, connection refused, TLS handshake failure) either never
            // reaches an HTTP response (Status <= 0) or wraps the underlying transport exception; an
            // actual HTTP response (400/401/404/429/5xx, ...) is a model/auth/rate-limit failure, not
            // a connectivity one, and must not trigger provider fallback.
            return clientResultEx.Status <= 0
                || (clientResultEx.InnerException is { } clientInner && IsConnectivityFailure(clientInner, callerToken));
        }

        return ex.InnerException is { } inner && IsConnectivityFailure(inner, callerToken);
    }
}
