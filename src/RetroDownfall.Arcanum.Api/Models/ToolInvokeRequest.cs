using System.Text.Json;

namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Request body for <c>POST /api/tools/invoke</c> — directly executes a built-in tool by name.
/// </summary>
/// <remarks>
/// Member names come from the camelCase policy on <c>ArcanumJsonContext</c>; the defaults are what a member the
/// body omits takes, so a body of <c>{}</c> binds to an empty tool name rather than failing in the binder.
/// </remarks>
public sealed record ToolInvokeRequest(string ToolName = "", JsonElement Arguments = default);
