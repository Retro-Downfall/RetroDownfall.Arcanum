using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class CodexReaderTests : IAsyncLifetime
{
    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile("CODEX.md", "Local codex body.");
    }

    public async Task DisposeAsync()
    {
        await _workspace.DisposeAsync();
    }

    [Fact]
    public async Task ReadCodexFileAsync_returns_content_under_size_limit()
    {
        string path = _workspace.WriteFile("docs/CODEX.md", "Standalone codex.");

        string? content = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        Assert.Equal("Standalone codex.", content);
    }

    [Fact]
    public async Task ReadCodexFileAsync_returns_too_large_when_file_exceeds_limit()
    {
        string path = _workspace.WriteFile("huge/CODEX.md", new string('x', 5000));

        (SecureFileReadStatus Status, string? Text) result = await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 1024, CancellationToken.None);

        Assert.Equal(SecureFileReadStatus.TooLarge, result.Status);

        Assert.Null(result.Text);
    }

    [Fact]
    public async Task ReadCodexAsync_returns_local_workspace_codex()
    {
        string? content = await CodexReader.ReadCodexAsync(_workspace.Root, maxSizeBytes: 4096, CancellationToken.None);

        Assert.Equal("Local codex body.", content);
    }

    /// <summary>
    /// A git-tracked symlink named CODEX.md inside a cloned campaign made the API read path hand back the
    /// link target's bytes verbatim: FileInfo.Length stats through the link and File.ReadAllTextAsync
    /// follows it. The read must fail closed on symlinks the way every other workspace read does.
    /// </summary>
    [SkippableFact]
    public async Task ReadCodexFileAsync_rejects_a_symlinked_codex()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string outsideDir = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outsideDir);

        try
        {
            string secretPath = Path.Combine(outsideDir, "id_ed25519");

            await File.WriteAllTextAsync(secretPath, "PRIVATE KEY MATERIAL");

            string linkPath = Path.Combine(_workspace.CreateSubdir("cloned"), "CODEX.md");

            File.CreateSymbolicLink(linkPath, secretPath);

            (SecureFileReadStatus Status, string? Text) result = await CodexReader.ReadCodexFileAsync(linkPath, maxSizeBytes: 4096, CancellationToken.None);

            Assert.Equal(SecureFileReadStatus.Rejected, result.Status);

            Assert.Null(result.Text);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    /// <summary>
    /// FileInfo.Length reports 0 for a FIFO, so the size gate passed and the blocking open(2) inside
    /// File.ReadAllTextAsync never returned until a writer appeared — the cancellation token cannot
    /// interrupt an open, so the request hung past RequestAborted and leaked a thread-pool thread.
    /// </summary>
    [SkippableFact]
    public async Task ReadCodexFileAsync_rejects_a_fifo_instead_of_blocking_forever()
    {
        Skip.If(OperatingSystem.IsWindows(), "mkfifo is a POSIX primitive.");

        string fifoPath = Path.Combine(_workspace.CreateSubdir("fifo"), "CODEX.md");

        using (System.Diagnostics.Process? mkfifo = System.Diagnostics.Process.Start("mkfifo", fifoPath))
        {
            Skip.If(mkfifo is null, "mkfifo is unavailable on this host.");

            await mkfifo!.WaitForExitAsync();

            Skip.If(mkfifo.ExitCode != 0, "mkfifo failed on this host.");
        }

        Assert.True(File.Exists(fifoPath));

        // Offloaded so a regression blocks a pool thread rather than wedging the whole test run: the
        // blocking open(2) happens before the method ever yields, so a direct call never returns a Task.
        Task<(SecureFileReadStatus Status, string? Text)> read = Task.Run(
            () => CodexReader.ReadCodexFileAsync(fifoPath, maxSizeBytes: 4096, CancellationToken.None));

        Task completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(read, completed);

        Assert.Equal(SecureFileReadStatus.Rejected, (await read).Status);
    }

    [Fact]
    public async Task Cached_content_is_not_returned_when_limit_is_lowered()
    {
        string path = _workspace.WriteFile("lowered/CODEX.md", new string('x', 2000));

        string? generous = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        string? lowered = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 1024, CancellationToken.None)).Text;

        string? generousAgain = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        Assert.Equal(new string('x', 2000), generous);

        Assert.Null(lowered);

        Assert.Equal(new string('x', 2000), generousAgain);
    }

    [Fact]
    public async Task Cached_content_is_not_returned_when_the_file_changes_without_a_new_mtime()
    {
        string path = _workspace.WriteFile("same-mtime/CODEX.md", "first body");

        DateTime pinned = File.GetLastWriteTimeUtc(path);

        string? first = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        // A different length with the original mtime restored defeats an mtime-only validation.
        File.WriteAllText(path, "a considerably longer replacement body");

        File.SetLastWriteTimeUtc(path, pinned);

        string? second = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        Assert.Equal("first body", first);

        Assert.Equal("a considerably longer replacement body", second);
    }

    [Fact]
    public async Task Cached_content_is_not_returned_when_a_new_file_with_the_same_length_and_mtime_replaces_it()
    {
        string path = _workspace.WriteFile("same-length/CODEX.md", "first body");

        DateTime pinned = File.GetLastWriteTimeUtc(path);

        string? first = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        // Another file of the same length, its mtime set to the original's, renamed over the name: only the
        // file identity differs, so a length-and-mtime validation would serve the stale body.
        string replacement = _workspace.WriteFile("same-length/replacement.tmp", "other text");

        Assert.Equal("first body".Length, "other text".Length);

        File.SetLastWriteTimeUtc(replacement, pinned);

        File.Move(replacement, path, overwrite: true);

        string? second = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        Assert.Equal("first body", first);

        Assert.Equal("other text", second);
    }

    /// <summary>
    /// The shared process-wide cache never exceeds its capacity however many codexes are read. The eviction
    /// order itself is pinned deterministically in <c>CodexReadCacheTests</c> on a private cache, because
    /// other tests insert into this shared one concurrently.
    /// </summary>
    [Fact]
    public async Task Shared_cache_never_exceeds_its_capacity()
    {
        Assert.True(CodexReader.CacheCapacity > 1);

        string[] paths = Enumerable
            .Range(0, CodexReader.CacheCapacity + 8)
            .Select(index => _workspace.WriteFile($"bounded/{index}/CODEX.md", $"body {index}"))
            .ToArray();

        foreach (string path in paths)
        {
            Assert.Equal(SecureFileReadStatus.Success, (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Status);
        }

        Assert.True(CodexReader.CachedEntryCountForTests <= CodexReader.CacheCapacity);
    }

    [Fact]
    public async Task ReadCodexFileAsync_uses_cache_for_unchanged_file()
    {
        string path = _workspace.WriteFile("cached/CODEX.md", "cached text");

        DateTime initialLastWriteTimeUtc = File.GetLastWriteTimeUtc(path);

        string? first = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        File.WriteAllText(path, "mutated text");

        File.SetLastWriteTimeUtc(path, initialLastWriteTimeUtc.AddSeconds(1));

        string? second = (await CodexReader.ReadCodexFileAsync(path, maxSizeBytes: 4096, CancellationToken.None)).Text;

        Assert.Equal("cached text", first);

        Assert.Equal("mutated text", second);
    }
}
