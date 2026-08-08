using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// End-to-end contract for the expanded <c>arcanum doctor</c> surface (issue #33). The three
/// invariants worth pinning at this level are the ones a unit test cannot reach: the pre-#33
/// <c>--json</c> document still parses and still carries its original check names, an unrecognized
/// id is a closed configuration error rather than a silently smaller diagnostic, and a repair
/// changes nothing without an explicit, confirmed <c>--apply</c>.
/// </summary>
[Collection("GlobalConsole")]
public sealed class DoctorDiagnosticsCommandTests : IDisposable
{

    private readonly string _testHome =
        Path.Combine(Path.GetTempPath(), "arcanum-tests", $"doctor-diag-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public DoctorDiagnosticsCommandTests()
    {

        Directory.CreateDirectory(_testHome);

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

    }

    [Fact]
    public async Task Every_check_in_the_report_carries_a_stable_id_and_subsystem()
    {

        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.All(report.Checks, check => Assert.False(string.IsNullOrWhiteSpace(check.Id)));

        Assert.All(report.Checks, check => Assert.NotNull(check.Subsystem));

        Assert.All(report.Checks, check => Assert.NotNull(check.Outcome));

        // The id namespace is the same vocabulary --only and --skip resolve against.
        Assert.All(
            report.Checks,
            check => Assert.StartsWith(
                check.Subsystem!.Value.ToString().ToLowerInvariant() + ".",
                check.Id,
                StringComparison.Ordinal));

    }

    [Fact]
    public async Task The_report_carries_an_aggregate_outcome_alongside_the_legacy_healthy_flag()
    {

        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.NotNull(report.Outcome);

        Assert.Equal(
            DoctorOutcomes.Aggregate(report.Checks.Select(check => check.Outcome!.Value)),
            report.Outcome);

    }

    [Fact]
    public async Task Every_check_that_needs_attention_names_a_command_the_operator_can_run()
    {

        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        DoctorReport report = Deserialize(result.Output);

        IReadOnlyList<DoctorCheck> failing =
            [.. report.Checks.Where(check => check.Outcome == DoctorOutcome.Unhealthy)];

        Assert.All(
            failing,
            check => Assert.All(
                check.Remedies ?? [],
                remedy => Assert.StartsWith("arcanum ", remedy.Command, StringComparison.Ordinal)));

    }

    [Fact]
    public async Task Only_runs_a_single_subsystem()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--only", "runtime"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.NotEmpty(report.Checks);

        Assert.All(report.Checks, check => Assert.Equal(DoctorSubsystem.Runtime, check.Subsystem));

    }

    [Fact]
    public async Task Only_a_subsystem_covers_its_legacy_panel_checks_too()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--only", "runtime"]);

        DoctorReport report = Deserialize(result.Output);

        // runtime.tool_child_sandbox is one of the pre-#33 panel checks. A selector that silently
        // dropped it would report a smaller diagnostic than the operator asked for.
        Assert.Contains(report.Checks, check => check.Id == "runtime.tool_child_sandbox");

        Assert.Contains(report.Checks, check => check.Id == "runtime.pid_file");

        Assert.All(report.Checks, check => Assert.Equal(DoctorSubsystem.Runtime, check.Subsystem));

    }

    [Fact]
    public async Task Skip_removes_only_the_named_subsystem_and_keeps_every_other_legacy_check()
    {

        CliTestResult unfiltered = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        CliTestResult skipped = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--skip", "mcp"]);

        DoctorReport all = Deserialize(unfiltered.Output);

        DoctorReport withoutMcp = Deserialize(skipped.Output);

        Assert.DoesNotContain(withoutMcp.Checks, check => check.Subsystem == DoctorSubsystem.Mcp);

        Assert.Equal(
            all.Checks.Count(check => check.Subsystem != DoctorSubsystem.Mcp),
            withoutMcp.Checks.Count);

        // The pre-#33 checks are not one indivisible block: skipping MCP must not take them all out.
        Assert.Contains(withoutMcp.Checks, check => check.Id == "system.version");

    }

    [Fact]
    public async Task A_selector_naming_a_legacy_check_id_is_recognized()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--only", "system.tokenizer"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.Equal("system.tokenizer", Assert.Single(report.Checks).Id);

    }

    [Fact]
    public async Task An_unrecognized_id_exits_with_the_configuration_error_code()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--only", "runtime.not_a_real_check"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("arcanum doctor list", result.Error);

    }

    [Fact]
    public async Task Apply_without_a_repair_selector_is_rejected_rather_than_repairing_everything()
    {

        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--apply"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("--repair", result.Error);

    }

    [Fact]
    public async Task Network_probes_do_not_run_unless_the_operator_opts_in()
    {

        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        DoctorReport report = Deserialize(result.Output);

        DoctorCheck providers = Assert.Single(
            report.Checks,
            check => check.Id == "providers.reachability");

        Assert.Equal(DoctorOutcome.Skipped, providers.Outcome);

    }

    [Fact]
    public async Task List_emits_one_json_document_naming_every_id()
    {

        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "list", "--json"]);

        DoctorCatalog? catalog = JsonSerializer.Deserialize(
            result.Output,
            DoctorReportJsonContext.Default.DoctorCatalog);

        Assert.NotNull(catalog);

        Assert.NotEmpty(catalog.Checks);

        Assert.NotEmpty(catalog.Repairs);

        // Every repair must be discoverable and must name a detector that is itself listed.
        Assert.All(
            catalog.Repairs,
            repair => Assert.All(
                repair.DetectorIds,
                detectorId => Assert.Contains(catalog.Checks, check => check.Id == detectorId)));

    }

    [Fact]
    public async Task Explain_describes_a_repair_and_the_detector_that_justifies_it()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "explain", "permissions.apply_owner_only", "--json"]);

        DoctorCatalog? catalog = JsonSerializer.Deserialize(
            result.Output,
            DoctorReportJsonContext.Default.DoctorCatalog);

        Assert.NotNull(catalog);

        DoctorCatalogRepair repair = Assert.Single(catalog.Repairs);

        Assert.Equal("permissions.apply_owner_only", repair.Id);

        Assert.Contains("permissions.posture", repair.DetectorIds);

    }

    [Fact]
    public async Task Explain_rejects_an_unknown_id_with_the_configuration_error_code()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "explain", "nope.not_real"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("arcanum doctor list", result.Error);

    }

    [Fact]
    public async Task A_repair_without_apply_reports_a_plan_and_changes_nothing()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--repair", "paths.create_managed_directories"]);

        DoctorReport report = Deserialize(result.Output);

        DoctorRepairResult repair = Assert.Single(report.Repairs ?? []);

        Assert.Equal(DoctorRepairState.Planned, repair.State);

        Assert.NotEmpty(repair.Steps);

        Assert.False(Directory.Exists(ArcanumPaths.AttachmentsDirectory));

    }

    [Fact]
    public async Task A_declined_repair_is_a_success_that_changed_nothing()
    {

        ServiceCollection services = BuildServices();

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(false));

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["doctor", "--repair", "paths.create_managed_directories", "--apply"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("Nothing was changed", result.Error);

    }

    [Fact]
    public async Task A_confirmed_repair_applies_and_a_second_run_converges()
    {

        ServiceCollection services = BuildServices();

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult first = await CliTestHarness.RunAsync(
            services,
            ["doctor", "--json", "--repair", "paths.create_managed_directories", "--apply"]);

        DoctorRepairResult applied = Assert.Single(Deserialize(first.Output).Repairs ?? []);

        Assert.Equal(DoctorRepairState.Applied, applied.State);

        Assert.True(Directory.Exists(ArcanumPaths.AttachmentsDirectory));

        ServiceCollection again = BuildServices();

        again.RemoveAll<IConfirmationPrompt>();

        again.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult second = await CliTestHarness.RunAsync(
            again,
            ["doctor", "--json", "--repair", "paths.create_managed_directories", "--apply"]);

        DoctorRepairResult converged = Assert.Single(Deserialize(second.Output).Repairs ?? []);

        Assert.Equal(DoctorRepairState.AlreadyConverged, converged.State);

        Assert.Empty(converged.Steps);

    }

    [Fact]
    public async Task Fix_permissions_with_json_emits_the_typed_report_rather_than_a_text_payload()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--fix-permissions"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.NotEmpty(report.Checks);

        DoctorRepairResult repair = Assert.Single(report.Repairs ?? []);

        Assert.Equal("permissions.apply_owner_only", repair.RepairId);

        Assert.NotEqual(DoctorRepairState.Planned, repair.State);

    }

    [Fact]
    public async Task No_finding_or_remedy_ever_carries_a_credential_value()
    {

        const string secretValue = "sk-live-must-never-appear";

        ServiceCollection services = BuildServices();

        services.AddSingleton<ISecretStore>(new SecretBearingStore(secretValue));

        CliTestResult result = await CliTestHarness.RunAsync(services, ["doctor", "--json"]);

        Assert.DoesNotContain(secretValue, result.Output);

        Assert.DoesNotContain(secretValue, result.Error);

    }

    [Fact]
    public async Task A_repair_id_passed_to_only_is_rejected_instead_of_reporting_an_empty_all_clear()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--only", "permissions.apply_owner_only"]);

        // 'arcanum doctor list' prints repair ids and check ids in one document, so picking the
        // wrong column is the expected mistake. Accepting it would run zero checks and report a
        // clean bill of health that a CI gate would then trust.
        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("--repair permissions.apply_owner_only", result.Error);

    }

    [Fact]
    public async Task A_selection_that_matches_nothing_is_rejected_instead_of_reporting_healthy()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--only", "paths", "--skip", "paths"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("selects no diagnostics", result.Error);

    }

    [Fact]
    public async Task Fix_permissions_does_not_force_apply_another_repair_or_skip_its_confirmation()
    {

        ServiceCollection services = BuildServices();

        services.RemoveAll<IConfirmationPrompt>();

        RecordingConfirmationPrompt prompt = new(answer: false);

        services.AddSingleton<IConfirmationPrompt>(prompt);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["doctor", "--json", "--fix-permissions", "--repair", "paths.create_managed_directories"]);

        // Without --apply the co-named repair must stay a plan, and the alias must not become a
        // back door that applies it unconfirmed.
        DoctorReport report = Deserialize(result.Output);

        DoctorRepairResult directories = Assert.Single(
            report.Repairs ?? [],
            repair => repair.RepairId == "paths.create_managed_directories");

        Assert.Equal(DoctorRepairState.Planned, directories.State);

        Assert.False(Directory.Exists(ArcanumPaths.AttachmentsDirectory));

        Assert.Equal(0, prompt.Invocations);

    }

    [Fact]
    public async Task Fix_permissions_keeps_exiting_zero_when_other_checks_are_unhealthy()
    {

        // Pre-#33 this always returned 0. A fresh installation legitimately has failing local checks,
        // so scripts that harden permissions must not start failing because the report grew.
        CliTestResult report = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        Assert.Equal(
            DoctorOutcome.Unhealthy,
            Deserialize(report.Output).Outcome);

        CliTestResult fix = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--fix-permissions"]);

        Assert.Equal((int)CliExitCode.Success, fix.ExitCode);

    }

    [Fact]
    public async Task An_absent_optional_configuration_file_is_skipped_so_strict_passes_a_default_install()
    {

        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--only", "configuration.file", "--only", "mcp.global_config"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.All(report.Checks, check => Assert.Equal(DoctorOutcome.Skipped, check.Outcome));

        Assert.All(report.Checks, check => Assert.Equal("ok", check.Status));

        CliTestResult strict = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--only", "configuration.file", "--only", "mcp.global_config", "--strict"]);

        Assert.Equal((int)CliExitCode.Success, strict.ExitCode);

    }

    [Fact]
    public async Task A_filtered_run_does_not_probe_the_host_it_was_told_to_skip()
    {

        ServiceCollection services = BuildServices();

        CountingHttpClientFactory counter = new();

        services.AddSingleton<IHttpClientFactory>(counter);

        _ = await CliTestHarness.RunAsync(services, ["doctor", "--json", "--only", "runtime"]);

        // '--only runtime' excludes both host checks, so the diagnostic must not reach the network
        // at all — filtering results after paying for them is not filtering.
        Assert.Equal(0, counter.Sends);

    }

    private ServiceCollection BuildServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<ISecretStore>(new NullSecretStore());

        // Deterministic: never depend on whether a local 'arcanum serve' happens to be listening.
        services.AddSingleton<IHttpClientFactory>(new ConnectionRefusedHttpClientFactory());

        return services;

    }

    private static DoctorReport Deserialize(string output)
    {

        DoctorReport? report = JsonSerializer.Deserialize(
            output,
            DoctorReportJsonContext.Default.DoctorReport);

        Assert.NotNull(report);

        return report;

    }

    private sealed class RecordingConfirmationPrompt(bool answer) : IConfirmationPrompt
    {

        public int Invocations { get; private set; }

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken = default)
        {

            Invocations++;

            return Task.FromResult(answer);

        }

    }

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {

        private readonly CountingHandler _handler = new();

        public int Sends => _handler.Sends;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

    }

    private sealed class CountingHandler : HttpMessageHandler
    {

        private int _sends;

        public int Sends => _sends;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _sends);

            throw new HttpRequestException(
                "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused));

        }

    }

    private sealed class StubConfirmationPrompt(bool answer) : IConfirmationPrompt
    {

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(answer);

    }

    private sealed class NullSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class SecretBearingStore(string secret) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(secret);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(secret));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(secret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class ConnectionRefusedHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(new ConnectionRefusedHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

    }

    private sealed class ConnectionRefusedHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused));

    }

    public void Dispose()
    {

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

        if (Directory.Exists(_testHome))
        {

            Directory.Delete(_testHome, recursive: true);

        }

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

}
