namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Exact public inference-failure copy by transport surface.
/// Native and OpenAI surfaces retain separate constants; unknown failures use neutral copy.
/// </summary>
internal static class PublicInferenceErrorMessages
{
    public const string NativeGenericFailure =
        "Inference failed. See server logs for details.";

    public const string OpenAiGenericFailure =
        "Inference failed. See server logs for details.";

    public const string ModelNotConfigured =
        "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.";

    public const string ToolChoiceModelUnsupported =
        "The selected model does not support the requested tool choice.";

    public const string ToolChoiceUnavailable =
        "The requested tool choice cannot be satisfied with the available tools.";
}
