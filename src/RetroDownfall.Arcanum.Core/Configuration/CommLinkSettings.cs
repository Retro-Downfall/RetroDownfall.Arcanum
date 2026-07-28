namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CommLinkSettings
{

    public string? WebhookUrl { get; set; }

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
    /// populated, any configured <see cref="WebhookUrl"/> whose host is not in this list is
    /// rejected at startup.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

}
