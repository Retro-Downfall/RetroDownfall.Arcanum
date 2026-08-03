using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class BackupCommandTests
{

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        2,
        12,
        30,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Backup_help_exposes_the_supported_family_and_never_offers_a_literal_passphrase()
    {

        FakeBackupService backup = new();

        FakeBackupPassphraseReader passphrases = new();

        ServiceCollection services = CreateServices(backup, passphrases);

        CliTestResult family = CliTestHarness.Run(services, "backup", "--help");

        Assert.Equal((int)CliExitCode.Success, family.ExitCode);

        Assert.Contains("create", family.Output, StringComparison.Ordinal);

        Assert.Contains("inspect", family.Output, StringComparison.Ordinal);

        Assert.Contains("verify", family.Output, StringComparison.Ordinal);

        Assert.Contains("list", family.Output, StringComparison.Ordinal);

        CliTestResult create = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--help");

        Assert.Equal((int)CliExitCode.Success, create.ExitCode);

        Assert.Contains("--scope", create.Output, StringComparison.Ordinal);

        Assert.Contains("--session-id", create.Output, StringComparison.Ordinal);

        Assert.Contains("--include", create.Output, StringComparison.Ordinal);

        Assert.Contains("--exclude", create.Output, StringComparison.Ordinal);

        Assert.Contains("--dry-run", create.Output, StringComparison.Ordinal);

        Assert.Contains("--overwrite", create.Output, StringComparison.Ordinal);

        Assert.Contains("--passphrase-env", create.Output, StringComparison.Ordinal);

        Assert.Contains("--passphrase-fd", create.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(
            create.Output.Split(global::System.Environment.NewLine),
            line => string.Equals(
                line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                "--passphrase",
                StringComparison.Ordinal));

    }

    [Fact]
    public void Create_dry_run_binds_typed_scope_session_and_components_without_reading_a_passphrase()
    {

        FakeBackupService backup = new();

        FakeBackupPassphraseReader passphrases = new();

        ServiceCollection services = CreateServices(backup, passphrases);

        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--dry-run",
            "--scope",
            "specific-session",
            "--session-id",
            sessionId.ToString("D"),
            "--include",
            "audit-logs",
            "guardrail-logs",
            "--exclude",
            "master-api-key");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupPlanRequest request = Assert.IsType<BackupPlanRequest>(backup.LastPlanRequest);

        Assert.Equal(BackupScope.SpecificSession, request.Scope);

        Assert.Equal(sessionId, request.SessionId);

        Assert.Equal(
            [BackupComponent.AuditLogs, BackupComponent.GuardrailLogs],
            request.Include);

        Assert.Equal([BackupComponent.MasterApiKey], request.Exclude);

        Assert.Equal(1, backup.PlanCalls);

        Assert.Equal(0, backup.CreateCalls);

        Assert.Empty(passphrases.Requests);

        Assert.Contains("Backup plan", result.Output, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("ARCANUM_BACKUP_SECRET", 0)]
    [InlineData(null, -1)]
    public void Create_dry_run_ignores_passphrase_source_options_that_it_never_consumes(
        string? environmentVariable,
        int fileDescriptor)
    {

        FakeBackupService backup = new();

        FakeBackupPassphraseReader passphrases = new();

        ServiceCollection services = CreateServices(backup, passphrases);

        List<string> arguments =
        [
            "backup",
            "create",
            "--dry-run",
            "--passphrase-fd",
            fileDescriptor.ToString(
                global::System.Globalization.CultureInfo.InvariantCulture),
        ];

        if (environmentVariable is not null)
        {

            arguments.Add("--passphrase-env");

            arguments.Add(environmentVariable);

        }

        CliTestResult result = CliTestHarness.Run(
            services,
            [.. arguments]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, backup.PlanCalls);

        Assert.Equal(0, backup.CreateCalls);

        Assert.Empty(passphrases.Requests);

    }

    [Theory]
    [InlineData("--scope", "0")]
    [InlineData("--scope", "everything")]
    [InlineData("--include", "/tmp/arbitrary-file")]
    [InlineData("--exclude", "unknown-component")]
    public void Create_rejects_values_outside_the_typed_catalog(
        string option,
        string value)
    {

        FakeBackupService backup = new();

        ServiceCollection services = CreateServices(
            backup,
            new FakeBackupPassphraseReader());

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--dry-run",
            option,
            value);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(0, backup.PlanCalls);

        Assert.Contains("supported", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Create_requires_a_specific_session_id_but_allows_session_provenance_on_broader_scopes()
    {

        FakeBackupService backup = new();

        ServiceCollection services = CreateServices(
            backup,
            new FakeBackupPassphraseReader());

        CliTestResult missing = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--dry-run",
            "--scope",
            "specific-session");

        Assert.Equal((int)CliExitCode.ConfigurationError, missing.ExitCode);

        Assert.Contains("--session-id", missing.Error, StringComparison.Ordinal);

        Guid provenanceSessionId = Guid.Parse(
            "11111111-2222-3333-4444-555555555555");

        CliTestResult broaderScope = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--dry-run",
            "--session-id",
            provenanceSessionId.ToString("D"));

        Assert.Equal((int)CliExitCode.Success, broaderScope.ExitCode);

        Assert.Equal(provenanceSessionId, backup.LastPlanRequest?.SessionId);

        Assert.Equal(1, backup.PlanCalls);

    }

    [Fact]
    public void Create_allows_include_exclude_overlap_and_preserves_exclude_wins_semantics()
    {

        FakeBackupService backup = new();

        ServiceCollection services = CreateServices(
            backup,
            new FakeBackupPassphraseReader());

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--dry-run",
            "--include",
            "configuration",
            "--exclude",
            "configuration");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupPlanRequest request = Assert.IsType<BackupPlanRequest>(backup.LastPlanRequest);

        Assert.Equal([BackupComponent.Configuration], request.Include);

        Assert.Equal([BackupComponent.Configuration], request.Exclude);

    }

    [Fact]
    public void Create_rejects_ambiguous_passphrase_sources_before_planning_or_prompting()
    {

        FakeBackupService backup = new();

        FakeBackupPassphraseReader passphrases = new();

        ServiceCollection services = CreateServices(backup, passphrases);

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--passphrase-env",
            "ARCANUM_BACKUP_SECRET",
            "--passphrase-fd",
            "0");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("one passphrase source", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, backup.PlanCalls);

        Assert.Equal(0, backup.CreateCalls);

        Assert.Empty(passphrases.Requests);

    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ARCANUM_BACKUP_SECRET", null)]
    [InlineData(null, 0)]
    public void Create_uses_prompt_environment_reference_or_descriptor_without_putting_the_secret_in_output(
        string? environmentVariable,
        int? fileDescriptor)
    {

        char[] secret = "command secret".ToCharArray();

        FakeBackupService backup = new();

        FakeBackupPassphraseReader passphrases = new(secret);

        ServiceCollection services = CreateServices(backup, passphrases);

        List<string> arguments = ["backup", "create"];

        if (environmentVariable is not null)
        {

            arguments.Add("--passphrase-env");

            arguments.Add(environmentVariable);

        }

        if (fileDescriptor.HasValue)
        {

            arguments.Add("--passphrase-fd");

            arguments.Add(fileDescriptor.Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture));

        }

        CliTestResult result = CliTestHarness.Run(services, [.. arguments]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupPassphraseReadRequest request = Assert.Single(passphrases.Requests);

        Assert.Equal(environmentVariable, request.EnvironmentVariableName);

        Assert.Equal(fileDescriptor, request.FileDescriptor);

        Assert.Equal(BackupPassphraseReadPurpose.CreateArchive, request.Purpose);

        Assert.Equal("command secret", backup.LastCreatePassphrase);

        Assert.All(secret, character => Assert.Equal('\0', character));

        Assert.DoesNotContain("command secret", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("command secret", result.Error, StringComparison.Ordinal);

        Assert.Contains("Backup complete", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Create_json_dry_run_writes_one_structured_plan()
    {

        FakeBackupService backup = new();

        ServiceCollection services = CreateServices(
            backup,
            new FakeBackupPassphraseReader());

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--dry-run",
            "--json");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(27L, document.RootElement.GetProperty("estimatedBytes").GetInt64());

        Assert.False(document.RootElement.TryGetProperty("output", out _));

    }

    [Fact]
    public void Create_passes_output_and_explicit_overwrite_without_an_extra_confirmation_gate()
    {

        FakeBackupService backup = new();

        ServiceCollection services = CreateServices(
            backup,
            new FakeBackupPassphraseReader("overwrite secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "create",
            "--output",
            "/tmp/replaced.arcbackup",
            "--overwrite");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupCreateRequest request = Assert.IsType<BackupCreateRequest>(
            backup.LastCreateRequest);

        Assert.Equal("/tmp/replaced.arcbackup", request.OutputPath);

        Assert.True(request.Overwrite);

    }

    [Fact]
    public void ConfigureCliServices_resolves_the_real_backup_and_passphrase_services()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        GrimoireDbPassphraseSource databasePassphrase = Assert.IsType<GrimoireDbPassphraseSource>(
            provider.GetRequiredService<IGrimoireDbPassphraseSource>());

        Assert.Throws<InvalidOperationException>(() => _ = databasePassphrase.Passphrase);

        Assert.IsType<BackupService>(provider.GetRequiredService<IBackupService>());

        Assert.Throws<InvalidOperationException>(() => _ = databasePassphrase.Passphrase);

        Assert.IsType<BackupPassphraseReader>(
            provider.GetRequiredService<IBackupPassphraseReader>());

    }

    [Theory]
    [InlineData("help")]
    [InlineData("dry-run")]
    [InlineData("create")]
    [InlineData("inspect")]
    [InlineData("list")]
    public void Backup_commands_never_initialize_or_mutate_the_live_grimoire(string operation)
    {

        ThrowingGrimoireInitialization initialization = new();

        ServiceCollection services = CreateServices(
            new FakeBackupService(),
            new FakeBackupPassphraseReader("backup secret".ToCharArray()),
            initialization);

        string[] arguments = operation switch
        {
            "help" => ["backup", "--help"],
            "dry-run" => ["backup", "create", "--dry-run"],
            "create" => ["backup", "create"],
            "inspect" => ["backup", "inspect", "/tmp/sample.arcbackup"],
            "list" => ["backup", "list"],
            _ => throw new InvalidOperationException("Unsupported test operation."),
        };

        CliTestResult result = CliTestHarness.Run(services, arguments);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(0, initialization.Calls);

    }

    [Fact]
    public void Inspect_without_decryption_reads_only_outer_metadata_and_does_not_prompt()
    {

        FakeBackupService backup = new();

        FakeBackupPassphraseReader passphrases = new();

        ServiceCollection services = CreateServices(backup, passphrases);

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "inspect",
            "/tmp/sample.arcbackup");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupPassphraseReadRequest request = Assert.Single(passphrases.Requests);

        Assert.Equal(BackupPassphraseReadPurpose.InspectOuterMetadata, request.Purpose);

        Assert.Null(backup.LastInspectPassphrase);

        Assert.Contains("Format version: 1", result.Output, StringComparison.Ordinal);

        Assert.Contains("Manifest: encrypted", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Inspect_decrypt_prompts_only_when_requested_and_can_write_json()
    {

        FakeBackupService backup = new();

        backup.InspectResult = backup.InspectResult with
        {

            Manifest = Manifest(),

        };

        FakeBackupPassphraseReader passphrases = new("inspect secret".ToCharArray());

        ServiceCollection services = CreateServices(backup, passphrases);

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "inspect",
            "/tmp/sample.arcbackup",
            "--decrypt",
            "--json");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        BackupPassphraseReadRequest request = Assert.Single(passphrases.Requests);

        Assert.Equal(BackupPassphraseReadPurpose.OpenArchive, request.Purpose);

        Assert.Equal("inspect secret", backup.LastInspectPassphrase);

        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());

        Assert.True(document.RootElement.TryGetProperty("manifest", out _));

        Assert.DoesNotContain("inspect secret", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Verify_reads_a_secure_passphrase_and_returns_failure_for_invalid_archives()
    {

        FakeBackupService backup = new();

        backup.VerifyResult = backup.VerifyResult with
        {

            IsValid = false,

            Issues =
            [
                new BackupVerifyIssue(
                    "backup.authentication_failed",
                    "Archive authentication failed."),
            ],

        };

        FakeBackupPassphraseReader passphrases = new("verify secret".ToCharArray());

        ServiceCollection services = CreateServices(backup, passphrases);

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "verify",
            "/tmp/sample.arcbackup",
            "--passphrase-fd",
            "0");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        BackupPassphraseReadRequest request = Assert.Single(passphrases.Requests);

        Assert.Equal(0, request.FileDescriptor);

        Assert.Equal(BackupPassphraseReadPurpose.OpenArchive, request.Purpose);

        Assert.Equal("verify secret", backup.LastVerifyPassphrase);

        Assert.Contains("INVALID", result.Output, StringComparison.Ordinal);

        Assert.Contains("backup.authentication_failed", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void List_passes_the_optional_directory_and_writes_archive_metadata()
    {

        FakeBackupService backup = new();

        ServiceCollection services = CreateServices(
            backup,
            new FakeBackupPassphraseReader());

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "list",
            "--directory",
            "/tmp/backups");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal("/tmp/backups", backup.LastListDirectory);

        Assert.Contains("/tmp/sample.arcbackup", result.Output, StringComparison.Ordinal);

        Assert.Contains("format 1", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Create_incomplete_result_is_not_reported_as_success()
    {

        FakeBackupService backup = new();

        backup.CreateResult = backup.CreateResult with
        {

            Status = BackupCreateStatus.Incomplete,

            ArchivePath = null,

            Issues =
            [
                new BackupVerifyIssue(
                    "backup.missing_file",
                    "A selected file was unavailable."),
            ],

        };

        ServiceCollection services = CreateServices(
            backup,
            new FakeBackupPassphraseReader("create secret".ToCharArray()));

        CliTestResult result = CliTestHarness.Run(
            services,
            "backup",
            "create");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.DoesNotContain("Backup complete", result.Output, StringComparison.Ordinal);

        Assert.Contains("Backup incomplete", result.Output, StringComparison.Ordinal);

        Assert.Contains("backup.missing_file", result.Output, StringComparison.Ordinal);

    }

    private static ServiceCollection CreateServices(
        IBackupService backup,
        IBackupPassphraseReader passphrases,
        IGrimoireCliInitialization? initialization = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IBackupService>();

        services.AddSingleton(backup);

        services.RemoveAll<IBackupPassphraseReader>();

        services.AddSingleton(passphrases);

        services.RemoveAll<IGrimoireCliInitialization>();

        services.AddSingleton<IGrimoireCliInitialization>(
            initialization ?? new ThrowingGrimoireInitialization());

        return services;

    }

    private static BackupPlan Plan(BackupPlanRequest? request = null)
    {

        BackupPlanRequest effectiveRequest = request ?? new BackupPlanRequest(
            BackupScope.Full,
            null,
            [],
            []);

        return new BackupPlan(
            CreatedAt,
            effectiveRequest.Scope,
            effectiveRequest.SessionId,
            [
                new BackupPlanComponent(
                    BackupComponent.Configuration,
                    BackupComponentStatus.Complete,
                    "Selected.",
                    1,
                    27,
                    []),
            ],
            1,
            27,
            [],
            []);

    }

    private static BackupArchiveHeader Header() =>
        new(
            BackupArchiveFormat.CurrentVersion,
            "PBKDF2-HMAC-SHA256",
            600_000,
            "AES-256-GCM",
            65_536,
            CreatedAt,
            256);

    private static BackupManifest Manifest() =>
        new(
            BackupArchiveFormat.CurrentVersion,
            "0.1.0-beta",
            "test",
            "1",
            CreatedAt,
            "test-platform",
            new BackupEnvelopeDescriptor(
                "PBKDF2-HMAC-SHA256",
                "SHA-256",
                600_000,
                "c2FsdA==",
                "AES-256-GCM",
                256,
                12,
                16,
                65_536),
            BackupScope.Full,
            null,
            [],
            [],
            [],
            [],
            []);

    private sealed class FakeBackupPassphraseReader : IBackupPassphraseReader
    {

        private readonly Queue<char[]?> _values = new();

        public FakeBackupPassphraseReader(char[]? value = null)
        {

            if (value is not null)
            {

                _values.Enqueue(value);

            }

        }

        public List<BackupPassphraseReadRequest> Requests { get; } = [];

        public ValueTask<SensitiveBackupPassphrase?> ReadAsync(
            BackupPassphraseReadRequest request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(request);

            char[]? value = _values.Count > 0
                ? _values.Dequeue()
                : null;

            return ValueTask.FromResult(
                value is null
                    ? null
                    : new SensitiveBackupPassphrase(value));

        }

    }

    private sealed class ThrowingGrimoireInitialization : IGrimoireCliInitialization
    {

        public int Calls { get; private set; }

        public Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            throw new InvalidOperationException(
                "Backup commands must not initialize the live Grimoire.");

        }

    }

    private sealed class FakeBackupService : IBackupService
    {

        public BackupPlan? LastPlanRequestResult { get; private set; }

        public BackupPlanRequest? LastPlanRequest { get; private set; }

        public BackupCreateRequest? LastCreateRequest { get; private set; }

        public string? LastCreatePassphrase { get; private set; }

        public string? LastInspectPassphrase { get; private set; }

        public string? LastVerifyPassphrase { get; private set; }

        public string? LastListDirectory { get; private set; }

        public int PlanCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public BackupCreateResult CreateResult { get; set; } = new(
            BackupCreateStatus.Complete,
            "/tmp/sample.arcbackup",
            256,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            null,
            Plan(),
            []);

        public BackupInspectResult InspectResult { get; set; } = new(
            "/tmp/sample.arcbackup",
            256,
            Header(),
            BackupArchiveFormat.CurrentVersion,
            null);

        public BackupVerifyResult VerifyResult { get; set; } = new(
            "/tmp/sample.arcbackup",
            true,
            BackupArchiveFormat.CurrentVersion,
            1,
            27,
            true,
            [],
            null);

        public IReadOnlyList<BackupListItem> ListResult { get; set; } =
        [
            new BackupListItem(
                "/tmp/sample.arcbackup",
                256,
                Header()),
        ];

        public Task<BackupPlan> PlanAsync(
            BackupPlanRequest request,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            PlanCalls++;

            LastPlanRequest = request;

            LastPlanRequestResult = Plan(request);

            return Task.FromResult(LastPlanRequestResult);

        }

        public Task<BackupCreateResult> CreateAsync(
            BackupCreateRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            CreateCalls++;

            LastCreateRequest = request;

            LastCreatePassphrase = new string(recoveryPassphrase.Span);

            BackupCreateResult result = CreateResult with
            {

                Plan = Plan(request.Plan),

            };

            return Task.FromResult(result);

        }

        public Task<BackupInspectResult> InspectAsync(
            string archivePath,
            ReadOnlyMemory<char>? recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            LastInspectPassphrase = recoveryPassphrase.HasValue
                ? new string(recoveryPassphrase.Value.Span)
                : null;

            return Task.FromResult(
                InspectResult with
                {

                    ArchivePath = archivePath,

                });

        }

        public Task<BackupVerifyResult> VerifyAsync(
            string archivePath,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            LastVerifyPassphrase = new string(recoveryPassphrase.Span);

            return Task.FromResult(
                VerifyResult with
                {

                    ArchivePath = archivePath,

                });

        }

        public Task<IReadOnlyList<BackupListItem>> ListAsync(
            string? directory,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            LastListDirectory = directory;

            return Task.FromResult(ListResult);

        }

    }

}
