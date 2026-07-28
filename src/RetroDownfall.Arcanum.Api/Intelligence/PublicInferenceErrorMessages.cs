namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Exact public inference-failure copy by transport surface.
/// Native and OpenAI messages intentionally differ and must not be merged.
/// </summary>
internal static class PublicInferenceErrorMessages
{
    public const string NativeGenericFailure =
        "Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.";

    public const string OpenAiGenericFailure =
        "Inference failed. See server logs for details.";

    public const string ModelNotConfigured =
        "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.";

    public const string NativeTimeout =
        "Inference timed out. Increase Arcanum:Intelligence:InferenceTimeoutSeconds or retry with a shorter prompt.";

    public const string OpenAiTimeout =
        "Inference timed out.";
}
