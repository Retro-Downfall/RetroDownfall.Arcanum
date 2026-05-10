namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CommLinkSettings
{

    public string? WebhookUrl { get; init; }

    /// <summary>
    /// Timeout (seconds) for the named <c>HttpClient("CommLinkWebhook")</c> used to POST
    /// alerts. Default 15; clamp 1&#8211;120.
    /// </summary>
    public int WebhookTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// URI schemes the webhook dispatcher is permitted to call. Default
    /// <c>["https", "http"]</c>. Use a single-element array (for example <c>["https"]</c>) to
    /// require TLS; any URL whose scheme is not in this list is rejected with a warning.
    /// </summary>
    public string[] AllowedSchemes { get; init; } = ["https", "http"];

}
