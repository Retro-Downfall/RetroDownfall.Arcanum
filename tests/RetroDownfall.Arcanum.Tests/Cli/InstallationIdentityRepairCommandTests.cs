using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

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

        Assert.False(root.GetProperty("restored").GetBoolean());

        Assert.False(root.GetProperty("reseeded").GetBoolean());

        Assert.Equal("remove", root.GetProperty("action").GetString());

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

        Assert.False(root.GetProperty("restored").GetBoolean());

        Assert.Equal("remove", root.GetProperty("action").GetString());

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
            ErrorCodes.Data.ConfirmationRequired
            + ": Confirmation is required to repair the damaged installation identity. Re-run interactively, "
            + "pass --yes, or pass --dry-run to preview. Nothing was changed.",
            result.Error,
            StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    /// <summary>
    /// A confirmation that could not be asked is one document with the exit code, and carries its code
    /// the way every other refusal of this command does, so a script that splits the error on the
    /// separator finds a code.
    /// </summary>
    [Fact]
    public async Task A_confirmation_refusal_in_json_is_one_document_with_exit_code_2()
    {
        SetIdentity(DamagedValue);

        CliTestResult result = await RunAsync("--json", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(
            ErrorCodes.Data.ConfirmationRequired
            + ": Confirmation is required to repair the damaged installation identity. Re-run interactively, "
            + "pass --yes, or pass --dry-run to preview. Nothing was changed.",
            document.RootElement.GetProperty("error").GetString());

        Assert.Equal(2, document.RootElement.GetProperty("exitCode").GetInt32());

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

        Assert.Contains(
            ErrorCodes.Data.ConfirmationRequired + ": The repair was not confirmed. Nothing was changed.",
            result.Error,
            StringComparison.Ordinal);

        Assert.Single(prompt.Questions);

        AssertIdentityIs(DamagedValue);
    }

    /// <summary>
    /// The installation lock is not held while the operator is asked, so the profile can change while
    /// the question is open. The apply makes only the change that was confirmed.
    /// </summary>
    [Fact]
    public async Task A_profile_that_changes_while_the_question_is_open_is_refused_by_code_and_the_item_stays()
    {
        SetIdentity(DamagedValue);

        // No Grimoire and no anchor: the question is whether to remove the item. While it is open, a
        // closed restore anchor appears, so the same apply would write an identity instead.
        RecordingPrompt prompt = new(answer: true, whileOpen: () => SetRestoreAnchor(Named, BackupRestoreJournalAnchorState.Closed));

        CliTestResult result = await RunAsync(["data", "repair-installation-identity"], prompt);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        string question = Assert.Single(prompt.Questions);

        Assert.StartsWith("Remove this profile's damaged installation identity credential item", question, StringComparison.Ordinal);

        Assert.Contains(
            ErrorCodes.Data.PlanChanged
            + ": What the installation identity repair would do changed after it was confirmed, so nothing "
            + "was changed. Run the repair again.",
            result.Error,
            StringComparison.Ordinal);

        Assert.DoesNotContain(Named.ToString("D"), result.Output + result.Error, StringComparison.OrdinalIgnoreCase);

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

        SetRestoreAnchor(Named, BackupRestoreJournalAnchorState.Active);

        CliTestResult result = await RunAsync("--yes", "data", "repair-installation-identity");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains(ErrorCodes.Data.Blocked + ": ", result.Error, StringComparison.Ordinal);

        Assert.Contains("replacement restore is unfinished", result.Error, StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_dry_run_beside_a_closed_anchor_says_the_identity_is_written_back_and_changes_nothing()
    {
        SetIdentity(DamagedValue);

        SetRestoreAnchor(Named, BackupRestoreJournalAnchorState.Closed);

        CliTestResult text = await RunAsync("data", "repair-installation-identity", "--dry-run");

        Assert.Equal((int)CliExitCode.Success, text.ExitCode);

        Assert.Contains("writes that identity back into the one item", text.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("removes that one item", text.Output, StringComparison.Ordinal);

        CliTestResult json = await RunAsync("--json", "data", "repair-installation-identity", "--dry-run");

        using JsonDocument document = JsonDocument.Parse(json.Output);

        Assert.Equal("damaged", document.RootElement.GetProperty("outcome").GetString());

        Assert.Equal("restore", document.RootElement.GetProperty("action").GetString());

        Assert.False(document.RootElement.GetProperty("removed").GetBoolean());

        Assert.False(document.RootElement.GetProperty("restored").GetBoolean());

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task An_acknowledged_repair_beside_a_closed_anchor_writes_the_identity_back_and_says_so()
    {
        SetIdentity(DamagedValue);

        SetRestoreAnchor(Named, BackupRestoreJournalAnchorState.Closed);

        RecordingPrompt prompt = new(answer: true);

        CliTestResult text = await RunAsync(["data", "repair-installation-identity"], prompt);

        Assert.Equal((int)CliExitCode.Success, text.ExitCode);

        Assert.Equal(
            "Write back into this profile's damaged installation identity credential item "
            + $"(service {ArcanumCredentialIdentity.Service}, account {IdentityAccount()}) the identity a "
            + "closed anchor names, and change nothing else?",
            Assert.Single(prompt.Questions));

        Assert.Contains("Wrote the identity a closed anchor names back", text.Output, StringComparison.Ordinal);

        AssertIdentityIs(Named.ToString("D").ToUpperInvariant());

        // Again, as a script reads it.
        SetIdentity(DamagedValue);

        CliTestResult json = await RunAsync("--json", "--yes", "data", "repair-installation-identity");

        using JsonDocument document = JsonDocument.Parse(json.Output);

        Assert.Equal("restored", document.RootElement.GetProperty("outcome").GetString());

        Assert.True(document.RootElement.GetProperty("restored").GetBoolean());

        Assert.False(document.RootElement.GetProperty("removed").GetBoolean());

        Assert.False(document.RootElement.GetProperty("reseeded").GetBoolean());

        Assert.Equal("restore", document.RootElement.GetProperty("action").GetString());

        Assert.DoesNotContain(Named.ToString("D"), json.Output + json.Error, StringComparison.OrdinalIgnoreCase);

        AssertIdentityIs(Named.ToString("D").ToUpperInvariant());
    }

    [Fact]
    public async Task A_dry_run_beside_a_Grimoire_that_holds_its_row_says_the_identity_is_seeded_again_from_the_row_and_changes_nothing()
    {
        SetIdentity(DamagedValue);

        AuthorityRowData grimoire = HoldTheGrimoireRow(Named);

        CliTestResult text = await RunAsync(["data", "repair-installation-identity", "--dry-run"], grimoire);

        Assert.Equal((int)CliExitCode.Success, text.ExitCode);

        Assert.Contains("The Grimoire holds its authority row", text.Output, StringComparison.Ordinal);

        Assert.Contains("writes that row's installation identity into the one item", text.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("removes that one item", text.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("closed restore or offline-transition anchor", text.Output, StringComparison.Ordinal);

        CliTestResult json = await RunAsync(["--json", "data", "repair-installation-identity", "--dry-run"], grimoire);

        using JsonDocument document = JsonDocument.Parse(json.Output);

        Assert.Equal("damaged", document.RootElement.GetProperty("outcome").GetString());

        Assert.Equal("reseed", document.RootElement.GetProperty("action").GetString());

        Assert.False(document.RootElement.GetProperty("removed").GetBoolean());

        Assert.False(document.RootElement.GetProperty("restored").GetBoolean());

        Assert.False(document.RootElement.GetProperty("reseeded").GetBoolean());

        Assert.DoesNotContain(Named.ToString("D"), text.Output + json.Output + text.Error + json.Error, StringComparison.OrdinalIgnoreCase);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task An_acknowledged_repair_beside_a_Grimoire_that_holds_its_row_seeds_the_identity_again_and_says_so()
    {
        SetIdentity(DamagedValue);

        AuthorityRowData grimoire = HoldTheGrimoireRow(Named);

        RecordingPrompt prompt = new(answer: true);

        CliTestResult text = await RunAsync(["data", "repair-installation-identity"], prompt, grimoire);

        Assert.Equal((int)CliExitCode.Success, text.ExitCode);

        Assert.Equal(
            "Seed this profile's damaged installation identity credential item "
            + $"(service {ArcanumCredentialIdentity.Service}, account {IdentityAccount()}) again from the "
            + "installation the Grimoire's authority row carries, and change nothing else?",
            Assert.Single(prompt.Questions));

        Assert.Contains("Seeded this profile's installation identity credential item", text.Output, StringComparison.Ordinal);

        Assert.Contains("again from the installation the Grimoire's authority row carries", text.Output, StringComparison.Ordinal);

        AssertIdentityIs(Named.ToString("D").ToUpperInvariant());

        // Again, as a script reads it.
        SetIdentity(DamagedValue);

        CliTestResult json = await RunAsync(["--json", "--yes", "data", "repair-installation-identity"], grimoire);

        using JsonDocument document = JsonDocument.Parse(json.Output);

        Assert.Equal("reseeded", document.RootElement.GetProperty("outcome").GetString());

        Assert.True(document.RootElement.GetProperty("reseeded").GetBoolean());

        Assert.False(document.RootElement.GetProperty("restored").GetBoolean());

        Assert.False(document.RootElement.GetProperty("removed").GetBoolean());

        Assert.Equal("reseed", document.RootElement.GetProperty("action").GetString());

        Assert.DoesNotContain(Named.ToString("D"), text.Output + json.Output + text.Error + json.Error, StringComparison.OrdinalIgnoreCase);

        AssertIdentityIs(Named.ToString("D").ToUpperInvariant());
    }

    [Fact]
    public async Task A_closed_anchor_naming_another_installation_than_the_Grimoire_is_refused_by_code_and_the_item_stays()
    {
        SetIdentity(DamagedValue);

        AuthorityRowData grimoire = HoldTheGrimoireRow(Named);

        SetRestoreAnchor(Guid.Parse("f0f1f2f3-aaaa-4bbb-8ccc-ddddeeeeffff"), BackupRestoreJournalAnchorState.Closed);

        CliTestResult result = await RunAsync(["--yes", "data", "repair-installation-identity"], grimoire);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains(
            ErrorCodes.Covenant.ManualRecoveryRequired
            + ": The closed restore anchor of this profile names a different installation than the "
            + "Grimoire's authority row carries",
            result.Error,
            StringComparison.Ordinal);

        Assert.Contains("does not choose between", result.Error, StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    // Drives the handler's own re-proof under the lock, which is the refusal for a reset recorded after the
    // CLI's startup check. The harness runs the command tree without Program, so the startup check that
    // refuses an already-active reset first is pinned in InstallationFactoryResetArgvPreflightTests.
    [Fact]
    public async Task The_handlers_recheck_under_the_lock_refuses_a_legacy_active_reset_record_by_code_and_the_item_stays()
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

    private Task<CliTestResult> RunAsync(string[] args, AuthorityRowData grimoire) =>
        CliTestHarness.RunAsync(Compose(prompt: null, grimoire), args);

    private Task<CliTestResult> RunAsync(string[] args, RecordingPrompt prompt, AuthorityRowData grimoire) =>
        CliTestHarness.RunAsync(Compose(prompt, grimoire), args);

    private ServiceCollection Compose(RecordingPrompt? prompt, AuthorityRowData? grimoire = null)
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

        if (grimoire is not null)
        {
            services.RemoveAll<IInstallationResetStoppedHostDataService>();

            services.AddSingleton<IInstallationResetStoppedHostDataService>(grimoire);
        }

        return services;
    }

    /// <summary>
    /// A database file that exists, so the repair asks the Grimoire, and a data service that answers
    /// that it holds its authority row for the installation given.
    /// </summary>
    private static AuthorityRowData HoldTheGrimoireRow(Guid installation)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.WriteAllText(ArcanumPaths.GrimoireDatabaseFile, "database");

        return new AuthorityRowData(installation);
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

    private static readonly Guid Named = Guid.Parse("0a0b0c0d-1111-4222-8333-444455556666");

    /// <summary>A restore anchor in the exact form the store writes it, for this profile.</summary>
    private void SetRestoreAnchor(Guid installation, BackupRestoreJournalAnchorState state)
    {
        BackupRestoreProfileNamespace profile =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(ArcanumPaths.GrimoireDirectory).Value;

        BackupRestoreJournalAnchorV1 anchor = new(
            Version: 1,
            profile.Digest,
            installation,
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Revision: 3,
            new CovenantDigest([.. Enumerable.Repeat((byte)0x11, 32)]),
            new CovenantDigest([.. Enumerable.Repeat((byte)0x22, 32)]),
            state);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Set(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(profile.AccountSuffix),
                BackupRestoreJournalAuthenticator.EncodeAnchor(anchor).Value).Status);
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

    /// <summary>
    /// A stopped-host data service for a Grimoire that holds its authority row for one installation. It
    /// answers the two questions the repair asks and nothing else.
    /// </summary>
    private sealed class AuthorityRowData(Guid installation) : IInstallationResetStoppedHostDataService
    {
        public Task<Result<bool>> ReadAuthorityRowPresenceUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<bool>.Success(true));

        public Task<Result<Guid>> ReadIdentityUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<Guid>.Success(installation));

        public Task<Result<DataRetentionPlan>> PlanUnderStoppedHostAuthorityAsync(
            InstallationResetDataPlanRequest request,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetWorkspaceResolution>> ResolveWorkspaceUnderStoppedHostAuthorityAsync(
            string invocationDirectory,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<DataRetentionApplyResult>> ApplyUnderStoppedHostAuthorityAsync(
            DataRetentionApplyRequest request,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPrompt(bool answer, Action? whileOpen = null) : IConfirmationPrompt
    {
        public List<string> Questions { get; } = [];

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken)
        {
            Questions.Add(question);

            whileOpen?.Invoke();

            return Task.FromResult(answer);
        }
    }
}
