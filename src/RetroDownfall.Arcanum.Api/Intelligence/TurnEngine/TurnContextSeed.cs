using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>
/// Immutable-per-run context assembled once before provider attempts (ADR 0004).
/// Attempt-local clones live on <see cref="ProviderAttemptContext"/>.
/// </summary>
internal sealed class TurnContextSeed
{

    public required PingRequest Request { get; init; }

    public Guid? SessionId { get; init; }

    public Campaign? Campaign { get; init; }

    public string? CampaignId { get; init; }

    public string? WorkspaceRoot { get; init; }

    public bool CampaignRequiresWard { get; init; }

    public bool SanctumEnabled { get; init; }

    public IReadOnlyList<AITool> BaseInferenceTools { get; init; } = [];

    public IReadOnlyList<string> SpellScriptRoots { get; init; } = [];

    public ParsedSpell? ActiveSpell { get; init; }

    public string? CodexContent { get; init; }

    public string? SystemPrompt { get; init; }

    public IReadOnlyList<ChatMessage> BaseMessages { get; init; } = [];

    public bool HumanInteractionAvailable { get; init; }

    public bool HasIdempotencyKey { get; init; }

    public TurnPurpose Purpose { get; init; }

    public TurnResponseMode ResponseMode { get; init; }

}
