using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupPathRemapperTests
{

    [Fact]
    public void Unix_to_unix_mapping_rewrites_the_root_and_preserves_the_remainder()
    {

        BackupPathRemapper remapper = Valid(
            new BackupPathMapping(
                BackupPathMappingKind.CampaignRoot,
                "/home/old/campaigns",
                "/Users/new/campaigns"));

        Assert.True(remapper.TryRemap(
            BackupPathMappingKind.CampaignRoot,
            "/home/old/campaigns/alpha/notes",
            out string? remapped));

        Assert.Equal("/Users/new/campaigns/alpha/notes", remapped);

        Assert.True(remapper.TryRemap(
            BackupPathMappingKind.CampaignRoot,
            "/home/old/campaigns",
            out string? exact));

        Assert.Equal("/Users/new/campaigns", exact);

    }

    [Fact]
    public void Windows_source_maps_onto_a_unix_destination_with_converted_separators()
    {

        BackupPathRemapper remapper = Valid(
            new BackupPathMapping(
                BackupPathMappingKind.WorkspaceRoot,
                @"C:\Users\Old\src",
                "/Users/new/src"));

        Assert.True(remapper.TryRemap(
            BackupPathMappingKind.WorkspaceRoot,
            @"C:\Users\Old\src\project\Program.cs",
            out string? remapped));

        Assert.Equal("/Users/new/src/project/Program.cs", remapped);

    }

    [Fact]
    public void Unix_source_maps_onto_a_windows_destination_with_converted_separators()
    {

        BackupPathRemapper remapper = Valid(
            new BackupPathMapping(
                BackupPathMappingKind.WorkspaceRoot,
                "/home/old/src",
                @"D:\work\src"));

        Assert.True(remapper.TryRemap(
            BackupPathMappingKind.WorkspaceRoot,
            "/home/old/src/project/Program.cs",
            out string? remapped));

        Assert.Equal(@"D:\work\src\project\Program.cs", remapped);

    }

    [Fact]
    public void A_windows_source_matches_case_insensitively_but_a_unix_source_does_not()
    {

        BackupPathRemapper windows = Valid(
            new BackupPathMapping(
                BackupPathMappingKind.WorkspaceRoot,
                @"C:\Users\Old\src",
                "/new"));

        Assert.True(windows.TryRemap(
            BackupPathMappingKind.WorkspaceRoot,
            @"c:\users\old\SRC\a.txt",
            out string? remapped));

        Assert.Equal("/new/a.txt", remapped);

        BackupPathRemapper unix = Valid(
            new BackupPathMapping(
                BackupPathMappingKind.WorkspaceRoot,
                "/home/old/src",
                "/new"));

        Assert.False(unix.TryRemap(
            BackupPathMappingKind.WorkspaceRoot,
            "/home/old/SRC/a.txt",
            out _));

    }

    [Fact]
    public void A_value_outside_every_mapping_is_left_alone()
    {

        BackupPathRemapper remapper = Valid(
            new BackupPathMapping(
                BackupPathMappingKind.CampaignRoot,
                "/home/old",
                "/home/new"));

        Assert.False(remapper.TryRemap(
            BackupPathMappingKind.CampaignRoot,
            "/var/data/other",
            out _));

        Assert.False(remapper.TryRemap(
            BackupPathMappingKind.WorkspaceRoot,
            "/home/old/inside",
            out _));

    }

    [Fact]
    public void A_sibling_whose_name_merely_starts_with_the_root_is_not_remapped()
    {

        BackupPathRemapper remapper = Valid(
            new BackupPathMapping(
                BackupPathMappingKind.CampaignRoot,
                "/home/old",
                "/home/new"));

        Assert.False(remapper.TryRemap(
            BackupPathMappingKind.CampaignRoot,
            "/home/older/thing",
            out _));

    }

    [Theory]
    [InlineData("relative/path", "/absolute")]
    [InlineData("/absolute", "relative/path")]
    [InlineData("", "/absolute")]
    [InlineData("   ", "/absolute")]
    public void Mappings_must_name_absolute_roots_on_both_sides(string from, string to)
    {

        Assert.Contains(
            Issues(new BackupPathMapping(BackupPathMappingKind.CampaignRoot, from, to)),
            static issue => issue.Code == "backup.restore_mapping_not_absolute");

    }

    [Theory]
    [InlineData("/home/../etc", "/new")]
    [InlineData("/home/old", "/new/../etc")]
    public void Mappings_containing_traversal_segments_are_rejected(string from, string to)
    {

        Assert.Contains(
            Issues(new BackupPathMapping(BackupPathMappingKind.CampaignRoot, from, to)),
            static issue => issue.Code == "backup.restore_mapping_escapes");

    }

    [Fact]
    public void Overlapping_source_roots_of_one_kind_are_ambiguous()
    {

        Assert.Contains(
            Issues(
                new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/old", "/a"),
                new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/old/inner", "/b")),
            static issue => issue.Code == "backup.restore_mapping_ambiguous");

    }

    [Fact]
    public void Duplicate_source_roots_that_differ_only_by_case_are_ambiguous()
    {

        Assert.Contains(
            Issues(
                new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/Old", "/a"),
                new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/old", "/b")),
            static issue => issue.Code == "backup.restore_mapping_case_collision");

    }

    [Fact]
    public void Two_source_roots_landing_on_one_destination_collide()
    {

        Assert.Contains(
            Issues(
                new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/a", "/shared"),
                new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/b", "/shared")),
            static issue => issue.Code == "backup.restore_mapping_target_collision");

    }

    [Fact]
    public void Different_kinds_may_share_overlapping_roots()
    {

        BackupPathRemapper remapper = Valid(
            new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/old", "/a"),
            new BackupPathMapping(BackupPathMappingKind.WorkspaceRoot, "/home/old", "/b"));

        Assert.True(remapper.TryRemap(
            BackupPathMappingKind.CampaignRoot,
            "/home/old/x",
            out string? campaign));

        Assert.Equal("/a/x", campaign);

        Assert.True(remapper.TryRemap(
            BackupPathMappingKind.WorkspaceRoot,
            "/home/old/x",
            out string? workspace));

        Assert.Equal("/b/x", workspace);

    }

    [Theory]
    [InlineData(@"C:\bad|name")]
    [InlineData(@"C:\bad?name")]
    [InlineData(@"C:\trailing.")]
    [InlineData(@"C:\space \sub")]
    [InlineData(@"C:\CON")]
    [InlineData(@"C:\dir\LPT1")]
    public void Destinations_that_are_invalid_on_the_windows_target_are_rejected(string to)
    {

        Assert.Contains(
            Issues(new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/old", to)),
            static issue => issue.Code == "backup.restore_mapping_invalid_target_name");

    }

    [Fact]
    public void A_destination_beneath_its_own_source_is_a_containment_escape()
    {

        Assert.Contains(
            Issues(new BackupPathMapping(BackupPathMappingKind.CampaignRoot, "/home/old", "/home/old/inner")),
            static issue => issue.Code == "backup.restore_mapping_escapes");

    }

    [Fact]
    public void An_empty_mapping_set_validates_and_remaps_nothing()
    {

        BackupPathRemapper remapper = Valid();

        Assert.Empty(remapper.Mappings);

        Assert.False(remapper.TryRemap(
            BackupPathMappingKind.CampaignRoot,
            "/anything",
            out _));

    }

    private static BackupPathRemapper Valid(params BackupPathMapping[] mappings)
    {

        BackupPathRemapValidation validation = BackupPathRemapper.Create(mappings);

        Assert.Empty(validation.Issues);

        return Assert.IsType<BackupPathRemapper>(validation.Remapper);

    }

    private static BackupVerifyIssue[] Issues(params BackupPathMapping[] mappings)
    {

        BackupPathRemapValidation validation = BackupPathRemapper.Create(mappings);

        Assert.Null(validation.Remapper);

        Assert.NotEmpty(validation.Issues);

        return validation.Issues;

    }

}
