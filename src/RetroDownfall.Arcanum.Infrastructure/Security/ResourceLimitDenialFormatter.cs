using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Shared, sanitized model-facing denial text for OS-enforced resource limit breaches, plus the
/// breach-recording call that must accompany it. Used by both <c>execute_command</c> and
/// <c>run_spell_script</c> so the two tools stay consistent.
/// </summary>
/// <remarks>
/// The message intentionally contains only the resource name and the configured limit value — never
/// signal numbers, PIDs, cgroup paths, or stack traces. Operator-facing detail (signal, workspace
/// path) is recorded separately via <see cref="ISanctumGuard.RecordResourceLimitBreachAsync"/> and is
/// visible only in the Sanctum breach audit log, not to the model.
/// </remarks>
public static class ResourceLimitDenialFormatter
{

    public static async Task<string> RecordAndDescribeAsync(
        ISanctumGuard sanctumGuard,
        string? workspaceRoot,
        string toolName,
        ResourceLimits limits,
        ResourceLimitKind? exceededResource,
        CancellationToken cancellationToken)
    {

        ResourceLimitKind resource = exceededResource ?? ResourceLimitKind.Cpu;

        string limitValue = DescribeLimitValue(resource, limits);

        await sanctumGuard.RecordResourceLimitBreachAsync(
            workspaceRoot,
            toolName,
            resource,
            limitValue,
            actualValue: null,
            cancellationToken).ConfigureAwait(false);

        string resourceName = DescribeResourceName(resource);

        return $"Execution blocked: this tool exceeded the {resourceName} limit ({limitValue}). The invocation has been terminated and recorded as a breach.";

    }

    private static string DescribeResourceName(ResourceLimitKind resource) => resource switch
    {
        ResourceLimitKind.Cpu => "CPU time",
        ResourceLimitKind.Memory => "memory",
        ResourceLimitKind.FileDescriptors => "open file descriptor",
        _ => "resource",
    };

    private static string DescribeLimitValue(ResourceLimitKind resource, ResourceLimits limits) => resource switch
    {
        ResourceLimitKind.Cpu => $"{limits.MaxCpuSeconds}s",
        ResourceLimitKind.Memory => $"{limits.MaxMemoryMb} MB",
        ResourceLimitKind.FileDescriptors => $"{limits.MaxFileDescriptors}",
        _ => "n/a",
    };

}
