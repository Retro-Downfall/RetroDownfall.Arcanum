using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Completes durable peer lifecycle denial before acknowledging an observed configuration reload.</summary>
internal sealed class GuildInboundPeerReloadService(GuildInboundPeerRegistry peers, GuildLocalHostPolicy policy,
    IServiceScopeFactory scopes, ILogger<GuildInboundPeerReloadService> logger, IGrimoireConnectionAdmissionGate admission,
    IOptionsMonitor<ArcanumSettings> settings) : BackgroundService
{
    private readonly SemaphoreSlim _serial = new(1, 1);

    private bool _subscribed;

    private int _stopping;

    private volatile bool _needsRetry = true;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        policy.SetInboundPersistenceAvailable(false);

        peers.Changed += OnChanged;

        _subscribed = true;

        await ReconcileAsync(cancellationToken).ConfigureAwait(false);

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnChanged()
    {
        if (Volatile.Read(ref _stopping) == 0)
            ReconcileAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                if (_needsRetry) await ReconcileAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _stopping) != 0) return;

            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            {
                _needsRetry = true;

                policy.SetInboundPersistenceAvailable(false);

                return;
            }

            await using IGrimoireWorkLease work = acquired;

            if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect) || effect is null)
            {
                _needsRetry = true;

                policy.SetInboundPersistenceAvailable(false);

                return;
            }

            await using IGrimoireExternalEffectGroup heldEffect = effect;

            // Failure-marker publication retains this reconciliation admission until its
            // own journal work completes, before the outer scopes are released.
            try
            {
                using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

                if (!settings.CurrentValue.Features.Guild)
                {
                    Result<bool> roots = await policy.HasInboundRootsAsync(lifetime.Token).ConfigureAwait(false);

                    if (roots.IsFailure)
                    {
                        _needsRetry = true;

                        policy.SetInboundPersistenceAvailable(false);

                        return;
                    }

                    if (!roots.Value)
                    {
                        _needsRetry = false;

                        policy.SetInboundPersistenceAvailable(false);

                        return;
                    }
                }

                Result published = peers.RetryPublication();

                if (published.IsFailure)
                {
                    _needsRetry = true;

                    policy.SetInboundPersistenceAvailable(false);

                    return;
                }

                Result identity = await policy.InitializeIdentityAsync(lifetime.Token).ConfigureAwait(false);

                Result reconciled = identity.IsFailure ? identity
                    : await policy.ReconcileInboundPeersAsync(lifetime.Token, RequestCancellationAsync).ConfigureAwait(false);

                if (reconciled.IsFailure) _ = peers.PersistUncertainDenial();

                _needsRetry = reconciled.IsFailure || peers.HasPendingDenials;

                policy.SetInboundPersistenceAvailable(reconciled.IsSuccess);

                if (reconciled.IsFailure)
                    logger.LogWarning("Inbound peer authority awaits durable lifecycle reconciliation ({ReasonCode}).", reconciled.Error.Code);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _ = peers.PersistUncertainDenial();

                _needsRetry = true;

                policy.SetInboundPersistenceAvailable(false);

                logger.LogWarning("Inbound peer authority awaits durable lifecycle reconciliation ({ExceptionKind}).", exception.GetType().Name);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<Result> RequestCancellationAsync(Guid root, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

        var found = await store.GetAsync(caller.Value, root, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        if (found.Value.Quest.CancellationRequested || found.Value.Quest.ResultReceiptId is not null) return Result.Success();

        GuildCancelQuestRequest request = new(Guid.NewGuid(), found.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

        var intent = GuildQuestIntent.Compute(root, request);

        if (intent.IsFailure) return intent.Error;

        var accepted = await scope.ServiceProvider.GetRequiredService<ConclaveArchmage>().RequestCancellationAsync(
            caller.Value, new(request.IdempotencyKey, intent.Value), root, request, ct).ConfigureAwait(false);

        return accepted.IsFailure ? accepted.Error : Result.Success();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopping, 1);

        peers.StopPublishing();

        if (_subscribed)
        {
            peers.Changed -= OnChanged;

            _subscribed = false;
        }

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Configuration callbacks are outside BackgroundService.ExecuteTask. A stopped-host
            // observation therefore also joins the exact serial admission they may still own.
            await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            _serial.Release();
        }
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _stopping, 1);

        peers.StopPublishing();

        if (_subscribed)
        {
            peers.Changed -= OnChanged;

            _subscribed = false;
        }

        base.Dispose();

        _serial.Wait();

        _serial.Release();
    }
}
