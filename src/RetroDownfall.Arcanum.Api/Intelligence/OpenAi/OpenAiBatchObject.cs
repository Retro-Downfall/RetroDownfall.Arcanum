using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>Body for <c>POST /v1/batches</c>. See <c>docs/Arcanum.DESIGN.md</c> §11.21.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; endpoint tests cover wire parsing.
public sealed record OpenAiBatchRequest(
    [property: JsonPropertyName("input_file_id")] string? InputFileId,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("completion_window")] string? CompletionWindow = "24h");

/// <summary>OpenAI-shaped <c>batch</c> object. See <c>docs/Arcanum.DESIGN.md</c> §11.21.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
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
    [property: JsonPropertyName("object")] string ObjectKind = "batch")
{

    private const string WireIdPrefix = "batch_";

    public static string ToWireId(Guid id) => WireIdPrefix + id.ToString("N");

    public static OpenAiBatchObject FromRecord(BatchRecord record) => new(
        Id: ToWireId(record.Id),
        Endpoint: record.Endpoint,
        InputFileId: $"file-{record.InputFileId:N}",
        CompletionWindow: "24h",
        Status: record.Status,
        CreatedAt: record.CreatedAt.ToUnixTimeSeconds(),
        RequestCounts: new OpenAiBatchRequestCounts(

            record.TotalRequestCount,

            record.CompletedRequestCount,

            record.FailedRequestCount),
        OutputFileId: record.OutputFileId is { } outputId ? $"file-{outputId:N}" : null,
        ErrorFileId: record.ErrorFileId is { } errorId ? $"file-{errorId:N}" : null,
        CompletedAt: record.CompletedAt?.ToUnixTimeSeconds());

}

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiBatchRequestCounts(
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("completed")] long Completed,
    [property: JsonPropertyName("failed")] long Failed)
{

    public static readonly OpenAiBatchRequestCounts Empty = new(0, 0, 0);

}

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiBatchListResponse(
    [property: JsonPropertyName("data")] List<OpenAiBatchObject> Data,
    [property: JsonPropertyName("has_more")] bool HasMore = false,
    [property: JsonPropertyName("next_cursor")] string? NextCursor = null,
    [property: JsonPropertyName("object")] string ObjectKind = "list");
