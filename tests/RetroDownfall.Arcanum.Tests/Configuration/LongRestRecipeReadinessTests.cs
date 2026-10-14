using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Diagnostics;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Configuration;

/// <summary>
/// "LongRest: enabled but recipe unresolved" is one fact with one set of predicates: the validator warning, the doctor row, the
/// health row and the runtime epoch's recipe all read <see cref="LongRestRecipeResolver"/>. It is a warning, never a startup
/// refusal, and an ordinary turn is unaffected; a turn that must finish protected Long Rest maintenance first still ends.
/// </summary>
public sealed class LongRestRecipeReadinessTests
{
    public static TheoryData<string> Unresolved => new()
    {
        "fast-model-matches-nothing",
        "no-embedding-model",
        "no-embedding-dimensions",
        "embedding-provider-missing",
        "chat-provider-not-openai-compatible",
        "embedding-provider-relative-endpoint",
        "default-model-matches-nothing",
    };

    private static ArcanumSettings Settings(string? variant = null)
    {
        ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

        switch (variant)
        {
            case "fast-model-matches-nothing": settings.FastModel = "not-configured"; break;

            case "no-embedding-model": settings.Integrations.Embeddings.Model = null; break;

            case "no-embedding-dimensions": settings.Integrations.Embeddings.Dimensions = 0; break;

            case "embedding-provider-missing": settings.Integrations.Embeddings.Provider = "nowhere"; break;

            case "chat-provider-not-openai-compatible": settings.Providers[0].Type = AiProviderKind.ClaudeCodeCli; break;

            case "embedding-provider-relative-endpoint": settings.Providers[0].Endpoint = "not-a-uri"; break;

            case "default-model-matches-nothing": settings.DefaultModel = "not-configured"; break;
        }

        return settings;
    }

    [Fact]
    public void An_enabled_feature_with_a_resolvable_recipe_has_no_warning_and_a_healthy_row()
    {
        ArcanumSettings settings = Settings();

        Assert.Empty(new ConfigurationValidator().ValidateLongRest(settings));

        Assert.Empty(LongRestRecipeResolver.Problems(settings));

        HealthComponentDto row = ArcanumHealthChecker.BuildLongRestComponent(settings)!;

        Assert.Equal("LongRest", row.Name);

        Assert.Equal(HealthStatus.Healthy, row.Status);
    }

    [Theory]
    [MemberData(nameof(Unresolved))]
    public void An_enabled_feature_whose_recipe_is_unresolved_warns_in_the_validator_and_degrades_the_health_row(string variant)
    {
        ArcanumSettings settings = Settings(variant);

        ConfigurationValidationError warning = Assert.Single(new ConfigurationValidator().ValidateLongRest(settings));

        Assert.Equal("features.longRest", warning.Pointer);

        Assert.StartsWith("LongRest: enabled but recipe unresolved", warning.Detail, StringComparison.Ordinal);

        HealthComponentDto row = ArcanumHealthChecker.BuildLongRestComponent(settings)!;

        Assert.Equal(HealthStatus.Degraded, row.Status);

        Assert.StartsWith("LongRest: enabled but recipe unresolved", row.Detail, StringComparison.Ordinal);

        // Keys and model names only: neither an endpoint nor a credential setting may appear.
        Assert.DoesNotContain("provider.test", warning.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain("provider.test", row.Detail, StringComparison.Ordinal);
    }

    // An unresolved recipe does not refuse startup or an ordinary turn, but it does end a turn that must finish protected Long Rest
    // maintenance first (protected failures are fatal, and the protected request needs the recipe). Every reader says so, and none
    // of them claims that no turn is refused.
    [Theory]
    [MemberData(nameof(Unresolved))]
    public async Task Every_unresolved_recipe_report_names_the_protected_turn_refusal_and_never_claims_no_turn_is_refused(string variant)
    {
        ArcanumSettings settings = Settings(variant);

        string warning = Assert.Single(new ConfigurationValidator().ValidateLongRest(settings)).Detail;

        string health = ArcanumHealthChecker.BuildLongRestComponent(settings)!.Detail!;

        string doctor = (await new LongRestRecipeCheck(Options.Create(settings), new ConfigurationValidator()).InspectAsync(CancellationToken.None)).Detail!;

        foreach (string text in new[] { warning, health, doctor })
        {
            Assert.StartsWith("LongRest: enabled but recipe unresolved", text, StringComparison.Ordinal);

            Assert.Contains("protected", text, StringComparison.Ordinal);

            Assert.Contains(ErrorCodes.LongRest.Unavailable, text, StringComparison.Ordinal);

            Assert.DoesNotContain("not refused", text, StringComparison.Ordinal);

            Assert.DoesNotContain("no turn is refused", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Unresolved))]
    public void The_warning_row_and_epoch_recipe_agree_because_they_share_one_set_of_predicates(string variant)
    {
        ArcanumSettings settings = Settings(variant);

        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        Assert.True(((ILongRestConfigurationProvider)epoch).Capture().Recipe.IsFailure);

        Assert.True(LongRestRecipeResolver.Resolve(settings).IsFailure);

        Assert.NotEmpty(LongRestRecipeResolver.Problems(settings));

        // And the resolvable settings agree the other way.
        ArcanumSettings fine = Settings();

        using LongRestConfigurationEpoch good = new(new LongRestConfigurationEpochTests.NotifyingOptions(fine), Guid.NewGuid());

        Assert.True(((ILongRestConfigurationProvider)good).Capture().Recipe.IsSuccess);

        Assert.True(LongRestRecipeResolver.Resolve(fine).IsSuccess);
    }

    [Theory]
    [MemberData(nameof(Unresolved))]
    public void The_recipe_warning_never_refuses_startup_or_validation_and_is_logged(string variant)
    {
        CapturingLogger logger = new();

        Result result = new ConfigurationValidator(logger).Validate(Settings(variant));

        // Other, unrelated validation may still object to a broken setting, but the recipe warning is never one of the errors.
        Assert.True(result.IsSuccess || result.Error.Details!.All(static detail => detail.Pointer != "features.longRest"));

        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Warning
            && entry.Text.Contains("LongRest: enabled but recipe unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disabled_feature_never_warns_and_has_no_health_row_however_broken_its_recipe_is()
    {
        ArcanumSettings settings = Settings("fast-model-matches-nothing");

        settings.Features.LongRest = false;

        CapturingLogger logger = new();

        Assert.Empty(new ConfigurationValidator(logger).ValidateLongRest(settings));

        _ = new ConfigurationValidator(logger).Validate(settings);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Text.Contains("LongRest:", StringComparison.Ordinal));

        Assert.Null(ArcanumHealthChecker.BuildLongRestComponent(settings));
    }

    [Fact]
    public async Task The_doctor_row_is_skipped_when_off_healthy_when_resolved_and_degraded_with_a_remedy_when_unresolved()
    {
        ArcanumSettings off = Settings();

        off.Features.LongRest = false;

        DoctorFinding skipped = await new LongRestRecipeCheck(Options.Create(off), new ConfigurationValidator()).InspectAsync(CancellationToken.None);

        Assert.Equal(DoctorOutcome.Skipped, skipped.Outcome);

        DoctorFinding healthy = await new LongRestRecipeCheck(Options.Create(Settings()), new ConfigurationValidator()).InspectAsync(CancellationToken.None);

        Assert.Equal(DoctorOutcome.Healthy, healthy.Outcome);

        DoctorFinding degraded = await new LongRestRecipeCheck(Options.Create(Settings("fast-model-matches-nothing")), new ConfigurationValidator())
            .InspectAsync(CancellationToken.None);

        Assert.Equal(DoctorOutcome.Degraded, degraded.Outcome);

        Assert.StartsWith("LongRest: enabled but recipe unresolved", degraded.Detail, StringComparison.Ordinal);

        Assert.Contains(degraded.Remedies!, static remedy => remedy.Command == DoctorRemedyCommands.ConfigEdit);

        Assert.Equal("configuration.long_rest_recipe", LongRestRecipeCheck.CheckId);

        Assert.Equal(DoctorSubsystem.Configuration, new LongRestRecipeCheck(Options.Create(off), new ConfigurationValidator()).Subsystem);
    }

    private sealed class CapturingLogger : ILogger<ConfigurationValidator>
    {
        internal List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
