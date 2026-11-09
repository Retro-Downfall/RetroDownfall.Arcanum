using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Wakes committed Quest work; durable start intent and the existing LRO remain authoritative.</summary>
internal sealed class GuildLocalQuestScheduler(IServiceScopeFactory scopes, IOptionsMonitor<ArcanumSettings> settings,
    IGrimoireOrdinaryConnectionFactory connections, LongRunningOperationOwnership ownership, TimeProvider time,
    ILogger<GuildLocalQuestScheduler> logger, IGuildLocalHostReadiness readiness,
    IGrimoireConnectionAdmissionGate admission) : BackgroundService, IGuildLocalQuestScheduler
{
    internal const string CancellationFrontierQuery = """
        SELECT CASE WHEN typeof(quest.Id)='text' AND length(CAST(quest.Id AS BLOB))=36 THEN quest.Id END
        FROM guild_quests quest JOIN guild_quest_start_intents intent ON quest.Id=intent.QuestId
            JOIN guild_local_executions binding ON binding.QuestId=quest.Id
        WHERE quest.CancellationRequested=1 AND quest.CancellationConverged=0 AND quest.Retired=0 AND quest.Id>$after
            AND NOT EXISTS(SELECT 1 FROM guild_quests parent WHERE parent.Id=quest.ParentQuestId AND parent.CancellationRequested=1)
        ORDER BY quest.Id LIMIT 100
        """;

    private readonly Channel<Guid> _wakes = Channel.CreateUnbounded<Guid>(new() { SingleReader = true, AllowSynchronousContinuations = false });

    private readonly ConcurrentDictionary<Guid, object> _pending = new();

    private int _ready;

    private int _stopping;

    public bool IsAvailable => Volatile.Read(ref _ready) == 1 && Volatile.Read(ref _stopping) == 0 && GuildLocalExecutionAdmission.IsAvailable(settings.CurrentValue)
        && readiness.RequireExecutionReady().IsSuccess;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshReadinessAsync(cancellationToken).ConfigureAwait(false);

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 1);

        Volatile.Write(ref _ready, 0);

        _wakes.Writer.TryComplete();

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Schedule(GuildQuestDto quest)
    {
        if (quest is null || quest.Id == Guid.Empty || !IsAvailable) return;

        Enqueue(quest.Id);
    }

    private void Enqueue(Guid questId)
    {
        if (!IsAvailable) return;

        object completion = new();

        if (!_pending.TryAdd(questId, completion)) return;

        if (!_wakes.Writer.TryWrite(questId))
        {
            _pending.TryRemove(new KeyValuePair<Guid, object>(questId, completion));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        Task pump = PumpAsync(lifetime);

        Task discovery = DiscoverAsync(lifetime);

        try
        {
            await Task.WhenAll(pump, discovery).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _ready, 0);

            _wakes.Writer.TryComplete();

            while (_wakes.Reader.TryRead(out Guid queued))
            {
                _pending.TryRemove(queued, out _);
            }
        }
    }

    private async Task PumpAsync(CancellationTokenSource lifetime)
    {
        CancellationToken ct = lifetime.Token;

        List<Task> active = new();

        try
        {
            try
            {
                await foreach (Guid questId in _wakes.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    // Observe completed tasks before removing them; the list retains only work still
                    // owed a join, rather than a completion signal detached from its actual task.
                    for (int index = active.Count - 1; index >= 0; index--)
                    {
                        Task completed = active[index];

                        if (!completed.IsCompleted) continue;

                        await completed.ConfigureAwait(false);

                        active.RemoveAt(index);
                    }

                    if (_pending.TryGetValue(questId, out object? completion))
                    {
                        Task execution = RunCommittedAsync(questId, completion, ct);

                        active.Add(execution);
                    }
                }
            }
            finally
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // Canceling a sibling cannot bypass the real dispatch-task/resource drain.
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    private async Task DiscoverAsync(CancellationTokenSource lifetime)
    {
        CancellationToken ct = lifetime.Token;

        try
        {
            using PeriodicTimer cadence = new(TimeSpan.FromSeconds(1), time);

            do
            {
                await RefreshReadinessAsync(ct).ConfigureAwait(false);

                if (!settings.CurrentValue.Features.Guild)
                    await RequestDisabledCancellationAsync(ct).ConfigureAwait(false);

                await DiscoverPendingAsync(ct).ConfigureAwait(false);
            }

            while (await cadence.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task DiscoverPendingAsync(CancellationToken ct)
    {
        if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return;

        await using IGrimoireWorkLease work = acquired;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        try
        {
            // Cascade discovery is independent of the live execution task. A parent waiting
            // for its descendants cannot suppress the durable stop work that lets it finish.
            await DiscoverCancellationFrontiersAsync(lifetime.Token).ConfigureAwait(false);

            Guid? after = null;

            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();

                // Availability includes retained-lock filesystem identity, so its probe belongs
                // inside the admitted scan scope just like the database page it protects.
                if (!IsAvailable) return;

                var page = await ReadPendingIdsWithinAsync(after, lifetime.Token).ConfigureAwait(false);

                if (page.IsFailure || page.Value.IsEmpty) return;

                foreach (Guid questId in page.Value) Enqueue(questId);

                after = page.Value[^1];
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && work.MaintenanceRevocation.IsCancellationRequested)
        {
            Volatile.Write(ref _ready, 0);
        }
    }

    private async Task DiscoverCancellationFrontiersAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

        GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

        var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

        if (caller.IsFailure) return;

        Guid? after = null;

        while (IsAvailable)
        {
            var roots = await ReadPendingIdsWithinAsync(after, ct, cancellationFrontiers: true).ConfigureAwait(false);

            if (roots.IsFailure || roots.Value.IsEmpty) return;

            foreach (Guid root in roots.Value)
            {
                long sequence = 0;

                while (IsAvailable)
                {
                    var page = await store.ReadCancellationWorkPageAsync(caller.Value, root, sequence, ct).ConfigureAwait(false);

                    if (page.IsFailure) break;

                    if (page.Value.IsEmpty)
                    {
                        _ = await store.RefreshCancellationConvergenceAsync(caller.Value, root, ct).ConfigureAwait(false);

                        break;
                    }

                    foreach (GuildCancellationWork child in page.Value)
                    {
                        GuildCancelQuestRequest request = new(Guid.NewGuid(), child.Revision, GuildCancellationReason.OperatorRequested);

                        var digest = GuildQuestIntent.Compute(child.QuestId, request);

                        if (digest.IsFailure) continue;

                        // Persist the ordinary receipt before any port call. Revision races are
                        // retried by the next scan; accepted children disappear from work pages.
                        var accepted = await store.RequestCancellationAsync(caller.Value,
                            new(request.IdempotencyKey, digest.Value), child.QuestId, request, ct).ConfigureAwait(false);

                        if (accepted.IsSuccess) Enqueue(child.QuestId);
                    }

                    sequence = page.Value[^1].IssuedSequence;
                }
            }

            after = roots.Value[^1];
        }
    }

    private async Task RequestDisabledCancellationAsync(CancellationToken ct)
    {
        CancellationToken callerCancellation = ct;

        try
        {
            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            {
                Volatile.Write(ref _ready, 0);

                return;
            }

            await using IGrimoireWorkLease work = acquired;

            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

            ct = lifetime.Token;

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();

            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

            if (caller.IsFailure) return;

            Guid? after = null;

            while (!settings.CurrentValue.Features.Guild)
            {
                var page = await ReadPendingIdsAsync(after, ct).ConfigureAwait(false);

                if (page.IsFailure || page.Value.IsEmpty) return;

                foreach (Guid questId in page.Value)
                {
                    if (settings.CurrentValue.Features.Guild) return;

                    var found = await store.GetAsync(caller.Value, questId, ct).ConfigureAwait(false);

                    if (found.IsFailure || found.Value.Quest.ResultReceiptId is not null) continue;

                    if (!found.Value.Quest.CancellationRequested)
                    {
                        GuildCancelQuestRequest request = new(Guid.NewGuid(), found.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

                        var digest = GuildQuestIntent.Compute(questId, request);

                        if (digest.IsFailure) continue;

                        // Revision conflicts are retried from the next durable scan; accepted flags suppress duplicate receipts.
                        var accepted = await store.RequestCancellationAsync(caller.Value,
                            new(request.IdempotencyKey, digest.Value), questId, request, ct).ConfigureAwait(false);

                        if (accepted.IsFailure) continue;
                    }

                    await DrainCancellationAsync(scope.ServiceProvider, caller.Value, questId, ct).ConfigureAwait(false);
                }

                after = page.Value[^1];
            }
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref _ready, 0);
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Guild disabled-work cancellation is unavailable ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private async Task RefreshReadinessAsync(CancellationToken ct)
    {
        if (!GuildLocalExecutionAdmission.IsAvailable(settings.CurrentValue) || Volatile.Read(ref _stopping) != 0)
        {
            Volatile.Write(ref _ready, 0);

            return;
        }

        CancellationToken callerCancellation = ct;

        try
        {
            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            {
                Volatile.Write(ref _ready, 0);

                return;
            }

            await using IGrimoireWorkLease work = acquired;

            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

            ct = lifetime.Token;

            // Readiness owns its physical-stage work/effect after durable identity reads.
            // This outer work scope still protects this caller's live probes and inventory.
            Result protectedHost = await readiness.EnsureReadyAsync(ct).ConfigureAwait(false);

            if (protectedHost.IsFailure)
            {
                Volatile.Write(ref _ready, 0);

                return;
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();

            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            IGuildLocalInferenceRuntime runtime = scope.ServiceProvider.GetRequiredService<IGuildLocalInferenceRuntime>();

            _ = scope.ServiceProvider.GetRequiredService<IGuildLocalExecutionEngine>();

            _ = scope.ServiceProvider.GetRequiredService<GuildLocalPort>();

            if (!runtime.IsAvailable || !store.HasLocalExecutionAdmission)
            {
                Volatile.Write(ref _ready, 0);

                return;
            }

            var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

            var inventory = caller.IsSuccess ? await ReadPendingIdsAsync(null, ct).ConfigureAwait(false)
                : Result<ImmutableArray<Guid>>.Failure(caller.Error);

            Volatile.Write(ref _ready, inventory.IsSuccess ? 1 : 0);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref _ready, 0);
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _ready, 0);

            logger.LogDebug("Guild scheduler readiness is unavailable ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private async Task RunCommittedAsync(Guid questId, object completion, CancellationToken ct)
    {
        try
        {
            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return;

            await using IGrimoireWorkLease work = acquired;

            await RunAdmittedAsync(work, questId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning("Guild scheduled work stopped for {QuestId} ({ExceptionType}).", questId, exception.GetType().Name);
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<Guid, object>(questId, completion));
        }
    }

    private async Task RunAdmittedAsync(IGrimoireWorkLease work, Guid questId, CancellationToken ct)
    {
        Guid operationId = Guid.Empty, claim = Guid.Empty;

        try
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

            ct = lifetime.Token;

            ct.ThrowIfCancellationRequested();

            if (!IsAvailable) return;

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();

            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            SignetAuthority authority = scope.ServiceProvider.GetRequiredService<SignetAuthority>();

            GuildLocalPort port = scope.ServiceProvider.GetRequiredService<GuildLocalPort>();

            var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

            if (caller.IsFailure) return;

            var pending = await store.ReadLocalPendingAsync(caller.Value, questId, ct).ConfigureAwait(false);

            if (pending.IsFailure) return;

            if (!pending.Value.Quest.Quest.CancellationRequested && pending.Value.RequiresAttention)
            {
                var frontier = await store.ReadLocalCancellationFrontierAsync(caller.Value, questId, ct).ConfigureAwait(false);

                if (frontier.IsFailure) return;

                if (frontier.Value.AncestorCanceled && frontier.Value.Quest.Quest.ResultReceiptId is null)
                {
                    GuildCancelQuestRequest stop = new(Guid.NewGuid(), frontier.Value.Quest.Quest.Revision,
                        GuildCancellationReason.OperatorRequested);

                    var digest = GuildQuestIntent.Compute(questId, stop);

                    if (digest.IsFailure) return;

                    var accepted = await store.RequestCancellationAsync(caller.Value,
                        new(stop.IdempotencyKey, digest.Value), questId, stop, ct).ConfigureAwait(false);

                    if (accepted.IsFailure) return;

                    await DrainCancellationAsync(scope.ServiceProvider, caller.Value, questId, ct).ConfigureAwait(false);

                    return;
                }
            }

            if (pending.Value.Quest.Quest.CancellationRequested)
            {
                await DrainCancellationAsync(scope.ServiceProvider, caller.Value, questId, ct).ConfigureAwait(false);

                return;
            }

            if (pending.Value.RequiresAttention || pending.Value.Request is null || !IsAvailable) return;

            GuildQuestDto quest = pending.Value.Quest.Quest;

            operationId = quest.OperationId;

            // Claim before a rebind can rotate the credential held by an already active local run.
            if (!ownership.TryClaim(operationId, out claim)) return;

            var recovery = await policy.CreateRecoveryAsync(caller.Value, quest.Id, quest.OperationId, quest.SignetRevision, ct).ConfigureAwait(false);

            if (recovery.IsFailure) return;

            var rebound = await authority.RebindAsync(recovery.Value, ct).ConfigureAwait(false);

            if (rebound.IsFailure) return;

            var prepared = await store.PrepareLocalScheduleAsync(caller.Value, rebound.Value, pending.Value.Operation.Revision, ct).ConfigureAwait(false);

            if (prepared.IsFailure || !IsAvailable) return;

            _ = await port.DispatchClaimedAsync(rebound.Value, prepared.Value, claim, ct).ConfigureAwait(false);
        }
        finally
        {
            if (claim != Guid.Empty) ownership.Release(operationId, claim);
        }
    }

    private async Task DrainCancellationAsync(IServiceProvider services, GuildCallerContext caller, Guid questId, CancellationToken ct)
    {
        GuildQuestStore store = services.GetRequiredService<GuildQuestStore>();

        var pending = await store.ReadLocalPendingAsync(caller, questId, ct).ConfigureAwait(false);

        if (pending.IsFailure || !pending.Value.Quest.Quest.CancellationRequested
            || pending.Value.Quest.Quest.ResultReceiptId is not null
            || pending.Value.Operation.LeaseOwner is not null && pending.Value.Operation.LeaseExpiresAt > time.GetUtcNow()) return;

        // A pending parent must not acquire a lease while its children still need their own
        // cancellation drain. The final receipt independently checks this frontier atomically.
        var frontier = await store.ReadLocalCancellationFrontierAsync(caller, questId, ct).ConfigureAwait(false);

        if (frontier.IsFailure || !frontier.Value.ChildrenStopped) return;

        Guid operationId = pending.Value.Operation.Id;

        if (!ownership.TryClaim(operationId, out Guid claim)) return;

        try
        {
            var mutation = await store.ReadLocalCancellationAsync(caller, questId, ct).ConfigureAwait(false);

            if (mutation.IsFailure) return;

            GuildQuestDto quest = pending.Value.Quest.Quest;

            GuildLocalHostPolicy policy = services.GetRequiredService<GuildLocalHostPolicy>();

            var recovery = await policy.CreateRecoveryAsync(caller, quest.Id, operationId, quest.SignetRevision, ct).ConfigureAwait(false);

            if (recovery.IsFailure) return;

            var execution = await services.GetRequiredService<SignetAuthority>().RebindAsync(recovery.Value, ct).ConfigureAwait(false);

            if (execution.IsFailure) return;

            _ = await services.GetRequiredService<GuildLocalPort>().RequestCancellationClaimedAsync(execution.Value,
                mutation.Value, questId, questId, claim, ct).ConfigureAwait(false);
        }
        finally
        {
            ownership.Release(operationId, claim);
        }
    }

    private async Task<Result<ImmutableArray<Guid>>> ReadPendingIdsAsync(Guid? after, CancellationToken ct)
    {
        if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            return new Error(ErrorCodes.Guild.Unavailable, "Pending work inventory is unavailable during installation maintenance.");

        await using IGrimoireWorkLease work = acquired;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        try
        {
            return await ReadPendingIdsWithinAsync(after, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && work.MaintenanceRevocation.IsCancellationRequested)
        {
            return new Error(ErrorCodes.Guild.Unavailable, "Pending work inventory yielded to installation maintenance.");
        }
    }

    private async Task<Result<ImmutableArray<Guid>>> ReadPendingIdsWithinAsync(Guid? after, CancellationToken ct, bool cancellationFrontiers = false)
    {
        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using IGrimoireOrdinaryConnectionLease held = opened.Value;

        await using SqliteTransaction transaction = held.Connection.BeginTransaction(deferred: true);

        Result available = await GuildStorageAvailability.CheckAsync(held.Connection, transaction, ct).ConfigureAwait(false);

        if (available.IsFailure) return available.Error;

        await using SqliteCommand command = held.Connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = cancellationFrontiers ? CancellationFrontierQuery : """
            SELECT CASE WHEN typeof(quest.Id)='text' AND length(CAST(quest.Id AS BLOB))=36 THEN quest.Id END
            FROM guild_quest_start_intents intent JOIN guild_quests quest ON quest.Id=intent.QuestId
                JOIN guild_local_executions binding ON binding.QuestId=quest.Id
            WHERE NOT EXISTS(SELECT 1 FROM guild_result_receipts receipt WHERE receipt.QuestId=quest.Id)
                AND quest.Retired=0 AND quest.Id>$after
            ORDER BY quest.Id LIMIT 100
            """;

        command.Parameters.AddWithValue("$after", after?.ToString("D").ToUpperInvariant() ?? string.Empty);

        ImmutableArray<Guid>.Builder ids = ImmutableArray.CreateBuilder<Guid>();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || !Guid.TryParse(reader.GetString(0), out Guid id) || id == Guid.Empty)
                return new Error(ErrorCodes.Guild.Unavailable, "The committed pending identity is inconsistent.");

            ids.Add(id);
        }

        return ids.ToImmutable();
    }
}
