using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildWorkspaceLeaseBroker
{
    internal Task<Result<GuildMutationPermit>> AdmitWriterMutationAsync(GuildVerifiedAuthority authority, GuildMutationPlan plan,
        string writerRoot, CancellationToken cancellationToken) => AdmitMutationCoreAsync(authority, plan, writerRoot, cancellationToken);

    private static Result<GuildMutationPlan> TranslateWriterPlan(string workspaceRoot, string writerRoot, GuildMutationPlan plan)
    {
        try
        {
            if (!Path.IsPathFullyQualified(writerRoot) || !WorkspacePathPolicy.IsPathUnderWorkspace(workspaceRoot, writerRoot)) return Forbidden;

            ImmutableArray<GuildPathScope>.Builder scopes = ImmutableArray.CreateBuilder<GuildPathScope>();

            foreach (GuildPathScope scope in plan.Scopes)
            {
                if (scope is null || Path.IsPathRooted(scope.RelativePath)) return Invalid;

                string path = Path.GetFullPath(scope.RelativePath, writerRoot);

                if (!WorkspacePathPolicy.IsPathUnderWorkspace(workspaceRoot, path)) return Forbidden;

                scopes.Add(new(Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/'), scope.Kind));
            }

            return plan with { Scopes = scopes.ToImmutable() };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return Invalid;
        }
    }

    internal async Task<Result> ValidateRollbackAsync(GuildMutationPermit permit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_permits.TryGetValue(permit, out ActivePermit? active)) return Forbidden;

            Result physical = await identities.ValidateAsync(active.Identity, cancellationToken).ConfigureAwait(false);

            if (physical.IsFailure) return physical;

            Result<bool> current = await store.ReadTransactionAsync<bool>(async (connection, transaction, ct) =>
            {
                foreach (HeldLease held in active.Leases)
                {
                    Result<GuildWorkspaceLeaseRecord> read = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, held.Row.Id, ct).ConfigureAwait(false);

                    if (read.IsFailure) return Result<bool>.Failure(read.Error);

                    if (read.Value.HostInstanceId != HostInstanceId || read.Value.Epoch != held.Row.Epoch
                        || read.Value.Owner != held.Row.Owner || read.Value.State == GuildWorkspaceLeaseState.Released
                        || read.Value.InFlightMutationCount <= 0) return Result<bool>.Failure(Forbidden);
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);

            return current.IsSuccess ? Result.Success() : current.Error;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<Result> RetainRecoveryAsync(GuildMutationPermit permit, string reasonCode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (!_permits.TryGetValue(permit, out ActivePermit? active)) return Forbidden;

            // Set before any cancellable persistence. Even storage failure cannot turn Dispose
            // into an ordinary successful completion or free this scope for another writer.
            active.RecoveryRequired = true;

            Result<bool> marked = await store.InTransactionAsync<bool>(async (connection, transaction, ct) =>
            {
                foreach (HeldLease held in active.Leases)
                {
                    Result<GuildWorkspaceLeaseRecord> read = await GuildWorkspaceLeaseStore.ReadWithinAsync(connection, transaction, held.Row.Id, ct).ConfigureAwait(false);

                    if (read.IsFailure) return Result<bool>.Failure(read.Error);

                    if (read.Value.Epoch != held.Row.Epoch || read.Value.HostInstanceId != HostInstanceId
                        || read.Value.Owner != held.Row.Owner || read.Value.InFlightMutationCount <= 0) return Result<bool>.Failure(Forbidden);

                    Result<GuildWorkspaceLeaseRecord> retained = await GuildWorkspaceLeaseStore.RetainRecoveryWithinAsync(connection, transaction, read.Value, ct).ConfigureAwait(false);

                    if (retained.IsFailure) return Result<bool>.Failure(retained.Error);

                    _held.Remove(held.Row.Id);
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);

            return marked.IsSuccess ? Result.Success() : marked.Error;
        }
        finally
        {
            _gate.Release();
        }
    }
}
