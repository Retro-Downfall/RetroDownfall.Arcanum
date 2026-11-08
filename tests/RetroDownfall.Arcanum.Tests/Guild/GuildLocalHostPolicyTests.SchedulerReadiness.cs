using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_scheduler_protects_every_physical_readiness_probe_with_work_admission(bool pendingWork)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://provider.example.test/v1", Models = [new() { Name = "model" }] }];

        RecordingGrimoireWorkAdmissionGate admission = new(f.Gate);

        await using GuildLocalHostReadiness physical = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), admission);

        ProbedSchedulerReadiness readiness = new(physical, admission);

        if (pendingWork)
        {
            var issued = await f.IssueAsync(await f.OperatorAsync());

            Assert.True(issued.IsSuccess, issued.Error.Message);
        }

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        GuildLocalQuestScheduler? scheduler = null;

        SchedulerEngine engine = new();

        LongRunningOperationOwnership ownership = new();

        ServiceCollection registration = new();

        registration.AddSingleton(f.Policy);

        registration.AddSingleton(f.Authority);

        registration.AddSingleton(f.Store);

        registration.AddSingleton<IGuildLocalExecutionEngine>(engine);

        registration.AddSingleton<IGuildLocalInferenceRuntime>(new SchedulerInference());

        registration.AddScoped<GuildLocalPort>(_ => new(f.Store, f.Policy, f.Authority,
            new LongRunningOperationStore(db, f.Connections), engine, new SchedulerInference(),
            scheduler!, ownership, TimeProvider.System));

        await using ServiceProvider services = registration.BuildServiceProvider();

        using GuildLocalQuestScheduler owner = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            f.Connections, ownership, TimeProvider.System, NullLogger<GuildLocalQuestScheduler>.Instance, readiness, admission);

        scheduler = owner;

        await scheduler.StartAsync(default);

        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

            await readiness.Probed.Task.WaitAsync(deadline.Token);

            if (pendingWork) await engine.Entered.Task.WaitAsync(deadline.Token);

            Assert.Equal(0, readiness.UnadmittedProbes);
        }
        finally
        {
            engine.Release.TrySetResult();

            await scheduler.StopAsync(default);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_scheduler_hot_enable_requires_effect_admission_before_installing_physical_protection(bool denyEffect)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Features.Guild = false;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://provider.example.test/v1", Models = [new() { Name = "model" }] }];

        RecordingGrimoireWorkAdmissionGate admission = new(f.Gate);

        string lockPath = Path.Combine(f.WorkspaceRoot, "host.lock");

        await using GuildLocalHostReadiness physical = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, lockPath, admission);

        ProbedSchedulerReadiness readiness = new(physical, admission);

        TaskCompletionSource attemptedEffect = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int disposedEffects = 0;

        admission.BeforeEffectGroupDisposalAsync = () =>
        {
            Assert.True(File.Exists(lockPath));

            Assert.True(WorkspaceMutationFence.IsInstalled);

            Assert.True(admission.HasActiveEffectGroup);

            Interlocked.Increment(ref disposedEffects);

            return ValueTask.CompletedTask;
        };

        IGrimoireClosingOwner? closing = null;

        admission.BeforeEffectGroupAdmission = () =>
        {
            if (denyEffect && closing is null)
            {
                var begun = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(150));

                Assert.True(begun.IsSuccess, begun.Error.Message);

                closing = begun.Value;
            }

            attemptedEffect.TrySetResult();
        };

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        GuildLocalQuestScheduler? scheduler = null;

        LongRunningOperationOwnership ownership = new();

        ServiceCollection registration = new();

        registration.AddSingleton(f.Policy);

        registration.AddSingleton(f.Authority);

        registration.AddSingleton(f.Store);

        registration.AddSingleton<IGuildLocalExecutionEngine>(new SchedulerEngine());

        registration.AddSingleton<IGuildLocalInferenceRuntime>(new SchedulerInference());

        registration.AddScoped<GuildLocalPort>(_ => new(f.Store, f.Policy, f.Authority,
            new LongRunningOperationStore(db, f.Connections), new SchedulerEngine(), new SchedulerInference(),
            scheduler!, ownership, TimeProvider.System));

        await using ServiceProvider services = registration.BuildServiceProvider();

        using GuildLocalQuestScheduler owner = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            f.Connections, ownership, TimeProvider.System, NullLogger<GuildLocalQuestScheduler>.Instance, readiness, admission);

        scheduler = owner;

        await scheduler.StartAsync(default);

        try
        {
            Assert.False(File.Exists(lockPath));

            f.Settings.CurrentValue.Features.Guild = true;

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

            await Task.WhenAny(attemptedEffect.Task, readiness.Ensured.Task).WaitAsync(deadline.Token);

            if (denyEffect)
            {
                Assert.Equal(1, admission.EffectGroupAttempts);

                Assert.Equal(1, readiness.EnsureCalls);

                Assert.Equal(0, disposedEffects);

                Assert.False(File.Exists(lockPath));

                Assert.False(WorkspaceMutationFence.IsInstalled);
            }
            else
            {
                await readiness.Ensured.Task.WaitAsync(deadline.Token);

                Assert.Equal(1, admission.EffectGroupAttempts);

                Assert.Equal(1, disposedEffects);

                Assert.True(File.Exists(lockPath));

                Assert.True(WorkspaceMutationFence.IsInstalled);
            }
        }
        finally
        {
            await scheduler.StopAsync(default);

            if (closing is not null) await closing.DisposeAsync();
        }
    }

    private sealed class ProbedSchedulerReadiness(IGuildLocalHostReadiness inner,
        RecordingGrimoireWorkAdmissionGate admission) : IGuildLocalHostReadiness
    {
        internal TaskCompletionSource Probed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int UnadmittedProbes;

        internal TaskCompletionSource Ensured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int EnsureCalls;

        public Result RequireExecutionReady()
        {
            if (!admission.HasActiveWorkLease) Interlocked.Increment(ref UnadmittedProbes);

            Result current = inner.RequireExecutionReady();

            Probed.TrySetResult();

            return current;
        }

        public async Task<Result> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref EnsureCalls);

            try
            {
                return await inner.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Ensured.TrySetResult();
            }
        }
    }
}
