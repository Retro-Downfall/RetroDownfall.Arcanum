using System.Text.Json.Serialization.Metadata;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Materializes tool output for model consumption: token-aware truncate, optional prefix/suffix,
/// truncated marker. Full artifacts for operators are optional and never auto-retrieved by the model.
/// </summary>
public interface IToolResultMaterializer
{

    ToolResultMaterialization Materialize(string toolName, string rawText, ToolResultMaterializerOptions? options = null);

    /// <summary>
    /// Reduces a structured envelope under the materializer's own budget.
    /// </summary>
    /// <remarks>
    /// No budget argument, unlike <see cref="Materialize"/>. Its one production caller never varied
    /// the budget, and every behaviour this path has — trimming leading items, updating the omission
    /// counters, falling back to a minimal valid envelope — is observable at the budget a shipped turn
    /// applies simply by handing it a larger envelope. A parameter that only ever let a suite reach
    /// those behaviours with a smaller payload was buying convenience with representativeness.
    /// </remarks>
    ToolResultMaterialization MaterializeStructured<T>(
        string toolName,
        T result,
        JsonTypeInfo<T> jsonTypeInfo)
        where T : IStructuredToolResult<T>;

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
