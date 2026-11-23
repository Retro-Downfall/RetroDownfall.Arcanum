using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckPortableToolchainResolution(
    bool Success,
    string? Code,
    string? Message,
    string? SdkVersion,
    string? ImageReference,
    string? OfflinePackageFeed,
    string? InitImageReference,
    FileHandleIdentity? FeedIdentity = null);

internal static class WorkspaceCheckPortableToolchain
{
    internal static WorkspaceCheckPortableToolchainResolution Resolve(
        string workspaceRoot,
        WorkspaceCheckSettings settings)
    {
        if (settings.PortableDotNet is not WorkspaceCheckPortableDotNetSettings configured)
        {
            return Failure(
                "portable_dotnet_unconfigured",
                "Configure operator-approved portable .NET SDK images and an offline package feed.");
        }

        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return Failure(
                "portable_dotnet_platform_unavailable",
                "Portable .NET checks require an arm64 macOS host.");
        }

        WorkspaceCheckPortableDotNetSettings portable = configured with
        {
            Images = Array.ConvertAll(configured.Images ?? [], static image => image is null ? null! : image with { }),
        };

        List<ConfigurationValidationError> errors = [];

        WorkspaceCheckPortableDotNetPolicy.Validate(portable, errors);

        if (errors.Count != 0)
        {
            ConfigurationValidationError invalid = errors[0];

            return Failure("portable_dotnet_invalid_configuration", $"{invalid.Pointer}. {invalid.Detail}");
        }

        string? workspace = WorkspaceCheckSdkResolver.CanonicalDirectory(workspaceRoot);

        if (workspace is null)
        {
            return Failure("workspace_unavailable", "The workspace root could not be canonicalized.");
        }

        string feedPath = NoFollowPathTopology.NormalizeMacOsSystemAlias(
            Path.GetFullPath(portable.OfflinePackageFeed));

        Result<NoFollowPathTopologyKind> feedTopology = NoFollowPathTopology.Classify(feedPath);

        if (feedTopology.IsFailure
            || feedTopology.Value != NoFollowPathTopologyKind.Directory
            || !TryCaptureDisjointFeedIdentity(feedPath, workspace, out FileHandleIdentity feedIdentity))
        {
            return Failure(
                "portable_dotnet_feed_unavailable",
                "Configure a directory without symbolic links for the offline package feed, with no overlap with the workspace.");
        }

        WorkspaceCheckSdkVersion[] versions = Array.ConvertAll(portable.Images, static image =>
        {
            _ = WorkspaceCheckSdkVersion.TryParse(image.SdkVersion, out WorkspaceCheckSdkVersion parsed);

            return parsed;
        });

        (string? version, string? code, string? message) =
            WorkspaceCheckSdkResolver.ResolvePortableVersion(workspace, versions);

        if (version is null)
        {
            return Failure(code!, message!);
        }

        WorkspaceCheckPortableImageSettings selected = portable.Images.Single(image => image.SdkVersion == version);

        return new WorkspaceCheckPortableToolchainResolution(
            true, null, null, version, selected.ImageReference, feedPath, portable.InitImageReference, feedIdentity);
    }

    private static bool TryCaptureDisjointFeedIdentity(string feed, string workspace, out FileHandleIdentity identity)
    {
        identity = default;

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(feed, out FileHandleMetadata feedMetadata)
            || feedMetadata.Kind != FileSystemObjectKind.Directory
            || !FileHandleIdentityInterop.TryGetPathMetadata(workspace, out FileHandleMetadata workspaceMetadata)
            || !IsOutsideDirectory(feed, workspaceMetadata.Identity)
            || !IsOutsideDirectory(workspace, feedMetadata.Identity))
        {
            return false;
        }

        identity = feedMetadata.Identity;

        return true;
    }

    private static bool IsOutsideDirectory(string path, FileHandleIdentity other)
    {
        // Compare identities to account for filesystem aliases and path normalization.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!FileHandleIdentityInterop.TryGetPathMetadata(current, out FileHandleMetadata metadata)
                || metadata.Kind != FileSystemObjectKind.Directory
                || FileHandleIdentity.IdentitiesMatch(metadata.Identity, other))
            {
                return false;
            }
        }

        return true;
    }

    private static WorkspaceCheckPortableToolchainResolution Failure(string code, string message) =>
        new(false, code, message, null, null, null, null);
}
