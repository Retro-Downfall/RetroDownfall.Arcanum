using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceReadAdmission(Guid WorkspaceId, GuildGrant Grant, GuildWorkspaceAuthority Physical);

/// <summary>Evidence for replaying an already-materialized read, with no authority to run another tool.</summary>
internal sealed record GuildWorkspaceReadDisclosure(GuildLocalToolRegistry Registry, GuildToolGrant Tool,
    Func<GuildToolGrant, bool> Eligibility, Guid WorkspaceId, ImmutableArray<GuildWorkspaceGrant> Scopes,
    Guid LogicalCallId, ImmutableArray<string> SourcePaths,
    Func<CancellationToken, Task<Result>> VerifyPhysicalAsync);

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildWorkspaceReadAdmission>> AdmitWorkspaceReadToolAsync(GuildChildToolAdmission call, CancellationToken ct)
    {
        if (!OwnsWorkspaceReadCall(call) || policy is not GuildLocalHostPolicy host)
            return Task.FromResult(Result<GuildWorkspaceReadAdmission>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildWorkspaceReadAdmission>(call.Registry.Parent.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireLocalToolGrantWithinAsync(connection, transaction, signets,
                    call.Registry, call.Tool, call.Eligibility, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (!OwnsWorkspaceReadCall(call) || admitted.Value.Quest.Quest.WorkspaceId is not { } workspaceId)
                    return InvalidReceipt;

                var physical = await host.ResolveWorkspaceAuthorityWithinAsync(connection, transaction, workspaceId, token).ConfigureAwait(false);

                if (physical.IsFailure) return physical.Error;

                GuildGrant grant = admitted.Value.Grant with { Workspaces = [.. admitted.Value.Grant.Workspaces.Where(scope => scope.WorkspaceId == workspaceId)] };

                return new GuildWorkspaceReadAdmission(workspaceId, grant, physical.Value);
            }, ct);
    }

    internal async Task<Result> RevalidateWorkspaceReadToolAsync(GuildChildToolAdmission call, GuildWorkspaceReadAdmission expected, CancellationToken ct)
    {
        if (!OwnsWorkspaceReadCall(call)) return InvalidReceipt;

        Result<GuildWorkspaceReadAdmission> current = await AdmitWorkspaceReadToolAsync(call, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        return OwnsWorkspaceReadCall(call) && current.Value.WorkspaceId == expected.WorkspaceId
            && current.Value.Grant.Workspaces.SequenceEqual(expected.Grant.Workspaces) ? Result.Success() : WorkspaceReadNotGranted;
    }

    private async Task<Result> ValidateWorkspaceToolDisclosuresWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildLocalInferenceExecution execution,
        IReadOnlyList<GuildWorkspaceReadDisclosure>? disclosures, CancellationToken ct)
    {
        if (disclosures is not { Count: > 0 }) return Result.Success();

        Dictionary<GuildToolGrant, ImmutableArray<GuildWorkspaceGrant>> liveScopes = [];

        foreach (GuildWorkspaceReadDisclosure disclosure in disclosures)
        {
            if (!ReferenceEquals(disclosure.Registry.Parent, execution) || !GuildLocalToolRegistry.IsWorkspaceReadTool(disclosure.Tool.Name)
                || !disclosure.Registry.Supports(disclosure.Tool) || !disclosure.Eligibility(disclosure.Tool)) return WorkspaceReadNotGranted;

            if (!liveScopes.TryGetValue(disclosure.Tool, out ImmutableArray<GuildWorkspaceGrant> scopes))
            {
                var admitted = await RequireLocalToolGrantWithinAsync(connection, transaction, signets,
                    disclosure.Registry, disclosure.Tool, disclosure.Eligibility, ct).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (admitted.Value.Quest.Quest.WorkspaceId != disclosure.WorkspaceId) return WorkspaceReadNotGranted;

                scopes = [.. admitted.Value.Grant.Workspaces.Where(scope => scope.WorkspaceId == disclosure.WorkspaceId)];

                liveScopes.Add(disclosure.Tool, scopes);
            }

            if (!scopes.SequenceEqual(disclosure.Scopes)) return WorkspaceReadNotGranted;

            Result physical = await disclosure.VerifyPhysicalAsync(ct).ConfigureAwait(false);

            if (physical.IsFailure) return physical;
        }

        return Result.Success();
    }

    private bool OwnsWorkspaceReadCall(GuildChildToolAdmission call) =>
        call is not null && ReferenceEquals(call.Registry.Parent.Store, this) && GuildLocalToolRegistry.IsWorkspaceReadTool(call.Tool.Name)
        && call.Registry.Owns(call) && ReferenceEquals(call.Registry.CaptureInvocation(call.Tool), call.Frame);

    private static Error WorkspaceReadNotGranted => new(ErrorCodes.Guild.Forbidden, "The run has no current read authority for this workspace.");
}
