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

    [JsonIgnore]
    public Session? Session { get; set; }

}
