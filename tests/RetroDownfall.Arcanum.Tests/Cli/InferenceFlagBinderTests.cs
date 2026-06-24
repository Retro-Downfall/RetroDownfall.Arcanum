using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class InferenceFlagBinderTests
{

    [Fact]
    public void TryParse_returns_nullables_for_empty_inputs()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {
            TestInferenceInputs inputs = new();

            ConfiguredThemePalette palette = CreatePalette();

            bool ok = InferenceFlagBinder.TryParse(inputs, palette, out InferenceFlagBinder.Parsed parsed, out int exitCode);

            Assert.True(ok);

            Assert.Equal(0, exitCode);

            Assert.Null(parsed.Temperature);

            Assert.Null(parsed.TopP);

            Assert.Null(parsed.MaxOutputTokens);

            Assert.Null(parsed.Seed);

            Assert.Null(parsed.Stop);

            Assert.Null(parsed.ResponseFormat);

            Assert.Null(parsed.PresencePenalty);

            Assert.Null(parsed.FrequencyPenalty);

            Assert.Empty(console.Output);
        }
        finally
        {
            AnsiConsole.Console = prior;
        }

    }

    [Fact]
    public void TryParse_parses_valid_numeric_flags()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {
            TestInferenceInputs inputs = new()
            {
                Temperature = "0.7",
                TopP = "0.9",
                MaxTokens = "128",
                Seed = "42",
                PresencePenalty = "-0.5",
                FrequencyPenalty = "1.25",
            };

            ConfiguredThemePalette palette = CreatePalette();

            bool ok = InferenceFlagBinder.TryParse(inputs, palette, out InferenceFlagBinder.Parsed parsed, out int exitCode);

            Assert.True(ok);

            Assert.Equal(0, exitCode);

            Assert.Equal(0.7f, parsed.Temperature);

            Assert.Equal(0.9f, parsed.TopP);

            Assert.Equal(128, parsed.MaxOutputTokens);

            Assert.Equal(42L, parsed.Seed);

            Assert.Equal(-0.5f, parsed.PresencePenalty);

            Assert.Equal(1.25f, parsed.FrequencyPenalty);

            Assert.Empty(console.Output);
        }
        finally
        {
            AnsiConsole.Console = prior;
        }

    }

    [Fact]
    public void TryParse_normalizes_response_format_aliases()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {
            TestInferenceInputs inputs = new() { ResponseFormat = "JSON" };

            ConfiguredThemePalette palette = CreatePalette();

            bool ok = InferenceFlagBinder.TryParse(inputs, palette, out InferenceFlagBinder.Parsed parsed, out int exitCode);

            Assert.True(ok);

            Assert.Equal("json_object", parsed.ResponseFormat);

            Assert.Empty(console.Output);
        }
        finally
        {
            AnsiConsole.Console = prior;
        }

    }

    [Fact]
    public void TryParse_filters_empty_stop_sequences()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {
            TestInferenceInputs inputs = new() { Stop = ["END", "", "STOP"] };

            ConfiguredThemePalette palette = CreatePalette();

            bool ok = InferenceFlagBinder.TryParse(inputs, palette, out InferenceFlagBinder.Parsed parsed, out int exitCode);

            Assert.True(ok);

            Assert.NotNull(parsed.Stop);

            Assert.Equal(["END", "STOP"], parsed.Stop);

            Assert.Empty(console.Output);
        }
        finally
        {
            AnsiConsole.Console = prior;
        }

    }

    [Fact]
    public void TryParse_rejects_out_of_range_temperature()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {
            TestInferenceInputs inputs = new() { Temperature = "3" };

            ConfiguredThemePalette palette = CreatePalette();

            bool ok = InferenceFlagBinder.TryParse(inputs, palette, out InferenceFlagBinder.Parsed parsed, out int exitCode);

            Assert.False(ok);

            Assert.Equal(1, exitCode);

            Assert.Contains("at most 2", console.Output, StringComparison.Ordinal);

        }
        finally
        {
            AnsiConsole.Console = prior;
        }

    }

    [Fact]
    public void TryParse_rejects_invalid_temperature()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {
            TestInferenceInputs inputs = new() { Temperature = "hot" };

            ConfiguredThemePalette palette = CreatePalette();

            bool ok = InferenceFlagBinder.TryParse(inputs, palette, out InferenceFlagBinder.Parsed parsed, out int exitCode);

            Assert.False(ok);

            Assert.Equal(1, exitCode);

            Assert.Contains("--temperature", console.Output);

            Assert.Contains("must be a number", console.Output);
        }
        finally
        {
            AnsiConsole.Console = prior;
        }

    }

    [Fact]
    public void TryParse_rejects_unknown_response_format()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {
            TestInferenceInputs inputs = new() { ResponseFormat = "yaml" };

            ConfiguredThemePalette palette = CreatePalette();

            bool ok = InferenceFlagBinder.TryParse(inputs, palette, out InferenceFlagBinder.Parsed parsed, out int exitCode);

            Assert.False(ok);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            AnsiConsole.Console = prior;
        }

    }

    private static ConfiguredThemePalette CreatePalette()
    {

        ThemeSemanticColors semantic = new();

        ThemeSemanticColors fallback = new();

        return new ConfiguredThemePalette(semantic, fallback);

    }

    private sealed class TestInferenceInputs : IInferenceFlagInputs
    {

        public string? Temperature { get; init; }

        public string? TopP { get; init; }

        public string? MaxTokens { get; init; }

        public string? Seed { get; init; }

        public string[]? Stop { get; init; }

        public string? ResponseFormat { get; init; }

        public string? PresencePenalty { get; init; }

        public string? FrequencyPenalty { get; init; }

    }

}
