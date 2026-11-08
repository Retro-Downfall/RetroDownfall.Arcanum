using System.Reflection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed partial class ApprenticeServiceReliabilityTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("resume")]
    [InlineData("reweave")]
    [InlineData("continue")]
    [InlineData("intervene")]
    [InlineData("operator")]
    public async Task Guild_cutover_refuses_direct_legacy_mutation_before_state_changes(string action)
    {
        Apprentice apprentice = TestApprentice(Guid.NewGuid());

        apprentice.Status = action is "resume" or "reweave" ? ApprenticeStatus.Paused.ToString()
            : action is "continue" or "intervene" or "operator" ? ApprenticeStatus.Escalated.ToString() : ApprenticeStatus.Idle.ToString();

        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = new() { Features = new() { Guild = true } };

        using ApprenticeService service = CreateService(repo, settings, new CapturingLogger<ApprenticeService>());

        try
        {
            Error error = action switch
            {
                "start" => (await service.StartAsync(apprentice.Id)).Error,
                "resume" => (await service.ResumeAsync(apprentice.Id)).Error,
                "reweave" => (await service.ReweaveAsync(apprentice.Id, [new() { Description = "Changed plan" }])).Error,
                "continue" => (await service.ContinueAsync(apprentice.Id, "Changed guidance", false)).Error,
                "intervene" => (await service.InterveneAsync(apprentice.Id, "Changed guidance", false)).Error,
                _ => (await service.InterveneOperatorAsync(apprentice.Id, "Changed guidance", false, new(apprentice.Id))).Error,
            };

            Assert.Equal(ErrorCodes.Guild.LegacyRunUnavailable, error.Code);

            Assert.Equal(apprentice.Status, repo.Get(apprentice.Id).Status);

            Assert.Equal(apprentice.Plan, repo.Get(apprentice.Id).Plan);

            Assert.Equal(apprentice.CheckpointData, repo.Get(apprentice.Id).CheckpointData);
        }
        finally
        {
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Guild_cutover_keeps_legacy_cancel_available_for_drain()
    {
        Apprentice apprentice = TestApprentice(Guid.NewGuid());

        apprentice.Status = ApprenticeStatus.Paused.ToString();

        InMemoryApprenticeRepository repo = new(apprentice);

        using ApprenticeService service = CreateService(repo, new() { Features = new() { Guild = true } }, new CapturingLogger<ApprenticeService>());

        Result<string> cancelled = await service.CancelAsync(apprentice.Id);

        Assert.True(cancelled.IsSuccess, cancelled.Error.Message);

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), repo.Get(apprentice.Id).Status);
    }

    [Fact]
    public async Task Guild_cutover_never_resumes_legacy_crash_recovery_into_execution_capacity()
    {
        Apprentice apprentice = TestApprentice(Guid.NewGuid());

        apprentice.Status = ApprenticeStatus.Planning.ToString();

        InMemoryApprenticeRepository repo = new(apprentice);

        LegacyRecoveryCapacity capacity = new();

        using ApprenticeService service = CreateService(repo, new() { Features = new() { Guild = true } }, new CapturingLogger<ApprenticeService>(), executionCapacity: capacity);

        MethodInfo recovery = typeof(ApprenticeService).GetMethod("ResumeCrashRecoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Assert.IsAssignableFrom<Task>(recovery.Invoke(service, [CancellationToken.None]));

        Assert.Equal(0, capacity.Attempts);

        Assert.Equal(ApprenticeStatus.Planning.ToString(), repo.Get(apprentice.Id).Status);
    }

    [Theory]
    [InlineData("start", false)]
    [InlineData("resume", false)]
    [InlineData("reweave", false)]
    [InlineData("continue", false)]
    [InlineData("start", true)]
    [InlineData("resume", true)]
    public async Task Guild_cutover_refuses_bound_or_uncertain_children_even_when_feature_is_off(string action, bool unavailable)
    {
        Apprentice apprentice = TestApprentice(Guid.NewGuid());

        apprentice.Status = action is "resume" or "reweave" ? ApprenticeStatus.Paused.ToString()
            : action == "continue" ? ApprenticeStatus.Escalated.ToString() : ApprenticeStatus.Idle.ToString();

        BoundLegacyRepository repo = new(apprentice, unavailable);

        using ApprenticeService service = CreateService(repo, new(), new CapturingLogger<ApprenticeService>());

        Error error = action switch
        {
            "start" => (await service.StartAsync(apprentice.Id)).Error,
            "resume" => (await service.ResumeAsync(apprentice.Id)).Error,
            "reweave" => (await service.ReweaveAsync(apprentice.Id, [new() { Description = "Changed plan" }])).Error,
            _ => (await service.ContinueAsync(apprentice.Id, "Changed guidance", false)).Error,
        };

        Assert.Equal(unavailable ? ErrorCodes.Guild.Unavailable : ErrorCodes.Guild.LegacyRunUnavailable, error.Code);

        Assert.Equal(apprentice.Status, repo.Get(apprentice.Id).Status);

        Assert.Equal(apprentice.Plan, repo.Get(apprentice.Id).Plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_cutover_recovery_excludes_bound_or_uncertain_children_while_feature_is_off(bool unavailable)
    {
        Apprentice apprentice = TestApprentice(Guid.NewGuid());

        apprentice.Status = ApprenticeStatus.Planning.ToString();

        BoundLegacyRepository repo = new(apprentice, unavailable);

        LegacyRecoveryCapacity capacity = new();

        using ApprenticeService service = CreateService(repo, new(), new CapturingLogger<ApprenticeService>(), executionCapacity: capacity);

        MethodInfo recovery = typeof(ApprenticeService).GetMethod("ResumeCrashRecoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Assert.IsAssignableFrom<Task>(recovery.Invoke(service, [CancellationToken.None]));

        Assert.Equal(0, capacity.Attempts);

        Assert.Equal(ApprenticeStatus.Planning.ToString(), repo.Get(apprentice.Id).Status);
    }

    private sealed class BoundLegacyRepository(Apprentice apprentice, bool unavailable) : InMemoryApprenticeRepository(apprentice)
    {
        public override Task<Result<bool>> IsGuildBoundAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(unavailable ? Result<bool>.Failure(new(ErrorCodes.Guild.Unavailable, "Binding lookup unavailable.")) : Result<bool>.Success(true));
    }

    private sealed class LegacyRecoveryCapacity : IApprenticeExecutionCapacity
    {
        internal int Attempts;

        public int RunningCount => 0;

        public bool TryAcquire(int maxConcurrent, out AdmissionGate.Lease? lease)
        {
            Attempts++;

            lease = null;

            return false;
        }
    }
}
