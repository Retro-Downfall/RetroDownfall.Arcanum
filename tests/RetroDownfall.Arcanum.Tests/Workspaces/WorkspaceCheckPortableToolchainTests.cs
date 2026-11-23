using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class WorkspaceCheckPortableToolchainTests : IDisposable
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string SdkImage = "mcr.microsoft.com/dotnet/sdk@sha256:" + Digest;

    private const string Sdk8Image = "mcr.microsoft.com/dotnet/sdk8@sha256:" + Digest;

    private const string InitImage = "ghcr.io/apple/container-init@sha256:" + Digest;

    private readonly string _root = Directory.CreateTempSubdirectory("arcanum-portable-").FullName;

    [SkippableTheory]
    [InlineData("""{"sdk":{"version":"7.0.102","rollForward":"disable"}}""", "7.0.102")]
    [InlineData("""{"sdk":{"version":"7.0.100","paths":["$host$"]}}""", "7.0.102")]
    [InlineData("""{"sdk":{"version":"7.0.102","rollForward":"latestMajor"}}""", "8.0.100")]
    public void Portable_dotnet_selects_the_pinned_image_from_inherited_workspace_policy(
        string globalJson,
        string expectedVersion)
    {
        Skip.IfNot(IsSupportedPlatform(), "The portable toolchain selector requires arm64 macOS.");

        (string workspace, WorkspaceCheckSettings settings) = Configure();

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(workspace)!, "global.json"), globalJson);

        WorkspaceCheckPortableToolchainResolution result = WorkspaceCheckPortableToolchain.Resolve(workspace, settings);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expectedVersion, result.SdkVersion);
        Assert.Equal(expectedVersion == "8.0.100" ? Sdk8Image : SdkImage, result.ImageReference);
        Assert.Equal(InitImage, result.InitImageReference);
        Assert.Equal(Path.GetFileName(settings.PortableDotNet!.OfflinePackageFeed), Path.GetFileName(result.OfflinePackageFeed));
    }

    [SkippableTheory]
    [InlineData("""{"sdk":{"version":"7.0.100","rollForward":"disable"}}""", "sdk_unavailable")]
    [InlineData("""{"sdk":{"paths":[]}}""", "sdk_unavailable")]
    [InlineData("""{"sdk":{"paths":["$host$","../dotnet"]}}""", "invalid_global_json")]
    public void Portable_dotnet_refuses_workspace_sdk_paths_and_unavailable_versions(
        string globalJson,
        string expectedCode)
    {
        Skip.IfNot(IsSupportedPlatform(), "The portable toolchain selector requires arm64 macOS.");

        (string workspace, WorkspaceCheckSettings settings) = Configure();

        File.WriteAllText(Path.Combine(workspace, "global.json"), globalJson);

        WorkspaceCheckPortableToolchainResolution result = WorkspaceCheckPortableToolchain.Resolve(workspace, settings);

        Assert.False(result.Success);

        Assert.Equal(expectedCode, result.Code);

        Assert.Null(result.ImageReference);
    }

    [Theory]
    [InlineData(SdkImage, true)]
    [InlineData("registry.example:5000/a_b/a.c-d@sha256:" + Digest, true)]
    [InlineData("mcr.microsoft.com/dotnet/sdk:8.0.100", false)]
    [InlineData("-mcr.microsoft.com/dotnet/sdk@sha256:" + Digest, false)]
    [InlineData("mcr.microsoft.com/dotnet/../sdk@sha256:" + Digest, false)]
    [InlineData("mcr.microsoft.com/dotnet//sdk@sha256:" + Digest, false)]
    [InlineData("mcr.microsoft.com/dotnet/sdk@sha256:" + Digest + "\n", false)]
    [InlineData("mcr.microsoft.com:65536/dotnet/sdk@sha256:" + Digest, false)]
    [InlineData("dotnet/sdk@sha256:" + Digest, false)]
    public void Portable_dotnet_image_validation_requires_an_explicit_registry_and_digest(
        string imageReference,
        bool accepted)
    {
        (_, WorkspaceCheckSettings settings) = Configure();
        WorkspaceCheckPortableDotNetSettings portable = settings.PortableDotNet!;
        portable.Images[0].ImageReference = imageReference;
        portable.InitImageReference = imageReference;

        List<ConfigurationValidationError> errors = [];
        WorkspaceCheckPortableDotNetPolicy.Validate(portable, errors);

        Assert.Equal(accepted, errors.Count == 0);
        if (!accepted)
        {
            Assert.Contains(errors, error => error.Pointer.EndsWith("images[0].imageReference", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Pointer.EndsWith("initImageReference", StringComparison.Ordinal));
        }
    }

    [SkippableFact]
    public void Portable_dotnet_refuses_feed_overlapping_source_or_resolving_through_a_link()
    {
        Skip.IfNot(IsSupportedPlatform(), "The portable toolchain selector requires arm64 macOS.");

        (string workspace, WorkspaceCheckSettings settings) = Configure();
        WorkspaceCheckPortableDotNetSettings portable = settings.PortableDotNet!;
        string linked = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(linked, portable.OfflinePackageFeed);

        foreach (string feed in new[] { _root, workspace, linked })
        {
            portable.OfflinePackageFeed = feed;

            WorkspaceCheckPortableToolchainResolution result = WorkspaceCheckPortableToolchain.Resolve(workspace, settings);

            Assert.False(result.Success);
            Assert.Equal("portable_dotnet_feed_unavailable", result.Code);
        }
    }

    [SkippableFact]
    public void Portable_dotnet_toolchain_selection_detects_a_replaced_offline_feed()
    {
        Skip.IfNot(IsSupportedPlatform(), "The portable toolchain selector requires arm64 macOS.");

        (string workspace, WorkspaceCheckSettings settings) = Configure();
        string feed = settings.PortableDotNet!.OfflinePackageFeed;
        WorkspaceCheckPortableToolchainResolution original = WorkspaceCheckPortableToolchain.Resolve(workspace, settings);

        Assert.True(original.Success, original.Message);
        Assert.NotNull(original.FeedIdentity);

        Directory.Move(feed, feed + "-previous");
        Directory.CreateDirectory(feed);
        WorkspaceCheckPortableToolchainResolution replaced = WorkspaceCheckPortableToolchain.Resolve(workspace, settings);

        Assert.True(replaced.Success, replaced.Message);
        Assert.NotNull(replaced.FeedIdentity);
        Assert.NotEqual(original.FeedIdentity, replaced.FeedIdentity);
    }

    [Fact]
    public void Portable_dotnet_toolchain_configuration_changes_affect_the_workspace_check_fingerprint()
    {
        (_, WorkspaceCheckSettings settings) = Configure();
        WorkspaceCheckPortableDotNetSettings portable = settings.PortableDotNet!;
        string initial = InternalCodingToolSettingsFingerprint.BuildWorkspaceCheck(settings);

        settings.PortableDotNet = portable with { InitImageReference = SdkImage };
        Assert.NotEqual(initial, InternalCodingToolSettingsFingerprint.BuildWorkspaceCheck(settings));

        settings.PortableDotNet = portable with { OfflinePackageFeed = _root };
        Assert.NotEqual(initial, InternalCodingToolSettingsFingerprint.BuildWorkspaceCheck(settings));

        portable.Images[0].ImageReference = InitImage;
        settings.PortableDotNet = portable;
        Assert.NotEqual(initial, InternalCodingToolSettingsFingerprint.BuildWorkspaceCheck(settings));
    }

    private (string Workspace, WorkspaceCheckSettings Settings) Configure()
    {
        string workspace = Directory.CreateDirectory(Path.Combine(_root, "repo", "src")).FullName;
        string feed = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;

        WorkspaceCheckSettings settings = new()
        {
            PortableDotNet = new WorkspaceCheckPortableDotNetSettings
            {
                OfflinePackageFeed = feed,
                InitImageReference = InitImage,
                Images =
                [
                    new WorkspaceCheckPortableImageSettings { SdkVersion = "7.0.102", ImageReference = SdkImage },
                    new WorkspaceCheckPortableImageSettings { SdkVersion = "8.0.100", ImageReference = Sdk8Image },
                ],
            },
        };

        return (workspace, settings);
    }

    private static bool IsSupportedPlatform() =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
