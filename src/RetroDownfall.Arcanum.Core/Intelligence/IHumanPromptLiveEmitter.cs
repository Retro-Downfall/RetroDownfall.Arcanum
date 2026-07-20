using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Per-turn live channel for human-in-the-loop frames (<c>ask_human</c> and MCP elicitation).
/// Wired via <see cref="HumanPromptLiveEmitterAmbient"/> for the duration of an attended streaming turn.
/// </summary>
public interface IHumanPromptLiveEmitter
{
    ValueTask EmitAsync(IntelligenceEvent evt, CancellationToken cancellationToken);
}
