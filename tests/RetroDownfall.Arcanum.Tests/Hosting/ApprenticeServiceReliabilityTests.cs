using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

[Collection("ApprenticeReliability")]
public sealed class ApprenticeServiceReliabilityTests
{

    // P8: intervene-and-resume at full capacity must acquire the slot FIRST and
    // return MaxReached with NO state mutation (no plan/checkpoint/publish).

    [Fact]
    public async Task Intervene_ResumeAtCapacity_ReturnsCapacityFailureWithNoStateMutation()
    {

        Guid escalade = Guid.NewGuid();

        string originalPlan = ApprenticeRepository.SerializePlan(
        [
            new PlanStep { Index = 1, Description = "Stuck step", Status = "failed", Attempts = 3 },
        ]);

        Apprentice apprentice = new()
        {

            Id = escalade,

            Name = "Reliability-1",

            Goal = "Survive a capacity-rejected resume.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Escalated.ToString(),

            Plan = originalPlan,

            CurrentStep = 0,

            CheckpointData = null,

            ErrorMessage = "Need Divine Intervention.",

        };

        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new()
        {

            Apprentices = new ApprenticeSettings
            {

                Enabled = true,

                MaxConcurrentApprentices = 1,

                MaxPendingStarts = 1,

            },

        };

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger);

        // Pre-fill the concurrency gate so the intervene resume cannot acquire a slot.

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out _));

        Result<string> result = await service
            .InterveneAsync(escalade, "Resume the quest.", resume: true, CancellationToken.None)
;

        Assert.True(result.IsFailure);

        Assert.Equal("Apprentice.MaxReached", result.Error.Code);

        Apprentice persisted = repo.Get(escalade);

        Assert.Equal(ApprenticeStatus.Escalated.ToString(), persisted.Status);

        Assert.Equal(originalPlan, persisted.Plan);

        Assert.Null(persisted.CheckpointData);

        Assert.Equal("Need Divine Intervention.", persisted.ErrorMessage);

    }

    // W2.4 Fix 2: crash recovery must not silently drop a resumable apprentice
    // when the concurrency gate AND the pending queue are full. It must emit a
    // warning (with id + failure code) so operators get a signal.

    [Fact]
    public async Task ResumeCrashRecovery_WhenGateAndQueueFull_LogsWarning()
    {

        Guid resumable = Guid.NewGuid();

        Apprentice apprentice = new()
        {

            Id = resumable,

            Name = "Reliability-2",

            Goal = "Be surfaced when recovery cannot resume.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Running.ToString(),

            Plan = "[]",

            CurrentStep = 0,

            CheckpointData = null,

        };

        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new()
        {

            Apprentices = new ApprenticeSettings
            {

                Enabled = true,

                MaxConcurrentApprentices = 1,

                MaxPendingStarts = 1,

            },

        };

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger);

        // Pre-fill the concurrency gate and the pending queue so the resumable
        // apprentice hits the PendingQueueFull branch of TryAcquireExecutionSlot.

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out _));

        GetPendingStarts(service).Enqueue(Guid.NewGuid());

        await InvokeResumeCrashRecoveryAsync(service, CancellationToken.None);

        LogEntry? warning = logger.Entries.FirstOrDefault(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(resumable.ToString(), StringComparison.Ordinal)
            && e.Message.Contains("PendingQueueFull", StringComparison.Ordinal));

        Assert.NotNull(warning);

    }

    // W2.4 Fix 1 (P2): an apprentice queued in _pendingStarts (status Idle, which
    // IsCancellable excludes) must be cancellable. CancelAsync must drain it from
    // the pending queue and mark it Cancelled (was: not cancellable, stayed queued).

    [Fact]
    public async Task CancelAsync_ApprenticePendingInQueue_DrainsAndMarksCancelled()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {

            Id = apprenticeId,

            Name = "Reliability-Fix1",

            Goal = "Be cancellable while pending in the start queue.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Idle.ToString(),

            Plan = "[]",

            CurrentStep = 0,

            CheckpointData = null,

        };

        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new()
        {

            Apprentices = new ApprenticeSettings
            {

                Enabled = true,

                MaxConcurrentApprentices = 1,

                MaxPendingStarts = 1,

            },

        };

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger);

        // Pre-fill the gate so StartAsync cannot acquire a slot and enqueues instead.

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out _));

        _ = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.Contains(apprenticeId, GetPendingStarts(service));

        Result<string> result = await service.CancelAsync(apprenticeId, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.DoesNotContain(apprenticeId, GetPendingStarts(service));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), persisted.Status);

    }

    // W2.4 Fix 2 (P2): StartAsync at capacity with queue space must return SUCCESS
    // (the apprentice is queued) rather than Failure("Apprentice.MaxReached"). No
    // execution task is started yet; the id sits in _pendingStarts and not in
    // _activeTasks.

    [Fact]
    public async Task StartAsync_AtCapacity_WithQueueSpace_ReturnsSuccessAndQueues()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {

            Id = apprenticeId,

            Name = "Reliability-Fix2",

            Goal = "Survive being queued on capacity.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Idle.ToString(),

            Plan = "[]",

            CurrentStep = 0,

            CheckpointData = null,

        };

        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new()
        {

            Apprentices = new ApprenticeSettings
            {

                Enabled = true,

                MaxConcurrentApprentices = 1,

                MaxPendingStarts = 1,

            },

        };

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger);

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out _));

        Result<string> result = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Contains(apprenticeId, GetPendingStarts(service));

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

    }

    // W2.4 Fix 3 (P2): a non-cancel exception in ONE Simulacrum branch must NOT
    // fail the whole Task.WhenAll. RunSimulacrumBranchAsync must catch the
    // exception and return a Terminal SingleStepResult (was: exception escaped,
    // faulting the whole group after siblings may have run tools).

    [Fact]
    public async Task RunSimulacrumBranchAsync_NonCancelException_ReturnsTerminalNotThrows()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {

            Id = apprenticeId,

            Name = "Reliability-Fix3",

            Goal = "Isolate a single branch fault.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Running.ToString(),

            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "Faulty branch" },
            ]),

            CurrentStep = 0,

            SessionId = Guid.NewGuid(),

        };

        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new()
        {

            Apprentices = new ApprenticeSettings
            {

                Enabled = true,

                MaxConcurrentApprentices = 1,

                MaxPendingStarts = 1,

            },

        };

        CapturingLogger<ApprenticeService> logger = new();

        ThrowingIntelligenceProvider intelligence = new();

        ApprenticeService service = CreateService(repo, settings, logger, intelligence);

        using SemaphoreSlim gate = new(1, 1);

        using CancellationTokenSource linkedCts = new();

        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("RunSimulacrumBranchAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task task = (Task)method!.Invoke(service, new object[]
        {

            gate,

            apprentice,

            plan,

            0,

            settings.Apprentices!,

            1,

            0,

            1,

            60,

            apprenticeId,

            linkedCts,

        })!;

        // RED: awaiting a faulted task throws InvalidOperationException.
        // GREEN: the branch returns a Terminal SingleStepResult.

        await task.WaitAsync(TimeSpan.FromSeconds(15));

        object? result = task.GetType().GetProperty("Result")!.GetValue(task);

        Assert.NotNull(result);

        object? kind = result!.GetType().GetProperty("Kind")!.GetValue(result);

        Assert.NotNull(kind);

        Assert.Equal("Terminal", kind!.ToString());

    }

    // W2.P1-2: a late OperationCanceledException handler from a prior generation must not
    // persist Paused over a newer Resume that already incremented the execution generation.

    [Fact]
    public async Task RunApprenticeAsync_StaleGeneration_DoesNotPersistPausedOverNewerResume()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {

            Id = apprenticeId,

            Name = "Reliability-P1-2",

            Goal = "Stale cancel must not clobber resume.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Running.ToString(),

            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 1, Description = "Step cancelled by stale generation" },
            ]),

            CurrentStep = 0,

            SessionId = Guid.NewGuid(),

        };

        OceOnFirstGetApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new()
        {

            Apprentices = new ApprenticeSettings
            {

                Enabled = true,

                MaxConcurrentApprentices = 1,

                MaxPendingStarts = 1,

            },

        };

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger, new FailingPlanIntelligence(), new NotImplementedGrimoireRepository());

        // Stale run owns generation 1; a newer resume already advanced to generation 2.
        SeedExecutionGeneration(service, apprenticeId, 2L);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("RunApprenticeAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task runTask = (Task)method!.Invoke(service, new object[] { apprenticeId, 1L })!;

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Apprentice persisted = repo.Get(apprenticeId);

        // OceOnFirstGet would normally drive Paused via the cancel handler; ownership mismatch
        // must leave the status alone (still Running as left by the newer resume).
        Assert.Equal(ApprenticeStatus.Running.ToString(), persisted.Status);

    }

    // W2.4 Fix 4 (P2): on linked-CTS cancellation (e.g. host StopAsync) the main
    // execution loop's catch(OperationCanceledException) must persist an
    // intermediate status so a later resume does not re-run the in-progress step.
    // If the status is still Running/Planning, persist Paused (was: swallowed with
    // no persist, apprentice left Running and re-ran the partial step on resume).

    [Fact]
    public async Task RunApprenticeAsync_OperationCanceled_PersistsPausedNotRunning()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {

            Id = apprenticeId,

            Name = "Reliability-Fix4",

            Goal = "Persist Paused on cancel.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Running.ToString(),

            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 1, Description = "Step that will be cancelled mid-run" },
            ]),

            CurrentStep = 0,

            SessionId = Guid.NewGuid(),

        };

        OceOnFirstGetApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new()
        {

            Apprentices = new ApprenticeSettings
            {

                Enabled = true,

                MaxConcurrentApprentices = 1,

                MaxPendingStarts = 1,

            },

        };

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger, new FailingPlanIntelligence(), new NotImplementedGrimoireRepository());

        SeedExecutionGeneration(service, apprenticeId, 1L);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("RunApprenticeAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task runTask = (Task)method!.Invoke(service, new object[] { apprenticeId, 1L })!;

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), persisted.Status);

    }

    // W2.4 Fix 5 (P2): a Planning apprentice resumed after a host restart must
    // emit ApprenticeResumed on the chronicle hub, not a duplicate
    // ApprenticeStarted (was: the recovery path always emitted ApprenticeStarted
    // for any non-Running/Paused status, including Planning).

    [Fact]
    public async Task RunApprenticeAsync_PlanningResumeAfterRestart_EmitsApprenticeResumedNotStarted()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {

            Id = apprenticeId,

            Name = "Reliability-Fix5",

            Goal = "Emit ApprenticeResumed on restart resume.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Planning.ToString(),

            Plan = "[]",

            CurrentStep = 0,

            SessionId = null,

        };

        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new()
        {

            Apprentices = new ApprenticeSettings
            {

                Enabled = true,

                MaxConcurrentApprentices = 1,

                MaxPendingStarts = 1,

            },

        };

        CapturingLogger<ApprenticeService> logger = new();

        FailingPlanIntelligence intelligence = new();

        NotImplementedGrimoireRepository grimoire = new();

        ChronicleHub hub = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        ApprenticeService service = CreateService(repo, settings, logger, intelligence, grimoire, hub);

        List<ApprenticeEvent> captured = new();

        using CancellationTokenSource subscribeCts = new(TimeSpan.FromSeconds(10));

        TaskCompletionSource primed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Call the collector directly (not Task.Run): an async method runs
        // synchronously up to its first await-that-yields, which here is the
        // channel MoveNextAsync AFTER the subscription is registered. So by the
        // time CollectChronicleAsync returns its Task, the channel is live and a
        // publish will be delivered (no Task.Run scheduling race).

        Task collector = CollectChronicleAsync(
            service.SubscribeChronicleAsync(apprenticeId, subscribeCts.Token),
            captured,
            primed,
            subscribeCts.Token);

        // Prime the subscription with a sentinel so we know the collector is
        // listening before RunApprenticeAsync publishes its first event.

        hub.Publish(apprenticeId, new ApprenticeEvent
        {

            Type = ApprenticeEventType.EventsDropped,

            ApprenticeId = apprenticeId,

            Timestamp = DateTimeOffset.UtcNow,

            Summary = "prime",

        });

        await primed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (captured)
        {

            captured.Clear();

        }

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("RunApprenticeAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        SeedExecutionGeneration(service, apprenticeId, 1L);

        Task runTask = (Task)method!.Invoke(service, new object[] { apprenticeId, 1L })!;

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        subscribeCts.Cancel();

        try
        {

            await collector.WaitAsync(TimeSpan.FromSeconds(5));

        }

        catch (OperationCanceledException)
        {

        }

        List<ApprenticeEvent> events;

        lock (captured)
        {

            events = captured.ToList();

        }

        Assert.DoesNotContain(events, e => e.Type == ApprenticeEventType.ApprenticeStarted);

        Assert.Contains(events, e => e.Type == ApprenticeEventType.ApprenticeResumed);

    }

    private static ApprenticeService CreateService(
        InMemoryApprenticeRepository repo,
        ArcanumSettings settings,
        ILogger<ApprenticeService> logger,
        IArcanumIntelligenceProvider? intelligence = null,
        IGrimoireRepository? grimoire = null,
        ChronicleHub? hub = null)
    {

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        ChronicleHub resolvedHub = hub ?? new(options);

        SingleServiceScopeFactory scopeFactory = new(repo, intelligence, grimoire);

        return new ApprenticeService(scopeFactory, options, resolvedHub, logger);

    }

    private static ApprenticeConcurrencyGate GetConcurrencyGate(ApprenticeService service)
    {

        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_concurrencyGate", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return (ApprenticeConcurrencyGate)field!.GetValue(service)!;

    }

    private static ConcurrentQueue<Guid> GetPendingStarts(ApprenticeService service)
    {

        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_pendingStarts", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return (ConcurrentQueue<Guid>)field!.GetValue(service)!;

    }

    private static ConcurrentDictionary<Guid, Task> GetActiveTasks(ApprenticeService service)
    {

        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_activeTasks", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return (ConcurrentDictionary<Guid, Task>)field!.GetValue(service)!;

    }

    private static void SeedExecutionGeneration(ApprenticeService service, Guid apprenticeId, long generation)
    {

        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_executionGenerations", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        ConcurrentDictionary<Guid, long> generations =
            (ConcurrentDictionary<Guid, long>)field!.GetValue(service)!;

        generations[apprenticeId] = generation;

    }

    private static async Task InvokeResumeCrashRecoveryAsync(ApprenticeService service, CancellationToken cancellationToken)
    {

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("ResumeCrashRecoveryAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task? task = (Task?)method!.Invoke(service, new object?[] { cancellationToken });

        Assert.NotNull(task);

        await task!;

    }

    private static async Task CollectChronicleAsync(
        IAsyncEnumerable<ApprenticeEvent> events,
        List<ApprenticeEvent> captured,
        TaskCompletionSource primed,
        CancellationToken cancellationToken)
    {

        await foreach (ApprenticeEvent e in events.ConfigureAwait(false))
        {

            lock (captured)
            {

                captured.Add(e);

            }

            primed.TrySetResult();

            if (e.Type == ApprenticeEventType.ApprenticeFailed)
            {

                break;

            }

            if (cancellationToken.IsCancellationRequested)
            {

                break;

            }

        }

    }

    private sealed class SingleServiceScopeFactory : IServiceScopeFactory
    {

        private readonly IServiceProvider _provider;

        public SingleServiceScopeFactory(
            IApprenticeRepository repo,
            IArcanumIntelligenceProvider? intelligence = null,
            IGrimoireRepository? grimoire = null)
        {

            _provider = new SingleServiceProvider(repo, intelligence, grimoire);

        }

        public IServiceScope CreateScope() => new SingleScope(_provider);

        private sealed class SingleScope : IServiceScope
        {

            public SingleScope(IServiceProvider provider)
            {

                ServiceProvider = provider;

            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose()
            {

            }

        }

        private sealed class SingleServiceProvider : IServiceProvider
        {

            private readonly IApprenticeRepository _repo;

            private readonly IArcanumIntelligenceProvider? _intelligence;

            private readonly IGrimoireRepository? _grimoire;

            public SingleServiceProvider(
                IApprenticeRepository repo,
                IArcanumIntelligenceProvider? intelligence,
                IGrimoireRepository? grimoire)
            {

                _repo = repo;

                _intelligence = intelligence;

                _grimoire = grimoire;

            }

            public object? GetService(Type serviceType)
            {

                if (serviceType == typeof(IApprenticeRepository))
                {

                    return _repo;

                }

                if (serviceType == typeof(IArcanumIntelligenceProvider) && _intelligence is not null)
                {

                    return _intelligence;

                }

                if (serviceType == typeof(IGrimoireRepository) && _grimoire is not null)
                {

                    return _grimoire;

                }

                return null;

            }

        }

    }

    private class InMemoryApprenticeRepository : IApprenticeRepository
    {

        private readonly Dictionary<Guid, Apprentice> _store = new();

        public InMemoryApprenticeRepository(params Apprentice[] apprentices)
        {

            foreach (Apprentice apprentice in apprentices)
            {

                _store[apprentice.Id] = apprentice;

            }

        }

        public Apprentice Get(Guid id)
        {

            return _store[id];

        }

        public virtual Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {

            Apprentice? value = _store.TryGetValue(id, out Apprentice? found) ? found : null;

            return Task.FromResult(value);

        }

        public Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
        {

            _store[apprentice.Id] = apprentice;

            return Task.FromResult(apprentice);

        }

        public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default)
        {

            string running = ApprenticeStatus.Running.ToString();

            IReadOnlyList<Apprentice> values = _store.Values
                .Where(a => string.Equals(a.Status, running, StringComparison.Ordinal))
                .ToList();

            return Task.FromResult(values);

        }

        public Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default)
        {

            string planning = ApprenticeStatus.Planning.ToString();

            IReadOnlyList<Apprentice> values = _store.Values
                .Where(a => string.Equals(a.Status, planning, StringComparison.Ordinal))
                .ToList();

            return Task.FromResult(values);

        }

        public Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
        {

            _store[apprentice.Id] = apprentice;

            return Task.FromResult(apprentice);

        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {

            return Task.FromResult(_store.Remove(id));

        }

        public Task<ListPageResult<Apprentice>> ListAsync(
            Guid? campaignId,
            string? status,
            int? limit = null,
            DateTimeOffset? beforeUpdatedAt = null,
            CancellationToken cancellationToken = default)
        {

            throw new NotImplementedException();

        }

    }

    private sealed class CapturingLogger<TCategory> : ILogger<TCategory>
    {

        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {

            get
            {

                lock (_entries)
                {

                    return _entries.ToList();

                }

            }

        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {

            string message = formatter(state, exception);

            lock (_entries)
            {

                _entries.Add(new LogEntry(logLevel, message, exception));

            }

        }

        private sealed class NoopDisposable : IDisposable
        {

            public static readonly NoopDisposable Instance = new();

            public void Dispose()
            {

            }

        }

    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    // Throws OperationCanceledException on the FIRST GetByIdAsync call (simulating
    // a cancelled DB read during host shutdown), then delegates to the in-memory
    // store for subsequent calls so the Fix 4 persistence path can read/update.

    private sealed class OceOnFirstGetApprenticeRepository : InMemoryApprenticeRepository
    {

        private int _calls;

        public OceOnFirstGetApprenticeRepository(params Apprentice[] apprentices) : base(apprentices)
        {

        }

        public override Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {

            if (Interlocked.Increment(ref _calls) == 1)
            {

                throw new OperationCanceledException("Simulated host shutdown cancellation.");

            }

            return base.GetByIdAsync(id, cancellationToken);

        }

    }

    // StreamPromptAsync throws a non-cancel exception to inject a Simulacrum
    // branch fault (Fix 3). ExecutePromptAsync is unused by the Fix 3 test.

    private sealed class ThrowingIntelligenceProvider : IArcanumIntelligenceProvider
    {

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null) =>
            throw new InvalidOperationException("Branch fault injection.");

    }

    // ExecutePromptAsync returns Failure so RunApprenticeAsync's plan-generation
    // path fails fast and exits via FailApprenticeAsync (Fix 5). StreamPromptAsync
    // is unused by the Fix 5 test.

    private sealed class FailingPlanIntelligence : IArcanumIntelligenceProvider
    {

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null)
        {

            return Task.FromResult(
                Result<PromptTurnResult>.Failure(new Error("Plan.Failed", "Plan generation failed.")));

        }

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null) =>
            throw new NotImplementedException();

    }

    // Minimal IGrimoireRepository stub: every method throws NotImplementedException.
    // RunApprenticeAsync resolves the grimoire from the scope before plan
    // generation, but the failing-plan path returns before any grimoire call, so
    // the stub just needs to exist.

    private sealed class NotImplementedGrimoireRepository : IGrimoireRepository
    {

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId, string prompt, string model, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task FinalizeAssistantEntryAsync(
            Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DiscardAssistantEntryAsync(
            Guid assistantEntryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AppendToolInteractionAsync(
            Guid sessionId, string toolName, string arguments, string result, string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveCompletedExchangeAsync(
            string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
            Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
            Guid sessionId, int takeLast, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(
            Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
            int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(
            Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAsync(
            Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAndCostAsync(
            Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task UpdateSessionCampaignRollupAsync(
            Guid sessionId, string summary, DateTime lastSummarizedMessageAt,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync(
            int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(
            string workspacePath, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

    }

}
