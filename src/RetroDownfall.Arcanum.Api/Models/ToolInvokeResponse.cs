using System.Text.Json;

namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Result body for <c>POST /api/tools/invoke</c> — the raw tool output serialized as JSON.
/// </summary>
public sealed record ToolInvokeResponse(JsonElement Result = default);
