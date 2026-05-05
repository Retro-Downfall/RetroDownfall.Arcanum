using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public sealed record PingRequest(
    string Prompt,
    string? Model = null,
    string WorkingDirectory = "",
    PatternSnapshot? ContextSnapshot = null,
    Guid? ConversationId = null,
    bool DisableMcpTools = false,
    bool CliTerminalFormatting = false,
    bool UnattendedMode = false);
