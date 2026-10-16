using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// <c>arcanum data repair-installation-identity</c>, run through the real command tree and the real
/// command line container over a redirected profile.
/// </summary>
/// <remarks>
/// The service's own cases pin every precondition. These pin what the operator and a script see: the
/// exit code, the two output streams, the JSON document, and that the confirmation is asked before
/// anything is removed.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class InstallationIdentityRepairCommandTests : IDisposable
{
    private const string DamagedValue = "c0ffee00-1234-4abc-8def-0123456789ab";

    private readonly ArcanumTestHomeScope _home = new("arcanum-repair-cli");

    private readonly InMemoryOsCredentialStore _credentials = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public async Task A_dry_run_reports_the_item_and_changes_nothing()
    {
        SetIdentity(DamagedValue);

        CliTestResult result = await RunAsync("data", "repair-installation-identity", "--dry-run");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("Dry run.", result.Output, StringComparison.Ordinal);

        Assert.Contains($"service {ArcanumCredentialIdentity.Service}, account {IdentityAccount()}", result.Output, StringComparison.Ordinal);

        Assert.Contains("Nothing was changed.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(DamagedValue, result.Output + result.Error, StringComparison.OrdinalIgnoreCase);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_dry_run_in_json_is_one_document_and_changes_nothing()
    {
        SetIdentity(DamagedValue);

        CliTestResult result = await RunAsync("--json", "data", "repair-installation-identity", "--dry-run");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement root = document.RootElement;

        Assert.Equal("damaged", root.GetProperty("outcome").GetString());

        Assert.True(root.GetProperty("dryRun").GetBoolean());

        Assert.False(root.GetProperty("removed").GetBoolean());

        Assert.Equal(ArcanumCredentialIdentity.Service, root.GetProperty("credentialService").GetString());

        Assert.Equal(IdentityAccount(), root.GetProperty("credentialAccount").GetString());

        Assert.DoesNotContain(DamagedValue, result.Output + result.Error, StringComparison.OrdinalIgnoreCase);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task An_acknowledged_repair_removes_only_the_item_and_says_so()
    {
        SetIdentity(DamagedValue);

        SetOther(ArcanumCredentialIdentity.MasterApiKeyAccount);

        CliTestResult result = await RunAsync("--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("Removed this profile's damaged installation identity credential item", result.Output, StringComparison.Ordinal);

        Assert.Contains("The next start creates its Grimoire under a new identity.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(DamagedValue, result.Output + result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            _credentials.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.TryGet(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MasterApiKeyAccount).Status);
    }

    [Fact]
    public async Task An_acknowledged_repair_in_json_reports_the_removal()
    {
        SetIdentity(DamagedValue);

        CliTestResult result = await RunAsync("--json", "--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement root = document.RootElement;

        Assert.Equal("removed", root.GetProperty("outcome").GetString());

        Assert.False(root.GetProperty("dryRun").GetBoolean());

        Assert.True(root.GetProperty("removed").GetBoolean());

        Assert.Equal(IdentityAccount(), root.GetProperty("credentialAccount").GetString());

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            _credentials.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);
    }

    [Fact]
    public async Task A_profile_with_no_item_has_nothing_to_repair()
    {
        CliTestResult result = await RunAsync("--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(
            "This profile carries no installation identity credential item. There is nothing to repair." + global::System.Environment.NewLine,
            result.Output);

        using JsonDocument document = JsonDocument.Parse(
            (await RunAsync("--json", "data", "repair-installation-identity")).Output);

        Assert.Equal("absent", document.RootElement.GetProperty("outcome").GetString());

        Assert.False(document.RootElement.GetProperty("removed").GetBoolean());
    }

    [Fact]
    public async Task Without_an_acknowledgement_a_non_interactive_run_is_refused_before_anything_is_removed()
    {
        SetIdentity(DamagedValue);

        CliTestResult result = await RunAsync("data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains(
            "Confirmation is required to remove the damaged installation identity. Re-run interactively, "
            + "pass --yes, or pass --dry-run to preview. Nothing was changed.",
            result.Error,
            StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_confirmation_that_is_accepted_removes_the_item_and_names_it_in_the_question()
    {
        SetIdentity(DamagedValue);

        RecordingPrompt prompt = new(answer: true);

        CliTestResult result = await RunAsync(
            [
                "data",
                "repair-installation-identity",
            ],
            prompt);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        string question = Assert.Single(prompt.Questions);

        Assert.Equal(
            "Remove this profile's damaged installation identity credential item "
            + $"(service {ArcanumCredentialIdentity.Service}, account {IdentityAccount()}), and nothing else?",
            question);

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            _credentials.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);
    }

    [Fact]
    public async Task A_confirmation_that_is_declined_removes_nothing()
    {
        SetIdentity(DamagedValue);

        RecordingPrompt prompt = new(answer: false);

        CliTestResult result = await RunAsync(
            [
                "data",
                "repair-installation-identity",
            ],
            prompt);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("The repair was not confirmed. Nothing was changed.", result.Error, StringComparison.Ordinal);

        Assert.Single(prompt.Questions);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_dry_run_and_an_absent_item_never_ask()
    {
        RecordingPrompt prompt = new(answer: true);

        SetIdentity(DamagedValue);

        _ = await RunAsync(["data", "repair-installation-identity", "--dry-run"], prompt);

        _ = _credentials.Delete(ArcanumCredentialIdentity.Service, IdentityAccount());

        _ = await RunAsync(["data", "repair-installation-identity"], prompt);

        Assert.Empty(prompt.Questions);
    }

    [Fact]
    public async Task A_valid_identity_is_refused_by_code_and_is_never_removed()
    {
        SetIdentity("C0FFEE00-1234-4ABC-8DEF-0123456789AB");

        CliTestResult result = await RunAsync("--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Equal(
            ErrorCodes.Data.Conflict
            + ": This profile's installation identity is a valid canonical identity. A valid identity is "
            + "never removed, so nothing was changed."
            + global::System.Environment.NewLine,
            result.Error);

        AssertIdentityIs("C0FFEE00-1234-4ABC-8DEF-0123456789AB");
    }

    [Fact]
    public async Task A_refusal_in_json_carries_the_code_the_message_and_the_exit_code()
    {
        SetIdentity("C0FFEE00-1234-4ABC-8DEF-0123456789AB");

        CliTestResult result = await RunAsync("--json", "--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(
            ErrorCodes.Data.Conflict
            + ": This profile's installation identity is a valid canonical identity. A valid identity is "
            + "never removed, so nothing was changed.",
            document.RootElement.GetProperty("error").GetString());

        Assert.Equal((int)CliExitCode.GenericError, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Evidence_bound_to_the_identity_is_refused_by_code_and_the_item_stays()
    {
        SetIdentity(DamagedValue);

        SetOther(ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(ProfileSuffix()));

        CliTestResult result = await RunAsync("--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains(ErrorCodes.Data.Blocked + ": ", result.Error, StringComparison.Ordinal);

        Assert.Contains("restore journal anchor credential", result.Error, StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_legacy_active_reset_record_is_refused_by_code_and_the_item_stays()
    {
        SetIdentity(DamagedValue);

        InstallationResetActiveStore activeStore = new(ArcanumPaths.GrimoireDirectory, _credentials);

        Result written = await activeStore.WriteLegacyV1ForTestsAsync(
            new InstallationResetActiveRecord(
                InstallationResetActiveStore.CurrentVersion,
                Guid.NewGuid(),
                "plan",
                InstallationResetScope.Global,
                null,
                new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
                InstallationResetPhase.Prepared,
                PointOfNoReturn: false,
                RowsDeleted: 0,
                FilesDeleted: 0,
                EstimatedBytesDeleted: 0,
                CredentialResults: [],
                LastErrorCode: null),
            CancellationToken.None);

        Assert.True(written.IsSuccess, written.IsFailure ? written.Error.Message : string.Empty);

        CliTestResult result = await RunAsync("--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains(ErrorCodes.Data.ResetInProgress + ": An installation reset is active.", result.Error, StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_held_installation_lock_is_refused_by_code_and_the_item_stays()
    {
        SetIdentity(DamagedValue);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(ArcanumPaths.GrimoireDirectory));

        CliTestResult result = await RunAsync("--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains(ErrorCodes.Data.FileLocked + ": A running host, a backup restore, or an installation reset holds", result.Error, StringComparison.Ordinal);

        Assert.Contains("Stop the host with 'arcanum serve quit'", result.Error, StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task The_command_composition_carries_no_Covenant_runtime_gate_or_hosted_work()
    {
        SetIdentity(DamagedValue);

        ServiceCollection services = Compose(prompt: null);

        StoppedHostResetTestSupport.RefuseToConstruct<StoppedHostCovenantAuthority>(services);

        StoppedHostResetTestSupport.RefuseToConstruct<StoppedHostFactoryErasure>(services);

        StoppedHostResetTestSupport.RefuseEveryCall<ICovenantOperationGate>(services);

        StoppedHostResetTestSupport.RefuseTheMasterKeyRead(services);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["--yes", "data", "repair-installation-identity"]);

        Assert.True(
            result.ExitCode == (int)CliExitCode.Success,
            $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}");
    }

    private Task<CliTestResult> RunAsync(params string[] args) =>
        CliTestHarness.RunAsync(Compose(prompt: null), args);

    private Task<CliTestResult> RunAsync(string[] args, RecordingPrompt prompt) =>
        CliTestHarness.RunAsync(Compose(prompt), args);

    private ServiceCollection Compose(RecordingPrompt? prompt)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IOsCredentialStore>();

        services.AddSingleton<IOsCredentialStore>(_credentials);

        StoppedHostResetTestSupport.UseTheProductionActiveResetRead(services, _credentials);

        if (prompt is not null)
        {
            services.RemoveAll<IConfirmationPrompt>();

            services.AddSingleton<IConfirmationPrompt>(prompt);
        }

        return services;
    }

    private string ProfileSuffix() =>
        BackupRestoreJournalAuthenticator.ResolveProfileNamespace(ArcanumPaths.GrimoireDirectory).Value.AccountSuffix;

    private string IdentityAccount() =>
        ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(ProfileSuffix());

    private void SetIdentity(string value)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Set(ArcanumCredentialIdentity.Service, IdentityAccount(), value).Status);
    }

    private void SetOther(string account)
    {
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Set(ArcanumCredentialIdentity.Service, account, "other-credential-material").Status);
    }

    private void AssertIdentityIs(string expected)
    {
        OsCredentialStoreResult stored = _credentials.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount());

        Assert.Equal(OsCredentialStoreStatus.Ok, stored.Status);

        Assert.Equal(expected, stored.Value);
    }

    private sealed class RecordingPrompt(bool answer) : IConfirmationPrompt
    {
        public List<string> Questions { get; } = [];

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken)
        {
            Questions.Add(question);

            return Task.FromResult(answer);
        }
    }
}
