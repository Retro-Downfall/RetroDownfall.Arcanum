namespace RetroDownfall.Arcanum.Api.Security;

public static class ArcanumApiHeaders
{
    public const string ApiKey = "X-Arcanum-Key";

    /// <summary>Client-supplied replay-protection key — see <see cref="IdempotencyEndpointFilters"/>.</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>Opaque continuation for stable audit-log paging.</summary>
    public const string AuditNextCursor = "X-Arcanum-Next-Cursor";

    /// <summary>Advisory notice emitted when a <c>/v1</c> structured-output request was downgraded.</summary>
    public const string StructuredOutputWarning = "X-Arcanum-Structured-Output-Warning";

    /// <summary>
    /// The one header that suppresses durable context injection for a request. Its only legal value
    /// is the lowercase literal <c>none</c>; every other value is a 400 decided before the body is
    /// read (DESIGN §10.18).
    /// </summary>
    public const string ContextPolicy = "X-Arcanum-Context-Policy";
}
