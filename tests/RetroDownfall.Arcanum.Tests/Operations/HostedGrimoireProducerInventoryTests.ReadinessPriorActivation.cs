using Microsoft.CodeAnalysis;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("prior-resolution")]
    [InlineData("prior-constructor-prebinding")]
    [InlineData("prior-factory-prebinding")]
    public void Readiness_fresh_holder_proof_includes_services_resolved_before_handler_materialization(string shape)
    {
        string source = ExplicitReadinessScopedFixture("inner-scope");

        void ReplaceOne(string before, string after)
        {
            Assert.Equal(1, source.Split(before, StringSplitOptions.None).Length - 1);

            source = source.Replace(before, after, StringComparison.Ordinal);
        }

        const string holder = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        const string activation = "RetroDownfall.Arcanum.Infrastructure.Operations.PriorReadinessActivation";

        string registration = shape == "prior-factory-prebinding"
            ? $$"""
                services.AddScoped<{{activation}}>(static sp =>
                {
                    _ = sp.GetRequiredService<{{holder}}>().Bind(new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease());

                    return new {{activation}}(sp.GetRequiredService<{{holder}}>());
                });
                """
            : $"services.AddScoped<{activation}>();";

        const string existing = "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitCoordinator>();";

        ReplaceOne(existing, existing + " " + registration);

        const string materialize = "Dictionary<string, ILongRunningOperationRecoveryHandler> scopedHandlers = operationScope.ServiceProvider";

        ReplaceOne(materialize, $"_ = operationScope.ServiceProvider.GetRequiredService<{activation}>(); " + materialize);

        string initializer = shape == "prior-constructor-prebinding"
            ? "private readonly IDisposable foreignBinding = workAdmission.Bind(new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease());"
            : "";

        source += $$"""
            namespace RetroDownfall.Arcanum.Infrastructure.Operations
            {
                internal sealed class PriorReadinessActivation({{holder}} workAdmission)
                {
                    {{initializer}}
                }
            }
            """;

        Assert.Empty(Compile(source).GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        AssertExplicitReadinessDiscovery(source, "PerUnitCoordinator", shape == "prior-resolution" ? "owned-lease" : shape);
    }
}
