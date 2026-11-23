using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class WorkspaceCheckPortableInputsTests : IAsyncLifetime
{
    private readonly TempWorkspace fixture = new();

    public Task InitializeAsync() => fixture.InitializeAsync();

    public Task DisposeAsync() => fixture.DisposeAsync();

    [SkippableFact]
    public async Task Workspace_and_package_copies_share_the_workspace_staging_allowance()
    {
        Skip.IfNot(OperatingSystem.IsMacOS());

        string source = fixture.CreateSubdir("source");

        string packages = fixture.CreateSubdir("feed");

        await File.WriteAllTextAsync(Path.Combine(source, "source.cs"), "1234");

        await File.WriteAllTextAsync(Path.Combine(packages, "approved.nupkg"), "56789");

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(source, out var sourceIdentity));

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(packages, out var feedIdentity));

        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var stage = await WorkspaceCheckPortableInputs.CreateAsync(source, sourceIdentity.Identity, packages,
                feedIdentity.Identity, 8, CancellationToken.None);
        });

        Assert.Contains("staging allowance", error.Message);
    }

    [SkippableFact]
    public async Task Snapshot_contains_source_and_only_approved_packages_and_is_independent_of_later_workspace_edits()
    {
        Skip.IfNot(OperatingSystem.IsMacOS());

        string workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "workspace")).FullName;

        string packages = Directory.CreateDirectory(Path.Combine(fixture.Root, "packages")).FullName;

        string input = Path.Combine(workspace, "Book.cs");

        await File.WriteAllTextAsync(input, "original");

        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(input, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await File.WriteAllTextAsync(Path.Combine(packages, "approved.1.0.nupkg"), "package");

        await File.WriteAllTextAsync(Path.Combine(packages, "notes.txt"), "operator note");

        foreach (string name in new[] { ".git", ".arcanum", "bin", "obj" })
        {
            string directory = Directory.CreateDirectory(Path.Combine(workspace, name)).FullName;

            await File.WriteAllTextAsync(Path.Combine(directory, "excluded.txt"), "excluded");
        }

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(workspace, out var root));

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(packages, out var feed));

        await Assert.ThrowsAsync<IOException>(() => WorkspaceCheckPortableInputs.CreateAsync(workspace, root.Identity,
            packages, root.Identity, 4096, CancellationToken.None));

        using var snapshot = await WorkspaceCheckPortableInputs.CreateAsync(workspace, root.Identity, packages, feed.Identity,
            4096, CancellationToken.None);

        await File.WriteAllTextAsync(input, "changed");

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(snapshot.Source, "Book.cs")));

        if (OperatingSystem.IsMacOS())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(snapshot.Source, "Book.cs")).HasFlag(UnixFileMode.UserExecute));

            Assert.False(File.GetUnixFileMode(snapshot.Root).HasFlag(UnixFileMode.OtherExecute));
        }

        Assert.Single(Directory.GetFileSystemEntries(snapshot.Source));

        Assert.Equal("approved.1.0.nupkg", Path.GetFileName(Assert.Single(Directory.GetFiles(snapshot.Packages))));
    }
}
