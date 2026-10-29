using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class RetentionMeasurementChurnTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_preserves_the_writer_fault_when_cleanup_observes_the_same_failure(bool dated)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("arcanum-retention-churn-fault-");

        try
        {
            // A directory at the first file path deterministically refuses the real writer.
            Directory.CreateDirectory(Path.Combine(root.FullName, dated ? "audit-29000000.jsonl" : ".staged-0.tmp"));

            Exception? failure = await Record.ExceptionAsync(async () =>
            {
                await using RetentionMeasurementChurn churn = await RetentionMeasurementChurn.StartAsync(root.FullName, dated);
            });

            Assert.NotNull(failure);

            Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());
        }
        finally
        {
            TestDirectoryCleanup.TryDelete(root.FullName, nameof(RetentionMeasurementChurnTests));
        }
    }
}
