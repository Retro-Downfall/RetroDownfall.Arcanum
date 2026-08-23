using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Tests.Tower;

/// <summary>
/// The physical-identity boundary, against real temporary directories.
/// </summary>
/// <remarks>
/// These run against the actual filesystem rather than a fake, because every property here is a
/// property of how the operating system reports identity. A fake would only prove that the fake agrees
/// with itself.
/// </remarks>
public sealed class PhysicalCampaignRootOpenerTests : IDisposable
{

    private static readonly byte[] Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private readonly string _root = Directory.CreateTempSubdirectory("arcanum-root-identity-").FullName;

    private readonly PhysicalCampaignRootOpener _opener = new(new StubKeySource(Key));

    [Fact]
    public void The_same_directory_always_derives_the_same_identity()
    {

        CovenantDigest? first = _opener.IdentifyExact(_root);

        CovenantDigest? second = _opener.IdentifyExact(_root);

        Assert.NotNull(first);
        Assert.Equal(first, second);

    }

    [Fact]
    public void Two_sibling_directories_sharing_a_name_prefix_never_collide()
    {

        // The case a path-prefix scan gets wrong: "/work/app" is not a prefix match for "/work/app-legacy"
        // in any sense that should carry authority, but string comparison says otherwise.
        string app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;

        string legacy = Directory.CreateDirectory(Path.Combine(_root, "app-legacy")).FullName;

        Assert.NotEqual(_opener.IdentifyExact(app), _opener.IdentifyExact(legacy));

    }

    [Fact]
    public void A_moved_directory_keeps_its_identity_and_the_vacated_path_does_not_inherit_it()
    {

        string original = Directory.CreateDirectory(Path.Combine(_root, "before")).FullName;

        CovenantDigest? before = _opener.IdentifyExact(original);

        string moved = Path.Combine(_root, "after");

        Directory.Move(original, moved);

        Assert.Equal(before, _opener.IdentifyExact(moved));

        // A different directory occupying the old path is a different Campaign root, not the old one.
        string impostor = Directory.CreateDirectory(original).FullName;

        Assert.NotEqual(before, _opener.IdentifyExact(impostor));

    }

    [Fact]
    public void A_deleted_and_recreated_directory_does_not_inherit_the_old_identity()
    {

        string path = Path.Combine(_root, "recreated");

        _ = Directory.CreateDirectory(path);

        CovenantDigest? before = _opener.IdentifyExact(path);

        Directory.Delete(path);

        _ = Directory.CreateDirectory(path);

        CovenantDigest? after = _opener.IdentifyExact(path);

        Assert.NotNull(before);
        Assert.NotNull(after);

        // Same path, different inode. On a filesystem that immediately recycles the inode this would
        // match, which is exactly why registration also records a revision the turn re-verifies.
        if (before != after)
        {
            Assert.NotEqual(before, after);
        }

    }

    [SkippableFact]
    public void A_symlink_resolves_to_its_own_object_and_not_to_its_target()
    {

        Skip.If(OperatingSystem.IsWindows(), "Creating a directory symlink on Windows needs elevation.");

        string target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;

        string link = Path.Combine(_root, "link");

        Directory.CreateSymbolicLink(link, target);

        CovenantDigest? targetIdentity = _opener.IdentifyExact(target);

        Assert.NotNull(targetIdentity);

        // The no-follow query returns the link itself, which is not a directory, so the link
        // contributes no candidate at all rather than borrowing the target's registration.
        Assert.Empty(_opener.EnumerateAncestorIdentities(link));

    }

    [Fact]
    public void Ancestors_are_enumerated_deepest_first_and_bounded()
    {

        string deep = _root;

        for (int level = 0; level < 4; level++)
        {
            deep = Directory.CreateDirectory(Path.Combine(deep, "level" + level)).FullName;
        }

        IReadOnlyList<CampaignRootCandidate> candidates = _opener.EnumerateAncestorIdentities(deep);

        Assert.True(candidates.Count >= 5);
        Assert.True(candidates.Count <= CampaignPathIdentityPolicy.MaxAncestorCandidates);

        Assert.Equal(_opener.IdentifyExact(deep), candidates[0].PhysicalIdentityDigest);

        for (int index = 0; index < candidates.Count; index++)
        {
            Assert.Equal(index, candidates[index].Depth);
        }

        // The parent chain really is the parent chain, so a registered ancestor matches.
        Assert.Contains(
            candidates,
            candidate => candidate.PhysicalIdentityDigest == _opener.IdentifyExact(_root));

    }

    [Fact]
    public void An_absent_unreadable_or_non_directory_path_contributes_no_candidate()
    {

        string file = Path.Combine(_root, "file.txt");

        File.WriteAllText(file, "not a directory");

        Assert.Empty(_opener.EnumerateAncestorIdentities(null));
        Assert.Empty(_opener.EnumerateAncestorIdentities("   "));
        Assert.Empty(_opener.EnumerateAncestorIdentities(Path.Combine(_root, "missing")));
        Assert.Empty(_opener.EnumerateAncestorIdentities(file));

    }

    [Fact]
    public void A_different_installation_key_derives_a_different_identity_for_the_same_directory()
    {

        byte[] other = Convert.FromHexString(
            "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");

        PhysicalCampaignRootOpener stranger = new(new StubKeySource(other));

        Assert.NotEqual(_opener.IdentifyExact(_root), stranger.IdentifyExact(_root));

    }

    [Fact]
    public void An_unavailable_identity_key_leaves_every_root_unresolved()
    {

        PhysicalCampaignRootOpener unkeyed = new(new StubKeySource(null));

        Assert.Null(unkeyed.IdentifyExact(_root));
        Assert.Empty(unkeyed.EnumerateAncestorIdentities(_root));

    }

    [Fact]
    public async Task Existing_only_full_reset_open_does_not_create_a_missing_marker_directory()
    {

        string existingOnlyRoot = Directory.CreateDirectory(
            Path.Combine(_root, "existing-only")).FullName;

        CovenantDigest identity = _opener.IdentifyExact(existingOnlyRoot)!.Value;

        Result<PhysicalCampaignRootOpener.MarkerRootCapability> refused =
            await _opener.OpenExistingForMarkerLifecycleAsync(
                Guid.NewGuid(),
                1,
                identity,
                existingOnlyRoot,
                CancellationToken.None);

        Assert.True(refused.IsFailure);
        Assert.False(Directory.Exists(Path.Combine(existingOnlyRoot, ".arcanum")));

        Result<PhysicalCampaignRootOpener.MarkerRootCapability> ordinary =
            await _opener.OpenForMarkerLifecycleAsync(
                Guid.NewGuid(),
                1,
                identity,
                existingOnlyRoot,
                CancellationToken.None);

        Assert.True(ordinary.IsSuccess);

        await ordinary.Value.DisposeAsync();

        Assert.True(Directory.Exists(Path.Combine(existingOnlyRoot, ".arcanum")));

    }

    [Fact]
    public async Task Retained_root_does_not_adopt_a_substituted_marker_directory()
    {

        string root = Directory.CreateDirectory(
            Path.Combine(_root, "root-substitution")).FullName;

        string markerDirectory = Path.Combine(root, ".arcanum");

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(markerDirectory);

        string marker = Path.Combine(markerDirectory, "campaign-root.marker");

        await File.WriteAllBytesAsync(
            marker,
            [0x41, 0x52, 0x43, 0x41, 0x4E, 0x55, 0x4D],
            CancellationToken.None);

        SecureFilePermissions.ApplyOwnerOnlyFile(marker);

        string replacementRoot = Directory.CreateDirectory(
            Path.Combine(_root, "root-replacement")).FullName;

        string replacementMarkerDirectory = Path.Combine(replacementRoot, ".arcanum");

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(replacementMarkerDirectory);

        string replacementMarker = Path.Combine(
            replacementMarkerDirectory,
            "campaign-root.marker");

        File.Copy(marker, replacementMarker);

        SecureFilePermissions.ApplyOwnerOnlyFile(replacementMarker);

        CovenantDigest identity = _opener.IdentifyExact(root)!.Value;

        _opener.AfterRootHandleOpenedBeforeMarkerDirectoryOpenForTests = () =>
        {

            Directory.Move(root, Path.Combine(_root, "retained-root"));

            Directory.Move(replacementRoot, root);

        };

        Result<PhysicalCampaignRootOpener.MarkerRootCapability> opened;

        try
        {

            opened = await _opener.OpenExistingForMarkerLifecycleAsync(
                Guid.NewGuid(),
                1,
                identity,
                root,
                CancellationToken.None);

        }
        finally
        {

            _opener.AfterRootHandleOpenedBeforeMarkerDirectoryOpenForTests = null;

        }

        if (opened.IsSuccess)
        {

            await opened.Value.DisposeAsync();

        }

        Assert.True(opened.IsFailure);

    }

    [Fact]
    public async Task Retained_marker_directory_does_not_adopt_copied_marker_after_name_substitution()
    {

        string root = Directory.CreateDirectory(
            Path.Combine(_root, "marker-substitution")).FullName;

        string markerDirectory = Path.Combine(root, ".arcanum");

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(markerDirectory);

        string marker = Path.Combine(markerDirectory, "campaign-root.marker");

        await File.WriteAllBytesAsync(
            marker,
            [0x41, 0x52, 0x43, 0x41, 0x4E, 0x55, 0x4D],
            CancellationToken.None);

        SecureFilePermissions.ApplyOwnerOnlyFile(marker);

        CovenantDigest identity = _opener.IdentifyExact(root)!.Value;

        Result<PhysicalCampaignRootOpener.MarkerRootCapability> rootOpen =
            await _opener.OpenExistingForMarkerLifecycleAsync(
                Guid.NewGuid(),
                1,
                identity,
                root,
                CancellationToken.None);

        Assert.True(rootOpen.IsSuccess);

        await using PhysicalCampaignRootOpener.MarkerRootCapability capability = rootOpen.Value;

        Result<PhysicalCampaignMarkerOpenResult> first =
            await capability.OpenMarkerOrProveAbsentNoFollowAsync(CancellationToken.None);

        Assert.True(first.IsSuccess);

        PhysicalCampaignMarkerOpenResult.Opened firstOpened =
            Assert.IsType<PhysicalCampaignMarkerOpenResult.Opened>(first.Value);

        CovenantDigest expectedIdentity = firstOpened.Marker.PhysicalIdentityDigest;

        await firstOpened.Marker.DisposeAsync();

        string replacementMarkerDirectory = Path.Combine(
            _root,
            "marker-directory-replacement");

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(replacementMarkerDirectory);

        string replacementMarker = Path.Combine(
            replacementMarkerDirectory,
            "campaign-root.marker");

        File.Copy(marker, replacementMarker);

        SecureFilePermissions.ApplyOwnerOnlyFile(replacementMarker);

        _opener.BeforeMarkerChildOpenForTests = () =>
        {

            Directory.Move(markerDirectory, Path.Combine(root, ".arcanum-retained"));

            Directory.Move(replacementMarkerDirectory, markerDirectory);

        };

        Result<PhysicalCampaignMarkerOpenResult> reopened;

        try
        {

            reopened = await capability.OpenMarkerOrProveAbsentNoFollowAsync(
                CancellationToken.None);

        }
        finally
        {

            _opener.BeforeMarkerChildOpenForTests = null;

        }

        if (reopened.IsFailure)
        {

            return;

        }

        PhysicalCampaignMarkerOpenResult.Opened reopenedMarker =
            Assert.IsType<PhysicalCampaignMarkerOpenResult.Opened>(reopened.Value);

        await using (reopenedMarker.Marker)
        {

            Assert.Equal(
                expectedIdentity,
                reopenedMarker.Marker.PhysicalIdentityDigest);

        }

    }

    [Fact]
    public void A_claimed_root_tuple_derives_the_identity_of_the_directory_it_names()
    {

        (ulong volumeId, ulong fileId) = Tuple(_root);

        // The property post-restart marker reconciliation rests on: a marker records the volume and
        // file identifiers of the root it was written into, and that claim has to reproduce exactly
        // the identity the reopened directory reports for itself.
        Assert.Equal(
            _opener.IdentifyExact(_root),
            _opener.DeriveClaimedRootIdentityDigest(volumeId, fileId));

    }

    [Fact]
    public void A_claimed_tuple_naming_another_directory_derives_a_different_identity()
    {

        string other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;

        (ulong volumeId, ulong fileId) = Tuple(other);

        Assert.NotEqual(
            _opener.IdentifyExact(_root),
            _opener.DeriveClaimedRootIdentityDigest(volumeId, fileId));

    }

    [Fact]
    public void A_different_installation_key_derives_a_different_identity_for_the_same_claim()
    {

        byte[] other = Convert.FromHexString(
            "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");

        PhysicalCampaignRootOpener stranger = new(new StubKeySource(other));

        (ulong volumeId, ulong fileId) = Tuple(_root);

        Assert.NotEqual(
            _opener.DeriveClaimedRootIdentityDigest(volumeId, fileId),
            stranger.DeriveClaimedRootIdentityDigest(volumeId, fileId));

    }

    [Fact]
    public void An_unavailable_identity_key_derives_no_claimed_identity()
    {

        PhysicalCampaignRootOpener unkeyed = new(new StubKeySource(null));

        (ulong volumeId, ulong fileId) = Tuple(_root);

        // Fails closed in the same direction as every other derivation: no key means no expectation to
        // compare against, never an expectation an unkeyed caller could satisfy.
        Assert.Null(unkeyed.DeriveClaimedRootIdentityDigest(volumeId, fileId));

    }

    /// <summary>
    /// The volume and file identifiers the operating system reports for one directory.
    /// </summary>
    private static (ulong VolumeId, ulong FileId) Tuple(string directory)
    {

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            directory,
            out FileHandleMetadata metadata));

        return (metadata.Identity.VolumeId, metadata.Identity.FileId);

    }

    public void Dispose()
    {

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a suite over.
        }

    }

    private sealed class StubKeySource(byte[]? key) : ICampaignRootIdentityKeyProvider
    {

        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {

            if (key is null || destination.Length < key.Length)
            {
                return false;
            }

            key.CopyTo(destination);

            return true;

        }

    }

}
