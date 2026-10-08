using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Infrastructure.Security;

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

    /// <summary>
    /// <c>CreateOwnerOnlyTempFile</c> narrows the file at creation on every platform, and the
    /// recent-resources staging file repeats the narrowing explicitly, before the first byte and not
    /// after the move, as belt and braces for a file the create reused. The action stands in for the
    /// platform narrowing so the order can be pinned on every host.
    /// </summary>
    [Fact]
    public void CreateStagingFile_applies_owner_only_before_any_byte_is_written()
    {
        Directory.CreateDirectory(_root);

        string staging = Path.Combine(_root, "recent-resources.txt.tmp.probe");

        List<string> applied = [];

        long lengthWhenApplied = -1;

        using (FileStream stream = RecentResourceStore.CreateStagingFile(
            staging,
            path =>
            {
                applied.Add(path);

                lengthWhenApplied = new FileInfo(path).Length;
            }))
        {
            Assert.Equal([staging], applied);

            Assert.Equal(0, lengthWhenApplied);

            Assert.Equal(0, stream.Length);
        }
    }

    [Fact]
    public async Task RememberAsync_stages_through_the_hardened_staging_file()
    {
        ProductionSource source = ProductionSourceInventory.Sources().Single(
            static candidate => candidate.IsExactOwner("src/RetroDownfall.Arcanum.Cli/UX/RecentResourceStore.cs"));

        Assert.Equal(1, source.Occurrences("CreateStagingFile(temp)"));

        Assert.Equal(1, source.Occurrences("SecureFilePermissions.CreateOwnerOnlyTempFile("));

        Assert.Equal(1, source.Occurrences("applyOwnerOnly(path)"));

        Directory.CreateDirectory(_root);

        string destination = Path.Combine(_root, "recent-resources.txt");

        RecentResourceStore store = new(destination, new RecordingArcanumClientMutationBoundary());

        await store.RememberAsync("session", "session-alpha", AllowAsync);

        Assert.Equal(["session-alpha"], store.GetRecentIds("session"));
    }

    /// <summary>
    /// The Windows lane of the same rule: with the real narrowing, the staging file's ACL already
    /// grants only the current user while the file is still empty. Not run on a non-Windows host.
    /// </summary>
    [SkippableFact]
    public void CreateStagingFile_leaves_an_empty_staging_file_owner_only_on_windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The ACL is what this asserts against.");

        Directory.CreateDirectory(_root);

        string staging = Path.Combine(_root, "recent-resources.txt.tmp.windows");

        using (FileStream stream = RecentResourceStore.CreateStagingFile(staging))
        {
            Assert.Equal(0, stream.Length);

            Assert.True(
                SecureFilePermissions.HasOwnerOnlyPosture(staging, isDirectory: false),
                "The staging file was readable beyond its owner before the first byte was written.");
        }
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
