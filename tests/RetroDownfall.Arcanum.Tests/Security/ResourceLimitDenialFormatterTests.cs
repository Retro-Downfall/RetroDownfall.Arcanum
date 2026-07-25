using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ResourceLimitDenialFormatterTests
{

    private static readonly ResourceLimits Limits = new()
    {
        MaxCpuSeconds = 7,
        MaxMemoryMb = 11,
        MaxFileDescriptors = 13,
    };

    [Theory]
    [InlineData(ResourceLimitKind.Cpu, "CPU time", "7s")]
    [InlineData(ResourceLimitKind.Memory, "memory", "11 MB")]
    [InlineData(ResourceLimitKind.FileDescriptors, "open file descriptor", "13")]
    [InlineData((ResourceLimitKind)999, "resource", "n/a")]
    public async Task RecordAndDescribeAsync_records_and_formats_each_resource(
        ResourceLimitKind resource,
        string resourceName,
        string limitValue)
    {

        RecordingSanctumGuard guard = new();

        using CancellationTokenSource cancellation = new();

        string message = await ResourceLimitDenialFormatter.RecordAndDescribeAsync(
            guard,
            @"C:\isolated\workspace",
            "execute_command",
            Limits,
            resource,
            cancellation.Token);

        Assert.Equal(
            $"Execution blocked: this tool exceeded the {resourceName} limit ({limitValue}). "
            + "The invocation has been terminated and recorded as a breach.",
            message);

        Breach breach = Assert.Single(guard.Breaches);

        Assert.Equal(@"C:\isolated\workspace", breach.WorkspaceRoot);

        Assert.Equal("execute_command", breach.ToolName);

        Assert.Equal(resource, breach.Resource);

        Assert.Equal(limitValue, breach.LimitValue);

        Assert.Null(breach.ActualValue);

        Assert.Equal(cancellation.Token, breach.CancellationToken);

    }

    [Fact]
    public async Task RecordAndDescribeAsync_null_resource_defaults_to_cpu()
    {

        RecordingSanctumGuard guard = new();

        string message = await ResourceLimitDenialFormatter.RecordAndDescribeAsync(
            guard,
            workspaceRoot: null,
            toolName: "run_spell_script",
            Limits,
            exceededResource: null,
            CancellationToken.None);

        Assert.Contains("CPU time limit (7s)", message, StringComparison.Ordinal);

        Breach breach = Assert.Single(guard.Breaches);

        Assert.Null(breach.WorkspaceRoot);

        Assert.Equal(ResourceLimitKind.Cpu, breach.Resource);

        Assert.Equal("7s", breach.LimitValue);

    }

    [Fact]
    public async Task RecordAndDescribeAsync_propagates_cancelled_breach_recording()
    {

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        RecordingSanctumGuard guard = new()
        {
            RecordBehavior = token => Task.FromCanceled(token),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ResourceLimitDenialFormatter.RecordAndDescribeAsync(
                guard,
                "/isolated/workspace",
                "execute_command",
                Limits,
                ResourceLimitKind.Memory,
                cancellation.Token));

        Breach breach = Assert.Single(guard.Breaches);

        Assert.Equal(ResourceLimitKind.Memory, breach.Resource);

        Assert.Equal("11 MB", breach.LimitValue);

        Assert.Equal(cancellation.Token, breach.CancellationToken);

    }

    private sealed record Breach(
        string? WorkspaceRoot,
        string ToolName,
        ResourceLimitKind Resource,
        string LimitValue,
        string? ActualValue,
        CancellationToken CancellationToken);

    private sealed class RecordingSanctumGuard : ISanctumGuard
    {

        public List<Breach> Breaches { get; } = [];

        public Func<CancellationToken, Task>? RecordBehavior { get; init; }

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(Limits);

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default)
        {

            Breaches.Add(new Breach(workspaceRoot, toolName, resource, limitValue, actualValue, ct));

            return RecordBehavior?.Invoke(ct) ?? Task.CompletedTask;

        }

    }

}
