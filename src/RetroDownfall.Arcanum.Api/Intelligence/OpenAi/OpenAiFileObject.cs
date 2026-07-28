using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>OpenAI-shaped <c>file</c> object for <c>/v1/files</c>. See <c>docs/Arcanum.DESIGN.md</c> §11.20.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiFileObject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("object")] string ObjectKind = "file")
{

    public static OpenAiFileObject FromRecord(UploadedFileRecord record) => new(
        Id: $"file-{record.Id:N}",
        Bytes: record.Bytes,
        CreatedAt: record.CreatedAt.ToUnixTimeSeconds(),
        Filename: record.Filename,
        Purpose: record.Purpose);

}

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiFileListResponse(
    [property: JsonPropertyName("data")] List<OpenAiFileObject> Data,
    [property: JsonPropertyName("object")] string ObjectKind = "list");

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiFileDeleteResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("object")] string ObjectKind = "file");
