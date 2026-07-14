using System.Text.Json;

namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.ToolInvokeResponse</c> (result of
/// <c>POST /api/tools/invoke</c> — raw tool output serialized as JSON). Kept in TheForge.Core to
/// avoid referencing the ASP.NET-heavy Api project. camelCase wire via <c>TheForgeJsonContext</c>
/// (<c>result</c>).
/// </summary>
public sealed record ToolInvokeResponse(JsonElement Result);
