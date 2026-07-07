using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

/// <summary>
/// Resolves and executes built-in <see cref="AIFunction"/> tools by name for the
/// <c>POST /api/tools/invoke</c> diagnostic endpoint.
/// </summary>
public interface IBuiltInToolRegistry
{

    /// <summary>
    /// Returns the names of all built-in tools currently available.
    /// </summary>
    IReadOnlyList<string> GetToolNames();

    /// <summary>
    /// Invokes a built-in tool by name with the provided JSON arguments.
    /// </summary>
    Task<Result<JsonElement>> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken);

}
