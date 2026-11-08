using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed partial class GrimoireOfflineTransitionJournalFileStoreTests
{
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
