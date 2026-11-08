using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Prepared before an exclusive native create or no-overwrite rename, never a post-hoc pathname assertion.</summary>
internal sealed class WorkspaceLeaseDirectoryCreation : IDisposable
{
    private readonly object _sync = new();

    private readonly SafeFileHandle _parent;

    private readonly WorkspaceLeaseRetainedDirectory? _source;

    private bool _finished;

    private WorkspaceLeaseDirectoryCreation(string path, SafeFileHandle parent, FileHandleIdentity parentIdentity, bool sensitive,
        ImmutableArray<WorkspaceLeaseDirectoryIdentity> ancestors, ImmutableArray<string> segments, WorkspaceLeaseRetainedDirectory? source)
    {
        Path = path;

        ParentPath = System.IO.Path.GetDirectoryName(path)!;

        ParentIdentity = parentIdentity;

        CaseSensitive = sensitive;

        Ancestors = ancestors;

        Segments = segments;

        _parent = parent;

        _source = source;
    }

    internal string Path { get; }

    internal string ParentPath { get; }

    internal FileHandleIdentity ParentIdentity { get; }

    internal bool CaseSensitive { get; }

    internal ImmutableArray<WorkspaceLeaseDirectoryIdentity> Ancestors { get; }

    internal ImmutableArray<string> Segments { get; }

    internal string? SourcePath => _source?.Path;

    internal FileHandleIdentity? SourceIdentity => _source?.Identity;

    internal static Result<WorkspaceLeaseDirectoryCreation> Prepare(string path, SafeFileHandle? relocationSource = null)
    {
        SafeFileHandle? parent = null;

        WorkspaceLeaseRetainedDirectory? source = null;

        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return WorkspaceLeaseIdentityResolver.Unavailable;

            string full = NoFollowPathTopology.NormalizeMacOsSystemAlias(System.IO.Path.GetFullPath(path));

            string leaf = System.IO.Path.GetFileName(full);

            string? parentPath = System.IO.Path.GetDirectoryName(full);

            if (parentPath is null || !WorkspaceLeaseIdentityResolver.IsSegment(leaf) || leaf.Any(character => character > 127))
                return WorkspaceLeaseIdentityResolver.Unavailable;

            Result<NoFollowPathTopologyKind> topology = NoFollowPathTopology.Classify(parentPath);

            if (topology.IsFailure || topology.Value != NoFollowPathTopologyKind.Directory
                || !FileHandleIdentityInterop.TryOpenDirectoryForCreation(parentPath, out parent, out FileHandleMetadata metadata)
                || !FileHandleIdentityInterop.TryGetHandleKernelPath(parent, out string? canonicalParent)
                || !FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(parent, out bool sensitive)
                || !WorkspaceLeaseIdentityResolver.TryCaptureRootChain(canonicalParent, metadata.Identity, out var ancestors, out var segments))
                return WorkspaceLeaseIdentityResolver.Unavailable;

            if (relocationSource is not null)
            {
                source = WorkspaceLeaseRetainedDirectory.Capture(relocationSource);

                if (source is null || !source.ValidateNamed()) return WorkspaceLeaseIdentityResolver.Unavailable;
            }

            var result = new WorkspaceLeaseDirectoryCreation(System.IO.Path.Combine(canonicalParent, leaf), parent, metadata.Identity,
                sensitive, ancestors, segments, source);

            if (result.ValidateBeforeEffect().IsFailure) return WorkspaceLeaseIdentityResolver.Unavailable;

            parent = null;

            source = null;

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            return WorkspaceLeaseIdentityResolver.Unavailable;
        }
        finally
        {
            parent?.Dispose();

            source?.Dispose();
        }
    }

    internal Result ValidateBeforeEffect()
    {
        lock (_sync)
        {
            if (_finished || !ValidateParent() || _source is not null && !_source.ValidateNamed())
                return Result.Failure(WorkspaceLeaseIdentityResolver.Unavailable);

            SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(_parent, System.IO.Path.GetFileName(Path), out var existing);

            existing?.Dispose();

            return status == SecureFileOpenStatus.NotFound ? Result.Success() : Result.Failure(WorkspaceLeaseIdentityResolver.Unavailable);
        }
    }

    internal Result<WorkspaceLeaseCreatedDirectory> CreateExclusive(UnixFileMode? mode = null)
    {
        lock (_sync)
        {
            if (_source is not null || ValidateBeforeEffect().IsFailure) return WorkspaceLeaseIdentityResolver.Unavailable;

            uint bits = mode is null ? 0x1FFU : (uint)mode.Value;

            if (!FileHandleIdentityInterop.TryCreateDirectoryExclusiveRelative(_parent, System.IO.Path.GetFileName(Path), bits, CaseSensitive, out var created)
                || created is null)
            {
                _finished = true;

                return WorkspaceLeaseIdentityResolver.Unavailable;
            }

            using (created)
            {
                return Complete(created);
            }
        }
    }

    /// <summary>Called only by the trusted native creation/rename seam after its exclusive effect succeeded.</summary>
    internal Result<WorkspaceLeaseCreatedDirectory> Complete(SafeFileHandle createdDirectory) => WorkspaceLeaseCreatedDirectory.Mint(this, createdDirectory);

    internal bool TryComplete(SafeFileHandle created, out WorkspaceLeaseRetainedDirectory? parent, out WorkspaceLeaseRetainedDirectory? directory)
    {
        parent = null;

        directory = null;

        lock (_sync)
        {
            if (_finished || !ValidateParent()) return false;

            _finished = true;

            parent = WorkspaceLeaseRetainedDirectory.Capture(_parent);

            directory = WorkspaceLeaseRetainedDirectory.Capture(created);

            if (parent is null || directory is null || !parent.ValidateNamed() || !directory.ValidateNamed()
                || !string.Equals(System.IO.Path.GetDirectoryName(directory.Path), ParentPath, StringComparison.Ordinal)
                || !string.Equals(System.IO.Path.GetFileName(directory.Path), System.IO.Path.GetFileName(Path), CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                || SourceIdentity is { } expected && directory.Identity != expected)
            {
                parent?.Dispose();

                directory?.Dispose();

                parent = null;

                directory = null;

                return false;
            }

            return true;
        }
    }

    private bool ValidateParent() => !_parent.IsClosed && !_parent.IsInvalid
        && FileHandleIdentityInterop.TryGetHandleMetadata(_parent, out var held) && held.Identity == ParentIdentity
        && FileHandleIdentityInterop.TryGetHandleKernelPath(_parent, out string? actual) && actual == ParentPath
        && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(ParentPath, out var named) && named.Identity == ParentIdentity
        && FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(_parent, out bool sensitive) && sensitive == CaseSensitive;

    public void Dispose()
    {
        lock (_sync)
        {
            _finished = true;

            _source?.Dispose();

            _parent.Dispose();
        }
    }
}

internal sealed class WorkspaceLeaseCreatedDirectory : IDisposable
{
    private readonly object _sync = new();

    private readonly WorkspaceLeaseRetainedDirectory _parent;

    private readonly WorkspaceLeaseRetainedDirectory _directory;

    private bool _disposed;

    private WorkspaceLeaseCreatedDirectory(WorkspaceLeaseDirectoryCreation creation, WorkspaceLeaseRetainedDirectory parent, WorkspaceLeaseRetainedDirectory directory)
    {
        Preparation = creation;

        _parent = parent;

        _directory = directory;
    }

    internal WorkspaceLeaseDirectoryCreation Preparation { get; }

    internal string Path => _directory.Path;

    internal FileHandleIdentity Identity => _directory.Identity;

    internal string? SourcePath => Preparation.SourcePath;

    internal FileHandleIdentity? SourceIdentity => Preparation.SourceIdentity;

    internal static Result<WorkspaceLeaseCreatedDirectory> Mint(WorkspaceLeaseDirectoryCreation creation, SafeFileHandle handle) =>
        creation.TryComplete(handle, out var parent, out var directory)
            ? new WorkspaceLeaseCreatedDirectory(creation, parent!, directory!)
            : WorkspaceLeaseIdentityResolver.Unavailable;

    internal Result Validate()
    {
        lock (_sync)
        {
            return !_disposed && _parent.ValidateNamed() && _directory.ValidateNamed()
                ? Result.Success() : Result.Failure(WorkspaceLeaseIdentityResolver.Unavailable);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;

            _directory.Dispose();

            _parent.Dispose();
        }
    }
}

/// <summary>Retains the original OS handle even if its caller closes the SafeFileHandle wrapper.</summary>
internal sealed class WorkspaceLeaseRetainedDirectory : IDisposable
{
    private SafeFileHandle? _original;

    private readonly SafeFileHandle _view;

    private WorkspaceLeaseRetainedDirectory(SafeFileHandle original, SafeFileHandle view, string path, FileHandleIdentity identity, bool sensitive)
    {
        _original = original;

        _view = view;

        Path = path;

        Identity = identity;

        CaseSensitive = sensitive;
    }

    internal string Path { get; }

    internal FileHandleIdentity Identity { get; }

    internal bool CaseSensitive { get; }

    internal static WorkspaceLeaseRetainedDirectory? Capture(SafeFileHandle handle)
    {
        bool retained = false;

        SafeFileHandle? view = null;

        try
        {
            handle.DangerousAddRef(ref retained);

            view = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(view, out var metadata) || metadata.Kind != FileSystemObjectKind.Directory
                || !FileHandleIdentityInterop.TryGetHandleKernelPath(view, out string? path)
                || !FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(view, out bool sensitive)) return null;

            var result = new WorkspaceLeaseRetainedDirectory(handle, view, path, metadata.Identity, sensitive);

            retained = false;

            view = null;

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        {
            return null;
        }
        finally
        {
            view?.Dispose();

            if (retained) handle.DangerousRelease();
        }
    }

    internal bool ValidateHeld() => _original is not null
        && FileHandleIdentityInterop.TryGetHandleMetadata(_view, out var metadata)
        && metadata.Kind == FileSystemObjectKind.Directory && metadata.Identity == Identity;

    internal bool IsChildAbsent(string leaf)
    {
        if (_original is null) return false;

        SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(_view, leaf, out var child);

        child?.Dispose();

        return status == SecureFileOpenStatus.NotFound;
    }

    internal bool ValidateNamed() => _original is not null
        && FileHandleIdentityInterop.TryGetHandleMetadata(_view, out var held) && held.Kind == FileSystemObjectKind.Directory && held.Identity == Identity
        && FileHandleIdentityInterop.TryGetHandleKernelPath(_view, out string? actual) && actual == Path
        && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(Path, out var named) && named.Kind == FileSystemObjectKind.Directory && named.Identity == Identity
        && FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(_view, out bool sensitive) && sensitive == CaseSensitive;

    public void Dispose()
    {
        SafeFileHandle? original = Interlocked.Exchange(ref _original, null);

        if (original is not null)
        {
            _view.Dispose();

            original.DangerousRelease();
        }
    }
}
