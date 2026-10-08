using System.Text.Json;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Pattern;

public sealed class PatternSnapshotTests
{
    [Fact]
    public void Constructor_NullThreads_NormalizesToEmpty()
    {
        PatternSnapshot snapshot = new(default, "/root", null!);

        Assert.NotNull(snapshot.Threads);

        Assert.Empty(snapshot.Threads);
    }

    [Fact]
    public void WithExpression_NullThreads_NormalizesToEmpty()
    {
        PatternSnapshot snapshot = new PatternSnapshot(default, "/root", ["a"]) with { Threads = null! };

        Assert.NotNull(snapshot.Threads);

        Assert.Empty(snapshot.Threads);
    }

    [Fact]
    public void Deserialize_MissingThreads_NormalizesToEmpty()
    {
        PatternSnapshot? snapshot = JsonSerializer.Deserialize<PatternSnapshot>(
            """{"Domain":0,"RootPath":"/root"}""", AdHocJson.Options);

        Assert.NotNull(snapshot);

        Assert.NotNull(snapshot!.Threads);

        Assert.Empty(snapshot.Threads);
    }

    [Fact]
    public void Deserialize_ExplicitNullThreads_NormalizesToEmpty()
    {
        PatternSnapshot? snapshot = JsonSerializer.Deserialize<PatternSnapshot>(
            """{"Domain":0,"RootPath":"/root","Threads":null}""", AdHocJson.Options);

        Assert.NotNull(snapshot);

        Assert.NotNull(snapshot!.Threads);

        Assert.Empty(snapshot.Threads);
    }

    [Fact]
    public void Threads_PreservedWhenProvided()
    {
        PatternSnapshot snapshot = new(default, "/root", ["one", "two"]);

        Assert.Equal(2, snapshot.Threads.Length);

        Assert.Equal("one", snapshot.Threads[0]);
    }
}
