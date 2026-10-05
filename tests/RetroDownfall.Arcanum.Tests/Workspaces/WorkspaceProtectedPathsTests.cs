using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class WorkspaceProtectedPathsTests
{
    [Theory]
    [InlineData(".git")]
    [InlineData(".git/config")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData("./.git/hooks/pre-commit")]
    [InlineData(".git\\hooks\\pre-commit")]
    [InlineData("nested/checkout/.git/config")]
    [InlineData(".arcanum")]
    [InlineData(".arcanum/campaign.json")]
    [InlineData(".arcanum/deep/state.json")]
    public void Git_metadata_and_the_arcanum_marker_directory_are_protected_on_every_platform(
        string relativePath)
    {
        foreach (WorkspacePathAliasPlatform platform in Enum.GetValues<WorkspacePathAliasPlatform>())
        {
            Assert.True(
                WorkspaceProtectedPaths.IsProtectedRelativePath(relativePath, platform),
                $"{relativePath} should be protected on {platform}.");
        }
    }

    [Theory]
    [InlineData("src/app.cs")]
    [InlineData("README.md")]
    [InlineData(".github/workflows/build.yml")]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData("docs/.gitkeep")]
    [InlineData("src/.arcanum/notes.md")]
    [InlineData(".arcanum-project-claim")]
    [InlineData("gitfoo/config")]
    public void Ordinary_paths_and_git_lookalikes_are_not_protected(string relativePath)
    {
        foreach (WorkspacePathAliasPlatform platform in Enum.GetValues<WorkspacePathAliasPlatform>())
        {
            Assert.False(
                WorkspaceProtectedPaths.IsProtectedRelativePath(relativePath, platform),
                $"{relativePath} should not be protected on {platform}.");
        }
    }

    [Theory]
    [InlineData(".GIT/hooks/pre-commit", "MacOS", true)]
    [InlineData(".Git/config", "Windows", true)]
    [InlineData(".ARCANUM/campaign.json", "MacOS", true)]
    [InlineData(".Arcanum/campaign.json", "Windows", true)]
    [InlineData(".GIT/config", "Linux", false)]
    [InlineData(".ARCANUM/campaign.json", "Linux", false)]
    public void Case_folding_follows_the_platform_alias_model(
        string relativePath,
        string platformName,
        bool expected) =>
        Assert.Equal(
            expected,
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                Enum.Parse<WorkspacePathAliasPlatform>(platformName)));

    [Theory]
    [InlineData(".git./config")]
    [InlineData(".git /config")]
    [InlineData(".arcanum./campaign.json")]
    [InlineData("GIT~1/config")]
    public void Windows_trailing_dot_space_and_short_name_aliases_are_protected(string relativePath)
    {
        Assert.True(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.Windows));

        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.MacOS));
    }

    [Theory]
    [InlineData("GIT~2/config")]
    [InlineData("git~9/hooks/pre-commit")]
    [InlineData("nested/checkout/GIT~3/config")]
    [InlineData("ARCANU~1/campaign.json")]
    [InlineData("arcanu~1/campaign.json")]
    [InlineData("ARCANU~2/state/x.json")]
    [InlineData("ARCANU~1./campaign.json")]
    public void Windows_numbered_short_names_of_protected_directories_are_protected(string relativePath)
    {
        Assert.True(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.Windows));

        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.MacOS));

        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.Linux));
    }

    [Theory]
    [InlineData("src/ARCANU~1/notes.md")]
    [InlineData("GIT~/config")]
    [InlineData("GIT~x/config")]
    [InlineData("GIT~1x/config")]
    [InlineData("ARCANU~/campaign.json")]
    [InlineData("ARCANUM~1/campaign.json")]
    public void Windows_short_name_lookalikes_and_non_leading_arcanum_short_names_are_not_protected(
        string relativePath) =>
        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.Windows));

    [Theory]
    [InlineData(".git::$INDEX_ALLOCATION/hooks/pre-commit")]
    [InlineData(".git::$DATA")]
    [InlineData(".git:hidden-stream")]
    [InlineData(".GIT::$INDEX_ALLOCATION/config")]
    [InlineData("GIT~1::$INDEX_ALLOCATION/config")]
    [InlineData("nested/checkout/.git::$INDEX_ALLOCATION/config")]
    [InlineData(".arcanum::$INDEX_ALLOCATION/campaign.json")]
    [InlineData("ARCANU~1::$INDEX_ALLOCATION/campaign.json")]
    public void Windows_alternate_data_stream_suffixes_do_not_hide_a_protected_name(string relativePath)
    {
        Assert.True(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.Windows));

        // Off Windows a colon is an ordinary filename character, so these are different names.
        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.MacOS));

        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.Linux));
    }

    [Theory]
    [InlineData(".g‌it/hooks/pre-commit")]
    [InlineData(".﻿git/config")]
    [InlineData(".git‍/config")]
    [InlineData(".GIT‮/config")]
    [InlineData(".arc⁯anum/campaign.json")]
    [InlineData("nested/checkout/.g‌it/config")]
    public void Macos_ignorable_code_points_do_not_hide_a_protected_name(string relativePath)
    {
        // HFS+ ignores these code points when it compares names, so the spelling names the same
        // directory there. Other platforms keep them as distinct characters.
        Assert.True(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.MacOS));

        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.Linux));

        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                relativePath,
                WorkspacePathAliasPlatform.Windows));
    }

    [Fact]
    public void Non_ascii_segments_neither_throw_nor_over_match()
    {
        Assert.False(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                "café/.gitx",
                WorkspacePathAliasPlatform.MacOS));
    }

    [Fact]
    public void Absolute_paths_are_judged_relative_to_the_workspace_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-protected-root");

        Assert.True(
            WorkspaceProtectedPaths.IsProtectedAbsolutePath(
                root,
                Path.Combine(root, ".git", "hooks", "pre-commit")));

        Assert.False(
            WorkspaceProtectedPaths.IsProtectedAbsolutePath(
                root,
                Path.Combine(root, "src", "app.cs")));

        // A workspace that itself lives under a directory named .git is not self-protecting; only
        // the workspace-relative segments count.
        string insideDotGit = Path.Combine(Path.GetTempPath(), ".git", "workspace");

        Assert.False(
            WorkspaceProtectedPaths.IsProtectedAbsolutePath(
                insideDotGit,
                Path.Combine(insideDotGit, "src", "app.cs")));
    }

    [SkippableFact]
    public void Windows_lane_trailing_dot_git_alias_is_protected_on_the_host_platform()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Trailing-dot aliasing is a Windows filesystem behaviour; the platform-seam theory above pins the logic on every host.");

        Assert.True(
            WorkspaceProtectedPaths.IsProtectedRelativePath(
                ".git./hooks/pre-commit",
                WorkspaceRelativePath.CurrentPlatform));
    }
}
