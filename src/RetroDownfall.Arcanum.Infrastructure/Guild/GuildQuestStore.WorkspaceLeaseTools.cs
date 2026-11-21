using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceLeaseToolReceipt(Guid Id, Guid WorkspaceId, GuildWorkspaceLeaseOwnerKind OwnerKind,
    Guid QuestId, GuildWorkspaceLeaseState State, long Epoch, long Revision, DateTimeOffset ExpiresAt, GuildMutationReceiptDto Receipt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildWorkspaceLeaseToolReceipt))]
internal sealed partial class GuildWorkspaceLeaseToolJsonContext : JsonSerializerContext;

internal sealed partial class GuildQuestStore
{
    internal bool HasWorkspaceLeaseBroker => readiness?.GetWorkspaceLeaseService() is { IsSuccess: true, Value: GuildWorkspaceLeaseBroker };

    internal async Task<Result<GuildLocalChildToolResult>> ExecuteWorkspaceLeaseToolAsync(GuildLocalChildToolCall call,
        CancellationToken ct)
    {
        GuildChildToolAdmission admission = call.Admission;

        GuildLocalInferenceExecution execution = admission.Registry.Parent;

        if (!ReferenceEquals(execution.Store, this) || !admission.Registry.Owns(admission)
            || !ReferenceEquals(admission.Registry.CaptureInvocation(admission.Tool), admission.Frame)
            || execution.Driver.Caller is not { Kind: GuildCallerKind.Run } caller
            || !ReferenceEquals(caller.Execution, execution.Driver.Execution) || call.ParentQuest.WorkspaceId is not { } workspaceId
            || call.ParentQuest.Id != execution.Driver.Execution.QuestId || readiness is null) return InvalidReceipt;

        Result<GuildWorkspaceLeaseToolRequest> request = GuildWorkspaceLeaseToolRequest.Parse(admission, workspaceId);

        if (request.IsFailure) return request.Error;

        Result<IGuildWorkspaceLeaseService> service = readiness.GetWorkspaceLeaseService();

        if (service.IsFailure) return service.Error;

        if (service.Value is not GuildWorkspaceLeaseBroker broker) return Unavailable;

        JsonElement? projection = null;

        Result<WorkspaceLeaseDto> changed = await broker.ExecuteToolAsync(caller, request.Value, new(Check, Project), ct).ConfigureAwait(false);

        if (changed.IsFailure) return changed.Error;

        if (projection is not { } result) return InvalidReceipt;

        admission.RecordCommittedProjection(call.ParentQuest, result);

        return new GuildLocalChildToolResult(call.ParentQuest, Projection: result);

        // Serialize the model's bounded observation in the same transaction as the lease change.
        // An allocation refusal rolls back a newly written lease and its idempotency receipt together.
        Result Project(GuildWorkspaceLeaseMutationReceipt receipt, bool replayed)
        {
            GuildWorkspaceLeaseRecord lease = receipt.Result;

            if (lease.Owner.Kind != GuildWorkspaceLeaseOwnerKind.Quest || lease.Owner.QuestId != call.ParentQuest.Id
                || lease.Owner.RootQuestId != call.ParentQuest.RootQuestId || lease.Owner.WorkspaceId != workspaceId
                || receipt.Action != request.Value.Action || receipt.IdempotencyKey != request.Value.Mutation.IdempotencyKey)
                return InvalidReceipt;

            GuildWorkspaceLeaseToolReceipt observation = new(lease.Id, workspaceId, lease.Owner.Kind, call.ParentQuest.Id,
                lease.State, lease.Epoch, lease.Revision, lease.ExpiresAt,
                new(receipt.Id, receipt.IdempotencyKey, replayed, receipt.AcceptedAt, lease.Revision));

            JsonElement value = JsonSerializer.SerializeToElement(observation, GuildWorkspaceLeaseToolJsonContext.Default.GuildWorkspaceLeaseToolReceipt);

            Result valid = admission.ValidateProjectionAllocation(value);

            if (valid.IsSuccess) projection = value;

            return valid;
        }

        async Task<Result> Check(SqliteConnection connection, SqliteTransaction transaction,
            GuildWorkspaceLeaseBinding binding, CancellationToken token)
        {
            if (binding.Owner.WorkspaceId != workspaceId || binding.Owner.QuestId != call.ParentQuest.Id
                || !ReferenceEquals(binding.Authority.Execution, execution.Driver.Execution)
                || binding.Authority.Action != request.Value.Action
                || binding.Authority.EffectiveGrant is not { } grant || !grant.Tools.Contains(admission.Tool)
                || !grant.Workspaces.Any(scope => scope.WorkspaceId == workspaceId && scope.Access == GuildWorkspaceAccess.ReadWrite)
                || policy is not GuildLocalHostPolicy { IsExecutionEnabled: true } || RequireLocalExecutionReady().IsFailure
                || !admission.Registry.Owns(admission) || !ReferenceEquals(admission.Registry.CaptureInvocation(admission.Tool), admission.Frame))
                return ClosedDispatch;

            var active = await RequireDriverBindingWithinAsync(connection, transaction, execution.Driver, true, false, token).ConfigureAwait(false);

            if (active.IsFailure) return active.Error;

            if (active.Value.Row.Quest.State == QuestState.InputRequired) return ClosedDispatch;

            var current = await ReadDispatchWithinAsync(connection, transaction, execution.Dispatch.DispatchId, token).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            return current.Value is { } dispatch && SameProviderDispatch(dispatch, execution.Dispatch)
                && dispatch.AccountingAssociationId == execution.Driver.Execution.RunId
                && dispatch.Evidence != GuildDispatchEvidence.ResolvedNoDispatch ? Result.Success() : ClosedDispatch;
        }
    }
}
