using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

internal sealed class UnseenServantPacerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    internal UnseenServantPacer Pacer { get; }

    internal UnseenServantJob Job { get; }

    internal Store Watermarks { get; } = new();

    internal EventBus Events { get; } = new();

    internal TestCapturingLogger<UnseenServantPacer> Logger { get; } = new();

    internal int AsyncDisposals { get; private set; }

    internal Func<Task> BeforeScopeDisposal { get; set; } = static () => Task.CompletedTask;

    internal UnseenServantPacerHarness(string name = "watch", string spell = "patrol")
    {
        Job = new UnseenServantJob { Name = name, TargetSpell = spell, IntervalMinutes = 5 };

        ArcanumSettings settings = new() { Daemon = new DaemonSettings { Jobs = [Job] } };

        ServiceCollection services = new();

        services.AddSingleton<IUnseenServantWatermarkStore>(Watermarks);

        _provider = services.BuildServiceProvider();

        Pacer = new UnseenServantPacer(Events, new TestOptionsMonitor<ArcanumSettings>(settings),
            new ScopeFactory(this, _provider.GetRequiredService<IServiceScopeFactory>()), Logger);
    }

    internal Task<bool> SetAsync(int minutes, CancellationToken token = default) => Pacer.SetDynamicIntervalAsync(Job.Name, minutes, token);

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    internal sealed class Store : IUnseenServantWatermarkStore
    {
        internal UnseenServantWatermark? Row { get; set; }

        internal Func<CancellationToken, Task> BeforeSave { get; set; } = static _ => Task.CompletedTask;

        internal Func<CancellationToken, Task> AfterRead { get; set; } = static _ => Task.CompletedTask;

        internal int Saves { get; private set; }

        internal Action<int> AfterSave { get; set; } = static _ => { };

        public async Task<UnseenServantWatermark?> GetAsync(string jobKey, CancellationToken cancellationToken = default)
        {
            UnseenServantWatermark? row = Row;

            await AfterRead(cancellationToken);

            return row;
        }

        public async Task SaveAsync(string jobKey, DateTimeOffset lastRunAt, int effectiveIntervalMinutes, CancellationToken cancellationToken = default)
        {
            Saves++;

            await BeforeSave(cancellationToken);

            Row = new UnseenServantWatermark(jobKey, lastRunAt, effectiveIntervalMinutes);

            AfterSave(effectiveIntervalMinutes);
        }

        public async Task SaveIntervalAsync(string jobKey, DateTimeOffset initialLastRunAt, int effectiveIntervalMinutes, CancellationToken cancellationToken = default)
        {
            Saves++;

            await BeforeSave(cancellationToken);

            Row = new UnseenServantWatermark(jobKey, Row?.LastRunAt ?? initialLastRunAt, effectiveIntervalMinutes);

            AfterSave(effectiveIntervalMinutes);
        }

        public Task SaveLastRunAsync(string jobKey, DateTimeOffset lastRunAt, int initialIntervalMinutes, CancellationToken cancellationToken = default) =>
            SaveAsync(jobKey, lastRunAt, Row?.EffectiveIntervalMinutes ?? initialIntervalMinutes, cancellationToken);

        public Task<IReadOnlyList<UnseenServantWatermark>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnseenServantWatermark>>(Row is null ? [] : [Row]);

        public Task DeleteAsync(string jobKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ScopeFactory(UnseenServantPacerHarness harness, IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(harness, inner.CreateScope());

        private sealed class Scope(UnseenServantPacerHarness harness, IServiceScope inner) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => inner.ServiceProvider;

            public void Dispose() => inner.Dispose();

            public async ValueTask DisposeAsync()
            {
                harness.AsyncDisposals++;

                await harness.BeforeScopeDisposal();

                await ((IAsyncDisposable)inner).DisposeAsync();
            }
        }
    }

    internal sealed class EventBus : IEventBus
    {
        internal List<DaemonEvent> Published { get; } = [];

        public void Publish<T>(T @event) where T : notnull
        {
            if (@event is DaemonEvent daemonEvent)
            {
                Published.Add(daemonEvent);
            }
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull => AsyncEnumerable.Empty<T>();
    }
}
