using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// Pins the one rule the full walk, the incremental drain and the watcher intake share, so a path the
/// walk would never send to the embedding provider cannot reach it through a watcher event.
/// </summary>
public sealed class WorkspaceIndexEligibilityTests
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".json", ".cs", ".md" };

    [Theory]
    [InlineData(".env.json")]
    [InlineData(".secrets/token.json")]
    [InlineData("src/.cache/nested/data.json")]
    [InlineData("src/nested/.data.json")]
    [InlineData("a/b/c/.hidden/d/e.json")]
    public void Every_relative_segment_with_a_leading_dot_makes_a_path_ineligible(string path)
    {
        string relativePath = OsPath(path);

        Assert.False(WorkspaceIndexEligibility.HasEligibleSegments(relativePath));

        Assert.False(WorkspaceIndexEligibility.IsEligible(relativePath, default(FileAttributes), Extensions));

        Assert.False(WorkspaceIndexEligibility.IsEligible(relativePath, static _ => FileAttributes.Normal, Extensions));
    }

    [Theory]
    [InlineData("node_modules/pkg/index.js")]
    [InlineData("src/obj/Debug/Foo.cs")]
    [InlineData("Bin/Release/App.json")]
    [InlineData("build/out.json")]
    [InlineData("a/packages/b.md")]
    [InlineData("dist/readme.md")]
    public void An_ignored_directory_segment_makes_a_path_ineligible_without_regard_to_case(string path)
    {
        string relativePath = OsPath(path);

        Assert.False(WorkspaceIndexEligibility.HasEligibleSegments(relativePath));

        Assert.False(WorkspaceIndexEligibility.IsEligible(relativePath, default(FileAttributes), Extensions));
    }

    [Theory]
    [InlineData("src/Foo.cs")]
    [InlineData("README.md")]
    [InlineData("docs/guide/intro.md")]
    [InlineData("src/dotted.name.json")]
    [InlineData("src/binary/objects.cs")]
    public void A_visible_path_with_a_configured_extension_is_eligible(string path)
    {
        string relativePath = OsPath(path);

        Assert.True(WorkspaceIndexEligibility.HasEligibleSegments(relativePath));

        Assert.True(WorkspaceIndexEligibility.IsEligible(relativePath, default(FileAttributes), Extensions));

        Assert.True(WorkspaceIndexEligibility.IsEligible(relativePath, static _ => FileAttributes.Normal, Extensions));
    }

    [Fact]
    public void The_workspace_root_passes_the_lexical_rule_and_paths_outside_it_do_not()
    {
        Assert.True(WorkspaceIndexEligibility.HasEligibleSegments("."));

        Assert.False(WorkspaceIndexEligibility.HasEligibleSegments(".."));

        Assert.False(WorkspaceIndexEligibility.HasEligibleSegments(OsPath("../outside/file.cs")));

        Assert.False(WorkspaceIndexEligibility.HasEligibleSegments(OsPath("./.hidden/file.cs")));
    }

    [Fact]
    public void The_extension_rule_applies_only_when_extensions_are_supplied()
    {
        string relativePath = OsPath("assets/logo.png");

        Assert.False(WorkspaceIndexEligibility.IsEligible(relativePath, default(FileAttributes), Extensions));

        Assert.True(WorkspaceIndexEligibility.IsEligible(relativePath, default(FileAttributes), extensions: null));
    }

    [Theory]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory)]
    public void A_hidden_or_system_entry_is_ineligible_even_without_a_leading_dot(FileAttributes attributes)
    {
        Assert.False(WorkspaceIndexEligibility.IsEligible(OsPath("src/visible.cs"), attributes, Extensions));
    }

    /// <summary>
    /// Windows hides entries by attribute rather than by name, and on Unix <c>File.GetAttributes</c>
    /// reports only the leaf's own bit, so the incremental path reads every ancestor's attributes
    /// through this seam; a hidden ancestor named without a leading dot must still reject its subtree.
    /// </summary>
    [Fact]
    public void A_hidden_ancestor_directory_without_a_leading_dot_makes_its_whole_subtree_ineligible()
    {
        List<string> consulted = [];

        FileAttributes Attributes(string prefix)
        {
            consulted.Add(prefix);

            return string.Equals(prefix, OsPath("src/hidden-dir"), StringComparison.Ordinal)
                ? FileAttributes.Directory | FileAttributes.Hidden
                : FileAttributes.Normal;
        }

        Assert.False(WorkspaceIndexEligibility.IsEligible(OsPath("src/hidden-dir/inner/file.cs"), Attributes, Extensions));

        Assert.Equal(["src", OsPath("src/hidden-dir")], consulted);
    }

    [Fact]
    public void A_hidden_leaf_without_a_leading_dot_is_ineligible_through_the_attribute_seam()
    {
        FileAttributes Attributes(string prefix) =>
            string.Equals(prefix, OsPath("src/visible.cs"), StringComparison.Ordinal)
                ? FileAttributes.Hidden
                : FileAttributes.Normal;

        Assert.False(WorkspaceIndexEligibility.IsEligible(OsPath("src/visible.cs"), Attributes, Extensions));
    }

    [Fact]
    public void A_path_rejected_lexically_never_reads_a_filesystem_attribute()
    {
        static FileAttributes Throwing(string prefix) =>
            throw new InvalidOperationException($"Attributes of '{prefix}' must not be read for a lexically ineligible path.");

        Assert.False(WorkspaceIndexEligibility.IsEligible(OsPath(".secrets/token.json"), Throwing, Extensions));

        Assert.False(WorkspaceIndexEligibility.IsEligible(OsPath("node_modules/pkg/index.json"), Throwing, Extensions));

        Assert.False(WorkspaceIndexEligibility.IsEligible(OsPath("assets/logo.png"), Throwing, Extensions));
    }

    [Fact]
    public void Every_ancestor_and_the_leaf_are_consulted_for_an_eligible_path()
    {
        List<string> consulted = [];

        FileAttributes Attributes(string prefix)
        {
            consulted.Add(prefix);

            return FileAttributes.Normal;
        }

        Assert.True(WorkspaceIndexEligibility.IsEligible(OsPath("src/inner/file.cs"), Attributes, Extensions));

        Assert.Equal(["src", OsPath("src/inner"), OsPath("src/inner/file.cs")], consulted);
    }

    private static string OsPath(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}
