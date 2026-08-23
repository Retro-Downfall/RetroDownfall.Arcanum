using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.TheForge;

/// <summary>
/// The one Campaign-resolution seam for inference, composed from three narrow readers.
/// </summary>
/// <remarks>
/// Reads first, decides second. Every fact the truth table needs is gathered before any of it is
/// interpreted, so the decision itself is a pure function that can be tested row by row without a
/// database (§10.12).
///
/// <para>Order matters in one place only: a supplied Session that does not exist fails immediately,
/// before any path work. A missing Session is never silently replaced with a new one, and doing the
/// filesystem walk first would mean an unknown Session ID could still cause Arcanum to open directories
/// a caller named.</para>
///
/// <para>The availability generation is read last, for the Campaign the table actually resolved. Reading
/// it earlier for a candidate that loses the conflict check would be wasted work at best, and at worst
/// would report a Campaign missing when the real answer was that two sources disagreed.</para>
/// </remarks>
internal sealed class CanonicalCampaignContextResolver(
    ISessionCampaignBindingReader bindings,
    ICampaignPathIdentityReader paths,
    ICampaignAvailabilityReader availability) : ICanonicalCampaignContextResolver
{

    public async ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
        CanonicalCampaignResolutionRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result<SessionCampaignBindingRecord?> session = await bindings
            .FindAsync(request.SessionId, cancellationToken)
            .ConfigureAwait(false);

        if (session.IsFailure)
        {
            return session.Error;
        }

        bool workingDirectorySupplied = !string.IsNullOrWhiteSpace(request.WorkingDirectory);

        Result<RegisteredCampaignIdentity?> workspace = await paths
            .ResolveMostSpecificAsync(request.WorkingDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (workspace.IsFailure)
        {
            return workspace.Error;
        }

        Guid? candidate = ResolveCandidate(session.Value?.Binding, request.ExplicitCampaignId, workspace.Value);

        long? generation = null;

        if (candidate is { } campaignId)
        {

            Result<long?> read = await availability
                .FindAvailabilityGenerationAsync(campaignId, cancellationToken)
                .ConfigureAwait(false);

            if (read.IsFailure)
            {
                return read.Error;
            }

            generation = read.Value;

        }

        return CanonicalCampaignResolutionPolicy.Resolve(
            session.Value?.Binding,
            request.ExplicitCampaignId,
            workspace.Value,
            workingDirectorySupplied,
            generation);

    }

    /// <summary>
    /// The Campaign the table will settle on when no source conflicts.
    /// </summary>
    /// <remarks>
    /// Only used to decide whose availability to read. The policy re-derives it and owns every conflict
    /// decision, so a disagreement between this and the policy can only ever cost one wasted read.
    /// </remarks>
    private static Guid? ResolveCandidate(
        SessionCampaignBinding? session,
        Guid? explicitCampaignId,
        RegisteredCampaignIdentity? workspace)
    {

        if (session is { Kind: SessionCampaignBindingKind.GlobalOnly or SessionCampaignBindingKind.LegacyUnresolved })
        {
            return null;
        }

        return session?.CampaignId ?? explicitCampaignId ?? workspace?.CampaignId;

    }

}
