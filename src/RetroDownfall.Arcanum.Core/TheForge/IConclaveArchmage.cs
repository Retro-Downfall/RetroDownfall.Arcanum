using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.TheForge;

/// <summary>
/// Request to cast a Sending - create a child Apprentice within The Conclave. When
/// <see cref="ParentApprenticeId"/> is set the lineage is persisted into the child's checkpoint.
/// </summary>
public sealed record ConclaveCastRequest(
    string Goal,
    string? Name = null,
    string WorkspacePath = "",
    Guid? CampaignId = null,
    Guid? ParentApprenticeId = null);

/// <summary>
/// The <strong>Conclave Archmage</strong> mints child Apprentices for cross-Apprentice delegation.
/// It is the single source of delegation/domain logic shared by the in-process <c>cast_sending</c>
/// MCP tool and the <c>POST /api/apprentices/{id}/cast</c> endpoint, and enforces the
/// <c>Arcanum:Conclave:Enabled</c> gate.
/// </summary>
public interface IConclaveArchmage
{

    Task<Result<Apprentice>> CastAsync(ConclaveCastRequest request, CancellationToken cancellationToken = default);

}
