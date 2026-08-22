using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Coordination;

public enum ArcanumClientMutationPathDisposition : byte
{

    OutsideManagedRoot,

    Managed,

}

/// <summary>
/// Classifies a desktop-client destination without following symlinks or reparse points. An outside
/// path is admitted only when every existing ancestor is an ordinary directory and an existing leaf
/// is single-link, so an alias cannot turn an uncoordinated export into a managed-root mutation.
/// </summary>
public static class ArcanumClientMutationPathPolicy
{

    public static Result<ArcanumClientMutationPathDisposition> Classify(
        string managedRoot,
        string path)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullManagedRoot;

        string fullPath;

        try
        {

            fullManagedRoot = NormalizeMacOsSystemAlias(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot)));

            fullPath = NormalizeMacOsSystemAlias(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException)
        {

            return Failure();

        }

        Result<NoFollowPathTopologyKind> rootTopology =
            NoFollowPathTopology.Classify(fullManagedRoot);

        if (rootTopology.IsFailure
            || rootTopology.Value is NoFollowPathTopologyKind.RegularFile)
        {

            return Failure();

        }

        Result<NoFollowPathTopologyKind> pathTopology =
            NoFollowPathTopology.Classify(fullPath);

        if (pathTopology.IsFailure)
        {

            return Failure();

        }

        if (pathTopology.Value is NoFollowPathTopologyKind.RegularFile
            && (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                    fullPath,
                    out FileHandleMetadata metadata)
                || metadata.HardLinkCount != 1
                || metadata.Kind is not FileSystemObjectKind.RegularFile))
        {

            return Failure();

        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        bool managed = string.Equals(fullPath, fullManagedRoot, comparison)
            || fullPath.StartsWith(
                fullManagedRoot + Path.DirectorySeparatorChar,
                comparison);

        return Result<ArcanumClientMutationPathDisposition>.Success(
            managed
                ? ArcanumClientMutationPathDisposition.Managed
                : ArcanumClientMutationPathDisposition.OutsideManagedRoot);

    }

    private static Result<ArcanumClientMutationPathDisposition> Failure() =>
        Result<ArcanumClientMutationPathDisposition>.Failure(new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            "The mutation destination topology could not be classified safely."));

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
