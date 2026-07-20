using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.TheForge.Core.Models.OpenAi;

/// <summary>
/// OpenAI-shaped <c>file</c> object for <c>/v1/files</c>. Mirrored from Arcanum Api —
/// The Forge must not reference <c>RetroDownfall.Arcanum.Api</c>.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiFileObject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("object")] string ObjectKind = "file");

/// <summary>List envelope for <c>GET /v1/files</c>.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiFileListResponse(
    [property: JsonPropertyName("data")] List<OpenAiFileObject> Data,
    [property: JsonPropertyName("object")] string ObjectKind = "list");

/// <summary>Delete envelope for <c>DELETE /v1/files/{id}</c>.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiFileDeleteResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("object")] string ObjectKind = "file");
