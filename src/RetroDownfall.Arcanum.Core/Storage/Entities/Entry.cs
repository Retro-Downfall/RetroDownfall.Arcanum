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

    /// <summary>
    /// Strictly increasing per-session append position, and the authoritative chronological order
    /// within a session. <see cref="CreatedAt"/> alone is not sufficient: a turn writes its prompt
    /// and answer — and a tool call with its result — under one identical timestamp, and
    /// <see cref="Id"/> is a random Guid that cannot break that tie in append order. Allocated by
    /// the repositories under the per-session write lock; gaps are permitted, reuse is not.
    /// </summary>
    public long Sequence { get; set; }

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
