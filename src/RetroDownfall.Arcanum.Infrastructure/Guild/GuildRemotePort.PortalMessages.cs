using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildRemotePort
{
    private async Task DrainConnectedPortalAsync(Run run, CancellationToken ct)
    {
        if (Volatile.Read(ref run.PortalPeer) is not { } peer)
        {
            return;
        }

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Lifetime.Token);

        ct = lifetime.Token;

        try
        {
            if (!await run.PortalSerial.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                // Progress one page per pulse without holding the primary operation's driver
                // semaphore, A2A discovery slot, or a database transaction across the network.
                if (!run.Stopping)
                {
                    await DrainPortalWorkKindAsync(run, peer, GuildRemotePortalWorkKind.Send, ct).ConfigureAwait(false);
                }

                await DrainPortalWorkKindAsync(run, peer, GuildRemotePortalWorkKind.Observe, ct).ConfigureAwait(false);
            }
            finally
            {
                run.PortalSerial.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            // Transport observations are auxiliary. A possibly-sent row stays observable
            // through its existing durable evidence when a connection or host is interrupted.
        }
    }

    private async Task DrainPortalWorkKindAsync(Run run, A2AClientService.GuildPortalPeer peer,
        GuildRemotePortalWorkKind kind, CancellationToken ct)
    {
        GuildQuestDriver driver = Volatile.Read(ref run.Driver);

        var pending = await _quests.ReadRemotePortalWorkAsync(driver, ct, kind).ConfigureAwait(false);

        if (pending.IsFailure || pending.Value is not { } work)
        {
            return;
        }

        if (work.Kind == GuildRemotePortalWorkKind.Send)
        {
            _ = await _quests.SendRemotePortalMessageAsync(driver, work, peer.SendAsync, ct).ConfigureAwait(false);
        }
        else
        {
            _ = await _quests.ObserveRemotePortalMessageAsync(driver, work, peer.ReadReceiptAsync, ct).ConfigureAwait(false);
        }
    }

    internal async Task<Result<GuildPortalMessageDeliveryReceipt?>> ReconcileTerminalPortalAsync(
        GuildCallerContext owner, Guid questId, CancellationToken ct)
    {
        var pending = await _quests.ReadTerminalRemotePortalObservationAsync(owner, questId, ct).ConfigureAwait(false);

        if (pending.IsFailure)
        {
            return pending.Error;
        }

        if (pending.Value is not { } observation)
        {
            return Result<GuildPortalMessageDeliveryReceipt?>.Success(null);
        }

        try
        {
            var reconciled = await client.WithGuildPortalPeerAsync<GuildPortalMessageDeliveryReceipt>(observation.Target.DiscoveryEndpoint,
                observation.Target.Transport, new TerminalPortalDiscovery(this, gate, owner, questId, observation.Work), receiptOnly: true,
                (peer, token) => _quests.ObserveTerminalRemotePortalMessageAsync(owner, questId, observation.Work, peer.ReadReceiptAsync, token), ct)
                .ConfigureAwait(false);

            return reconciled.IsFailure ? reconciled.Error : Result<GuildPortalMessageDeliveryReceipt?>.Success(reconciled.Value);
        }
        catch (TerminalDiscoveryRefusedException error)
        {
            return error.Error;
        }
    }

    private sealed class TerminalPortalDiscovery(GuildRemotePort port, IGrimoireConnectionAdmissionGate admission, GuildCallerContext owner, Guid questId,
        GuildRemotePortalWork expected) : IA2AGuildDiscoveryAdmission
    {
        public async Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? lease) || lease is null)
            {
                throw new TerminalDiscoveryRefusedException(Unavailable);
            }

            await using IGrimoireWorkLease work = lease;

            if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect) || effect is null)
            {
                throw new TerminalDiscoveryRefusedException(Unavailable);
            }

            await using IGrimoireExternalEffectGroup held = effect;

            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

            var current = await port._quests.ReadTerminalRemotePortalObservationAsync(owner, questId, lifetime.Token).ConfigureAwait(false);

            if (current.IsFailure || current.Value?.Work != expected)
            {
                throw new TerminalDiscoveryRefusedException(current.IsFailure ? current.Error : Unavailable);
            }

            return await action(lifetime.Token).ConfigureAwait(false);
        }
    }

    private sealed class TerminalDiscoveryRefusedException(Error error) : Exception("Terminal peer receipt discovery was refused.")
    {
        internal Error Error { get; } = error;
    }
}
