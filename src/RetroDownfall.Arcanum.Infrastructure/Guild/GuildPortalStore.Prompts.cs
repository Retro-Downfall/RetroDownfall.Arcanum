using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    /// <summary>
    /// Materializes the exact admitted input for PingRequest.Prompt, before the provider's existing
    /// input guardrail pass. It neither scans twice nor acknowledges or dispatches any effect.
    /// A later provider/tool dispatch still requires its own live authority admission.
    /// </summary>
    internal Task<Result<string>> ReadPromptAsync(GuildPortalClaim claim, CancellationToken ct)
    {
        if (claim?.Driver?.Execution is null) return Task.FromResult(Result<string>.Failure(Forbidden));

        return authority.InQuestTransactionAsync<string>(claim.Driver.Execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            var admitted = await quests.RequireDriverWithinAsync(connection, transaction, signets, claim.Driver, true, false, token).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;

            Result exact = await RequireClaimWithinAsync(connection, transaction, claim, admitted.Value.Row, token).ConfigureAwait(false);

            if (exact.IsFailure) return exact.Error;

            Result<GuildCallerContext> caller = await DriverCallerWithinAsync(connection, transaction, claim.Driver, token).ConfigureAwait(false);

            if (caller.IsFailure) return caller.Error;

            Result<GuildEnvelope> found = await ReadEnvelopeWithinAsync(connection, transaction, claim.EnvelopeId, token).ConfigureAwait(false);

            if (found.IsFailure) return found.Error;

            GuildEnvelope envelope = found.Value;

            var prompt = await ReadInputPromptWithinAsync(connection, transaction, signets, caller.Value, envelope, token).ConfigureAwait(false);

            if (prompt.IsFailure) return prompt.Error;

            var current = await quests.RequireDriverWithinAsync(connection, transaction, signets, claim.Driver, true, false, token).ConfigureAwait(false);

            return current.IsFailure ? current.Error : prompt;
        }, ct);
    }
}
