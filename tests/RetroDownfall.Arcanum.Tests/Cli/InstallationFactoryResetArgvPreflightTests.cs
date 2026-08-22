using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class InstallationFactoryResetArgvPreflightTests
{

    [Theory]

    [InlineData("data factory-reset --workspace --dry-run")]

    [InlineData("data factory-reset --global --apply")]

    [InlineData("--yes data factory-reset --all --apply --force")]

    [InlineData("data factory-reset --all --help")]

    public void Valid_shapes_are_admitted(string commandLine)
    {

        InstallationFactoryResetPreflightResult result =
            InstallationFactoryResetArgvPreflight.Parse(Split(commandLine));

        Assert.True(result.IsFactoryReset);

        Assert.True(result.IsValid);

    }

    [Fact]

    public void External_remediation_attestation_is_admitted_only_for_full_apply()
    {

        InstallationFactoryResetPreflightResult result =
            InstallationFactoryResetArgvPreflight.Parse(
                Split("data factory-reset --all --apply --external-remediation-attestation remediation.json"));

        Assert.True(result.IsFactoryReset);

        Assert.True(result.IsValid);

        Assert.Equal("remediation.json", result.ExternalRemediationAttestationPath);

    }

    [Theory]

    [InlineData("data factory-reset --global --apply --external-remediation-attestation remediation.json")]

    [InlineData("data factory-reset --all --dry-run --external-remediation-attestation remediation.json")]

    [InlineData("data factory-reset --all --apply --external-remediation-attestation")]

    [InlineData("data factory-reset --all --apply --external-remediation-attestation one.json --external-remediation-attestation two.json")]

    public void Invalid_external_remediation_attestation_shapes_are_rejected(
        string commandLine)
    {

        InstallationFactoryResetPreflightResult result =
            InstallationFactoryResetArgvPreflight.Parse(Split(commandLine));

        Assert.True(result.IsFactoryReset);

        Assert.False(result.IsValid);

        Assert.False(string.IsNullOrWhiteSpace(result.Error));

    }

    [Theory]

    [InlineData("data factory-reset --dry-run")]

    [InlineData("data factory-reset --all")]

    [InlineData("data factory-reset --all --global --dry-run")]

    [InlineData("data factory-reset --all --all --dry-run")]

    [InlineData("data factory-reset --all --dry-run --apply")]

    [InlineData("data factory-reset --all --dry-run --dry-run")]

    [InlineData("data factory-reset --all --dry-run --force")]

    [InlineData("--yes data factory-reset --all --apply")]

    [InlineData("data factory-reset --all --apply --force")]

    public void Invalid_shapes_are_rejected(string commandLine)
    {

        InstallationFactoryResetPreflightResult result =
            InstallationFactoryResetArgvPreflight.Parse(Split(commandLine));

        Assert.True(result.IsFactoryReset);

        Assert.False(result.IsValid);

        Assert.False(string.IsNullOrWhiteSpace(result.Error));

    }

    [Theory]

    [InlineData("")]

    [InlineData("run hello")]

    [InlineData("data status")]

    public void Unrelated_commands_are_not_claimed(string commandLine)
    {

        InstallationFactoryResetPreflightResult result =
            InstallationFactoryResetArgvPreflight.Parse(Split(commandLine));

        Assert.False(result.IsFactoryReset);

    }

    [Fact]

    public async Task Program_rejects_invalid_shape_before_bootstrap_continuation()
    {

        int continuationCalls = 0;

        StringWriter error = new();

        TextWriter originalError = Console.Error;

        Console.SetError(error);

        try
        {

            int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
                ["data", "factory-reset", "--all", "--dry-run", "--dry-run"],
                () =>
                {

                    continuationCalls++;

                    return Task.FromResult(0);

                });

            Assert.Equal((int)CliExitCode.ConfigurationError, exitCode);

            Assert.Equal(0, continuationCalls);

            Assert.Contains("exactly one reset mode", error.ToString(), StringComparison.Ordinal);

        }
        finally
        {

            Console.SetError(originalError);

        }

    }

    [Fact]

    public async Task Program_continues_after_valid_shape()
    {

        int continuationCalls = 0;

        FakeStartupProbe probe = new(active: null);

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            ["data", "factory-reset", "--global", "--dry-run"],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(17);

            },
            probe);

        Assert.Equal(17, exitCode);

        Assert.Equal(1, continuationCalls);

        Assert.Equal(1, probe.ActiveReadCount);

    }

    [Fact]

    public async Task Program_defers_external_remediation_startup_lookup_until_after_decode()
    {

        int continuationCalls = 0;

        FakeStartupProbe probe = new(active: null);

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            [
                "data",
                "factory-reset",
                "--all",
                "--apply",
                "--external-remediation-attestation",
                "/must-not-be-disclosed/remediation.json",
            ],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(17);

            },
            probe);

        Assert.Equal(17, exitCode);

        Assert.Equal(1, continuationCalls);

        Assert.Equal(0, probe.ActiveReadCount);

    }

    public static TheoryData<string[]> NormalInvocationShapes =>
        new()
        {
            { Array.Empty<string>() },
            { ["--arcanum-deep-link", "eyJ0eXBlIjoic2Vzc2lvbiJ9"] },
            { ["center"] },
            { ["key", "status"] },
            { ["doctor"] },
            { ["config", "validate"] },
            { ["preset", "list"] },
            { ["backup", "list"] },
            { ["data", "encryption", "status"] },
            { ["session", "list"] },
            { ["run", "prompt"] },
            { ["run", "data", "factory-reset", "--global", "--apply"] },
        };

    [Theory]
    [MemberData(nameof(NormalInvocationShapes))]
    public async Task Program_blocks_every_normal_invocation_before_configuration_when_reset_is_active(
        string[] args)
    {

        FakeStartupProbe probe = new(
            new ActiveInstallationReset(
                InstallationResetScope.Global,
                WorkspaceRoot: null,
                PlanId: "active-plan"));

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            args,
            () =>
            {

                continuationCalls++;

                return Task.FromResult(0);

            },
            probe);

        Assert.Equal((int)CliExitCode.GenericError, exitCode);

        Assert.Equal(0, continuationCalls);

        Assert.Equal(1, probe.ActiveReadCount);

    }

    [Theory]

    [InlineData(InstallationResetScope.Global)]

    [InlineData(InstallationResetScope.All)]

    public async Task Program_delegates_serve_to_the_lock_first_host_for_prepared_handoffs(
        InstallationResetScope scope)
    {

        FakeStartupProbe probe = new(CreateHostRecoveryActive(scope));

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            ["serve"],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(31);

            },
            probe);

        Assert.Equal(31, exitCode);

        Assert.Equal(1, continuationCalls);

        Assert.Equal(0, probe.ActiveReadCount);

    }

    [Fact]
    public async Task Program_keeps_run_blocked_during_proof_free_host_recovery()
    {

        FakeStartupProbe probe = new(
            CreateHostRecoveryActive(InstallationResetScope.Global));

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            ["run", "prompt"],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(0);

            },
            probe);

        Assert.Equal((int)CliExitCode.GenericError, exitCode);

        Assert.Equal(0, continuationCalls);

    }

    [Fact]
    public async Task Program_delegates_every_serve_state_to_the_lock_first_host()
    {

        ActiveInstallationReset recoverable =
            CreateHostRecoveryActive(InstallationResetScope.Global);

        ActiveInstallationReset[] blocked =
        [
            recoverable with
            {
                DataHandoff = null,
            },
            recoverable with
            {
                OnlineDataCompletionDurable = true,
            },
            recoverable with
            {
                Phase = InstallationResetPhase.DataResetComplete,
            },
            recoverable with
            {
                Phase = InstallationResetPhase.OfflineCleanupComplete,
            },
            recoverable with
            {
                Phase = InstallationResetPhase.Verified,
            },
            recoverable with
            {
                Phase = InstallationResetPhase.Completed,
            },
            CreateHostRecoveryActive(InstallationResetScope.Workspace),
        ];

        foreach (ActiveInstallationReset active in blocked)
        {

            FakeStartupProbe probe = new(active);

            int continuationCalls = 0;

            int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
                ["serve"],
                () =>
                {

                    continuationCalls++;

                    return Task.FromResult(37);

                },
                probe);

            Assert.Equal(37, exitCode);

            Assert.Equal(1, continuationCalls);

            Assert.Equal(0, probe.ActiveReadCount);

        }

    }

    [Fact]
    public async Task Program_serve_reaches_locked_recovery_when_the_public_probe_would_reject_one_ahead()
    {

        FakeStartupProbe probe = new(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "Injected one-envelope-ahead public-probe refusal."));

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            ["serve"],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(41);

            },
            probe);

        Assert.Equal(41, exitCode);

        Assert.Equal(1, continuationCalls);

        Assert.Equal(0, probe.ActiveReadCount);

    }

    [Theory]
    [InlineData("run", "prompt")]
    [InlineData("data", "factory-reset", "--global", "--apply")]
    public async Task Program_keeps_the_exact_public_probe_for_run_and_reset_resume(
        params string[] args)
    {

        FakeStartupProbe probe = new(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "Injected exact public-probe refusal."));

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            args,
            () =>
            {

                continuationCalls++;

                return Task.FromResult(0);

            },
            probe);

        Assert.Equal((int)CliExitCode.GenericError, exitCode);

        Assert.Equal(0, continuationCalls);

        Assert.Equal(1, probe.ActiveReadCount);

    }

    [Fact]
    public async Task Program_run_continues_on_a_genuinely_fresh_absent_profile_parent_without_creating_it()
    {

        string retainedParent = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"missing-profile-{Guid.NewGuid():N}");

        string guardedRoot = Path.Combine(retainedParent, "arcanum");

        InstallationStartupProbe probe = new(
            guardedRoot,
            Path.Combine(guardedRoot, "arcanum.json"),
            Path.Combine(guardedRoot, "arcanum.db"),
            Path.Combine(guardedRoot, "security.dat"),
            new InMemoryOsCredentialStore());

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            ["run", "prompt"],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(43);

            },
            probe);

        Assert.Equal(43, exitCode);

        Assert.Equal(1, continuationCalls);

        Assert.False(Directory.Exists(retainedParent));

    }

    [Theory]

    [InlineData("run", "--help")]

    [InlineData("serve", "-h")]

    [InlineData("--version")]

    [InlineData("help", "sessions")]

    public async Task Program_keeps_help_and_version_available_during_an_active_reset(
        params string[] args)
    {

        FakeStartupProbe probe = new(
            new ActiveInstallationReset(
                InstallationResetScope.Global,
                WorkspaceRoot: null,
                PlanId: "active-plan"));

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            args,
            () => Task.FromResult(23),
            probe);

        Assert.Equal(23, exitCode);

        Assert.Equal(0, probe.ActiveReadCount);

    }

    [Fact]

    public async Task Program_blocks_a_private_deep_link_with_trailing_help_during_an_active_reset()
    {

        ApplicationDeepLink deepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.CommandCenter,
            ApplicationResourceKind.None,
            InitialView: ApplicationInitialView.CommandCenter);

        string payload = ApplicationDeepLinkCodec.Encode(deepLink);

        FakeStartupProbe probe = new(
            new ActiveInstallationReset(
                InstallationResetScope.Global,
                WorkspaceRoot: null,
                PlanId: "active-plan"));

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            [ApplicationDeepLinkCodec.ArgumentName, payload, "--help"],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(0);

            },
            probe);

        Assert.Equal((int)CliExitCode.GenericError, exitCode);

        Assert.Equal(0, continuationCalls);

        Assert.Equal(1, probe.ActiveReadCount);

    }

    [Theory]
    [InlineData("prompt", "create", "sample", "--version", "1")]
    [InlineData("spell", "version", "activate", "sample", "--version", "2")]
    public async Task Program_does_not_treat_a_subcommand_version_option_as_root_version(
        params string[] args)
    {

        FakeStartupProbe probe = new(
            new ActiveInstallationReset(
                InstallationResetScope.Global,
                WorkspaceRoot: null,
                PlanId: "active-plan"));

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            args,
            () =>
            {

                continuationCalls++;

                return Task.FromResult(0);

            },
            probe);

        Assert.Equal((int)CliExitCode.GenericError, exitCode);

        Assert.Equal(0, continuationCalls);

        Assert.Equal(1, probe.ActiveReadCount);

    }

    [Theory]

    [InlineData("--global", true)]

    [InlineData("--all", false)]

    public async Task Program_admits_only_the_matching_factory_reset_apply(
        string scopeOption,
        bool expectedAdmission)
    {

        FakeStartupProbe probe = new(
            new ActiveInstallationReset(
                InstallationResetScope.Global,
                WorkspaceRoot: null,
                PlanId: "active-plan"));

        int continuationCalls = 0;

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            ["data", "factory-reset", scopeOption, "--apply"],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(29);

            },
            probe);

        Assert.Equal(
            expectedAdmission ? 29 : (int)CliExitCode.GenericError,
            exitCode);

        Assert.Equal(expectedAdmission ? 1 : 0, continuationCalls);

        Assert.Equal(1, probe.ActiveReadCount);

    }

    private static string[] Split(string commandLine) =>
        commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static ActiveInstallationReset CreateHostRecoveryActive(
        InstallationResetScope scope) =>
        new(
            scope,
            WorkspaceRoot: scope is InstallationResetScope.Workspace or InstallationResetScope.All
                ? "/workspace"
                : null,
            PlanId: "active-plan",
            OperationId: Guid.Parse("51515151-5151-4151-8151-515151515151"),
            Phase: InstallationResetPhase.Prepared,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure,
            OnlineDataCompletionDurable: false);

    private sealed class FakeStartupProbe : IInstallationStartupProbe
    {

        private readonly Result<ActiveInstallationReset?> _active;

        public FakeStartupProbe(ActiveInstallationReset? active)
        {

            _active = Result<ActiveInstallationReset?>.Success(active);

        }

        public FakeStartupProbe(Error error)
        {

            _active = Result<ActiveInstallationReset?>.Failure(error);

        }

        public int ActiveReadCount { get; private set; }

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken)
        {

            ActiveReadCount++;

            return Task.FromResult(_active);

        }

        public Result<bool> IsFreshInstallation() =>
            Result<bool>.Success(false);

    }

}
