using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Core.Tower;

public sealed record TestPromptRequest(
    string? WorkingDirectory,
    PatternSnapshot? ContextSnapshot,
    ChronosyncReport? ChronosyncDelta,
    string? CodexPath,
    List<AttachedFileDto>? AttachedFiles);
