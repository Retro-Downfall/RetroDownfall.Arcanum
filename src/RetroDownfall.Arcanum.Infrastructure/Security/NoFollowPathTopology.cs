using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal enum NoFollowPathTopologyKind : byte
{

    Absent,

    RegularFile,

    Directory,

}

/// <summary>
/// Classifies one authoritative path without following symlinks or reparse points. Absence is
/// trusted only after every existing ancestor has been proven to be an ordinary directory.
/// </summary>
internal static class NoFollowPathTopology
{

    internal static Result<NoFollowPathTopologyKind> Classify(string path)
    {

        string fullPath;

        try
        {

            fullPath = NormalizeMacOsSystemAlias(Path.GetFullPath(path));

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException)
        {

            return Failure();

        }

        string? root = Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(root)
            || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                root,
                out FileHandleMetadata rootMetadata)
            || rootMetadata.Kind is not FileSystemObjectKind.Directory)
        {

            return Failure();

        }

        string current = root;

        string[] components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (int index = 0; index < components.Length; index++)
        {

            current = Path.Combine(current, components[index]);

            bool exact = index == components.Length - 1;

            if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                    current,
                    out FileHandleMetadata metadata))
            {

                if (metadata.Kind is FileSystemObjectKind.Other
                    || (!exact && metadata.Kind is not FileSystemObjectKind.Directory))
                {

                    return Failure();

                }

                if (exact)
                {

                    return Result<NoFollowPathTopologyKind>.Success(
                        metadata.Kind is FileSystemObjectKind.Directory
                            ? NoFollowPathTopologyKind.Directory
                            : NoFollowPathTopologyKind.RegularFile);

                }

                continue;

            }

            SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollow(
                current,
                out var handle);

            handle?.Dispose();

            return status is SecureFileOpenStatus.NotFound
                ? Result<NoFollowPathTopologyKind>.Success(
                    NoFollowPathTopologyKind.Absent)
                : Failure();

        }

        return Result<NoFollowPathTopologyKind>.Success(
            NoFollowPathTopologyKind.Directory);

    }

    private static Result<NoFollowPathTopologyKind> Failure() =>
        Result<NoFollowPathTopologyKind>.Failure(new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            "The authoritative path topology could not be classified safely."));

    private static string NormalizeMacOsSystemAlias(string path)
    {

        if (!OperatingSystem.IsMacOS())
        {

            return path;

        }

        foreach ((string Alias, string Target) mapping in new[]
                 {
                     ("/etc", "/private/etc"),
                     ("/tmp", "/private/tmp"),
                     ("/var", "/private/var"),
                 })
        {

            if (string.Equals(path, mapping.Alias, StringComparison.Ordinal))
            {

                return mapping.Target;

            }

            string prefix = mapping.Alias + Path.DirectorySeparatorChar;

            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {

                return mapping.Target + path[mapping.Alias.Length..];

            }

        }

        return path;

    }

}
