using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<Guid?>> ReadToolRemoteTargetAsync(GuildChildToolAdmission admission, string destination, CancellationToken ct) =>
        authority.InQuestTransactionAsync<Guid?>(admission.Registry.Parent.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var allowed = await RequireLocalToolWithinAsync(connection, transaction, signets, admission.Registry,
                    admission.Tool, admission.Eligibility, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                if (admission.Kind is not (GuildLocalChildToolKind.CreateAgentTask or GuildLocalChildToolKind.AdmitCohortMember) || !admission.Registry.Owns(admission)
                    || policy is not GuildLocalHostPolicy host || !host.IsConfiguredRemoteDestination(destination))
                    return new Error(ErrorCodes.Guild.DestinationChanged, "The task requires its current configured destination.");

                GuildIssuanceIdentity identity = new(policy.InstallationGeneration, allowed.Value.Quest.Id, null,
                    admission.IssuingStepId, admission.LogicalCallId);

                Guid? existing = await FindIssueAsync(connection, transaction, identity, token).ConfigureAwait(false);

                if (existing is not { } id) return Result<Guid?>.Success(null);

                var child = await ReadTaskTargetWithinAsync(connection, transaction, admission, id, true, token).ConfigureAwait(false);

                if (child.IsFailure) return child.Error;

                var target = await GuildRemoteExecutionStore.ReadRecoveryWithinAsync(connection, transaction, id, token).ConfigureAwait(false);

                return target.IsFailure ? target.Error : Uri.TryCreate(destination.Trim(), UriKind.Absolute, out Uri? selected)
                    && selected.AbsoluteUri == target.Value.DiscoveryEndpoint ? Result<Guid?>.Success(child.Value.Quest.AgentId)
                    : new Error(ErrorCodes.Guild.DestinationChanged, "The destination differs from the accepted task.");
            }, ct);

    private async Task<Result> ValidateToolContinuationWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildQuestStored child,
        GuildChildToolAdmission admission, string intent, CancellationToken ct)
    {
        if (admission.Kind != GuildLocalChildToolKind.SendAgentMessage || !admission.Registry.Owns(admission)
            || !ReferenceEquals(admission.Registry.Parent.Driver.Caller, caller) || !admission.MatchesIntent(intent)) return InvalidReceipt;

        Result allowed = await AuthorizeTaskToolWithinAsync(connection, transaction, signets, admission,
            child.Quest.Id, true, ct).ConfigureAwait(false);

        if (allowed.IsFailure) return allowed;

        if (child.Quest.ParentQuestId != caller.Execution?.QuestId) return TaskNotFound;

        return Result.Success();
    }
}
