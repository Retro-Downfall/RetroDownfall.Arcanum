using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class WorkspaceCheckRestoreArtifactSeederTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"arcanum-restore-seeder-{Guid.NewGuid():N}");

    private readonly List<string> _lockedDirectories = [];

    public WorkspaceCheckRestoreArtifactSeederTests() =>
        Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string locked in _lockedDirectories)
            {
                if (Directory.Exists(locked))
                {
                    File.SetUnixFileMode(
                        locked,
                        UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute);
                }
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [SkippableFact]
    public async Task SeedAsync_does_not_descend_into_ignored_directories()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "chmod 000 is the unreadable-directory fixture; Windows ACL denial is not modelled here.");

        string workspace = CreateDirectory("workspace");

        string projectDirectory = CreateDirectory("workspace/src/App");

        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        WriteRestoreArtifacts(CreateDirectory("workspace/src/App/obj"), "App.csproj");

        LockDirectory(CreateDirectory("workspace/node_modules/locked"));

        LockDirectory(CreateDirectory("workspace/.git/locked"));

        LockDirectory(CreateDirectory("workspace/src/App/bin/locked"));

        string artifactsRoot = CreateDirectory("run/artifacts");

        WorkspaceCheckRestoreSeedResult seeded =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                artifactsRoot,
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(seeded.Success, seeded.Message);

        Assert.NotEqual("restore_required", seeded.Code);

        Assert.Equal(1, seeded.ProjectCount);
    }

    [Fact]
    public void Project_enumeration_prunes_ignored_directories_but_keeps_everything_else()
    {
        string workspace = CreateDirectory("workspace");

        string[] kept =
        [
            "workspace/App.csproj",
            "workspace/src/Lib/Lib.fsproj",
            "workspace/src/Other/Other.VBPROJ",
            "workspace/binaries/Tool/Tool.csproj",
        ];

        string[] pruned =
        [
            "workspace/node_modules/pkg/Pkg.csproj",
            "workspace/.git/modules/Sub/Sub.csproj",
            "workspace/src/App/bin/Debug/Gen.csproj",
            "workspace/src/App/obj/Gen2.csproj",
            "workspace/deep/a/b/node_modules/x/X.csproj",
        ];

        foreach (string relative in kept.Concat(pruned))
        {
            string full = Path.Combine(_root, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            File.WriteAllText(full, "<Project />");
        }

        File.WriteAllText(Path.Combine(workspace, "readme.md"), "not a project");

        string[] found = [.. WorkspaceCheckRestoreArtifactSeeder.EnumerateProjectFiles(workspace)];

        Assert.Equal(
            kept.Select(relative => Path.Combine(_root, relative)).Order(StringComparer.Ordinal),
            found.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Revalidation_with_the_seeded_project_list_matches_the_enumerating_revalidation()
    {
        string workspace = CreateDirectory("workspace");

        string project = Path.Combine(workspace, "App.csproj");

        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        WriteRestoreArtifacts(CreateDirectory("workspace/obj"), "App.csproj");

        WorkspaceCheckRestoreSeedResult seeded =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                CreateDirectory("run/artifacts"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(seeded.Success, seeded.Message);

        Assert.Equal(["App.csproj"], seeded.Projects.Select(Path.GetFileName));

        Assert.True(
            WorkspaceCheckRestoreArtifactSeeder.RevalidateManifest(
                workspace,
                seeded.InputManifest!,
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None,
                seeded.Projects));

        Assert.True(
            WorkspaceCheckRestoreArtifactSeeder.RevalidateManifest(
                workspace,
                seeded.InputManifest!,
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None));

        // A project that appears after seeding is invisible to the handed-over list, which is why the
        // pre-start check re-enumerates; the enumerating form must still see it.
        File.WriteAllText(
            Path.Combine(workspace, "Added.csproj"),
            "<Project />");

        Assert.False(
            WorkspaceCheckRestoreArtifactSeeder.RevalidateManifest(
                workspace,
                seeded.InputManifest!,
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None));

        File.Delete(Path.Combine(workspace, "Added.csproj"));

        File.AppendAllText(project, "\n");

        Assert.False(
            WorkspaceCheckRestoreArtifactSeeder.RevalidateManifest(
                workspace,
                seeded.InputManifest!,
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None,
                seeded.Projects));
    }

    private string CreateDirectory(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);

        Directory.CreateDirectory(path);

        return path;
    }

    private void LockDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);

        _lockedDirectories.Add(path);
    }

    private static void WriteRestoreArtifacts(string obj, string projectFileName)
    {
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), "{}");

        File.WriteAllText(
            Path.Combine(obj, projectFileName + ".nuget.g.props"),
            "<Project />");

        File.WriteAllText(
            Path.Combine(obj, projectFileName + ".nuget.g.targets"),
            "<Project />");

        File.WriteAllText(
            Path.Combine(obj, projectFileName + ".nuget.dgspec.json"),
            "{}");

        File.WriteAllText(Path.Combine(obj, "project.nuget.cache"), "{}");
    }
}
