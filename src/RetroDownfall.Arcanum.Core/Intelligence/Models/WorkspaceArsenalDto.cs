namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record WorkspaceArsenalDto(
    List<string> ActiveSpells,
    List<string> NativeTools,
    List<McpServerStatusDto> McpServers,
    List<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary> Spells);
