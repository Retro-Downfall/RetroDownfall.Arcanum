using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Captures an exact existing directory before its identity-owned removal.</summary>
internal sealed class WorkspaceLeaseDirectoryRemoval : IDisposable
{
    private readonly object _sync = new();

    private WorkspaceLeaseRetainedDirectory? _parent;

    private readonly WorkspaceLeaseRetainedDirectory _directory;

    private bool _finished;

    private WorkspaceLeaseDirectoryRemoval(WorkspaceLeaseRetainedDirectory parent, WorkspaceLeaseRetainedDirectory directory,
        ImmutableArray<WorkspaceLeaseDirectoryIdentity> ancestors, ImmutableArray<string> segments)
    {
        _parent = parent;

        _directory = directory;

        Path = directory.Path;

        Identity = directory.Identity;

        ParentIdentity = parent.Identity;

        CaseSensitive = parent.CaseSensitive;

        Ancestors = ancestors;

        Segments = segments;
    }

    internal string Path { get; }

    internal FileHandleIdentity Identity { get; }

    internal FileHandleIdentity ParentIdentity { get; }

    internal bool CaseSensitive { get; }

    internal ImmutableArray<WorkspaceLeaseDirectoryIdentity> Ancestors { get; }

    internal ImmutableArray<string> Segments { get; }

    internal static Result<WorkspaceLeaseDirectoryRemoval> Prepare(string path, SafeFileHandle? retained = null)
    {
        SafeFileHandle? opened = null;

        SafeFileHandle? parentHandle = null;

        WorkspaceLeaseRetainedDirectory? directory = null;

        WorkspaceLeaseRetainedDirectory? parent = null;

        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return WorkspaceLeaseIdentityResolver.Unavailable;

            string full = NoFollowPathTopology.NormalizeMacOsSystemAlias(System.IO.Path.GetFullPath(path));

            Result<NoFollowPathTopologyKind> topology = NoFollowPathTopology.Classify(full);

            if (topology.IsFailure || topology.Value != NoFollowPathTopologyKind.Directory) return WorkspaceLeaseIdentityResolver.Unavailable;

            if (retained is null && !FileHandleIdentityInterop.TryOpenDirectoryMetadata(full, out opened, out _)) return WorkspaceLeaseIdentityResolver.Unavailable;

            directory = WorkspaceLeaseRetainedDirectory.Capture(retained ?? opened!);

            string? parentPath = System.IO.Path.GetDirectoryName(full);

            if (directory is null || parentPath is null
                || !FileHandleIdentityInterop.TryOpenDirectoryMetadata(parentPath, out parentHandle, out _)) return WorkspaceLeaseIdentityResolver.Unavailable;

            parent = WorkspaceLeaseRetainedDirectory.Capture(parentHandle);

            if (parent is null || !parent.ValidateNamed() || !directory.ValidateNamed()
                || !string.Equals(System.IO.Path.GetDirectoryName(directory.Path), parent.Path, StringComparison.Ordinal)
                || !string.Equals(System.IO.Path.GetFileName(full), System.IO.Path.GetFileName(directory.Path),
                    parent.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                || !WorkspaceLeaseIdentityResolver.TryCaptureRootChain(parent.Path, parent.Identity, out var ancestors, out var segments))
                return WorkspaceLeaseIdentityResolver.Unavailable;

            var result = new WorkspaceLeaseDirectoryRemoval(parent, directory, ancestors, segments);

            directory = null;

            parent = null;

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            return WorkspaceLeaseIdentityResolver.Unavailable;
        }
        finally
        {
            opened?.Dispose();

            parentHandle?.Dispose();

            directory?.Dispose();

            parent?.Dispose();
        }
    }

    internal Result ValidateBeforeEffect()
    {
        lock (_sync)
        {
            return !_finished && _parent is not null && _parent.ValidateNamed() && _directory.ValidateNamed()
                ? Result.Success() : Result.Failure(WorkspaceLeaseIdentityResolver.Unavailable);
        }
    }

    /// <summary>The caller has completed its native delete and closed any original leaf handle.</summary>
    internal Result<WorkspaceLeaseRemovedDirectory> Complete() => WorkspaceLeaseRemovedDirectory.Mint(this);

    internal bool TryComplete(out WorkspaceLeaseRetainedDirectory? parent)
    {
        parent = null;

        lock (_sync)
        {
            if (_finished || _parent is null) return false;

            _finished = true;

            bool held = _directory.ValidateHeld();

            // Windows ordinary deletion becomes visible after the last leaf handle drains.
            // The old identity was retained through the effect; only the parent is needed afterward.
            _directory.Dispose();

            if (!held || !_parent.ValidateNamed() || !_parent.IsChildAbsent(System.IO.Path.GetFileName(Path))) return false;

            parent = _parent;

            _parent = null;

            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _finished = true;

            _directory.Dispose();

            _parent?.Dispose();

            _parent = null;
        }
    }
}

internal sealed class WorkspaceLeaseRemovedDirectory : IDisposable
{
    private readonly object _sync = new();

    private readonly WorkspaceLeaseRetainedDirectory _parent;

    private bool _disposed;

    private WorkspaceLeaseRemovedDirectory(WorkspaceLeaseDirectoryRemoval preparation, WorkspaceLeaseRetainedDirectory parent)
    {
        Preparation = preparation;

        _parent = parent;
    }

    internal WorkspaceLeaseDirectoryRemoval Preparation { get; }

    internal string Path => Preparation.Path;

    internal FileHandleIdentity Identity => Preparation.Identity;

    internal static Result<WorkspaceLeaseRemovedDirectory> Mint(WorkspaceLeaseDirectoryRemoval preparation) =>
        preparation.TryComplete(out var parent) ? new WorkspaceLeaseRemovedDirectory(preparation, parent!) : WorkspaceLeaseIdentityResolver.Unavailable;

    internal Result Validate()
    {
        lock (_sync)
        {
            return !_disposed && _parent.ValidateNamed() && _parent.IsChildAbsent(System.IO.Path.GetFileName(Path))
                ? Result.Success() : Result.Failure(WorkspaceLeaseIdentityResolver.Unavailable);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;

            _parent.Dispose();
        }
    }
}
