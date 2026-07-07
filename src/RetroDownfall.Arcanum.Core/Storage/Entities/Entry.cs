using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Storage.Entities;

public sealed class Entry
{

    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public MessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public string ModelUsed { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public string? ToolArguments { get; set; }

    /// <summary>
    /// Pinned entries are always included in the inference context even when context compression
    /// would otherwise drop older entries. Controlled by <c>POST /api/sessions/{id}/entries/{entryId}/pin</c>
    /// and the corresponding DELETE.
    /// </summary>
    public bool IsPinned { get; set; }

    [JsonIgnore]
    public Session? Session { get; set; }

}
