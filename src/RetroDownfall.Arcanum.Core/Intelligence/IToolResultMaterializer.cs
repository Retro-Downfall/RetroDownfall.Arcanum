namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Materializes tool output for model consumption: token-aware truncate, optional prefix/suffix,
/// truncated marker. Full artifacts for operators are optional and never auto-retrieved by the model.
/// </summary>
public interface IToolResultMaterializer
{

    ToolResultMaterialization Materialize(string toolName, string rawText, ToolResultMaterializerOptions? options = null);

}

public sealed record ToolResultMaterializerOptions(
    int? MaxTokens = null,
    int? MaxUtf8Bytes = null,
    bool PreservePrefixAndSuffix = true);

public sealed record ToolResultMaterialization(
    string TextForModel,
    bool WasTruncated,
    int OriginalCharLength,
    int OriginalEstimatedTokens);
