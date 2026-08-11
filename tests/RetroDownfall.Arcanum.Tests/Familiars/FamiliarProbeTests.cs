using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// The probe answers one question — "is this Familiar ready?" — and must answer it without spending
/// anything, without authenticating, and without carrying account material out of the CLI. These
/// facts pin all three, using status output recorded from the real binaries.
/// </summary>
[Collection("ChildProcess")]
public sealed class FamiliarProbeTests
{

    [Fact]
    public async Task A_missing_binary_reports_not_installed_and_tells_the_operator_to_install_it()
    {

        RecordingFamiliarProcessRunner runner = new();

        FamiliarProbeResult result = await Probe(
            runner,
            AiProviderKind.ClaudeCodeCli,
            command: StubFamiliarCli.MissingExecutablePath());

        Assert.Equal(FamiliarProbeStatus.NotInstalled, result.Status);

        Assert.Contains("Arcanum never installs", result.Remediation, StringComparison.Ordinal);

        // Nothing was spawned: resolution is a filesystem question.
        Assert.Empty(runner.Requests);

    }

    [Fact]
    public async Task A_signed_in_claude_reports_configured_with_its_version()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.ClaudeAuthStatusConfigured),
            string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.Equal(FamiliarProbeStatus.Configured, result.Status);

        Assert.Equal(string.Empty, result.Remediation);

        Assert.Null(result.RemediationCommand);

    }

    [Fact]
    public async Task A_signed_out_claude_reports_not_configured_with_a_copyable_sign_in_command()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.ClaudeAuthStatusSignedOut),
            string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.Equal(FamiliarProbeStatus.NotConfigured, result.Status);

        Assert.Equal("claude auth login", result.RemediationCommand);

        Assert.Contains("never authenticates for you", result.Remediation, StringComparison.Ordinal);

    }

    /// <summary>
    /// <c>claude auth status --json</c> prints the account e-mail, organisation id, and organisation
    /// name. None of it may travel: the probe payload is status, version, and remediation.
    /// </summary>
    [Fact]
    public async Task Account_material_from_the_status_command_never_reaches_the_probe_payload()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.ClaudeAuthStatusConfigured),
            string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        string rendered = string.Join(
            '\n',
            result.ProviderName,
            result.ProviderType,
            result.Version,
            result.Summary,
            result.Remediation,
            result.RemediationCommand,
            string.Join(',', result.Models),
            string.Join(',', result.HiddenModels));

        Assert.DoesNotContain("operator@example.invalid", rendered, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Organization", rendered, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("00000000-0000", rendered, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_signed_in_codex_reports_configured_with_the_version_from_its_own_report()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.CodexDoctorConfigured),
            string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.CodexCli, stub.FileName);

        Assert.Equal(FamiliarProbeStatus.Configured, result.Status);

        Assert.Equal("0.147.0", result.Version);

    }

    [Fact]
    public async Task A_signed_out_codex_reports_not_configured()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.CodexDoctorSignedOut),
            string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.CodexCli, stub.FileName);

        Assert.Equal(FamiliarProbeStatus.NotConfigured, result.Status);

        Assert.Equal("codex login", result.RemediationCommand);

    }

    [Fact]
    public async Task Local_paths_from_the_codex_report_never_reach_the_probe_payload()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.CodexDoctorConfigured),
            string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.CodexCli, stub.FileName);

        Assert.DoesNotContain("/Users/", result.Summary, StringComparison.Ordinal);

        Assert.DoesNotContain("auth.json", result.Summary, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Truncated_status_output_reports_not_configured_rather_than_crashing()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            "{\"loggedIn\": tru",
            string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.Equal(FamiliarProbeStatus.NotConfigured, result.Status);

    }

    [Fact]
    public async Task A_non_zero_status_exit_reports_not_configured()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.NonZeroExit,
            1,
            string.Empty,
            "not logged in"));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.Equal(FamiliarProbeStatus.NotConfigured, result.Status);

    }

    /// <summary>
    /// A status command that never answered is not a sign-out. A wedged CLI that hits the probe
    /// deadline, or a binary the OS refused to start, must not send the operator to `auth login` to
    /// fix something that is not broken — the clean non-zero exit above is the one failure that
    /// really does mean "sign in".
    /// </summary>
    [Theory]
    [InlineData(FamiliarProcessFailure.TimedOut)]
    [InlineData(FamiliarProcessFailure.StartFailed)]
    public async Task A_claude_status_command_that_could_not_answer_is_not_reported_as_signed_out(
        FamiliarProcessFailure failure)
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(failure, 0, string.Empty, string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.Equal(FamiliarProbeStatus.NotConfigured, result.Status);

        Assert.DoesNotContain("not signed in", result.Summary, StringComparison.OrdinalIgnoreCase);

        Assert.NotEqual("claude auth login", result.RemediationCommand);

    }

    /// <summary>
    /// Neither CLI publishes a machine-readable catalogue, so "unknown" has to be a first-class,
    /// working outcome — and every surface downstream falls back to free text rather than blocking.
    /// </summary>
    [Fact]
    public async Task An_undeclared_catalogue_is_reported_as_unknown_never_as_a_guess()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.ClaudeAuthStatusConfigured),
            string.Empty));

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.Equal(FamiliarModelEnumeration.Unknown, result.Enumeration);

        Assert.Empty(result.Models);

    }

    [Fact]
    public async Task A_declared_catalogue_is_reported_as_operator_declared_with_the_hide_list_beside_it()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.ClaudeAuthStatusConfigured),
            string.Empty));

        ProviderSettings provider = new()
        {
            Name = "ClaudeCode-subscription",
            Type = AiProviderKind.ClaudeCodeCli,
            Command = stub.FileName,
            Models = ["claude-sonnet", "claude-legacy"],
            HiddenModels = ["claude-legacy", "claude-retired"],
        };

        FamiliarProbeResult result = await new FamiliarProbe(runner)
            .ProbeAsync(provider, CancellationToken.None);

        Assert.Equal(FamiliarModelEnumeration.OperatorDeclared, result.Enumeration);

        Assert.Equal(["claude-sonnet", "claude-legacy"], result.Models);

        // A hidden model the probe does not currently report is retained, not pruned — otherwise a
        // model that vanished for a release would come back unhidden.
        Assert.Equal(["claude-legacy", "claude-retired"], result.HiddenModels);

    }

    /// <summary>
    /// The probe answers a question; it never takes down the surface that asked. A host that cannot
    /// give a Familiar a private directory to run in is reported as unready — and nothing is
    /// spawned, because running the CLI anywhere else is the thing being refused.
    /// </summary>
    [Fact]
    public async Task A_host_that_cannot_provide_a_private_directory_is_reported_not_thrown()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        using TempRootScope scope = new();

        FamiliarProbeResult result = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.Equal(FamiliarProbeStatus.NotConfigured, result.Status);

        Assert.Empty(runner.Requests);

    }

    /// <summary>The probe asks for status, never for a completion — it must cost nothing.</summary>
    [Fact]
    public async Task The_probe_never_asks_the_familiar_for_a_completion()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.ClaudeAuthStatusConfigured),
            string.Empty));

        _ = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.All(
            runner.Requests,
            static request =>
            {

                Assert.DoesNotContain("--print", request.Arguments);

                Assert.DoesNotContain("-p", request.Arguments);

                Assert.Null(request.StandardInput);

            });

        Assert.Contains(
            runner.Requests,
            static request => request.Arguments.SequenceEqual(new[] { "auth", "status", "--json" }));

    }

    /// <summary>
    /// A status spawn is still a spawn. Both CLIs read project state out of their working root —
    /// Claude Code's <c>.claude/settings.json</c>, whose <c>hooks</c> block runs shell commands, and
    /// Codex's <c>AGENTS.md</c> and execpolicy <c>.rules</c> — so inheriting the host's current
    /// directory would let whatever repository the operator ran <c>arcanum serve</c> or
    /// <c>arcanum doctor</c> from steer the probe. The inference path already creates a private
    /// directory per turn for exactly this reason; readiness gets the same containment.
    /// </summary>
    [Theory]
    [InlineData(AiProviderKind.ClaudeCodeCli)]
    [InlineData(AiProviderKind.CodexCli)]
    public async Task Every_probe_spawn_runs_in_a_private_directory_never_the_hosts_own(
        AiProviderKind kind)
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(
                kind == AiProviderKind.ClaudeCodeCli
                    ? FamiliarFixtures.ClaudeAuthStatusConfigured
                    : FamiliarFixtures.CodexDoctorConfigured),
            string.Empty));

        _ = await Probe(runner, kind, stub.FileName);

        Assert.NotEmpty(runner.Requests);

        string hostDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());

        Assert.All(
            runner.Requests,
            request =>
            {

                Assert.False(string.IsNullOrWhiteSpace(request.WorkingDirectory));

                Assert.NotEqual(hostDirectory, Path.GetFullPath(request.WorkingDirectory!));

            });

    }

    /// <summary>
    /// One directory for the whole probe, not one per spawn: the version read and the status read
    /// are the same question asked twice, and a root that outlives both is what makes the probe
    /// cheap enough to run on the background health interval.
    /// </summary>
    [Fact]
    public async Task The_probes_spawns_share_one_private_directory()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        RecordingFamiliarProcessRunner runner = new();

        runner.SetBufferedOutput(new FamiliarProcessOutput(
            FamiliarProcessFailure.None,
            0,
            FamiliarFixtures.ReadText(FamiliarFixtures.ClaudeAuthStatusConfigured),
            string.Empty));

        _ = await Probe(runner, AiProviderKind.ClaudeCodeCli, stub.FileName);

        Assert.Equal(2, runner.Requests.Count);

        Assert.Single(runner.Requests.Select(static request => request.WorkingDirectory).Distinct());

    }

    private static Task<FamiliarProbeResult> Probe(
        IFamiliarProcessRunner runner,
        AiProviderKind kind,
        string command) =>
        new FamiliarProbe(runner).ProbeAsync(
            new ProviderSettings
            {
                Name = $"{kind}-subscription",
                Type = kind,
                Command = command,
            },
            CancellationToken.None);

}
