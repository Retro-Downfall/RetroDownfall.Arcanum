using RetroDownfall.Arcanum.Cli.UX;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class RecentResourceStoreTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-recents-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Remember_removes_staging_file_when_destination_replace_fails()
    {

        Directory.CreateDirectory(_root);

        string destination = Path.Combine(_root, "recent-resources.txt");

        Directory.CreateDirectory(destination);

        RecentResourceStore store = new(destination);

        store.Remember("session", Guid.NewGuid().ToString("N"));

        Assert.Empty(Directory.EnumerateFiles(
            _root,
            "recent-resources.txt.tmp.*"));

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

}
