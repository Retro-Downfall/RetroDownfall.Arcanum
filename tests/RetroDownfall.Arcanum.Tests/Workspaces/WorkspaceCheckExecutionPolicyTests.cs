using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

/// <summary>
/// <see cref="WorkspaceCheckExecutionPolicy.IsMandatoryJailAvailableForCurrentHost"/> spawns
/// <c>/usr/bin/sandbox-exec</c> to probe filesystem-jail availability. Every caller — the
/// <c>workspace_check</c> invocation path and the tools/list advertisement gate alike — used to pay
/// that spawn on every call; these tests pin the process-lifetime cache that now avoids it.
/// </summary>
public sealed class WorkspaceCheckExecutionPolicyTests : IDisposable
{

    public WorkspaceCheckExecutionPolicyTests()
    {

        WorkspaceCheckExecutionPolicy.ResetTestSeams();

    }

    public void Dispose()
    {

        WorkspaceCheckExecutionPolicy.ResetTestSeams();

    }

    [Fact]
    public void GetStatus_CalledTwice_ProbesMandatoryJailAtMostOnce()
    {

        WorkspaceCheckRuntime runtime = new(
            new WorkspaceCheckSettings { Enabled = false },
            new NeverUsedServiceScopeFactory());

        _ = runtime.GetStatus(Path.GetTempPath());

        _ = runtime.GetStatus(Path.GetTempPath());

        Assert.Equal(1, WorkspaceCheckExecutionPolicy.MandatoryJailProbeCountForTests);

    }

    private sealed class NeverUsedServiceScopeFactory : IServiceScopeFactory
    {

        public IServiceScope CreateScope() =>
            throw new NotSupportedException(
                "GetStatus never creates a scope; this factory exists only to satisfy the constructor.");

    }

}
