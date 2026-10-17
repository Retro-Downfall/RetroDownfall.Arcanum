using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.LongRest;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The turn policy for clean Long Rest scheduling. A failed clean schedule with a Long Rest error is logged and counted, and the
/// turn proceeds, because clean work belongs to the worker. A Covenant failure, a configuration change (including a flip back to
/// the same value) and a cancellation still end the turn. A busy clean scope is skipped like any Long Rest refusal.
/// </summary>
public sealed class LongRestTurnPolicyTests
{
    private const string RefusedMetric = "arcanum.long_rest.clean_schedule_refused";

    [Theory]
    [InlineData(ErrorCodes.LongRest.StaleInput)]
    [InlineData(ErrorCodes.LongRest.Unavailable)]
    [InlineData(ErrorCodes.LongRest.SweepParked)]
    [InlineData(ErrorCodes.LongRest.OperationBusy)]
    [InlineData(ErrorCodes.LongRest.InvalidRequest)]
    [InlineData(ErrorCodes.LongRest.IntegrityFailure)]
    [InlineData(ErrorCodes.LongRest.ModelContextTooSmall)]
    public async Task A_failed_clean_schedule_with_a_Long_Rest_code_is_logged_and_counted_and_the_turn_proceeds(string code)
    {
        using AsyncFlowMetricCapture metrics = new(RefusedMetric);

        Harness h = new(() => new Error(code, "The scope could not be scheduled."));

        Result<CampaignRollupTurnSnapshot?> result = await h.PrepareAsync();

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.NotNull(snapshot.ClaimLease);

        Assert.Equal(SessionTurnClaimState.PendingMaintenance, snapshot.ClaimLease!.Claim.State);

        Assert.Equal(1, h.Coordinator.CleanRequests);

        // Counted once, labelled by the closed code only.
        Assert.Equal(1, metrics.Sum(RefusedMetric, "code", code));

        Assert.Single(metrics.Measurements);

        // Logged content-free: the code, never the message or any identity.
        Assert.Single(h.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Text.Contains(code, StringComparison.Ordinal));

        Assert.DoesNotContain("could not be scheduled", h.Logger.Text, StringComparison.Ordinal);

        Assert.Equal(0, h.Claims.Completions);
    }

    [Theory]
    [InlineData(ErrorCodes.Covenant.StaleSnapshot)]
    [InlineData(ErrorCodes.Covenant.Unavailable)]
    [InlineData(ErrorCodes.Hub.SessionTurnBusy)]
    public async Task A_failed_clean_schedule_with_any_other_code_still_ends_the_turn_and_terminalizes_the_claim(string code)
    {
        using AsyncFlowMetricCapture metrics = new(RefusedMetric);

        Harness h = new(() => new Error(code, "Refused."));

        Result<CampaignRollupTurnSnapshot?> result = await h.PrepareAsync();

        Assert.True(result.IsFailure);

        Assert.Equal(code, result.Error.Code);

        Assert.Equal(1, h.Claims.Completions);

        Assert.Empty(metrics.Measurements);
    }

    [Fact]
    public async Task A_configuration_flip_during_a_refused_schedule_still_ends_the_turn_even_when_the_value_returns()
    {
        Harness h = null!;

        h = new(() =>
        {
            ArcanumSettings original = h.Options.CurrentValue;

            // A to B to A: the value is the same afterwards, but the epoch the turn accepted is gone.
            h.Options.Set(original with { Features = original.Features with { LongRest = false } });

            h.Options.Set(original);

            return new Error(ErrorCodes.LongRest.StaleInput, "Refused.");
        });

        Result<CampaignRollupTurnSnapshot?> result = await h.PrepareAsync();

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

        Assert.Equal(1, h.Claims.Completions);
    }

    [Fact]
    public async Task A_cancellation_of_the_clean_schedule_still_ends_the_turn_because_it_is_an_exception_not_a_result()
    {
        Harness h = new(() => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.PrepareAsync());

        Assert.Equal(1, h.Claims.Completions);
    }

    [Fact]
    public async Task A_clean_scope_that_schedules_cleanly_is_not_counted()
    {
        using AsyncFlowMetricCapture metrics = new(RefusedMetric);

        Harness h = new(() => null);

        Result<CampaignRollupTurnSnapshot?> result = await h.PrepareAsync();

        Assert.True(result.IsSuccess);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.Empty(metrics.Measurements);

        Assert.Empty(h.Logger.Entries);
    }

    /// <summary>A clean-only turn (no Covenant authority) over a real epoch and a scripted coordinator.</summary>
    private sealed class Harness
    {
        internal readonly LongRestConfigurationEpochTests.NotifyingOptions Options;

        internal readonly ScriptedCoordinator Coordinator;

        internal readonly CampaignRollupTurnSnapshotTests.AcceptingClaims Claims = new();

        internal readonly CapturingLogger Logger = new();

        private readonly CampaignRollupTurnPreparer _preparer;

        internal Harness(Func<Error?> cleanSchedule)
        {
            ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

            settings.Features.Covenant = false;

            Options = new(settings);

            LongRestConfigurationEpoch epoch = new(Options, Guid.NewGuid());

            Coordinator = new(cleanSchedule);

            _preparer = new(new CampaignRollupTurnSnapshotTests.ProbeStore(), Options, new CampaignRollupTurnSnapshotTests.Resolver(), null!,
                Coordinator, UnreachedLongRestPorts.Maintenance, UnreachedLongRestPorts.Discovery, epoch, new PassThroughRootEnricher(),
                claimBegin: new CampaignRollupTurnSnapshotTests.InputStore(), claims: Claims,
                authoritySnapshot: new CampaignRollupTurnSnapshotTests.Authority(), logger: Logger);
        }

        internal Task<Result<CampaignRollupTurnSnapshot?>> PrepareAsync()
        {
            ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
                ArcanumExecutionSurface.SessionBackedOperatorTurn, CanonicalCampaignContext.GlobalOnly,
                InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

            return _preparer.PrepareLiveAsync(new("Continue.", SessionId: Guid.NewGuid()), invocation, false, CancellationToken.None);
        }
    }

    private sealed class ScriptedCoordinator(Func<Error?> cleanSchedule) : ILongRestSweepCoordinator
    {
        internal int CleanRequests;

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct)
        {
            CleanRequests++;

            Error? refusal = cleanSchedule();

            return Task.FromResult(refusal is { } error
                ? Result<LongRunningOperationDto>.Failure(error)
                : Result<LongRunningOperationDto>.Success(new LongRunningOperationDto(Guid.NewGuid(), LongRunningOperationKinds.LongRestSweep,
                    LongRunningOperationState.Pending, LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint, null, null, null, null, null, null, null,
                    DateTimeOffset.UtcNow, null, null, null, null, null, 0, 0, false, "queued", null, 0)));
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct) =>
            throw new InvalidOperationException("A clean-only turn never requests a protected partition.");

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease authority, CancellationToken ct) =>
            throw new InvalidOperationException("A clean-only turn never requests a protected partition.");

        public Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
            throw new InvalidOperationException("A clean-only turn never resumes a sweep.");
    }

    internal sealed class CapturingLogger : ILogger<CampaignRollupTurnPreparer>
    {
        internal List<(LogLevel Level, string Text)> Entries { get; } = [];

        internal string Text => string.Join('\n', Entries.Select(static entry => entry.Text));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
