namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Ambient carrier of the current idempotent HTTP request's ownership-lost token. Not a public
/// <see cref="Core.Intelligence.PingRequest"/> field, so a forged body cannot set it. A turn used to
/// receive a separate "request carried an Idempotency-Key" flag from here and pass it down to a request
/// member that nothing read; only the token has a reader.
/// </summary>
internal static class TurnIdempotencyAmbient
{
    private static readonly AsyncLocal<State?> CurrentLocal = new();

    public static CancellationToken OwnershipLostToken =>
        CurrentLocal.Value?.OwnershipLostToken ?? CancellationToken.None;

    public static void Publish(CancellationToken ownershipLostToken = default) =>
        CurrentLocal.Value = new State(ownershipLostToken);

    public static void Clear() => CurrentLocal.Value = null;

    private sealed record State(CancellationToken OwnershipLostToken);
}
