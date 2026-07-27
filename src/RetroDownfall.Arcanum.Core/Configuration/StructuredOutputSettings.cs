namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Code-owned structured-output policy: JSON Schema validation, correction behavior, and
/// provider-side constrained decoding (OpenAI-compatible <c>strict: true</c>). Strict failure is
/// selected by the request's schema wrapper, not a public configuration root.
/// </summary>
public sealed record StructuredOutputSettings
{

    /// <summary>
    /// When <see langword="true"/> (default), Arcanum validates JSON Schema responses and requests correction
    /// on validation failures. When <see langword="false"/>, the feature is disabled and responses
    /// are returned as-is even when a <c>response_format: json_schema</c> is requested.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (default), Arcanum asks the provider to constrain decoding
    /// (OpenAI <c>strict: true</c>) when available. When <see langword="false"/>, only
    /// post-response validation is used.
    /// </summary>
    public bool UseProviderConstrainedDecoding { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, a response that remains invalid after correction stops making
    /// progress is rejected with a 400 <c>StructuredOutput.ValidationFailed</c> error. When
    /// <see langword="false"/> (default), the last response is returned with a warning header and
    /// <c>system_fingerprint</c> marker so callers can inspect it.
    /// </summary>
    public bool StrictMode { get; set; }

    /// <summary>
    /// Maximum recursion depth for JSON Schema parsing and validation. Default 10; clamped 1–50 by
    /// <see cref="ArcanumSettingClamps.JsonSchemaMaxDepth"/>. Deeply nested schemas or payloads
    /// that exceed this limit are rejected with <c>StructuredOutput.SchemaInvalid</c>.
    /// </summary>
    public int SchemaMaxDepth { get; set; } = 10;

}
