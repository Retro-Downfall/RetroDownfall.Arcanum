using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildStore
{
    internal async Task<Result<T>> AdmitConfiguredRemoteDiscoveryAsync<T>(GuildLocalHostPolicy policy,
        GuildCallerContext caller, Func<bool> currentDestination, Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        if (caller.Kind != GuildCallerKind.Operator || !policy.Authenticates(caller))
            return new Error(ErrorCodes.Guild.Forbidden, "A current operator is required for remote discovery.");

        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            return Deferred();

        await using IGrimoireWorkLease work = acquired;

        if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect) || effect is null) return Deferred();

        await using IGrimoireExternalEffectGroup heldEffect = effect;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        Result<bool> authorized = await ExecuteAsync<bool>(false, async (connection, transaction, token) =>
        {
            Result located = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, null, null, token).ConfigureAwait(false);

            return located.IsFailure ? located.Error : true;
        }, lifetime.Token).ConfigureAwait(false);

        if (authorized.IsFailure) return authorized.Error;

        if (!currentDestination())
            return new Error(ErrorCodes.Guild.DestinationChanged, "The configured remote destination changed during discovery.");

        lifetime.Token.ThrowIfCancellationRequested();

        // The physical request and its bounded body finish while maintenance still owns this work
        // and effect scope; the read transaction above has ended before any network operation.
        return await send(lifetime.Token).ConfigureAwait(false);
    }

    internal Task<Result<GuildStoredCard>> UpsertConfiguredRemoteAsync(GuildLocalHostPolicy policy,
        GuildCallerContext caller, GuildCardWrite write, Func<bool> currentDestination, CancellationToken ct) =>
        ExecuteAsync(true, async (connection, transaction, token) =>
        {
            if (!currentDestination())
                return new Error(ErrorCodes.Guild.DestinationChanged, "The configured remote destination changed during discovery.");

            Result authorized = await policy.AuthorizeLocateAsync(caller, connection, transaction,
                caller.Execution?.QuestId, null, null, token).ConfigureAwait(false);

            if (authorized.IsFailure) return Result<GuildStoredCard>.Failure(authorized.Error);

            return await UpsertWithinAsync(connection, transaction, write, token).ConfigureAwait(false);
        }, ct);
}
