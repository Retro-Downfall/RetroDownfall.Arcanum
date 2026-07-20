using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.TheForge.Core.Models.OpenAi;

/// <summary>
/// Body for <c>POST /v1/batches</c>. Mirrored from Arcanum Api —
/// The Forge must not reference <c>RetroDownfall.Arcanum.Api</c>.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiBatchRequest(
    [property: JsonPropertyName("input_file_id")] string? InputFileId,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("completion_window")] string? CompletionWindow = "24h");

/// <summary>OpenAI-shaped <c>batch</c> object for <c>/v1/batches</c>.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiBatchObject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("input_file_id")] string InputFileId,
    [property: JsonPropertyName("completion_window")] string CompletionWindow,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("request_counts")] OpenAiBatchRequestCounts RequestCounts,
    [property: JsonPropertyName("output_file_id")] string? OutputFileId = null,
    [property: JsonPropertyName("error_file_id")] string? ErrorFileId = null,
    [property: JsonPropertyName("completed_at")] long? CompletedAt = null,
    [property: JsonPropertyName("object")] string ObjectKind = "batch");

/// <summary>Per-batch request counts from <c>GET /v1/batches/{id}</c>.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiBatchRequestCounts(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("failed")] int Failed);

/// <summary>List envelope for <c>GET /v1/batches</c>.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiBatchListResponse(
    [property: JsonPropertyName("data")] List<OpenAiBatchObject> Data,
    [property: JsonPropertyName("has_more")] bool HasMore = false,
    [property: JsonPropertyName("object")] string ObjectKind = "list");
