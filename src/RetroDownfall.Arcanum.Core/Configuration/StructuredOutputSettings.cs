namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Settings governing structured-output enforcement: JSON schema validation, retry behavior, and
/// provider-side constrained decoding (GBNF grammars for llama.cpp, strict mode for OpenAI-compatible).
/// </summary>
public sealed record StructuredOutputSettings
{

    /// <summary>
    /// When <see langword="true"/> (default), Arcanum validates JSON Schema responses and retries
    /// on validation failures. When <see langword="false"/>, the feature is disabled and responses
    /// are returned as-is even when a <c>response_format: json_schema</c> is requested.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Maximum number of retry attempts after a validation failure. Default 2; clamped 0–5 by
    /// <see cref="ArcanumSettingClamps.StructuredOutputMaxValidationRetries"/>.
    /// </summary>
    public int MaxValidationRetries { get; init; } = 2;

    /// <summary>
    /// When <see langword="true"/> (default), Arcanum asks the provider to constrain decoding
    /// (llama.cpp GBNF grammar, OpenAI <c>strict: true</c>) when available. When
    /// <see langword="false"/>, only post-response validation is used.
    /// </summary>
    public bool UseProviderConstrainedDecoding { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/>, a response that fails schema validation after all retries is
    /// rejected with a 400 <c>StructuredOutput.ValidationFailed</c> error. When
    /// <see langword="false"/> (default), the last response is returned with a warning header and
    /// <c>system_fingerprint</c> marker so callers can inspect it.
    /// </summary>
    public bool StrictMode { get; init; }

    /// <summary>
    /// Maximum recursion depth for JSON Schema parsing and validation. Default 10; clamped 1–50 by
    /// <see cref="ArcanumSettingClamps.JsonSchemaMaxDepth"/>. Deeply nested schemas or payloads
    /// that exceed this limit are rejected with <c>StructuredOutput.SchemaInvalid</c>.
    /// </summary>
    public int SchemaMaxDepth { get; init; } = 10;

}
