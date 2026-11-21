using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using static RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceAuthority;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Walks only granted physical directory handles. At most one name page is held per ancestor.</summary>
internal sealed class GuildWorkspaceTree(DirectReadScope scope) : IWorkspaceSearchReadSource
{
    private readonly Dictionary<string, DirectReadProof> _searchFiles = new(StringComparer.Ordinal);

    private DirectReadProof _candidate;

    internal List<DirectReadProof> RootProofs { get; } = [];

    internal IEnumerable<DirectReadProof> Walk(string requested, bool recursive, bool listing,
        Func<string, bool> traverseDirectory, CancellationToken cancellationToken)
    {
        string[] roots = scope.SelectRoots(requested);

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using DirectDirectory? directory = scope.OpenDirectory(root);

            if (directory is null)
            {
                throw new IOException("The authorized workspace directory is unavailable.");
            }

            RootProofs.Add(new(root, true, directory.Fingerprint));

            if (root != requested)
            {
                string relativeRoot = RelativeTo(requested, root);

                if (!recursive && relativeRoot.Contains('/'))
                {
                    continue;
                }

                if (listing)
                {
                    yield return new(root, true, directory.Fingerprint);
                }

                if (!recursive || !traverseDirectory(relativeRoot))
                {
                    continue;
                }
            }

            Stack<DirectoryFrame> pending = new();

            pending.Push(new(root, directory.Fingerprint));

            while (pending.TryPeek(out DirectoryFrame? frame))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!frame.TryTakeNext(scope, cancellationToken, out string name))
                {
                    pending.Pop();

                    continue;
                }

                string path = frame.Path == "." ? name : frame.Path + "/" + name;

                if (!IsDirectFilePath(path))
                {
                    continue;
                }

                using DirectDirectory? parent = scope.OpenDirectory(frame.Path);

                if (parent?.Fingerprint != frame.Fingerprint || !parent.Matches())
                {
                    throw new IOException("An authorized workspace directory changed during traversal.");
                }

                if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(Path.Combine(parent.Path, name),
                        out FileHandleMetadata observed) || observed.Identity.VolumeId != parent.Identity.VolumeId)
                {
                    continue;
                }

                if (observed.Kind == FileSystemObjectKind.RegularFile && observed.HardLinkCount == 1)
                {
                    yield return new(path, false, FingerprintFile(parent.Fingerprint, observed.Identity));

                    continue;
                }

                if (observed.Kind != FileSystemObjectKind.Directory || listing && IsOrdinaryListingExcluded(name))
                {
                    continue;
                }

                using DirectDirectory? child = scope.OpenDirectory(path);

                if (child is null || child.Identity != observed.Identity || !child.Matches())
                {
                    continue;
                }

                if (listing)
                {
                    yield return new(path, true, child.Fingerprint);
                }

                if (recursive && traverseDirectory(RelativeTo(requested, path)))
                {
                    pending.Push(new(path, child.Fingerprint));
                }
            }
        }
    }

    public IEnumerable<WorkspaceTraversalFile> EnumerateFiles(string requestedRoot,
        Func<string, bool> includeFile, Func<string, bool> traverseDirectory, CancellationToken cancellationToken)
    {
        foreach (DirectReadProof proof in Walk(requestedRoot, recursive: true, listing: false, traverseDirectory, cancellationToken))
        {
            string relative = RelativeTo(requestedRoot, proof.Path);

            if (!proof.Directory && includeFile(relative))
            {
                _candidate = proof;

                yield return new(relative);
            }
        }
    }

    public WorkspaceSearchOpenedFile? OpenFile(string modelPath)
    {
        DirectFile? file = _candidate.Path == modelPath ? scope.OpenFile(modelPath, _candidate.Fingerprint) : null;

        if (file is null)
        {
            return null;
        }

        string identity = FormattableString.Invariant($"{file.Fingerprint}:{file.Modified}");

        return new(file.Stream, identity, file.Matches,
            () => _searchFiles[modelPath] = new(modelPath, false, file.Fingerprint, file.Modified));
    }

    internal DirectReadProof MatchProof(string path) => _searchFiles[path];

    private static string RelativeTo(string parent, string path) => parent == "." ? path : path[(parent.Length + 1)..];

    private static bool IsOrdinaryListingExcluded(string name) => name is "node_modules" or "bin" or "obj" or ".git";

    private sealed class DirectoryFrame(string path, string fingerprint)
    {
        private string[] _names = [];

        private int _index;

        private bool _more = true;

        internal string Path { get; } = path;

        internal string Fingerprint { get; } = fingerprint;

        internal bool TryTakeNext(DirectReadScope scope, CancellationToken cancellationToken, out string name)
        {
            name = "";

            if (_index == _names.Length)
            {
                if (!_more)
                {
                    return false;
                }

                using DirectDirectory? directory = scope.OpenDirectory(Path, enumerate: true);

                if (directory?.Fingerprint != Fingerprint || !directory.Matches()
                    || !SecureDirectoryNameEnumerator.TryEnumeratePage(directory.Handle, _names.LastOrDefault(),
                        100, "", cancellationToken, out _names, out _more)
                    || !directory.Matches())
                {
                    throw new IOException("The authorized workspace directory is unavailable.");
                }

                _index = 0;

                if (_names.Length == 0)
                {
                    return false;
                }
            }

            name = _names[_index++];

            return true;
        }
    }
}
