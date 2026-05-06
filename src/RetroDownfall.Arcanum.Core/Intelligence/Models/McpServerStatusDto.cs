namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record McpServerStatusDto(
    string ServerName,
    string Status,
    int ToolCount,
    List<string> ProvidedTools,
    string? ErrorMessage);
