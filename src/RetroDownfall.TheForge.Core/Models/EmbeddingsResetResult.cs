namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Infrastructure.Weave.EmbeddingsResetResult</c> (body of
/// <c>POST /api/embeddings/reset</c>). Kept in TheForge.Core to avoid referencing the ASP.NET/EF-heavy
/// Infrastructure project. <c>DeletedRowCounts</c> maps table name → row count deleted by the reset.
/// camelCase wire via <c>TheForgeJsonContext</c>.
/// </summary>
public sealed record EmbeddingsResetResult(Dictionary<string, int> DeletedRowCounts);
