using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    internal async Task<Result<GuildQuestDto>> IssueInboundAsync(GuildCallerContext caller,
        GuildIssueRootRequest request, GuildInboundIssue inbound, CancellationToken ct)
    {
        Result<string> intent = GuildQuestIntent.Compute(request);

        if (intent.IsFailure) return intent.Error;

        if (caller.Kind != GuildCallerKind.Peer || !CanIssueGuild()) return UnavailableGuild();

        GuildIssuanceIdentity issuance = new(caller.InstallationGeneration, null, caller.InvocationId,
            caller.InvocationId, request.IdempotencyKey);

        Result<GuildQuestStored> accepted = await quests!.IssueInboundAsync(caller, issuance, request, inbound, ct).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        guildScheduler!.Schedule(accepted.Value.Quest);

        return accepted.Value.Quest;
    }
}
