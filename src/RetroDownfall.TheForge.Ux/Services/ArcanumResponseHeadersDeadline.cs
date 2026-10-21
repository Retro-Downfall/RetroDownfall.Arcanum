namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>Whether the response starts an ordinary short call or completes caller-owned work.</summary>
public enum ArcanumRequestLifetime
{
    ResponseHeadersDeadline,

    CallerCancellation,
}

internal static class ArcanumResponseHeadersDeadline
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? timeout,
        TimeProvider clock)
    {
        using CancellationTokenSource? deadline = timeout is { } duration
            ? new CancellationTokenSource(duration, clock)
            : null;

        using CancellationTokenSource? linked = deadline is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            linked?.Token ?? cancellationToken).ConfigureAwait(false);
    }
}
