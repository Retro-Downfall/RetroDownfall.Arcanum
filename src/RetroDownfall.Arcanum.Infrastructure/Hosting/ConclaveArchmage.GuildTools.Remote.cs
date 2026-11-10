using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    public bool SupportsRemote => remoteResolver is not null && settings.CurrentValue.Features.Guild
        && settings.CurrentValue.ResolveConclave().Enabled && settings.CurrentValue.ResolveA2A().ClientEnabled;

    private async Task<Result<GuildLocalChildToolResult>> DispatchRemoteToolAsync(GuildLocalChildToolCall call, CancellationToken ct)
    {
        if (!SupportsRemote) return UnavailableGuild();

        var parsed = GuildTaskArguments.Creation(call);

        if (parsed.IsFailure) return parsed.Error;

        string destination = call.Arguments.GetProperty("agent").GetString()!;

        GuildChildToolAdmission admission = call.Admission;

        GuildCallerContext caller = admission.Registry.Parent.Driver.Caller!;

        var grant = await quests!.VerifyTaskGrantAsync(admission, parsed.Value.RequestedGrant, ct).ConfigureAwait(false);

        if (grant.IsFailure) return grant.Error;

        var prior = await quests!.ReadToolRemoteTargetAsync(admission, destination, ct).ConfigureAwait(false);

        if (prior.IsFailure) return prior.Error;

        Guid agentId;

        if (prior.Value is { } acceptedAgent) agentId = acceptedAgent;
        else
        {
            var resolved = await remoteResolver!.ResolveAsync(caller, destination, ct).ConfigureAwait(false);

            if (resolved.IsFailure) return resolved.Error;

            agentId = resolved.Value.AgentId;
        }

        GuildIssueChildRequest request = parsed.Value with { AgentId = agentId };

        GuildIssuanceIdentity issuance = new(caller.InstallationGeneration, call.ParentQuest.Id, null,
            admission.IssuingStepId, admission.LogicalCallId);

        var accepted = await quests.IssueToolChildAsync(admission, issuance, request, ct).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        guildScheduler!.Schedule(accepted.Value.Quest);

        return new GuildLocalChildToolResult(accepted.Value.Quest,
            Projection: JsonSerializer.SerializeToElement(accepted.Value.Quest, GuildJsonContext.Default.GuildQuestDto));
    }
}
