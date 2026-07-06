namespace RetroDownfall.Arcanum.Api.Security;

public static class ArcanumApiHeaders
{
    public const string ApiKey = "X-Arcanum-Key";

    /// <summary>Client-supplied replay-protection key — see <see cref="IdempotencyEndpointFilters"/>.</summary>
    public const string IdempotencyKey = "Idempotency-Key";
}
