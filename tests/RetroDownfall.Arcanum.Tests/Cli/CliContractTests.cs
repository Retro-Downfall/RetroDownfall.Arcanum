using System.Text.Json;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class CliContractTests
{

    [Fact]
    public void Root_help_exposes_recursive_machine_and_automation_flags()
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(services, "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("--json", result.Output, StringComparison.Ordinal);

        Assert.Contains("--plain", result.Output, StringComparison.Ordinal);

        Assert.Contains("--yes", result.Output, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--plain")]
    [InlineData("--yes")]
    public void Global_flags_are_valid_after_a_subcommand(string flag)
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(services, "operation", "--help", flag);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.DoesNotContain("Unrecognized command or argument", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Invalid_command_line_uses_configuration_exit_code()
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(services, "--definitely-not-an-arcanum-option");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

    }

    [Fact]
    public void Invalid_json_command_line_keeps_a_valid_payload_and_stderr_diagnostic()
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(
            services,
            "--json",
            "--definitely-not-an-arcanum-option");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(
            (int)CliExitCode.ConfigurationError,
            document.RootElement.GetProperty("exitCode").GetInt32());

        Assert.Contains("invalid", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Json_wraps_legacy_command_payload_as_one_valid_stdout_document()
    {

        ServiceCollection services = CreateServices();

        CliTestResult result = CliTestHarness.Run(
            services,
            "operation",
            "--help",
            "--json");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(
            (int)CliExitCode.Success,
            document.RootElement.GetProperty("exitCode").GetInt32());

        Assert.Contains(
            "cancel",
            document.RootElement.GetProperty("output").GetString(),
            StringComparison.OrdinalIgnoreCase);

        Assert.Empty(result.Error);

    }

    [Fact]
    public async Task Unhandled_exception_is_redacted_and_uses_generic_exit_code()
    {

        const string Secret = "sk-super-secret-value";

        ServiceCollection services = CreateServices();

        services.AddSingleton<ICliEnvironment>(new FakeCliEnvironment());

        services.AddTransient<ICommandCenterHost>(
            _ => new ThrowingCommandCenterHost(Secret));

        CliTestResult result = await CliTestHarness.RunAsync(services, []);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains("unexpected", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(Secret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(Secret, result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Console_dispatcher_keeps_payloads_and_diagnostics_on_separate_streams()
    {

        StringWriter standardOutput = new();

        StringWriter standardError = new();

        CliInvocationOptions options = new(Json: false, Plain: false, Yes: false);

        ConsoleDispatcher dispatcher = new(standardOutput, standardError, options);

        dispatcher.WritePayload("payload");

        dispatcher.WriteDiagnostic("diagnostic");

        string newLine = global::System.Environment.NewLine;

        Assert.Equal($"payload{newLine}", standardOutput.ToString());

        Assert.Equal($"diagnostic{newLine}", standardError.ToString());

    }

    [Fact]
    public void Plain_dispatcher_removes_ansi_from_both_streams()
    {

        StringWriter standardOutput = new();

        StringWriter standardError = new();

        CliInvocationOptions options = new(Json: false, Plain: true, Yes: false);

        ConsoleDispatcher dispatcher = new(standardOutput, standardError, options);

        dispatcher.WritePayload("\u001b[31mpayload\u001b[0m");

        dispatcher.WriteDiagnostic("\u001b[33mdiagnostic\u001b[0m");

        string newLine = global::System.Environment.NewLine;

        Assert.Equal($"payload{newLine}", standardOutput.ToString());

        Assert.Equal($"diagnostic{newLine}", standardError.ToString());

    }

    [Fact]
    public void Plain_dispatcher_removes_osc_hyperlinks()
    {

        StringWriter standardOutput = new();

        StringWriter standardError = new();

        CliInvocationOptions options = new(Json: false, Plain: true, Yes: false);

        ConsoleDispatcher dispatcher = new(standardOutput, standardError, options);

        dispatcher.WritePayload(
            "\u001b]8;;https://example.invalid\u001b\\payload\u001b]8;;\u001b\\");

        string newLine = global::System.Environment.NewLine;

        Assert.Equal(
            $"payload{newLine}",
            standardOutput.ToString());

    }

    [Fact]
    public async Task Confirmation_fails_closed_when_output_is_redirected_without_yes()
    {

        StringWriter standardOutput = new();

        StringWriter standardError = new();

        CliInvocationOptions options = new(Json: false, Plain: true, Yes: false);

        ConsoleDispatcher dispatcher = new(standardOutput, standardError, options);

        ConfirmationPrompt prompt = new(
            dispatcher,
            options,
            new StringReader("y" + global::System.Environment.NewLine),
            isOutputRedirected: static () => true);

        await Assert.ThrowsAsync<NonInteractiveConfirmationException>(
            () => prompt.PromptForConfirmationAsync(
                "Delete everything?",
                CancellationToken.None));

        Assert.Empty(standardOutput.ToString());

        Assert.Empty(standardError.ToString());

    }

    [Fact]
    public async Task Confirmation_yes_auto_approves_without_reading_or_prompting()
    {

        StringWriter standardOutput = new();

        StringWriter standardError = new();

        CliInvocationOptions options = new(Json: false, Plain: true, Yes: true);

        ConsoleDispatcher dispatcher = new(standardOutput, standardError, options);

        ConfirmationPrompt prompt = new(
            dispatcher,
            options,
            new ThrowingTextReader(),
            isOutputRedirected: static () => true);

        bool confirmed = await prompt.PromptForConfirmationAsync(
            "Delete everything?",
            CancellationToken.None);

        Assert.True(confirmed);

        Assert.Empty(standardOutput.ToString());

        Assert.Empty(standardError.ToString());

    }

    [Fact]
    public void Network_failures_map_to_network_exit_code_without_exposing_messages()
    {

        const string Secret = "Bearer secret-token";

        CliFailure failure = CliFailureMapper.Map(
            new HttpRequestException(Secret));

        Assert.Equal(CliExitCode.NetworkError, failure.ExitCode);

        Assert.DoesNotContain(Secret, failure.SafeMessage, StringComparison.Ordinal);

    }

    private static ServiceCollection CreateServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        return services;

    }

    private sealed class FakeCliEnvironment : ICliEnvironment
    {

        public bool IsInteractive => true;

        public bool ColorEnabled => true;

        public bool ShouldShowManaBar => true;

    }

    private sealed class ThrowingCommandCenterHost(string secret) : ICommandCenterHost
    {

        public Task<int> RunAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(secret);

    }

    private sealed class ThrowingTextReader : TextReader
    {

        public override string? ReadLine() =>
            throw new InvalidOperationException("Input must not be read.");

    }

}
