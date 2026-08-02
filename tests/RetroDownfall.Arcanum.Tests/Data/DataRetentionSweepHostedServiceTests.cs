using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class DataRetentionSweepHostedServiceTests
{

    [Fact]

    public async Task RunOnceAsync_AppliesOnlyWhenAutomaticSweepsAreEnabled()
    {

        RecordingRetentionService service = new();

        MutablePolicyStore policy = new(
            new RetentionSettings
            {

                AutomaticSweepsEnabled = false,

            });

        ServiceCollection services = new();

        services.AddSingleton<IDataRetentionService>(service);

        await using ServiceProvider provider = services.BuildServiceProvider();

        DataRetentionSweepHostedService hosted = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            policy,
            TimeProvider.System,
            NullLogger<DataRetentionSweepHostedService>.Instance);

        await hosted.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, service.ApplyCalls);

        policy.Current = policy.Current with
        {

            AutomaticSweepsEnabled = true,

        };

        await hosted.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, service.ApplyCalls);

        Assert.Equal(DataRetentionOperation.Prune, service.LastRequest?.Request.Operation);

    }

    private sealed class MutablePolicyStore(
        RetentionSettings current) : IDataRetentionPolicyStore
    {

        public RetentionSettings Current { get; set; } = current;

        public Task<Result<RetentionSettings>> UpdateRuleAsync(
            RetentionRuleUpdateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingRetentionService : IDataRetentionService
    {

        public int ApplyCalls { get; private set; }

        public DataRetentionApplyRequest? LastRequest { get; private set; }

        public Task<DataRetentionStatus> GetStatusAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DataRetentionPlan> PlanAsync(
            DataRetentionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<DataRetentionApplyResult>> ApplyAsync(
            DataRetentionApplyRequest request,
            CancellationToken cancellationToken = default)
        {

            ApplyCalls++;

            LastRequest = request;

            return Task.FromResult(
                Result<DataRetentionApplyResult>.Success(
                    new DataRetentionApplyResult(
                        Guid.NewGuid(),
                        "plan",
                        0,
                        0,
                        0,
                        0,
                        Reconciled: true,
                        [],
                        [])));

        }

    }

}
