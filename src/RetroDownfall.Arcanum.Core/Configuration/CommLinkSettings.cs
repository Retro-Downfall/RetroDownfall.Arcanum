namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Comm Link runtime projection. Environment reference and allowlists come from
/// <c>Arcanum:Integrations:CommLink</c>; transport timeout is code-owned.
/// </summary>
public sealed record CommLinkSettings
{

    /// <summary>
    /// Optional exact environment-variable name containing the secret-bearing webhook URL.
    /// The value itself is resolved only when a notification is dispatched.
    /// </summary>
    public string? WebhookUrlEnvironmentVariable { get; set; }

    /// <summary>
    /// Timeout (seconds) for the named <c>HttpClient("CommLinkWebhook")</c> used to POST
    /// alerts. Default 15; clamp 1&#8211;120.
    /// </summary>
    public int WebhookTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// URI schemes the webhook dispatcher is permitted to call. Default
    /// <c>["https"]</c>. Add <c>"http"</c> explicitly to opt in to plaintext webhooks;
    /// any URL whose scheme is not in this list is rejected with a warning.
    /// </summary>
    public string[] AllowedSchemes { get; set; } = ["https"];

    /// <summary>
    /// Optional list of allowed webhook hosts (e.g. <c>["hooks.example.com"]</c>). When
    /// populated, a resolved webhook URL whose host is not in this list is suppressed at dispatch.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

}
