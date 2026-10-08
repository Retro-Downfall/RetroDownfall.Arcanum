using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliContextStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(
            Path.GetTempPath(),
            "arcanum-cli-context-tests",
            Guid.NewGuid().ToString("N"));

    private string ContextPath =>
        Path.Combine(_directory, "cli-context.json");

    [Fact]
    public void Save_and_load_round_trip_versioned_non_secret_context()
    {
        CliContextStore store = new(ContextPath);

        CliContextDocument expected = new(
            Version: CliContextDocument.CurrentVersion,
            CampaignId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CampaignName: "alpha",
            WorkspaceId: "workspace-alpha",
            WorkspacePath: "/work/alpha",
            Model: "gpt-test",
            SessionId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

        ((ICliContextExclusiveWriter)store).SaveUnderExclusive(expected);

        CliContextDocument actual = store.Load();

        Assert.Equal(expected, actual);

        string json = File.ReadAllText(ContextPath);

        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);

        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_replaces_the_document_atomically_and_owner_only()
    {
        CliContextStore store = new(ContextPath);

        ((ICliContextExclusiveWriter)store).SaveUnderExclusive(
            CliContextDocument.Empty with { Model = "first" });

        ((ICliContextExclusiveWriter)store).SaveUnderExclusive(
            CliContextDocument.Empty with { Model = "second" });

        Assert.Equal("second", store.Load().Model);

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp.*"));

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(ContextPath);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                mode);
        }
    }

    /// <summary>
    /// A temp file created with the default mode and narrowed afterwards is readable by other local
    /// users for the whole write. Both stores stage through the shared helper that creates the file
    /// owner-only before any byte is written; a plain <c>FileMode.CreateNew</c> open is what the old
    /// code did, and what this pins out. The temp file is gone before a test could observe it, so the
    /// creation path is asserted in source, as <see cref="DirectCliWriterBoundaryTests"/> does for the
    /// writers.
    /// </summary>
    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Services/CliContextStore.cs")]
    [InlineData("src/RetroDownfall.Arcanum.Cli/UX/RecentResourceStore.cs")]
    public void Temp_file_is_owner_only_at_creation(string relativePath)
    {
        ProductionSource source = ProductionSourceInventory.Sources().Single(
            candidate => candidate.IsExactOwner(relativePath));

        Assert.Contains(
            "SecureFilePermissions.CreateOwnerOnlyTempFile(",
            source.Text,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "FileMode.CreateNew",
            source.Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Load</c> answers "empty" for a file it cannot use, which is right for a read but wrong as
    /// the base of a write: the next mutation would replace a newer Arcanum's context with an older
    /// one's, silently. A mutation over such a file refuses and leaves it byte for byte as it was.
    /// </summary>
    [Fact]
    public void Mutation_refuses_to_overwrite_a_newer_version_file()
    {
        Directory.CreateDirectory(_directory);

        const string Newer = """
            {
              "version": 2,
              "model": "written-by-a-newer-arcanum",
              "tags": ["a field this build does not know"]
            }
            """;

        File.WriteAllText(ContextPath, Newer);

        CliContextStore store = new(ContextPath);

        IOException refusal = Assert.ThrowsAny<IOException>(
            () => ((ICliContextExclusiveWriter)store).SaveUnderExclusive(
                CliContextDocument.Empty with { Model = "older-build" }));

        Assert.Contains("version 2", refusal.Message, StringComparison.Ordinal);

        Assert.Contains(ContextPath, refusal.Message, StringComparison.Ordinal);

        Assert.Equal(Newer, File.ReadAllText(ContextPath));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp.*"));
    }

    [Fact]
    public void Mutation_refuses_to_overwrite_an_unreadable_file()
    {
        Directory.CreateDirectory(_directory);

        const string Damaged = "{ \"version\": 1, \"model\": ";

        File.WriteAllText(ContextPath, Damaged);

        CliContextStore store = new(ContextPath);

        IOException refusal = Assert.ThrowsAny<IOException>(
            () => ((ICliContextExclusiveWriter)store).SaveUnderExclusive(
                CliContextDocument.Empty with { Model = "replacement" }));

        Assert.Contains("could not be read", refusal.Message, StringComparison.Ordinal);

        Assert.Equal(Damaged, File.ReadAllText(ContextPath));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp.*"));
    }

    [Fact]
    public void Mutation_still_creates_the_file_when_none_exists_and_replaces_a_current_one()
    {
        CliContextStore store = new(ContextPath);

        ((ICliContextExclusiveWriter)store).SaveUnderExclusive(
            CliContextDocument.Empty with { Model = "created" });

        Assert.Equal("created", store.Load().Model);

        ((ICliContextExclusiveWriter)store).SaveUnderExclusive(
            CliContextDocument.Empty with { Model = "replaced" });

        Assert.Equal("replaced", store.Load().Model);
    }

    [Fact]
    public void Load_fails_closed_for_unknown_versions()
    {
        Directory.CreateDirectory(_directory);

        File.WriteAllText(
            ContextPath,
            """
            {
              "version": 999,
              "model": "must-not-apply"
            }
            """);

        CliContextStore store = new(ContextPath);

        CliContextDocument actual = store.Load();

        Assert.Equal(CliContextDocument.Empty, actual);
    }

    [Fact]
    public void Load_does_not_apply_a_workspace_path_without_its_server_id()
    {
        Directory.CreateDirectory(_directory);

        File.WriteAllText(
            ContextPath,
            """
            {
              "version": 1,
              "workspacePath": "/untrusted/orphan"
            }
            """);

        CliContextDocument actual = new CliContextStore(ContextPath).Load();

        Assert.Null(actual.WorkspaceId);

        Assert.Null(actual.WorkspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
