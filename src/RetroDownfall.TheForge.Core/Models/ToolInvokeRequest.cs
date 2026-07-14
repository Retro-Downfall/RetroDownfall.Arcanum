using System.Text.Json;

namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.ToolInvokeRequest</c> (body of
/// <c>POST /api/tools/invoke</c> — invoke a built-in tool by name with untyped JSON arguments).
/// Kept in TheForge.Core to avoid referencing the ASP.NET-heavy Api project. camelCase wire via
/// <c>TheForgeJsonContext</c> (<c>toolName</c>, <c>arguments</c>).
/// </summary>
public sealed record ToolInvokeRequest(string ToolName, JsonElement Arguments);
