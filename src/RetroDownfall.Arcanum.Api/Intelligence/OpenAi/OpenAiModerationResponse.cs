using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>Response body for <c>POST /v1/moderations</c>. See DESIGN.md §11.18.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiModerationResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("results")] List<OpenAiModerationResult> Results);

/// <summary>
/// One verdict per <c>input</c> item. Phase 1 is a pass-through stub — Arcanum runs no local or
/// remote moderation model yet, so every field is always <c>false</c>/<c>0.0</c>.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiModerationResult(
    [property: JsonPropertyName("flagged")] bool Flagged,
    [property: JsonPropertyName("categories")] OpenAiModerationCategories Categories,
    [property: JsonPropertyName("category_scores")] OpenAiModerationCategoryScores CategoryScores)
{

    public static OpenAiModerationResult CreateUnflagged() => new(
        Flagged: false,
        Categories: OpenAiModerationCategories.None,
        CategoryScores: OpenAiModerationCategoryScores.Zero);

}

/// <summary>
/// Per-category boolean flags. Several OpenAI category names contain <c>/</c> or <c>-</c>, which are
/// not valid C# identifiers, hence the explicit <see cref="JsonPropertyNameAttribute"/> on every
/// property (the CamelCase source-generation default used elsewhere in <c>ArcanumJsonContext</c>
/// would neither produce these exact names nor handle the slash).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiModerationCategories(
    [property: JsonPropertyName("sexual")] bool Sexual,
    [property: JsonPropertyName("hate")] bool Hate,
    [property: JsonPropertyName("harassment")] bool Harassment,
    [property: JsonPropertyName("self-harm")] bool SelfHarm,
    [property: JsonPropertyName("sexual/minors")] bool SexualMinors,
    [property: JsonPropertyName("hate/threatening")] bool HateThreatening,
    [property: JsonPropertyName("violence/graphic")] bool ViolenceGraphic,
    [property: JsonPropertyName("self-harm/intent")] bool SelfHarmIntent,
    [property: JsonPropertyName("self-harm/instructions")] bool SelfHarmInstructions,
    [property: JsonPropertyName("harassment/threatening")] bool HarassmentThreatening,
    [property: JsonPropertyName("violence")] bool Violence)
{

    public static readonly OpenAiModerationCategories None = new(
        Sexual: false,
        Hate: false,
        Harassment: false,
        SelfHarm: false,
        SexualMinors: false,
        HateThreatening: false,
        ViolenceGraphic: false,
        SelfHarmIntent: false,
        SelfHarmInstructions: false,
        HarassmentThreatening: false,
        Violence: false);

}

/// <summary>Per-category confidence scores, same key set as <see cref="OpenAiModerationCategories"/>.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiModerationCategoryScores(
    [property: JsonPropertyName("sexual")] double Sexual,
    [property: JsonPropertyName("hate")] double Hate,
    [property: JsonPropertyName("harassment")] double Harassment,
    [property: JsonPropertyName("self-harm")] double SelfHarm,
    [property: JsonPropertyName("sexual/minors")] double SexualMinors,
    [property: JsonPropertyName("hate/threatening")] double HateThreatening,
    [property: JsonPropertyName("violence/graphic")] double ViolenceGraphic,
    [property: JsonPropertyName("self-harm/intent")] double SelfHarmIntent,
    [property: JsonPropertyName("self-harm/instructions")] double SelfHarmInstructions,
    [property: JsonPropertyName("harassment/threatening")] double HarassmentThreatening,
    [property: JsonPropertyName("violence")] double Violence)
{

    public static readonly OpenAiModerationCategoryScores Zero = new(
        Sexual: 0.0,
        Hate: 0.0,
        Harassment: 0.0,
        SelfHarm: 0.0,
        SexualMinors: 0.0,
        HateThreatening: 0.0,
        ViolenceGraphic: 0.0,
        SelfHarmIntent: 0.0,
        SelfHarmInstructions: 0.0,
        HarassmentThreatening: 0.0,
        Violence: 0.0);

}
