using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class ApprenticeServiceReliabilityTests
{

    // W2.4 Fix 1: an intervene-and-resume at full capacity must NOT leave the
    // apprentice persisted as Running. It must acquire the slot first and, on
    // capacity failure, revert to its prior Escalated state before returning.

    [Fact]
    public async Task Intervene_ResumeAtCapacity_RevertsToEscalatedAndReturnsCapacityFailure()
    {

        Guid escalade = Guid.NewGuid();

        Apprentice apprentice = new()
        {

            Id = escalade,

            Name = "Reliability-1",

            Goal = "Survive a capacity-rejected resume.",

            WorkspacePath = "/tmp/arcanum-test",

            Status = ApprenticeStatus.Escalated.ToString(),

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

        // Pre-fill the concurrency gate so the intervene resume cannot acquire a slot.

        GetConcurrencyGate(service).TryAcquire(1);

        Result<string> result = await service
            .InterveneAsync(escalade, "Resume the quest.", resume: true, CancellationToken.None)
;

        Assert.True(result.IsFailure);

        Assert.Equal("Apprentice.MaxReached", result.Error.Code);

        Apprentice persisted = repo.Get(escalade);

        Assert.Equal(ApprenticeStatus.Escalated.ToString(), persisted.Status);

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

        GetConcurrencyGate(service).TryAcquire(1);

        GetPendingStarts(service).Enqueue(Guid.NewGuid());

        await InvokeResumeCrashRecoveryAsync(service, CancellationToken.None);

        LogEntry? warning = logger.Entries.FirstOrDefault(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(resumable.ToString(), StringComparison.Ordinal)
            && e.Message.Contains("PendingQueueFull", StringComparison.Ordinal));

        Assert.NotNull(warning);

    }

    private static ApprenticeService CreateService(
        InMemoryApprenticeRepository repo,
        ArcanumSettings settings,
        ILogger<ApprenticeService> logger)
    {

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        ChronicleHub hub = new(options);

        SingleServiceScopeFactory scopeFactory = new(repo);

        return new ApprenticeService(scopeFactory, options, hub, logger);

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

    private static async Task InvokeResumeCrashRecoveryAsync(ApprenticeService service, CancellationToken cancellationToken)
    {

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("ResumeCrashRecoveryAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task? task = (Task?)method!.Invoke(service, new object?[] { cancellationToken });

        Assert.NotNull(task);

        await task!;

    }

    private sealed class SingleServiceScopeFactory : IServiceScopeFactory
    {

        private readonly IServiceProvider _provider;

        public SingleServiceScopeFactory(IApprenticeRepository repo)
        {

            _provider = new SingleServiceProvider(repo);

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

            public SingleServiceProvider(IApprenticeRepository repo)
            {

                _repo = repo;

            }

            public object? GetService(Type serviceType)
            {

                if (serviceType == typeof(IApprenticeRepository))
                {

                    return _repo;

                }

                return null;

            }

        }

    }

    private sealed class InMemoryApprenticeRepository : IApprenticeRepository
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

        public Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
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

}
