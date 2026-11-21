using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceMutationAdmission(Guid WorkspaceId, Guid SessionId, CanonicalCampaignContext Campaign,
    GuildGrant Grant, GuildVerifiedAuthority Authority, GuildWorkspaceAuthority Physical, GuildWorkspaceMutationFence Fence);

internal sealed partial class GuildQuestStore
{
    internal bool HasWorkspaceMutationFence => readiness?.GetWorkspaceMutationFence().IsSuccess == true;

    internal Task<Result<GuildWorkspaceMutationAdmission>> AdmitWorkspaceMutationToolAsync(GuildChildToolAdmission call, CancellationToken ct)
    {
        if (!OwnsWorkspaceMutationCall(call) || policy is not GuildLocalHostPolicy host)
            return Task.FromResult(Result<GuildWorkspaceMutationAdmission>.Failure(InvalidReceipt));

        GuildExecutionHandle execution = call.Registry.Parent.Driver.Execution;

        return authority.InQuestTransactionAsync<GuildWorkspaceMutationAdmission>(execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireLocalToolGrantWithinAsync(connection, transaction, signets,
                    call.Registry, call.Tool, call.Eligibility, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (!OwnsWorkspaceMutationCall(call) || admitted.Value.Quest.Quest is not
                    { WorkspaceId: Guid workspaceId, SessionId: Guid sessionId } quest || quest.CampaignId != workspaceId)
                    return InvalidReceipt;

                var fence = readiness!.GetWorkspaceMutationFence();

                if (fence.IsFailure) return fence.Error;

                var physical = await host.ResolveWorkspaceAuthorityWithinAsync(connection, transaction, workspaceId, token).ConfigureAwait(false);

                if (physical.IsFailure) return physical.Error;

                await using var command = Command(connection, transaction, "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey=1");

                if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not long epoch || epoch <= 0) return WorkspaceMutationNotGranted;

                GuildWorkspaceRegistration registration = physical.Value.Registration;

                CanonicalCampaignContext campaign = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(workspaceId),
                    epoch, registration.PolicyVersion, registration.Revision, registration.RootIdentity);

                GuildGrant grant = admitted.Value.Grant with { Workspaces = [.. admitted.Value.Grant.Workspaces.Where(scope =>
                    scope.WorkspaceId == workspaceId && scope.Access == GuildWorkspaceAccess.ReadWrite)] };

                GuildVerifiedAuthority proof = new(execution, GuildAuthorityAction.WorkspaceMutate,
                    workspaceId, quest.SignetRevision, grant, call.Tool);

                return new GuildWorkspaceMutationAdmission(workspaceId, sessionId, campaign, grant, proof, physical.Value, fence.Value);
            }, ct);
    }

    internal async Task<Result> RevalidateWorkspaceMutationToolAsync(GuildChildToolAdmission call, CancellationToken ct)
    {
        if (!OwnsWorkspaceMutationCall(call)) return InvalidReceipt;

        var allowed = await AuthorizeLocalToolAsync(call.Registry, call.Tool, call.Eligibility, ct).ConfigureAwait(false);

        return allowed.IsFailure ? allowed.Error : OwnsWorkspaceMutationCall(call) ? Result.Success() : InvalidReceipt;
    }

    private bool OwnsWorkspaceMutationCall(GuildChildToolAdmission call) => call is not null
        && ReferenceEquals(call.Registry.Parent.Store, this) && GuildLocalToolRegistry.IsWorkspaceMutationTool(call.Tool.Name)
        && call.Registry.Owns(call) && ReferenceEquals(call.Registry.CaptureInvocation(call.Tool), call.Frame);

    private static Error WorkspaceMutationNotGranted => new(ErrorCodes.Guild.Forbidden, "The run has no current write authority for this workspace.");
}
