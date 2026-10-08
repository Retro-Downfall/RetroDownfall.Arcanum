using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

/// <summary>
/// <c>workspace_check</c> sums a whole <c>dotnet build</c>/<c>dotnet test</c> tree against one memory
/// ceiling, so it gets a code-owned ceiling of <c>max(effective campaign ceiling, 4096 MB)</c> capped at
/// 8192 MB rather than the 512 MB Sanctum default that suits <c>execute_command</c> and
/// <c>run_spell_script</c>. These pin the limits <see cref="WorkspaceCheckRuntime"/> hands the runner.
/// </summary>
public sealed class WorkspaceCheckRuntimeTests
{
    [Fact]
    public void Memory_ceiling_is_4096_MB_under_default_Sanctum_limits()
    {
        ResourceLimits campaign = ClampLikeSanctumGuard(new ResourceLimits());

        Assert.Equal(512, ProcessResourceLimiter.EffectiveMemoryLimitMb(campaign));

        ResourceLimits run = WorkspaceCheckRuntime.ApplyMemoryCeiling(campaign);

        Assert.Equal(4096, ProcessResourceLimiter.EffectiveMemoryLimitMb(run));
    }

    [Fact]
    public void Memory_ceiling_follows_a_campaign_ceiling_above_the_floor()
    {
        ResourceLimits campaign = ClampLikeSanctumGuard(
            new ResourceLimits { MaxMemoryMb = 6000, MaxProcessMemoryMb = 6000 });

        ResourceLimits run = WorkspaceCheckRuntime.ApplyMemoryCeiling(campaign);

        Assert.Equal(6000, ProcessResourceLimiter.EffectiveMemoryLimitMb(run));
    }

    [Fact]
    public void Memory_ceiling_uses_the_min_rule_so_raising_one_key_alone_keeps_the_floor()
    {
        ResourceLimits campaign = ClampLikeSanctumGuard(new ResourceLimits { MaxMemoryMb = 6000 });

        ResourceLimits run = WorkspaceCheckRuntime.ApplyMemoryCeiling(campaign);

        Assert.Equal(4096, ProcessResourceLimiter.EffectiveMemoryLimitMb(run));
    }

    [Fact]
    public void Memory_ceiling_is_capped_at_8192_MB()
    {
        ResourceLimits unclamped = new() { MaxMemoryMb = 20_000, MaxProcessMemoryMb = 20_000 };

        Assert.Equal(
            WorkspaceCheckRuntime.MemoryCeilingMaxMb,
            ProcessResourceLimiter.EffectiveMemoryLimitMb(
                WorkspaceCheckRuntime.ApplyMemoryCeiling(unclamped)));

        ResourceLimits clamped = ClampLikeSanctumGuard(unclamped);

        Assert.Equal(
            8192,
            ProcessResourceLimiter.EffectiveMemoryLimitMb(
                WorkspaceCheckRuntime.ApplyMemoryCeiling(clamped)));
    }

    [Fact]
    public void Memory_ceiling_leaves_every_other_limit_untouched()
    {
        ResourceLimits campaign = ClampLikeSanctumGuard(new ResourceLimits { MaxCpuSeconds = 120, MaxFileDescriptors = 512 });

        ResourceLimits run = WorkspaceCheckRuntime.ApplyMemoryCeiling(campaign);

        Assert.Equal(
            campaign with { MaxMemoryMb = 4096, MaxProcessMemoryMb = 4096 },
            run);
    }

    [Fact]
    public void Exceeded_message_names_memory_and_the_applied_ceiling()
    {
        ResourceLimits run = WorkspaceCheckRuntime.ApplyMemoryCeiling(ClampLikeSanctumGuard(new ResourceLimits()));

        string message = WorkspaceCheckRuntime.DescribeExceededLimit(ResourceLimitKind.Memory, run);

        Assert.Contains("4096 MB memory limit", message, StringComparison.Ordinal);
    }

    /// <summary>Mirrors <c>SanctumGuard.ClampResourceLimits</c> for the memory keys.</summary>
    private static ResourceLimits ClampLikeSanctumGuard(ResourceLimits limits) =>
        limits with
        {
            MaxMemoryMb = ArcanumSettingClamps.SanctumMaxMemoryMb(limits.MaxMemoryMb),
            MaxProcessMemoryMb = ArcanumSettingClamps.SanctumMaxProcessMemoryMb(limits.MaxProcessMemoryMb),
        };
}
