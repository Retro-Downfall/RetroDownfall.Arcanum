using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The proof that one profile's restore history is over, and every shape that must not produce one.
/// </summary>
/// <remarks>
/// Only two arms yield a proof, and the negative arms matter more than the positive ones: the three
/// profile accounts are the only evidence that could ever finish an interrupted restore, so a proof
/// minted for an active or half-cleaned profile authorizes destroying the very thing recovery needs.
/// </remarks>
public sealed class BackupRestoreFullResetTerminalProofTests : IDisposable
{

    private static readonly Guid InstallationId =
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    private InMemoryOsCredentialStore _credentials = new();

    private readonly string _guarded = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"arcanum-terminal-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void A_profile_that_never_restored_proves_the_absence_arm()
    {

        Result<BackupRestoreFullResetTerminalProjectionV1> proved = Prove();

        Assert.True(proved.IsSuccess, proved.Error.Message);

        Assert.Equal(BackupRestoreFullResetTerminalArm.NeverRestoredAbsence, proved.Value.Arm);

        Assert.Equal(1, proved.Value.Version);

        Assert.Equal(InstallationId, proved.Value.InstallationId);

        // Nothing to commit to, so every optional field is absent rather than zero.
        Assert.Null(proved.Value.ClosedOperationId);

        Assert.Null(proved.Value.ClosedRevision);

        Assert.Null(proved.Value.AnchorAccountValueDigest);

        Assert.Null(proved.Value.JournalKeyAccountValueDigest);

        Assert.Null(proved.Value.InstallationAccountValueDigest);

    }

    [Fact]
    public void A_partially_cleaned_profile_proves_nothing_in_either_arm()
    {

        // The anchor is gone but the key survives: a restore did happen and somebody removed part of
        // its evidence. Answering "never restored" here would authorize destroying the rest.
        Seed(KeyAccount, "surviving-key");

        Assert.True(Prove().IsFailure);

        _credentials = new InMemoryOsCredentialStore();

        Seed(InstallationAccount, "surviving-identity");

        Assert.True(Prove().IsFailure);

    }

    [Fact]
    public void An_anchor_from_another_installation_or_another_profile_proves_nothing()
    {

        BackupRestoreProfileNamespace profile = Profile();

        SeedClosedAnchor(profile, InstallationId);

        // The right installation still proves.
        Assert.True(Prove().IsSuccess);

        // A different installation does not, even though everything else about the anchor is intact.
        Assert.True(Prove(Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff")).IsFailure);

        _credentials = new InMemoryOsCredentialStore();

        // An anchor whose recorded profile namespace is not this profile's is not ours to act on.
        SeedClosedAnchor(profile, InstallationId, otherProfileDigest: true);

        Assert.True(Prove().IsFailure);

    }

    [Fact]
    public void A_closed_anchor_with_the_whole_credential_set_proves_the_closed_arm_and_commits_to_it()
    {

        BackupRestoreProfileNamespace profile = Profile();

        SeedClosedAnchor(profile, InstallationId);

        Result<BackupRestoreFullResetTerminalProjectionV1> proved = Prove();

        Assert.True(proved.IsSuccess, proved.Error.Message);

        Assert.Equal(BackupRestoreFullResetTerminalArm.ClosedAnchor, proved.Value.Arm);

        Assert.NotNull(proved.Value.ClosedOperationId);

        Assert.NotNull(proved.Value.ClosedRevision);

        // Each account digest is the account-bound one a later compare-removal has to reproduce.
        Assert.Equal(
            BackupRestoreJournalAnchorStore.AccountValueDigest(
                KeyAccount,
                _credentials.TryGet(ArcanumCredentialIdentity.Service, KeyAccount).Value!),
            proved.Value.JournalKeyAccountValueDigest);

        Assert.Equal(
            BackupRestoreJournalAnchorStore.AccountValueDigest(
                InstallationAccount,
                _credentials.TryGet(ArcanumCredentialIdentity.Service, InstallationAccount).Value!),
            proved.Value.InstallationAccountValueDigest);

    }

    [Fact]
    public void The_terminal_evidence_digest_separates_the_two_arms_and_every_field_that_decided_them()
    {

        Result<BackupRestoreFullResetTerminalProjectionV1> absence = Prove();

        Assert.True(absence.IsSuccess, absence.Error.Message);

        BackupRestoreProfileNamespace profile = Profile();

        SeedClosedAnchor(profile, InstallationId);

        Result<BackupRestoreFullResetTerminalProjectionV1> closed = Prove();

        Assert.True(closed.IsSuccess, closed.Error.Message);

        // Two different observations of the same profile and installation, so only the arm and the
        // fields it carries can separate them.
        Assert.NotEqual(
            absence.Value.TerminalEvidenceDigest,
            closed.Value.TerminalEvidenceDigest);

        // Stable across reads: the same observed state has to reproduce the same commitment, or a
        // resume could never match the projection it persisted.
        Result<BackupRestoreFullResetTerminalProjectionV1> again = Prove();

        Assert.True(again.IsSuccess, again.Error.Message);

        Assert.Equal(
            closed.Value.TerminalEvidenceDigest,
            again.Value.TerminalEvidenceDigest);

    }

    [Fact]
    public void A_closed_anchor_missing_part_of_its_credential_set_proves_nothing()
    {

        BackupRestoreProfileNamespace profile = Profile();

        SeedClosedAnchor(profile, InstallationId);

        Assert.True(Prove().IsSuccess);

        _ = _credentials.Delete(ArcanumCredentialIdentity.Service, KeyAccount);

        Assert.True(Prove().IsFailure);

    }

    public void Dispose()
    {

        try
        {

            Directory.Delete(_guarded, recursive: true);

            // The maintenance lock lives beside the directory it guards, not inside it, so removing
            // the guarded root leaves the lock file behind at the temp root. Enough of those and the
            // suite stalls on lock contention rather than failing.
            File.Delete(RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock.LockPathFor(_guarded));

        }
        catch (IOException)
        {

            // A scratch directory under the OS temp root; a failure to remove it is not an outcome.
        }

    }

    private string AnchorAccount =>
        ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(Profile().AccountSuffix);

    private string KeyAccount =>
        ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(Profile().AccountSuffix);

    private string InstallationAccount =>
        ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(Profile().AccountSuffix);

    private BackupRestoreProfileNamespace Profile()
    {

        Result<BackupRestoreProfileNamespace> resolved =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guarded);

        Assert.True(resolved.IsSuccess, resolved.Error.Message);

        return resolved.Value;

    }

    private BackupRestoreJournalAnchorStore Store() =>
        new(
            _credentials,
            new BackupRestoreJournalKeyProvider(_credentials),
            new BackupRestoreJournalInstallationIdentityProvider(_credentials));

    private ArcanumMaintenanceLock Lock() =>
        Assert.IsType<ArcanumMaintenanceLock>(ArcanumMaintenanceLock.TryAcquire(_guarded));

    private Result<BackupRestoreFullResetTerminalProjectionV1> Prove(Guid? installationId = null)
    {

        using ArcanumMaintenanceLock held = Lock();

        return Store().ProveFullResetTerminal(
            held,
            _guarded,
            Profile(),
            installationId ?? InstallationId,
            []);

    }

    private void Seed(string account, string value) =>
        _ = _credentials.Set(ArcanumCredentialIdentity.Service, account, value);

    /// <summary>
    /// Writes the credential set an installation whose last restore closed actually leaves behind.
    /// </summary>
    private void SeedClosedAnchor(
        BackupRestoreProfileNamespace profile,
        Guid installationId,
        bool otherProfileDigest = false)
    {

        BackupRestoreJournalAnchorV1 anchor = new(
            Version: 1,
            otherProfileDigest
                ? new CovenantDigest([.. Enumerable.Repeat((byte)0x7C, 32)])
                : profile.Digest,
            installationId,
            Guid.Parse("11112222-3333-4444-8555-666677778888"),
            Revision: 4,
            new CovenantDigest([.. Enumerable.Repeat((byte)0x21, 32)]),
            new CovenantDigest([.. Enumerable.Repeat((byte)0x22, 32)]),
            BackupRestoreJournalAnchorState.Closed);

        Result<string> encoded = BackupRestoreJournalAuthenticator.EncodeAnchor(anchor);

        Assert.True(encoded.IsSuccess, encoded.Error.Message);

        Seed(AnchorAccount, encoded.Value);

        Seed(KeyAccount, Convert.ToBase64String([.. Enumerable.Repeat((byte)0x33, 32)]));

        Seed(InstallationAccount, installationId.ToString("D"));

    }

}
