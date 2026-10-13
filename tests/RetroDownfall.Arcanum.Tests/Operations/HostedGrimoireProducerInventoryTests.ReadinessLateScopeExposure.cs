using Microsoft.CodeAnalysis;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("retained-unobserved")]
    [InlineData("prior-carrier-call")]
    [InlineData("late-resolution")]
    public void Readiness_fresh_holder_history_covers_exposure_between_materialization_and_settlement(string shape)
    {
        string source = ExplicitReadinessScopedFixture("inner-scope");

        void ReplaceOne(string before, string after)
        {
            Assert.Equal(1, source.Split(before, StringSplitOptions.None).Length - 1);

            source = source.Replace(before, after, StringComparison.Ordinal);
        }

        const string holder = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        const string carrier = "RetroDownfall.Arcanum.Infrastructure.Operations.LateReadinessCarrier";

        const string registration = "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitCoordinator>();";

        ReplaceOne(registration, registration + $" services.AddScoped<{carrier}>();");

        if (shape != "late-resolution")
        {
            const string materialize = "Dictionary<string, ILongRunningOperationRecoveryHandler> scopedHandlers = operationScope.ServiceProvider";

            ReplaceOne(materialize, $"var priorCarrier = operationScope.ServiceProvider.GetRequiredService<{carrier}>(); " + materialize);
        }

        const string settlement = "await SettleAsync(scopedHandlers, operation);";

        if (shape == "prior-carrier-call")
        {
            ReplaceOne(settlement, "priorCarrier.BindForeign(); " + settlement);
        }
        else if (shape == "late-resolution")
        {
            ReplaceOne(settlement, $"operationScope.ServiceProvider.GetRequiredService<{carrier}>().BindForeign(); " + settlement);
        }

        source += $$"""
            namespace RetroDownfall.Arcanum.Infrastructure.Operations
            {
                internal sealed class LateReadinessCarrier({{holder}} workAdmission)
                {
                    internal void BindForeign() =>
                        _ = workAdmission.Bind(new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease());
                }
            }
            """;

        Assert.Empty(Compile(source).GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        AssertExplicitReadinessDiscovery(source, "PerUnitCoordinator", shape == "retained-unobserved" ? "owned-lease" : shape);
    }
}
