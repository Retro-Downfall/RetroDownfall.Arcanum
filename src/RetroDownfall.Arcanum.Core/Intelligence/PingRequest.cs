using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
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
    bool UnattendedMode = false,
    List<AttachedFileDto>? AttachedFiles = null,
    ChronosyncReport? ChronosyncDelta = null,
    List<CoreChatMessage>? StatelessMessages = null,
    string? OverrideSpellName = null);

