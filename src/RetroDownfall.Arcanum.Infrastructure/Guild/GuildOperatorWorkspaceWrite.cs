using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>The native workspace selection retained by an authenticated, persisted operator turn.</summary>
internal sealed class GuildOperatorWorkspaceWrite(GuildLocalHostPolicy policy, IGuildLocalHostReadiness readiness,
    WorkspaceLeaseIdentityResolver identities, WorkspaceLeaseIdentitySet physical, GuildWorkspaceMutationFence fence,
    GuildVerifiedAuthority authority, Guid sessionId, Guid? sessionCampaignId, Guid assistantEntryId, Guid workspaceId,
    string workingDirectory)
{
    internal string WorkingDirectory { get; } = Path.TrimEndingDirectorySeparator(workingDirectory);

    internal static bool IsWriter(string? name) => name is "write_file" or "edit_file" or ToolRiskClassifier.ApplyPatchToolName;

    internal Call? BeginCall(Guid persistedSession, Guid persistedAssistant, string toolName) =>
        persistedSession == sessionId && persistedAssistant == assistantEntryId && IsWriter(toolName) ? new(this, toolName) : null;

    internal static Error Forbidden => new(ErrorCodes.Guild.Forbidden,
        "A managed workspace writer requires an authenticated Session turn and a current registered workspace.");

    private async ValueTask<Result> ValidateAsync(CancellationToken ct)
    {
        Result<GuildWorkspaceMutationFence> current = readiness.GetWorkspaceMutationFence();

        if (current.IsFailure || !ReferenceEquals(fence, current.Value)) return Forbidden;

        Result owner = await policy.ValidateWorkspaceWriterSessionAsync(sessionId, sessionCampaignId, assistantEntryId, ct)
            .ConfigureAwait(false);

        if (owner.IsFailure) return owner;

        Result<GuildOperatorWorkspaceHostEvidence> workspace = await policy.ResolveOperatorAsync(workspaceId, ct).ConfigureAwait(false);

        if (workspace.IsFailure) return workspace.Error;

        if (!SamePath(workspace.Value.WorkspaceRoot, physical.WorkspaceRoot)) return Forbidden;

        return await identities.ValidateAsync(physical, ct).ConfigureAwait(false);
    }

    private IDisposable Enter(Call call) => fence.EnterInvocation(authority, workspaceId,
        callDigest: $"{assistantEntryId:N}:{call.ToolName}", operatorCall: call);

    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>One use on the private in-process MCP transport, expiring when dispatch returns.</summary>
    internal sealed class Call(GuildOperatorWorkspaceWrite owner, string toolName) : IDisposable
    {
        private int _state;

        internal string ToolName { get; } = toolName;

        internal bool IsActive => Volatile.Read(ref _state) == 1;

        internal WorkspaceLeaseIdentitySet PhysicalScope => owner.PhysicalScope;

        internal IDisposable? TryEnter(string name, string? root)
        {
            if (!IsWriter(name) || !string.Equals(name, ToolName, StringComparison.Ordinal)
                || root is null || !SamePath(root, owner.WorkingDirectory)
                || Interlocked.CompareExchange(ref _state, 1, 0) != 0) return null;

            return owner.Enter(this);
        }

        internal ValueTask<Result> ValidateAsync(CancellationToken ct) => IsActive
            ? owner.ValidateAsync(ct)
            : ValueTask.FromResult(Result.Failure(Forbidden));

        public void Dispose() => Interlocked.Exchange(ref _state, 2);
    }

    private WorkspaceLeaseIdentitySet PhysicalScope => physical;
}
