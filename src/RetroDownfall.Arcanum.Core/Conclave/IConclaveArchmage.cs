using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Conclave;

/// <summary>
/// Request to cast a Sending - create a child Apprentice within The Conclave. When
/// <see cref="ParentApprenticeId"/> is set the lineage is persisted into the child's checkpoint.
/// </summary>
/// <param name="DelegationChain">
/// A2A delegation hops that led to this cast, oldest first (see
/// <c>ConclaveDelegationChain</c>). Empty for purely local delegation. Carried onto the child so its own
/// outbound Sendings stay loop-aware instead of restarting the chain from empty.
/// </param>
public sealed record ConclaveCastRequest(
    string Goal,
    string? Name = null,
    string WorkspacePath = "",
    Guid? CampaignId = null,
    Guid? ParentApprenticeId = null,
    IReadOnlyList<string>? DelegationChain = null);

/// <summary>
/// The <strong>Conclave Archmage</strong> mints child Apprentices for cross-Apprentice delegation.
/// It is the single source of delegation/domain logic shared by the in-process <c>cast_sending</c>
/// MCP tool and the <c>POST /api/apprentices/{id}/cast</c> endpoint, and enforces the
/// <c>Arcanum:Features:Conclave</c> gate.
/// </summary>
public interface IConclaveArchmage
{

    Task<Result<Apprentice>> CastAsync(ConclaveCastRequest request, CancellationToken cancellationToken = default);

}
