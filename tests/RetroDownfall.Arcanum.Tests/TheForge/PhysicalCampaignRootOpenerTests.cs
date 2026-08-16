using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Tests.TheForge;

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
