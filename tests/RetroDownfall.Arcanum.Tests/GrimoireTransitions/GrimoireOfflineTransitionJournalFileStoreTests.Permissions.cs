using System.Security.AccessControl;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed partial class GrimoireOfflineTransitionJournalFileStoreTests
{
    [SkippableFact]
    public void Windows_owner_only_application_accepts_an_enumerated_modify_only_read_handle()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: exercises the shared Modify|Read|Write owner ACL.");

        GrimoireOfflineTransitionJournalLocation location = Location();

        File.WriteAllBytes(location.WorkingPath, Bytes("shared-owner-only-working-file").ToArray());

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(location.WorkingPath, logFailure: false));

        using GrimoireOfflineTransitionJournalFilePrimitives primitives = Value(
            GrimoireOfflineTransitionJournalFilePrimitives.Open(
                Path.GetDirectoryName(location.JournalPath)!,
                location.GuardedParentPhysicalIdentityDigest));

        using GrimoireOfflineTransitionJournalChildEnumeration children = Value(
            primitives.EnumerateExactChildren([location.WorkingLeaf]));

        GrimoireOfflineTransitionJournalOpenedFile observed = children.ExactChildren[location.WorkingLeaf];

        Assert.True(GrimoireOfflineTransitionJournalFilePrimitives.VerifyOwnerControlledOpenedFileHandle(observed));

        SecureFileOpenStatus writableStatus = primitives.OpenChild(
            location.WorkingLeaf,
            createExclusive: false,
            writable: true,
            out var writableProbe);

        writableProbe?.Dispose();

        Result applied = primitives.ApplyOwnerOnlyAndVerify(observed, location.WorkingLeaf);

        Assert.True(applied.IsSuccess,
            "Accepted shared Modify|Read|Write owner-only posture; enumeration and retained posture succeeded; "
            + "full writable reopen=" + writableStatus + "; permission application="
            + (applied.IsFailure ? applied.Error.Code : "success"));

        Assert.True(GrimoireOfflineTransitionJournalFilePrimitives.VerifyOwnerControlledOpenedFileHandle(observed));
    }

    [SkippableFact]
    public void Windows_owner_only_application_refuses_a_weakened_same_file_without_changing_its_acl()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: preserves the exact rejected DACL.");

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GrimoireOfflineTransitionJournalLocation location = Location();

        using GrimoireOfflineTransitionJournalFilePrimitives primitives = Value(
            GrimoireOfflineTransitionJournalFilePrimitives.Open(
                Path.GetDirectoryName(location.JournalPath)!,
                location.GuardedParentPhysicalIdentityDigest));

        using GrimoireOfflineTransitionJournalOpenedFile created = Value(
            primitives.CreateWorkingExclusive(location.WorkingLeaf));

        using GrimoireOfflineTransitionJournalChildEnumeration children = Value(
            primitives.EnumerateExactChildren([location.WorkingLeaf]));

        GrimoireOfflineTransitionJournalOpenedFile observed = children.ExactChildren[location.WorkingLeaf];

        GrantWindowsWorldRead(location.WorkingPath);

        string before = new FileInfo(location.WorkingPath).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.All);

        Assert.False(GrimoireOfflineTransitionJournalFilePrimitives.VerifyOwnerControlledOpenedFileHandle(observed));

        Result applied = primitives.ApplyOwnerOnlyAndVerify(observed, location.WorkingLeaf);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, applied.Error.Code);

        Assert.Equal(before, new FileInfo(location.WorkingPath).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    public void Owner_only_application_accepts_an_enumerated_read_handle()
    {
        GrimoireOfflineTransitionJournalLocation location = Location();

        using GrimoireOfflineTransitionJournalFilePrimitives primitives = Value(
            GrimoireOfflineTransitionJournalFilePrimitives.Open(
                Path.GetDirectoryName(location.JournalPath)!,
                location.GuardedParentPhysicalIdentityDigest));

        using GrimoireOfflineTransitionJournalOpenedFile created = Value(
            primitives.CreateWorkingExclusive(location.WorkingLeaf));

        using GrimoireOfflineTransitionJournalChildEnumeration children = Value(
            primitives.EnumerateExactChildren([location.WorkingLeaf]));

        GrimoireOfflineTransitionJournalOpenedFile observed = children.ExactChildren[location.WorkingLeaf];

        Result applied = primitives.ApplyOwnerOnlyAndVerify(observed, location.WorkingLeaf);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Code : "success");

        Assert.True(GrimoireOfflineTransitionJournalFilePrimitives.VerifyOwnerControlledOpenedFileHandle(observed));
    }

    [Fact]
    public void Owner_only_application_refuses_a_replaced_named_file_without_changing_its_permissions()
    {
        GrimoireOfflineTransitionJournalLocation location = Location();

        using GrimoireOfflineTransitionJournalFilePrimitives primitives = Value(
            GrimoireOfflineTransitionJournalFilePrimitives.Open(
                Path.GetDirectoryName(location.JournalPath)!,
                location.GuardedParentPhysicalIdentityDigest));

        using GrimoireOfflineTransitionJournalOpenedFile original = Value(
            primitives.CreateWorkingExclusive(location.WorkingLeaf));

        using GrimoireOfflineTransitionJournalChildEnumeration children = Value(
            primitives.EnumerateExactChildren([location.WorkingLeaf]));

        GrimoireOfflineTransitionJournalOpenedFile observed = children.ExactChildren[location.WorkingLeaf];

        Assert.True(primitives.MoveNoReplace(location.WorkingLeaf, location.PreviousLeaf).IsSuccess);

        using GrimoireOfflineTransitionJournalOpenedFile replacement = Value(
            primitives.CreateWorkingExclusive(location.WorkingLeaf));

        if (OperatingSystem.IsWindows())
        {
            GrantWindowsWorldRead(location.WorkingPath);
        }
        else
        {
            File.SetUnixFileMode(location.WorkingPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        Assert.False(GrimoireOfflineTransitionJournalFilePrimitives.VerifyOwnerControlledOpenedFileHandle(replacement));

        Result applied = primitives.ApplyOwnerOnlyAndVerify(observed, location.WorkingLeaf);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, applied.Error.Code);

        Assert.False(GrimoireOfflineTransitionJournalFilePrimitives.VerifyOwnerControlledOpenedFileHandle(replacement));
    }
}
