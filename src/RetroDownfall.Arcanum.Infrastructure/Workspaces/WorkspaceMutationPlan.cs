using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

/// <summary>An immutable complete namespace plan, including every possible recovery artifact, bound to one live physical permit.</summary>
internal sealed class WorkspaceMutationPlan(string workspaceRoot)
{
    private static readonly AsyncLocal<Frame?> Ambient = new();

    private readonly string _root = Path.GetFullPath(workspaceRoot);

    private readonly Dictionary<string, GuildPathScopeKind> _paths = new(StringComparer.Ordinal);

    private readonly Dictionary<(string Path, string Purpose), string> _artifacts = new();

    private bool _sealed;

    private Error? _identityError;

    internal string Root => _root;

    internal ImmutableArray<GuildPathScope> Scopes => _paths.Select(entry => new GuildPathScope(
        Path.GetRelativePath(_root, entry.Key).Replace('\\', '/'), entry.Value)).ToImmutableArray();

    internal void Add(string path, GuildPathScopeKind kind = GuildPathScopeKind.File)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("An admitted mutation plan cannot grow.");
        }

        string absolute = Path.GetFullPath(path);

        if (!OperatingSystem.IsWindows() && absolute.Contains('\\') && WorkspaceMutationFence.IsInstalled)
        {
            _identityError = new(ErrorCodes.Guild.WorkspaceIdentityUnavailable,
                "The filesystem filename cannot be represented by the canonical Guild path grammar.");
        }

        if (!Security.WorkspacePathPolicy.IsPathUnderWorkspace(_root, absolute))
        {
            throw new IOException("The mutation plan leaves its workspace.");
        }

        if (!_paths.TryGetValue(absolute, out GuildPathScopeKind existing) || existing != GuildPathScopeKind.Subtree)
        {
            _paths[absolute] = kind;
        }

        for (string? parent = Path.GetDirectoryName(absolute); parent is not null
            && !string.Equals(parent, _root, StringComparison.Ordinal)
            && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
        {
            _paths[parent] = GuildPathScopeKind.Subtree;
        }
    }

    internal string Artifact(string source, string purpose, GuildPathScopeKind kind = GuildPathScopeKind.File, string? prefix = null)
    {
        string absolute = Path.GetFullPath(source);

        if (_artifacts.TryGetValue((absolute, purpose), out string? existing))
        {
            return existing;
        }

        if (_sealed)
        {
            throw new IOException("A recovery artifact was absent from the admitted mutation plan.");
        }

        string artifact = Path.Combine(Path.GetDirectoryName(absolute) ?? _root,
            prefix is null ? $".{Path.GetFileName(absolute)}.arcanum-{Guid.NewGuid():N}.{purpose}" : $"{prefix}{Guid.NewGuid():N}");

        _artifacts.Add((absolute, purpose), artifact);

        Add(artifact, kind);

        return artifact;
    }

    internal string ArtifactChild(string directory, string purpose, GuildPathScopeKind kind, string prefix)
    {
        string absolute = Path.GetFullPath(directory);

        if (_artifacts.TryGetValue((absolute, purpose), out string? existing))
        {
            return existing;
        }

        if (_sealed)
        {
            throw new IOException("An admitted mutation plan cannot grow.");
        }

        string artifact = Path.Combine(absolute, $"{prefix}{Guid.NewGuid():N}");

        _artifacts.Add((absolute, purpose), artifact);

        Add(artifact, kind);

        return artifact;
    }

    internal void AddAtomicFile(string destination, string temporary)
    {
        Add(destination);

        Add(temporary);

        string backup = Artifact(destination, "atomic-backup", prefix: AtomicFile.BackupPrefix);

        string quarantine = Artifact(destination, "atomic-quarantine", prefix: AtomicFile.QuarantinePrefix);

        foreach (string artifact in new[] { temporary, backup, quarantine })
        {
            Artifact(artifact, "identity-cleanup", GuildPathScopeKind.Subtree, ".arcanum-cleanup-");
        }
    }

    internal async ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(CancellationToken cancellationToken)
    {
        _sealed = true;

        if (_identityError is not null)
        {
            return _identityError;
        }

        return await WorkspaceMutationFence.AdmitAsync(_root, Scopes, cancellationToken).ConfigureAwait(false);
    }

    internal IDisposable Enter(IWorkspaceMutationLease lease, bool rollback = false)
    {
        Frame? previous = Ambient.Value;

        IDisposable authority = lease.Enter();

        Ambient.Value = new(this, lease, rollback);

        return new Scope(previous, authority);
    }

    internal static string ArtifactPath(string source, string purpose, Func<string> legacy)
    {
        if (Ambient.Value is not { } frame)
        {
            return legacy();
        }

        if (frame.Plan._artifacts.TryGetValue((Path.GetFullPath(source), purpose), out string? planned))
        {
            return planned;
        }

        // A reserved fresh private subtree already describes every allocation within it. This
        // cannot enlarge a file scope or add a sibling namespace slot after admission.
        string candidate = legacy();

        Demand(candidate);

        return candidate;
    }

    internal static IDisposable BeginRollback()
    {
        Frame? previous = Ambient.Value;

        if (previous is not null)
        {
            Ambient.Value = previous with { Rollback = true };
        }

        return new Scope(previous, null);
    }

    internal static WorkspaceLeaseDirectoryCreation? PrepareDirectoryCreation(string path, SafeFileHandle? relocationSource = null, SafeFileHandle? actualParent = null)
    {
        DemandBound(path);

        if (!WorkspaceMutationFence.IsInstalled)
        {
            return null;
        }

        Result<WorkspaceLeaseDirectoryCreation> prepared = WorkspaceLeaseDirectoryCreation.Prepare(path, relocationSource);

        if (prepared.IsFailure)
        {
            throw new WorkspaceMutationFenceException(prepared.Error);
        }

        if (actualParent is not null
            && (!FileHandleIdentityInterop.TryGetHandleIdentity(actualParent, out FileHandleIdentity actualParentIdentity)
                || actualParentIdentity != prepared.Value.ParentIdentity))
        {
            prepared.Value.Dispose();

            throw new WorkspaceMutationFenceException(WorkspaceLeaseIdentityResolver.Unavailable);
        }

        Result valid = Ambient.Value!.Lease.ValidateDirectoryCreationAsync(prepared.Value,
            Ambient.Value.Rollback, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (valid.IsFailure)
        {
            prepared.Value.Dispose();

            throw new WorkspaceMutationFenceException(valid.Error);
        }

        return prepared.Value;
    }

    internal static void AdoptCreatedDirectory(WorkspaceLeaseDirectoryCreation? preparation, SafeFileHandle handle)
    {
        if (preparation is null)
        {
            return;
        }

        Result<WorkspaceLeaseCreatedDirectory> completed = preparation.Complete(handle);

        if (completed.IsFailure)
        {
            RetainRecovery("directory_identity_unavailable");

            throw new WorkspaceMutationFenceException(completed.Error);
        }

        using WorkspaceLeaseCreatedDirectory proof = completed.Value;

        AdoptCreatedDirectory(proof);
    }

    private static void AdoptCreatedDirectory(WorkspaceLeaseCreatedDirectory proof)
    {
        Frame frame = Ambient.Value ?? throw new InvalidOperationException("A directory effect lost its physical permit.");

        Result adopted = frame.Lease.AdoptCreatedDirectoryAsync(proof, frame.Rollback,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (adopted.IsFailure)
        {
            RetainRecovery("directory_identity_unavailable");

            throw new WorkspaceMutationFenceException(adopted.Error);
        }
    }

    internal static void CreateDirectoryChain(string path, UnixFileMode? mode = null)
    {
        DemandBound(path);

        if (!WorkspaceMutationFence.IsInstalled)
        {
            Directory.CreateDirectory(path);

            return;
        }

        Stack<string> missing = new();

        for (string? current = Path.GetFullPath(path); current is not null && !Directory.Exists(current);
            current = Path.GetDirectoryName(current))
        {
            missing.Push(current);
        }

        while (missing.TryPop(out string? directory))
        {
            using WorkspaceLeaseDirectoryCreation preparation = PrepareDirectoryCreation(directory)!;

            Result<WorkspaceLeaseCreatedDirectory> created = preparation.CreateExclusive(mode);

            if (created.IsFailure)
            {
                RetainRecovery("directory_identity_unavailable");

                throw new WorkspaceMutationFenceException(created.Error);
            }

            using WorkspaceLeaseCreatedDirectory proof = created.Value;

            AdoptCreatedDirectory(proof);
        }
    }

    internal static WorkspaceLeaseDirectoryRemoval? PrepareDirectoryRemoval(string path, SafeFileHandle? retained = null, SafeFileHandle? actualParent = null)
    {
        DemandBound(path);

        if (!WorkspaceMutationFence.IsInstalled)
        {
            return null;
        }

        Result<WorkspaceLeaseDirectoryRemoval> prepared = WorkspaceLeaseDirectoryRemoval.Prepare(path, retained);

        if (prepared.IsFailure)
        {
            throw new WorkspaceMutationFenceException(prepared.Error);
        }

        if (actualParent is not null
            && (!FileHandleIdentityInterop.TryGetHandleIdentity(actualParent, out FileHandleIdentity actualParentIdentity)
                || actualParentIdentity != prepared.Value.ParentIdentity))
        {
            prepared.Value.Dispose();

            throw new WorkspaceMutationFenceException(WorkspaceLeaseIdentityResolver.Unavailable);
        }

        Result valid = Ambient.Value!.Lease.ValidateDirectoryRemovalAsync(prepared.Value,
            Ambient.Value.Rollback, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (valid.IsFailure)
        {
            prepared.Value.Dispose();

            throw new WorkspaceMutationFenceException(valid.Error);
        }

        return prepared.Value;
    }

    internal static void AdoptRemovedDirectory(WorkspaceLeaseDirectoryRemoval? preparation)
    {
        if (preparation is null)
        {
            return;
        }

        Result<WorkspaceLeaseRemovedDirectory> completed = preparation.Complete();

        if (completed.IsFailure)
        {
            RetainRecovery("directory_identity_unavailable");

            throw new WorkspaceMutationFenceException(completed.Error);
        }

        using WorkspaceLeaseRemovedDirectory proof = completed.Value;

        Frame frame = Ambient.Value ?? throw new InvalidOperationException("A directory effect lost its physical permit.");

        Result adopted = frame.Lease.AdoptRemovedDirectoryAsync(proof, frame.Rollback,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (adopted.IsFailure)
        {
            RetainRecovery("directory_identity_unavailable");

            throw new WorkspaceMutationFenceException(adopted.Error);
        }
    }

    internal static void DeleteDirectory(string path, bool recursive, SafeFileHandle? retained = null)
    {
        using WorkspaceLeaseDirectoryRemoval? preparation = PrepareDirectoryRemoval(path, retained);

        Directory.Delete(path, recursive);

        retained?.Dispose();

        AdoptRemovedDirectory(preparation);
    }

    internal static void MoveDirectory(string source, string destination, SafeFileHandle? retained = null)
    {
        DemandBound(source);

        SafeFileHandle? opened = null;

        if (WorkspaceMutationFence.IsInstalled && retained is null
            && !FileHandleIdentityInterop.TryOpenDirectoryMetadata(source, out opened, out _))
        {
            throw new WorkspaceMutationFenceException(WorkspaceLeaseIdentityResolver.Unavailable);
        }

        using (opened)
        using (WorkspaceLeaseDirectoryCreation? preparation = PrepareDirectoryCreation(destination, retained ?? opened))
        {
            Directory.Move(source, destination);

            if (preparation is not null)
            {
                AdoptCreatedDirectory(preparation, retained ?? opened!);
            }
        }
    }

    internal static void RetainRecovery(string reasonCode)
    {
        if (Ambient.Value is not { } frame)
        {
            return;
        }

        Result retained = frame.Lease.RetainRecoveryAsync(reasonCode, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (retained.IsFailure)
        {
            throw new WorkspaceMutationFenceException(retained.Error);
        }
    }

    internal static async ValueTask<Result> CreateDirectoryAsync(string root, string path, CancellationToken cancellationToken)
    {
        WorkspaceMutationPlan plan = new(root);

        plan.Add(path, GuildPathScopeKind.Subtree);

        Result<IWorkspaceMutationLease> admitted = await plan.AdmitAsync(cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return admitted.Error;
        }

        await using IWorkspaceMutationLease lease = admitted.Value;

        using IDisposable binding = plan.Enter(lease);

        try
        {
            Demand(path);

            CreateDirectoryChain(path);

            return Result.Success();
        }
        catch (WorkspaceMutationFenceException exception)
        {
            return exception.Error;
        }
    }

    internal static void DemandBound(string path)
    {
        if (Ambient.Value is null && WorkspaceMutationFence.IsInstalled)
        {
            throw new WorkspaceMutationFenceException(new(ErrorCodes.Guild.ToolBoundaryUnavailable,
                "The native writer requires its host-bound complete physical mutation plan."));
        }

        Demand(path);
    }

    internal static void Demand(string path, bool rollback = false)
    {
        if (Ambient.Value is not { } frame)
        {
            return;
        }

        string absolute = Path.GetFullPath(path);

        bool covered = frame.Plan._paths.Any(entry => string.Equals(entry.Key, absolute, StringComparison.Ordinal)
            || entry.Value == GuildPathScopeKind.Subtree
                && absolute.StartsWith(entry.Key.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        if (!covered)
        {
            throw new WorkspaceMutationFenceException(new(ErrorCodes.Guild.ToolBoundaryUnavailable,
                "The actual mutation was absent from its complete physical plan."));
        }

        Result valid = frame.Lease.ValidateAsync(rollback || frame.Rollback, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (valid.IsFailure)
        {
            throw new WorkspaceMutationFenceException(valid.Error);
        }
    }

    private sealed record Frame(WorkspaceMutationPlan Plan, IWorkspaceMutationLease Lease, bool Rollback);

    private sealed class Scope(Frame? previous, IDisposable? authority) : IDisposable
    {
        public void Dispose()
        {
            Ambient.Value = previous;

            authority?.Dispose();
        }
    }
}

internal sealed class WorkspaceMutationFenceException(Error error) : IOException(error.Message)
{
    internal Error Error { get; } = error;
}
