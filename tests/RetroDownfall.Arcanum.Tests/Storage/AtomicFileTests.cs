using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class AtomicFileTests : IDisposable
{

    private readonly string _root;

    public AtomicFileTests()
    {

        _root = Path.Combine(Path.GetTempPath(), $"arcanum-atomicfile-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_root);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task ReplaceAsync_writes_content_and_leaves_no_temp_residue()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        string tempPath = TempPathFor(destination);

        bool replaced = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "durable content", ct),
            CancellationToken.None);

        Assert.True(replaced);

        Assert.Equal("durable content", await File.ReadAllTextAsync(destination));

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        Assert.False(File.Exists(tempPath));

    }

    [Fact]
    public async Task ReplaceAsync_atomically_overwrites_existing_destination()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        await File.WriteAllTextAsync(destination, "original");

        bool replaced = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "replacement", ct),
            CancellationToken.None);

        Assert.True(replaced);

        Assert.Equal("replacement", await File.ReadAllTextAsync(destination));

        Assert.Single(Directory.GetFiles(_root));

    }

    [Fact]
    public async Task ReplaceAsync_invokes_afterReplace_hook_after_the_move_completes()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        bool destinationExistedWhenHookRan = false;

        bool replaced = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "hooked", ct),
            CancellationToken.None,
            afterReplace: () =>
            {

                destinationExistedWhenHookRan = File.Exists(destination);

                return true;

            });

        Assert.True(replaced);

        Assert.True(destinationExistedWhenHookRan);

    }

    [Fact]
    public async Task ReplaceAsync_when_beforeReplace_returns_false_aborts_and_cleans_temp()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        string tempPath = TempPathFor(destination);

        bool replaced = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "should not land", ct),
            CancellationToken.None,
            beforeReplace: () => false);

        Assert.False(replaced);

        Assert.False(File.Exists(destination));

        Assert.False(File.Exists(tempPath));

        Assert.Empty(Directory.GetFiles(_root));

    }

    [Fact]
    public async Task ReplaceAsync_when_afterReplace_returns_false_still_replaces_but_reports_failure()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        await File.WriteAllTextAsync(destination, "original");

        string tempPath = TempPathFor(destination);

        bool replaced = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "post-move state", ct),
            CancellationToken.None,
            afterReplace: () => false);

        Assert.False(replaced);

        // The rename happens before afterReplace, so a fail-closed post-move hook cannot undo it.
        Assert.Equal("post-move state", await File.ReadAllTextAsync(destination));

        Assert.False(File.Exists(tempPath));

    }

    [Fact]
    public async Task ReplaceAsync_when_write_throws_cleans_temp_and_propagates_and_keeps_destination()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        await File.WriteAllTextAsync(destination, "original");

        string tempPath = TempPathFor(destination);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AtomicFile.ReplaceAsync(
                destination,
                tempPath,
                (_, _) => throw new InvalidOperationException("write failed"),
                CancellationToken.None));

        Assert.False(File.Exists(tempPath));

        Assert.Equal("original", await File.ReadAllTextAsync(destination));

    }

    private string TempPathFor(string destination) =>
        Path.Combine(_root, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

    private static async Task WriteTextAsync(Stream stream, string text, CancellationToken cancellationToken)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(text);

        await stream.WriteAsync(bytes, cancellationToken);

    }

}
