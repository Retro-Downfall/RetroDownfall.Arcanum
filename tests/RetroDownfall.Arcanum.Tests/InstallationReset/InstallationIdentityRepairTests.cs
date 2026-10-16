using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The guarded repair of a damaged pre-database installation identity: what it removes, and every
/// condition under which it removes nothing.
/// </summary>
/// <remarks>
/// The repair removes one credential item, so most of these cases are about the absence of a delete.
/// Each refusal asserts the code and the message a script and an operator read, that the damaged item
/// is still there afterwards, and that no delete was even attempted.
/// </remarks>
public sealed class InstallationIdentityRepairTests : IAsyncLifetime
{
    private const string DamagedValue = "c0ffee00-1234-4abc-8def-0123456789ab";

    private const string CanonicalValue = "C0FFEE00-1234-4ABC-8DEF-0123456789AB";

    private readonly TempWorkspace _workspace = new();

    private readonly InMemoryOsCredentialStore _inner = new();

    private readonly RepairCredentialStore _credentials;

    private string _root = string.Empty;

    private string _database = string.Empty;

    private BackupRestoreProfileNamespace _profile = null!;

    public InstallationIdentityRepairTests()
    {
        _credentials = new RepairCredentialStore(_inner);
    }

    public async Task InitializeAsync()
    {
        await _workspace.InitializeAsync();

        _root = _workspace.CreateSubdir("arcanum");

        _database = Path.Combine(_root, "arcanum.db");

        _profile = BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_root).Value;
    }

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task A_profile_with_no_identity_item_has_nothing_to_repair_and_nothing_is_touched()
    {
        InstallationIdentityRepair repair = Create();

        Result<InstallationIdentityRepairReport> planned = await repair.PlanAsync();

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Absent, planned.Value.State);

        Result<InstallationIdentityRepairReport> applied = await repair.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Absent, applied.Value.State);

        Assert.Equal(0, _credentials.DeleteCount);

        Assert.Equal(0, _credentials.SetCount);
    }

    [Fact]
    public async Task A_valid_canonical_identity_is_never_removed()
    {
        SetIdentity(CanonicalValue);

        InstallationIdentityRepair repair = Create();

        foreach (Result<InstallationIdentityRepairReport> result in new[]
                 {
                     await repair.PlanAsync(),
                     await repair.ApplyAsync(),
                 })
        {
            AssertRefused(result, ErrorCodes.Data.Conflict, "valid canonical identity");
        }

        AssertIdentityIs(CanonicalValue);
    }

    [Theory]
    [InlineData("c0ffee00-1234-4abc-8def-0123456789ab")]
    [InlineData("C0FFEE0012344ABC8DEF0123456789AB")]
    [InlineData("{C0FFEE00-1234-4ABC-8DEF-0123456789AB}")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not an identity")]
    [InlineData("")]
    public async Task A_planned_repair_of_a_damaged_value_reports_it_and_changes_nothing(string stored)
    {
        SetIdentity(stored);

        int setsBefore = _credentials.SetCount;

        Result<InstallationIdentityRepairReport> planned = await Create().PlanAsync();

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Damaged, planned.Value.State);

        Assert.Equal(ArcanumCredentialIdentity.Service, planned.Value.CredentialService);

        Assert.Equal(IdentityAccount(), planned.Value.CredentialAccount);

        Assert.Equal(0, _credentials.DeleteCount);

        Assert.Equal(setsBefore, _credentials.SetCount);

        AssertIdentityIs(stored);
    }

    [Theory]
    [InlineData("c0ffee00-1234-4abc-8def-0123456789ab")]
    [InlineData("not an identity")]
    [InlineData("")]
    public async Task An_applied_repair_removes_only_the_identity_item_and_reads_its_absence_back(string stored)
    {
        SetIdentity(stored);

        // Everything else this profile and this installation hold is somebody else's.
        SetOther(ArcanumCredentialIdentity.MasterApiKeyAccount);

        string otherProfile = ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(
            new string('a', ArcanumCredentialIdentity.ProfileNamespaceSuffixLength));

        SetOther(otherProfile);

        Result<InstallationIdentityRepairReport> applied = await Create().ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Removed, applied.Value.State);

        Assert.Equal(IdentityAccount(), applied.Value.CredentialAccount);

        Assert.Equal([IdentityAccount()], _credentials.Deleted);

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            _inner.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _inner.TryGet(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MasterApiKeyAccount).Status);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _inner.TryGet(ArcanumCredentialIdentity.Service, otherProfile).Status);

        // And it is repeatable: the second run finds nothing, and removes nothing.
        Result<InstallationIdentityRepairReport> again = await Create().ApplyAsync();

        Assert.True(again.IsSuccess);

        Assert.Equal(InstallationIdentityRepairState.Absent, again.Value.State);

        Assert.Single(_credentials.Deleted);
    }

    [Fact]
    public async Task The_stored_value_is_never_reported()
    {
        SetIdentity(DamagedValue);

        Result<InstallationIdentityRepairReport> planned = await Create().PlanAsync();

        Result<InstallationIdentityRepairReport> applied = await Create().ApplyAsync();

        foreach (InstallationIdentityRepairReport report in new[] { planned.Value, applied.Value })
        {
            Assert.DoesNotContain(DamagedValue, report.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_store_that_does_not_answer_is_not_a_damaged_value(bool throws)
    {
        SetIdentity(DamagedValue);

        _credentials.UnreadableAccounts.Add(IdentityAccount());

        _credentials.ThrowOnUnreadable = throws;

        InstallationIdentityRepair repair = Create();

        foreach (Result<InstallationIdentityRepairReport> result in new[]
                 {
                     await repair.PlanAsync(),
                     await repair.ApplyAsync(),
                 })
        {
            AssertRefused(result, ErrorCodes.Covenant.Unavailable, "could not be read");

            Assert.Contains("Unlock or repair", result.Error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, _credentials.DeleteCount);
    }

    [Theory]
    [InlineData("restore journal key")]
    [InlineData("restore journal anchor")]
    [InlineData("offline transition journal key")]
    [InlineData("offline transition journal anchor")]
    [InlineData("installation reset record key")]
    [InlineData("installation reset record anchor")]
    public async Task Evidence_bound_to_the_identity_blocks_the_repair(string evidence)
    {
        SetIdentity(DamagedValue);

        SetOther(EvidenceAccount(evidence));

        // The credential evidence is what is under test here: the probe is told no record is active,
        // so a reset key or anchor on its own is judged by the repair and not by the probe that would
        // otherwise refuse it first (see the real-probe cases below).
        InstallationIdentityRepair repair = Create(
            startupProbe: new FixedStartupProbe(Result<ActiveInstallationReset?>.Success(null)));

        foreach (Result<InstallationIdentityRepairReport> result in new[]
                 {
                     await repair.PlanAsync(),
                     await repair.ApplyAsync(),
                 })
        {
            AssertRefused(result, ErrorCodes.Data.Blocked, evidence + " credential");
        }

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task Evidence_whose_presence_cannot_be_proved_blocks_the_repair()
    {
        SetIdentity(DamagedValue);

        _credentials.IndeterminateAccounts.Add(EvidenceAccount("restore journal anchor"));

        Result<InstallationIdentityRepairReport> result = await Create().ApplyAsync();

        AssertRefused(result, ErrorCodes.Covenant.Unavailable, "restore journal anchor credential could not be proved");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task An_active_installation_reset_blocks_the_repair()
    {
        SetIdentity(DamagedValue);

        InstallationIdentityRepair repair = Create(
            startupProbe: new FixedStartupProbe(
                Result<ActiveInstallationReset?>.Success(
                    new ActiveInstallationReset(
                        InstallationResetScope.Global,
                        WorkspaceRoot: null,
                        PlanId: "plan",
                        OperationId: Guid.NewGuid(),
                        Phase: InstallationResetPhase.Prepared))));

        Result<InstallationIdentityRepairReport> result = await repair.ApplyAsync();

        AssertRefused(result, ErrorCodes.Data.ResetInProgress, "installation reset is active");

        AssertIdentityIs(DamagedValue);
    }

    [Theory]
    [InlineData("installation reset record key")]
    [InlineData("installation reset record anchor")]
    public async Task A_reset_credential_with_no_record_the_real_probe_can_prove_blocks_the_repair(string orphan)
    {
        SetIdentity(DamagedValue);

        SetOther(EvidenceAccount(orphan));

        Result<InstallationIdentityRepairReport> result = await Create().ApplyAsync();

        AssertRefused(result, ErrorCodes.Data.ControlPathUnavailable, "could not be proved");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_legacy_active_reset_record_the_real_probe_reads_blocks_the_repair()
    {
        SetIdentity(DamagedValue);

        InstallationResetActiveStore activeStore = new(_root, _credentials);

        Result written = await activeStore.WriteLegacyV1ForTestsAsync(
            CreateActiveRecord(),
            CancellationToken.None);

        Assert.True(written.IsSuccess, written.IsFailure ? written.Error.Message : string.Empty);

        Result<InstallationIdentityRepairReport> result = await Create().ApplyAsync();

        AssertRefused(result, ErrorCodes.Data.ResetInProgress, "installation reset is active");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task An_authenticated_active_reset_record_beside_a_damaged_identity_blocks_the_repair()
    {
        InstallationResetActiveStore activeStore = new(_root, _credentials);

        using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                   ArcanumMaintenanceLock.TryAcquire(_root)))
        {
            Result<InstallationResetActivePublication> begun = await activeStore.BeginAsync(
                held,
                Guid.Parse("91919191-9191-4191-8191-919191919191"),
                CreateActiveRecord(),
                CancellationToken.None);

            Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : string.Empty);
        }

        // The record's own publication seeded a canonical identity, and the record is authenticated
        // against it. Once the item is damaged the record can no longer be proved, and the repair
        // refuses on that rather than treating an unprovable record as no record.
        SetIdentity(DamagedValue);

        Result<InstallationIdentityRepairReport> result = await Create().ApplyAsync();

        AssertRefused(result, ErrorCodes.Data.ControlPathUnavailable, "could not be proved");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task An_active_reset_probe_that_cannot_answer_blocks_the_repair()
    {
        SetIdentity(DamagedValue);

        InstallationIdentityRepair repair = Create(
            startupProbe: new FixedStartupProbe(
                Result<ActiveInstallationReset?>.Failure(
                    new Error(ErrorCodes.Data.ControlPathUnavailable, "The probe found a damaged record."))));

        Result<InstallationIdentityRepairReport> result = await repair.ApplyAsync();

        AssertRefused(result, ErrorCodes.Data.ControlPathUnavailable, "could not be proved");

        Assert.Contains("The probe found a damaged record.", result.Error.Message, StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_running_host_or_any_other_holder_of_the_installation_lock_blocks_the_repair()
    {
        SetIdentity(DamagedValue);

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_root));

        InstallationIdentityRepair repair = Create();

        foreach (Result<InstallationIdentityRepairReport> result in new[]
                 {
                     await repair.PlanAsync(),
                     await repair.ApplyAsync(),
                 })
        {
            AssertRefused(result, ErrorCodes.Data.FileLocked, "Stop the host with 'arcanum serve quit'");
        }

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task The_installation_lock_is_released_when_the_repair_returns()
    {
        SetIdentity(DamagedValue);

        _ = await Create().ApplyAsync();

        using ArcanumMaintenanceLock? reacquired = ArcanumMaintenanceLock.TryAcquire(_root);

        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task A_database_file_that_holds_its_authority_row_blocks_the_repair()
    {
        SetIdentity(DamagedValue);

        File.WriteAllText(_database, "database");

        RowProbe rows = new(Result<bool>.Success(true));

        InstallationIdentityRepair repair = Create(data: rows);

        foreach (Result<InstallationIdentityRepairReport> result in new[]
                 {
                     await repair.PlanAsync(),
                     await repair.ApplyAsync(),
                 })
        {
            AssertRefused(result, ErrorCodes.Data.Blocked, "holds its authority row");
        }

        Assert.Equal(2, rows.Reads);

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_position_that_cannot_be_proved_blocks_the_repair_instead_of_being_guessed()
    {
        SetIdentity(DamagedValue);

        File.WriteAllText(_database, "database");

        RowProbe rows = new(Result<bool>.Failure(
            new Error(ErrorCodes.Data.InventoryUnavailable, "The existing Grimoire could not be opened.")));

        Result<InstallationIdentityRepairReport> result = await Create(data: rows).ApplyAsync();

        AssertRefused(result, ErrorCodes.Data.InventoryUnavailable, "authority row could not be proved");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_database_file_without_its_authority_row_does_not_block_the_repair()
    {
        SetIdentity(DamagedValue);

        File.WriteAllText(_database, "database");

        RowProbe rows = new(Result<bool>.Success(false));

        Result<InstallationIdentityRepairReport> applied = await Create(data: rows).ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Removed, applied.Value.State);

        Assert.Equal(1, rows.Reads);
    }

    [Fact]
    public async Task No_database_file_means_the_Grimoire_is_never_opened()
    {
        SetIdentity(DamagedValue);

        RowProbe rows = new(Result<bool>.Failure(
            new Error(ErrorCodes.Data.InventoryUnavailable, "must not be asked")));

        Result<InstallationIdentityRepairReport> applied = await Create(data: rows).ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(0, rows.Reads);
    }

    [Fact]
    public async Task The_default_data_service_cannot_prove_the_row_so_a_database_blocks_the_repair()
    {
        SetIdentity(DamagedValue);

        File.WriteAllText(_database, "database");

        Result<InstallationIdentityRepairReport> result = await Create(data: new DefaultMemberData()).ApplyAsync();

        AssertRefused(result, ErrorCodes.Data.InventoryUnavailable, "authority row could not be proved");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task Every_precondition_is_proved_again_when_the_repair_is_applied()
    {
        SetIdentity(DamagedValue);

        InstallationIdentityRepair repair = Create();

        Result<InstallationIdentityRepairReport> planned = await repair.PlanAsync();

        Assert.True(planned.IsSuccess);

        // Between the question and the answer, a restore anchor is published for this profile.
        SetOther(EvidenceAccount("restore journal anchor"));

        Result<InstallationIdentityRepairReport> applied = await repair.ApplyAsync();

        AssertRefused(applied, ErrorCodes.Data.Blocked, "restore journal anchor credential");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task An_item_that_vanishes_between_the_plan_and_the_apply_is_reported_and_not_deleted_again()
    {
        SetIdentity(DamagedValue);

        InstallationIdentityRepair repair = Create();

        Assert.Equal(InstallationIdentityRepairState.Damaged, (await repair.PlanAsync()).Value.State);

        _ = _inner.Delete(ArcanumCredentialIdentity.Service, IdentityAccount());

        Result<InstallationIdentityRepairReport> applied = await repair.ApplyAsync();

        Assert.True(applied.IsSuccess);

        Assert.Equal(InstallationIdentityRepairState.Absent, applied.Value.State);

        Assert.Equal(0, _credentials.DeleteCount);
    }

    [Fact]
    public async Task A_delete_the_store_refuses_is_reported_and_leaves_the_item()
    {
        SetIdentity(DamagedValue);

        _credentials.DeleteFails = true;

        Result<InstallationIdentityRepairReport> applied = await Create().ApplyAsync();

        AssertRefused(applied, ErrorCodes.Covenant.Unavailable, "could not be removed");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_delete_that_does_not_read_back_as_removed_is_not_reported_as_removed()
    {
        SetIdentity(DamagedValue);

        _credentials.DeleteIsIgnored = true;

        Result<InstallationIdentityRepairReport> applied = await Create().ApplyAsync();

        AssertRefused(applied, ErrorCodes.Covenant.IntegrityFailure, "did not read back as removed");

        AssertIdentityIs(DamagedValue, deleteAttempts: 1);
    }

    [Fact]
    public async Task A_cancelled_repair_changes_nothing()
    {
        SetIdentity(DamagedValue);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => Create().ApplyAsync(cancellation.Token));

        Assert.Equal(0, _credentials.DeleteCount);

        AssertIdentityIs(DamagedValue);
    }

    private InstallationIdentityRepair Create(
        IInstallationStartupProbe? startupProbe = null,
        IInstallationResetStoppedHostDataService? data = null) =>
        new(
            _credentials,
            startupProbe ?? new InstallationStartupProbe(
                _root,
                Path.Combine(_root, "arcanum.json"),
                _database,
                Path.Combine(_root, "security.dat"),
                _credentials),
            data ?? new RowProbe(Result<bool>.Success(false)),
            _root,
            _database);

    private string IdentityAccount() =>
        ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(_profile.AccountSuffix);

    private string EvidenceAccount(string evidence) =>
        evidence switch
        {
            "restore journal key" =>
                ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(_profile.AccountSuffix),
            "restore journal anchor" =>
                ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(_profile.AccountSuffix),
            "offline transition journal key" =>
                ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(_profile.AccountSuffix),
            "offline transition journal anchor" =>
                ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(_profile.AccountSuffix),
            "installation reset record key" =>
                ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(_profile.AccountSuffix),
            "installation reset record anchor" =>
                ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(_profile.AccountSuffix),
            _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null),
        };

    private void SetIdentity(string value)
    {
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _inner.Set(ArcanumCredentialIdentity.Service, IdentityAccount(), value).Status);
    }

    private void SetOther(string account)
    {
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _inner.Set(ArcanumCredentialIdentity.Service, account, "other-credential-material").Status);
    }

    private void AssertIdentityIs(string expected, int deleteAttempts = 0)
    {
        OsCredentialStoreResult stored = _inner.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount());

        Assert.Equal(OsCredentialStoreStatus.Ok, stored.Status);

        Assert.Equal(expected, stored.Value);

        Assert.Equal(deleteAttempts, _credentials.DeleteCount);
    }

    private static void AssertRefused(
        Result<InstallationIdentityRepairReport> result,
        string code,
        string messageFragment)
    {
        Assert.True(result.IsFailure, "The repair should have been refused.");

        Assert.Equal(code, result.Error.Code);

        Assert.Contains(messageFragment, result.Error.Message, StringComparison.Ordinal);
    }

    private static InstallationResetActiveRecord CreateActiveRecord()
    {
        InstallationResetAcceptedBinding binding = new(
            "binding",
            ["/selected"],
            ["/excluded"],
            [],
            [ArcanumCredentialIdentity.MasterApiKeyAccount],
            ["data-plan"]);

        return new InstallationResetActiveRecord(
            InstallationResetActiveStore.CurrentVersion,
            Guid.NewGuid(),
            "composite-plan",
            InstallationResetScope.Global,
            null,
            binding,
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure);
    }

    private sealed class FixedStartupProbe(Result<ActiveInstallationReset?> active) : IInstallationStartupProbe
    {
        public Result<bool> IsFreshInstallation() => throw new NotSupportedException();

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(active);
    }

    private sealed class RowProbe(Result<bool> answer)
        : DefaultMemberData, IInstallationResetStoppedHostDataService
    {
        public int Reads { get; private set; }

        public Task<Result<bool>> ReadAuthorityRowPresenceUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(issuer);

            Reads++;

            return Task.FromResult(answer);
        }
    }

    /// <summary>
    /// A data service that implements nothing the interface answers by default, so the default member is
    /// what a caller reaches.
    /// </summary>
    private class DefaultMemberData : IInstallationResetStoppedHostDataService
    {
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

        public Task<Result<Guid>> ReadIdentityUnderStoppedHostAuthorityAsync(
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

    private sealed class RepairCredentialStore(InMemoryOsCredentialStore inner)
        : IOsCredentialStore, IOsCredentialPresenceProbe
    {
        public HashSet<string> UnreadableAccounts { get; } = new(StringComparer.Ordinal);

        public HashSet<string> IndeterminateAccounts { get; } = new(StringComparer.Ordinal);

        public bool ThrowOnUnreadable { get; set; }

        public bool DeleteFails { get; set; }

        public bool DeleteIsIgnored { get; set; }

        public List<string> Deleted { get; } = [];

        public int DeleteCount => Deleted.Count;

        public int SetCount { get; private set; }

        public bool IsAvailable => true;

        public OsCredentialStoreStatus ProbePresence(string service, string account) =>
            IndeterminateAccounts.Contains(account)
                ? OsCredentialStoreStatus.Failed
                : inner.ProbePresence(service, account);

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            if (!UnreadableAccounts.Contains(account))
            {
                return inner.TryGet(service, account);
            }

            return ThrowOnUnreadable
                ? throw new IOException("The store did not answer.")
                : OsCredentialStoreResult.Failed("The store did not answer.");
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            SetCount++;

            return inner.Set(service, account, secret);
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            if (DeleteFails)
            {
                return OsCredentialStoreResult.Failed("The store refused the delete.");
            }

            Deleted.Add(account);

            return DeleteIsIgnored
                ? OsCredentialStoreResult.Ok(string.Empty)
                : inner.Delete(service, account);
        }
    }
}
