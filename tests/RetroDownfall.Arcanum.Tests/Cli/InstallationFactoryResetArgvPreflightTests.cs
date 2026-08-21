using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

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

        int exitCode = await RetroDownfall.Arcanum.Cli.Program.RunBeforeConfigurationAsync(
            ["data", "factory-reset", "--global", "--dry-run"],
            () =>
            {

                continuationCalls++;

                return Task.FromResult(17);

            });

        Assert.Equal(17, exitCode);

        Assert.Equal(1, continuationCalls);

    }

    [Theory]

    [InlineData("run", "prompt")]

    [InlineData("run", "data", "factory-reset", "--global", "--apply")]

    [InlineData("serve")]

    public async Task Program_blocks_run_and_serve_before_configuration_when_reset_is_active(
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

    [InlineData(InstallationResetScope.Global)]

    [InlineData(InstallationResetScope.All)]

    public async Task Program_admits_serve_for_proof_free_prepared_host_handoff(
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

        Assert.Equal(1, probe.ActiveReadCount);

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
    public async Task Program_blocks_serve_for_legacy_proof_complete_later_and_workspace_states()
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

                    return Task.FromResult(0);

                },
                probe);

            Assert.Equal((int)CliExitCode.GenericError, exitCode);

            Assert.Equal(0, continuationCalls);

        }

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

    private sealed class FakeStartupProbe(
        ActiveInstallationReset? active) : IInstallationStartupProbe
    {

        public int ActiveReadCount { get; private set; }

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken)
        {

            ActiveReadCount++;

            return Task.FromResult(
                Result<ActiveInstallationReset?>.Success(active));

        }

        public Result<bool> IsFreshInstallation() =>
            Result<bool>.Success(false);

    }

}
