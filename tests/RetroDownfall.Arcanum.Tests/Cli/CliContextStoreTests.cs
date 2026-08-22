using RetroDownfall.Arcanum.Cli.Services;

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
