using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>Opaque effects cannot establish complete physical mutation or process-extinction proof.</summary>
internal static class WorkspaceProcessBoundary
{
    internal const string Message = "Guild.ToolBoundaryUnavailable: complete filesystem effect containment and process-tree extinction are unavailable. Use the managed file tools or an eligible read-only check.";

    internal static bool RequiresContainment => WorkspaceMutationFence.IsInstalled || GuildToolInvocationAmbient.Current is not null;

    internal static bool TryBeginOpaqueEffect(out WorkspaceMutationFence.OpaqueEffect? effect)
    {
        effect = null;

        return GuildToolInvocationAmbient.Current is null && WorkspaceMutationFence.TryBeginOpaqueEffect(out effect);
    }

    internal static bool IsReadOnlyJail(ChildProcessSandboxRequest? sandbox) =>
        sandbox is { RequireAppliedFilesystemJail: true, AllowUnsandboxed: false, DenyFileWrites: true, DenyNetwork: true }
        && sandbox.ReadWriteRoots.Count == 0;

    internal static CappedChildProcessRunResult RefusedRun() => new()
    {
        Outcome = CappedChildProcessOutcome.PreStartValidationFailed,
        PreStartValidationCode = ErrorCodes.Guild.ToolBoundaryUnavailable,
        PreStartValidationError = Message,
    };
}
