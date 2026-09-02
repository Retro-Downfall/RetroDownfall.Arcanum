using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Diagnostics;

/// <summary>
/// The permission detector and its repair are the pair issue #33 leans on hardest: the repair is the
/// one that already existed (<c>--fix-permissions</c>) and the one most likely to be re-run, so
/// convergence — a second apply reporting <see cref="DoctorRepairState.AlreadyConverged"/> without
/// touching a byte — is the invariant under test, alongside the rule that a missing path is skipped
/// rather than reported as a fault.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class PermissionPostureTests : IAsyncLifetime
{

    private TempWorkspace _temp = null!;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    private string? _originalTestHome;

    public async Task InitializeAsync()
    {

        _temp = new TempWorkspace();

        await _temp.InitializeAsync();

        _originalDotnetEnvironment =
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment =
            global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        _originalTestHome =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _temp.Root);

        // Create the managed directories the way Arcanum does, so the posture detector sees a
        // correctly-installed tree and the only finding in each test is the one it plants.
        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.GrimoireDirectory);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.SecretStoreDirectory);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.LogDirectory);

    }

    public async Task DisposeAsync()
    {

        global::System.Environment.SetEnvironmentVariable(
            "DOTNET_ENVIRONMENT",
            _originalDotnetEnvironment);

        global::System.Environment.SetEnvironmentVariable(
            "ASPNETCORE_ENVIRONMENT",
            _originalAspNetCoreEnvironment);

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _originalTestHome);

        await _temp.DisposeAsync();

    }

    [Fact]
    public void The_inventory_names_every_path_the_repair_would_harden()
    {

        IReadOnlyList<string> inventory = SecureFilePermissions.EnumerateOwnerOnlyPaths();

        Assert.Contains(ArcanumPaths.ConfigurationFile, inventory);

        Assert.Contains(ArcanumPaths.GrimoireDatabaseFile, inventory);

        Assert.Contains(ArcanumPaths.ApiKeyStoreFile, inventory);

        Assert.Contains(ArcanumPaths.GrimoireKeyStoreFile, inventory);

        Assert.Contains(ArcanumPaths.FileEncryptionKeyStoreFile, inventory);

        Assert.Contains(ArcanumPaths.PerplexityApiKeyStoreFile, inventory);

    }

    [Fact]
    public void The_inventory_covers_the_per_provider_credential_mirrors()
    {

        IReadOnlyList<string> inventory = SecureFilePermissions.EnumerateOwnerOnlyPaths(["openai"]);

        Assert.Contains(ArcanumPaths.InferenceProviderApiKeyStoreFile("openai"), inventory);

    }

    [Fact]
    public void Inspecting_never_creates_a_path_it_reports_on()
    {

        string secret = ArcanumPaths.ApiKeyStoreFile;

        Assert.False(File.Exists(secret));

        SensitivePathPosture posture = Assert.Single(
            SecureFilePermissions.InspectOwnerOnlyPosture(),
            candidate => candidate.Path == secret);

        Assert.False(posture.Exists);

        Assert.False(File.Exists(secret));

    }

    [SkippableFact]
    public async Task A_group_readable_secret_is_degraded_and_names_the_repair()
    {

        Skip.If(OperatingSystem.IsWindows(), "Unix group-readable mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        WriteGroupReadable(ArcanumPaths.ApiKeyStoreFile);

        PermissionPostureCheck check = new(Options.Create(new ArcanumSettings()));

        DoctorFinding finding = await check.InspectAsync(CancellationToken.None);

        Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

        DoctorRemedy remedy = Assert.Single(finding.Remedies ?? []);

        Assert.Equal(PermissionApplyOwnerOnlyRepair.RepairId, remedy.RepairId);

        Assert.Contains("--repair permissions.apply_owner_only", remedy.Command);

    }

    [Fact]
    public async Task A_secret_that_does_not_exist_is_healthy_rather_than_a_fault()
    {

        PermissionPostureCheck check = new(Options.Create(new ArcanumSettings()));

        DoctorFinding finding = await check.InspectAsync(CancellationToken.None);

        Assert.Equal(DoctorOutcome.Healthy, finding.Outcome);

    }

    [SkippableFact]
    public async Task Planning_lists_only_the_paths_whose_mode_differs_and_changes_nothing()
    {

        Skip.If(OperatingSystem.IsWindows(), "Unix group-readable mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        WriteGroupReadable(ArcanumPaths.ApiKeyStoreFile);

        WriteOwnerOnly(ArcanumPaths.GrimoireKeyStoreFile);

        PermissionApplyOwnerOnlyRepair repair = new(Options.Create(new ArcanumSettings()));

        DoctorRepairResult plan = await repair.PlanAsync(CancellationToken.None);

        Assert.Equal(DoctorRepairState.Planned, plan.State);

        DoctorRepairStep step = Assert.Single(plan.Steps);

        Assert.Equal(ArcanumPaths.ApiKeyStoreFile, step.Target);

        Assert.NotEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(ArcanumPaths.ApiKeyStoreFile));

    }

    [SkippableFact]
    public async Task Applying_hardens_the_path_and_a_second_apply_converges_without_changing_anything()
    {

        Skip.If(OperatingSystem.IsWindows(), "Unix group-readable mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        WriteGroupReadable(ArcanumPaths.ApiKeyStoreFile);

        PermissionApplyOwnerOnlyRepair repair = new(Options.Create(new ArcanumSettings()));

        DoctorRepairResult first = await repair.ApplyAsync(CancellationToken.None);

        Assert.Equal(DoctorRepairState.Applied, first.State);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(ArcanumPaths.ApiKeyStoreFile));

        DoctorRepairResult second = await repair.ApplyAsync(CancellationToken.None);

        Assert.Equal(DoctorRepairState.AlreadyConverged, second.State);

        Assert.Empty(second.Steps);

    }

    [Fact]
    public async Task Applying_with_nothing_to_do_converges_rather_than_reporting_a_change()
    {

        PermissionApplyOwnerOnlyRepair repair = new(Options.Create(new ArcanumSettings()));

        DoctorRepairResult result = await repair.ApplyAsync(CancellationToken.None);

        Assert.Equal(DoctorRepairState.AlreadyConverged, result.State);

    }

    [SkippableFact]
    public async Task No_step_ever_records_the_content_of_the_file_it_hardens()
    {

        Skip.If(OperatingSystem.IsWindows(), "Unix owner-only mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        const string secretValue = "sk-live-do-not-print";

        File.WriteAllText(ArcanumPaths.ApiKeyStoreFile, secretValue);

        File.SetUnixFileMode(
            ArcanumPaths.ApiKeyStoreFile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        PermissionApplyOwnerOnlyRepair repair = new(Options.Create(new ArcanumSettings()));

        DoctorRepairResult plan = await repair.PlanAsync(CancellationToken.None);

        foreach (DoctorRepairStep step in plan.Steps)
        {

            Assert.DoesNotContain(secretValue, step.Before);

            Assert.DoesNotContain(secretValue, step.After);

            Assert.DoesNotContain(secretValue, step.Target);

        }

        Assert.DoesNotContain(secretValue, plan.Summary);

    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void WriteGroupReadable(string path)
    {

        File.WriteAllText(path, "value");

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void WriteOwnerOnly(string path)
    {

        File.WriteAllText(path, "value");

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    }

}
