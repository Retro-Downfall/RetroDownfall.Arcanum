using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Single content part inside an OpenAI <c>messages[].content</c> array.
/// Discriminator <c>type</c> is one of <c>"text"</c> or <c>"image_url"</c>. Only the field that
/// matches the discriminator is populated by clients; the others stay <c>null</c>.
/// </summary>
public sealed record OpenAiContentPart(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("image_url")] OpenAiImageUrl? ImageUrl = null);

public sealed record OpenAiImageUrl(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("detail")] string? Detail = null);
