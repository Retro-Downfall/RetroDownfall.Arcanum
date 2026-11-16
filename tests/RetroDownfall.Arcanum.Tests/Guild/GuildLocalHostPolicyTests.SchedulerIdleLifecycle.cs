using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_scheduler_invalidates_quiet_after_real_options_monitor_publications_between_ticks(bool restoreOriginalPublication)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        ArcanumSettings original = new() { Features = new() { Guild = false } };

        IdleLifecycleOptionsFactory factory = new(original);

        using ConfigurationRoot reload = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection().Build();

        using OptionsMonitor<ArcanumSettings> options = new(factory,
            [new ConfigurationChangeTokenSource<ArcanumSettings>(Options.DefaultName, reload)], new OptionsCache<ArcanumSettings>());

        Assert.Same(original, options.CurrentValue);

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), options,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), f.Gate);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        await scheduler.StartAsync(default);

        try
        {
            await cadence.FirstTimerCreated.WaitAsync(watchdog.Token);

            await traced.WaitForInitialProofAsync(watchdog.Token);

            long boundary = traced.CompletedBoundaryStatements;

            long stamps = traced.CompletedStamps;

            int opens = f.Connections.Kinds.Count;

            // Use the framework OptionsMonitor and real change tokens. Returning the exact
            // original publication isolates notification invalidation from reference checks.
            factory.Publication = new() { Features = new() { Guild = true } };

            reload.Reload();

            Assert.True(options.CurrentValue.Features.Guild);

            factory.Publication = restoreOriginalPublication ? original : new() { Features = new() { Guild = false } };

            reload.Reload();

            Assert.False(options.CurrentValue.Features.Guild);

            if (restoreOriginalPublication) Assert.Same(original, options.CurrentValue);
            else Assert.NotSame(original, options.CurrentValue);

            cadence.Advance(TimeSpan.FromSeconds(1));

            await traced.WaitForBoundaryAfterAsync(boundary, watchdog.Token);

            Assert.True(f.Connections.Kinds.Count > opens, "Published enable/disable must invalidate quiet even when the next tick sees false again.");

            await traced.WaitForStampCountAsync(stamps + 2, watchdog.Token);

            int renewedOpens = f.Connections.Kinds.Count;

            long renewedBoundary = traced.CompletedBoundaryStatements;

            cadence.Advance(TimeSpan.FromSeconds(1));

            await traced.WaitForBoundaryAfterAsync(renewedBoundary, watchdog.Token);

            Assert.Equal(renewedOpens, f.Connections.Kinds.Count);

            Assert.Equal(1, traced.LastBoundaryAutocommit);
        }
        finally
        {
            await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromMinutes(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_scheduler_reproves_after_maintenance_reopen_with_the_same_sqlite_data_version(bool fullyClosed)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        RecordingGrimoireWorkAdmissionGate admission = new(f.Gate);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), admission);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        await scheduler.StartAsync(default);

        try
        {
            await cadence.FirstTimerCreated.WaitAsync(watchdog.Token);

            await traced.WaitForInitialProofAsync(watchdog.Token);

            long initialVersion = traced.LastStampDataVersion;

            long initialStamps = traced.CompletedStamps;

            long initialGeneration = f.Gate.CurrentGeneration;

            int initialOpens = f.Connections.Kinds.Count;

            var begun = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(84));

            Assert.True(begun.IsSuccess, begun.Error.Message);

            await using IGrimoireClosingOwner closing = begun.Value;

            var drained = await f.Gate.DrainRequestAndWorkAsync(closing, watchdog.Token);

            Assert.True(drained.IsSuccess, drained.Error.Message);

            await WaitForIdleLifecycleGenerationWaitAsync(admission, watchdog.Token);

            Assert.Equal(0, f.Connections.LiveFreshLeaseCount);

            Assert.Equal(initialOpens, f.Connections.Kinds.Count);

            if (fullyClosed)
            {
                var closed = await f.Gate.CloseConnectionAdmissionAsync(closing, watchdog.Token);

                Assert.True(closed.IsSuccess, closed.Error.Message);

                await using IGrimoireExclusiveClosedLease held = closed.Value;

                // No database write is performed: the fresh connection's local numeric
                // version can equal the old value, while admission generation must change.
                var reopened = await held.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, default);

                Assert.True(reopened.IsSuccess, reopened.Error.Message);
            }
            else
            {
                var reopened = await f.Gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), default);

                Assert.True(reopened.IsSuccess, reopened.Error.Message);
            }

            Assert.True(f.Gate.CurrentGeneration > initialGeneration);

            // Reopen must wake the real scheduler without a clock tick or database commit.
            await traced.WaitForStampCountAsync(initialStamps + 2, watchdog.Token);

            Assert.True(f.Connections.Kinds.Count > initialOpens);

            Assert.Equal(initialVersion, traced.LastStampDataVersion);

            Assert.Equal(1, f.Connections.LiveFreshLeaseCount);

            long boundary = traced.CompletedBoundaryStatements;

            int renewedOpens = f.Connections.Kinds.Count;

            cadence.Advance(TimeSpan.FromSeconds(1));

            await traced.WaitForBoundaryAfterAsync(boundary, watchdog.Token);

            Assert.Equal(renewedOpens, f.Connections.Kinds.Count);

            Assert.Equal(1, traced.LastBoundaryAutocommit);
        }
        finally
        {
            await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromMinutes(2));
        }

        Assert.Equal(0, admission.ActiveGenerationWaiters);

        Assert.Equal(0, f.Connections.LiveFreshLeaseCount);
    }

    [Fact]
    public async Task Disabled_scheduler_stop_joins_the_actual_generation_wait_without_reopening_admission()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        RecordingGrimoireWorkAdmissionGate admission = new(f.Gate);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), admission);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        await scheduler.StartAsync(default);

        try
        {
            await cadence.FirstTimerCreated.WaitAsync(watchdog.Token);

            await traced.WaitForInitialProofAsync(watchdog.Token);

            long generation = f.Gate.CurrentGeneration;

            var begun = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(85));

            Assert.True(begun.IsSuccess, begun.Error.Message);

            await using IGrimoireClosingOwner closing = begun.Value;

            var drained = await f.Gate.DrainRequestAndWorkAsync(closing, watchdog.Token);

            Assert.True(drained.IsSuccess, drained.Error.Message);

            await WaitForIdleLifecycleGenerationWaitAsync(admission, watchdog.Token);

            Assert.Equal(0, f.Connections.LiveFreshLeaseCount);

            int opens = f.Connections.Kinds.Count;

            await scheduler.StopAsync(default).WaitAsync(watchdog.Token);

            Assert.True(scheduler.ExecuteTask!.IsCompleted);

            Assert.Equal(0, admission.ActiveGenerationWaiters);

            Assert.Equal(opens, f.Connections.Kinds.Count);

            Assert.Equal(generation, f.Gate.CurrentGeneration);

            Assert.False(f.Gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out _));

            // The host joined while admission remained closed to ordinary work; this
            // nondestructive fixture can now cleanly return its independent gate to normal.
            var reopened = await f.Gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), default);

            Assert.True(reopened.IsSuccess, reopened.Error.Message);
        }
        finally
        {
            await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromMinutes(2));
        }

        Assert.Equal(0, f.Connections.LiveFreshLeaseCount);
    }

    private static async Task WaitForIdleLifecycleGenerationWaitAsync(RecordingGrimoireWorkAdmissionGate admission, CancellationToken ct)
    {
        while (admission.ActiveGenerationWaiters == 0) await Task.Delay(10, ct);
    }

    private sealed class IdleLifecycleOptionsFactory(ArcanumSettings initial) : IOptionsFactory<ArcanumSettings>
    {
        private ArcanumSettings _publication = initial;

        internal ArcanumSettings Publication
        {
            get => Volatile.Read(ref _publication);

            set => Volatile.Write(ref _publication, value);
        }

        public ArcanumSettings Create(string name) => Publication;
    }
}
