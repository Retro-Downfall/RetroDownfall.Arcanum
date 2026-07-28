namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Ambient flag: the current HTTP request carried an <c>Idempotency-Key</c> header.
/// Not a public <see cref="Core.Intelligence.PingRequest"/> field — forged bodies cannot set it.
/// </summary>
internal static class TurnIdempotencyAmbient
{

    private static readonly AsyncLocal<State?> CurrentLocal = new();

    public static bool Current => CurrentLocal.Value?.HasIdempotencyKey == true;

    public static CancellationToken OwnershipLostToken =>
        CurrentLocal.Value?.OwnershipLostToken ?? CancellationToken.None;

    public static void Publish(
        bool hasIdempotencyKey,
        CancellationToken ownershipLostToken = default) =>
        CurrentLocal.Value = new State(hasIdempotencyKey, ownershipLostToken);

    public static void Clear() => CurrentLocal.Value = null;

    private sealed record State(
        bool HasIdempotencyKey,
        CancellationToken OwnershipLostToken);

}
