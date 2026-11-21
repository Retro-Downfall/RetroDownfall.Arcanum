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
using RetroDownfall.Arcanum.Core.Intelligence.Models;
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
    // Retired is before Id in the published cancellation index. Seek each fixed partition
    // separately, then merge at most 200 rows instead of sorting all unconverged history.
    internal const string CancellationFrontierQuery = """
        SELECT Id FROM (
            SELECT Id FROM (
                SELECT CASE WHEN typeof(quest.Id)='text' AND length(CAST(quest.Id AS BLOB))=36 THEN quest.Id END AS Id
                FROM guild_quests quest JOIN guild_quest_start_intents intent ON quest.Id=intent.QuestId
                    LEFT JOIN guild_local_executions binding ON binding.QuestId=quest.Id
                    LEFT JOIN guild_remote_executions remote ON remote.QuestId=quest.Id
                WHERE quest.CancellationRequested=1 AND quest.CancellationConverged=0 AND quest.Retired=0 AND quest.Id>$after
                    AND (binding.QuestId IS NOT NULL OR (remote.QuestId IS NOT NULL
                        AND NOT EXISTS(SELECT 1 FROM guild_quests parent WHERE parent.Id=quest.ParentQuestId AND parent.CancellationRequested=1)))
                ORDER BY quest.Id LIMIT 100
            )
            UNION ALL
            SELECT Id FROM (
                SELECT CASE WHEN typeof(quest.Id)='text' AND length(CAST(quest.Id AS BLOB))=36 THEN quest.Id END AS Id
                FROM guild_quests quest JOIN guild_quest_start_intents intent ON quest.Id=intent.QuestId
                    JOIN guild_local_executions binding ON binding.QuestId=quest.Id
                WHERE quest.CancellationRequested=1 AND quest.CancellationConverged=0 AND quest.Retired=1 AND quest.Id>$after
                ORDER BY quest.Id LIMIT 100
            )
        ) ORDER BY Id LIMIT 100
        """;

    internal const string AccountingRecoveryFrontierQuery = """
        SELECT Id,RootQuestId,AccountingStateCode FROM guild_quests
        WHERE Id>$after ORDER BY Id LIMIT 100
        """;

    private string? _accountingScanAfter;

    private readonly Channel<Guid> _wakes = Channel.CreateUnbounded<Guid>(new() { SingleReader = true, AllowSynchronousContinuations = false });

    private readonly ConcurrentDictionary<Guid, object> _pending = new();

    private int _ready;

    private int _stopping;

    private long _configurationEpoch;

    public bool IsAvailable => Volatile.Read(ref _ready) == 1 && Volatile.Read(ref _stopping) == 0 && CanDrive(settings.CurrentValue)
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

    public void Schedule(GuildQuestDto quest) => _ = TrySchedule(quest);

    internal bool TrySchedule(GuildQuestDto quest) =>
        quest is not null && quest.Id != Guid.Empty && IsAvailable && Enqueue(quest.Id);

    private bool Enqueue(Guid questId)
    {
        if (!IsAvailable) return false;

        object completion = new();

        if (!_pending.TryAdd(questId, completion)) return false;

        if (_wakes.Writer.TryWrite(questId)) return true;

        _pending.TryRemove(new KeyValuePair<Guid, object>(questId, completion));

        return false;
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

        long observedGeneration = admission.CurrentGeneration;

        using IDisposable? subscription = settings.OnChange((_, _) => Interlocked.Increment(ref _configurationEpoch));

        try
        {
            using PeriodicTimer cadence = new(TimeSpan.FromSeconds(1), time);

            do
            {
                if (observedGeneration != admission.CurrentGeneration)
                {
                    observedGeneration = admission.CurrentGeneration;

                    _accountingScanAfter = null;
                }

                ArcanumSettings publication = settings.CurrentValue;

                long epoch = Interlocked.Read(ref _configurationEpoch);

                bool healthy = await RefreshReadinessAsync(ct).ConfigureAwait(false);

                if (!settings.CurrentValue.Features.Guild)
                    healthy &= await RequestDisabledCancellationAsync(ct).ConfigureAwait(false);

                healthy &= await DiscoverPendingAsync(ct).ConfigureAwait(false);

                if (healthy && !publication.Features.Guild && ReferenceEquals(publication, settings.CurrentValue)
                    && epoch == Interlocked.Read(ref _configurationEpoch) && admission.IsOrdinaryGeneration(observedGeneration)
                    && _pending.IsEmpty && _accountingScanAfter is null)
                {
                    IdleObservationResult result = await ObserveIdleAsync(cadence, publication, epoch, observedGeneration, ct).ConfigureAwait(false);

                    if (result.Generation is { } generation && !admission.IsOrdinaryGeneration(generation))
                    {
                        _accountingScanAfter = null;

                        observedGeneration = await admission.WaitForNextOpenGenerationAsync(generation, ct).ConfigureAwait(false);

                        continue;
                    }

                    if (result.RestartImmediately) continue;
                }

                if (!await cadence.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
            }
            while (true);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private readonly record struct IdleObservationResult(bool RestartImmediately, long? Generation);

    private async Task<IdleObservationResult> ObserveIdleAsync(PeriodicTimer cadence, ArcanumSettings publication,
        long epoch, long expectedGeneration, CancellationToken ct)
    {
        if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            return default;

        await using IGrimoireWorkLease work = acquired;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        long generation = work.Generation;

        try
        {
            Result<IGrimoireOrdinaryConnectionLease> opened;

            try
            {
                opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return new(false, generation);
            }

            if (opened.IsFailure) return new(false, generation);

            await using IGrimoireOrdinaryConnectionLease connection = opened.Value;

            RecoveryObservation observation;

            try
            {
                observation = new(connection.Connection, publication, epoch, lifetime.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new(false, generation);
            }

            if (generation != expectedGeneration || !ObservationCurrent(observation, work)
                || !await observation.ProveAsync(ct).ConfigureAwait(false) || !ObservationCurrent(observation, work))
                return new(false, generation);

            while (await cadence.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (!ObservationCurrent(observation, work)
                    || !await observation.ObserveAsync(ct).ConfigureAwait(false) || !ObservationCurrent(observation, work))
                    return new(true, generation);
            }

            return new(false, generation);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && work.MaintenanceRevocation.IsCancellationRequested)
        {
            return new(true, generation);
        }
    }

    private bool ObservationCurrent(RecoveryObservation observation, IGrimoireWorkLease work) =>
        !work.MaintenanceRevocationPending && !work.MaintenanceRevocation.IsCancellationRequested && admission.IsOrdinaryGeneration(work.Generation)
        && ReferenceEquals(observation.Publication, settings.CurrentValue)
        && !settings.CurrentValue.Features.Guild && observation.Epoch == Interlocked.Read(ref _configurationEpoch)
        && _pending.IsEmpty && _accountingScanAfter is null;

    // This connection is an observation hint only. The normal recovery paths remain the sole
    // authority for acting on any work found by the global census.
    private sealed class RecoveryObservation
    {
        private const string QuietCensus = """
            SELECT CASE WHEN
                EXISTS(SELECT 1 FROM guild_quests c WHERE c.AccountingStateCode IS NOT 0)
                OR EXISTS(SELECT 1 FROM guild_quests c
                    WHERE (EXISTS(SELECT 1 FROM guild_local_executions local WHERE local.QuestId=c.Id)
                        OR EXISTS(SELECT 1 FROM guild_remote_executions remote WHERE remote.QuestId=c.Id)) AND
            """ + GuildQuestRetentionPins.StoppedExecutionUnresolved + """
                )
                OR EXISTS(SELECT 1 FROM guild_quest_start_intents intent JOIN guild_quests quest ON quest.Id=intent.QuestId
                    WHERE (EXISTS(SELECT 1 FROM guild_local_executions binding WHERE binding.QuestId=quest.Id)
                        OR EXISTS(SELECT 1 FROM guild_remote_executions remote WHERE remote.QuestId=quest.Id))
                        AND NOT EXISTS(SELECT 1 FROM guild_result_receipts receipt WHERE receipt.QuestId=quest.Id)
                        AND quest.Retired=0)
                OR EXISTS(SELECT 1 FROM guild_quests quest JOIN guild_quest_start_intents intent ON quest.Id=intent.QuestId
                    WHERE quest.CancellationRequested=1 AND quest.CancellationConverged=0 AND
                        ((quest.Retired=0 AND (EXISTS(SELECT 1 FROM guild_local_executions binding WHERE binding.QuestId=quest.Id)
                            OR (EXISTS(SELECT 1 FROM guild_remote_executions remote WHERE remote.QuestId=quest.Id)
                                AND NOT EXISTS(SELECT 1 FROM guild_quests parent WHERE parent.Id=quest.ParentQuestId AND parent.CancellationRequested=1))))
                         OR (quest.Retired=1 AND EXISTS(SELECT 1 FROM guild_local_executions binding WHERE binding.QuestId=quest.Id))))
                OR EXISTS(SELECT 1 FROM guild_cohorts WHERE typeof(StateCode) <> 'integer' OR StateCode NOT IN(4,5,6))
                OR EXISTS(SELECT 1 FROM guild_cohort_stop_frontiers WHERE Completed IS NOT 1)
            THEN 0 ELSE 1 END
            """;

        private readonly SqliteConnection _connection;

        private readonly CancellationToken _token;

        private readonly SQLitePCL.sqlite3 _handle;

        private (long Schema, long Data, long Changes)? _stamp;

        internal RecoveryObservation(SqliteConnection connection,
            ArcanumSettings publication, long epoch, CancellationToken token)
        {
            _connection = connection;

            _token = token;

            _handle = connection.Handle ?? throw new InvalidOperationException("The observer has no SQLite handle.");

            Publication = publication;

            Epoch = epoch;
        }

        internal ArcanumSettings Publication { get; }

        internal long Epoch { get; }

        internal CancellationToken Token => _token;

        internal async Task<bool> ObserveAsync(CancellationToken ct)
        {
            try
            {
                (long Schema, long Data, long Changes) current = await ReadStampAsync().ConfigureAwait(false);

                return _stamp == current || await ProveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { Token.ThrowIfCancellationRequested(); _stamp = null; return false; }
        }

        internal async Task<bool> ProveAsync(CancellationToken ct)
        {
            try
            {
                var before = await ReadStampAsync().ConfigureAwait(false);

                bool quiet = await RunAttemptAsync(transactional: true, async (connection, transaction, token) =>
                {
                    Result available = await GuildStorageAvailability.CheckAsync(connection, transaction, token).ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();

                    if (available.IsFailure) return false;

                    await using SqliteCommand command = connection.CreateCommand();

                    command.Transaction = transaction;

                    command.CommandText = QuietCensus;

                    object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();

                    return value is long number && number == 1;
                }).ConfigureAwait(false);

                var after = await ReadStampAsync().ConfigureAwait(false);

                _stamp = quiet && before == after ? after : null;

                return _stamp is not null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { Token.ThrowIfCancellationRequested(); _stamp = null; return false; }
        }

        private Task<(long Schema, long Data, long Changes)> ReadStampAsync() =>
            RunAttemptAsync(transactional: false, async (connection, _, token) =>
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = "SELECT (SELECT schema_version FROM pragma_schema_version), (SELECT data_version FROM pragma_data_version), total_changes()";

                await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false)
                    || reader.GetValue(0) is not long schema || reader.GetValue(1) is not long data || reader.GetValue(2) is not long changes
                    || await reader.ReadAsync(token).ConfigureAwait(false))
                    throw new InvalidOperationException("The observer stamp is unavailable.");

                return (schema, data, changes);
            });

        private async Task<T> RunAttemptAsync<T>(bool transactional, Func<SqliteConnection, SqliteTransaction?, CancellationToken, Task<T>> action)
        {
            SqliteConnection connection = _connection;

            Token.ThrowIfCancellationRequested();

            if (connection.State != System.Data.ConnectionState.Open || !ReferenceEquals(connection.Handle, _handle))
                throw new InvalidOperationException("The observer connection changed.");

            CancellationTokenRegistration interrupt = Token.Register(static handle => SQLitePCL.raw.sqlite3_interrupt((SQLitePCL.sqlite3)handle!), _handle);

            (int ProviderSeconds, int NativeMilliseconds)? timeout = null;

            SqliteTransaction? transaction = null;

            try
            {
                timeout = SqliteBusyRetry.BeginShortAttempt(connection, Token);

                if (transactional) transaction = connection.BeginTransaction(deferred: true);

                T result = await action(connection, transaction, Token).ConfigureAwait(false);

                Token.ThrowIfCancellationRequested();

                return result;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 9 && Token.IsCancellationRequested)
            {
                throw new OperationCanceledException(Token);
            }
            finally
            {
                interrupt.Dispose();

                try
                {
                    if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (timeout is { } original && !SqliteBusyRetry.TryRestoreShortAttempt(connection, original))
                        throw new InvalidOperationException("The observer connection settings could not be restored.");
                }
            }
        }

    }

    internal async Task<bool> DiscoverPendingAsync(CancellationToken ct)
    {
        if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return false;

        await using IGrimoireWorkLease work = acquired;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        try
        {
            // Cohort decisions and stop frontiers remain recovery work even while Guild
            // execution is disabled. They never rebind the creator's work authority.
            bool healthy = await DiscoverCohortsAsync(lifetime.Token).ConfigureAwait(false);

            healthy &= await DiscoverAccountingRecoveryAsync(lifetime.Token).ConfigureAwait(false);

            // Cascade discovery is independent of the live execution task. A parent waiting
            // for its descendants cannot suppress the durable stop work that lets it finish.
            healthy &= await DiscoverCancellationFrontiersAsync(work, lifetime.Token).ConfigureAwait(false);

            Guid? after = null;

            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();

                // Availability includes retained-lock filesystem identity, so its probe belongs
                // inside the admitted scan scope just like the database page it protects.
                if (!IsAvailable) return healthy;

                var page = await ReadPendingIdsWithinAsync(after, lifetime.Token).ConfigureAwait(false);

                if (page.IsFailure) return false;

                if (page.Value.IsEmpty) return healthy;

                foreach (Guid questId in page.Value) Enqueue(questId);

                after = page.Value[^1];
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && work.MaintenanceRevocation.IsCancellationRequested)
        {
            Volatile.Write(ref _ready, 0);

            return false;
        }
    }

    private async Task<bool> DiscoverAccountingRecoveryAsync(CancellationToken ct)
    {
        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

        if (opened.IsFailure) return false;

        List<(Guid Root, Guid Quest)> candidates = [];

        string? last = null;

        int scanned = 0;

        await using (IGrimoireOrdinaryConnectionLease held = opened.Value)
        {
            await using SqliteTransaction transaction = held.Connection.BeginTransaction(deferred: true);

            Result available = await GuildStorageAvailability.CheckAsync(held.Connection, transaction, ct).ConfigureAwait(false);

            if (available.IsFailure) return false;

            await using SqliteCommand command = held.Connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = AccountingRecoveryFrontierQuery;

            command.Parameters.AddWithValue("$after", _accountingScanAfter ?? string.Empty);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                scanned++;

                if (reader.IsDBNull(0) || reader.GetValue(0) is not string id) return false;

                last = id;

                if (reader.GetValue(2) is not long state || state is not (1 or 2)
                    || reader.IsDBNull(1) || !Guid.TryParse(id, out Guid quest) || quest == Guid.Empty
                    || !Guid.TryParse(reader.GetString(1), out Guid root) || root == Guid.Empty) continue;

                candidates.Add((root, quest));
            }
        }

        // Progress is by inspected rows, including blocked and already-settled entries.
        // A restart begins a fresh bounded census; an uninterrupted scan eventually wraps.
        _accountingScanAfter = scanned == 100 ? last : null;

        if (candidates.Count == 0) return true;

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        GuildQuestStore? store = scope.ServiceProvider.GetService<GuildQuestStore>();

        if (store is null) return false;

        bool healthy = true;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if ((await store.RefreshAccountingProjectionAsync(candidate.Root, candidate.Quest, ct).ConfigureAwait(false)).IsFailure)
                healthy = false;
        }

        return healthy;
    }

    private async Task<bool> DiscoverCohortsAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        GuildCohortStore? cohorts = scope.ServiceProvider.GetService<GuildCohortStore>();

        if (cohorts is null) return false;

        GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

        var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

        if (caller.IsFailure) return false;

        GuildCohortWorkCursor? after = null;

        bool healthy = true;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await cohorts.ReadWorkPageAsync(caller.Value, after, ct).ConfigureAwait(false);

            if (page.IsFailure) return false;

            foreach (Guid cohortId in page.Value.CohortIds)
            {
                if ((await cohorts.ReconcileAsync(caller.Value, cohortId, ct).ConfigureAwait(false)).IsFailure) healthy = false;
            }

            if (page.Value.NextAfter is not { } next) return healthy;

            after = next;
        }
    }

    private async Task<bool> DiscoverCancellationFrontiersAsync(IGrimoireWorkLease work, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

        GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

        var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

        if (caller.IsFailure) return false;

        bool healthy = true;

        Guid? after = null;

        while (true)
        {
            var roots = await ReadPendingIdsWithinAsync(after, ct, cancellationFrontiers: true).ConfigureAwait(false);

            if (roots.IsFailure) return false;

            if (roots.Value.IsEmpty) return healthy;

            foreach (Guid root in roots.Value)
            {
                ct.ThrowIfCancellationRequested();

                // Native stop recovery remains available for retired owners and while work
                // admission is disabled. It cannot schedule or rebind a Run.
                await DrainCancellationAsync(work, scope.ServiceProvider, caller.Value, root, ct).ConfigureAwait(false);

                long sequence = 0;

                while (true)
                {
                    var page = await store.ReadCancellationWorkPageAsync(caller.Value, root, sequence, ct).ConfigureAwait(false);

                    if (page.IsFailure) { healthy = false; break; }

                    if (page.Value.IsEmpty)
                    {
                        if ((await store.RefreshCancellationConvergenceAsync(caller.Value, root, ct).ConfigureAwait(false)).IsFailure)
                            healthy = false;

                        break;
                    }

                    foreach (GuildCancellationWork child in page.Value)
                    {
                        GuildCancelQuestRequest request = new(Guid.NewGuid(), child.Revision, GuildCancellationReason.OperatorRequested);

                        var digest = GuildQuestIntent.Compute(child.QuestId, request);

                        if (digest.IsFailure) { healthy = false; continue; }

                        // Persist the ordinary receipt before any port call. Revision races are
                        // retried by the next scan; accepted children disappear from work pages.
                        var accepted = await store.RequestCancellationAsync(caller.Value,
                            new(request.IdempotencyKey, digest.Value), child.QuestId, request, ct).ConfigureAwait(false);

                        if (accepted.IsSuccess) Enqueue(child.QuestId);
                        else healthy = false;
                    }

                    sequence = page.Value[^1].IssuedSequence;
                }
            }

            after = roots.Value[^1];
        }
    }

    private async Task<bool> RequestDisabledCancellationAsync(CancellationToken ct)
    {
        CancellationToken callerCancellation = ct;

        try
        {
            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            {
                Volatile.Write(ref _ready, 0);

                return false;
            }

            await using IGrimoireWorkLease work = acquired;

            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

            ct = lifetime.Token;

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();

            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

            if (caller.IsFailure) return false;

            Guid? after = null;

            bool healthy = true;

            while (!settings.CurrentValue.Features.Guild)
            {
                var page = await ReadPendingIdsAsync(after, ct).ConfigureAwait(false);

                if (page.IsFailure) return false;

                if (page.Value.IsEmpty) return healthy;

                foreach (Guid questId in page.Value)
                {
                    if (settings.CurrentValue.Features.Guild) return false;

                    var found = await store.GetAsync(caller.Value, questId, ct).ConfigureAwait(false);

                    if (found.IsFailure) { healthy = false; continue; }

                    if (found.Value.Quest.ResultReceiptId is not null) continue;

                    if (!found.Value.Quest.CancellationRequested)
                    {
                        GuildCancelQuestRequest request = new(Guid.NewGuid(), found.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

                        var digest = GuildQuestIntent.Compute(questId, request);

                        if (digest.IsFailure) { healthy = false; continue; }

                        // Revision conflicts are retried from the next durable scan; accepted flags suppress duplicate receipts.
                        var accepted = await store.RequestCancellationAsync(caller.Value,
                            new(request.IdempotencyKey, digest.Value), questId, request, ct).ConfigureAwait(false);

                        if (accepted.IsFailure) { healthy = false; continue; }
                    }

                    await DrainCancellationAsync(work, scope.ServiceProvider, caller.Value, questId, ct).ConfigureAwait(false);
                }

                after = page.Value[^1];
            }

            return false;
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref _ready, 0);

            return false;
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Guild disabled-work cancellation is unavailable ({ExceptionType}).", exception.GetType().Name);

            return false;
        }
    }

    private static bool CanDrive(ArcanumSettings current) => GuildLocalExecutionAdmission.IsAvailable(current)
        || GuildRemotePort.IsTransportEnabled(current);

    private async Task<bool> RefreshReadinessAsync(CancellationToken ct)
    {
        if (!CanDrive(settings.CurrentValue) || Volatile.Read(ref _stopping) != 0)
        {
            Volatile.Write(ref _ready, 0);

            return Volatile.Read(ref _stopping) == 0;
        }

        CancellationToken callerCancellation = ct;

        try
        {
            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            {
                Volatile.Write(ref _ready, 0);

                return false;
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

                return false;
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();

            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            IGuildLocalInferenceRuntime runtime = scope.ServiceProvider.GetRequiredService<IGuildLocalInferenceRuntime>();

            _ = scope.ServiceProvider.GetRequiredService<IGuildLocalExecutionEngine>();

            _ = scope.ServiceProvider.GetRequiredService<GuildLocalPort>();

            if ((!runtime.IsAvailable || !store.HasLocalExecutionAdmission) && !GuildRemotePort.IsTransportEnabled(settings.CurrentValue))
            {
                Volatile.Write(ref _ready, 0);

                return false;
            }

            var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

            var inventory = caller.IsSuccess ? await ReadPendingIdsAsync(null, ct).ConfigureAwait(false)
                : Result<ImmutableArray<Guid>>.Failure(caller.Error);

            Volatile.Write(ref _ready, inventory.IsSuccess ? 1 : 0);

            return inventory.IsSuccess;
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref _ready, 0);

            return false;
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _ready, 0);

            logger.LogDebug("Guild scheduler readiness is unavailable ({ExceptionType}).", exception.GetType().Name);

            return false;
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
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        ct = lifetime.Token;

        ct.ThrowIfCancellationRequested();

        if (!IsAvailable) return;

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

        GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

        var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

        if (caller.IsFailure) return;

        var pending = await store.ReadLocalPendingAsync(caller.Value, questId, ct).ConfigureAwait(false);

        if (pending.IsFailure) return;

        if (pending.Value.IsRemote)
        {
            await RunRemoteAdmittedAsync(work, scope.ServiceProvider, caller.Value, pending.Value, ct).ConfigureAwait(false);

            return;
        }

        await RunLocalAdmittedAsync(work, scope.ServiceProvider, caller.Value, pending.Value, ct).ConfigureAwait(false);
    }

    private async Task RunLocalAdmittedAsync(IGrimoireWorkLease work, IServiceProvider services, GuildCallerContext caller,
        GuildLocalPendingWork pending, CancellationToken ct)
    {
        work.MaintenanceRevocation.ThrowIfCancellationRequested();

        GuildQuestStore store = services.GetRequiredService<GuildQuestStore>();

        Guid questId = pending.Quest.Quest.Id;

        if (!pending.Quest.Quest.CancellationRequested && pending.RequiresAttention)
        {
            var frontier = await store.ReadLocalCancellationFrontierAsync(caller, questId, ct).ConfigureAwait(false);

            if (frontier.IsFailure) return;

            if (frontier.Value.AncestorCanceled && frontier.Value.Quest.Quest.ResultReceiptId is null)
            {
                GuildCancelQuestRequest stop = new(Guid.NewGuid(), frontier.Value.Quest.Quest.Revision,
                    GuildCancellationReason.OperatorRequested);

                var digest = GuildQuestIntent.Compute(questId, stop);

                if (digest.IsFailure) return;

                var accepted = await store.RequestCancellationAsync(caller,
                    new(stop.IdempotencyKey, digest.Value), questId, stop, ct).ConfigureAwait(false);

                if (accepted.IsFailure) return;

                await DrainCancellationAsync(work, services, caller, questId, ct).ConfigureAwait(false);

                return;
            }
        }

        if (pending.Quest.Quest.CancellationRequested)
        {
            await DrainCancellationAsync(work, services, caller, questId, ct).ConfigureAwait(false);

            return;
        }

        if (pending.RequiresAttention || pending.Request is null || !IsAvailable) return;

        await RunClaimedLocalAdmittedAsync(work, services, caller, pending, ct).ConfigureAwait(false);
    }

    private async Task RunClaimedLocalAdmittedAsync(IGrimoireWorkLease work, IServiceProvider services, GuildCallerContext caller,
        GuildLocalPendingWork pending, CancellationToken ct)
    {
        work.MaintenanceRevocation.ThrowIfCancellationRequested();

        GuildQuestDto quest = pending.Quest.Quest;

        // Claim before a rebind can rotate the credential held by an already active local run.
        if (!ownership.TryClaim(quest.OperationId, out Guid claim)) return;

        try
        {
            GuildLocalHostPolicy policy = services.GetRequiredService<GuildLocalHostPolicy>();

            var recovery = await policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, ct).ConfigureAwait(false);

            if (recovery.IsFailure) return;

            var rebound = await services.GetRequiredService<SignetAuthority>().RebindAsync(recovery.Value, ct).ConfigureAwait(false);

            if (rebound.IsFailure) return;

            var prepared = await services.GetRequiredService<GuildQuestStore>().PrepareLocalScheduleAsync(
                caller, rebound.Value, pending.Operation.Revision, ct).ConfigureAwait(false);

            if (prepared.IsFailure || !IsAvailable) return;

            await RunLocalPortAdmittedAsync(work, services, rebound.Value, prepared.Value, claim, ct).ConfigureAwait(false);
        }
        finally
        {
            ownership.Release(quest.OperationId, claim);
        }
    }

    private async Task RunLocalPortAdmittedAsync(IGrimoireWorkLease work, IServiceProvider services,
        GuildExecutionHandle execution, GuildDispatchRequest request, Guid claim, CancellationToken ct)
    {
        work.MaintenanceRevocation.ThrowIfCancellationRequested();

        _ = await services.GetRequiredService<GuildLocalPort>().DispatchClaimedAsync(work, this, execution, request, claim, ct).ConfigureAwait(false);
    }

    internal async Task<Result<GuildDispatchObservation>> RunLocalCompletionAdmittedAsync(IGrimoireWorkLease work,
        GuildLocalPort port, GuildLocalPort.LocalRun running, GuildLocalPortState before, GuildDispatchRequest request,
        Result<PromptTurnResult> result, CancellationToken ct)
    {
        return await port.CompleteAsync(running, before, request, result, ct).ConfigureAwait(false);
    }

    private async Task RunRemoteAdmittedAsync(IGrimoireWorkLease work, IServiceProvider services, GuildCallerContext caller,
        GuildLocalPendingWork pending, CancellationToken ct)
    {
        work.MaintenanceRevocation.ThrowIfCancellationRequested();

        GuildQuestStore store = services.GetRequiredService<GuildQuestStore>();

        Guid questId = pending.Quest.Quest.Id;

        if (pending.Quest.Quest.ResultReceiptId is not null)
        {
            _ = await services.GetRequiredService<GuildRemotePort>().ReconcileTerminalPortalAsync(caller, questId, ct).ConfigureAwait(false);

            return;
        }

        if (pending.IsRemote && !pending.Quest.Quest.CancellationRequested)
        {
            var denied = await store.RequiresRemoteCancellationAsync(caller, questId, ct).ConfigureAwait(false);

            if (denied.IsFailure) return;

            if (denied.Value)
            {
                GuildCancelQuestRequest stop = new(Guid.NewGuid(), pending.Quest.Quest.Revision,
                    GuildCancellationReason.OperatorRequested);

                var digest = GuildQuestIntent.Compute(questId, stop);

                if (digest.IsFailure) return;

                var accepted = await store.RequestCancellationAsync(caller, new(stop.IdempotencyKey, digest.Value), questId, stop, ct).ConfigureAwait(false);

                if (accepted.IsFailure) return;

                await DrainCancellationAsync(work, services, caller, questId, ct).ConfigureAwait(false);

                return;
            }
        }

        if (pending.Quest.Quest.CancellationRequested)
        {
            await DrainCancellationAsync(work, services, caller, questId, ct).ConfigureAwait(false);

            return;
        }

        if (pending.RequiresAttention || pending.Request is null || !IsAvailable) return;

        GuildQuestDto quest = pending.Quest.Quest;

        if (!ownership.TryClaim(quest.OperationId, out Guid claim)) return;

        try
        {
            GuildLocalHostPolicy policy = services.GetRequiredService<GuildLocalHostPolicy>();

            var recovery = await policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, ct).ConfigureAwait(false);

            if (recovery.IsFailure) return;

            var rebound = await services.GetRequiredService<SignetAuthority>().RebindAsync(recovery.Value, ct).ConfigureAwait(false);

            if (rebound.IsFailure) return;

            var prepared = await store.PrepareLocalScheduleAsync(caller, rebound.Value, pending.Operation.Revision, ct).ConfigureAwait(false);

            if (prepared.IsFailure || !IsAvailable) return;

            _ = await services.GetRequiredService<GuildRemotePort>().DispatchClaimedAsync(rebound.Value, prepared.Value, claim, ct).ConfigureAwait(false);
        }
        finally { ownership.Release(quest.OperationId, claim); }
    }

    private async Task DrainCancellationAsync(IGrimoireWorkLease work, IServiceProvider services, GuildCallerContext caller, Guid questId, CancellationToken ct)
    {
        work.MaintenanceRevocation.ThrowIfCancellationRequested();

        GuildQuestStore store = services.GetRequiredService<GuildQuestStore>();

        var cleanup = await store.CreateLocalCleanupDriverAsync(caller, questId, ct).ConfigureAwait(false);

        if (cleanup.IsSuccess)
        {
            var state = await store.ReadLocalPortAsync(cleanup.Value, questId, ct).ConfigureAwait(false);

            if (state.IsFailure || state.Value.Operation.LeaseOwner is not null
                && state.Value.Operation.LeaseExpiresAt > time.GetUtcNow()) return;

            var children = await store.LocalChildrenStoppedAsync(cleanup.Value, ct).ConfigureAwait(false);

            if (children.IsFailure || !children.Value) return;

            var identity = await store.ReadLocalCleanupStopIdentityAsync(cleanup.Value, ct).ConfigureAwait(false);

            if (identity.IsFailure || !ownership.TryClaim(state.Value.Operation.Id, out Guid localClaim)) return;

            try
            {
                _ = await services.GetRequiredService<GuildLocalPort>().RequestCancellationOwnedAsync(caller,
                    identity.Value, questId, questId, localClaim, ct).ConfigureAwait(false);
            }
            finally { ownership.Release(state.Value.Operation.Id, localClaim); }

            return;
        }

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

            if (pending.Value.IsRemote)
            {
                _ = await services.GetRequiredService<GuildRemotePort>().RequestCancellationOwnedAsync(caller,
                    mutation.Value, questId, questId, claim, ct).ConfigureAwait(false);

                return;
            }

            _ = await services.GetRequiredService<GuildLocalPort>().RequestCancellationOwnedAsync(caller,
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
                LEFT JOIN guild_local_executions binding ON binding.QuestId=quest.Id
            LEFT JOIN guild_remote_executions remote ON remote.QuestId=quest.Id
            WHERE (binding.QuestId IS NOT NULL OR remote.QuestId IS NOT NULL)
                AND (NOT EXISTS(SELECT 1 FROM guild_result_receipts receipt WHERE receipt.QuestId=quest.Id)
                    OR remote.QuestId IS NOT NULL AND EXISTS(
                        SELECT 1 FROM guild_remote_portal_deliveries delivery JOIN guild_inbox item ON item.Id=delivery.InboxId
                        WHERE delivery.RecipientQuestId=quest.Id AND delivery.TransportStateCode IN (1,2)
                            AND item.DeliveryStateCode=1))
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
