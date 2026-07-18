namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Scrying — vision/multimodality. Bound from <c>Arcanum:Scrying</c>. Governs image content
/// accepted on inference requests (native <c>ContentParts</c>/<c>ScryingFoci</c> and OpenAI
/// <c>/v1/chat/completions</c> <c>image_url</c> parts) independent of per-model capability
/// declarations (see <see cref="ModelEntry.SupportsVision"/>).
/// </summary>
public sealed record ScryingSettings
{

    /// <summary>
    /// Master kill-switch. When <c>false</c>, image content is rejected at the API boundary even
    /// for vision-capable models — useful for privacy-sensitive deployments.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum bytes per image, measured against the decoded <c>data:</c> URI payload (base64
    /// image bytes). Default <c>1,048,576</c> (1 MiB); clamped 1 KiB - 20 MiB at runtime.
    /// <c>http(s)</c>-hosted images are not size-checked here — the downstream provider fetches
    /// and rejects them.
    /// </summary>
    public long MaxImageBytes { get; set; } = 1_048_576L;

    /// <summary>
    /// Maximum images per inference request (across native <c>ScryingFoci</c>/<c>ContentParts</c>
    /// and <c>/v1</c> <c>image_url</c> parts combined). Default <c>10</c>; clamped 1-100 at runtime.
    /// </summary>
    public int MaxImagesPerRequest { get; set; } = 10;

    /// <summary>
    /// Allowed image MIME types. Non-matching types are rejected. Only enforced for
    /// <c>data:</c>-URI images (CLI <c>ScryingFoci</c> and inline data URIs) where the MIME type is
    /// present in the payload; not enforced for <c>http(s)</c> URLs.
    /// </summary>
    public string[] AllowedMimeTypes { get; set; } =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "image/bmp",
    ];

}
