using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// <see cref="CampaignRootContainment"/> on real directories: the file system answers whenever it can, the text of both paths only when it
/// cannot, and a path is re-expressed under a root's own spelling by the physical identity of the directories above it.
/// </summary>
public sealed class CampaignRootContainmentTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("arcanum-root-containment-").FullName;

    public void Dispose() =>
        _ = TestDirectoryCleanup.TryDelete(_parent, nameof(CampaignRootContainmentTests));

    // The root is not on disk, so there is nothing to ask the file system: the text of both paths decides, by whole components.
    [Fact]
    public void A_root_that_is_not_on_disk_is_matched_by_the_text_of_both_paths()
    {
        string root = Path.Combine(_parent, "never-created");

        CampaignRootContainment? below = CampaignRootContainment.TryCreate(Path.Combine(root, "src", "deep"));

        Assert.NotNull(below);

        Assert.Equal(2, below.DepthBelow(root));

        Assert.Equal(0, CampaignRootContainment.TryCreate(root + Path.DirectorySeparatorChar)!.DepthBelow(root));

        CampaignRootContainment? sibling = CampaignRootContainment.TryCreate(Path.Combine(_parent, "never-created-legacy", "src"));

        Assert.NotNull(sibling);

        Assert.Null(sibling.DepthBelow(root));

        Assert.Null(CampaignRootContainment.TryCreate(_parent)!.DepthBelow(root));
    }

    [Fact]
    public void The_case_of_a_root_that_is_not_on_disk_is_folded_only_where_file_systems_usually_fold_it()
    {
        string root = Path.Combine(_parent, "Never-Created");

        CampaignRootContainment? variant = CampaignRootContainment.TryCreate(Path.Combine(_parent, "NEVER-CREATED", "src"));

        Assert.NotNull(variant);

        int? depth = variant.DepthBelow(root);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            Assert.Equal(1, depth);
        }
        else
        {
            Assert.Null(depth);
        }
    }

    [SkippableFact]
    public void A_root_on_disk_is_matched_by_the_directory_it_reaches_not_by_its_spelling()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string target = Directory.CreateDirectory(Path.Combine(canonicalParent, "target", "proj")).FullName;

        string below = Directory.CreateDirectory(Path.Combine(target, "src", "deep")).FullName;

        string link = Path.Combine(_parent, "alias");

        Directory.CreateSymbolicLink(link, target);

        // A root registered as a link reaches the target directory, so a working directory below the target is below the root...
        Assert.Equal(2, CampaignRootContainment.TryCreate(below)!.DepthBelow(link));

        // ...and a working directory that is a different directory is not, however alike the texts are.
        string lookAlike = Directory.CreateDirectory(Path.Combine(canonicalParent, "target", "proj-legacy", "src")).FullName;

        Assert.Null(CampaignRootContainment.TryCreate(lookAlike)!.DepthBelow(link));
    }

    [SkippableFact]
    public void A_path_is_re_expressed_under_the_roots_own_spelling_by_the_directory_it_reaches()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string root = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        _ = Directory.CreateDirectory(Path.Combine(root, "src"));

        File.WriteAllText(Path.Combine(root, "notes.txt"), "inside");

        string physical = PhysicalTestPaths.Resolve(root);

        string alias = Path.Combine(_parent, "alias");

        Directory.CreateSymbolicLink(alias, root);

        Assert.Equal(root, CampaignRootContainment.RebaseOntoRoot(root, physical));

        Assert.Equal(Path.Combine(root, "notes.txt"), CampaignRootContainment.RebaseOntoRoot(root, Path.Combine(physical, "notes.txt")));

        Assert.Equal(Path.Combine(root, "notes.txt"), CampaignRootContainment.RebaseOntoRoot(root, Path.Combine(alias, "notes.txt")));

        // A path that does not exist yet keeps its missing tail below the part that does.
        Assert.Equal(
            Path.Combine(root, "src", "new", "deeper.txt"),
            CampaignRootContainment.RebaseOntoRoot(root, Path.Combine(alias, "src", "new", "deeper.txt")));
    }

    [SkippableFact]
    public void A_path_that_does_not_reach_the_root_is_not_re_expressed_under_it()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string root = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        string sibling = Directory.CreateDirectory(Path.Combine(_parent, "proj-legacy")).FullName;

        string outside = Directory.CreateDirectory(Path.Combine(_parent, "outside")).FullName;

        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");

        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);

        Assert.Null(CampaignRootContainment.RebaseOntoRoot(root, sibling));

        Assert.Null(CampaignRootContainment.RebaseOntoRoot(root, _parent));

        // An in-root link that leads out is followed to where it leads, which is not the root.
        Assert.Null(CampaignRootContainment.RebaseOntoRoot(root, Path.Combine(root, "escape", "secret.txt")));

        // A root that is not on disk reaches no directory, so no path is below it.
        Assert.Null(CampaignRootContainment.RebaseOntoRoot(Path.Combine(_parent, "never-created"), root));
    }

    [Fact]
    public void The_same_text_is_the_same_registration_and_a_name_prefix_sibling_is_not()
    {
        string root = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        _ = Directory.CreateDirectory(Path.Combine(_parent, "proj-legacy"));

        Assert.True(CampaignRootContainment.SameText(root, root));

        Assert.True(CampaignRootContainment.SameText(root, root + Path.DirectorySeparatorChar));

        Assert.False(CampaignRootContainment.SameText(root, Path.Combine(_parent, "proj-legacy")));

        Assert.False(CampaignRootContainment.SameText(root, Path.Combine(_parent, "proj", "src")));
    }
}
