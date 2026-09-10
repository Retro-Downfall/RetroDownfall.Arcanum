using System.Net;
using System.Net.Http.Headers;
using RetroDownfall.Arcanum.Api.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Owns the one authenticated send path for every local API client. A rejected short-lived process
/// capability is renewed exactly once; replayable requests are rebuilt before the single retry,
/// and a rejected retry is evicted locally for the next call without a third request in this call.
/// </summary>
internal static class ArcanumAuthenticatedHttpSender
{
    internal static readonly TimeSpan PresenceProbeTimeout = TimeSpan.FromSeconds(2);

    internal static Task<ArcanumAuthenticatedHttpResponse> SendAsync(
        HttpClient client,
        ArcanumApiCredentialLease credentialLease,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        bool canReplayAfterUnauthorized,
        CancellationToken cancellationToken) =>
        SendAsync(
            client,
            credentialLease,
            requestFactory,
            completionOption,
            canReplayAfterUnauthorized,
            PresenceProbeTimeout,
            cancellationToken);

    internal static async Task<ArcanumAuthenticatedHttpResponse> SendAsync(
        HttpClient client,
        ArcanumApiCredentialLease credentialLease,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        bool canReplayAfterUnauthorized,
        TimeSpan credentialTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(credentialLease);
        ArgumentNullException.ThrowIfNull(requestFactory);

        if (credentialTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(credentialTimeout),
                "The credential-resolution timeout must be positive.");
        }

        RejectDefaultCredentialHeaders(client);

        ApiCredentialLeaseResult credentials = await credentialLease
            .ResolveAsync(credentialTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (credentials is not { IsVerified: true, ProcessCapability: not null })
        {
            return ArcanumAuthenticatedHttpResponse.Unauthenticated(credentials);
        }

        string capability = credentials.ProcessCapability;
        List<HttpRequestMessage> requests = [];
        HttpResponseMessage? response = null;

        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                HttpRequestMessage request = requestFactory()
                    ?? throw new InvalidOperationException(
                        "The authenticated request factory returned null.");

                requests.Add(request);

                RejectRequestCredentialHeaders(request);
                BindRequestToVerifiedAuthority(
                    client,
                    request,
                    credentialLease.CanonicalAuthority);

                _ = request.Headers.TryAddWithoutValidation(
                    ArcanumApiHeaders.ProcessCapability,
                    capability);

                response = await client
                    .SendAsync(request, completionOption, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    return ArcanumAuthenticatedHttpResponse.Authenticated(
                        response,
                        credentials,
                        requests);
                }

                if (attempt != 0)
                {
                    _ = credentialLease.InvalidateRejectedCapability(capability);

                    return ArcanumAuthenticatedHttpResponse.Authenticated(
                        response,
                        credentials,
                        requests);
                }

                ApiCredentialLeaseResult refreshed = await credentialLease
                    .RefreshAsync(
                        capability,
                        credentialTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (refreshed is not { IsVerified: true, ProcessCapability: not null })
                {
                    response.Dispose();
                    response = null;
                    DisposeRequests(requests);
                    requests.Clear();

                    return ArcanumAuthenticatedHttpResponse.Unauthenticated(
                        refreshed);
                }

                if (!canReplayAfterUnauthorized)
                {
                    return ArcanumAuthenticatedHttpResponse.Authenticated(
                        response,
                        refreshed,
                        requests);
                }

                response.Dispose();
                response = null;
                credentials = refreshed;
                capability = refreshed.ProcessCapability;
            }

            throw new InvalidOperationException(
                "The bounded authenticated-send loop did not return a response.");
        }
        catch
        {
            response?.Dispose();
            DisposeRequests(requests);

            throw;
        }
    }

    private static void RejectDefaultCredentialHeaders(HttpClient client)
    {
        if (ContainsCredentialHeader(client.DefaultRequestHeaders))
        {
            throw new InvalidOperationException(
                "The local API client must leave authentication headers to the authenticated sender.");
        }
    }

    private static void RejectRequestCredentialHeaders(HttpRequestMessage request)
    {
        if (ContainsCredentialHeader(request.Headers)
            || (request.Content is not null
                && ContainsCredentialHeader(request.Content.Headers)))
        {
            throw new InvalidOperationException(
                "The request factory must leave local API authentication to the authenticated sender.");
        }
    }

    private static bool ContainsCredentialHeader(HttpHeaders headers) =>
        headers.NonValidated.Any(static header =>
            string.Equals(
                header.Key,
                ArcanumApiHeaders.ProcessCapability,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                header.Key,
                ArcanumApiHeaders.ApiKey,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                header.Key,
                "Authorization",
                StringComparison.OrdinalIgnoreCase));

    private static void BindRequestToVerifiedAuthority(
        HttpClient client,
        HttpRequestMessage request,
        string verifiedAuthority)
    {
        Uri? requestUri = request.RequestUri;

        if (requestUri is null)
        {
            throw new InvalidOperationException(
                "The authenticated request must have a URI.");
        }

        Uri absoluteUri;

        if (requestUri.IsAbsoluteUri)
        {
            absoluteUri = requestUri;
        }
        else if (client.BaseAddress is not null)
        {
            absoluteUri = new Uri(client.BaseAddress, requestUri);
        }
        else
        {
            throw new InvalidOperationException(
                "A relative authenticated request requires a local API base address.");
        }

        if (!ArcanumPresenceProofProtocol.TryCanonicalAuthority(
                absoluteUri,
                out string? requestAuthority)
            || !string.Equals(
                requestAuthority,
                verifiedAuthority,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The authenticated request authority does not match the verified local API presence authority.");
        }
    }

    internal static void DisposeRequests(IEnumerable<HttpRequestMessage> requests)
    {
        foreach (HttpRequestMessage request in requests)
        {
            request.Dispose();
        }
    }
}

/// <summary>
/// Couples a response to every request body that must remain alive while the response is consumed.
/// </summary>
internal sealed class ArcanumAuthenticatedHttpResponse : IDisposable
{
    private IReadOnlyList<HttpRequestMessage>? _requests;

    private HttpResponseMessage? _response;

    private ArcanumAuthenticatedHttpResponse(
        HttpResponseMessage? response,
        ApiCredentialLeaseResult credentials,
        IReadOnlyList<HttpRequestMessage>? requests)
    {
        _response = response;
        Credentials = credentials;
        _requests = requests;
    }

    internal bool IsAuthenticated => _response is not null;

    internal HttpResponseMessage? Response => _response;

    internal ApiCredentialLeaseResult Credentials { get; }

    internal static ArcanumAuthenticatedHttpResponse Authenticated(
        HttpResponseMessage response,
        ApiCredentialLeaseResult credentials,
        IReadOnlyList<HttpRequestMessage> requests) =>
        new(response, credentials, requests);

    internal static ArcanumAuthenticatedHttpResponse Unauthenticated(
        ApiCredentialLeaseResult credentials) =>
        new(null, credentials, null);

    public void Dispose()
    {
        HttpResponseMessage? response = Interlocked.Exchange(ref _response, null);
        IReadOnlyList<HttpRequestMessage>? requests = Interlocked.Exchange(ref _requests, null);

        response?.Dispose();

        if (requests is not null)
        {
            ArcanumAuthenticatedHttpSender.DisposeRequests(requests);
        }
    }
}
