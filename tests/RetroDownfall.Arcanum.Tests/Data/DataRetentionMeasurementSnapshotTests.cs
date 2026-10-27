using System.Reflection;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class DataRetentionMeasurementSnapshotTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retention-observation-" + Guid.NewGuid().ToString("N"))).FullName;

    private static MethodInfo Measurement => typeof(DataRetentionService).GetMethod("MeasureObservedFiles", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static (long Files, long Bytes) Measure(IEnumerable<FileInfo> source) =>
        Assert.IsType<ValueTuple<long, long>>(Measurement.Invoke(null, [source]));

    [Fact]
    public void Measurement_snapshot_drops_a_disappeared_uncached_file_without_a_ghost_count()
    {
        string path = Path.Combine(_root, "uncached.bin");

        File.WriteAllBytes(path, [1, 2, 3]);

        FileInfo uncached = new(path);

        File.Delete(path);

        Assert.Equal((0L, 0L), Measure([uncached]));
    }

    [Fact]
    public void Measurement_snapshot_may_truthfully_keep_metadata_observed_before_disappearance()
    {
        string path = Path.Combine(_root, "cached.bin");

        File.WriteAllBytes(path, [1, 2, 3]);

        FileInfo cached = Assert.Single(new DirectoryInfo(_root).EnumerateFiles());

        Assert.Equal(3, cached.Length);

        File.Delete(path);

        Assert.Equal((1L, 3L), Measure([cached]));
    }

    [Fact]
    public void Measurement_snapshot_keeps_only_completed_pairs_when_the_enumerated_tree_disappears()
    {
        string path = Path.Combine(_root, "first.bin");

        File.WriteAllBytes(path, [1, 2, 3]);

        IEnumerable<FileInfo> Entries()
        {
            yield return new FileInfo(path);

            throw new DirectoryNotFoundException("PRIVATE-REMOVED-DIRECTORY");
        }

        Assert.Equal((1L, 3L), Measure(Entries()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Measurement_snapshot_does_not_hide_unknown_io_or_access_failure(bool denied)
    {
        Exception expected = denied ? new UnauthorizedAccessException("PRIVATE-ACCESS") : new IOException("PRIVATE-IO");

        IEnumerable<FileInfo> Entries()
        {
            yield return new FileInfo(Path.Combine(_root, "gone.bin"));

            throw expected;
        }

        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => Measurement.Invoke(null, [Entries()]));

        Assert.Same(expected, failure.InnerException);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
