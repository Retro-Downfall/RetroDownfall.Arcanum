using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

// Uses the installed DI container; it does not teach the old unkeyed test provider keyed semantics.
internal sealed class SelectedRecoveryScopeFixture : IAsyncDisposable
{
    internal const string SelectedKind = LongRunningOperationKinds.BlobEncryptionKeyRotation;

    internal const string UnrelatedKind = LongRunningOperationKinds.BlobEncryptionMigration;

    private readonly ConcurrentQueue<ScopeState> _scopes = new();

    private readonly AsyncServiceScope _outerScope;

    internal SelectedRecoveryScopeFixture(string shape = "valid")
    {
        Store = new FakeLongRunningOperationStore(Time);

        ServiceCollection services = new();

        services.AddScoped<ScopeState>(_ =>
        {
            ScopeState state = new(Store);

            _scopes.Enqueue(state);

            return state;
        });

        services.AddScoped<ILongRunningOperationStore>(provider =>
        {
            ScopeState state = provider.GetRequiredService<ScopeState>();

            state.StoreResolutions++;

            return state.Store;
        });

        services.AddScoped<ILongRunningOperationClassifiedRecoveryLeaseAcquisition>(provider =>
        {
            ScopeState state = provider.GetRequiredService<ScopeState>();

            state.AcquisitionResolutions++;

            return state.Acquisition;
        });

        services.AddScoped<ILongRunningOperationRecoveryHandler>(provider => CreateHandler(provider, SelectedKind, keyed: false));

        services.AddScoped<ILongRunningOperationRecoveryHandler>(provider => CreateHandler(provider, UnrelatedKind, keyed: false));

        if (shape != "unknown-key")
        {
            services.AddKeyedScoped<ILongRunningOperationRecoveryHandler>(SelectedKind, (provider, key) =>
                CreateHandler(provider, shape == "wrong-key" ? UnrelatedKind : SelectedKind, keyed: true, key));
        }

        if (shape == "duplicate-key")
        {
            services.AddKeyedScoped<ILongRunningOperationRecoveryHandler>(SelectedKind, (provider, key) =>
                CreateHandler(provider, SelectedKind, keyed: true, key));
        }

        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        _outerScope = Provider.CreateAsyncScope();

        Outer = _outerScope.ServiceProvider.GetRequiredService<ScopeState>();

        // This invokes the existing public constructor and both unkeyed registrations before the
        // test arms the unrelated-constructor sentinel. Missing/duplicate global validation stays real.
        Reconciler = new LongRunningOperationReconciler(
            new PagingOnlyOperationStore(Store),
            _outerScope.ServiceProvider.GetServices<ILongRunningOperationRecoveryHandler>(),
            Time,
            NullLogger<LongRunningOperationReconciler>.Instance,
            new LongRunningOperationOwnership(),
            Provider.GetRequiredService<IServiceScopeFactory>());
    }

    internal FakeTimeProvider Time { get; } = new();

    internal FakeLongRunningOperationStore Store { get; }

    internal ServiceProvider Provider { get; }

    internal ScopeState Outer { get; }

    internal LongRunningOperationReconciler Reconciler { get; }

    internal bool ForbidUnrelatedConstruction { get; set; }

    internal ScopeState[] OperationScopes => [.. _scopes.Where(state => !ReferenceEquals(state, Outer))];

    private Handler CreateHandler(IServiceProvider provider, string kind, bool keyed, object? key = null)
    {
        ScopeState state = provider.GetRequiredService<ScopeState>();

        if (keyed)
        {
            state.RequestedKeys.Add(Assert.IsType<string>(key));
        }
        else
        {
            state.UnkeyedConstructions++;
        }

        return new Handler(this, state, kind);
    }

    public async ValueTask DisposeAsync()
    {
        await _outerScope.DisposeAsync();

        await Provider.DisposeAsync();
    }

    internal sealed class ScopeState : IAsyncDisposable
    {
        internal ScopeState(FakeLongRunningOperationStore shared)
        {
            Store = new CancelsOnCompensationOperationStore(shared);

            Acquisition = new Acquisition(this, shared);
        }

        internal ILongRunningOperationStore Store { get; }

        internal Acquisition Acquisition { get; }

        internal List<Handler> Handlers { get; } = [];

        internal List<string> RequestedKeys { get; } = [];

        internal int StoreResolutions { get; set; }

        internal int AcquisitionResolutions { get; set; }

        internal int UnkeyedConstructions { get; set; }

        internal int UnrelatedConstructionAttempts { get; set; }

        internal int AsyncDisposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            AsyncDisposals++;

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Acquisition(ScopeState state, FakeLongRunningOperationStore shared)
        : ILongRunningOperationClassifiedRecoveryLeaseAcquisition
    {
        internal List<Guid> OperationIds { get; } = [];

        public Task<LongRunningOperationLeaseResult> TryAcquireClassifiedRecoveryLeaseAsync(
            LongRunningOperationRecoveryFingerprint expected,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(0, state.AsyncDisposals);

            OperationIds.Add(expected.OperationId);

            return shared.TryAcquireClassifiedRecoveryLeaseAsync(expected, ownerId, utcNow, leaseExpiresAt, cancellationToken);
        }
    }

    internal sealed class Handler : ILongRunningOperationRecoveryHandler, IAsyncDisposable
    {
        private readonly SelectedRecoveryScopeFixture _fixture;

        private readonly ScopeState _state;

        internal Handler(SelectedRecoveryScopeFixture fixture, ScopeState state, string kind)
        {
            _fixture = fixture;

            _state = state;

            Kind = kind;

            if (kind == UnrelatedKind)
            {
                state.UnrelatedConstructionAttempts++;

                if (fixture.ForbidUnrelatedConstruction && !ReferenceEquals(state, fixture.Outer))
                {
                    throw new InvalidOperationException("UNRELATED_RECOVERY_HANDLER_CONSTRUCTED_AFTER_OUTER_VALIDATION");
                }
            }

            state.Handlers.Add(this);
        }

        public string Kind { get; }

        public int SupportedCheckpointVersion => 0;

        internal List<Guid> OperationIds { get; } = [];

        internal int AsyncDisposals { get; private set; }

        public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
            LongRunningOperation operation,
            CancellationToken cancellationToken)
        {
            Assert.NotSame(_fixture.Outer, _state);

            Assert.Equal(0, _state.AsyncDisposals);

            Assert.Equal(0, AsyncDisposals);

            Assert.Equal(operation.Id, Assert.Single(_state.Acquisition.OperationIds));

            LongRunningOperation? persisted = await _state.Store.GetAsync(operation.Id, cancellationToken);

            Assert.NotNull(persisted);

            Assert.Equal(operation.Revision, persisted.Revision);

            OperationIds.Add(operation.Id);

            return LongRunningOperationRecoveryResult.Completed();
        }

        public ValueTask DisposeAsync()
        {
            Assert.Equal(0, _state.AsyncDisposals);

            AsyncDisposals++;

            return ValueTask.CompletedTask;
        }
    }
}
