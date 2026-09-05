using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// Removal of the three profile-namespaced restore-journal credentials.
/// </summary>
/// <remarks>
/// The suite asserts the order, the compare, and the idempotence separately, because a remover that
/// deleted all three unconditionally would satisfy any test that only checked they were gone
/// afterwards. What matters is that it deletes exactly these three, in exactly this order, only when
/// each still holds the value the terminal proof committed to.
/// </remarks>
public sealed class InstallationResetRestoreCredentialCleanupTests
{

    private static readonly CovenantDigest ProfileNamespace =
        new([.. Enumerable.Repeat((byte)0xAB, 32)]);

    private static readonly Guid InstallationId =
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    [Fact]
    public void Cleanup_phase_codes_are_literal_and_exhaustive()
    {

        Assert.Equal(1, (byte)InstallationResetRestoreCredentialCleanupPhase.AnchorRemoved);

        Assert.Equal(2, (byte)InstallationResetRestoreCredentialCleanupPhase.JournalKeyRemoved);

        Assert.Equal(
            3,
            (byte)InstallationResetRestoreCredentialCleanupPhase.InstallationIdentityRemoved);

        // Code 4 keeps the exact value and meaning it has always had. The enum is serialized as a
        // number inside an in-flight active record, so a record resumed at 4 still reads as "the
        // restore trio is gone" — it now also reads as "the transition pair is still owed", which is
        // what such a record actually means once a slot exists to owe anything about.
        Assert.Equal(
            4,
            (byte)InstallationResetRestoreCredentialCleanupPhase.RestoreCredentialsVerifiedAbsent);

        Assert.Equal(
            5,
            (byte)InstallationResetRestoreCredentialCleanupPhase.TransitionAnchorRemoved);

        Assert.Equal(6, (byte)InstallationResetRestoreCredentialCleanupPhase.TransitionKeyRemoved);

        Assert.Equal(
            7,
            (byte)InstallationResetRestoreCredentialCleanupPhase.TransitionCredentialsVerifiedAbsent);

        Assert.Equal(7, Enum.GetValues<InstallationResetRestoreCredentialCleanupPhase>().Length);

        Assert.Equal(1, (byte)BackupRestoreFullResetTerminalArm.NeverRestoredAbsence);

        Assert.Equal(2, (byte)BackupRestoreFullResetTerminalArm.ClosedAnchor);

        Assert.Equal(2, Enum.GetValues<BackupRestoreFullResetTerminalArm>().Length);

        Assert.Equal(
            1,
            (byte)GrimoireOfflineTransitionFullResetTerminalArm.NeverTransitionedAbsence);

        Assert.Equal(2, (byte)GrimoireOfflineTransitionFullResetTerminalArm.ClosedAnchor);

        Assert.Equal(2, Enum.GetValues<GrimoireOfflineTransitionFullResetTerminalArm>().Length);

    }

    [Fact]
    public void The_trio_is_derived_from_the_profile_namespace_and_is_fully_namespaced()
    {

        InstallationResetRestoreCredentialTrio trio =
            InstallationResetRestoreCredentialCleanup.Derive(ProfileNamespace);

        string suffix = Convert.ToHexStringLower(ProfileNamespace.Bytes);

        Assert.Equal($"backup-restore-journal-installation-{suffix}", trio.InstallationAccount);

        Assert.Equal($"backup-restore-journal-key-{suffix}", trio.JournalKeyAccount);

        Assert.Equal($"backup-restore-journal-anchor-{suffix}", trio.AnchorAccount);

        // Each one is recognised as a restore-journal account in its complete namespaced form, and a
        // bare prefix or a differently-namespaced sibling is not.
        Assert.All(
            new[] { trio.InstallationAccount, trio.JournalKeyAccount, trio.AnchorAccount },
            static account =>
                Assert.True(ArcanumCredentialIdentity.IsBackupRestoreJournalAccount(account)));

        Assert.False(
            ArcanumCredentialIdentity.IsBackupRestoreJournalAccount(
                "backup-restore-journal-anchor-"));

        Assert.False(
            ArcanumCredentialIdentity.IsBackupRestoreJournalAccount("backup-restore-journal-anchor"));

        Assert.False(
            ArcanumCredentialIdentity.IsBackupRestoreJournalAccount(
                "backup-restore-journal-anchor-" + new string('A', 64)));

    }

    [Fact]
    public void A_never_restored_installation_verifies_absence_without_removing_anything()
    {

        RecordingCredentialStore store = new();

        InstallationResetRestoreCredentialCleanup subject = new(store);

        Result<InstallationResetRestoreCredentialCleanupPhase> removed =
            subject.Remove(Terminal(BackupRestoreFullResetTerminalArm.NeverRestoredAbsence));

        Assert.True(removed.IsSuccess, removed.Error.Message);

        Assert.Equal(InstallationResetRestoreCredentialCleanupPhase.RestoreCredentialsVerifiedAbsent, removed.Value);

        Assert.Empty(store.Deletes);

    }

    [Fact]
    public void A_closed_anchor_removes_exactly_the_trio_in_anchor_key_identity_order()
    {

        InstallationResetRestoreCredentialTrio trio =
            InstallationResetRestoreCredentialCleanup.Derive(ProfileNamespace);

        RecordingCredentialStore store = new();

        store.Seed(trio.InstallationAccount, "installation-value");

        store.Seed(trio.JournalKeyAccount, "key-value");

        store.Seed(trio.AnchorAccount, "anchor-value");

        // An unrelated account and a differently-namespaced sibling, both of which must survive.
        store.Seed(ArcanumCredentialIdentity.MasterApiKeyAccount, "master");

        string otherProfile =
            "backup-restore-journal-anchor-" + new string('b', 64);

        store.Seed(otherProfile, "other-profile-anchor");

        InstallationResetRestoreCredentialCleanup subject = new(store);

        Result<InstallationResetRestoreCredentialCleanupPhase> removed =
            subject.Remove(TerminalFor(store, trio));

        Assert.True(removed.IsSuccess, removed.Error.Message);

        Assert.Equal(InstallationResetRestoreCredentialCleanupPhase.RestoreCredentialsVerifiedAbsent, removed.Value);

        // The anchor first: once it is gone no surviving journal can authenticate, so no partially
        // removed state can be mistaken for a restore in progress.
        Assert.Equal(
            [trio.AnchorAccount, trio.JournalKeyAccount, trio.InstallationAccount],
            store.Deletes);

        Assert.Equal("master", store.Peek(ArcanumCredentialIdentity.MasterApiKeyAccount));

        Assert.Equal("other-profile-anchor", store.Peek(otherProfile));

    }

    [Fact]
    public void Removal_is_idempotent_from_every_partial_state()
    {

        InstallationResetRestoreCredentialTrio trio =
            InstallationResetRestoreCredentialCleanup.Derive(ProfileNamespace);

        // Each partial state is one a crash between a delete and the record of it actually leaves,
        // paired with the deletions that remain to be issued from it in the one legal order.
        (string[] Present, string[] ExpectedDeletes)[] survivors =
        [
            (
                [trio.InstallationAccount, trio.JournalKeyAccount],
                [trio.JournalKeyAccount, trio.InstallationAccount]
            ),
            ([trio.InstallationAccount], [trio.InstallationAccount]),
            ([], []),
        ];

        foreach ((string[] present, string[] expectedDeletes) in survivors)
        {

            RecordingCredentialStore store = new();

            foreach (string account in present)
            {

                store.Seed(account, Value(account));

            }

            BackupRestoreFullResetTerminalProjectionV1 terminal = new(
                Version: 1,
                BackupRestoreFullResetTerminalArm.ClosedAnchor,
                ProfileNamespace,
                InstallationId,
                ClosedOperationId: Guid.NewGuid(),
                ClosedRevision: 3,
                ClosedEnvelopeDigest: ProfileNamespace,
                ClosedJournalLocationDigest: ProfileNamespace,
                Digest(trio.InstallationAccount),
                Digest(trio.JournalKeyAccount),
                Digest(trio.AnchorAccount),
                TerminalEvidenceDigest: ProfileNamespace);

            Result<InstallationResetRestoreCredentialCleanupPhase> removed =
                new InstallationResetRestoreCredentialCleanup(store).Remove(terminal);

            Assert.True(removed.IsSuccess, removed.Error.Message);

            Assert.Equal(
                InstallationResetRestoreCredentialCleanupPhase.RestoreCredentialsVerifiedAbsent,
                removed.Value);

            Assert.Equal(expectedDeletes, store.Deletes);

        }

    }

    [Fact]
    public void An_account_whose_value_changed_since_the_proof_is_refused_rather_than_deleted()
    {

        InstallationResetRestoreCredentialTrio trio =
            InstallationResetRestoreCredentialCleanup.Derive(ProfileNamespace);

        RecordingCredentialStore store = new();

        store.Seed(trio.InstallationAccount, Value(trio.InstallationAccount));

        store.Seed(trio.JournalKeyAccount, Value(trio.JournalKeyAccount));

        store.Seed(trio.AnchorAccount, "written after the proof");

        BackupRestoreFullResetTerminalProjectionV1 terminal = new(
            Version: 1,
            BackupRestoreFullResetTerminalArm.ClosedAnchor,
            ProfileNamespace,
            InstallationId,
            ClosedOperationId: Guid.NewGuid(),
            ClosedRevision: 3,
            ClosedEnvelopeDigest: ProfileNamespace,
            ClosedJournalLocationDigest: ProfileNamespace,
            Digest(trio.InstallationAccount),
            Digest(trio.JournalKeyAccount),
            Digest(trio.AnchorAccount),
            TerminalEvidenceDigest: ProfileNamespace);

        Result<InstallationResetRestoreCredentialCleanupPhase> removed =
            new InstallationResetRestoreCredentialCleanup(store).Remove(terminal);

        Assert.True(removed.IsFailure);

        // Nothing was deleted, including the two accounts whose values still matched: the anchor is
        // first in the order, so the refusal happens before either of the others is touched.
        Assert.Empty(store.Deletes);

        Assert.Equal("written after the proof", store.Peek(trio.AnchorAccount));

    }

    [Fact]
    public void An_account_value_digest_is_bound_to_the_account_it_came_from()
    {

        InstallationResetRestoreCredentialTrio trio =
            InstallationResetRestoreCredentialCleanup.Derive(ProfileNamespace);

        // Three slots holding identical bytes must not authorize each other's removal.
        Assert.NotEqual(
            BackupRestoreJournalAnchorStore.AccountValueDigest(trio.AnchorAccount, "same"),
            BackupRestoreJournalAnchorStore.AccountValueDigest(trio.JournalKeyAccount, "same"));

        Assert.NotEqual(
            BackupRestoreJournalAnchorStore.AccountValueDigest(trio.JournalKeyAccount, "same"),
            BackupRestoreJournalAnchorStore.AccountValueDigest(trio.InstallationAccount, "same"));

        Assert.Equal(
            BackupRestoreJournalAnchorStore.AccountValueDigest(trio.AnchorAccount, "same"),
            BackupRestoreJournalAnchorStore.AccountValueDigest(trio.AnchorAccount, "same"));

    }

    [Fact]
    public void An_account_present_where_the_absence_arm_projected_none_is_refused()
    {

        InstallationResetRestoreCredentialTrio trio =
            InstallationResetRestoreCredentialCleanup.Derive(ProfileNamespace);

        RecordingCredentialStore store = new();

        store.Seed(trio.AnchorAccount, "unexpected");

        Result<InstallationResetRestoreCredentialCleanupPhase> removed =
            new InstallationResetRestoreCredentialCleanup(store)
                .Remove(Terminal(BackupRestoreFullResetTerminalArm.NeverRestoredAbsence));

        Assert.True(removed.IsFailure);

        Assert.Empty(store.Deletes);

        Assert.Equal("unexpected", store.Peek(trio.AnchorAccount));

    }

    private static BackupRestoreFullResetTerminalProjectionV1 Terminal(
        BackupRestoreFullResetTerminalArm arm) =>
        new(
            Version: 1,
            arm,
            ProfileNamespace,
            InstallationId,
            ClosedOperationId: null,
            ClosedRevision: null,
            ClosedEnvelopeDigest: null,
            ClosedJournalLocationDigest: null,
            InstallationAccountValueDigest: null,
            JournalKeyAccountValueDigest: null,
            AnchorAccountValueDigest: null,
            TerminalEvidenceDigest: ProfileNamespace);

    private static BackupRestoreFullResetTerminalProjectionV1 TerminalFor(
        RecordingCredentialStore store,
        InstallationResetRestoreCredentialTrio trio) =>
        new(
            Version: 1,
            BackupRestoreFullResetTerminalArm.ClosedAnchor,
            ProfileNamespace,
            InstallationId,
            ClosedOperationId: Guid.NewGuid(),
            ClosedRevision: 3,
            ClosedEnvelopeDigest: ProfileNamespace,
            ClosedJournalLocationDigest: ProfileNamespace,
            BackupRestoreJournalAnchorStore.AccountValueDigest(
                trio.InstallationAccount,
                store.Peek(trio.InstallationAccount)!),
            BackupRestoreJournalAnchorStore.AccountValueDigest(
                trio.JournalKeyAccount,
                store.Peek(trio.JournalKeyAccount)!),
            BackupRestoreJournalAnchorStore.AccountValueDigest(
                trio.AnchorAccount,
                store.Peek(trio.AnchorAccount)!),
            TerminalEvidenceDigest: ProfileNamespace);

    private static string Value(string account) => $"value-for-{account}";

    private static CovenantDigest Digest(string account) =>
        BackupRestoreJournalAnchorStore.AccountValueDigest(account, Value(account));

    /// <summary>
    /// An in-memory credential store that records the exact order of deletions.
    /// </summary>
    /// <remarks>
    /// The order is the assertion, not a diagnostic. A remover that deleted the journal key before the
    /// anchor would leave a state no recovery can either finish or safely abandon, and a store that
    /// only reported the final contents could not tell the two apart.
    /// </remarks>
    private sealed class RecordingCredentialStore : IOsCredentialStore
    {

        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public bool IsAvailable => true;

        internal List<string> Deletes { get; } = [];

        internal void Seed(string account, string value) => _values[account] = value;

        internal string? Peek(string account) =>
            _values.TryGetValue(account, out string? value) ? value : null;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            _values.TryGetValue(account, out string? value)
                ? new OsCredentialStoreResult(OsCredentialStoreStatus.Ok, value, null)
                : new OsCredentialStoreResult(OsCredentialStoreStatus.NotFound, null, null);

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            _values[account] = secret;

            return new OsCredentialStoreResult(OsCredentialStoreStatus.Ok, null, null);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            Deletes.Add(account);

            return _values.Remove(account)
                ? new OsCredentialStoreResult(OsCredentialStoreStatus.Ok, null, null)
                : new OsCredentialStoreResult(OsCredentialStoreStatus.NotFound, null, null);

        }

    }

}
