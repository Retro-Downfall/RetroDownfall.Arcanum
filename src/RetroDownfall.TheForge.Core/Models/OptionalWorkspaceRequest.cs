namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Serialization.OptionalWorkspaceRequest</c>
/// (optional workspace body for <c>POST /api/mcp/reload</c> and <c>POST /api/intelligence/arsenal</c>).
/// Kept in TheForge.Core to avoid referencing the ASP.NET-heavy Api project. camelCase wire via
/// <c>TheForgeJsonContext</c>.
/// </summary>
public sealed record OptionalWorkspaceRequest(string? WorkingDirectory = null);
