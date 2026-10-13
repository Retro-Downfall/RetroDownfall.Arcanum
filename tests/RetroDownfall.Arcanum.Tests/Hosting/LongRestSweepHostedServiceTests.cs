using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.LongRest;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

[Collection("Grimoire")]
public sealed class LongRestSweepHostedServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_reset_aware_wrapper_starts_only_in_ordinary_host(bool reset)
    {
        await using var h = await LongRestSweepSchedulingTests.SchedulingHarness.CreateAsync();

        Guid operation = await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        h.Expect(operation);

        InstallationResetApiAdmission admission = new();

        if (reset)
        {
            admission.PublishRecovery(new(InstallationResetScope.All, null, "long-rest-fixture", Guid.NewGuid(),
                DataHandoff: InstallationResetDataHandoff.HostFactoryErasure));
        }

        using InstallationResetRecoveryAwareHostedService<LongRestSweepHostedService> wrapper = new(h.Service, admission);

        await h.StartAsync(wrapper);

        if (reset)
        {
            Assert.Null(h.Service.ExecuteTask);

            Assert.Equal(0, h.ScopesCreated);

            Assert.Equal(0, h.F.Handler.SendCount);

            await h.AssertPendingAsync(operation);
        }
        else
        {
            await h.WaitAndStopAsync();

            await h.AssertCompletedAsync();
        }

        await wrapper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Real_host_holds_admission_through_scope_disposal_and_hands_its_exact_lease_by_value()
    {
        using Harness h = new();

        await h.RunOne(h.Operation, CancellationToken.None);

        Assert.Equal(["scope", "resume", "scope-dispose", "lease-dispose"], h.Events);

        Assert.Equal(h.Operation, h.Sweep.Operation);

        // Exactly one Long Rest lease was minted, and it is the one the port was handed, live and unrevoked.
        Assert.Equal([GrimoireWorkKind.LongRestSweep], h.Gate.RequestedWorkKinds);

        Assert.Single(h.Gate.InnerWorkLeases);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, h.Sweep.AdmissionKind);

        Assert.Equal(h.Inner.CurrentGeneration, h.Sweep.AdmissionGeneration);

        Assert.False(h.Sweep.RevocationPending);
    }

    [Fact]
    public void The_admitted_sweep_port_carries_the_lease_and_no_capability()
    {
        System.Reflection.MethodInfo member = Assert.Single(typeof(ILongRestAdmittedSweep).GetMethods());

        Assert.Equal(nameof(ILongRestAdmittedSweep.ResumeAdmittedAsync), member.Name);

        Assert.Equal([typeof(Guid), typeof(IGrimoireWorkLease), typeof(CancellationToken)],
            member.GetParameters().Select(static parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task Gate_off_performs_no_admission_or_scope_work()
    {
        using Harness h = new();

        var disabled = LongRestConfigurationEpochTests.Settings();

        disabled.Features.LongRest = false;

        h.Options.Set(disabled);

        await h.RunOne(h.Operation, CancellationToken.None);

        Assert.Empty(h.Events);

        Assert.Empty(h.Gate.RequestedWorkKinds);
    }

    [Fact]
    public async Task Refusal_and_immediate_reclose_retain_the_exact_operation_without_scope_or_polling()
    {
        using Harness h = new();

        await using IGrimoireClosingOwner initial = h.Inner.BeginOrResumeExclusive(Owner()).Value;

        using CancellationTokenSource cancellation = new();

        Task run = h.RunOne(h.Operation, cancellation.Token);

        Assert.False(run.IsCompleted);

        Assert.Empty(h.Events);

        Assert.Equal(1, h.Gate.GenerationWaits);

        TaskCompletionSource<IGrimoireClosingOwner> reclosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        h.Gate.AfterOpenGenerationObserved = _ => reclosed.TrySetResult(h.Inner.BeginOrResumeExclusive(Owner()).Value);

        Assert.True((await h.Inner.AbortClosingAsync(initial, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        await using IGrimoireClosingOwner closing = await reclosed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(h.Events);

        h.Gate.AfterOpenGenerationObserved = _ => { };

        Assert.True((await h.Inner.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(h.Operation, h.Sweep.Operation);

        Assert.Equal(1, h.Events.Count(value => value == "resume"));
    }

    private static CovenantExclusiveRecoveryOwner Owner() => new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));

    private sealed class Harness : IDisposable
    {
        internal List<string> Events { get; } = [];

        internal Guid Operation { get; } = Guid.NewGuid();

        internal GrimoireConnectionAdmissionGate Inner { get; } = new(TimeProvider.System);

        internal RecordingGrimoireWorkAdmissionGate Gate { get; }

        internal LongRestConfigurationEpochTests.NotifyingOptions Options { get; } = new(LongRestConfigurationEpochTests.Settings());

        private LongRestConfigurationEpoch Epoch { get; }

        internal Sweep Sweep { get; }

        internal LongRestSweepHostedService Service { get; }

        internal LongRestSweepQueue Queue { get; } = new();

        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Func<Guid, CancellationToken, Task> RunOne => Service.RunOneAsync;

        internal Harness()
        {
            Epoch = new(Options, Guid.NewGuid());

            Gate = new(Inner);

            Gate.BeforeWorkLeaseDisposalAsync = () => { Events.Add("lease-dispose"); Disposed.TrySetResult(); return ValueTask.CompletedTask; };

            Sweep = new(Events);

            Service = new(new Scopes(Events, Sweep), Queue, Epoch, Gate, NullLoggerFactory.Instance);
        }

        public void Dispose()
        {
            Service.Dispose();

            Epoch.Dispose();
        }
    }

    private sealed class Scopes(List<string> events, Sweep sweep) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            events.Add("scope");

            return new Scope(events, sweep);
        }
    }

    private sealed class Scope(List<string> events, Sweep sweep) : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) => serviceType == typeof(ILongRestAdmittedSweep) ? sweep : null;

        public void Dispose() => throw new InvalidOperationException("Async scope disposal is required.");

        public ValueTask DisposeAsync()
        {
            events.Add("scope-dispose");

            return ValueTask.CompletedTask;
        }
    }

    private sealed class Sweep(List<string> events) : ILongRestAdmittedSweep
    {
        internal Guid Operation { get; private set; }

        internal GrimoireWorkKind? AdmissionKind { get; private set; }

        internal long AdmissionGeneration { get; private set; }

        internal bool RevocationPending { get; private set; }

        public Task<Result<LongRestSweepProgress>> ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct)
        {
            events.Add("resume");

            Operation = operationId;

            AdmissionKind = admission.Kind;

            AdmissionGeneration = admission.Generation;

            RevocationPending = admission.MaintenanceRevocationPending;

            return Task.FromResult(Result<LongRestSweepProgress>.Success(new(operationId, Guid.NewGuid(), LongRestSweepScopeKind.Global, null,
                LongRestSweepPhase.Completed, 0, null, null)));
        }
    }
}
