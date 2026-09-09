using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class ArcanumHealthCheckerFileEncryptionTests
{
    [Theory]
    [InlineData(FileEncryptionRuntimeState.Pending, HealthStatus.Degraded)]
    [InlineData(FileEncryptionRuntimeState.Deferred, HealthStatus.Healthy)]
    [InlineData(FileEncryptionRuntimeState.Ready, HealthStatus.Healthy)]
    [InlineData(FileEncryptionRuntimeState.Unavailable, HealthStatus.Unhealthy)]
    public void File_encryption_health_reads_one_constant_time_runtime_snapshot(
        FileEncryptionRuntimeState state,
        HealthStatus expected)
    {
        CountingRuntimeStatus status = new(
            new FileEncryptionRuntimeSnapshot(state, $"state={state}"));

        HealthComponentDto component =
            ArcanumHealthChecker.BuildFileEncryptionComponent(status);

        Assert.Equal("FileEncryption", component.Name);
        Assert.Equal(expected, component.Status);
        Assert.Equal($"state={state}", component.Detail);
        Assert.Equal(1, status.ReadCount);
    }

    [Fact]
    public void Missing_file_encryption_runtime_status_degrades_without_throwing()
    {
        HealthComponentDto component =
            ArcanumHealthChecker.BuildFileEncryptionComponent(status: null);

        Assert.Equal(HealthStatus.Degraded, component.Status);
        Assert.Contains("unavailable", component.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_status_failure_is_sanitized_and_does_not_take_health_down()
    {
        HealthComponentDto component =
            ArcanumHealthChecker.BuildFileEncryptionComponent(
                new ThrowingRuntimeStatus());

        Assert.Equal(HealthStatus.Unhealthy, component.Status);
        Assert.Contains(nameof(IOException), component.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive path", component.Detail, StringComparison.Ordinal);
    }

    private sealed class CountingRuntimeStatus(FileEncryptionRuntimeSnapshot snapshot) :
        IFileEncryptionRuntimeStatus
    {
        public int ReadCount { get; private set; }

        public FileEncryptionRuntimeSnapshot Current
        {
            get
            {
                ReadCount++;

                return snapshot;
            }
        }
    }

    private sealed class ThrowingRuntimeStatus : IFileEncryptionRuntimeStatus
    {
        public FileEncryptionRuntimeSnapshot Current =>
            throw new IOException("sensitive path");
    }
}
