using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Diagnostics;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class BudgetEnforcementReadinessTests
{
    [Theory]
    [InlineData(true, 0, DoctorOutcome.Degraded)]
    [InlineData(false, 0, DoctorOutcome.Skipped)]
    [InlineData(true, 10, DoctorOutcome.Healthy)]
    public async Task Registered_offline_budget_diagnostic_distinguishes_missing_limit_from_disabled_and_enforced(bool enabled, int limit, DoctorOutcome expected)
    {
        ServiceCollection services = new();

        services.AddSingleton<IOptions<ArcanumSettings>>(Options.Create(new ArcanumSettings
        {
            Cost = new CostSettings { Budget = new BudgetPolicySettings { Enabled = enabled, DailyLimitUsd = limit } },
        }));

        services.AddSingleton<ConfigurationValidator>();

        services.AddArcanumDoctorDiagnostics();

        ServiceDescriptor descriptor = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDoctorCheck)
            && descriptor.ImplementationType?.Name == "BudgetEnforcementCheck");

        using ServiceProvider provider = services.BuildServiceProvider();

        IDoctorCheck check = (IDoctorCheck)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

        Assert.Equal("configuration.budget_enforcement", check.Id);

        Assert.False(check.RequiresNetwork);

        DoctorFinding finding = await check.InspectAsync(CancellationToken.None);

        Assert.Equal(expected, finding.Outcome);

        if (expected == DoctorOutcome.Degraded)
        {
            Assert.Contains("not enforced", finding.Detail, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("Arcanum:Cost:Budget:DailyLimitUsd", finding.Detail, StringComparison.Ordinal);

            Assert.NotEmpty(finding.Remedies!);
        }
    }
}
