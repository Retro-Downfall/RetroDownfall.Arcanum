using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildQuestStored>> IssueToolChildAsync(GuildChildToolAdmission admission,
        GuildIssuanceIdentity issuance, GuildIssueChildRequest request, CancellationToken ct)
    {
        if (admission is null || issuance is null || request is null || !admission.Registry.Owns(admission)
            || !ReferenceEquals(admission.Registry.Parent.Store, this) || issuance.IssuingStepId != admission.IssuingStepId
            || issuance.LogicalCallId != admission.LogicalCallId || request.IdempotencyKey != admission.LogicalCallId
            || issuance.RootInvocationId is not null || issuance.InstallationGeneration != policy.InstallationGeneration
            || issuance.IssuerQuestId != admission.Registry.Parent.Driver.Execution.QuestId) return InvalidReceipt;

        GuildQuestDriver driver = admission.Registry.Parent.Driver;

        if (driver.Caller is null) return InvalidReceipt;

        Result<string> digest = ToolIssueIntent(driver.Execution.QuestId, request);

        if (digest.IsFailure) return digest.Error;

        if (!admission.BindIntent(digest.Value)) return Conflict;

        Result<GuildQuestStored> accepted = await IssueAsync(driver.Caller, issuance, null, driver.Execution.QuestId,
            request, ct, admission).ConfigureAwait(false);

        // Receipt publication follows the durable commit, just like Signet credentials and native identity.
        if (accepted.IsSuccess) admission.RecordCommitted(accepted.Value.Quest);

        return accepted;
    }

    // A host tool's stable call identity binds its arguments. The current parent revision is a
    // first-admission CAS, not part of that logical call: result application advances it on replay.
    private static Result<string> ToolIssueIntent(Guid parent, GuildIssueChildRequest request) =>
        GuildQuestIntent.Compute(parent, request with { ExpectedRevision = 1 });

    private async Task<Result<GuildIssueChildRequest>> ResolveToolBudgetWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored parent, GuildIssueChildRequest request, CancellationToken ct)
    {
        if (allocations is not GuildBudgetLedger ledger) return Unavailable;

        Result<GuildBudgetSnapshot> available = await ledger.ReadWithinAsync(connection, transaction,
            parent.AllocationAccountId, ct).ConfigureAwait(false);

        if (available.IsFailure) return available.Error;

        GuildRequestedGrant requested = request.RequestedGrant ?? new();

        GuildBudgetGrant? budget = requested.Budget.IsSpecified ? requested.Budget.Value : null;

        Result<GuildBudgetGrant> resolved = GuildBudgetAllocationPolicy.ResolveHint(
            budget is null ? null : new(budget.Usd, budget.Tokens), available.Value.Remaining, parent.Quest.ResolvedGrant.Budget);

        return resolved.IsFailure ? resolved.Error : request with
        {
            RequestedGrant = requested with { Budget = new(true, resolved.Value) },
        };
    }

    internal bool OwnsToolAuthority(IGuildToolAuthority candidate) => ReferenceEquals(authority, candidate);

    internal GuildToolInvocationContext CreateToolContext(GuildLocalInferenceExecution execution, GuildLocalToolRegistry registry,
        Func<GuildToolGrant, bool> currentEligibility) =>
        new(execution.Driver.Execution, authority, currentEligibility, registry);

    internal Task<Result<GuildQuestStored>> AuthorizeLocalToolAsync(GuildLocalToolRegistry registry, GuildToolGrant tool,
        Func<GuildToolGrant, bool> eligibility, CancellationToken ct)
    {
        if (registry is null || !ReferenceEquals(registry.Parent.Store, this) || !DriverShape(registry.Parent.Driver))
            return Task.FromResult(Result<GuildQuestStored>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildQuestStored>(registry.Parent.Driver.Execution.RootQuestId,
            (connection, transaction, signets, token) => RequireLocalToolWithinAsync(connection, transaction, signets,
                registry, tool, eligibility, token), ct);
    }

    private async Task<Result<GuildQuestStored>> RequireLocalToolWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildLocalToolRegistry registry, GuildToolGrant tool,
        Func<GuildToolGrant, bool> eligibility, CancellationToken ct)
    {
        if (policy is not GuildLocalHostPolicy { IsExecutionEnabled: true } || allocations is not GuildBudgetLedger
            || RequireLocalExecutionReady().IsFailure || !ReferenceEquals(registry.Parent.Store, this)
            || !registry.Supports(tool) || !eligibility(tool)) return GuildLocalToolRegistry.Unavailable;

        GuildLocalInferenceExecution execution = registry.Parent;

        var admitted = await RequireDriverWithinAsync(connection, transaction, signets, execution.Driver, true, false, ct).ConfigureAwait(false);

        if (admitted.IsFailure) return admitted.Error;

        if (admitted.Value.Row.Quest.State == QuestState.InputRequired) return ClosedDispatch;

        Result<GuildGrant> live = await signets.VerifyAsync(execution.Driver.Execution, ct).ConfigureAwait(false);

        if (live.IsFailure) return live.Error;

        bool creation = tool.Name is "delegate_task" or "create_agent_task";

        bool remoteTool = tool.Name == "create_agent_task" && GuildTaskArguments.IsRemote(registry.CaptureInvocation(tool)?.Admission?.Arguments);

        JsonElement? arguments = registry.CaptureInvocation(tool)?.Admission?.Arguments;

        bool mayCreate = tool.Name == "delegate_task" ? live.Value.Delegation.Contains(GuildDelegationKind.Local)
            : !creation || (arguments is not null
                ? live.Value.Delegation.Contains(remoteTool ? GuildDelegationKind.Remote : GuildDelegationKind.Local)
                : registry.ChildDispatcher?.SupportsLocal == true && live.Value.Delegation.Contains(GuildDelegationKind.Local)
                    || registry.ChildDispatcher?.SupportsRemote == true && live.Value.Delegation.Contains(GuildDelegationKind.Remote));

        if (!live.Value.Tools.Contains(tool) || !mayCreate)
            return new Error(ErrorCodes.Guild.ToolNotGranted, "The exact local tool and delegation rights are not granted.");

        if (tool.Name == "create_agent_task" && arguments is not null
            && (remoteTool ? registry.ChildDispatcher?.SupportsRemote != true : registry.ChildDispatcher?.SupportsLocal != true))
            return new Error(ErrorCodes.Guild.Unavailable, "The selected task destination capability is unavailable.");

        if (remoteTool && (live.Value.Budget.Usd.Mode != GuildBudgetMode.Unlimited
            || live.Value.Budget.Tokens.Mode != GuildBudgetMode.Unlimited
            || allocations is GuildBudgetLedger { RequiresDailyReservation: true }))
            return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "General A2A cannot enforce the active finite spending authority.");

        var current = await ReadDispatchWithinAsync(connection, transaction, execution.Dispatch.DispatchId, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        if (current.Value is not { } dispatch || !SameProviderDispatch(dispatch, execution.Dispatch)
            || dispatch.QuestId != execution.Driver.Execution.QuestId || dispatch.AccountingAssociationId != execution.Driver.Execution.RunId
            || dispatch.Evidence == GuildDispatchEvidence.ResolvedNoDispatch) return Conflict;

        var input = await MaterializeProviderInputWithinAsync(connection, transaction, admitted.Value.Row,
            registry.AcceptedInput, dispatch, ct).ConfigureAwait(false);

        if (input.IsFailure) return input.Error;

        if (dispatch.LocalChildId != input.Value.Binding.ApprenticeId) return Conflict;

        if (registry.AcceptedInput.Mode != GuildLocalExecutionMode.Apprentice || !registry.IsActive
            || !eligibility(tool) || policy is not GuildLocalHostPolicy { IsExecutionEnabled: true }
            || RequireLocalExecutionReady().IsFailure) return GuildLocalToolRegistry.Unavailable;

        return admitted.Value.Row;
    }

    private async Task<Result> ValidateToolIssueWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, string digest,
        GuildChildToolAdmission admission, CancellationToken ct)
    {
        if (!admission.Registry.Owns(admission) || !ReferenceEquals(admission.Registry.Parent.Driver.Caller, caller)
            || !admission.MatchesIntent(digest)) return InvalidReceipt;

        var authorized = await RequireLocalToolWithinAsync(connection, transaction, signets, admission.Registry,
            admission.Tool, admission.Eligibility, ct).ConfigureAwait(false);

        if (authorized.IsFailure) return authorized.Error;

        if (admission.Kind == GuildLocalChildToolKind.CreateAgentTask && GuildTaskArguments.IsRemote(admission.Arguments)
            && (policy is not GuildLocalHostPolicy host
                || !host.IsConfiguredRemoteDestination(admission.Arguments.GetProperty("agent").GetString()!)))
            return new Error(ErrorCodes.Guild.DestinationChanged, "The Sending requires its current configured destination.");

        return Result.Success();
    }

    private async Task<Result> ValidateToolChildWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored child, GuildChildToolAdmission admission, CancellationToken ct)
    {
        if (!admission.Registry.Owns(admission) || policy is not GuildLocalHostPolicy { IsExecutionEnabled: true }
            || RequireLocalExecutionReady().IsFailure)
            return GuildLocalToolRegistry.Unavailable;

        if (admission.Kind == GuildLocalChildToolKind.CreateAgentTask && !GuildTaskArguments.IsRemote(admission.Arguments)
            && admission.Registry.ChildDispatcher?.SupportsLocal != true)
            return new Error(ErrorCodes.Guild.Unavailable, "Local task creation is unavailable.");

        if (admission.Kind == GuildLocalChildToolKind.CreateAgentTask && GuildTaskArguments.IsRemote(admission.Arguments))
        {
            var remote = await GuildRemoteExecutionStore.ReadRecoveryWithinAsync(connection, transaction, child.Quest.Id, ct).ConfigureAwait(false);

            return remote.IsFailure ? remote.Error : Result.Success();
        }

        // This check adopts an immutable child mode, never authorizes another execution. The
        // current parent and every owner/epoch/revocation check still apply after child completion.
        var input = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, child.Quest.Id, null, ct,
            allowTerminalQuestObservation: true).ConfigureAwait(false);

        if (input.IsFailure) return input.Error;

        GuildLocalExecutionMode expected = admission.Tool.SourceKind == GuildToolSourceKind.BuiltIn
            ? GuildLocalExecutionMode.Sterile : GuildLocalExecutionMode.Apprentice;

        return input.Value.Mode == expected ? Result.Success()
            : new Error(ErrorCodes.Guild.AuthorityMismatch, "The local child mode differs from its exact tool route.");
    }
}
