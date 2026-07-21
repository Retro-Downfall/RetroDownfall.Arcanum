using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>
/// Per-provider-candidate state. Messages/options are attempt-local clones so a failed
/// pre-commit attempt cannot contaminate the next candidate (ADR 0004).
/// </summary>
internal sealed class ProviderAttemptContext
{

    public required int AttemptIndex { get; init; }

    public required string? ProviderName { get; init; }

    public required string? Model { get; init; }

    public required ChatClientLease Lease { get; init; }

    public int ContextWindowLimit { get; init; }

    public List<ChatMessage> Messages { get; init; } = [];

    public ChatOptions? Options { get; set; }

    public ProviderAttemptState State { get; set; } = ProviderAttemptState.Pending;

    public bool Committed { get; set; }

    public bool NoToolsRetryUsed { get; set; }

    public bool UsesTools { get; set; } = true;

}
