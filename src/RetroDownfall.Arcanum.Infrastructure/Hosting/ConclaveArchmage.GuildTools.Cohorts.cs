using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    private async Task<Result<GuildLocalChildToolResult>> DispatchCohortToolAsync(GuildLocalChildToolCall call, CancellationToken ct)
    {
        if (!SupportsCohorts) return UnavailableGuild();

        GuildChildToolAdmission admission = call.Admission;

        GuildCallerContext caller = admission.Registry.Parent.Driver.Caller!;

        if (call.Kind == GuildLocalChildToolKind.CreateCohort)
        {
            var request = GuildCohortToolArguments.Create(admission);

            if (request.IsFailure) return request.Error;

            var created = await cohorts!.CreateAsync(caller, request.Value, ct, admission).ConfigureAwait(false);

            return created.IsFailure ? created.Error : CohortToolResult(call.ParentQuest, admission);
        }

        JsonElement arguments = admission.Arguments;

        if (!GuildTaskArguments.Identity(arguments, "cohortId", out Guid cohortId)
            || !GuildCohortToolArguments.Revision(arguments, out long revision)) return GuildTaskArguments.Invalid;

        if (call.Kind == GuildLocalChildToolKind.AdmitCohortMember)
        {
            var parsed = GuildTaskArguments.Creation(call, "cohortId", "expectedRevision");

            if (parsed.IsFailure) return parsed.Error;

            bool remote = GuildTaskArguments.IsRemote(arguments);

            if (remote ? !SupportsRemote : !SupportsLocal) return UnavailableGuild();

            GuildIssueChildRequest request = parsed.Value;

            if (remote)
            {
                string destination = arguments.GetProperty("agent").GetString()!;

                var grant = await quests!.VerifyTaskGrantAsync(admission, request.RequestedGrant, ct).ConfigureAwait(false);

                if (grant.IsFailure) return grant.Error;

                var prior = await quests.ReadToolRemoteTargetAsync(admission, destination, ct).ConfigureAwait(false);

                if (prior.IsFailure) return prior.Error;

                Guid agentId;

                if (prior.Value is { } acceptedAgent) agentId = acceptedAgent;
                else
                {
                    var resolved = await remoteResolver!.ResolveAsync(caller, destination, ct, admission).ConfigureAwait(false);

                    if (resolved.IsFailure) return resolved.Error;

                    agentId = resolved.Value.AgentId;
                }

                request = request with { AgentId = agentId };
            }

            GuildIssuanceIdentity issuance = new(caller.InstallationGeneration, call.ParentQuest.Id, null,
                admission.IssuingStepId, admission.LogicalCallId);

            var child = await quests!.IssueCohortMemberAsync(caller, issuance, cohortId, revision, request, ct, admission).ConfigureAwait(false);

            if (child.IsFailure) return child.Error;

            guildScheduler!.Schedule(child.Value.Quest);

            return CohortToolResult(child.Value.Quest, admission);
        }

        GuildRevisionRequest change = new(admission.LogicalCallId, revision);

        if (call.Kind == GuildLocalChildToolKind.CloseCohort)
        {
            if (!GuildCohortToolArguments.MatchesRevision(admission, cohortId, null, change)) return GuildTaskArguments.Invalid;

            var closed = await cohorts!.CloseAsync(caller, cohortId, change, ct, admission).ConfigureAwait(false);

            return closed.IsFailure ? closed.Error : CohortToolResult(call.ParentQuest, admission);
        }

        if (call.Kind != GuildLocalChildToolKind.RemoveCohortMember
            || !GuildTaskArguments.Identity(arguments, "memberId", out Guid memberId)
            || !GuildCohortToolArguments.MatchesRevision(admission, cohortId, memberId, change)) return GuildTaskArguments.Invalid;

        var removed = await cohorts!.RemoveAsync(caller, cohortId, memberId, change, ct, admission).ConfigureAwait(false);

        return removed.IsFailure ? removed.Error : CohortToolResult(call.ParentQuest, admission);
    }

    private static Result<GuildLocalChildToolResult> CohortToolResult(GuildQuestDto quest, GuildChildToolAdmission admission) =>
        admission.ReadCommittedProjection() is { } projection
            ? new GuildLocalChildToolResult(quest, Projection: projection)
            : GuildLocalToolRegistry.Unavailable;
}
