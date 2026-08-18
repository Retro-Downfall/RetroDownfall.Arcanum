using System.Text;
using RetroDownfall.TheForge.Ux.Services.Git;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class GitProcessRunnerTests
{

    private const string Porcelain = " M alpha.txt\n M bravo.txt\n M charlie.txt\n";

    [Fact]
    public async Task ReadBoundedAsync_UnderCap_ReturnsOutputVerbatim()
    {

        (string text, bool truncated) = await ReadAsync(Porcelain, maxChars: Porcelain.Length);

        Assert.False(truncated);

        Assert.Equal(Porcelain, text);

    }

    [Fact]
    public async Task ReadBoundedAsync_Truncated_DoesNotInjectASentinelIntoTheOutput()
    {

        // The captured text is machine-readable git output: any human-readable notice appended to it is
        // parsed as another change row.
        (string text, bool truncated) = await ReadAsync(Porcelain, maxChars: 20);

        Assert.True(truncated);

        Assert.DoesNotContain("The Forge", text, StringComparison.Ordinal);

        IReadOnlyList<GitPorcelainEntry> entries = GitPorcelainParser.Parse(text);

        Assert.All(entries, entry => Assert.Contains(entry.Path, Porcelain, StringComparison.Ordinal));

    }

    [Fact]
    public async Task ReadBoundedAsync_Truncated_DropsTheTrailingPartialLine()
    {

        // The bound cuts at a character offset, so without this a half path (" M char") would parse as a
        // complete porcelain row and be diffed or staged as if it were a real file.
        (string text, bool truncated) = await ReadAsync(Porcelain, maxChars: 33);

        Assert.True(truncated);

        Assert.Equal(" M alpha.txt\n M bravo.txt\n", text);

    }

    [Fact]
    public async Task ReadBoundedAsync_TruncatedBeforeAnyNewline_ReturnsNoLines()
    {

        (string text, bool truncated) = await ReadAsync(Porcelain, maxChars: 5);

        Assert.True(truncated);

        Assert.Equal(string.Empty, text);

    }

    private static async Task<(string Text, bool Truncated)> ReadAsync(string payload, int maxChars)
    {

        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(payload));

        using StreamReader reader = new(stream);

        return await GitProcessRunner.ReadBoundedAsync(reader, maxChars, CancellationToken.None);

    }

}
