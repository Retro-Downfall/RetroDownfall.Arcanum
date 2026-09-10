namespace RetroDownfall.Arcanum.Api.Security;

public static class ArcanumApiHeaders
{
    public const string ApiKey = "X-Arcanum-Key";

    /// <summary>Fresh 256-bit nonce for the anonymous local-host presence proof.</summary>
    public const string PresenceNonce = "X-Arcanum-Presence-Nonce";

    /// <summary>Version of the local-host presence proof protocol.</summary>
    public const string PresenceVersion = "X-Arcanum-Presence-Version";

    /// <summary>Canonical loopback authority bound into the local-host presence proof.</summary>
    public const string PresenceAuthority = "X-Arcanum-Presence-Authority";

    /// <summary>Nonce-bound HMAC proving the responder knows this installation's API-key digest.</summary>
    public const string PresenceProof = "X-Arcanum-Presence-Proof";

    /// <summary>Nonce-bound encrypted capability issued by the responding Arcanum process.</summary>
    public const string PresenceCapability = "X-Arcanum-Presence-Capability";

    /// <summary>Short-lived bearer capability accepted only by the issuing Arcanum process.</summary>
    public const string ProcessCapability = "X-Arcanum-Process-Capability";

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
