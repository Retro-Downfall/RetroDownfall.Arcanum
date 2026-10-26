using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Diagnostics;
using RetroDownfall.Arcanum.Core.Cli;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]
public sealed class CampaignRollupConfigurationTests : IDisposable
{
    private readonly ArcanumTestHomeScope _home = new("arcanum-campaign-rollup-config");

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"campaignRollups\":true}", true)]
    [InlineData("{\"campaignRollups\":false}", false)]
    public void Canonical_configuration_projects_the_explicit_campaign_rollup_gate(string features, bool expected)
    {
        string path = Path.Combine(_home.Root, "campaign-rollups.json");

        File.WriteAllText(path, "{\"Arcanum\":{\"features\":" + features + "}}");

        ArcanumSettings settings = ConfigurationBootstrapper.LoadArcanumSettingsFile(path);

        using JsonDocument roundtrip = JsonDocument.Parse(JsonSerializer.Serialize(
            settings,
            ConfigurationJsonContext.Default.ArcanumSettings));

        Assert.True(roundtrip.RootElement.GetProperty("features").TryGetProperty("campaignRollups", out JsonElement gate));

        Assert.Equal(expected, gate.GetBoolean());

        IntelligenceSettings runtime = settings.ResolveIntelligence();

        Assert.Equal(expected, runtime.EnableCampaignRollups);
    }

    [Theory]
    [InlineData(true, AiProviderKind.ClaudeCodeCli, false, true)]
    [InlineData(true, AiProviderKind.CodexCli, false, true)]
    [InlineData(true, AiProviderKind.ClaudeCodeCli, true, false)]
    [InlineData(false, AiProviderKind.ClaudeCodeCli, false, false)]
    [InlineData(true, AiProviderKind.OpenAICompatible, false, false)]
    public void Startup_warning_uses_the_effective_maintenance_provider_without_refusing_turns(
        bool enabled, AiProviderKind kind, bool compatibleFast, bool expectedWarning)
    {
        ArcanumSettings settings = MaintenanceSettings(enabled, kind, compatibleFast);

        TestCapturingLogger<ConfigurationValidator> logger = new();

        var validation = new ConfigurationValidator(logger).Validate(settings);

        Assert.True(validation.IsSuccess, validation.IsFailure ? validation.Error.Message : null);

        TestLogEntry[] warnings = [.. logger.Entries.Where(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("features.campaignRollups", StringComparison.Ordinal))];

        Assert.Equal(expectedWarning ? 1 : 0, warnings.Length);

        if (expectedWarning)
        {
            Assert.Contains("OpenAI-compatible", warnings[0].Message, StringComparison.Ordinal);

            Assert.Contains("FastModel", warnings[0].Message, StringComparison.Ordinal);

            Assert.Contains("DefaultModel", warnings[0].Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true, AiProviderKind.ClaudeCodeCli, false, DoctorOutcome.Degraded)]
    [InlineData(true, AiProviderKind.CodexCli, false, DoctorOutcome.Degraded)]
    [InlineData(true, AiProviderKind.ClaudeCodeCli, true, DoctorOutcome.Healthy)]
    [InlineData(false, AiProviderKind.ClaudeCodeCli, false, DoctorOutcome.Skipped)]
    [InlineData(true, AiProviderKind.OpenAICompatible, false, DoctorOutcome.Healthy)]
    public async Task Registered_offline_doctor_reports_the_selected_campaign_maintenance_provider(
        bool enabled, AiProviderKind kind, bool compatibleFast, DoctorOutcome expected)
    {
        ServiceCollection services = new();

        services.AddSingleton<IOptions<ArcanumSettings>>(Options.Create(MaintenanceSettings(enabled, kind, compatibleFast)));

        services.AddSingleton<ConfigurationValidator>();

        services.AddArcanumDoctorDiagnostics();

        ServiceDescriptor descriptor = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDoctorCheck)
            && descriptor.ImplementationType?.Name == "CampaignRollupModelCheck");

        using ServiceProvider provider = services.BuildServiceProvider();

        IDoctorCheck check = (IDoctorCheck)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

        Assert.Equal("configuration.campaign_rollup_model", check.Id);

        Assert.False(check.RequiresNetwork);

        DoctorFinding finding = await check.InspectAsync(CancellationToken.None);

        Assert.Equal(expected, finding.Outcome);

        if (expected == DoctorOutcome.Degraded)
        {
            Assert.Contains("OpenAI-compatible", finding.Detail, StringComparison.Ordinal);

            Assert.Contains(finding.Remedies!, remedy => remedy.Command == DoctorRemedyCommands.ConfigEdit);
        }
    }

    private static ArcanumSettings MaintenanceSettings(bool enabled, AiProviderKind kind, bool compatibleFast) => new()
    {
        Features = new FeatureSettings { CampaignRollups = enabled },
        DefaultModel = "default",
        FastModel = compatibleFast ? "maintenance" : null,
        Providers =
        [
            new() { Name = "primary", Type = kind, Endpoint = kind == AiProviderKind.OpenAICompatible ? "https://provider.test/v1" : string.Empty, Models = ["default"] },
            new() { Name = "fast", Endpoint = "https://fast.test/v1", Models = ["maintenance"] },
        ],
    };

    public void Dispose() => _home.Dispose();
}
