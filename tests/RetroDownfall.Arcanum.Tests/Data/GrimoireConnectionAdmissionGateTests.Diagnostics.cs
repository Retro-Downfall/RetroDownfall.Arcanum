using System.Globalization;
using System.Reflection;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Tests.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{
    [Fact]
    public async Task Stage_one_timeout_reports_only_current_bounded_holders_including_pending_effects()
    {
        ManualTimeProvider clock = new();

        TimeSpan window = TimeSpan.FromMilliseconds(250);

        GrimoireConnectionAdmissionGate gate = new(clock, new RecordingStageTwoDrain(block: false), OpeningTimeout, window);

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? first));

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? second));

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.QuiesceableStream, out IGrimoireRequestLease? stream));

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.TapestryWeaving, out IGrimoireWorkLease? weaveOne));

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.TapestryWeaving, out IGrimoireWorkLease? weaveTwo));

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? index));

        Assert.True(index!.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect));

        await index.DisposeAsync();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? released));

        using SqliteConnection connection = new("Data Source=private-connection-canary");

        using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(connection);

        await using IGrimoireClosingOwner owner = Begin(gate, Owner(101));

        Task<Result> waiting = gate.DrainRequestAndWorkAsync(owner, CancellationToken.None).AsTask();

        await released!.DisposeAsync();

        await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

        clock.Advance(window);

        Result refusal = await waiting.WaitAsync(BoundedWait);

        try
        {
            Assert.True(refusal.IsFailure);

            Assert.Equal("Grimoire.WorkDrainTimeout", refusal.Error.Code);

            foreach (string fact in new[] { "Finite=2", "QuiesceableStream=1", "TapestryWeaving=2", "WorkspaceIndexing=1", "openingConnections=1", "memoryDelegationPending=0", "windowMs=250", "generation=1" })
            {
                Assert.Contains(fact, refusal.Error.Message, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("SagaExtraction=", refusal.Error.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("private-connection-canary", refusal.Error.Message, StringComparison.Ordinal);

            Assert.False(gate.TryAcquireWorkLease(GrimoireWorkKind.UnseenServant, out _));

            Assert.Contains("owner", refusal.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ticket.MarkFailed();

            await effect!.DisposeAsync();

            await weaveTwo!.DisposeAsync();

            await weaveOne!.DisposeAsync();

            await stream!.DisposeAsync();

            await second!.DisposeAsync();

            await first!.DisposeAsync();
        }
    }

    [Fact]
    public async Task Physical_open_timeout_reports_current_count_and_same_owner_recovery_without_reopening()
    {
        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        using SqliteConnection first = new("Data Source=private-physical-path");

        using SqliteConnection second = new();

        using IGrimoireConnectionOpenTicket pending = gate.AcquireOrdinaryOpen(first);

        using IGrimoireConnectionOpenTicket settled = gate.AcquireOrdinaryOpen(second);

        await using IGrimoireClosingOwner owner = Begin(gate, Owner(102));

        Task<Result<IGrimoireExclusiveClosedLease>> closing = gate.CloseConnectionAdmissionAsync(owner, CancellationToken.None).AsTask();

        settled.MarkFailed();

        await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

        clock.Advance(OpeningTimeout);

        Result<IGrimoireExclusiveClosedLease> refusal = await closing.WaitAsync(BoundedWait);

        bool terminalReported = false;

        try
        {
            Assert.True(refusal.IsFailure);

            Assert.Equal("Grimoire.OpeningTimeout", refusal.Error.Code);

            foreach (string fact in new[] { "openingConnections=1", "windowMs=1000", "generation=2", "same owner" })
            {
                Assert.Contains(fact, refusal.Error.Message, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("private-physical-path", refusal.Error.Message, StringComparison.Ordinal);

            Assert.False(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out _));

            pending.MarkFailed();

            terminalReported = true;

            Result<IGrimoireExclusiveClosedLease> resumed = await gate.CloseConnectionAdmissionAsync(owner, CancellationToken.None);

            Assert.True(resumed.IsSuccess, resumed.Error.Message);

            await resumed.Value.DisposeAsync();
        }
        finally
        {
            if (!terminalReported)
            {
                pending.MarkFailed();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_timeout_logs_one_content_free_warning_outside_the_gate_lock(bool physical)
    {
        ManualTimeProvider clock = new();

        AdmissionTimeoutLogger logger = new();

        GrimoireConnectionAdmissionGate gate = new(clock, new RecordingStageTwoDrain(block: false), OpeningTimeout,
            OpeningTimeout, static _ => ValueTask.CompletedTask, GrimoireInstallationMaintenancePaths.Instance, logger);

        logger.GateSync = typeof(GrimoireConnectionAdmissionGate).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)!;

        using SqliteConnection connection = new("Data Source=private-logger-canary");

        using IGrimoireConnectionOpenTicket? ticket = physical ? gate.AcquireOrdinaryOpen(connection) : null;

        IGrimoireWorkLease? work = null;

        if (!physical)
        {
            Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out work));
        }

        await using IGrimoireClosingOwner owner = Begin(gate, Owner(103));

        Task<Error> error = ObserveTimeoutErrorAsync(gate, owner, physical);

        await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

        clock.Advance(OpeningTimeout);

        Error refusal = await error.WaitAsync(BoundedWait);

        try
        {
            (LogLevel level, string message, Exception? exception, bool underLock) = Assert.Single(logger.Rows);

            Assert.Equal(LogLevel.Warning, level);

            Assert.Contains(refusal.Code, message, StringComparison.Ordinal);

            Assert.Contains(refusal.Message, message, StringComparison.Ordinal);

            Assert.DoesNotContain("private-logger-canary", message, StringComparison.Ordinal);

            Assert.Null(exception);

            Assert.False(underLock);
        }
        finally
        {
            ticket?.MarkFailed();

            if (work is not null)
            {
                await work.DisposeAsync();
            }
        }
    }

    [SkippableFact]
    public async Task Admission_timeout_reports_a_real_memory_delegation_effect_without_source_data()
    {
        await using MemoryDelegationLifecycleTests.Harness harness = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using MemoryDelegationRoot root = await harness.PrepareAsync();

        Assert.True((await harness.Coordinator.ConsumeAsync(root, harness.RootId, Guid.NewGuid(), harness.Digest)).IsSuccess);

        Result<MemoryDelegationEffect> admitted = await harness.Coordinator.AdmitEffectAsync(root);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        await using MemoryDelegationEffect effect = admitted.Value;

        ManualTimeProvider clock = new();

        AdmissionTimeoutLogger logger = new();

        GrimoireConnectionAdmissionGate gate = new(clock, new RecordingStageTwoDrain(block: false),
            GrimoireInstallationMaintenancePaths.Instance, harness.Coordinator, logger);

        logger.GateSync = typeof(GrimoireConnectionAdmissionGate).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)!;

        await using IGrimoireClosingOwner owner = Begin(gate, Owner(104));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(owner, CancellationToken.None).AsTask();

        await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

        clock.Advance(TimeSpan.FromSeconds(5));

        Result refusal = await drain.WaitAsync(BoundedWait);

        Assert.True(refusal.IsFailure);

        Assert.Contains("memoryDelegationPending=1", refusal.Error.Message, StringComparison.Ordinal);

        Assert.Contains("requests=[]; work=[]", refusal.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(harness.RootId.ToString("D"), refusal.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("fact", refusal.Error.Message, StringComparison.Ordinal);

        Assert.Single(logger.Rows);

        await effect.DisposeAsync();

        Assert.True((await gate.DrainRequestAndWorkAsync(owner, CancellationToken.None)).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Successful_or_cancelled_admission_waits_emit_no_timeout_warning(bool physical, bool cancel)
    {
        ManualTimeProvider clock = new();

        AdmissionTimeoutLogger logger = new();

        GrimoireConnectionAdmissionGate gate = new(clock, new RecordingStageTwoDrain(block: false), OpeningTimeout,
            OpeningTimeout, static _ => ValueTask.CompletedTask, GrimoireInstallationMaintenancePaths.Instance, logger);

        await using IGrimoireClosingOwner owner = Begin(gate, Owner(105));

        using CancellationTokenSource cancellation = new();

        if (cancel)
        {
            await cancellation.CancelAsync();
        }

        if (physical)
        {
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.CloseConnectionAdmissionAsync(owner, cancellation.Token).AsTask());
            }
            else
            {
                Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(owner, cancellation.Token);

                Assert.True(closed.IsSuccess, closed.Error.Message);

                await closed.Value.DisposeAsync();
            }
        }
        else
        {
            // An already-drained stage1 intentionally returns success without scheduling a wait.
            Assert.True((await gate.DrainRequestAndWorkAsync(owner, cancellation.Token)).IsSuccess);
        }

        Assert.Empty(logger.Rows);
    }

    [Fact]
    public async Task Production_admission_DI_routes_timeout_warning_to_the_registered_logger()
    {
        ManualTimeProvider clock = new();

        AdmissionTimeoutLogger logger = new();

        ServiceCollection services = new();

        services.AddArcanumGrimoireForCli();

        services.AddSingleton<TimeProvider>(clock);

        services.AddSingleton<ICovenantConnectionDrain>(new RecordingStageTwoDrain(block: false));

        services.AddSingleton<ILogger<GrimoireConnectionAdmissionGate>>(logger);

        await using ServiceProvider provider = services.BuildServiceProvider();

        GrimoireConnectionAdmissionGate gate = provider.GetRequiredService<GrimoireConnectionAdmissionGate>();

        logger.GateSync = typeof(GrimoireConnectionAdmissionGate).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)!;

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? request));

        await using IGrimoireRequestLease held = request!;

        await using IGrimoireClosingOwner owner = Begin(gate, Owner(106));

        Task<Result> waiting = gate.DrainRequestAndWorkAsync(owner, CancellationToken.None).AsTask();

        await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

        clock.Advance(TimeSpan.FromSeconds(5));

        Result refusal = await waiting.WaitAsync(BoundedWait);

        Assert.True(refusal.IsFailure);

        var warning = Assert.Single(logger.Rows);

        Assert.Equal(LogLevel.Warning, warning.Level);

        Assert.Contains("Finite=1", warning.Message, StringComparison.Ordinal);

        Assert.False(warning.UnderLock);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public async Task Admission_timeout_summary_stays_bounded_and_culture_invariant(int count)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;

        List<IGrimoireRequestLease> leases = [];

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            ManualTimeProvider clock = new();

            TimeSpan window = TimeSpan.FromTicks(2501250);

            GrimoireConnectionAdmissionGate gate = new(clock, new RecordingStageTwoDrain(block: false), OpeningTimeout, window);

            for (int i = 0; i < count; i++)
            {
                Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? lease));

                leases.Add(lease!);
            }

            await using IGrimoireClosingOwner owner = Begin(gate, Owner(107));

            Task<Result> waiting = gate.DrainRequestAndWorkAsync(owner, CancellationToken.None).AsTask();

            await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

            clock.Advance(window);

            Result refusal = await waiting.WaitAsync(BoundedWait);

            Assert.True(refusal.IsFailure);

            Assert.Contains("windowMs=250.125", refusal.Error.Message, StringComparison.Ordinal);

            Assert.Contains(FormattableString.Invariant($"requests=[Finite={count}]"), refusal.Error.Message, StringComparison.Ordinal);

            Assert.True(refusal.Error.Message.Length < 1024);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;

            foreach (IGrimoireRequestLease lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Cancelling_pending_stage_one_drain_emits_no_timeout_warning()
    {
        ManualTimeProvider clock = new();

        AdmissionTimeoutLogger logger = new();

        GrimoireConnectionAdmissionGate gate = new(clock, new RecordingStageTwoDrain(block: false), OpeningTimeout,
            OpeningTimeout, static _ => ValueTask.CompletedTask, GrimoireInstallationMaintenancePaths.Instance, logger);

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? request));

        await using IGrimoireRequestLease held = request!;

        await using IGrimoireClosingOwner owner = Begin(gate, Owner(108));

        using CancellationTokenSource cancellation = new();

        Task<Result> waiting = gate.DrainRequestAndWorkAsync(owner, cancellation.Token).AsTask();

        await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(BoundedWait));

        Assert.Empty(logger.Rows);

        Assert.False(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out _));
    }

    private static async Task<Error> ObserveTimeoutErrorAsync(GrimoireConnectionAdmissionGate gate, IGrimoireClosingOwner owner, bool physical)
    {
        if (physical)
        {
            return (await gate.CloseConnectionAdmissionAsync(owner, CancellationToken.None)).Error;
        }

        return (await gate.DrainRequestAndWorkAsync(owner, CancellationToken.None)).Error;
    }

    private sealed class AdmissionTimeoutLogger : ILogger<GrimoireConnectionAdmissionGate>
    {
        internal object GateSync { get; set; } = new();

        internal List<(LogLevel Level, string Message, Exception? Exception, bool UnderLock)> Rows { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Rows.Add((logLevel, formatter(state, exception), exception, Monitor.IsEntered(GateSync)));
    }

    [Theory]
    [InlineData("Grimoire.AdmissionLifecycleConflict", 409)]
    [InlineData("Grimoire.OpeningTimeout", 503)]
    [InlineData("Grimoire.StaleOpenGeneration", 503)]
    [InlineData("Grimoire.WorkDrainTimeout", 503)]
    [InlineData("Grimoire.MaintenanceUnavailable", 503)]
    public void Admission_gate_wire_codes_have_specific_HTTP_statuses(string code, int status) =>
        Assert.Equal(status, ArcanumErrorMapper.ResolveStatusCode(code));

    [Theory]
    [InlineData("AdmissionLifecycleConflict")]
    [InlineData("OpeningTimeout")]
    [InlineData("StaleOpenGeneration")]
    [InlineData("WorkDrainTimeout")]
    public void Admission_gate_wire_codes_have_exact_central_constants(string name)
    {
        FieldInfo? field = typeof(ErrorCodes.Grimoire).GetField(name, BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(field);

        Assert.True(field.IsLiteral);

        Assert.Equal("Grimoire." + name, field.GetRawConstantValue());
    }
}
