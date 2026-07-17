using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

public sealed class ToolChildSandboxCapabilityReporterTests
{

    [Fact]
    public void MacOs_with_sandbox_exec_is_active_and_not_degraded()
    {

        ToolChildSandboxStatus status = ToolChildSandboxCapabilityReporter.Build(
            ToolChildSandboxCapabilityReporter.Platforms.MacOs,
            escapeHatchEnabled: false,
            macOsSandboxExecPresent: true);

        Assert.Equal(ToolChildFilesystemJailMode.Active, status.FilesystemJailMode);

        Assert.Equal(ToolChildResourceLimitsMode.Active, status.ResourceLimitsMode);

        Assert.Equal(ToolChildNetworkIsolationMode.NotProvided, status.NetworkIsolationMode);

        Assert.True(status.IsBetaSafeDefault);

        Assert.False(status.IsHealthDegraded);

        Assert.False(status.EscapeHatchEnabled);

        Assert.Contains("not provided", status.PublicMessage, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void MacOs_without_sandbox_exec_is_fail_closed_degraded()
    {

        ToolChildSandboxStatus status = ToolChildSandboxCapabilityReporter.Build(
            ToolChildSandboxCapabilityReporter.Platforms.MacOs,
            escapeHatchEnabled: false,
            macOsSandboxExecPresent: false);

        Assert.Equal(ToolChildFilesystemJailMode.InactiveFailClosed, status.FilesystemJailMode);

        Assert.True(status.IsHealthDegraded);

        Assert.False(status.IsBetaSafeDefault);

    }

    [Fact]
    public void Linux_inactive_fail_closed_is_degraded_with_exact_denial()
    {

        ToolChildSandboxStatus status = ToolChildSandboxCapabilityReporter.Build(
            ToolChildSandboxCapabilityReporter.Platforms.Linux,
            escapeHatchEnabled: false);

        Assert.Equal(ToolChildFilesystemJailMode.InactiveFailClosed, status.FilesystemJailMode);

        Assert.True(status.IsHealthDegraded);

        Assert.Contains(
            ToolChildSandboxCapabilityReporter.LinuxBetaDenialMessage,
            status.PublicMessage,
            StringComparison.Ordinal);

        Assert.Equal(ToolChildNetworkIsolationMode.NotProvided, status.NetworkIsolationMode);

    }

    [Fact]
    public void Windows_no_fs_jail_is_degraded_even_when_documented()
    {

        ToolChildSandboxStatus status = ToolChildSandboxCapabilityReporter.Build(
            ToolChildSandboxCapabilityReporter.Platforms.Windows,
            escapeHatchEnabled: false);

        Assert.Equal(ToolChildFilesystemJailMode.NotAvailable, status.FilesystemJailMode);

        Assert.Equal(ToolChildResourceLimitsMode.Partial, status.ResourceLimitsMode);

        Assert.True(status.IsHealthDegraded);

        Assert.False(status.IsBetaSafeDefault);

        Assert.Contains("Job Objects", status.PublicMessage, StringComparison.Ordinal);

        Assert.Contains("does not bypass", status.PublicMessage, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Escape_hatch_is_always_degraded_with_prominent_guidance()
    {

        foreach (string platform in new[]
                 {
                     ToolChildSandboxCapabilityReporter.Platforms.MacOs,
                     ToolChildSandboxCapabilityReporter.Platforms.Linux,
                     ToolChildSandboxCapabilityReporter.Platforms.Windows,
                 })
        {

            ToolChildSandboxStatus status = ToolChildSandboxCapabilityReporter.Build(
                platform,
                escapeHatchEnabled: true,
                macOsSandboxExecPresent: true);

            Assert.Equal(ToolChildFilesystemJailMode.UnsandboxedEscapeHatch, status.FilesystemJailMode);

            Assert.True(status.EscapeHatchEnabled);

            Assert.True(status.IsHealthDegraded);

            Assert.False(status.IsBetaSafeDefault);

            Assert.Contains("AllowUnsandboxedToolChildren", status.PublicMessage, StringComparison.Ordinal);

        }

    }

    [Fact]
    public void LinuxDeferredDetail_matches_exact_beta_denial()
    {

        Assert.Equal(
            ToolChildSandboxCapabilityReporter.LinuxBetaDenialMessage,
            ChildProcessFilesystemJail.LinuxDeferredDetail);

    }

}
