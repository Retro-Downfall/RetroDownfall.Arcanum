namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Ambient flag: the current HTTP request carried an <c>Idempotency-Key</c> header.
/// Not a public <see cref="Core.Intelligence.PingRequest"/> field — forged bodies cannot set it.
/// </summary>
internal static class TurnIdempotencyAmbient
{

    private static readonly AsyncLocal<bool?> CurrentLocal = new();

    public static bool Current => CurrentLocal.Value == true;

    public static void Publish(bool hasIdempotencyKey) => CurrentLocal.Value = hasIdempotencyKey;

    public static void Clear() => CurrentLocal.Value = null;

}
