using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class OwnedTemporaryDirectoryTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-owned-temp-" + Guid.NewGuid().ToString("N"));

    public OwnedTemporaryDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public void Delete_succeeds_only_while_path_still_names_the_owned_directory()
    {

        string path = Path.Combine(_root, "stage");

        OwnedTemporaryDirectory owned = OwnedTemporaryDirectory.Create(path);

        File.WriteAllText(Path.Combine(path, "payload"), "owned");

        Assert.True(owned.TryDelete());

        Assert.False(Directory.Exists(path));

        OwnedTemporaryDirectory replaced = OwnedTemporaryDirectory.Create(path);

        string moved = Path.Combine(_root, "moved-owned");

        Directory.Move(path, moved);

        Directory.CreateDirectory(path);

        string sentinel = Path.Combine(path, "do-not-delete");

        File.WriteAllText(sentinel, "replacement");

        Assert.False(replaced.TryDelete());

        Assert.True(File.Exists(sentinel));

    }

    [Fact]
    public void Create_secures_the_temporary_child_without_restricting_its_parent_on_Unix()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string parent = Path.Combine(_root, "shared-parent");

        Directory.CreateDirectory(parent);

        UnixFileMode parentMode =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute;

        File.SetUnixFileMode(parent, parentMode);

        OwnedTemporaryDirectory owned = OwnedTemporaryDirectory.Create(
            Path.Combine(parent, "stage"));

        Assert.Equal(parentMode, File.GetUnixFileMode(parent));

        Assert.Equal(
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute,
            File.GetUnixFileMode(owned.Path));

        Assert.True(owned.TryDelete());

    }

}
