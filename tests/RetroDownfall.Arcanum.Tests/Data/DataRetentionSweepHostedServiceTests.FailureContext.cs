using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionSweepHostedServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Automatic_retention_warning_carries_only_the_known_durable_operation(bool known)
    {
        Guid? operation = known ? Guid.NewGuid() : null;

        Error failure = new(ErrorCodes.Data.ReconciliationFailed, "Fixed diagnostic reason.") { RetentionOperationId = operation };

        await using ServiceProvider provider = Services(new FailingDiagnosticHostedRetentionService(failure)).BuildServiceProvider();

        TestCapturingLogger<DataRetentionSweepHostedService> logger = new();

        using DataRetentionSweepHostedService hosted = new(provider.GetRequiredService<IServiceScopeFactory>(),
            new MutablePolicyStore(new RetentionSettings { AutomaticSweepsEnabled = true }), TimeProvider.System,
            new GrimoireConnectionAdmissionGate(TimeProvider.System), new LongRunningOperationOwnership(), logger);

        await hosted.RunOnceAsync(CancellationToken.None);

        TestLogEntry warning = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, warning.Level);

        KeyValuePair<string, object?> property = Assert.Single(warning.Properties!, value => value.Key == "OperationId");

        Assert.Equal(operation, property.Value);

        if (operation is { } id) Assert.Contains(id.ToString("D"), warning.Message, StringComparison.Ordinal);

        Assert.Null(warning.Exception);
    }

    private sealed class FailingDiagnosticHostedRetentionService(Error failure) : IDataRetentionHostedSweep
    {
        public Task<DataRetentionHostedSweepOutcome> ApplyOrResumeHostedPruneAsync(
            DataRetentionHostedSweepContinuation? continuation,
            IGrimoireWorkLease workLease,
            CancellationToken cancellationToken) => Task.FromResult(new DataRetentionHostedSweepOutcome(
                DataRetentionHostedSweepDisposition.Concluded, null, Result<DataRetentionApplyResult>.Failure(failure)));
    }
}
