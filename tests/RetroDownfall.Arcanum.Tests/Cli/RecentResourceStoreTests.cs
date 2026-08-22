using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class RecentResourceStoreTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-recents-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RememberAsync_removes_staging_file_when_destination_replace_fails()
    {

        Directory.CreateDirectory(_root);

        string destination = Path.Combine(_root, "recent-resources.txt");

        Directory.CreateDirectory(destination);

        RecentResourceStore store = new(
            destination,
            new RecordingArcanumClientMutationBoundary());

        await store.RememberAsync(
            "session",
            Guid.NewGuid().ToString("N"),
            AllowAsync);

        Assert.Empty(Directory.EnumerateFiles(
            _root,
            "recent-resources.txt.tmp.*"));

    }

    [Theory]
    [InlineData((byte)ArcanumClientMutationDisposition.Blocked)]
    [InlineData((byte)ArcanumClientMutationDisposition.Unsafe)]
    public async Task RememberAsync_refusal_is_nonfatal_and_does_not_write(
        byte dispositionValue)
    {

        Directory.CreateDirectory(_root);

        string destination = Path.Combine(_root, "recent-resources.txt");

        RecordingArcanumClientMutationBoundary boundary = new(
            (ArcanumClientMutationDisposition)dispositionValue);

        RecentResourceStore store = new(destination, boundary);

        await store.RememberAsync(
            "session",
            "session-alpha",
            AllowAsync);

        Assert.False(File.Exists(destination));

        Assert.Empty(store.GetRecentIds("session"));

        Assert.Equal(1, boundary.Calls);

    }

    [Fact]
    public async Task RememberAsync_completed_persistence_records_the_selection()
    {

        Directory.CreateDirectory(_root);

        string destination = Path.Combine(_root, "recent-resources.txt");

        RecordingArcanumClientMutationBoundary boundary = new();

        RecentResourceStore store = new(destination, boundary);

        await store.RememberAsync(
            "session",
            "session-alpha",
            AllowAsync);

        Assert.Equal(["session-alpha"], store.GetRecentIds("session"));

        Assert.Equal(1, boundary.Calls);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    private static Task<Result<bool>> AllowAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Result<bool>.Success(true));

    }

}
