using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class BackupRestoreCommandTests
{

    [Fact]
    public void Restore_and_migrate_help_expose_their_options_and_never_offer_a_literal_passphrase()
    {

        ServiceCollection services = CreateServices(new FakeRestoreService());

        CliTestResult family = CliTestHarness.Run(services, "backup", "--help");

        Assert.Contains("restore", family.Output, StringComparison.Ordinal);

        Assert.Contains("migrate", family.Output, StringComparison.Ordinal);

        CliTestResult restore = CliTestHarness.Run(services, "backup", "restore", "--help");

        Assert.Equal((int)CliExitCode.Success, restore.ExitCode);

        foreach (string option in new[]
                 {
                     "--conflict-mode",
                     "--destination",
                     "--session-id",
                     "--map",
                     "--restore-master-api-key",
                     "--dry-run",
                     "--no-safety-backup",
                     "--yes",
                     "--passphrase-env",
                     "--passphrase-fd",
                 })
        {

            Assert.Contains(option, restore.Output, StringComparison.Ordinal);

        }

        Assert.DoesNotContain("--passphrase ", restore.Output, StringComparison.Ordinal);

        CliTestResult migrate = CliTestHarness.Run(services, "backup", "migrate", "--help");

        Assert.Equal((int)CliExitCode.Success, migrate.ExitCode);

        Assert.Contains("--output", migrate.Output, StringComparison.Ordinal);

        Assert.Contains("--overwrite", migrate.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void A_dry_run_reports_the_plan_and_never_calls_the_mutating_path()
    {

        FakeRestoreService restore = new();

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup",
            "--dry-run");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.NotNull(restore.LastPlanRequest);

        Assert.Null(restore.LastRestoreRequest);

        Assert.Contains("Restore plan", result.Output, StringComparison.Ordinal);

        Assert.Contains("Confirmation required", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void A_non_destructive_mode_forwards_typed_options_without_a_destructive_confirmation()
    {

        FakeRestoreService restore = new();

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup",
            "--conflict-mode",
            "new-profile-root",
            "--destination",
            "/tmp/second-profile",
            "--map",
            "workspace-root=/old/src=/new/src",
            "--no-safety-backup");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupRestoreRequest request = Assert.IsType<BackupRestoreRequest>(restore.LastRestoreRequest);

        Assert.Equal(BackupRestoreConflictMode.NewProfileRoot, request.ConflictMode);

        Assert.Equal("/tmp/second-profile", request.DestinationRoot);

        // Only replace-installation is destructive, so nothing was confirmed and nothing needed to be.
        Assert.False(request.Confirmed);

        Assert.False(request.CreateSafetyBackup);

        Assert.False(request.RestoreMasterApiKey);

        BackupPathMapping mapping = Assert.Single(request.PathMappings ?? []);

        Assert.Equal(BackupPathMappingKind.WorkspaceRoot, mapping.Kind);

        Assert.Equal("/old/src", mapping.From);

        Assert.Equal("/new/src", mapping.To);

        Assert.Equal("restore secret", restore.LastPassphrase);

    }

    [Fact]
    public void Declining_the_confirmation_prompt_cancels_before_any_restore_call()
    {

        FakeRestoreService restore = new();

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()),
            new StubConfirmationPrompt(false));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Null(restore.LastRestoreRequest);

        Assert.Contains("Restore cancelled.", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Accepting_the_confirmation_prompt_marks_the_request_confirmed()
    {

        FakeRestoreService restore = new();

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()),
            new StubConfirmationPrompt(true));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.True(Assert.IsType<BackupRestoreRequest>(restore.LastRestoreRequest).Confirmed);

    }

    [Fact]
    public void Selected_sessions_are_forwarded_for_an_import()
    {

        FakeRestoreService restore = new();

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup",
            "--conflict-mode",
            "import-selected-sessions",
            "--session-id",
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupRestoreRequest request = Assert.IsType<BackupRestoreRequest>(restore.LastRestoreRequest);

        Assert.Equal(
            [
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ],
            request.SessionIds ?? []);

    }

    [Theory]
    [InlineData("--conflict-mode", "not-a-mode")]
    [InlineData("--map", "not-a-kind=/a=/b")]
    [InlineData("--map", "workspace-root/only-two-parts")]
    public void Unsupported_typed_values_are_configuration_errors(string option, string value)
    {

        FakeRestoreService restore = new();

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup",
            option,
            value);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Null(restore.LastRestoreRequest);

    }

    [Fact]
    public void A_rejected_restore_is_not_reported_as_success()
    {

        FakeRestoreService restore = new()
        {

            Status = BackupRestoreStatus.Rejected,

            Issues =
            [
                new BackupVerifyIssue(
                    "backup.restore_format_newer",
                    "Upgrade Arcanum to restore this archive."),
            ],

        };

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains("REJECTED", result.Output, StringComparison.Ordinal);

        Assert.Contains("backup.restore_format_newer", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void A_rolled_back_restore_is_not_reported_as_success()
    {

        FakeRestoreService restore = new()
        {

            Status = BackupRestoreStatus.RolledBack,

            Issues =
            [
                new BackupVerifyIssue(
                    "backup.restore_commit_failed",
                    "The prior installation was returned to its original state."),
            ],

        };

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains("ROLLED BACK", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Migrate_requires_an_explicit_output_and_reports_the_written_archive()
    {

        FakeRestoreService restore = new();

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()));

        CliTestResult missing = CliTestHarness.Run(
            services,
            "backup",
            "migrate",
            "/tmp/sample.arcbackup");

        Assert.Equal((int)CliExitCode.ConfigurationError, missing.ExitCode);

        Assert.Null(restore.LastMigrateRequest);

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "migrate",
            "/tmp/sample.arcbackup",
            "--output",
            "/tmp/migrated.arcbackup",
            "--overwrite");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupMigrateRequest request = Assert.IsType<BackupMigrateRequest>(restore.LastMigrateRequest);

        Assert.Equal("/tmp/migrated.arcbackup", request.OutputPath);

        Assert.True(request.Overwrite);

        Assert.Contains("Archive migration: complete", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Specifying_two_passphrase_sources_is_a_configuration_error()
    {

        FakeRestoreService restore = new();

        ServiceCollection services = CreateServices(
            restore,
            new FakeBackupPassphraseReader("restore secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "restore",
            "/tmp/sample.arcbackup",
            "--passphrase-env",
            "ARCANUM_TEST_PASSPHRASE",
            "--passphrase-fd",
            "0");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Null(restore.LastRestoreRequest);

    }

    private static ServiceCollection CreateServices(
        IBackupRestoreService restore,
        IBackupPassphraseReader? passphrases = null,
        IConfirmationPrompt? confirmationPrompt = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IBackupRestoreService>();

        services.AddSingleton(restore);

        services.RemoveAll<IBackupPassphraseReader>();

        services.AddSingleton(passphrases ?? new FakeBackupPassphraseReader());

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton(confirmationPrompt ?? new StubConfirmationPrompt(true));

        services.RemoveAll<IGrimoireCliInitialization>();

        services.AddSingleton<IGrimoireCliInitialization>(new ThrowingGrimoireInitialization());

        return services;

    }

    private sealed class FakeBackupPassphraseReader(char[]? value = null) : IBackupPassphraseReader
    {

        public ValueTask<SensitiveBackupPassphrase?> ReadAsync(
            BackupPassphraseReadRequest request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<SensitiveBackupPassphrase?>(
                value is null
                    ? null
                    : new SensitiveBackupPassphrase([.. value]));

        }

    }

    private sealed class ThrowingGrimoireInitialization : IGrimoireCliInitialization
    {

        public Task EnsureInitializedAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The backup family must never bootstrap the Grimoire.");

    }

    private sealed class StubConfirmationPrompt(bool answer) : IConfirmationPrompt
    {

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(answer);

    }

    private sealed class FakeRestoreService : IBackupRestoreService
    {

        public BackupRestoreStatus Status { get; init; } = BackupRestoreStatus.Completed;

        public BackupVerifyIssue[] Issues { get; init; } = [];

        public BackupRestoreRequest? LastPlanRequest { get; private set; }

        public BackupRestoreRequest? LastRestoreRequest { get; private set; }

        public BackupMigrateRequest? LastMigrateRequest { get; private set; }

        public string? LastPassphrase { get; private set; }

        public Task<BackupRestorePlan> PlanAsync(
            BackupRestoreRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {

            LastPlanRequest = request;

            LastPassphrase = recoveryPassphrase.ToString();

            return Task.FromResult(Plan(request));

        }

        public Task<BackupRestoreResult> RestoreAsync(
            BackupRestoreRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {

            LastRestoreRequest = request;

            LastPassphrase = recoveryPassphrase.ToString();

            return Task.FromResult(
                new BackupRestoreResult(
                    Status,
                    request.ArchivePath,
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    request.ConflictMode,
                    request.DestinationRoot ?? "/tmp/arcanum",
                    SafetyBackupPath: null,
                    Plan(request),
                    Manifest: null,
                    new BackupRestoreReconciliation(1, 1, 0, 0, 0, 0, []),
                    [new BackupRestorePhaseRecord(BackupRestorePhase.Commit, "committed")],
                    Issues));

        }

        public Task<BackupMigrateResult> MigrateAsync(
            BackupMigrateRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {

            LastMigrateRequest = request;

            LastPassphrase = recoveryPassphrase.ToString();

            return Task.FromResult(
                new BackupMigrateResult(
                    Migrated: true,
                    request.ArchivePath,
                    request.OutputPath,
                    SourceFormatVersion: 1,
                    BackupArchiveFormat.CurrentVersion,
                    OutputBytes: 4096,
                    Manifest: null,
                    Issues: []));

        }

        private static BackupRestorePlan Plan(BackupRestoreRequest request) =>
            new(
                new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero),
                request.ArchivePath,
                BackupArchiveFormat.CurrentVersion,
                request.ConflictMode,
                request.DestinationRoot ?? "/tmp/arcanum",
                [BackupComponent.GrimoireDatabase],
                Entries: 3,
                RestoredBytes: 2048,
                RequiredBytes: 4096,
                AvailableBytes: 1_000_000,
                "sha256-source",
                "sha256-destination",
                SchemaMigrationRequired: true,
                request.SessionIds ?? [],
                [
                    .. (request.PathMappings ?? []).Select(
                        static mapping => new BackupRestoreMappingPlan(
                            mapping.Kind,
                            mapping.From,
                            mapping.To,
                            MatchedTargets: 1)),
                ],
                UnmappedNonportablePaths: [],
                RequiresConfirmation: true,
                SafetyBackupPlanned: request.CreateSafetyBackup,
                Warnings: [],
                Blockers: []);

    }

}
